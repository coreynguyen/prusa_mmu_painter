using System.Numerics;
using _3MFTool.Models;

namespace _3MFTool.Imaging;

public static class TextureProjector
{
    public static void ProjectToMesh(Mesh mesh, byte[] rgba, int width, int height, Vector3[] palette, 
        IProgress<(string, float)>? progress = null)
    {
        if (mesh.Triangles.Count == 0 || palette.Length == 0) return;

        int total = mesh.Triangles.Count;
        for (int i = 0; i < total; i++)
        {
            if (i % 1000 == 0)
                progress?.Report(("Projecting texture...", (float)i / total));

            var tri = mesh.Triangles[i];
            tri.PaintData.Clear();

            if (tri.UV == null)
            {
                tri.PaintData.Add(new SubTriangle());
                continue;
            }

            var centerUV = (tri.UV.UV0 + tri.UV.UV1 + tri.UV.UV2) / 3f;
            var color = SampleTexture(rgba, width, height, centerUV);
            int colorIdx = FindNearestPaletteColor(color, palette);

            tri.PaintData.Add(new SubTriangle { ExtruderId = colorIdx });
        }

        progress?.Report(("Complete", 1.0f));
    }

    public static void ProjectToMeshSubdivided(Mesh mesh, byte[] rgba, int width, int height, Vector3[] palette,
        int subdivisions, IProgress<(string, float)>? progress = null)
    {
        if (mesh.Triangles.Count == 0 || palette.Length == 0) return;

        int total = mesh.Triangles.Count;
        int reportInterval = subdivisions >= 8 ? 1 : (subdivisions >= 6 ? 10 : (subdivisions >= 4 ? 50 : 100));
        
        for (int i = 0; i < total; i++)
        {
            if (i % reportInterval == 0)
                progress?.Report(($"Projecting triangle {i + 1}/{total}...", (float)i / total));

            var tri = mesh.Triangles[i];
            tri.PaintData.Clear();

            if (tri.UV == null)
            {
                tri.PaintData.Add(new SubTriangle());
                continue;
            }

            var uv0 = tri.UV.UV0;
            var uv1 = tri.UV.UV1;
            var uv2 = tri.UV.UV2;

            // Generate all sub-triangles at the target depth
            var subs = new List<SubTriangle>();
            GenerateSubdividedTriangles(
                BarycentricCoord.Corner0, BarycentricCoord.Corner1, BarycentricCoord.Corner2,
                uv0, uv1, uv2, rgba, width, height, palette, 0, subdivisions, subs);

            // Now merge adjacent triangles with same color
            tri.PaintData = MergeAdjacentSameColor(subs);
        }

        progress?.Report(("Complete", 1.0f));
    }

    /// <summary>
    /// Project using a pre-computed index map (from quantization).
    /// This preserves the original pixel-to-color mapping even if palette colors are changed.
    /// </summary>
    public static void ProjectWithIndexMap(Mesh mesh, byte[] indexMap, int width, int height, int paletteOffset,
        int subdivisions, IProgress<(string, float)>? progress = null)
    {
        if (mesh.Triangles.Count == 0) return;

        int total = mesh.Triangles.Count;
        // Report more frequently for high subdivision (each tri takes longer)
        int reportInterval = subdivisions >= 8 ? 1 : (subdivisions >= 6 ? 10 : (subdivisions >= 4 ? 50 : 100));
        
        for (int i = 0; i < total; i++)
        {
            if (i % reportInterval == 0)
                progress?.Report(($"Projecting triangle {i + 1}/{total}...", (float)i / total));

            var tri = mesh.Triangles[i];
            tri.PaintData.Clear();

            if (tri.UV == null)
            {
                tri.PaintData.Add(new SubTriangle());
                continue;
            }

            var uv0 = tri.UV.UV0;
            var uv1 = tri.UV.UV1;
            var uv2 = tri.UV.UV2;

            // Generate all sub-triangles at the target depth using index map
            var subs = new List<SubTriangle>();
            GenerateSubdividedTrianglesFromIndexMap(
                BarycentricCoord.Corner0, BarycentricCoord.Corner1, BarycentricCoord.Corner2,
                uv0, uv1, uv2, indexMap, width, height, paletteOffset, 0, subdivisions, subs);

            // Merge adjacent triangles with same color
            tri.PaintData = MergeAdjacentSameColor(subs);
        }

        progress?.Report(("Complete", 1.0f));
    }

    /// <summary>
    /// Recursively subdivide and generate sub-triangles using index map.
    /// </summary>
    private static void GenerateSubdividedTrianglesFromIndexMap(
        BarycentricCoord b0, BarycentricCoord b1, BarycentricCoord b2,
        Vector2 uv0, Vector2 uv1, Vector2 uv2,
        byte[] indexMap, int width, int height, int paletteOffset,
        int depth, int maxDepth, List<SubTriangle> result)
    {
        if (depth >= maxDepth)
        {
            // Sample index at centroid UV
            var centroidBary = new BarycentricCoord(
                (b0.U + b1.U + b2.U) / 3f,
                (b0.V + b1.V + b2.V) / 3f,
                (b0.W + b1.W + b2.W) / 3f);
            var centroidUV = centroidBary.Interpolate(uv0, uv1, uv2);
            int paletteIdx = SampleIndexMap(indexMap, width, height, centroidUV);
            
            // Add paletteOffset because mesh palette has index 0 = unpainted
            result.Add(new SubTriangle(new[] { b0, b1, b2 }, paletteIdx + paletteOffset, depth));
            return;
        }

        // Subdivide into 4 - MUST MATCH ENCODER/DECODER ORDER: Center, Corner2, Corner1, Corner0
        var m01 = BarycentricCoord.Midpoint(b0, b1);
        var m12 = BarycentricCoord.Midpoint(b1, b2);
        var m20 = BarycentricCoord.Midpoint(b2, b0);

        GenerateSubdividedTrianglesFromIndexMap(m01, m12, m20, uv0, uv1, uv2, indexMap, width, height, paletteOffset, depth + 1, maxDepth, result); // Center
        GenerateSubdividedTrianglesFromIndexMap(m12, b2, m20, uv0, uv1, uv2, indexMap, width, height, paletteOffset, depth + 1, maxDepth, result); // Corner 2
        GenerateSubdividedTrianglesFromIndexMap(m01, b1, m12, uv0, uv1, uv2, indexMap, width, height, paletteOffset, depth + 1, maxDepth, result); // Corner 1
        GenerateSubdividedTrianglesFromIndexMap(b0, m01, m20, uv0, uv1, uv2, indexMap, width, height, paletteOffset, depth + 1, maxDepth, result); // Corner 0
    }

