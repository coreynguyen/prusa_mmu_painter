using System.IO;
using System.Numerics;
using SharpGLTF.Geometry;
using SharpGLTF.Geometry.VertexTypes;
using SharpGLTF.Materials;
using SharpGLTF.Scenes;
using SharpGLTF.Schema2;
using _3MFTool.Models;

namespace _3MFTool.IO;

// Define vertex type with position, normal, UV, and color
using VERTEX = VertexBuilder<VertexPositionNormal, VertexColor1Texture1, VertexEmpty>;

public static class GlbExporter
{
    public static async Task<bool> SaveAsync(string filepath, Models.Mesh mesh, Vector3[] palette, float scale = 1.0f,
        byte[]? embeddedTexture = null, IProgress<(string, float)>? progress = null, CancellationToken ct = default)
        => await Task.Run(() => Save(filepath, mesh, palette, scale, embeddedTexture, progress, ct), ct);

    public static bool Save(string filepath, Models.Mesh mesh, Vector3[] palette, float scale = 1.0f,
        byte[]? embeddedTexture = null, IProgress<(string, float)>? progress = null, CancellationToken ct = default)
    {
        try
        {
            progress?.Report(("Building GLB mesh...", 0));
            
            // Create mesh builder with vertex colors
            var meshBuilder = new MeshBuilder<VertexPositionNormal, VertexColor1Texture1, VertexEmpty>("mesh");
            
            // Create a simple material - vertex colors will be used for coloring
            var material = new MaterialBuilder("material")
                .WithDoubleSide(true)
                .WithMetallicRoughnessShader()
                .WithBaseColor(Vector4.One); // White base, vertex colors provide actual color
            
            var primitive = meshBuilder.UsePrimitive(material);
            
            // Process triangles - split vertices per face to maintain sharp color boundaries
            for (int i = 0; i < mesh.Triangles.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                if (i % 5000 == 0) 
                    progress?.Report(("Writing triangles...", 0.1f + 0.7f * i / mesh.Triangles.Count));
                
                var tri = mesh.Triangles[i];
                
                // Get face color from paint data
                Vector4 faceColor = GetFaceColor(tri, palette);
                
                // Get vertex positions (scaled)
                var p0 = mesh.Vertices[tri.Indices.V0] * scale;
                var p1 = mesh.Vertices[tri.Indices.V1] * scale;
                var p2 = mesh.Vertices[tri.Indices.V2] * scale;
                
                // Get normal
                var normal = tri.Normal;
                if (normal.LengthSquared() < 0.001f)
                    normal = Vector3.UnitY;
                
                // Get UVs - flip V coordinate (our origin is bottom-left, glTF uses top-left)
                Vector2 uv0 = Vector2.Zero, uv1 = Vector2.Zero, uv2 = Vector2.Zero;
                if (tri.UV != null)
                {
                    uv0 = new Vector2(tri.UV.UV0.X, 1f - tri.UV.UV0.Y);
                    uv1 = new Vector2(tri.UV.UV1.X, 1f - tri.UV.UV1.Y);
                    uv2 = new Vector2(tri.UV.UV2.X, 1f - tri.UV.UV2.Y);
                }
                
                // Create vertices with same color for all 3 (sharp edges)
                var v0 = CreateVertex(p0, normal, uv0, faceColor);
                var v1 = CreateVertex(p1, normal, uv1, faceColor);
                var v2 = CreateVertex(p2, normal, uv2, faceColor);
                
                // Add triangle
                primitive.AddTriangle(v0, v1, v2);
            }
            
            progress?.Report(("Building scene...", 0.85f));
            
            // Build scene
            var scene = new SceneBuilder();
            scene.AddRigidMesh(meshBuilder, Matrix4x4.Identity);
            
            // Convert to model and save
            progress?.Report(("Writing file...", 0.9f));
            var model = scene.ToGltf2();
            
            // Determine format from extension
            string ext = Path.GetExtension(filepath).ToLowerInvariant();
            if (ext == ".gltf")
                model.SaveGLTF(filepath);
            else
                model.SaveGLB(filepath);
            
            progress?.Report(("Complete", 1.0f));
            return true;
        }
        catch
        {
            return false;
        }
    }
    
    private static VERTEX CreateVertex(Vector3 pos, Vector3 normal, Vector2 uv, Vector4 color)
    {
        return new VERTEX(
            new VertexPositionNormal(pos, normal),
            new VertexColor1Texture1(color, uv)
        );
    }
    
    /// <summary>
    /// Get the color for a triangle face from its paint data.
    /// For subdivided faces, compute weighted average.
    /// </summary>
    private static Vector4 GetFaceColor(Models.Triangle tri, Vector3[] palette)
    {
        if (tri.PaintData.Count == 0)
            return new Vector4(0.8f, 0.8f, 0.8f, 1f); // Unpainted gray
        
        if (tri.PaintData.Count == 1)
        {
            int idx = tri.PaintData[0].ExtruderId;
            if (idx >= 0 && idx < palette.Length)
            {
                var c = palette[idx];
                return new Vector4(c.X, c.Y, c.Z, 1f);
            }
            return new Vector4(0.8f, 0.8f, 0.8f, 1f);
        }
        
        // Multiple sub-triangles - compute weighted average
        Vector3 colorSum = Vector3.Zero;
        float totalWeight = 0;
        
        foreach (var sub in tri.PaintData)
        {
            float weight = ComputeSubTriangleArea(sub);
            
            int idx = sub.ExtruderId;
            Vector3 color;
            if (idx >= 0 && idx < palette.Length)
                color = palette[idx];
            else
                color = new Vector3(0.8f, 0.8f, 0.8f);
            
            colorSum += color * weight;
            totalWeight += weight;
        }
        
        var result = totalWeight > 0 ? colorSum / totalWeight : new Vector3(0.8f, 0.8f, 0.8f);
        return new Vector4(result.X, result.Y, result.Z, 1f);
    }
    
    private static float ComputeSubTriangleArea(SubTriangle sub)
    {
        if (sub.BaryCorners.Length < 3) return 1f;
        
        var c0 = sub.BaryCorners[0];
        var c1 = sub.BaryCorners[1];
        var c2 = sub.BaryCorners[2];
        
        float ax = c1.U - c0.U;
        float ay = c1.V - c0.V;
        float bx = c2.U - c0.U;
        float by = c2.V - c0.V;
        
        return MathF.Abs(ax * by - ay * bx) * 0.5f;
    }
}
