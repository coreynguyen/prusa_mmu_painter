using System.Numerics;
using System.Runtime.CompilerServices;
using SharpGLTF.Schema2;
using _3MFTool.Models;

namespace _3MFTool.IO;

public static class GlbLoader
{
    public static async Task<Models.Mesh> LoadAsync(string filepath, IProgress<(string, float)>? progress = null, CancellationToken ct = default)
        => await Task.Run(() => Load(filepath, progress, ct), ct);

    public static Models.Mesh Load(string filepath, IProgress<(string, float)>? progress = null, CancellationToken ct = default)
    {
        progress?.Report(("Loading GLB...", 0));
        
        var model = ModelRoot.Load(filepath);
        var mesh = new Models.Mesh();
        
        // Temporary lists for raw triangle data (before welding)
        var rawVertices = new List<Vector3>();
        var rawColors = new List<Vector4?>(); // RGBA vertex colors
        var rawUVs = new List<Vector2>();
        var rawTriangles = new List<(int a, int b, int c)>();
        
        progress?.Report(("Processing scene...", 0.1f));
        
        // Flatten all nodes in the default scene
        if (model.DefaultScene != null)
        {
            foreach (var node in model.DefaultScene.VisualChildren)
            {
                ct.ThrowIfCancellationRequested();
                ProcessNode(node, Matrix4x4.Identity, rawVertices, rawColors, rawUVs, rawTriangles);
            }
        }
        
        if (rawVertices.Count == 0)
        {
            progress?.Report(("Complete", 1.0f));
            return mesh;
        }
        
        bool hasVertexColors = rawColors.Any(c => c.HasValue);
        
        // Smart weld: First extract colors per-triangle, then weld vertices
        progress?.Report(("Welding vertices...", 0.4f));
        
        var uniqueVertices = new Dictionary<Vector3, int>(new Vector3Comparer());
        var triangleColors = new List<Vector4?>(); // Color for each triangle (from first vertex)
        
        foreach (var (a, b, c) in rawTriangles)
        {
            ct.ThrowIfCancellationRequested();
            
            // Extract color from first vertex of triangle BEFORE welding
            Vector4? triColor = null;
            if (hasVertexColors && a < rawColors.Count)
                triColor = rawColors[a];
            triangleColors.Add(triColor);
            
            // Weld vertices by position
            int idx0 = GetOrAddVertex(mesh, uniqueVertices, rawVertices[a]);
            int idx1 = GetOrAddVertex(mesh, uniqueVertices, rawVertices[b]);
            int idx2 = GetOrAddVertex(mesh, uniqueVertices, rawVertices[c]);
            
            // Skip degenerate triangles
            if (idx0 == idx1 || idx1 == idx2 || idx2 == idx0) continue;
            
            var tri = new Triangle(idx0, idx1, idx2);
            
            // Add UVs if present
            if (a < rawUVs.Count && b < rawUVs.Count && c < rawUVs.Count)
            {
                tri.UV = new TriangleUV(rawUVs[a], rawUVs[b], rawUVs[c]);
            }
            
            mesh.Triangles.Add(tri);
        }
        
        // Convert vertex colors to face paint using histogram quantization
        if (hasVertexColors)
        {
            progress?.Report(("Quantizing vertex colors...", 0.7f));
            ConvertVertexColorsToFacePaint(mesh, triangleColors);
        }
        
        // Try to extract embedded texture
        mesh.TexturePath = null; // Will be handled separately via ExtractTexture
        
        progress?.Report(("Computing bounds...", 0.9f));
        mesh.ComputeBounds();
        mesh.ComputeNormals();
        
        progress?.Report(("Complete", 1.0f));
        return mesh;
    }
    
    /// <summary>
    /// Extract embedded base color texture from GLB file.
    /// Returns raw PNG/JPG bytes, or null if no texture found.
    /// </summary>
    public static byte[]? ExtractTexture(string filepath)
    {
        try
        {
            var model = ModelRoot.Load(filepath);
            
            foreach (var glMesh in model.LogicalMeshes)
            {
                foreach (var prim in glMesh.Primitives)
                {
                    var mat = prim.Material;
                    if (mat == null) continue;
                    
                    // Try to find base color texture
                    var channels = mat.Channels;
                    foreach (var channel in channels)
                    {
                        if (channel.Texture != null)
                        {
                            var image = channel.Texture.PrimaryImage;
                            if (image?.Content.IsValid == true)
                            {
                                // Get the raw image bytes
                                return image.Content.Content.ToArray();
                            }
                        }
                    }
                }
            }
        }
        catch { }
        
        return null;
    }
    
