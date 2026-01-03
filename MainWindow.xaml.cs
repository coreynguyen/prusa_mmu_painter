using System.IO;
using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using _3MFTool.Models;
using _3MFTool.IO;
using _3MFTool.Rendering;
using _3MFTool.Imaging;

using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;
using MessageBox = System.Windows.MessageBox;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using RadioButton = System.Windows.Controls.RadioButton;
using Key = System.Windows.Input.Key;
using Keyboard = System.Windows.Input.Keyboard;
using ModifierKeys = System.Windows.Input.ModifierKeys;
using Cursors = System.Windows.Input.Cursors;
using Path = System.IO.Path;
using ColorDialog = System.Windows.Forms.ColorDialog;

namespace _3MFTool;

public partial class MainWindow : Window
{
    private ViewportControl? _viewport;
    private Mesh? _mesh;
    private string? _currentFilePath;
    
    // BRUSH palette - for painting on the mesh (always separate from texture)
    private Vector3[] _brushPalette = (Vector3[])DefaultPalette.Colors.Clone();
    
    // QUANTIZED palette - for texture preview only (never directly linked to mesh)
    private Vector3[]? _quantizedPalette;
    private Vector3[]? _originalQuantizedPalette; // Backup of original quantization results
    
    private int _currentColor = 0;  // 0-based: color 0 = first extruder
    private PaintTool _baseTool = PaintTool.Paint;
    private CancellationTokenSource? _loadingCts;

    // Texture data
    private byte[]? _textureData;
    private byte[]? _quantizedIndexMap; // Stores palette index for each pixel (not the actual colors)
    private int _textureWidth, _textureHeight;
    private BitmapSource? _originalTextureBitmap;
    private BitmapSource? _quantizedTextureBitmap;
    private bool _showingQuantized;
    private WriteableBitmap? _uvOverlayBitmap;
    private bool _meshHasProjectedTexture; // Track if mesh was projected (used for status)