    private static int SampleIndexMap(byte[] indexMap, int width, int height, Vector2 uv)
    {
        // Wrap UVs to 0-1 range properly
        float u = uv.X % 1f;
        float v = uv.Y % 1f;
        if (u < 0) u += 1f;
        if (v < 0) v += 1f;

        // Convert to pixel coordinates (flip V for texture space)
        int x = Math.Clamp((int)(u * (width - 1)), 0, width - 1);
        int y = Math.Clamp((int)((1f - v) * (height - 1)), 0, height - 1);

        int idx = y * width + x;
        if (idx >= indexMap.Length) return 0;

        return indexMap[idx];
    }

    /// <summary>
    /// Recursively subdivide and generate sub-triangles. 
    /// Always subdivides to target depth, then samples color at centroid.
    /// </summary>
    private static void GenerateSubdividedTriangles(
        BarycentricCoord b0, BarycentricCoord b1, BarycentricCoord b2,
        Vector2 uv0, Vector2 uv1, Vector2 uv2,
        byte[] rgba, int width, int height, Vector3[] palette,
        int depth, int maxDepth, List<SubTriangle> result)
    {
        // At target depth, sample and create sub-triangle
        if (depth >= maxDepth)
        {
            // Sample at centroid UV
            var centroidBary = new BarycentricCoord(
                (b0.U + b1.U + b2.U) / 3f,
                (b0.V + b1.V + b2.V) / 3f,
                (b0.W + b1.W + b2.W) / 3f);
            var centroidUV = centroidBary.Interpolate(uv0, uv1, uv2);
            var color = SampleTexture(rgba, width, height, centroidUV);
            int colorIdx = FindNearestPaletteColor(color, palette);

            result.Add(new SubTriangle(new[] { b0, b1, b2 }, colorIdx, depth));
            return;
        }

        // Subdivide into 4 - MUST MATCH ENCODER/DECODER ORDER: Center, Corner2, Corner1, Corner0
        var m01 = BarycentricCoord.Midpoint(b0, b1);
        var m12 = BarycentricCoord.Midpoint(b1, b2);
        var m20 = BarycentricCoord.Midpoint(b2, b0);

        GenerateSubdividedTriangles(m01, m12, m20, uv0, uv1, uv2, rgba, width, height, palette, depth + 1, maxDepth, result); // Center
        GenerateSubdividedTriangles(m12, b2, m20, uv0, uv1, uv2, rgba, width, height, palette, depth + 1, maxDepth, result); // Corner 2
        GenerateSubdividedTriangles(m01, b1, m12, uv0, uv1, uv2, rgba, width, height, palette, depth + 1, maxDepth, result); // Corner 1
        GenerateSubdividedTriangles(b0, m01, m20, uv0, uv1, uv2, rgba, width, height, palette, depth + 1, maxDepth, result); // Corner 0
    }

    private static Vector3 SampleTexture(byte[] rgba, int width, int height, Vector2 uv)
    {
        // Wrap UVs to 0-1 range properly
        float u = uv.X % 1f;
        float v = uv.Y % 1f;
        if (u < 0) u += 1f;
        if (v < 0) v += 1f;

        // Convert to pixel coordinates (flip V for texture space)
        int x = Math.Clamp((int)(u * (width - 1)), 0, width - 1);
        int y = Math.Clamp((int)((1f - v) * (height - 1)), 0, height - 1);

        int idx = (y * width + x) * 4;
        if (idx + 2 >= rgba.Length) return Vector3.One * 0.5f;

        return new Vector3(rgba[idx] / 255f, rgba[idx + 1] / 255f, rgba[idx + 2] / 255f);
    }

    private static int FindNearestPaletteColor(Vector3 color, Vector3[] palette)
    {
        int nearest = 0;
        float nearestDist = float.MaxValue;
        for (int i = 0; i < palette.Length; i++)
        {
            float d = Vector3.DistanceSquared(color, palette[i]);
            if (d < nearestDist) { nearestDist = d; nearest = i; }
        }
        return nearest;
    }

    /// <summary>
    /// Merge sub-triangles that all have the same color into a single triangle.
    /// This is a simple optimization - if all are same color, collapse to one.
    /// </summary>
    private static List<SubTriangle> MergeAdjacentSameColor(List<SubTriangle> subs)
    {
        if (subs.Count <= 1) return subs;
        
        // If all same color, collapse to single triangle
        int firstColor = subs[0].ExtruderId;
        bool allSame = true;
        foreach (var sub in subs)
        {
            if (sub.ExtruderId != firstColor)
            {
                allSame = false;
                break;
            }
        }
        
        if (allSame)
        {
            return new List<SubTriangle> { new SubTriangle { ExtruderId = firstColor } };
        }
        
        return subs;
    }
}
