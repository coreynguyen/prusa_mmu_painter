# 3MF MMU Paint Tool Wiki

Welcome to the 3MF MMU Paint Tool documentation!

## Getting Started

1. [Installation](#installation)
2. [Quick Start Guide](#quick-start)
3. [Controls Reference](#controls)

## Technical Documentation

- [MMU Segmentation Format](MMU-Segmentation-Format.md) - Detailed explanation of how multi-material painting data is encoded in 3MF files

## Installation

### Prerequisites
- Windows 10 or Windows 11
- [.NET 8.0 Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)
- OpenGL 3.3+ compatible graphics card

### Download
Download the latest release from the [Releases](../../releases) page.

### Building from Source
```bash
git clone https://github.com/yourusername/3MFTool.git
cd 3MFTool
dotnet build -c Release
```

## Quick Start

### 1. Load a Model
- File → Open (Ctrl+O)
- Supports `.obj` and `.3mf` files
- Model must have UV coordinates for texture projection

### 2. Load a Texture
- Click "Load Texture" in the right panel
- Supports PNG, JPG, BMP formats

### 3. Quantize the Texture
- Set the number of colors (2-8, matching your printer's extruders)
- Choose a quantization method:
  - **Uniform** - Fast, evenly distributed colors
  - **K-Means** - Best color matching, slower
  - **Median Cut** - Good balance of speed and quality
  - **Octree** - Fast with good results
- Click "Quantize Texture"

### 4. Project to Mesh
- Set subdivision level (4-6 recommended for most models)
- Higher values = more detail but longer processing
- Click "Project to Mesh"

### 5. Paint (Optional)
- Left-click to paint with selected color
- Use number keys 1-8 to switch colors
- Use E for eraser, I for eyedropper

### 6. Save
- File → Save (Ctrl+S)
- Exports as .3mf with MMU segmentation data
- Open directly in PrusaSlicer or OrcaSlicer

## Controls

| Input | Action |
|-------|--------|
| **Mouse** | |
| Left Click | Paint |
| Right Drag | Orbit camera |
| Middle Drag | Pan camera |
| Scroll | Zoom |
| Alt + Scroll | Resize brush |
| **Keyboard** | |
| 1-8 | Select color |
| Tab | Next color |
| Shift+Tab | Previous color |
| B | Paint tool |
| E | Erase tool |
| I | Eyedropper |
| M (hold) | Mask mode |
| [ | Smaller brush |
| ] | Larger brush |
| Ctrl+Z | Undo |
| Ctrl+Y | Redo |
| F | Frame model |
| R | Reset view |

## Tips

### Subdivision Levels
- **Level 4**: 256 sub-triangles per face - good for simple textures
- **Level 6**: 4,096 sub-triangles per face - detailed textures
- **Level 8**: 65,536 sub-triangles per face - very high detail
- **Level 10**: 1M+ sub-triangles - extreme detail, slow processing

### Color Quantization
- Start with K-Means for best color matching
- Use fewer colors if your printer has limited extruders
- Edit quantized colors by double-clicking in the palette

### Performance
- Large textures (4K+) may be slow to quantize
- High subdivision levels (9-10) require patience
- Painting performance is optimized for real-time interaction

## Troubleshooting

### Model appears unpainted after projection
- Check that your model has UV coordinates
- Enable "Show UVs" checkbox to verify UV mapping
- Try a lower subdivision level first

### Colors don't match expected
- Ensure you've quantized before projecting
- Edit palette colors by double-clicking
- Use K-Means quantization for better color matching

### Saved file not recognized by slicer
- Verify the .3mf opens in PrusaSlicer/OrcaSlicer
- Check that MMU painting mode is enabled in slicer
- Ensure the model has at least some painted regions
