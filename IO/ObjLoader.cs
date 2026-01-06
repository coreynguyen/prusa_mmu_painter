using System.Globalization;
using System.IO;
using System.Numerics;
using System.Runtime.CompilerServices;
using _3MFTool.Models;

namespace _3MFTool.IO;

public static class ObjLoader
{
    public static async Task<Mesh> LoadAsync(string filepath, IProgress<(string, float)>? progress = null, CancellationToken ct = default)
        => await Task.Run(() => Load(filepath, progress, ct), ct);

    public static Mesh Load(string filepath, IProgress<(string, float)>? progress = null, CancellationToken ct = default)
    {
        var mesh = new Mesh();
        var vertices = new List<Vector3>();
        var vertexColors = new List<Vector3?>(); // Nullable - not all vertices have colors
        var uvs = new List<Vector2>();
        var faces = new List<(int[] vIdx, int[] uvIdx)>();
        string baseDir = Path.GetDirectoryName(filepath) ?? "";
        string? mtlFile = null;
        
        // ZBrush MRGB polypaint data
        var mrgbColors = new List<Vector3>();
        bool hasMrgbData = false;

        progress?.Report(("Reading file...", 0));
        var lines = File.ReadAllLines(filepath);
        int total = lines.Length;
        bool hasVertexColors = false;

        for (int i = 0; i < total; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (i % 50000 == 0) progress?.Report(("Parsing...", 0.1f + 0.3f * i / total));

            var line = lines[i].Trim();
            if (string.IsNullOrEmpty(line)) continue;
            
            // Check for ZBrush MRGB polypaint data (comes as a comment line)
            if (line.StartsWith("#MRGB "))
            {
                ParseMrgbLine(line.Substring(6), mrgbColors);
                hasMrgbData = true;
                continue;
            }
            
            if (line[0] == '#') continue; // Skip other comments

            if (line.StartsWith("mtllib "))
            {
                mtlFile = line.Substring(7).Trim();
            }
            else if (line.StartsWith("v "))
            {
                var p = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (p.Length >= 4)
                {
                    vertices.Add(new Vector3(
                        float.Parse(p[1], CultureInfo.InvariantCulture),
                        float.Parse(p[2], CultureInfo.InvariantCulture),
                        float.Parse(p[3], CultureInfo.InvariantCulture)));
                    
                    // Check for vertex colors (v x y z r g b)
                    if (p.Length >= 7)
                    {
                        hasVertexColors = true;
                        vertexColors.Add(new Vector3(
                            float.Parse(p[4], CultureInfo.InvariantCulture),
                            float.Parse(p[5], CultureInfo.InvariantCulture),
                            float.Parse(p[6], CultureInfo.InvariantCulture)));
                    }
                    else
                    {
                        vertexColors.Add(null);
                    }
                }
            }
            else if (line.StartsWith("vt "))
            {
                var p = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (p.Length >= 3)
                    uvs.Add(new Vector2(
                        float.Parse(p[1], CultureInfo.InvariantCulture),
                        float.Parse(p[2], CultureInfo.InvariantCulture)));
            }
            else if (line.StartsWith("f "))
            {
                var p = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var vIdxs = new List<int>();
                var uvIdxs = new List<int>();

                for (int j = 1; j < p.Length; j++)
                {
                    var idx = p[j].Split('/');
                    vIdxs.Add(int.Parse(idx[0]) - 1);
                    if (idx.Length > 1 && !string.IsNullOrEmpty(idx[1]))
                        uvIdxs.Add(int.Parse(idx[1]) - 1);
                }

                // Triangulate
                for (int j = 1; j < vIdxs.Count - 1; j++)
                {
                    var triV = new[] { vIdxs[0], vIdxs[j], vIdxs[j + 1] };
                    var triUV = uvIdxs.Count >= vIdxs.Count
                        ? new[] { uvIdxs[0], uvIdxs[j], uvIdxs[j + 1] }
                        : Array.Empty<int>();
                    faces.Add((triV, triUV));
                }
            }
        }

        // Apply ZBrush MRGB colors if present and no inline vertex colors
        if (hasMrgbData && !hasVertexColors && mrgbColors.Count > 0)
        {
            progress?.Report(("Applying ZBrush polypaint colors...", 0.42f));
            hasVertexColors = true;
            
            // Apply MRGB colors to vertex color list
            for (int i = 0; i < vertices.Count; i++)
            {
                if (i < mrgbColors.Count)
                    vertexColors[i] = mrgbColors[i];
            }
        }

        // Try to find texture from MTL file
        if (!string.IsNullOrEmpty(mtlFile))
        {
            progress?.Report(("Parsing MTL...", 0.45f));
            string? texturePath = ParseMtlForTexture(Path.Combine(baseDir, mtlFile), baseDir);
            if (!string.IsNullOrEmpty(texturePath))
                mesh.TexturePath = texturePath;
        }

        // Also check for common texture filenames if no MTL
        if (string.IsNullOrEmpty(mesh.TexturePath))
        {
            string baseName = Path.GetFileNameWithoutExtension(filepath);
            string[] extensions = { ".png", ".jpg", ".jpeg", ".bmp", ".tga" };
            string[] prefixes = { "", "_diffuse", "_color", "_albedo", "_d", "_tex" };
            
            foreach (var prefix in prefixes)
            {
                foreach (var ext in extensions)
                {
                    string candidate = Path.Combine(baseDir, baseName + prefix + ext);
                    if (File.Exists(candidate))
                    {
                        mesh.TexturePath = candidate;
                        break;
                    }
                }
                if (!string.IsNullOrEmpty(mesh.TexturePath)) break;
            }
        }

        // Z-up detection DISABLED for round-trip compatibility
        // Our OBJ exporter writes Y-up, so re-importing should not convert
        // If importing from Blender/etc (Z-up), user can rotate manually or use Blender's import options
        progress?.Report(("Processing coordinates...", 0.5f));
        // bool isZUp = DetectZUp(vertices);
        // if (isZUp)
        //     for (int i = 0; i < vertices.Count; i++)
        //     {
        //         var v = vertices[i];
        //         vertices[i] = new Vector3(v.X, v.Z, -v.Y);
        //     }

        // VERTEX WELDING: Merge vertices that are very close together
        progress?.Report(("Welding vertices...", 0.55f));
        var (weldedVerts, indexRemap) = WeldVertices(vertices, vertexColors, 0.0001f);
        
        mesh.Vertices = weldedVerts.verts;
        var weldedColors = weldedVerts.colors;

        progress?.Report(("Building triangles...", 0.65f));
        for (int i = 0; i < faces.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (i % 20000 == 0) progress?.Report(("Building triangles...", 0.65f + 0.2f * i / faces.Count));

            var (vIdx, uvIdx) = faces[i];
            
            // Remap indices through weld table
            int v0 = indexRemap[vIdx[0]];
            int v1 = indexRemap[vIdx[1]];
            int v2 = indexRemap[vIdx[2]];
            
            // Skip degenerate triangles
            if (v0 == v1 || v1 == v2 || v2 == v0) continue;
            
            var tri = new Triangle(v0, v1, v2);
            if (uvIdx.Length == 3 && uvs.Count > 0)
                tri.UV = new TriangleUV(
                    uvIdx[0] < uvs.Count ? uvs[uvIdx[0]] : Vector2.Zero,
                    uvIdx[1] < uvs.Count ? uvs[uvIdx[1]] : Vector2.Zero,
                    uvIdx[2] < uvs.Count ? uvs[uvIdx[2]] : Vector2.Zero);
            mesh.Triangles.Add(tri);
        }

        // Convert vertex colors to face painting if present
        if (hasVertexColors && weldedColors.Count > 0)
        {
            progress?.Report(("Converting vertex colors to face paint...", 0.9f));
            ConvertVertexColorsToFacePaint(mesh, weldedColors);
        }

        progress?.Report(("Computing bounds...", 0.95f));
        mesh.ComputeBounds();
        mesh.ComputeNormals();
        progress?.Report(("Complete", 1.0f));
        return mesh;
    }

