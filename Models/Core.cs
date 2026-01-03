using System.Numerics;
using System.Runtime.CompilerServices;

namespace _3MFTool.Models;

public readonly struct BarycentricCoord
{
    public readonly float U, V, W;
    public BarycentricCoord(float u, float v, float w) { U = u; V = v; W = w; }
    public static BarycentricCoord Corner0 => new(1, 0, 0);
    public static BarycentricCoord Corner1 => new(0, 1, 0);
    public static BarycentricCoord Corner2 => new(0, 0, 1);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Vector3 Interpolate(Vector3 v0, Vector3 v1, Vector3 v2) => U * v0 + V * v1 + W * v2;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Vector2 Interpolate(Vector2 v0, Vector2 v1, Vector2 v2) => U * v0 + V * v1 + W * v2;

    public static BarycentricCoord Midpoint(BarycentricCoord a, BarycentricCoord b) =>
        new((a.U + b.U) * 0.5f, (a.V + b.V) * 0.5f, (a.W + b.W) * 0.5f);

    public static BarycentricCoord Centroid => new(1f / 3f, 1f / 3f, 1f / 3f);
}

public class SubTriangle
{
    public BarycentricCoord[] BaryCorners { get; set; }
    public int ExtruderId { get; set; }
    public int Depth { get; set; }
    public bool Masked { get; set; }

    public SubTriangle()
    {
        BaryCorners = new[] { BarycentricCoord.Corner0, BarycentricCoord.Corner1, BarycentricCoord.Corner2 };
    }

    public SubTriangle(BarycentricCoord[] corners, int extruderId, int depth, bool masked = false)
    {
        BaryCorners = corners;
        ExtruderId = extruderId;
        Depth = depth;
        Masked = masked;
    }

    public SubTriangle Clone() => new((BarycentricCoord[])BaryCorners.Clone(), ExtruderId, Depth, Masked);
}

public readonly struct TriangleIndices
{
    public readonly int V0, V1, V2;
    public TriangleIndices(int v0, int v1, int v2) { V0 = v0; V1 = v1; V2 = v2; }
}

public class TriangleUV
{
    public Vector2 UV0 { get; set; }
    public Vector2 UV1 { get; set; }
    public Vector2 UV2 { get; set; }
    public TriangleUV() { }
    public TriangleUV(Vector2 uv0, Vector2 uv1, Vector2 uv2) { UV0 = uv0; UV1 = uv1; UV2 = uv2; }
}

public class Triangle
{
    public TriangleIndices Indices { get; set; }
    public Vector3 Normal { get; set; } = Vector3.UnitY;
    public TriangleUV? UV { get; set; }
    public List<SubTriangle> PaintData { get; set; } = new() { new SubTriangle() };
    public bool Masked { get; set; }
    public string? MmuSegmentation { get; set; }

    public Triangle() { }
    public Triangle(int v0, int v1, int v2) { Indices = new TriangleIndices(v0, v1, v2); }
    public int SubTriangleCount => PaintData.Count;
}

public class Mesh
{
    public List<Vector3> Vertices { get; set; } = new();
    public List<Triangle> Triangles { get; set; } = new();
    public Vector3 BoundsMin { get; private set; }
    public Vector3 BoundsMax { get; private set; }
    public Vector3 Center { get; private set; }
    public float BoundingRadius { get; private set; }
    public string? TexturePath { get; set; }
    public Vector3[]? DetectedPalette { get; set; } // Palette detected from vertex colors on import
    public int TotalSubTriangles => Triangles.Sum(t => t.SubTriangleCount);

    public void ComputeBounds()
    {
        if (Vertices.Count == 0) { BoundsMin = BoundsMax = Center = Vector3.Zero; BoundingRadius = 1; return; }
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var v in Vertices) { min = Vector3.Min(min, v); max = Vector3.Max(max, v); }
        BoundsMin = min; BoundsMax = max; Center = (min + max) * 0.5f;
        BoundingRadius = Vector3.Distance(Center, max);
    }

    public void ComputeNormals()
    {
        foreach (var tri in Triangles)
        {
            if (tri.Indices.V0 >= Vertices.Count || tri.Indices.V1 >= Vertices.Count || tri.Indices.V2 >= Vertices.Count) continue;
            var v0 = Vertices[tri.Indices.V0]; var v1 = Vertices[tri.Indices.V1]; var v2 = Vertices[tri.Indices.V2];
            var normal = Vector3.Cross(v1 - v0, v2 - v0);
            tri.Normal = normal.LengthSquared() > 0 ? Vector3.Normalize(normal) : Vector3.UnitY;
        }
    }

    public void ClearPaint()
    {
        foreach (var tri in Triangles) { tri.PaintData.Clear(); tri.PaintData.Add(new SubTriangle()); }
    }

    /// <summary>
    /// Initialize paint data for all triangles - ensures every triangle has exactly one paintable SubTriangle.
    /// Call this before painting on a fresh mesh or to reset complex subdivision.
    /// </summary>
    public void InitializePaintData()
    {
        foreach (var tri in Triangles)
        {
            if (tri.PaintData.Count == 0)
                tri.PaintData.Add(new SubTriangle());
        }
    }

    public void ClearMasks()
    {
        foreach (var tri in Triangles) { tri.Masked = false; foreach (var sub in tri.PaintData) sub.Masked = false; }
    }
}

public enum PaintTool { Paint, Erase, Mask, Unmask, Eyedropper }

public static class DefaultPalette
{
    // 0-based palette: index 0 = first color (red), index 1 = second (green), etc.
    // No special "unpainted" slot - Color 0 in MMU format = first extruder
    public static readonly Vector3[] Colors = new Vector3[]
    {
        new(1.0f, 0.3f, 0.3f), new(0.3f, 1.0f, 0.3f), new(0.3f, 0.3f, 1.0f), new(1.0f, 1.0f, 0.3f),
        new(1.0f, 0.3f, 1.0f), new(0.3f, 1.0f, 1.0f), new(1.0f, 0.6f, 0.2f), new(0.6f, 0.3f, 0.6f),
    };
    public static Vector3 GetColor(int idx) => idx >= 0 && idx < Colors.Length ? Colors[idx] : Colors[0];
}
