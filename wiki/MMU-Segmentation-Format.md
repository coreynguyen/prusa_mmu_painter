# MMU Segmentation Format

This document describes the multi-material painting data format used by PrusaSlicer and OrcaSlicer, stored in the `slic3rpe:mmu_segmentation` attribute of triangle elements in 3MF files.

## Overview

When you paint on a model in PrusaSlicer's MMU painting mode, each triangle can be subdivided into smaller regions with different colors. This subdivision is stored as a **quadtree** encoded into a hexadecimal string.

```xml
<triangle v1="0" v2="1" v3="2" slic3rpe:mmu_segmentation="48403"/>
```

## Key Concepts

### Reading Direction

**The string is read backwards** (from end to start). The last character represents the root node of the tree.

### Nibble Structure

Each hex character (nibble) encodes 4 bits:
- **Lower 2 bits**: Split type (number of children)
- **Upper 2 bits**: Color index or special side indicator

```
Nibble: [CC][SS]
         │   └── Split type (0-3)
         └────── Color/Special (0-3)
```

### Split Types

| Split Value | Meaning | Children |
|-------------|---------|----------|
| 0 | Leaf node (no split) | 0 |
| 1 | Split into 2 triangles | 2 |
| 2 | Split into 3 triangles | 3 |
| 3 | Split into 4 triangles | 4 |

### Color Encoding

For leaf nodes (split = 0):

| Color Index | Encoding | Hex |
|-------------|----------|-----|
| 0 | `0b0000` | `0` |
| 1 | `0b0100` | `4` |
| 2 | `0b1000` | `8` |
| 3+ | `0b1100` + extension | `C` + `(color-3)` |

**Examples:**
- Color 0 → `"0"`
- Color 1 → `"4"`
- Color 2 → `"8"`
- Color 3 → `"0C"` (extension nibble `0`, then marker `C`)
- Color 4 → `"1C"`
- Color 5 → `"2C"`

## Tree Structure

### 4-Way Subdivision (Split = 3)

When a triangle is fully subdivided, it creates 4 child triangles:

```
        v0
        /\
       /  \
      /    \
   t01------t20
    /\      /\
   /  \    /  \
  /    \  /    \
v1-----t12------v2

Child 0: Center  (t01, t12, t20)
Child 1: Corner2 (t12, v2, t20)
Child 2: Corner1 (t01, v1, t12)
Child 3: Corner0 (v0, t01, t20)
```

Where:
- `t01` = midpoint of edge v0-v1
- `t12` = midpoint of edge v1-v2
- `t20` = midpoint of edge v2-v0

### 2-Way and 3-Way Splits

For partial splits (split types 1 and 2), the **upper 2 bits indicate which edge is the "special side"**:

| Special Side | Edge |
|--------------|------|
| 0 | v0-v1 |
| 1 | v1-v2 |
| 2 | v2-v0 |

## Decoding Algorithm

```
function decode(string):
    position = length - 1  // Start at end
    return parseNode()

function parseNode():
    nibble = getNextNibble()  // Read backwards
    splitType = nibble & 0b11
    upper = nibble >> 2
    
    if splitType == 0:  // Leaf
        color = upper
        if color == 3:  // Extended color
            color = getNextNibble() + 3
        return LeafNode(color)
    else:
        numChildren = splitType + 1
        specialSide = upper
        children = []
        for i in range(numChildren):
            children.append(parseNode())
        return SplitNode(children, specialSide)
```

## Encoding Algorithm

```
function encode(tree):
    buffer = []
    encodeNode(tree, buffer)
    return reverse(buffer)  // Reverse at end!

function encodeNode(node, buffer):
    if node.isLeaf:
        if node.color < 3:
            buffer.append(node.color << 2)
        else:
            buffer.append(0xC)  // Marker
            buffer.append(node.color - 3)  // Extension
    else:
        // Write parent FIRST (pre-order traversal)
        splitType = numChildren - 1
        code = (specialSide << 2) | splitType
        buffer.append(code)
        
        // Then write children
        for child in node.children:
            encodeNode(child, buffer)
```

## Complete Example

### Encoded String: `"48403"`

**Step-by-step decoding (read backwards):**

1. Read `3` → nibble `0011` → split=3 (4 children), special=0
2. Read `0` → nibble `0000` → split=0 (leaf), color=0
3. Read `4` → nibble `0100` → split=0 (leaf), color=1
4. Read `8` → nibble `1000` → split=0 (leaf), color=2
5. Read `4` → nibble `0100` → split=0 (leaf), color=1

**Result:**
```
Root: 4-way split
├── Child 0 (Center):  Color 0
├── Child 1 (Corner2): Color 1
├── Child 2 (Corner1): Color 2
└── Child 3 (Corner0): Color 1
```

### Visual Representation

```
        v0 (Color 1)
        /\
       /  \
      / C0 \
   t01------t20
    /\ Ctr  /\
   /C1\(0) /C2\
  /    \  /(1)\
v1------t12-----v2
(Color 2)    (Color 1)
```

## File Structure

The segmentation data is stored in the 3MF's model file:

```
model.3mf
└── 3D/
    └── 3dmodel.model
        └── <triangle ... slic3rpe:mmu_segmentation="..."/>
```

### Namespace Declaration

```xml
<model xmlns:slic3rpe="http://schemas.slic3r.org/3mf/2017/06">
```

### Metadata

```xml
<metadata name="slic3rpe:Version3mf">1</metadata>
<metadata name="slic3rpe:MmPaintingVersion">1</metadata>
```

## OrcaSlicer Compatibility

OrcaSlicer uses the same format but with a different attribute name:

```xml
<triangle v1="0" v2="1" v3="2" paint_color="48403"/>
```

## References

- [PrusaSlicer Source - TriangleSelector.cpp](https://github.com/prusa3d/PrusaSlicer/blob/master/src/libslic3r/TriangleSelector.cpp)
- [3MF Specification](https://3mf.io/specification/)

## Implementation Notes

### Child Order Matters

The order of children in the quadtree **must match** the decoder's expectation:

```
Index 0: Center  (t01, t12, t20)
Index 1: Corner2 (t12, v2, t20)
Index 2: Corner1 (t01, v1, t12)
Index 3: Corner0 (v0, t01, t20)
```

Using a different order will cause colors to appear in wrong positions.

### Pre-order Traversal

When encoding, write the **parent node before its children**. The final string reversal ensures the decoder (reading backwards) encounters the parent first.

### Solid Color Optimization

If all sub-triangles have the same color, the tree can be pruned to a single leaf node, significantly reducing string length.
