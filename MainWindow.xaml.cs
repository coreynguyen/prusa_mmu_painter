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
    private bool _meshHasProjectedTexture; // Track if mesh was projected

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

    private void Undo_Click(object sender, RoutedEventArgs e)
    {
        _viewport?.Undo();
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
            if (e.Key == Key.O) { OpenModel_Click(sender, e); e.Handled = true; }
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
            var border = new Border
            {
                Width = 24, Height = 24, Margin = new Thickness(1), BorderThickness = new Thickness(2),
                BorderBrush = i == _currentColor ? Brushes.White : Brushes.Transparent,
                Background = new SolidColorBrush(Color.FromRgb((byte)(c.X * 255), (byte)(c.Y * 255), (byte)(c.Z * 255))),
                Tag = i, Cursor = Cursors.Hand, ToolTip = $"Extruder {i + 1} (press {i}, double-click to edit)"
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
                    // Single click: select
                    _currentColor = idx; 
                    _viewport?.SetCurrentColor(idx); 
                    UpdateColorSelection(); 
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

    private async void OpenModel_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Title = "Open Model", Filter = "3D Models|*.obj;*.3mf|OBJ|*.obj|3MF|*.3mf" };
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
            if (ext == ".3mf")
            {
                mesh = await ThreeMFIO.LoadAsync(filepath, progress, ct);
            }
            else
            {
                mesh = await ObjLoader.LoadAsync(filepath, progress, ct);
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

            if (!string.IsNullOrEmpty(mesh.TexturePath) && File.Exists(mesh.TexturePath))
            {
                txtLoadingStatus.Text = "Loading texture...";
                await LoadTextureFileAsync(mesh.TexturePath);
                ShowStatus($"Loaded: {Path.GetFileName(filepath)} (with texture)");
            }
            else
            {
                ShowStatus($"Loaded: {Path.GetFileName(filepath)}");
            }

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
        if (!defaultName.EndsWith(".3mf", StringComparison.OrdinalIgnoreCase)) defaultName = Path.GetFileNameWithoutExtension(defaultName) + ".3mf";

        var dlg = new SaveFileDialog { Title = "Save 3MF", Filter = "3MF|*.3mf", FileName = defaultName };
        if (dlg.ShowDialog() != true) return;

        loadingOverlay.Visibility = Visibility.Visible;
        loadingProgress.IsIndeterminate = false;
        loadingProgress.Value = 0;
        txtLoadingPercent.Text = "0%";
        btnCancelLoading.Visibility = Visibility.Collapsed; // Can't cancel save
        
        var progress = new Progress<(string, float)>(p => { 
            txtLoadingStatus.Text = p.Item1; 
            loadingProgress.Value = p.Item2 * 100; 
            txtLoadingPercent.Text = $"{(int)(p.Item2 * 100)}%";
        });

        try
        {
            // Diagnostic: count color distribution before save
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
            
            bool ok = await ThreeMFIO.SaveAsync(dlg.FileName, _mesh, progress);
            ShowStatus(ok ? $"Saved: {Path.GetFileName(dlg.FileName)} ({colorInfo})" : "Save failed");
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
            btnProject.IsEnabled = _mesh != null;
            _showingQuantized = false;
            btnToggleQuantized.Content = "Show Quantized";
            _quantizedTextureBitmap = null;
            _quantizedPalette = null;
            _originalQuantizedPalette = null;
            _quantizedIndexMap = null;
            btnToggleQuantized.IsEnabled = false;
            btnExportQuantized.IsEnabled = false;
            btnResetQuantizedColors.IsEnabled = false;
            quantizedPalettePanel.Children.Clear();
            
            if (chkShowUVs.IsChecked == true)
            {
                RenderUVOverlay();
                uvOverlayImage.Visibility = Visibility.Visible;
            }
                
            ShowStatus($"Loaded texture: {Path.GetFileName(filepath)} ({_textureWidth}x{_textureHeight})");
        }
        catch (Exception ex) { MessageBox.Show($"Load failed:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error); }
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
    }

    private void ProjSubdiv_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (txtProjSubdiv != null) txtProjSubdiv.Text = ((int)e.NewValue).ToString();
    }

    private async void Quantize_Click(object sender, RoutedEventArgs e)
    {
        if (_textureData == null) return;

        int numColors = (int)sliderNumColors.Value;
        var method = (QuantizeMethod)cmbQuantizeMethod.SelectedIndex;

        loadingOverlay.Visibility = Visibility.Visible;
        txtLoadingStatus.Text = "Quantizing...";
        loadingProgress.IsIndeterminate = true;
        txtLoadingPercent.Text = "";
        btnCancelLoading.Visibility = Visibility.Collapsed;

        try
        {
            // Get quantized palette - this creates fresh colors from the image
            var result = await Task.Run(() => Quantizer.Quantize(_textureData, _textureWidth, _textureHeight, numColors, method));
            
            // Store both original and working copy
            _originalQuantizedPalette = (Vector3[])result.Clone();
            _quantizedPalette = result;
            
            txtLoadingStatus.Text = "Building index map...";
            
            // Build index map (which palette index each pixel maps to)
            _quantizedIndexMap = await Task.Run(() => BuildQuantizedIndexMap(_textureData, _textureWidth, _textureHeight, _quantizedPalette));

            // Generate initial quantized texture from index map
            RegenerateQuantizedTexture();

            // Auto-show quantized preview
            _showingQuantized = true;
            texturePreview.Source = _quantizedTextureBitmap;
            btnToggleQuantized.Content = "Show Original";
            btnToggleQuantized.IsEnabled = true;
            btnExportQuantized.IsEnabled = true;

            // Update palette display (clickable to edit)
            UpdateQuantizedPaletteDisplay();
            btnResetQuantizedColors.IsEnabled = true;

            ShowStatus($"Quantized to {numColors} colors. Edit colors in texture palette, then Project to apply.");
        }
        catch (Exception ex) { MessageBox.Show($"Quantization failed:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error); }
        finally { loadingOverlay.Visibility = Visibility.Collapsed; loadingProgress.IsIndeterminate = false; }
    }

    private static byte[] BuildQuantizedIndexMap(byte[] rgba, int width, int height, Vector3[] palette)
    {
        byte[] indexMap = new byte[width * height];
        for (int i = 0; i < indexMap.Length; i++)
        {
            int pixelOffset = i * 4;
            var color = new Vector3(rgba[pixelOffset] / 255f, rgba[pixelOffset + 1] / 255f, rgba[pixelOffset + 2] / 255f);
            
            int nearest = 0;
            float nearestDist = float.MaxValue;
            for (int j = 0; j < palette.Length; j++)
            {
                float d = Vector3.DistanceSquared(color, palette[j]);
                if (d < nearestDist) { nearestDist = d; nearest = j; }
            }
            indexMap[i] = (byte)nearest;
        }
        return indexMap;
    }

    private async void ProjectTexture_Click(object sender, RoutedEventArgs e)
    {
        if (_mesh == null || _textureData == null) return;

        // Use quantized palette if available, otherwise show error
        if (_quantizedPalette == null || _quantizedPalette.Length == 0) 
        { 
            MessageBox.Show("Please quantize the texture first to create a palette", "Info", MessageBoxButton.OK, MessageBoxImage.Information); 
            return; 
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
        txtMeshInfo.Text = $"Vertices: {_mesh.Vertices.Count:N0}\n" +
                          $"Triangles: {_mesh.Triangles.Count:N0}\n" +
                          $"With UVs: {uvCount:N0}\n" +
                          $"Sub-triangles: {_mesh.TotalSubTriangles:N0}\n" +
                          $"Bounds: {_mesh.BoundingRadius:F1} units";
        txtMeshInfo.Foreground = (System.Windows.Media.Brush)FindResource("TextBrush");
    }

    private void ShowStatus(string msg) => txtStatus.Text = msg;
}
