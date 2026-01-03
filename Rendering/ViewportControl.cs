using System.Numerics;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using OpenTK.Graphics.OpenGL4;
using OpenTK.WinForms;
using _3MFTool.Models;

namespace _3MFTool.Rendering;

public class ViewportControl : GLControl
{
    // P/Invoke for reliable keyboard state detection
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);
    private const int VK_MENU = 0x12;    // Alt
    private const int VK_CONTROL = 0x11; // Ctrl
    private const int VK_SHIFT = 0x10;   // Shift
    
    private static bool IsKeyDown(int vKey) => (GetAsyncKeyState(vKey) & 0x8000) != 0;
    
    // Core state
    private bool _initialized;
    private Mesh? _mesh;
    private SpatialGrid? _grid;
    private Camera _camera = new();
    private MeshRenderer _renderer = new();
    private BrushRenderer _brushRenderer = new();
    private UndoManager _undoManager = new(maxHistory: 50);
    
    // Palette
    private Vector3[] _palette = DefaultPalette.Colors;
    
    // Display options
    public bool ShowWireframe { get; set; }
    public bool ShowSubdivisions { get; set; }
    public bool ShowTexture { get; set; }
    public bool ShowQuantizedTexture { get; set; }
    public bool FlipTextureH { get; set; }
    public bool FlipTextureV { get; set; }
    
    // Paint mode
    public bool PaintEnabled { get; set; } = false;  // Off by default to prevent accidental painting
    
    // Texture
    private int _textureId;
    private int _quantizedTextureId;
    private bool _hasTexture;
    private bool _hasQuantizedTexture;
    
    // Brush state
    private Models.Brush _brush = new() { Radius = 0.5f };
    private int _currentColor = 0;  // 0-based: matches first extruder
    private int _maxPaintColor = 7; // Maximum allowed color index (0-7 = 8 colors)
    private PaintTool _tool = PaintTool.Paint;
    
    // Input state
    private bool _lmb, _rmb, _mmb;
    private bool _shift, _ctrl, _alt;
    private System.Drawing.Point _lastMouse;
    
    // Hover state
    private int _hoverTri = -1;
    private Vector3 _hoverPos, _hoverNorm;
    
    // Painting state
    private bool _painting;
    private bool _strokeDirty;
    private Vector3 _lastPaintPos;
    private bool _hasLastPaint;
    
    // Paint preview (rough-in during stroke)
    private List<(Vector3 pos, float radius, Vector4 color)> _paintPreview = new();

    // Events
    public event Action<int>? ColorPicked;
    public event Action<string>? StatusMessage;
    public event Action? MeshModified;
    public event Action<PaintTool>? ToolChanged;
    public event Action<float>? BrushSizeChanged;
    public event Action? UndoStateChanged;

    // Undo/Redo public API
    public bool CanUndo => _undoManager.CanUndo;
    public bool CanRedo => _undoManager.CanRedo;
    public int UndoCount => _undoManager.UndoCount;
    public int RedoCount => _undoManager.RedoCount;

    public ViewportControl() : base(new GLControlSettings
    {
        API = OpenTK.Windowing.Common.ContextAPI.OpenGL,
        APIVersion = new Version(3, 3),
        NumberOfSamples = 4
    })
    {
        _undoManager.StateChanged += () => UndoStateChanged?.Invoke();
    }

    // =========================================================================
    // Initialization
    // =========================================================================
    
    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        GL.ClearColor(0.12f, 0.12f, 0.12f, 1f);
        GL.Enable(EnableCap.DepthTest);
        GL.Enable(EnableCap.Blend);
        GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        
        _renderer.Init();
        _brushRenderer.Init();
        _initialized = true;
    }

    // =========================================================================
    // Public API
    // =========================================================================
    
    public void SetMesh(Mesh? mesh)
    {
        _mesh = mesh;
        if (mesh == null)
        {
            _grid = null;
            _brush.SetMesh(null, null);
            _undoManager.SetMesh(null);
            Invalidate(); // Refresh viewport to clear display
            return;
        }
        _grid = new SpatialGrid(mesh);
        _grid.Build();
        _brush.SetMesh(mesh, _grid);
        _undoManager.SetMesh(mesh);
        _camera.Frame(mesh.Center, mesh.BoundingRadius);
        RebuildVBO();
    }

    /// <summary>
    /// Set mesh and palette together - use after projection to avoid double-rebuild
    /// </summary>
    public void SetMeshWithPalette(Mesh? mesh, Vector3[] palette)
    {
        _mesh = mesh;
        _palette = palette;
        if (mesh == null)
        {
            _grid = null;
            _brush.SetMesh(null, null);
            _undoManager.SetMesh(null);
            Invalidate(); // Refresh viewport to clear display
            return;
        }
        _grid = new SpatialGrid(mesh);
        _grid.Build();
        _brush.SetMesh(mesh, _grid);
        _undoManager.SetMesh(mesh);
        // Don't re-frame camera - keep current view
        RebuildVBO();
    }

    public void SetPalette(Vector3[] palette)
    {
        _palette = palette;
        RebuildVBO();
    }

    public void SetCurrentColor(int c) => _currentColor = c;
    public void SetMaxPaintColor(int max) => _maxPaintColor = Math.Clamp(max, 0, 7);
    public void SetTool(PaintTool t) { _tool = t; UpdateActiveTool(); }
    public void SetBrushRadius(float r) { _brush.Radius = r; Invalidate(); }

    /// <summary>
    /// Begin a bulk operation that modifies many triangles at once.
    /// Call before making changes, then call EndBulkOperation after.
    /// </summary>
    public void BeginBulkOperation()
    {
        if (_mesh == null) return;
        _undoManager.BeginStroke();
        
        // Mark ALL triangles as potentially modified
        for (int i = 0; i < _mesh.Triangles.Count; i++)
            _undoManager.MarkTriangleModified(i);
    }
    
    /// <summary>
    /// End a bulk operation and commit to undo history.
    /// </summary>
    public void EndBulkOperation()
    {
        _undoManager.EndStroke();
    }

    public bool Undo()
    {
        if (_undoManager.Undo())
        {
            RebuildVBO(ShowSubdivisions);
            MeshModified?.Invoke();
            StatusMessage?.Invoke($"Undo ({_undoManager.UndoCount} remaining)");
            return true;
        }
        return false;
    }

    public bool Redo()
    {
        if (_undoManager.Redo())
        {
            RebuildVBO(ShowSubdivisions);
            MeshModified?.Invoke();
            StatusMessage?.Invoke($"Redo ({_undoManager.RedoCount} remaining)");
            return true;
        }
        return false;
    }

    public void ClearUndoHistory() => _undoManager.Clear();
    public float GetBrushRadius() => _brush.Radius;
    public void FrameMesh() { if (_mesh != null) _camera.Frame(_mesh.Center, _mesh.BoundingRadius); Invalidate(); }
    public void ResetCamera() 
    { 
        _camera = new Camera(); 
        if (_mesh != null) _camera.Frame(_mesh.Center, _mesh.BoundingRadius); 
        Invalidate(); 
    }

    public void SetTexture(byte[] rgba, int w, int h)
    {
        if (!_initialized) return;
        MakeCurrent();
        
        if (_textureId != 0) GL.DeleteTexture(_textureId);
        _textureId = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D, _textureId);
        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba, w, h, 0, PixelFormat.Rgba, PixelType.UnsignedByte, rgba);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.Repeat);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.Repeat);
        GL.BindTexture(TextureTarget.Texture2D, 0);
        _hasTexture = true;
        Invalidate();
    }

    public void SetQuantizedTexture(byte[] rgba, int w, int h)
    {
        if (!_initialized) return;
        MakeCurrent();
        
        if (_quantizedTextureId != 0) GL.DeleteTexture(_quantizedTextureId);
        _quantizedTextureId = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D, _quantizedTextureId);
        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba, w, h, 0, PixelFormat.Rgba, PixelType.UnsignedByte, rgba);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest); // Nearest for crisp colors
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.Repeat);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.Repeat);
        GL.BindTexture(TextureTarget.Texture2D, 0);
        _hasQuantizedTexture = true;
        Invalidate();
    }

    public void ClearQuantizedTexture()
    {
        if (!_initialized) return;
        MakeCurrent();
        if (_quantizedTextureId != 0)
        {
            GL.DeleteTexture(_quantizedTextureId);
            _quantizedTextureId = 0;
        }
        _hasQuantizedTexture = false;
        ShowQuantizedTexture = false;
        Invalidate();
    }

    public void ClearTextures()
    {
        if (!_initialized) return;
        MakeCurrent();
        if (_textureId != 0)
        {
            GL.DeleteTexture(_textureId);
            _textureId = 0;
        }
        if (_quantizedTextureId != 0)
        {
            GL.DeleteTexture(_quantizedTextureId);
            _quantizedTextureId = 0;
        }
        _hasTexture = false;
        _hasQuantizedTexture = false;
        ShowQuantizedTexture = false;
        ShowTexture = false;
        Invalidate();
    }

    public bool HasTexture => _hasTexture;
    public bool HasQuantizedTexture => _hasQuantizedTexture;

    public void RebuildVBO(bool buildSubdivEdges = false)
    {
        if (!_initialized || _mesh == null) return;
        MakeCurrent();
        _renderer.Update(_mesh, _palette, buildSubdivEdges);
        Invalidate();
    }

    // =========================================================================
    // Rendering
    // =========================================================================
    
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (!_initialized) return;
        
        MakeCurrent();
        GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
        
        _camera.Aspect = (float)Width / Height;
        var model = Matrix4x4.Identity;
        var view = _camera.View;
        var proj = _camera.Projection;
        var mvp = model * view * proj;
        var lightDir = Vector3.Normalize(new Vector3(-0.5f, -1f, -0.3f));
        
        // Choose which texture to display
        int texId = _textureId;
        bool showTex = ShowTexture && _hasTexture;
        
        if (ShowQuantizedTexture && _hasQuantizedTexture)
        {
            texId = _quantizedTextureId;
            showTex = true;
        }
        
        _renderer.Draw(mvp, model, lightDir, _camera.Position,
            texId, showTex, FlipTextureH, FlipTextureV);
        
        if (ShowWireframe)
            _renderer.DrawWireframe(mvp, new Vector3(0.9f, 0.9f, 0.9f));
        
        if (ShowSubdivisions)
            _renderer.DrawSubdivisionEdges(mvp, new Vector3(0.3f, 0.8f, 1.0f));
        
        // Draw paint preview circles during stroke (only when painting)
        if (PaintEnabled)
        {
            foreach (var (pos, radius, color) in _paintPreview)
                _brushRenderer.Draw(pos, radius, view, proj, color);
        
            // Draw brush indicator (only when paint mode is on)
            if (_hoverTri >= 0)
                _brushRenderer.Draw(_hoverPos, _brush.Radius, view, proj, GetToolColor());
        }
        
        SwapBuffers();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (_initialized) { MakeCurrent(); GL.Viewport(0, 0, Width, Height); Invalidate(); }
    }

    private Vector4 GetToolColor()
    {
        var active = GetActiveTool();
        return active switch
        {
            PaintTool.Paint => new Vector4(_palette[_currentColor % _palette.Length], 0.35f),
            PaintTool.Erase => new Vector4(0.5f, 0.5f, 0.5f, 0.35f),
            PaintTool.Mask => new Vector4(1f, 0.2f, 0.2f, 0.35f),
            PaintTool.Unmask => new Vector4(0.2f, 1f, 0.2f, 0.35f),
            _ => new Vector4(1f, 1f, 1f, 0.35f)
        };
    }

    private PaintTool GetActiveTool()
    {
        // Ctrl+Alt = Unmask, Ctrl alone = Mask
        if (_ctrl && _alt) return PaintTool.Unmask;
        if (_ctrl) return PaintTool.Mask;
        if (_shift && _tool == PaintTool.Paint) return PaintTool.Erase;
        return _tool;
    }

    private void UpdateActiveTool()
    {
        ToolChanged?.Invoke(GetActiveTool());
        Invalidate();
    }

    // =========================================================================
    // Mouse Input
    // =========================================================================
    
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        _lastMouse = e.Location;
        Focus();
        
        if (e.Button == MouseButtons.Left) _lmb = true;
        if (e.Button == MouseButtons.Right) _rmb = true;
        if (e.Button == MouseButtons.Middle) _mmb = true;
        
        // Use GetAsyncKeyState for reliable keyboard state
        bool ctrlNow = IsKeyDown(VK_CONTROL);
        bool altNow = IsKeyDown(VK_MENU);
        _ctrl = ctrlNow;
        _alt = altNow;
        _shift = IsKeyDown(VK_SHIFT);
        
        // Alt alone = orbit mode (don't paint)
        // Ctrl+Alt = Unmask (should paint)
        // Ctrl alone = Mask (should paint)
        bool isOrbitMode = altNow && !ctrlNow;
        
        // Start painting (only if paint mode is enabled and not in orbit mode)
        if (_lmb && !isOrbitMode && _mesh != null && _hoverTri >= 0 && PaintEnabled)
        {
            _painting = true;
            _strokeDirty = false;
            _hasLastPaint = false;
            _paintPreview.Clear();
            
            // Begin undo capture
            _undoManager.BeginStroke();
            
            // Add preview point
            AddPreviewPoint(_hoverPos);
            
            ApplyPaint(_hoverPos);
            _lastPaintPos = _hoverPos;
            _hasLastPaint = true;
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        
        // Use GetAsyncKeyState for reliable keyboard state
        _alt = IsKeyDown(VK_MENU);
        _ctrl = IsKeyDown(VK_CONTROL);
        _shift = IsKeyDown(VK_SHIFT);
        
        if (e.Button == MouseButtons.Left)
        {
            _lmb = false;
            if (_painting)
            {
                _painting = false;
                _hasLastPaint = false;
                _paintPreview.Clear();
                
                // End undo capture
                _undoManager.EndStroke();
                
                if (_strokeDirty)
                {
                    // Rebuild VBO with subdivision edges if showing them
                    RebuildVBO(ShowSubdivisions);
                    MeshModified?.Invoke();
                }
                else
                {
                    Invalidate(); // Clear preview
                }
            }
        }
        if (e.Button == MouseButtons.Right) _rmb = false;
        if (e.Button == MouseButtons.Middle) _mmb = false;
    }

    private void AddPreviewPoint(Vector3 pos)
    {
        var color = GetToolColor();
        color.W = 0.6f; // More visible
        _paintPreview.Add((pos, _brush.Radius, color));
        
        // Limit preview points
        if (_paintPreview.Count > 200)
            _paintPreview.RemoveAt(0);
        
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        
        // Use GetAsyncKeyState for reliable keyboard state (fixes stuck Alt in WPF/WinForms interop)
        _alt = IsKeyDown(VK_MENU);
        _ctrl = IsKeyDown(VK_CONTROL);
        _shift = IsKeyDown(VK_SHIFT);
        
        int dx = e.X - _lastMouse.X;
        int dy = e.Y - _lastMouse.Y;
        
        // Use actual keyboard state
        bool altNow = _alt;
        
        // Camera controls - match Python viewport exactly
        if (_rmb && !altNow)
        {
            // RMB = Orbit (matches Python: rot_y += dx * 0.4, rot_x += dy * 0.4)
            _camera.Orbit(dx * 0.4f, dy * 0.4f);
            Invalidate();
        }
        else if (_mmb && altNow)
        {
            // Alt+MMB = Orbit (same as RMB in Python)
            _camera.Orbit(dx * 0.4f, dy * 0.4f);
            Invalidate();
        }
        else if (_mmb && !altNow)
        {
            // MMB = Pan
            _camera.Pan(dx, dy);
            Invalidate();
        }
        else
        {
            // Update hover
            UpdateHover(e.X, e.Y);
            
            // Continue painting
            if (_painting && _hoverTri >= 0)
            {
                // Add preview point for visual feedback
                AddPreviewPoint(_hoverPos);
                
                if (_hasLastPaint)
                    InterpolatePaint(_lastPaintPos, _hoverPos);
                else
                    ApplyPaint(_hoverPos);
                
                _lastPaintPos = _hoverPos;
                _hasLastPaint = true;
            }
        }
        
        _lastMouse = e.Location;
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        
        // Use GetAsyncKeyState for reliable keyboard state (fixes stuck Alt in WPF/WinForms interop)
        bool altNow = IsKeyDown(VK_MENU);
        bool ctrlNow = IsKeyDown(VK_CONTROL);
        bool shiftNow = IsKeyDown(VK_SHIFT);
        
        // Sync tracked state with hardware state
        _alt = altNow;
        _ctrl = ctrlNow;
        _shift = shiftNow;
        
        if (altNow)
        {
            // Alt+Scroll = Brush resize
            float scaleFactor = 1.0f + (e.Delta / 1200f);
            scaleFactor = Math.Clamp(scaleFactor, 0.5f, 2.0f);
            _brush.Radius = Math.Clamp(_brush.Radius * scaleFactor, 0.001f, 100f);
            BrushSizeChanged?.Invoke(_brush.Radius);
            StatusMessage?.Invoke($"Brush: {_brush.Radius:F3}");
        }
        else
        {
            // Normal scroll = Zoom
            _camera.DoZoom(e.Delta);
        }
        
        Invalidate();
    }

    // =========================================================================
    // Keyboard Input
    // =========================================================================
    
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        bool changed = false;
        
        if (e.KeyCode == Keys.ShiftKey && !_shift) { _shift = true; changed = true; }
        if (e.KeyCode == Keys.ControlKey && !_ctrl) { _ctrl = true; changed = true; }
        if (e.KeyCode == Keys.Menu && !_alt) { _alt = true; changed = true; }
        
        if (changed) UpdateActiveTool();
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        bool changed = false;
        
        if (e.KeyCode == Keys.ShiftKey) { _shift = false; changed = true; }
        if (e.KeyCode == Keys.ControlKey) { _ctrl = false; changed = true; }
        if (e.KeyCode == Keys.Menu) { _alt = false; changed = true; }
        
        if (changed) UpdateActiveTool();
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        switch (keyData)
        {
            case Keys.Control | Keys.Z:
                Undo();
                return true;
            case Keys.Control | Keys.Y:
                Redo();
                return true;
            case Keys.Control | Keys.Shift | Keys.Z:
                Redo();
                return true;
            case Keys.B: _tool = PaintTool.Paint; UpdateActiveTool(); return true;
            case Keys.E: _tool = PaintTool.Erase; UpdateActiveTool(); return true;
            case Keys.C:
                // Instant eyedropper - pick color under cursor without changing tool mode
                if (_mesh != null && _hoverTri >= 0)
                {
                    var tri = _mesh.Triangles[_hoverTri];
                    if (tri.PaintData.Count > 0) ColorPicked?.Invoke(tri.PaintData[0].ExtruderId);
                }
                return true;
            case Keys.D1: case Keys.D2: case Keys.D3: case Keys.D4:
            case Keys.D5: case Keys.D6: case Keys.D7: case Keys.D8:
                // D1 = first color (index 0), D2 = second color (index 1), etc.
                int idx = (int)keyData - (int)Keys.D1;
                if (idx >= 0 && idx < _palette.Length) { _currentColor = idx; ColorPicked?.Invoke(idx); }
                return true;
            case Keys.OemOpenBrackets:
                _brush.Radius = Math.Max(0.001f, _brush.Radius * 0.85f);
                BrushSizeChanged?.Invoke(_brush.Radius);
                Invalidate();
                return true;
            case Keys.OemCloseBrackets:
                _brush.Radius = Math.Min(100f, _brush.Radius * 1.15f);
                BrushSizeChanged?.Invoke(_brush.Radius);
                Invalidate();
                return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    // =========================================================================
    // Painting
    // =========================================================================
    
    private void UpdateHover(int sx, int sy)
    {
        if (_mesh == null || _grid == null) { _hoverTri = -1; return; }
        
        var (origin, dir) = _camera.ScreenRay(sx, sy, Width, Height);
        var hit = _grid.Raycast(origin, dir);
        
        if (hit.HasValue)
        {
            _hoverTri = hit.Value.TriangleIndex;
            _hoverPos = hit.Value.HitPoint;
            _hoverNorm = _mesh.Triangles[_hoverTri].Normal;
            _brush.SetPosition(_hoverPos, _hoverNorm, _hoverTri);
        }
        else
        {
            _hoverTri = -1;
        }
        Invalidate();
    }

    private void InterpolatePaint(Vector3 from, Vector3 to)
    {
        float dist = Vector3.Distance(from, to);
        float step = _brush.Radius * 0.5f;
        
        if (dist <= step)
        {
            ApplyPaintAt(to);
            return;
        }
        
        int steps = Math.Max(1, (int)(dist / step));
        for (int i = 1; i <= steps; i++)
        {
            float t = (float)i / steps;
            ApplyPaintAt(Vector3.Lerp(from, to, t));
        }
    }

    private void ApplyPaint(Vector3 pos)
    {
        ApplyPaintAt(pos);
    }

    private void ApplyPaintAt(Vector3 pos)
    {
        if (_mesh == null || _grid == null) return;
        
        var active = GetActiveTool();
        
        // Clamp color to max allowed (respects extruder limit)
        int paintColor = Math.Min(_currentColor, _maxPaintColor);
        
        // For small brushes, find triangles by searching from position directly
        _brush.SetPosition(pos, _hoverNorm, _hoverTri);
        
        // Find affected triangles
        var affected = _brush.FindAffectedTrianglesAt(pos, active == PaintTool.Unmask);
        
        // Mark for undo BEFORE modifying
        foreach (int i in affected)
            _undoManager.MarkTriangleModified(i);
        
        // Apply paint
        foreach (int i in affected)
            _brush.ApplyPaint(i, active, paintColor);
        
        if (affected.Count > 0) _strokeDirty = true;
    }

    // =========================================================================
    // Focus Handling - Reset modifier keys when focus changes
    // =========================================================================
    
    protected override void OnLostFocus(EventArgs e)
    {
        base.OnLostFocus(e);
        ResetModifierKeys();
    }

    protected override void OnGotFocus(EventArgs e)
    {
        base.OnGotFocus(e);
        // Use GetAsyncKeyState for reliable keyboard state
        _shift = IsKeyDown(VK_SHIFT);
        _ctrl = IsKeyDown(VK_CONTROL);
        _alt = IsKeyDown(VK_MENU);
        UpdateActiveTool();
    }

    /// <summary>
    /// Public method to reset input state - called from WPF when modifier keys are released
    /// </summary>
    public void ResetInputState()
    {
        // Use GetAsyncKeyState to get actual hardware state
        _shift = IsKeyDown(VK_SHIFT);
        _ctrl = IsKeyDown(VK_CONTROL);
        _alt = IsKeyDown(VK_MENU);
        UpdateActiveTool();
        Invalidate();
    }
    
    /// <summary>
    /// Force sync with actual keyboard state
    /// </summary>
    public void SyncModifierState()
    {
        _shift = IsKeyDown(VK_SHIFT);
        _ctrl = IsKeyDown(VK_CONTROL);
        _alt = IsKeyDown(VK_MENU);
        UpdateActiveTool();
        Invalidate();
    }

    private void ResetModifierKeys()
    {
        _shift = false;
        _ctrl = false;
        _alt = false;
        _lmb = false;
        _rmb = false;
        _mmb = false;
        if (_painting)
        {
            _painting = false;
            _paintPreview.Clear();
            
            // End stroke (commit to undo history)
            _undoManager.EndStroke();
            
            if (_strokeDirty)
            {
                RebuildVBO(ShowSubdivisions);
                MeshModified?.Invoke();
            }
        }
        UpdateActiveTool();
    }

    // =========================================================================
    // Cleanup
    // =========================================================================
    
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _renderer?.Dispose();
            _brushRenderer?.Dispose();
            if (_textureId != 0) GL.DeleteTexture(_textureId);
            if (_quantizedTextureId != 0) GL.DeleteTexture(_quantizedTextureId);
        }
        base.Dispose(disposing);
    }
}