    /// <summary>
    /// Parse a ZBrush MRGB line containing vertex colors in MMRRGGBB hex format.
    /// Up to 64 colors per line, 8 hex chars each.
    /// MM = mask (ignored), RR/GG/BB = vertex color (0-255)
    /// </summary>
    private static void ParseMrgbLine(string hexData, List<Vector3> colors)
    {
        // Remove any whitespace
        hexData = hexData.Replace(" ", "").Replace("\r", "").Replace("\n", "");
        
        // Each color is 8 hex chars: MMRRGGBB
        for (int i = 0; i + 8 <= hexData.Length; i += 8)
        {
            try
            {
                // Skip mask bytes (first 2 chars)
                // string mask = hexData.Substring(i, 2);
                string rHex = hexData.Substring(i + 2, 2);
                string gHex = hexData.Substring(i + 4, 2);
                string bHex = hexData.Substring(i + 6, 2);
                
                int r = Convert.ToInt32(rHex, 16);
                int g = Convert.ToInt32(gHex, 16);
                int b = Convert.ToInt32(bHex, 16);
                
                colors.Add(new Vector3(r / 255f, g / 255f, b / 255f));
            }
            catch
            {
                // Skip malformed entries
                colors.Add(new Vector3(0.5f, 0.5f, 0.5f));
            }
        }
    }