    private static void ProcessNode(
        Node node, 
        Matrix4x4 parentTransform,
        List<Vector3> vertices,
        List<Vector4?> colors,
        List<Vector2> uvs,
        List<(int, int, int)> triangles)
    {
        var globalTransform = node.LocalMatrix * parentTransform;
        
        if (node.Mesh != null)
        {
            foreach (var primitive in node.Mesh.Primitives)
            {
                int baseVertex = vertices.Count;
                
                // Get accessors
                var posAccessor = primitive.GetVertexAccessor("POSITION");
                if (posAccessor == null) continue;
                
                var positions = posAccessor.AsVector3Array();
                var colorAccessor = primitive.GetVertexAccessor("COLOR_0");
                var uvAccessor = primitive.GetVertexAccessor("TEXCOORD_0");
                
                // Add vertices with transforms
                for (int i = 0; i < positions.Count; i++)
                {
                    // Transform position
                    var pos = Vector3.Transform(positions[i], globalTransform);
                    vertices.Add(pos);
                    
                    // Vertex color (RGBA)
                    if (colorAccessor != null)
                    {
                        var colorArray = colorAccessor.AsVector4Array();
                        colors.Add(i < colorArray.Count ? colorArray[i] : null);
                    }
                    else
                    {
                        colors.Add(null);
                    }
                    
                    // UV - flip V coordinate (glTF has top-left origin, we use bottom-left)
                    if (uvAccessor != null)
                    {
                        var uvArray = uvAccessor.AsVector2Array();
                        if (i < uvArray.Count)
                        {
                            var uv = uvArray[i];
                            uvs.Add(new Vector2(uv.X, 1f - uv.Y)); // Flip V
                        }
                        else
                        {
                            uvs.Add(Vector2.Zero);
                        }
                    }
                    else
                    {
                        uvs.Add(Vector2.Zero);
                    }
                }
                
                // Get triangle indices
                var indices = primitive.GetTriangleIndices();
                foreach (var (a, b, c) in indices)
                {
                    triangles.Add((baseVertex + a, baseVertex + b, baseVertex + c));
                }
            }
        }
        
        // Process children recursively
        foreach (var child in node.VisualChildren)
        {
            ProcessNode(child, globalTransform, vertices, colors, uvs, triangles);
        }
    }
    
    private static int GetOrAddVertex(Models.Mesh mesh, Dictionary<Vector3, int> lookup, Vector3 pos)
    {
        if (lookup.TryGetValue(pos, out int index))
            return index;
        
        mesh.Vertices.Add(pos);
        int newIndex = mesh.Vertices.Count - 1;
        lookup[pos] = newIndex;
        return newIndex;
    }
    
    /// <summary>
    /// Convert per-triangle vertex colors to face-based painting.
    /// Uses histogram-based quantization to find the best 8-color palette.
    /// </summary>
    private static void ConvertVertexColorsToFacePaint(Models.Mesh mesh, List<Vector4?> triangleColors)
    {
        // Collect all valid colors (as Vector3 RGB)
        var allColors = new List<Vector3>();
        foreach (var c in triangleColors)
        {
            if (c.HasValue)
                allColors.Add(new Vector3(c.Value.X, c.Value.Y, c.Value.Z));
        }
        
        if (allColors.Count == 0)
        {
            mesh.DetectedPalette = Array.Empty<Vector3>();
            return;
        }
        
        // PASS 1: Build color histogram using spatial bucketing
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
        
        // Sort by frequency
        var sortedBuckets = buckets.Values.OrderByDescending(b => b.count).ToList();
        
        // PASS 2: Select up to 8 distinct palette colors
        const float mergeThreshold = 0.015f;
        const float mergeThresholdSq = mergeThreshold * mergeThreshold;
        
        var palette = new List<(Vector3 color, int count)>();
        
        foreach (var bucket in sortedBuckets)
        {
            var bucketColor = bucket.sum / bucket.count;
            
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
                if (palette.Count >= 8) break;
            }
            else if (mergeInto >= 0)
            {
                var existing = palette[mergeInto];
                int totalCount = existing.count + bucket.count;
                var mergedColor = (existing.color * existing.count + bucketColor * bucket.count) / totalCount;
                palette[mergeInto] = (mergedColor, totalCount);
            }
        }
        
        var detectedPalette = palette.Select(p => p.color).ToArray();
        mesh.DetectedPalette = detectedPalette;
        
        if (detectedPalette.Length == 0)
            return;
        
        // PASS 3: Build lookup table
        var bucketToPalette = new Dictionary<int, int>();
        foreach (var kvp in buckets)
        {
            var bucketColor = kvp.Value.sum / kvp.Value.count;
            int nearestIdx = FindNearestPaletteIndex(bucketColor, detectedPalette);
            bucketToPalette[kvp.Key] = nearestIdx;
        }
        
        // PASS 4: Assign face colors
        for (int i = 0; i < mesh.Triangles.Count && i < triangleColors.Count; i++)
        {
            var triColor = triangleColors[i];
            var tri = mesh.Triangles[i];
            
            if (!triColor.HasValue)
            {
                tri.PaintData.Clear();
                tri.PaintData.Add(new SubTriangle { ExtruderId = 0 });
                continue;
            }
            
            var c = new Vector3(triColor.Value.X, triColor.Value.Y, triColor.Value.Z);
            int key = ColorToBucketKey(c, bucketScale, bucketBits);
            int paletteIdx = bucketToPalette.TryGetValue(key, out int idx) ? idx : 0;
            
            tri.PaintData.Clear();
            tri.PaintData.Add(new SubTriangle { ExtruderId = paletteIdx + 1 }); // +1 because 0 = unpainted
        }
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
    /// Comparer for Vector3 with tolerance for floating point errors
    /// </summary>
    private class Vector3Comparer : IEqualityComparer<Vector3>
    {
        private const float Tolerance = 0.0001f;
        
        public bool Equals(Vector3 a, Vector3 b)
        {
            return MathF.Abs(a.X - b.X) < Tolerance &&
                   MathF.Abs(a.Y - b.Y) < Tolerance &&
                   MathF.Abs(a.Z - b.Z) < Tolerance;
        }
        
        public int GetHashCode(Vector3 v)
        {
            // Round to tolerance grid for consistent hashing
            int x = (int)(v.X / Tolerance);
            int y = (int)(v.Y / Tolerance);
            int z = (int)(v.Z / Tolerance);
            return HashCode.Combine(x, y, z);
        }
    }
}