// =========================================================================
// Brush Indicator Renderer
// =========================================================================

public class BrushRenderer : IDisposable
{
    private int _vao, _vbo, _ebo, _prog;
    private int _uMVP, _uColor;
    private int _indexCount;

    public void Init()
    {
        // Generate sphere
        var verts = new List<float>();
        var inds = new List<uint>();
        int slices = 16, stacks = 12;

        for (int i = 0; i <= stacks; i++)
        {
            float phi = MathF.PI * i / stacks;
            for (int j = 0; j <= slices; j++)
            {
                float theta = 2 * MathF.PI * j / slices;
                verts.Add(MathF.Sin(phi) * MathF.Cos(theta));
                verts.Add(MathF.Cos(phi));
                verts.Add(MathF.Sin(phi) * MathF.Sin(theta));
            }
        }

        for (int i = 0; i < stacks; i++)
        {
            for (int j = 0; j < slices; j++)
            {
                uint a = (uint)(i * (slices + 1) + j);
                uint b = a + (uint)(slices + 1);
                inds.Add(a); inds.Add(b); inds.Add(a + 1);
                inds.Add(b); inds.Add(b + 1); inds.Add(a + 1);
            }
        }

        _indexCount = inds.Count;

        _vao = GL.GenVertexArray();
        _vbo = GL.GenBuffer();
        _ebo = GL.GenBuffer();

        GL.BindVertexArray(_vao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo);
        GL.BufferData(BufferTarget.ArrayBuffer, verts.Count * 4, verts.ToArray(), BufferUsageHint.StaticDraw);
        GL.BindBuffer(BufferTarget.ElementArrayBuffer, _ebo);
        GL.BufferData(BufferTarget.ElementArrayBuffer, inds.Count * 4, inds.ToArray(), BufferUsageHint.StaticDraw);
        GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 12, 0);
        GL.EnableVertexAttribArray(0);
        GL.BindVertexArray(0);