    /// <summary>
    /// Convert vertex colors to face-based painting.
    /// Uses two-pass quantization for Blender round-trip compatibility:
    /// 1. Build histogram, find most frequent color clusters
    /// 2. Map all colors to nearest cluster centroid
    /// </summary>
    private static void ConvertVertexColorsToFacePaint(Mesh mesh, List<Vector3?> vertexColors)
    {
        // Collect all valid colors
        var allColors = new List<Vector3>();
        foreach (var c in vertexColors)
        {
            if (c.HasValue)
                allColors.Add(c.Value);
        }
        
        if (allColors.Count == 0)
        {
            mesh.DetectedPalette = Array.Empty<Vector3>();
            return;
        }
        
        // PASS 1: Build color histogram using spatial bucketing
        // Use 5 bits per channel = 32 levels = 32768 buckets (fast lookup)
        const int bucketBits = 5;
        const int bucketLevels = 1 << bucketBits;
        const float bucketScale = bucketLevels - 1;
        
        var buckets = new Dictionary<int, (Vector3 sum, int count)>();
        
        foreach (var c in allColors)
        {
            int r = (int)(c.X * bucketScale);
            int g = (int)(c.Y * bucketScale);
            int b = (int)(c.Z * bucketScale);
            int key = (r << (bucketBits * 2)) | (g << bucketBits) | b;
            
            if (buckets.TryGetValue(key, out var existing))
                buckets[key] = (existing.sum + c, existing.count + 1);
            else
                buckets[key] = (c, 1);
        }
        
        // Sort buckets by frequency (most common first)
        var sortedBuckets = buckets.Values
            .OrderByDescending(b => b.count)
            .ToList();
        
        // PASS 2: Select up to 8 distinct palette colors from most frequent buckets
        // Merge buckets that are too similar (color distance < threshold)
        const float mergeThreshold = 0.015f; // ~4 in 0-255 range
        const float mergeThresholdSq = mergeThreshold * mergeThreshold;
        
        var palette = new List<(Vector3 color, int count)>();
        
        foreach (var bucket in sortedBuckets)
        {
            var bucketColor = bucket.sum / bucket.count;
            
            // Check if this color is distinct from existing palette colors
            bool isDistinct = true;
            int mergeInto = -1;
            
            for (int i = 0; i < palette.Count; i++)
            {
                if (Vector3.DistanceSquared(bucketColor, palette[i].color) < mergeThresholdSq)
                {
                    isDistinct = false;
                    mergeInto = i;
                    break;
                }
            }
            
            if (isDistinct)
            {
                palette.Add((bucketColor, bucket.count));
                if (palette.Count >= 8) break; // Max 8 colors for MMU
            }
            else if (mergeInto >= 0)
            {
                // Merge into existing palette entry (weighted average)
                var existing = palette[mergeInto];
                int totalCount = existing.count + bucket.count;
                var mergedColor = (existing.color * existing.count + bucketColor * bucket.count) / totalCount;
                palette[mergeInto] = (mergedColor, totalCount);
            }
        }
        
        // If we don't have enough colors, that's fine - use what we have
        var detectedPalette = palette.Select(p => p.color).ToArray();
        mesh.DetectedPalette = detectedPalette;
        
        if (detectedPalette.Length == 0)
            return;
        
        // PASS 3: Build lookup table for fast vertex color → palette mapping
        // Pre-compute nearest palette color for each bucket
        var bucketToPalette = new Dictionary<int, int>();
        
        foreach (var kvp in buckets)
        {
            var bucketColor = kvp.Value.sum / kvp.Value.count;
            int nearestIdx = FindNearestPaletteIndex(bucketColor, detectedPalette);
            bucketToPalette[kvp.Key] = nearestIdx;
        }
        
        // PASS 4: Assign face colors using precomputed lookup
        // For each triangle, use the most common palette color among its vertices
        Parallel.ForEach(mesh.Triangles, tri =>
        {
            var c0 = tri.Indices.V0 < vertexColors.Count ? vertexColors[tri.Indices.V0] : null;
            var c1 = tri.Indices.V1 < vertexColors.Count ? vertexColors[tri.Indices.V1] : null;
            var c2 = tri.Indices.V2 < vertexColors.Count ? vertexColors[tri.Indices.V2] : null;
            
            // Get palette indices for each vertex
            var indices = new int[3];
            int validCount = 0;
            
            if (c0.HasValue)
            {
                int key = ColorToBucketKey(c0.Value, bucketScale, bucketBits);
                indices[validCount++] = bucketToPalette.TryGetValue(key, out int idx) ? idx : 0;
            }
            if (c1.HasValue)
            {
                int key = ColorToBucketKey(c1.Value, bucketScale, bucketBits);
                indices[validCount++] = bucketToPalette.TryGetValue(key, out int idx) ? idx : 0;
            }
            if (c2.HasValue)
            {
                int key = ColorToBucketKey(c2.Value, bucketScale, bucketBits);
                indices[validCount++] = bucketToPalette.TryGetValue(key, out int idx) ? idx : 0;
            }
            
            if (validCount == 0)
            {
                tri.PaintData.Clear();
                tri.PaintData.Add(new SubTriangle { ExtruderId = 0 });
                return;
            }
            
            // Use most common index, or first if all different
            int finalIdx;
            if (validCount == 3 && indices[0] == indices[1] && indices[1] == indices[2])
                finalIdx = indices[0];
            else if (validCount >= 2 && indices[0] == indices[1])
                finalIdx = indices[0];
            else if (validCount == 3 && indices[1] == indices[2])
                finalIdx = indices[1];
            else if (validCount == 3 && indices[0] == indices[2])
                finalIdx = indices[0];
            else
                finalIdx = indices[0]; // Default to first vertex's color
            
            // ExtruderId: 0 = unpainted, 1+ = palette colors
            tri.PaintData.Clear();
            tri.PaintData.Add(new SubTriangle { ExtruderId = finalIdx + 1 });
        });
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ColorToBucketKey(Vector3 c, float bucketScale, int bucketBits)
    {
        int r = (int)(c.X * bucketScale);
        int g = (int)(c.Y * bucketScale);
        int b = (int)(c.Z * bucketScale);
        return (r << (bucketBits * 2)) | (g << bucketBits) | b;
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int FindNearestPaletteIndex(Vector3 color, Vector3[] palette)
    {
        int nearest = 0;
        float minDist = float.MaxValue;
        
        for (int i = 0; i < palette.Length; i++)
        {
            float dist = Vector3.DistanceSquared(color, palette[i]);
            if (dist < minDist)
            {
                minDist = dist;
                nearest = i;
            }
        }
        
        return nearest;
    }

    /// <summary>
    /// Weld vertices that are within tolerance of each other.
    /// Also merges vertex colors by averaging.
    /// </summary>
    private static ((List<Vector3> verts, List<Vector3?> colors), int[] remap) WeldVertices(
        List<Vector3> vertices, List<Vector3?> colors, float tolerance)
    {
        if (vertices.Count == 0)
            return ((new List<Vector3>(), new List<Vector3?>()), Array.Empty<int>());
        
        float cellSize = tolerance * 2f;
        var grid = new Dictionary<(int, int, int), List<int>>();
        var weldedVerts = new List<Vector3>();
        var weldedColors = new List<Vector3?>();
        var colorAccum = new List<(Vector3 sum, int count)>(); // For averaging colors
        int[] remap = new int[vertices.Count];
        
        for (int i = 0; i < vertices.Count; i++)
        {
            var v = vertices[i];
            int cx = (int)MathF.Floor(v.X / cellSize);
            int cy = (int)MathF.Floor(v.Y / cellSize);
            int cz = (int)MathF.Floor(v.Z / cellSize);
            
            int weldTo = -1;
            float tolSq = tolerance * tolerance;
            
            for (int dx = -1; dx <= 1 && weldTo < 0; dx++)
            for (int dy = -1; dy <= 1 && weldTo < 0; dy++)
            for (int dz = -1; dz <= 1 && weldTo < 0; dz++)
            {
                var key = (cx + dx, cy + dy, cz + dz);
                if (grid.TryGetValue(key, out var cell))
                {
                    foreach (int existingIdx in cell)
                    {
                        if (Vector3.DistanceSquared(v, weldedVerts[existingIdx]) <= tolSq)
                        {
                            weldTo = existingIdx;
                            break;
                        }
                    }
                }
            }
            
            if (weldTo >= 0)
            {
                remap[i] = weldTo;
                // Accumulate color for averaging
                if (i < colors.Count && colors[i].HasValue)
                {
                    var (sum, count) = colorAccum[weldTo];
                    colorAccum[weldTo] = (sum + colors[i]!.Value, count + 1);
                }
            }
            else
            {
                int newIdx = weldedVerts.Count;
                weldedVerts.Add(v);
                remap[i] = newIdx;
                
                // Initialize color accumulator
                if (i < colors.Count && colors[i].HasValue)
                {
                    weldedColors.Add(colors[i]);
                    colorAccum.Add((colors[i]!.Value, 1));
                }
                else
                {
                    weldedColors.Add(null);
                    colorAccum.Add((Vector3.Zero, 0));
                }
                
                var key = (cx, cy, cz);
                if (!grid.TryGetValue(key, out var cell))
                {
                    cell = new List<int>();
                    grid[key] = cell;
                }
                cell.Add(newIdx);
            }
        }
        
        // Finalize averaged colors
        for (int i = 0; i < weldedColors.Count; i++)
        {
            var (sum, count) = colorAccum[i];
            if (count > 0)
                weldedColors[i] = sum / count;
        }
        
        return ((weldedVerts, weldedColors), remap);
    }

    private static string? ParseMtlForTexture(string mtlPath, string baseDir)
    {
        if (!File.Exists(mtlPath)) return null;

        try
        {
            var lines = File.ReadAllLines(mtlPath);
            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("map_Kd ") || trimmed.StartsWith("map_Ka ") || 
                    trimmed.StartsWith("map_d ") || trimmed.StartsWith("map_Bump "))
                {
                    string texName = trimmed.Substring(trimmed.IndexOf(' ') + 1).Trim();
                    
                    if (texName.StartsWith("-"))
                    {
                        var parts = texName.Split(' ');
                        texName = parts[^1];
                    }
                    
                    if (File.Exists(texName)) return texName;
                    
                    string candidate = Path.Combine(baseDir, texName);
                    if (File.Exists(candidate)) return candidate;
                    
                    candidate = Path.Combine(baseDir, Path.GetFileName(texName));
                    if (File.Exists(candidate)) return candidate;
                }
            }
        }
        catch { }

        return null;
    }

    private static bool DetectZUp(List<Vector3> verts)
    {
        if (verts.Count == 0) return false;
        float yMin = float.MaxValue, yMax = float.MinValue, zMin = float.MaxValue, zMax = float.MinValue;
        foreach (var v in verts)
        {
            yMin = MathF.Min(yMin, v.Y); yMax = MathF.Max(yMax, v.Y);
            zMin = MathF.Min(zMin, v.Z); zMax = MathF.Max(zMax, v.Z);
        }
        return (zMax - zMin) > (yMax - yMin) * 1.5f && zMin >= -0.1f;
    }
}
