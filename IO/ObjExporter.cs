using System.Globalization;
using System.IO;
using System.Numerics;
using _3MFTool.Models;

namespace _3MFTool.IO;

public static class ObjExporter
{
    public static async Task<bool> SaveAsync(string filepath, Mesh mesh, Vector3[] palette, float scale = 1.0f, 
        IProgress<(string, float)>? progress = null, CancellationToken ct = default)
        => await Task.Run(() => Save(filepath, mesh, palette, scale, progress, ct), ct);

    public static bool Save(string filepath, Mesh mesh, Vector3[] palette, float scale = 1.0f,
        IProgress<(string, float)>? progress = null, CancellationToken ct = default)
    {
        try
        {
            progress?.Report(("Computing vertex colors...", 0));
            
            // Convert face paint to vertex colors
            // For each vertex, collect all faces that use it and average their colors
            var vertexColors = ComputeVertexColors(mesh, palette);
            
            progress?.Report(("Writing OBJ file...", 0.3f));
            
            using var writer = new StreamWriter(filepath);
            
            // Header
            writer.WriteLine("# OBJ exported by 3MF Tool");
            writer.WriteLine("# Vertex colors in extended format: v x y z r g b");
            writer.WriteLine($"# Vertices: {mesh.Vertices.Count}");
            writer.WriteLine($"# Faces: {mesh.Triangles.Count}");
            writer.WriteLine();
            
            // Write vertices with colors
            // NO coordinate conversion - write Y-up directly for round-trip compatibility
            // User can convert in Blender/other software if needed
            for (int i = 0; i < mesh.Vertices.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                if (i % 10000 == 0) progress?.Report(("Writing vertices...", 0.3f + 0.3f * i / mesh.Vertices.Count));
                
                var v = mesh.Vertices[i] * scale;
                var color = i < vertexColors.Length ? vertexColors[i] : new Vector3(0.8f, 0.8f, 0.8f);
                
                // Write: v x y z r g b (Y-up, same as internal)
                writer.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "v {0:F6} {1:F6} {2:F6} {3:F6} {4:F6} {5:F6}",
                    v.X, v.Y, v.Z, color.X, color.Y, color.Z));
            }
            
            writer.WriteLine();
            
            // Write UVs if present
            bool hasUVs = mesh.Triangles.Any(t => t.UV != null);
            if (hasUVs)
            {
                progress?.Report(("Writing UVs...", 0.6f));
                foreach (var tri in mesh.Triangles)
                {
                    if (tri.UV != null)
                    {
                        writer.WriteLine(string.Format(CultureInfo.InvariantCulture, "vt {0:F6} {1:F6}", tri.UV.UV0.X, tri.UV.UV0.Y));
                        writer.WriteLine(string.Format(CultureInfo.InvariantCulture, "vt {0:F6} {1:F6}", tri.UV.UV1.X, tri.UV.UV1.Y));
                        writer.WriteLine(string.Format(CultureInfo.InvariantCulture, "vt {0:F6} {1:F6}", tri.UV.UV2.X, tri.UV.UV2.Y));
                    }
                }
                writer.WriteLine();
            }
            
            // Write faces
            progress?.Report(("Writing faces...", 0.7f));
            int uvIndex = 1;
            for (int i = 0; i < mesh.Triangles.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                if (i % 5000 == 0) progress?.Report(("Writing faces...", 0.7f + 0.3f * i / mesh.Triangles.Count));
                
                var tri = mesh.Triangles[i];
                // OBJ uses 1-based indices
                int v0 = tri.Indices.V0 + 1;
                int v1 = tri.Indices.V1 + 1;
                int v2 = tri.Indices.V2 + 1;
                
                if (hasUVs && tri.UV != null)
                {
                    writer.WriteLine($"f {v0}/{uvIndex} {v1}/{uvIndex + 1} {v2}/{uvIndex + 2}");
                    uvIndex += 3;
                }
                else
                {
                    writer.WriteLine($"f {v0} {v1} {v2}");
                }
            }
            