        const string vs = "#version 330 core\nlayout(location=0) in vec3 aPos;\nuniform mat4 uMVP;\nvoid main(){gl_Position=uMVP*vec4(aPos,1);}";
        const string fs = "#version 330 core\nuniform vec4 uColor;\nout vec4 FragColor;\nvoid main(){FragColor=uColor;}";
        
        int v = GL.CreateShader(ShaderType.VertexShader); GL.ShaderSource(v, vs); GL.CompileShader(v);
        int f = GL.CreateShader(ShaderType.FragmentShader); GL.ShaderSource(f, fs); GL.CompileShader(f);
        _prog = GL.CreateProgram(); GL.AttachShader(_prog, v); GL.AttachShader(_prog, f); GL.LinkProgram(_prog);
        GL.DeleteShader(v); GL.DeleteShader(f);
        
        _uMVP = GL.GetUniformLocation(_prog, "uMVP");
        _uColor = GL.GetUniformLocation(_prog, "uColor");
    }

    public void Draw(Vector3 pos, float radius, Matrix4x4 view, Matrix4x4 proj, Vector4 color)
    {
        var model = Matrix4x4.CreateScale(radius) * Matrix4x4.CreateTranslation(pos);
        var mvp = model * view * proj;
        
        GL.UseProgram(_prog);
        GL.UniformMatrix4(_uMVP, 1, false, new[] {
            mvp.M11, mvp.M12, mvp.M13, mvp.M14,
            mvp.M21, mvp.M22, mvp.M23, mvp.M24,
            mvp.M31, mvp.M32, mvp.M33, mvp.M34,
            mvp.M41, mvp.M42, mvp.M43, mvp.M44
        });
        GL.Uniform4(_uColor, color.X, color.Y, color.Z, color.W);
        
        GL.Disable(EnableCap.CullFace);
        GL.DepthMask(false);
        GL.BindVertexArray(_vao);
        GL.DrawElements(PrimitiveType.Triangles, _indexCount, DrawElementsType.UnsignedInt, 0);
        GL.BindVertexArray(0);
        GL.DepthMask(true);
        GL.Enable(EnableCap.CullFace);
        GL.UseProgram(0);
    }

    public void Dispose()
    {
        if (_vao != 0) GL.DeleteVertexArray(_vao);
        if (_vbo != 0) GL.DeleteBuffer(_vbo);
        if (_ebo != 0) GL.DeleteBuffer(_ebo);
        if (_prog != 0) GL.DeleteProgram(_prog);
    }
}