    public MainWindow()
    {
        InitializeComponent();
        SetupColorPanel();
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        App.SetWindowDarkMode(this, true);
        try
        {
            _viewport = new ViewportControl();
            _viewport.ColorPicked += c => { _currentColor = c; _viewport.SetCurrentColor(c); UpdateColorSelection(); };
            _viewport.StatusMessage += ShowStatus;
            _viewport.MeshModified += UpdateMeshInfo;
            _viewport.ToolChanged += t => { txtActiveTool.Text = $"Active: {t}"; };
            _viewport.BrushSizeChanged += r => { 
                if (sliderBrushSize != null) 
                    sliderBrushSize.Value = Math.Clamp(r, sliderBrushSize.Minimum, sliderBrushSize.Maximum); 
            };
            _viewport.UndoStateChanged += UpdateUndoButtons;
            _viewport.SetCurrentColor(_currentColor);  // Sync initial color
            viewportHost.Child = _viewport;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"OpenGL init failed:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void UpdateUndoButtons()
    {
        Dispatcher.Invoke(() =>
        {
            if (_viewport == null) return;
            btnUndo.IsEnabled = _viewport.CanUndo;
            btnRedo.IsEnabled = _viewport.CanRedo;
            txtUndoStatus.Text = $"Undo: {_viewport.UndoCount}  Redo: {_viewport.RedoCount}";
        });
    }
    
    // Palette backup for limit operation undo
    private Vector3[]? _paletteBackup = null;
    private int _paletteBackupUndoCount = -1;
    private int _currentExtruderLimit = 8; // Track current limit for undo
    private int _limitBackup = 8;
    private Vector3[]? _originalPaletteColors = null; // Original colors before any zeroing

    private void Undo_Click(object sender, RoutedEventArgs e)
    {
        if (_viewport == null) return;
        
        int beforeCount = _viewport.UndoCount;
        _viewport.Undo();
        
        // Check if we should restore palette backup
        if (_paletteBackup != null && _paletteBackupUndoCount == beforeCount)
        {
            // Restore the palette and limit
            Array.Copy(_paletteBackup, _brushPalette, 8);
            _currentExtruderLimit = _limitBackup;
            
            _viewport.SetPalette(_brushPalette);
            _viewport.SetMaxPaintColor(_currentExtruderLimit - 1);
            SetupColorPanel();
            
            // CRITICAL: Force a full VBO rebuild to fix "Corrupt" visuals
            // This ensures the mesh geometry on GPU matches the restored palette
            _viewport.RebuildVBO();
            
            _paletteBackup = null;
            _paletteBackupUndoCount = -1;
            
            ShowStatus($"Undo Limit: Restored {_currentExtruderLimit} colors");
        }
        else
        {
            // Even for normal paint undo, update the mesh info
            UpdateMeshInfo();
        }
    }

    private void Redo_Click(object sender, RoutedEventArgs e)
    {
        _viewport?.Redo();
    }

    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        _loadingCts?.Cancel();
        _viewport?.Dispose();
    }

    private void CancelLoading_Click(object sender, RoutedEventArgs e)
    {
        _loadingCts?.Cancel();
    }

    private void Window_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            if (e.Key == Key.N) { NewScene_Click(sender, e); e.Handled = true; }
            else if (e.Key == Key.O) { OpenModel_Click(sender, e); e.Handled = true; }
            else if (e.Key == Key.S) { SaveModel_Click(sender, e); e.Handled = true; }
            else if (e.Key == Key.Z) { _viewport?.Undo(); e.Handled = true; }
            else if (e.Key == Key.Y) { _viewport?.Redo(); e.Handled = true; }
        }
        else if (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            if (e.Key == Key.Z) { _viewport?.Redo(); e.Handled = true; }
        }
        else
        {
            switch (e.Key)
            {
                case Key.F: FrameModel_Click(sender, e); e.Handled = true; break;
                case Key.R: ResetView_Click(sender, e); e.Handled = true; break;
                case Key.T: chkShowTexture.IsChecked = !chkShowTexture.IsChecked; e.Handled = true; break;
                case Key.W: chkWireframe.IsChecked = !chkWireframe.IsChecked; e.Handled = true; break;
                case Key.Q: 
                    if (chkPreviewQuantized.IsEnabled)
                        chkPreviewQuantized.IsChecked = !chkPreviewQuantized.IsChecked;
                    e.Handled = true; 
                    break;
                case Key.P:
                    // Toggle paint mode
                    btnPaintMode.IsChecked = !btnPaintMode.IsChecked;
                    PaintMode_Click(sender, e);
                    e.Handled = true;
                    break;
                
                // Color selection hotkeys (1-9 and 0 for colors 0-9)
                case Key.D1: case Key.NumPad1: SelectColor(0); e.Handled = true; break;
                case Key.D2: case Key.NumPad2: SelectColor(1); e.Handled = true; break;
                case Key.D3: case Key.NumPad3: SelectColor(2); e.Handled = true; break;
                case Key.D4: case Key.NumPad4: SelectColor(3); e.Handled = true; break;
                case Key.D5: case Key.NumPad5: SelectColor(4); e.Handled = true; break;
                case Key.D6: case Key.NumPad6: SelectColor(5); e.Handled = true; break;
                case Key.D7: case Key.NumPad7: SelectColor(6); e.Handled = true; break;
                case Key.D8: case Key.NumPad8: SelectColor(7); e.Handled = true; break;
                case Key.D9: case Key.NumPad9: SelectColor(8); e.Handled = true; break;
                case Key.D0: case Key.NumPad0: SelectColor(9); e.Handled = true; break;
                
                // Tab to cycle colors
                case Key.Tab:
                    if (Keyboard.Modifiers == ModifierKeys.Shift)
                        SelectColor((_currentColor - 1 + _brushPalette.Length) % _brushPalette.Length);
                    else
                        SelectColor((_currentColor + 1) % _brushPalette.Length);
                    e.Handled = true;
                    break;
            }
        }
    }
    
    private void Window_KeyUp(object sender, System.Windows.Input.KeyEventArgs e)
    {
        // Safety: When modifier keys are released, ensure viewport syncs its state
        // This fixes the "stuck Alt/Shift" issue after using Alt+Scroll for brush resize
        if (e.Key == Key.LeftAlt || e.Key == Key.RightAlt ||
            e.Key == Key.LeftShift || e.Key == Key.RightShift ||
            e.Key == Key.LeftCtrl || e.Key == Key.RightCtrl ||
            e.Key == Key.System) // System key is Alt in WPF
        {
            _viewport?.ResetInputState();
        }
    }
    
    private void SelectColor(int idx)
    {
        if (idx >= 0 && idx < _brushPalette.Length)
        {
            _currentColor = idx;
            _viewport?.SetCurrentColor(idx);
            UpdateColorSelection();
            ShowStatus($"Extruder {idx + 1} (Color {idx})");
        }
    }

    private void SetupColorPanel()
    {
        colorPanel.Children.Clear();
        for (int i = 0; i < _brushPalette.Length; i++)
        {
            var c = _brushPalette[i];
            bool isLimited = i >= _currentExtruderLimit;
            
            var border = new Border
            {
                Width = 24, Height = 24, Margin = new Thickness(1), BorderThickness = new Thickness(2),
                BorderBrush = i == _currentColor ? Brushes.White : Brushes.Transparent,
                Background = new SolidColorBrush(Color.FromRgb((byte)(c.X * 255), (byte)(c.Y * 255), (byte)(c.Z * 255))),
                Tag = i, Cursor = Cursors.Hand, 
                ToolTip = isLimited ? $"Extruder {i + 1} (DISABLED - beyond limit)" : $"Extruder {i + 1} (press {i + 1}, double-click to edit)",
                Opacity = isLimited ? 0.3 : 1.0
            };
            int idx = i;
            border.MouseLeftButtonDown += (s, e) => 
            { 
                if (e.ClickCount == 2)
                {
                    // Double-click: edit color
                    EditPaletteColor(idx, false);
                }
                else
                {
                    // Single click: select (but warn if beyond limit)
                    _currentColor = idx; 
                    _viewport?.SetCurrentColor(idx); 
                    UpdateColorSelection(); 
                    if (idx >= _currentExtruderLimit)
                        ShowStatus($"Extruder {idx + 1} DISABLED (beyond limit of {_currentExtruderLimit})");
                    else
                        ShowStatus($"Extruder {idx + 1} (Color {idx})"); 
                }
            };
            colorPanel.Children.Add(border);
        }
    }

    private void EditPaletteColor(int index, bool isQuantized)
    {
        var palette = isQuantized ? _quantizedPalette : _brushPalette;
        if (palette == null || index >= palette.Length) return;

        // Prevent editing disabled brush palette colors
        if (!isQuantized && index >= _currentExtruderLimit)
        {
            ShowStatus($"Cannot edit color {index + 1} - beyond limit of {_currentExtruderLimit}");
            return;
        }

        var current = palette[index];
        var dlg = new ColorDialog
        {
            Color = System.Drawing.Color.FromArgb(
                (int)(current.X * 255), 
                (int)(current.Y * 255), 
                (int)(current.Z * 255)),
            FullOpen = true
        };

        if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            var newColor = new Vector3(dlg.Color.R / 255f, dlg.Color.G / 255f, dlg.Color.B / 255f);
            palette[index] = newColor;

            if (isQuantized)
            {
                // Update quantized palette display
                UpdateQuantizedPaletteDisplay();
                // Regenerate texture preview with new color
                RegenerateQuantizedTexture();
                // NOTE: Do NOT update mesh palette automatically
                // User must click "Project" again to apply changes to mesh
                ShowStatus($"Quantized color {index} updated. Click 'Project to Mesh' to apply to model.");
            }
            else
            {
                // Update brush palette display and viewport
                SetupColorPanel();
                _viewport?.SetPalette(_brushPalette);
                ShowStatus($"Brush color {index} updated");
            }
        }
    }

    private void UpdateQuantizedPaletteDisplay()
    {
        if (_quantizedPalette == null) return;

        quantizedPalettePanel.Children.Clear();
        for (int i = 0; i < _quantizedPalette.Length; i++)
        {
            var c = _quantizedPalette[i];
            var border = new Border
            {
                Width = 28, Height = 28, Margin = new Thickness(2), BorderThickness = new Thickness(1),
                BorderBrush = Brushes.Gray,
                Background = new SolidColorBrush(Color.FromRgb((byte)(c.X * 255), (byte)(c.Y * 255), (byte)(c.Z * 255))),
                Cursor = Cursors.Hand,
                ToolTip = $"Color {i} - Click to edit"
            };
            int idx = i;
            border.MouseLeftButtonDown += (s, e) => EditPaletteColor(idx, true);
            quantizedPalettePanel.Children.Add(border);
        }
    }

    private byte[]? _quantizedTextureRgba; // Store for viewport upload

    private void RegenerateQuantizedTexture()
    {
        if (_quantizedIndexMap == null || _quantizedPalette == null) return;

        // Build RGBA from index map + current palette colors
        byte[] rgbaData = new byte[_textureWidth * _textureHeight * 4];
        for (int i = 0; i < _quantizedIndexMap.Length; i++)
        {
            int paletteIdx = _quantizedIndexMap[i];
            var color = paletteIdx < _quantizedPalette.Length ? _quantizedPalette[paletteIdx] : Vector3.One;
            int pixelOffset = i * 4;
            rgbaData[pixelOffset] = (byte)(color.X * 255);
            rgbaData[pixelOffset + 1] = (byte)(color.Y * 255);
            rgbaData[pixelOffset + 2] = (byte)(color.Z * 255);
            rgbaData[pixelOffset + 3] = 255;
        }

        // Store for viewport
        _quantizedTextureRgba = rgbaData;
        
        // Upload to viewport for 3D preview
        _viewport?.SetQuantizedTexture(rgbaData, _textureWidth, _textureHeight);
        chkPreviewQuantized.IsEnabled = true;

        // Convert to BGRA for WPF
        var quantizedBitmap = new WriteableBitmap(_textureWidth, _textureHeight, 96, 96, PixelFormats.Bgra32, null);
        byte[] bgraData = new byte[rgbaData.Length];
        for (int i = 0; i < rgbaData.Length; i += 4)
        {
            bgraData[i] = rgbaData[i + 2];     // B
            bgraData[i + 1] = rgbaData[i + 1]; // G
            bgraData[i + 2] = rgbaData[i];     // R
            bgraData[i + 3] = rgbaData[i + 3]; // A
        }
        quantizedBitmap.WritePixels(new Int32Rect(0, 0, _textureWidth, _textureHeight), bgraData, _textureWidth * 4, 0);
        _quantizedTextureBitmap = quantizedBitmap;

        if (_showingQuantized)
        {
            texturePreview.Source = _quantizedTextureBitmap;
        }
    }

    private void ResetQuantizedColors_Click(object sender, RoutedEventArgs e)
    {
        if (_originalQuantizedPalette == null) return;
        
        // Restore original quantized colors
        _quantizedPalette = (Vector3[])_originalQuantizedPalette.Clone();
        
        // Update displays
        UpdateQuantizedPaletteDisplay();
        RegenerateQuantizedTexture();
        
        ShowStatus("Quantized colors reset to original values");
    }

    private void UpdateMeshPalette()
    {
        if (_quantizedPalette == null) return;

        // Build new mesh palette from quantized colors
        var newPalette = new Vector3[Math.Max(8, _quantizedPalette.Length + 1)];
        newPalette[0] = new Vector3(0.8f, 0.8f, 0.8f); // Unpainted
        for (int i = 0; i < _quantizedPalette.Length && i + 1 < newPalette.Length; i++)
            newPalette[i + 1] = _quantizedPalette[i];
        for (int i = _quantizedPalette.Length + 1; i < newPalette.Length; i++)
            newPalette[i] = DefaultPalette.Colors[i % DefaultPalette.Colors.Length];

        _brushPalette = newPalette;
        SetupColorPanel();
        _viewport?.SetPalette(_brushPalette);
    }

    private void UpdateColorSelection()
    {
        foreach (var child in colorPanel.Children)
            if (child is Border b) b.BorderBrush = (int)b.Tag == _currentColor ? Brushes.White : Brushes.Transparent;
        txtCurrentColor.Text = $"Current: {_currentColor}";
    }

    private void NewScene_Click(object sender, RoutedEventArgs e)
    {
        // Clear mesh
        _mesh = null;
        _currentFilePath = null;
        _viewport?.SetMesh(null);
        
        // Clear textures
        _textureData = null;
        _textureWidth = 0;
        _textureHeight = 0;
        _originalTextureBitmap = null;
        _quantizedTextureBitmap = null;
        _quantizedPalette = null;
        _originalQuantizedPalette = null;
        _showingQuantized = false;
        
        texturePreview.Source = null;
        _viewport?.ClearTextures();
        
        // Reset paint mode
        _paintModeEnabled = false;
        btnPaintMode.IsChecked = false;
        btnPaintMode.Content = "OFF";
        if (_viewport != null) _viewport.PaintEnabled = false;
        
        // Reset brush palette to defaults
        _brushPalette = new Vector3[]
        {
            new(0.5f, 0.5f, 0.5f), new(1f, 0f, 0f), new(0f, 1f, 0f), new(0f, 0f, 1f),
            new(1f, 1f, 0f), new(1f, 0f, 1f), new(0f, 1f, 1f), new(1f, 0.5f, 0f)
        };
        _viewport?.SetPalette(_brushPalette);
        SetupColorPanel();
        
        // Clear palette backup and reset limit
        _paletteBackup = null;
        _paletteBackupUndoCount = -1;
        _currentExtruderLimit = 8;
        _limitBackup = 8;
        _originalPaletteColors = null; // Clear original palette so next limit uses fresh colors
        _viewport?.SetMaxPaintColor(7);
        
        // Reset UI
        txtMeshInfo.Text = "No mesh loaded";
        btnSave.IsEnabled = false;
        btnProject.IsEnabled = false;
        btnResetQuantizedColors.IsEnabled = false;
        
        // Clear palette displays
        quantizedPalettePanel.Children.Clear();
        
        // Reset title
        Title = "3MF Tool";
        
        _viewport?.Invalidate();
        ShowStatus("Scene cleared");
    }

    private async void OpenModel_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Title = "Open Model", Filter = "3D Models|*.obj;*.3mf;*.glb;*.gltf|OBJ|*.obj|3MF|*.3mf|GLB/GLTF|*.glb;*.gltf" };
        if (dlg.ShowDialog() != true) return;
        await LoadModelAsync(dlg.FileName);
    }

    private async Task LoadModelAsync(string filepath)
    {
        _loadingCts?.Cancel();
        _loadingCts = new CancellationTokenSource();
        var ct = _loadingCts.Token;

        loadingOverlay.Visibility = Visibility.Visible;
        loadingProgress.Value = 0;
        loadingProgress.IsIndeterminate = false;
        txtLoadingPercent.Text = "0%";
        btnCancelLoading.Visibility = Visibility.Visible;
        _meshHasProjectedTexture = false;

        var progress = new Progress<(string, float)>(p => { 
            txtLoadingStatus.Text = p.Item1; 
            loadingProgress.Value = p.Item2 * 100; 
            txtLoadingPercent.Text = $"{(int)(p.Item2 * 100)}%";
        });

        try
        {
            Mesh mesh;
            string ext = Path.GetExtension(filepath).ToLowerInvariant();
            byte[]? embeddedTexture = null;
            
            if (ext == ".3mf")
            {
                mesh = await ThreeMFIO.LoadAsync(filepath, progress, ct);
                // 3MF is already in millimeters - no scaling needed
            }
            else if (ext == ".glb" || ext == ".gltf")
            {
                mesh = await GlbLoader.LoadAsync(filepath, progress, ct);
                
                // Try to extract embedded texture
                embeddedTexture = GlbLoader.ExtractTexture(filepath);
                
                // Apply import scale (GLB has no standard unit like OBJ)
                float scale = GetImportScale(mesh);
                if (MathF.Abs(scale - 1.0f) > 0.001f)
                {
                    txtLoadingStatus.Text = $"Scaling by {scale:F2}...";
                    for (int i = 0; i < mesh.Vertices.Count; i++)
                        mesh.Vertices[i] *= scale;
                    mesh.ComputeBounds();
                }
            }
            else
            {
                mesh = await ObjLoader.LoadAsync(filepath, progress, ct);
                
                // Apply import scale for OBJ files (which have no standard unit)
                float scale = GetImportScale(mesh);
                if (MathF.Abs(scale - 1.0f) > 0.001f)
                {
                    txtLoadingStatus.Text = $"Scaling by {scale:F2}...";
                    for (int i = 0; i < mesh.Vertices.Count; i++)
                        mesh.Vertices[i] *= scale;
                    mesh.ComputeBounds(); // Recompute after scaling
                }
            }

            txtLoadingStatus.Text = "Building spatial index...";
            txtLoadingPercent.Text = "";
            loadingProgress.IsIndeterminate = true;
            await Task.Run(() => { var g = new SpatialGrid(mesh); g.Build(); }, ct);
            loadingProgress.IsIndeterminate = false;

            _mesh = mesh;
            _currentFilePath = filepath;
            _viewport?.SetMesh(mesh);
            UpdateMeshInfo();
            UpdateUndoButtons();
            btnSave.IsEnabled = true;

            // Track what was loaded for status message
            bool hasVertexColors = false;
            bool hasEmbeddedTexture = embeddedTexture != null;
            
            // Load embedded texture from GLB if present
            if (embeddedTexture != null)
            {
                txtLoadingStatus.Text = "Loading embedded texture...";
                await LoadTextureFromMemoryAsync(embeddedTexture);
            }
            
            // If mesh has detected palette from vertex colors, use it for brush palette
            if (mesh.DetectedPalette != null && mesh.DetectedPalette.Length > 0)
            {
                hasVertexColors = true;
                // Build brush palette: slot 0 = unpainted gray, slots 1+ = detected colors
                var newPalette = new Vector3[Math.Max(8, mesh.DetectedPalette.Length + 1)];
                newPalette[0] = new Vector3(0.8f, 0.8f, 0.8f); // Unpainted
                for (int i = 0; i < mesh.DetectedPalette.Length && i + 1 < newPalette.Length; i++)
                    newPalette[i + 1] = mesh.DetectedPalette[i];
                for (int i = mesh.DetectedPalette.Length + 1; i < newPalette.Length; i++)
                    newPalette[i] = DefaultPalette.Colors[i % DefaultPalette.Colors.Length];
                
                _brushPalette = newPalette;
                SetupColorPanel();
                _viewport?.SetPalette(_brushPalette);
            }

            // Auto-set subdivision level based on mesh complexity
            AutoSetSubdivisionLevel(mesh.Triangles.Count);

            bool hasTexture = false;
            if (!string.IsNullOrEmpty(mesh.TexturePath) && File.Exists(mesh.TexturePath))
            {
                hasTexture = true;
                txtLoadingStatus.Text = "Loading texture...";
                await LoadTextureFileAsync(mesh.TexturePath);
            }
            
            // Build combined status message
            string statusMsg = $"Loaded: {Path.GetFileName(filepath)}";
            var extras = new List<string>();
            if (hasVertexColors) extras.Add($"{mesh.DetectedPalette!.Length} vertex colors → face paint");
            if (hasEmbeddedTexture) extras.Add("embedded texture");
            else if (hasTexture) extras.Add("texture");
            if (extras.Count > 0) statusMsg += $" ({string.Join(", ", extras)})";
            ShowStatus(statusMsg);

            btnProject.IsEnabled = _textureData != null && _mesh != null;
            Title = $"3MF Tool - {Path.GetFileName(filepath)}";
            
            if (chkShowUVs.IsChecked == true)
            {
                RenderUVOverlay();
                uvOverlayImage.Visibility = Visibility.Visible;
            }
        }
        catch (OperationCanceledException) { ShowStatus("Cancelled"); }
        catch (Exception ex) { MessageBox.Show($"Load failed:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error); }
        finally { loadingOverlay.Visibility = Visibility.Collapsed; btnCancelLoading.Visibility = Visibility.Collapsed; }
    }

    private async void SaveModel_Click(object sender, RoutedEventArgs e)
    {
        if (_mesh == null) { MessageBox.Show("No model to save", "Warning", MessageBoxButton.OK, MessageBoxImage.Warning); return; }

        string defaultName = !string.IsNullOrEmpty(_currentFilePath) ? Path.GetFileName(_currentFilePath) : "export.3mf";
        string baseName = Path.GetFileNameWithoutExtension(defaultName);

        var dlg = new SaveFileDialog 
        { 
            Title = "Save Model", 
            Filter = "3MF (Face Paint)|*.3mf|OBJ (Vertex Colors)|*.obj|GLB (Vertex Colors)|*.glb|GLTF (Vertex Colors)|*.gltf", 
            FileName = baseName + ".3mf"
        };
        if (dlg.ShowDialog() != true) return;

        loadingOverlay.Visibility = Visibility.Visible;
        loadingProgress.IsIndeterminate = false;
        loadingProgress.Value = 0;
        txtLoadingPercent.Text = "0%";
        btnCancelLoading.Visibility = Visibility.Collapsed;
        
        var progress = new Progress<(string, float)>(p => { 
            txtLoadingStatus.Text = p.Item1; 
            loadingProgress.Value = p.Item2 * 100; 
            txtLoadingPercent.Text = $"{(int)(p.Item2 * 100)}%";
        });

        try
        {
            float exportScale = GetExportScale();
            string ext = Path.GetExtension(dlg.FileName).ToLowerInvariant();
            
            // Diagnostic: count color distribution
            var colorCounts = new Dictionary<int, int>();
            foreach (var tri in _mesh.Triangles)
            {
                foreach (var sub in tri.PaintData)
                {
                    colorCounts.TryGetValue(sub.ExtruderId, out int count);
                    colorCounts[sub.ExtruderId] = count + 1;
                }
            }
            string colorInfo = string.Join(", ", colorCounts.OrderBy(kv => kv.Key).Select(kv => $"C{kv.Key}:{kv.Value}"));
            string scaleInfo = MathF.Abs(exportScale - 1.0f) > 0.001f ? $" (scale ×{exportScale})" : "";
            
            bool ok;
            if (ext == ".obj")
            {
                // Export as OBJ with vertex colors
                ok = await ObjExporter.SaveAsync(dlg.FileName, _mesh, _brushPalette, exportScale, progress);
                ShowStatus(ok ? $"Saved OBJ: {Path.GetFileName(dlg.FileName)}{scaleInfo} (vertex colors)" : "Save failed");
            }
            else if (ext == ".glb" || ext == ".gltf")
            {
                // Export as GLB/GLTF with vertex colors
                // Optionally embed texture if we have one
                byte[]? embeddedTex = null;
                if (_textureData != null && _textureWidth > 0 && _textureHeight > 0)
                {
                    // Convert RGBA back to PNG for embedding
                    embeddedTex = CreatePngFromRgba(_textureData, _textureWidth, _textureHeight);
                }
                ok = await GlbExporter.SaveAsync(dlg.FileName, _mesh, _brushPalette, exportScale, embeddedTex, progress);
                ShowStatus(ok ? $"Saved {ext.ToUpper().TrimStart('.')}: {Path.GetFileName(dlg.FileName)}{scaleInfo} (vertex colors)" : "Save failed");
            }
            else
            {
                // Export as 3MF with face painting
                ok = await ThreeMFIO.SaveAsync(dlg.FileName, _mesh, exportScale, progress);
                ShowStatus(ok ? $"Saved 3MF: {Path.GetFileName(dlg.FileName)}{scaleInfo} ({colorInfo})" : "Save failed");
            }
        }
        catch (Exception ex) { MessageBox.Show($"Save failed:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error); }
        finally { loadingOverlay.Visibility = Visibility.Collapsed; }
    }

    private void Tool_Changed(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton rb && rb.Tag is string tag && int.TryParse(tag, out int t))
        {
            _baseTool = (PaintTool)t;
            _viewport?.SetTool(_baseTool);
            txtActiveTool.Text = $"Active: {_baseTool}";
            ShowStatus($"Tool: {_baseTool}");
        }
    }

    private void ClearMasks_Click(object sender, RoutedEventArgs e) { _mesh?.ClearMasks(); _viewport?.RebuildVBO(); ShowStatus("Masks cleared"); }
    private void ClearPaint_Click(object sender, RoutedEventArgs e) { _mesh?.ClearPaint(); _viewport?.RebuildVBO(); ShowStatus("Paint cleared"); }
    private void ResetSubdivisions_Click(object sender, RoutedEventArgs e) 
    { 
        _mesh?.ClearPaint();
        _viewport?.RebuildVBO(); 
        UpdateMeshInfo();
        ShowStatus("Subdivisions reset"); 
    }

    private void BrushSize_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (txtBrushSize != null) { txtBrushSize.Text = $"Size: {e.NewValue:F3}"; _viewport?.SetBrushRadius((float)e.NewValue); }
    }

    private void Display_Changed(object sender, RoutedEventArgs e)
    {
        if (_viewport == null) return;
        _viewport.ShowWireframe = chkWireframe.IsChecked == true;
        _viewport.ShowSubdivisions = chkSubdivisions.IsChecked == true;
        _viewport.ShowTexture = chkShowTexture.IsChecked == true;
        _viewport.FlipTextureH = chkFlipH.IsChecked == true;
        _viewport.FlipTextureV = chkFlipV.IsChecked == true;
        
        // If subdivision edges toggled ON, rebuild VBO with edge data
        if (sender == chkSubdivisions && chkSubdivisions.IsChecked == true)
            _viewport.RebuildVBO(buildSubdivEdges: true);
        else
            _viewport.Invalidate();
    }

    private void PreviewQuantized_Changed(object sender, RoutedEventArgs e)
    {
        if (_viewport == null) return;
        
        bool showQuantized = chkPreviewQuantized.IsChecked == true;
        _viewport.ShowQuantizedTexture = showQuantized;
        
        // When showing quantized preview, disable regular texture display
        if (showQuantized)
        {
            chkShowTexture.IsChecked = false;
            _viewport.ShowTexture = false;
        }
        
        _viewport.Invalidate();
    }

    private void FrameModel_Click(object sender, RoutedEventArgs e) => _viewport?.FrameMesh();
    private void ResetView_Click(object sender, RoutedEventArgs e) => _viewport?.ResetCamera();

    private void LoadTexture_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Title = "Load Texture", Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.tga" };
        if (dlg.ShowDialog() != true) return;
        _ = LoadTextureFileAsync(dlg.FileName);
    }

    private async Task LoadTextureFileAsync(string filepath)
    {
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = new Uri(filepath);
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.EndInit();

            _originalTextureBitmap = bitmap;
            texturePreview.Source = bitmap;
            txtNoTexture.Visibility = Visibility.Collapsed;

            var formatted = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
            _textureWidth = formatted.PixelWidth;
            _textureHeight = formatted.PixelHeight;
            _textureData = new byte[_textureWidth * _textureHeight * 4];
            formatted.CopyPixels(_textureData, _textureWidth * 4, 0);

            // BGRA to RGBA
            for (int i = 0; i < _textureData.Length; i += 4)
            {
                byte b = _textureData[i], r = _textureData[i + 2];
                _textureData[i] = r; _textureData[i + 2] = b;
            }

            _viewport?.SetTexture(_textureData, _textureWidth, _textureHeight);

            btnQuantize.IsEnabled = true;
            btnAutoQuantize.IsEnabled = true;
            btnProject.IsEnabled = _mesh != null;
            _showingQuantized = false;
            btnToggleQuantized.Content = "Show Quantized";
            _quantizedTextureBitmap = null;
            _quantizedPalette = null;
            _originalQuantizedPalette = null;
            _quantizedIndexMap = null;
            _quantizedTextureRgba = null;
            btnToggleQuantized.IsEnabled = false;
            btnExportQuantized.IsEnabled = false;
            btnResetQuantizedColors.IsEnabled = false;
            quantizedPalettePanel.Children.Clear();
            
            // Clear quantized preview on viewport
            _viewport?.ClearQuantizedTexture();
            chkPreviewQuantized.IsChecked = false;
            chkPreviewQuantized.IsEnabled = false;
            
            // Reset auto-quantize state
            btnAutoQuantize.IsChecked = false;
            btnAutoQuantize.Content = "Auto Quantize";
            
            if (chkShowUVs.IsChecked == true)
            {
                RenderUVOverlay();
                uvOverlayImage.Visibility = Visibility.Visible;
            }
                
            ShowStatus($"Loaded texture: {Path.GetFileName(filepath)} ({_textureWidth}x{_textureHeight})");
        }
        catch (Exception ex) { MessageBox.Show($"Load failed:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private async Task LoadTextureFromMemoryAsync(byte[] imageData)
    {
        try
        {
            await Task.Run(() => { }); // Keep async for consistency
            
            var bitmap = new BitmapImage();
            using (var ms = new MemoryStream(imageData))
            {
                bitmap.BeginInit();
                bitmap.StreamSource = ms;
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.EndInit();
            }
            bitmap.Freeze();

            _originalTextureBitmap = bitmap;
            texturePreview.Source = bitmap;
            txtNoTexture.Visibility = Visibility.Collapsed;

            var formatted = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
            _textureWidth = formatted.PixelWidth;
            _textureHeight = formatted.PixelHeight;
            _textureData = new byte[_textureWidth * _textureHeight * 4];
            formatted.CopyPixels(_textureData, _textureWidth * 4, 0);

            // BGRA to RGBA
            for (int i = 0; i < _textureData.Length; i += 4)
            {
                byte b = _textureData[i], r = _textureData[i + 2];
                _textureData[i] = r; _textureData[i + 2] = b;
            }

            _viewport?.SetTexture(_textureData, _textureWidth, _textureHeight);

            btnQuantize.IsEnabled = true;
            btnAutoQuantize.IsEnabled = true;
            btnProject.IsEnabled = _mesh != null;
            _showingQuantized = false;
            btnToggleQuantized.Content = "Show Quantized";
            _quantizedTextureBitmap = null;
            _quantizedPalette = null;
            _originalQuantizedPalette = null;
            _quantizedIndexMap = null;
            _quantizedTextureRgba = null;
            btnToggleQuantized.IsEnabled = false;
            btnExportQuantized.IsEnabled = false;
            btnResetQuantizedColors.IsEnabled = false;
            quantizedPalettePanel.Children.Clear();
            
            _viewport?.ClearQuantizedTexture();
            chkPreviewQuantized.IsChecked = false;
            chkPreviewQuantized.IsEnabled = false;
            
            btnAutoQuantize.IsChecked = false;
            btnAutoQuantize.Content = "Auto Quantize";
            
            if (chkShowUVs.IsChecked == true)
            {
                RenderUVOverlay();
                uvOverlayImage.Visibility = Visibility.Visible;
            }
        }
        catch (Exception ex) { MessageBox.Show($"Load embedded texture failed:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void ShowUVs_Changed(object sender, RoutedEventArgs e)
    {
        if (chkShowUVs.IsChecked == true)
        {
            RenderUVOverlay();
            uvOverlayImage.Visibility = Visibility.Visible;
        }
        else
        {
            uvOverlayImage.Visibility = Visibility.Collapsed;
        }
    }

    private void ToggleQuantized_Click(object sender, RoutedEventArgs e)
    {
        if (_originalTextureBitmap == null) return;
        if (_quantizedPalette == null || _quantizedIndexMap == null) return;

        _showingQuantized = !_showingQuantized;
        
        if (_showingQuantized)
        {
            // Regenerate to ensure we have latest colors
            RegenerateQuantizedTexture();
            texturePreview.Source = _quantizedTextureBitmap;
        }
        else
        {
            texturePreview.Source = _originalTextureBitmap;
        }
        
        btnToggleQuantized.Content = _showingQuantized ? "Show Original" : "Show Quantized";
    }

    private void ExportQuantized_Click(object sender, RoutedEventArgs e)
    {
        if (_quantizedTextureBitmap == null) return;

        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export Quantized Texture",
            Filter = "PNG Image|*.png",
            DefaultExt = ".png",
            FileName = "quantized_texture.png"
        };

        if (dlg.ShowDialog() == true)
        {
            try
            {
                using var fs = new FileStream(dlg.FileName, FileMode.Create);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(_quantizedTextureBitmap));
                encoder.Save(fs);
                ShowStatus($"Exported: {Path.GetFileName(dlg.FileName)}");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Export failed:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private void RenderUVOverlay()
    {
        if (_mesh == null || _textureWidth == 0 || _textureHeight == 0) return;

        int width = _textureWidth;
        int height = _textureHeight;
        
        _uvOverlayBitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
        byte[] pixels = new byte[width * height * 4];

        int maxTris = Math.Min(_mesh.Triangles.Count, 50000);
        int step = Math.Max(1, _mesh.Triangles.Count / maxTris);

        for (int i = 0; i < _mesh.Triangles.Count; i += step)
        {
            var tri = _mesh.Triangles[i];
            if (tri.UV == null) continue;

            float u0 = tri.UV.UV0.X, v0 = tri.UV.UV0.Y;
            float u1 = tri.UV.UV1.X, v1 = tri.UV.UV1.Y;
            float u2 = tri.UV.UV2.X, v2 = tri.UV.UV2.Y;

            u0 = ((u0 % 1f) + 1f) % 1f; v0 = ((v0 % 1f) + 1f) % 1f;
            u1 = ((u1 % 1f) + 1f) % 1f; v1 = ((v1 % 1f) + 1f) % 1f;
            u2 = ((u2 % 1f) + 1f) % 1f; v2 = ((v2 % 1f) + 1f) % 1f;

            int x0 = (int)(u0 * (width - 1));
            int y0 = (int)((1f - v0) * (height - 1));
            int x1 = (int)(u1 * (width - 1));
            int y1 = (int)((1f - v1) * (height - 1));
            int x2 = (int)(u2 * (width - 1));
            int y2 = (int)((1f - v2) * (height - 1));

            DrawLine(pixels, width, height, x0, y0, x1, y1, 0, 255, 0, 200);
            DrawLine(pixels, width, height, x1, y1, x2, y2, 0, 255, 0, 200);
            DrawLine(pixels, width, height, x2, y2, x0, y0, 0, 255, 0, 200);
        }

        _uvOverlayBitmap.WritePixels(new Int32Rect(0, 0, width, height), pixels, width * 4, 0);
        uvOverlayImage.Source = _uvOverlayBitmap;
    }

    private static void DrawLine(byte[] pixels, int width, int height, int x0, int y0, int x1, int y1, byte r, byte g, byte b, byte a)
    {
        int dx = Math.Abs(x1 - x0), sx = x0 < x1 ? 1 : -1;
        int dy = -Math.Abs(y1 - y0), sy = y0 < y1 ? 1 : -1;
        int err = dx + dy;

        while (true)
        {
            if (x0 >= 0 && x0 < width && y0 >= 0 && y0 < height)
            {
                int idx = (y0 * width + x0) * 4;
                pixels[idx] = b; pixels[idx + 1] = g; pixels[idx + 2] = r; pixels[idx + 3] = a;
            }
            if (x0 == x1 && y0 == y1) break;
            int e2 = 2 * err;
            if (e2 >= dy) { err += dy; x0 += sx; }
            if (e2 <= dx) { err += dx; y0 += sy; }
        }
    }

    private void NumColors_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (txtNumColors != null) txtNumColors.Text = ((int)e.NewValue).ToString();
        TriggerAutoQuantize();
    }

    private void ProjSubdiv_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (txtProjSubdiv != null) txtProjSubdiv.Text = ((int)e.NewValue).ToString();
    }

    private void QuantizeParam_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (txtColorWeight != null) txtColorWeight.Text = sliderColorWeight.Value.ToString("F1");
        if (txtPopularityWeight != null) txtPopularityWeight.Text = sliderPopularityWeight.Value.ToString("F1");
        if (txtGamma != null) txtGamma.Text = sliderGamma.Value.ToString("F1");
        if (txtContrast != null) txtContrast.Text = sliderContrast.Value.ToString("F1");
        if (txtBrightness != null) txtBrightness.Text = sliderBrightness.Value.ToString("F1");
        if (txtSaturation != null) txtSaturation.Text = sliderSaturation.Value.ToString("F1");
        if (txtShadowLift != null) txtShadowLift.Text = sliderShadowLift.Value.ToString("F1");
        if (txtHighlightCompress != null) txtHighlightCompress.Text = sliderHighlightCompress.Value.ToString("F1");
        
        // Trigger auto-quantize if enabled
        TriggerAutoQuantize();
    }

    private void QuantizeCheckbox_Changed(object sender, RoutedEventArgs e)
    {
        TriggerAutoQuantize();
    }

    private void QuantizeMethod_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        TriggerAutoQuantize();
    }

    private void AutoQuantize_Click(object sender, RoutedEventArgs e)
    {
        if (btnAutoQuantize.IsChecked == true)
        {
            // Enable auto-quantize and run initial quantization
            btnAutoQuantize.Content = "Auto ✓";
            _ = DoAutoQuantize();
            
            // Auto-enable quantized preview on mesh
            if (_mesh != null && chkPreviewQuantized != null)
            {
                chkPreviewQuantized.IsChecked = true;
            }
        }
        else
        {
            btnAutoQuantize.Content = "Auto Quantize";
        }
    }

    private bool _paintModeEnabled = false;

    private void PaintMode_Click(object sender, RoutedEventArgs e)
    {
        _paintModeEnabled = btnPaintMode.IsChecked == true;
        
        // Update button appearance
        btnPaintMode.Content = _paintModeEnabled ? "ON" : "OFF";
        
        // Sync with viewport
        if (_viewport != null)
        {
            _viewport.PaintEnabled = _paintModeEnabled;
            _viewport.Invalidate(); // Force redraw to show/hide brush
        }
        
        // Update status
        if (_paintModeEnabled)
            ShowStatus("Paint mode enabled - click and drag to paint");
        else
            ShowStatus("Paint mode disabled - painting locked");
    }

    private System.Threading.CancellationTokenSource? _autoQuantizeCts;
    private DateTime _lastAutoQuantize = DateTime.MinValue;

    private async void TriggerAutoQuantize()
    {
        if (btnAutoQuantize?.IsChecked != true || _textureData == null) return;
        
        // Debounce: wait 150ms after last change before quantizing
        _autoQuantizeCts?.Cancel();
        _autoQuantizeCts = new System.Threading.CancellationTokenSource();
        var token = _autoQuantizeCts.Token;
        
        try
        {
            await Task.Delay(150, token);
            if (!token.IsCancellationRequested)
            {
                _ = DoAutoQuantize();
            }
        }
        catch (TaskCanceledException) { }
    }

    private async Task DoAutoQuantize()
    {
        if (_textureData == null) return;

        int numColors = (int)sliderNumColors.Value;
        var method = (QuantizeMethod)cmbQuantizeMethod.SelectedIndex;
        
        // Show processing indicator
        txtProcessing.Visibility = Visibility.Visible;
        ShowStatus($"Auto-quantizing with {method}...");
        
        // Check if palette is locked and we have an existing palette
        bool useLocked = chkLockPalette.IsChecked == true && _quantizedPalette != null;
        Vector3[]? lockedPalette = useLocked ? (Vector3[])_quantizedPalette!.Clone() : null;
        
        int pixelCount = _textureWidth * _textureHeight;
        int dynamicSampleRate = Math.Max(1, pixelCount / 500000);
        
        var options = new QuantizeOptions
        {
            ColorWeight = (float)sliderColorWeight.Value,
            PopularityWeight = (float)sliderPopularityWeight.Value,
            KMeansIterations = 20, // Fewer iterations for speed in auto mode
            SampleRate = dynamicSampleRate,
            Gamma = (float)sliderGamma.Value,
            Contrast = (float)sliderContrast.Value,
            Brightness = (float)sliderBrightness.Value,
            Saturation = (float)sliderSaturation.Value,
            NormalizeLuminance = chkNormalizeLum.IsChecked == true,
            ShadowLift = (float)sliderShadowLift.Value,
            HighlightCompress = (float)sliderHighlightCompress.Value
        };

        try
        {
            // 1. ALWAYS run the algorithm to find the REGIONS/SHAPES
            // The algorithm defines WHERE the color boundaries are
            var naturalPalette = await Task.Run(() => Quantizer.Quantize(
                _textureData, _textureWidth, _textureHeight, numColors, method, options));
            
            if (naturalPalette == null || naturalPalette.Length == 0) return;

            // Validate natural palette
            for (int i = 0; i < naturalPalette.Length; i++)
            {
                var c = naturalPalette[i];
                if (float.IsNaN(c.X) || float.IsNaN(c.Y) || float.IsNaN(c.Z))
                    naturalPalette[i] = new Vector3(0.5f, 0.5f, 0.5f);
                naturalPalette[i] = new Vector3(
                    Math.Clamp(naturalPalette[i].X, 0f, 1f),
                    Math.Clamp(naturalPalette[i].Y, 0f, 1f),
                    Math.Clamp(naturalPalette[i].Z, 0f, 1f));
            }

            if (useLocked && lockedPalette != null)
            {
                // LOCKED MODE: Use algorithm's REGIONS but force LOCKED COLORS
                
                // A. Map pixels to the algorithm's natural palette (defines regions)
                var naturalMap = await Task.Run(() => Quantizer.MapPixelsToPalette(
                    _textureData, _textureWidth, _textureHeight, naturalPalette, options));
                
                // B. Ensure locked palette has correct size
                var effectiveLockedPalette = GetLockedPaletteForSize(lockedPalette, numColors);
                
                // C. Create translation table: natural color index → nearest locked color index
                byte[] translationTable = new byte[naturalPalette.Length];
                for (int i = 0; i < naturalPalette.Length; i++)
                {
                    translationTable[i] = (byte)FindNearestColorIndex(naturalPalette[i], effectiveLockedPalette, options.ColorWeight);
                }
                
                // D. Rewrite the map using locked indices
                _quantizedIndexMap = new byte[naturalMap.Length];
                Parallel.For(0, naturalMap.Length, i =>
                {
                    _quantizedIndexMap[i] = translationTable[naturalMap[i]];
                });
                
                // E. Use locked palette for display
                _quantizedPalette = effectiveLockedPalette;
                
                ShowStatus($"Regions: {method} | Weight: {options.ColorWeight:F1} | Gamma: {options.Gamma:F1}");
            }
            else
            {
                // STANDARD MODE: Use natural palette and map directly
                _quantizedPalette = naturalPalette;
                _originalQuantizedPalette = (Vector3[])naturalPalette.Clone();
                
                _quantizedIndexMap = await Task.Run(() => Quantizer.MapPixelsToPalette(
                    _textureData, _textureWidth, _textureHeight, _quantizedPalette, options));
                
                ShowStatus($"{method}: {numColors} colors | Weight: {options.ColorWeight:F1} | Gamma: {options.Gamma:F1}");
            }

            // 2. Generate texture and update UI
            RegenerateQuantizedTexture();

            _showingQuantized = true;
            texturePreview.Source = _quantizedTextureBitmap;
            btnToggleQuantized.Content = "Show Original";
            btnToggleQuantized.IsEnabled = true;
            btnExportQuantized.IsEnabled = true;
            UpdateQuantizedPaletteDisplay();
            btnResetQuantizedColors.IsEnabled = true;
            
            // Ensure viewport shows the updated quantized texture
            if (chkPreviewQuantized.IsChecked == true && _viewport != null)
            {
                _viewport.ShowQuantizedTexture = true;
                _viewport.Invalidate();
            }
        }
        catch (Exception ex) 
        { 
            ShowStatus($"Auto-quantize error: {ex.Message}");
        }
        finally
        {
            // Hide processing indicator
            txtProcessing.Visibility = Visibility.Collapsed;
        }
    }

    private async void Quantize_Click(object sender, RoutedEventArgs e)
    {
        if (_textureData == null) return;

        int numColors = (int)sliderNumColors.Value;
        var method = (QuantizeMethod)cmbQuantizeMethod.SelectedIndex;
        
        // Check if palette is locked and we have an existing palette
        bool useLocked = chkLockPalette.IsChecked == true && _quantizedPalette != null;
        Vector3[]? lockedPalette = useLocked ? (Vector3[])_quantizedPalette!.Clone() : null;
        
        // Dynamic sample rate for large images - prevents freezing on 4K+ textures
        int pixelCount = _textureWidth * _textureHeight;
        int dynamicSampleRate = Math.Max(1, pixelCount / 500000);
        
        var options = new QuantizeOptions
        {
            ColorWeight = (float)sliderColorWeight.Value,
            PopularityWeight = (float)sliderPopularityWeight.Value,
            KMeansIterations = 30,
            SampleRate = dynamicSampleRate,
            Gamma = (float)sliderGamma.Value,
            Contrast = (float)sliderContrast.Value,
            Brightness = (float)sliderBrightness.Value,
            Saturation = (float)sliderSaturation.Value,
            NormalizeLuminance = chkNormalizeLum.IsChecked == true,
            ShadowLift = (float)sliderShadowLift.Value,
            HighlightCompress = (float)sliderHighlightCompress.Value
        };

        loadingOverlay.Visibility = Visibility.Visible;
        txtLoadingStatus.Text = useLocked ? $"Finding regions with {method}..." : $"Quantizing with {method}...";
        loadingProgress.IsIndeterminate = true;
        txtLoadingPercent.Text = "";
        btnCancelLoading.Visibility = Visibility.Collapsed;

        try
        {
            // 1. ALWAYS run the algorithm to find the REGIONS/SHAPES
            txtLoadingStatus.Text = $"Running {method} algorithm...";
            var naturalPalette = await Task.Run(() => Quantizer.Quantize(
                _textureData, _textureWidth, _textureHeight, numColors, method, options));
            
            if (naturalPalette == null || naturalPalette.Length == 0)
            {
                MessageBox.Show($"Quantization returned no colors for method {method}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            // Validate natural palette
            for (int i = 0; i < naturalPalette.Length; i++)
            {
                var c = naturalPalette[i];
                if (float.IsNaN(c.X) || float.IsNaN(c.Y) || float.IsNaN(c.Z) ||
                    float.IsInfinity(c.X) || float.IsInfinity(c.Y) || float.IsInfinity(c.Z))
                    naturalPalette[i] = new Vector3(0.5f, 0.5f, 0.5f);
                naturalPalette[i] = new Vector3(
                    Math.Clamp(naturalPalette[i].X, 0f, 1f),
                    Math.Clamp(naturalPalette[i].Y, 0f, 1f),
                    Math.Clamp(naturalPalette[i].Z, 0f, 1f));
            }

            if (useLocked && lockedPalette != null)
            {
                // LOCKED MODE: Use algorithm's REGIONS but force LOCKED COLORS
                txtLoadingStatus.Text = "Mapping regions to locked colors...";
                
                // A. Map pixels to the algorithm's natural palette (defines regions)
                var naturalMap = await Task.Run(() => Quantizer.MapPixelsToPalette(
                    _textureData, _textureWidth, _textureHeight, naturalPalette, options));
                
                // B. Ensure locked palette has correct size
                var effectiveLockedPalette = GetLockedPaletteForSize(lockedPalette, numColors);
                
                // C. Create translation table: natural color index → nearest locked color index
                byte[] translationTable = new byte[naturalPalette.Length];
                for (int i = 0; i < naturalPalette.Length; i++)
                {
                    translationTable[i] = (byte)FindNearestColorIndex(naturalPalette[i], effectiveLockedPalette, options.ColorWeight);
                }
                
                // D. Rewrite the map using locked indices
                _quantizedIndexMap = new byte[naturalMap.Length];
                Parallel.For(0, naturalMap.Length, i =>
                {
                    _quantizedIndexMap[i] = translationTable[naturalMap[i]];
                });
                
                // E. Use locked palette for display
                _quantizedPalette = effectiveLockedPalette;
                
                ShowStatus($"Regions defined by {method}, mapped to locked palette.");
            }
            else
            {
                // STANDARD MODE: Use natural palette and map directly
                txtLoadingStatus.Text = "Building index map...";
                
                _quantizedPalette = naturalPalette;
                _originalQuantizedPalette = (Vector3[])naturalPalette.Clone();
                
                _quantizedIndexMap = await Task.Run(() => Quantizer.MapPixelsToPalette(
                    _textureData, _textureWidth, _textureHeight, _quantizedPalette, options));
                
                ShowStatus($"Quantized to {numColors} colors using {method}. Edit colors in palette, then Project.");
            }

            // Generate texture and update UI
            RegenerateQuantizedTexture();

            _showingQuantized = true;
            texturePreview.Source = _quantizedTextureBitmap;
            btnToggleQuantized.Content = "Show Original";
            btnToggleQuantized.IsEnabled = true;
            btnExportQuantized.IsEnabled = true;
            UpdateQuantizedPaletteDisplay();
            btnResetQuantizedColors.IsEnabled = true;
        }
        catch (Exception ex) 
        { 
            MessageBox.Show($"Quantization failed:\n{ex.Message}\n\nStack trace:\n{ex.StackTrace}", "Error", MessageBoxButton.OK, MessageBoxImage.Error); 
        }
        finally { loadingOverlay.Visibility = Visibility.Collapsed; loadingProgress.IsIndeterminate = false; }
    }

    private async void ProjectTexture_Click(object sender, RoutedEventArgs e)
    {
        if (_mesh == null || _textureData == null) return;

        // Use quantized palette if available, otherwise offer to quantize
        if (_quantizedPalette == null || _quantizedPalette.Length == 0) 
        { 
            var result = ShowDarkDialog(
                "Quantization Required",
                "A quantized texture is needed for projection.\n\nWould you like to quantize the texture now?",
                "Yes, Quantize",
                "Cancel");
            
            if (result == true)
            {
                // Run quantization
                await DoAutoQuantize();
                
                // Check if quantization succeeded
                if (_quantizedPalette == null || _quantizedPalette.Length == 0)
                {
                    ShowStatus("Quantization failed or was cancelled");
                    return;
                }
            }
            else
            {
                return;
            }
        }

        // Validate palette colors aren't corrupted
        for (int i = 0; i < _quantizedPalette.Length; i++)
        {
            var c = _quantizedPalette[i];
            if (float.IsNaN(c.X) || float.IsNaN(c.Y) || float.IsNaN(c.Z))
            {
                ShowStatus($"Warning: Color {i} is invalid, resetting to original");
                if (_originalQuantizedPalette != null && i < _originalQuantizedPalette.Length)
                    _quantizedPalette[i] = _originalQuantizedPalette[i];
                else
                    _quantizedPalette[i] = new Vector3(0.5f, 0.5f, 0.5f);
            }
        }

        loadingOverlay.Visibility = Visibility.Visible;
        loadingProgress.IsIndeterminate = false;
        btnCancelLoading.Visibility = Visibility.Collapsed; // Can't cancel projection easily
        
        var progress = new Progress<(string, float)>(p => { 
            txtLoadingStatus.Text = p.Item1; 
            loadingProgress.Value = p.Item2 * 100;
            txtLoadingPercent.Text = $"{(int)(p.Item2 * 100)}%";
        });

        try
        {
            int subdiv = (int)sliderProjSubdiv.Value;

            // Warn about high subdivision levels
            long estimatedSubTris = (long)_mesh.Triangles.Count * (long)Math.Pow(4, subdiv);
            if (subdiv >= 9)
            {
                var result = MessageBox.Show(
                    $"Subdivision level {subdiv} will create approximately {estimatedSubTris:N0} sub-triangles.\n\n" +
                    $"This may take several minutes and use significant memory.\n\n" +
                    $"Continue?",
                    "High Subdivision Warning",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);
                if (result != MessageBoxResult.Yes) 
                {
                    loadingOverlay.Visibility = Visibility.Collapsed;
                    return;
                }
            }

            // Build brush palette directly from quantized colors
            // ExtruderId 1 = palette[0], ExtruderId 2 = palette[1], etc.
            // (PrusaSlicer uses 1-based extruder IDs, 0 = default/unpainted)
            var newBrushPalette = new Vector3[_quantizedPalette.Length];
            for (int i = 0; i < _quantizedPalette.Length; i++) 
            {
                newBrushPalette[i] = new Vector3(_quantizedPalette[i].X, _quantizedPalette[i].Y, _quantizedPalette[i].Z);
            }

            // Capture references for background thread
            var indexMap = _quantizedIndexMap;
            int texWidth = _textureWidth;
            int texHeight = _textureHeight;
            var texData = _textureData;
            var mesh = _mesh;
            
            txtLoadingStatus.Text = $"Projecting texture (subdivisions: {subdiv}, ~{estimatedSubTris:N0} sub-triangles)...";
            
            await Task.Run(() =>
            {
                if (indexMap != null && subdiv > 0)
                {
                    // No offset - Color 0 = first extruder, Color 1 = second, etc.
                    TextureProjector.ProjectWithIndexMap(mesh, indexMap, texWidth, texHeight, 0, subdiv, progress);
                }
                else if (subdiv > 0)
                {
                    TextureProjector.ProjectToMeshSubdivided(mesh, texData, texWidth, texHeight, newBrushPalette, subdiv, progress);
                }
                else
                {
                    TextureProjector.ProjectToMesh(mesh, texData, texWidth, texHeight, newBrushPalette, progress);
                }
            });

            // Set the brush palette
            _brushPalette = newBrushPalette;
            _meshHasProjectedTexture = true;
            
            // Diagnostic: count triangles and color distribution
            int totalSubTris = 0;
            var colorCounts = new Dictionary<int, int>();
            foreach (var tri in _mesh.Triangles)
            {
                totalSubTris += tri.PaintData.Count;
                foreach (var sub in tri.PaintData)
                {
                    colorCounts.TryGetValue(sub.ExtruderId, out int count);
                    colorCounts[sub.ExtruderId] = count + 1;
                }
            }
            
            // Update UI
            SetupColorPanel();
            
            // CRITICAL: Update viewport with mesh AND palette together
            // This rebuilds VBO, spatial grid, brush state, and clears undo history
            txtLoadingStatus.Text = "Rebuilding display...";
            _viewport?.SetMeshWithPalette(_mesh, _brushPalette);
            
            UpdateMeshInfo();
            string colorInfo = string.Join(", ", colorCounts.OrderBy(kv => kv.Key).Select(kv => $"C{kv.Key}:{kv.Value}"));
            ShowStatus($"Projected {totalSubTris:N0} sub-triangles. Colors: {colorInfo}");
        }
        catch (Exception ex) 
        { 
            MessageBox.Show($"Projection failed:\n{ex.Message}\n\n{ex.StackTrace}", "Error", MessageBoxButton.OK, MessageBoxImage.Error); 
        }
        finally { loadingOverlay.Visibility = Visibility.Collapsed; }
    }

    private void UpdateMeshInfo()
    {
        if (_mesh == null) { txtMeshInfo.Text = "No mesh loaded"; txtMeshInfo.Foreground = (System.Windows.Media.Brush)FindResource("TextDimBrush"); return; }
        
        int uvCount = _mesh.Triangles.Count(t => t.UV != null);
        string projStatus = _meshHasProjectedTexture ? " [Projected]" : "";
        txtMeshInfo.Text = $"Vertices: {_mesh.Vertices.Count:N0}\n" +
                          $"Triangles: {_mesh.Triangles.Count:N0}\n" +
                          $"With UVs: {uvCount:N0}\n" +
                          $"Sub-triangles: {_mesh.TotalSubTriangles:N0}{projStatus}\n" +
                          $"Bounds: {_mesh.BoundingRadius:F1} units";
        txtMeshInfo.Foreground = (System.Windows.Media.Brush)FindResource("TextBrush");
    }

    private void ShowStatus(string msg) => txtStatus.Text = msg;

    /// <summary>
    /// Shows a dark-themed dialog with custom buttons.
    /// Returns true if primary button clicked, false if secondary, null if closed.
    /// </summary>
    private bool? ShowDarkDialog(string title, string message, string primaryButton, string secondaryButton)
    {
        var dialog = new Window
        {
            Title = title,
            Width = 400,
            Height = 180,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            ResizeMode = ResizeMode.NoResize,
            Background = new SolidColorBrush(Color.FromRgb(30, 30, 30)),
            Foreground = Brushes.White,
            WindowStyle = WindowStyle.ToolWindow
        };

        bool? result = null;

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var messageText = new TextBlock
        {
            Text = message,
            Foreground = Brushes.White,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(20),
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 14
        };
        Grid.SetRow(messageText, 0);
        grid.Children.Add(messageText);

        var buttonPanel = new System.Windows.Controls.StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            Margin = new Thickness(20, 10, 20, 20)
        };
        Grid.SetRow(buttonPanel, 1);

        var primaryBtn = new System.Windows.Controls.Button
        {
            Content = primaryButton,
            Padding = new Thickness(20, 8, 20, 8),
            Margin = new Thickness(0, 0, 10, 0),
            Background = new SolidColorBrush(Color.FromRgb(0, 122, 204)),
            Foreground = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0, 122, 204))
        };
        primaryBtn.Click += (s, e) => { result = true; dialog.Close(); };

        var secondaryBtn = new System.Windows.Controls.Button
        {
            Content = secondaryButton,
            Padding = new Thickness(20, 8, 20, 8),
            Background = new SolidColorBrush(Color.FromRgb(60, 60, 60)),
            Foreground = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(80, 80, 80))
        };
        secondaryBtn.Click += (s, e) => { result = false; dialog.Close(); };

        buttonPanel.Children.Add(primaryBtn);
        buttonPanel.Children.Add(secondaryBtn);
        grid.Children.Add(buttonPanel);

        dialog.Content = grid;
        dialog.ShowDialog();

        return result;
    }

    /// <summary>
    /// Returns a palette of the requested size, preserving locked colors.
    /// If expanding, generates new colors that are distinct from existing ones.
    /// If contracting, keeps the first N colors.
    /// </summary>
    private Vector3[] GetLockedPaletteForSize(Vector3[] lockedPalette, int targetSize)
    {
        if (lockedPalette.Length == targetSize)
            return (Vector3[])lockedPalette.Clone();
        
        var result = new Vector3[targetSize];
        
        // Copy existing colors (up to target size)
        int copyCount = Math.Min(lockedPalette.Length, targetSize);
        for (int i = 0; i < copyCount; i++)
            result[i] = lockedPalette[i];
        
        // If we need more colors, generate new distinct ones
        if (targetSize > lockedPalette.Length)
        {
            // Generate colors that are maximally different from existing
            for (int i = lockedPalette.Length; i < targetSize; i++)
            {
                // Try to find a color that's far from all existing colors
                Vector3 bestColor = new Vector3(0.5f, 0.5f, 0.5f);
                float bestMinDist = 0;
                
                // Try several candidate colors
                for (int attempt = 0; attempt < 50; attempt++)
                {
                    // Generate candidate using golden ratio for good distribution
                    float hue = (i * 0.618033988749895f + attempt * 0.1f) % 1.0f;
                    float sat = 0.6f + (attempt % 3) * 0.15f;
                    float val = 0.7f + (attempt % 4) * 0.1f;
                    
                    var candidate = HsvToRgb(hue, sat, val);
                    
                    // Find minimum distance to all existing colors in result
                    float minDist = float.MaxValue;
                    for (int j = 0; j < i; j++)
                    {
                        float dist = Vector3.DistanceSquared(candidate, result[j]);
                        if (dist < minDist) minDist = dist;
                    }
                    
                    if (minDist > bestMinDist)
                    {
                        bestMinDist = minDist;
                        bestColor = candidate;
                    }
                }
                
                result[i] = bestColor;
            }
        }
        
        return result;
    }

    private static Vector3 HsvToRgb(float h, float s, float v)
    {
        int hi = (int)(h * 6) % 6;
        float f = h * 6 - hi;
        float p = v * (1 - s);
        float q = v * (1 - f * s);
        float t = v * (1 - (1 - f) * s);
        return hi switch { 
            0 => new Vector3(v, t, p), 
            1 => new Vector3(q, v, p), 
            2 => new Vector3(p, v, t), 
            3 => new Vector3(p, q, v), 
            4 => new Vector3(t, p, v), 
            _ => new Vector3(v, p, q) 
        };
    }

    /// <summary>
    /// Find the index of the nearest color in the palette using perceptual weighted distance.
    /// Used for translating algorithm-defined regions to locked palette colors.
    /// </summary>
    private static int FindNearestColorIndex(Vector3 target, Vector3[] palette, float weight)
    {
        int bestIndex = 0;
        float minDist = float.MaxValue;

        for (int i = 0; i < palette.Length; i++)
        {
            // Weighted distance with perceptual color weighting (matches Quantizer logic)
            float dr = (target.X - palette[i].X) * weight;
            float dg = (target.Y - palette[i].Y) * weight;
            float db = (target.Z - palette[i].Z) * weight;
            
            // Perceptual weighting: green > red > blue
            float dist = dr * dr * 0.299f + dg * dg * 0.587f + db * db * 0.114f;
            
            if (dist < minDist)
            {
                minDist = dist;
                bestIndex = i;
            }
        }
        return bestIndex;
    }

    private void AutoSetSubdivisionLevel(int triangleCount)
    {
        // Auto-set subdivision based on mesh complexity
        // Goal: balance detail vs performance
        // Low poly needs high subdivision, high poly needs low
        int subdiv;
        if (triangleCount < 50)
            subdiv = 10;  // Very low poly - max detail
        else if (triangleCount < 100)
            subdiv = 8;
        else if (triangleCount < 500)
            subdiv = 6;
        else if (triangleCount < 1000)
            subdiv = 5;
        else if (triangleCount < 5000)
            subdiv = 4;
        else if (triangleCount < 10000)
            subdiv = 3;
        else if (triangleCount < 50000)
            subdiv = 2;
        else
            subdiv = 1;  // Very high poly - minimal subdivision

        sliderProjSubdiv.Value = subdiv;
        ShowStatus($"Auto-set subdivision to {subdiv} for {triangleCount:N0} triangles");
    }

    /// <summary>
    /// Get import scale factor based on UI selection and auto-detection.
    /// </summary>
    private float GetImportScale(Mesh mesh)
    {
        int selectedIndex = cmbImportScale.SelectedIndex;
        
        // Custom scale
        if (selectedIndex == 5)
        {
            if (float.TryParse(txtCustomImportScale.Text, out float customScale))
                return customScale;
            return 1.0f;
        }
        
        // Auto-detect based on bounding box size
        if (selectedIndex == 0)
        {
            float size = mesh.BoundingRadius * 2f; // Diameter
            
            // Heuristics for detecting source units:
            // - If bounding box < 0.5 units, probably meters → scale to mm (×1000)
            // - If bounding box 0.5-5 units, probably centimeters → scale to mm (×10)  
            // - If bounding box 5-500 units, probably already mm → no scale
            // - If bounding box > 500 units, could be mm or other issue → no scale
            
            if (size < 0.5f)
            {
                ShowStatus($"Auto-detected: model appears to be in meters (size={size:F3}), scaling ×1000");
                return 1000f;
            }
            else if (size < 5f)
            {
                ShowStatus($"Auto-detected: model appears to be in centimeters (size={size:F2}), scaling ×10");
                return 10f;
            }
            else
            {
                ShowStatus($"Auto-detected: model appears to be in millimeters (size={size:F1})");
                return 1.0f;
            }
        }
        
        // Fixed scale options
        return selectedIndex switch
        {
            1 => 1000f,   // m → mm
            2 => 10f,     // cm → mm
            3 => 1.0f,    // mm (no change)
            4 => 25.4f,   // inches → mm
            _ => 1.0f
        };
    }

    private void ImportScale_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        // Show/hide custom scale textbox
        if (txtCustomImportScale != null)
            txtCustomImportScale.Visibility = cmbImportScale.SelectedIndex == 5 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ExportScale_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        // Show/hide custom scale textbox
        if (txtCustomExportScale != null)
            txtCustomExportScale.Visibility = cmbExportScale.SelectedIndex == 5 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void LimitExtruders_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (txtLimitExtruders != null)
            txtLimitExtruders.Text = ((int)e.NewValue).ToString();
    }

    private void SmoothRadius_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (txtSmoothRadius != null)
            txtSmoothRadius.Text = ((int)e.NewValue).ToString();
    }

    /// <summary>
    /// Destructive palette quantization with optimization and smoothing.
    /// </summary>
    private void ApplyExtruderLimit_Click(object sender, RoutedEventArgs e)
    {
        if (_mesh == null || _viewport == null)
        {
            ShowStatus("No mesh loaded");
            return;
        }
        
        int limit = (int)sliderLimitExtruders.Value; // 1-8
        int smoothRadius = (int)sliderSmoothRadius.Value; // 0-500
        
        // OPTIMIZATION: If expanding limit (e.g., 5 -> 8) and no smoothing,
        // just update UI - no mesh processing needed!
        if (limit >= _currentExtruderLimit && smoothRadius == 0)
        {
            // Just unlock more colors in UI
            _viewport.SetMaxPaintColor(limit - 1);
            _currentExtruderLimit = limit;
            SetupColorPanel();
            ShowStatus($"Limit increased to {limit} (no mesh changes)");
            return;
        }
        
        // 1. Undo Backup
        _paletteBackup = new Vector3[8];
        Array.Copy(_brushPalette, _paletteBackup, 8);
        _limitBackup = _currentExtruderLimit;
        
        _viewport.BeginBulkOperation();

        int remappedCount = 0;

        // 2. DESTRUCTIVE REMAP (only if reducing colors)
        if (limit < _currentExtruderLimit)
        {
            var remapTable = new int[256];
            
            // Valid range (0 to limit-1)
            for (int i = 0; i < limit; i++) remapTable[i] = i;

            // Invalid range (limit to 255) -> remap to nearest valid
            for (int i = limit; i < 256; i++)
            {
                int srcIdx = Math.Clamp(i, 0, 7);
                Vector3 srcColor = _brushPalette[srcIdx];
                float minDist = float.MaxValue;
                int bestMatch = 0;
                
                for (int j = 0; j < limit; j++)
                {
                    float dist = Vector3.DistanceSquared(srcColor, _brushPalette[j]);
                    if (dist < minDist) { minDist = dist; bestMatch = j; }
                }
                remapTable[i] = bestMatch;
            }

            // Apply Remap
            for (int i = 0; i < _mesh.Triangles.Count; i++)
            {
                var tri = _mesh.Triangles[i];
                for (int k = 0; k < tri.PaintData.Count; k++)
                {
                    int oldId = tri.PaintData[k].ExtruderId;
                    int safeId = Math.Clamp(oldId, 0, 255);
                    int newId = remapTable[safeId];

                    if (newId != oldId)
                    {
                        tri.PaintData[k].ExtruderId = newId;
                        remappedCount++;
                    }
                }
            }
        }

        // 3. SMOOTHING / DENOISING
        int smoothedCount = 0;
        if (smoothRadius > 0)
        {
            smoothedCount = DenoiseMesh(_mesh, smoothRadius, limit);
        }

        _viewport.EndBulkOperation();

        // 4. Update UI
        _paletteBackupUndoCount = _viewport.UndoCount;
        
        // Zero out unused palette slots
        for (int i = limit; i < 8; i++) 
            _brushPalette[i] = Vector3.Zero;
        
        _viewport.SetMaxPaintColor(limit - 1);
        _currentExtruderLimit = limit;
        
        _viewport.SetPalette(_brushPalette);
        _viewport.RebuildVBO();
        SetupColorPanel();
        UpdateUndoButtons();
        
        ShowStatus($"Limit {limit}: {remappedCount} remapped, {smoothedCount} specks removed");
    }
    
    /// <summary>
    /// Removes isolated islands of color smaller than minRegionSize
    /// by merging them into their dominant neighbor color.
    /// </summary>
    private int DenoiseMesh(Mesh mesh, int minRegionSize, int maxValidColor)
    {
        if (minRegionSize <= 0) return 0;
        
        // 1. Build adjacency graph
        var adjacency = BuildTriangleAdjacency(mesh);
        
        // 2. Get current dominant color per triangle
        var triangleColors = new int[mesh.Triangles.Count];
        for (int i = 0; i < mesh.Triangles.Count; i++)
        {
            triangleColors[i] = GetTriangleColor(mesh.Triangles[i]);
        }

        int totalFixed = 0;
        
        // Run multiple passes to handle cascading small regions
        for (int pass = 0; pass < 5; pass++)
        {
            // 3. Find all connected regions
            var regions = FindColorRegions(triangleColors, adjacency);
            
            // 4. Filter for small regions (speckles)
            var smallRegions = regions
                .Where(r => r.Triangles.Count < minRegionSize)
                .OrderBy(r => r.Triangles.Count)
                .ToList();
            
            if (smallRegions.Count == 0) break;

            int passFixed = 0;

            // 5. Absorb small regions into neighbors
            foreach (var region in smallRegions)
            {
                var neighborColorCounts = new Dictionary<int, int>();
                
                foreach (int triIdx in region.Triangles)
                {
                    if (adjacency.TryGetValue(triIdx, out var neighbors))
                    {
                        foreach (int n in neighbors)
                        {
                            if (!region.Triangles.Contains(n))
                            {
                                int nColor = triangleColors[n];
                                // Only count valid colors
                                if (nColor >= 0 && nColor < maxValidColor)
                                    neighborColorCounts[nColor] = neighborColorCounts.GetValueOrDefault(nColor, 0) + 1;
                            }
                        }
                    }
                }
                
                if (neighborColorCounts.Count > 0)
                {
                    int targetColor = neighborColorCounts.OrderByDescending(k => k.Value).First().Key;
                    
                    foreach (int triIdx in region.Triangles)
                    {
                        var tri = mesh.Triangles[triIdx];
                        
                        // Set all sub-triangles to target color
                        for (int k = 0; k < tri.PaintData.Count; k++)
                        {
                            tri.PaintData[k].ExtruderId = targetColor;
                        }
                        
                        triangleColors[triIdx] = targetColor;
                        passFixed++;
                    }
                }
            }
            
            totalFixed += passFixed;
            if (passFixed == 0) break;
        }
        
        return totalFixed;
    }
    
    /// <summary>
    /// Find all connected regions of the same color
    /// </summary>
    private List<ColorRegion> FindColorRegions(int[] triangleColors, Dictionary<int, List<int>> adjacency)
    {
        var regions = new List<ColorRegion>();
        var visited = new bool[triangleColors.Length];
        
        for (int i = 0; i < triangleColors.Length; i++)
        {
            if (visited[i]) continue;
            
            // Flood fill to find connected region
            var region = new ColorRegion { Color = triangleColors[i] };
            var queue = new Queue<int>();
            queue.Enqueue(i);
            visited[i] = true;
            
            while (queue.Count > 0)
            {
                int triIdx = queue.Dequeue();
                region.Triangles.Add(triIdx);
                
                if (!adjacency.TryGetValue(triIdx, out var neighbors)) continue;
                
                foreach (int n in neighbors)
                {
                    if (!visited[n] && triangleColors[n] == region.Color)
                    {
                        visited[n] = true;
                        queue.Enqueue(n);
                    }
                }
            }
            
            regions.Add(region);
        }
        
        return regions;
    }
    
    private class ColorRegion
    {
        public int Color;
        public HashSet<int> Triangles = new();
    }
    
    /// <summary>
    /// Get the primary color of a triangle (from first SubTriangle)
    /// </summary>
    private static int GetTriangleColor(Triangle tri)
    {
        if (tri.PaintData.Count > 0)
            return tri.PaintData[0].ExtruderId;
        return 0;
    }
    
    /// <summary>
    /// Set the color of a triangle (clears subdivisions, sets single color)
    /// </summary>
    private static void SetTriangleColor(Triangle tri, int color)
    {
        tri.PaintData.Clear();
        tri.PaintData.Add(new SubTriangle { ExtruderId = color });
    }
    
    /// <summary>
    /// Build triangle adjacency map (triangles sharing edges)
    /// </summary>
    private static Dictionary<int, List<int>> BuildTriangleAdjacency(Mesh mesh)
    {
        var edgeToTri = new Dictionary<(int, int), List<int>>();
        var adjacency = new Dictionary<int, List<int>>();
        
        for (int i = 0; i < mesh.Triangles.Count; i++)
        {
            adjacency[i] = new List<int>();
            var tri = mesh.Triangles[i];
            
            AddEdgeForAdjacency(edgeToTri, tri.Indices.V0, tri.Indices.V1, i);
            AddEdgeForAdjacency(edgeToTri, tri.Indices.V1, tri.Indices.V2, i);
            AddEdgeForAdjacency(edgeToTri, tri.Indices.V2, tri.Indices.V0, i);
        }
        
        foreach (var tris in edgeToTri.Values)
        {
            if (tris.Count == 2)
            {
                int a = tris[0], b = tris[1];
                if (!adjacency[a].Contains(b)) adjacency[a].Add(b);
                if (!adjacency[b].Contains(a)) adjacency[b].Add(a);
            }
        }
        
        return adjacency;
    }
    
    private static void AddEdgeForAdjacency(Dictionary<(int, int), List<int>> edgeToTri, int v0, int v1, int triIdx)
    {
        var key = v0 < v1 ? (v0, v1) : (v1, v0);
        if (!edgeToTri.TryGetValue(key, out var list))
        {
            list = new List<int>();
            edgeToTri[key] = list;
        }
        list.Add(triIdx);
    }
    
    /// <summary>
    /// Get export scale factor based on UI selection.
    /// </summary>
    private float GetExportScale()
    {
        int selectedIndex = cmbExportScale.SelectedIndex;
        
        // Custom scale
        if (selectedIndex == 5)
        {
            if (float.TryParse(txtCustomExportScale.Text, out float customScale))
                return customScale;
            return 1.0f;
        }
        
        return selectedIndex switch
        {
            0 => 1.0f,      // No change
            1 => 0.001f,    // mm → m
            2 => 0.1f,      // mm → cm
            3 => 10f,       // cm → mm
            4 => 1000f,     // m → mm
            _ => 1.0f
        };
    }

    /// <summary>
    /// Convert RGBA pixel data to PNG bytes for embedding in GLB.
    /// </summary>
    private byte[]? CreatePngFromRgba(byte[] rgba, int width, int height)
    {
        try
        {
            // Create WriteableBitmap from RGBA data
            var bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
            
            // Convert RGBA to BGRA
            var bgra = new byte[rgba.Length];
            for (int i = 0; i < rgba.Length; i += 4)
            {
                bgra[i] = rgba[i + 2];     // B
                bgra[i + 1] = rgba[i + 1]; // G
                bgra[i + 2] = rgba[i];     // R
                bgra[i + 3] = rgba[i + 3]; // A
            }
            
            bitmap.WritePixels(new Int32Rect(0, 0, width, height), bgra, width * 4, 0);
            
            // Encode to PNG
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            
            using var ms = new MemoryStream();
            encoder.Save(ms);
            return ms.ToArray();
        }
        catch
        {
            return null;
        }
    }
}