            progress?.Report(("Complete", 1.0f));
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Convert face-based paint data to vertex colors.
    /// For each vertex, average the colors of all faces that use it.
    /// </summary>
    private static Vector3[] ComputeVertexColors(Mesh mesh, Vector3[] palette)
    {
        var vertexColors = new Vector3[mesh.Vertices.Count];
        var vertexCounts = new int[mesh.Vertices.Count];
        
        // Default to gray (unpainted)
        for (int i = 0; i < vertexColors.Length; i++)
            vertexColors[i] = new Vector3(0.8f, 0.8f, 0.8f);
        
        foreach (var tri in mesh.Triangles)
        {
            // Get the dominant color for this face
            // For subdivided faces, use the most common color or average
            Vector3 faceColor = GetFaceColor(tri, palette);
            
            // Add to each vertex
            int v0 = tri.Indices.V0;
            int v1 = tri.Indices.V1;
            int v2 = tri.Indices.V2;
            
            if (v0 < vertexColors.Length)
            {
                if (vertexCounts[v0] == 0)
                    vertexColors[v0] = faceColor;
                else
                    vertexColors[v0] = (vertexColors[v0] * vertexCounts[v0] + faceColor) / (vertexCounts[v0] + 1);
                vertexCounts[v0]++;
            }
            
            if (v1 < vertexColors.Length)
            {
                if (vertexCounts[v1] == 0)
                    vertexColors[v1] = faceColor;
                else
                    vertexColors[v1] = (vertexColors[v1] * vertexCounts[v1] + faceColor) / (vertexCounts[v1] + 1);
                vertexCounts[v1]++;
            }
            
            if (v2 < vertexColors.Length)
            {
                if (vertexCounts[v2] == 0)
                    vertexColors[v2] = faceColor;
                else
                    vertexColors[v2] = (vertexColors[v2] * vertexCounts[v2] + faceColor) / (vertexCounts[v2] + 1);
                vertexCounts[v2]++;
            }
        }
        
        return vertexColors;
    }

    /// <summary>
    /// Get the dominant/average color for a triangle face.
    /// For simple faces (1 SubTriangle), use that color directly.
    /// For subdivided faces, compute weighted average based on area.
    /// </summary>
    private static Vector3 GetFaceColor(Triangle tri, Vector3[] palette)
    {
        if (tri.PaintData.Count == 0)
            return new Vector3(0.8f, 0.8f, 0.8f); // Unpainted
        
        if (tri.PaintData.Count == 1)
        {
            // Simple case - single color
            // ExtruderId maps directly to palette index (palette[0] = unpainted gray)
            int idx = tri.PaintData[0].ExtruderId;
            if (idx >= 0 && idx < palette.Length)
                return palette[idx];
            return new Vector3(0.8f, 0.8f, 0.8f);
        }
        
        // Multiple sub-triangles - compute weighted average
        // Weight by approximate area (using barycentric coverage)
        Vector3 colorSum = Vector3.Zero;
        float totalWeight = 0;
        
        foreach (var sub in tri.PaintData)
        {
            // Approximate weight from barycentric area
            float weight = ComputeSubTriangleArea(sub);
            
            // ExtruderId maps directly to palette index
            int idx = sub.ExtruderId;
            Vector3 color;
            if (idx >= 0 && idx < palette.Length)
                color = palette[idx];
            else
                color = new Vector3(0.8f, 0.8f, 0.8f);
            
            colorSum += color * weight;
            totalWeight += weight;
        }
        
        return totalWeight > 0 ? colorSum / totalWeight : new Vector3(0.8f, 0.8f, 0.8f);
    }

    /// <summary>
    /// Compute approximate area of a sub-triangle in barycentric space.
    /// </summary>
    private static float ComputeSubTriangleArea(SubTriangle sub)
    {
        if (sub.BaryCorners.Length < 3) return 1f;
        
        var c0 = sub.BaryCorners[0];
        var c1 = sub.BaryCorners[1];
        var c2 = sub.BaryCorners[2];
        
        // Use 2D cross product for area in barycentric space (using U,V components)
        float ax = c1.U - c0.U;
        float ay = c1.V - c0.V;
        float bx = c2.U - c0.U;
        float by = c2.V - c0.V;
        
        return MathF.Abs(ax * by - ay * bx) * 0.5f;
    }
}
