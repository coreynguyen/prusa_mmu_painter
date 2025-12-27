using System.Globalization;
using System.IO;
using System.Numerics;
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
        var uvs = new List<Vector2>();
        var faces = new List<(int[] vIdx, int[] uvIdx)>();
        string baseDir = Path.GetDirectoryName(filepath) ?? "";
        string? mtlFile = null;

        progress?.Report(("Reading file...", 0));
        var lines = File.ReadAllLines(filepath);
        int total = lines.Length;

        for (int i = 0; i < total; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (i % 50000 == 0) progress?.Report(("Parsing...", 0.1f + 0.4f * i / total));

            var line = lines[i].Trim();
            if (string.IsNullOrEmpty(line) || line[0] == '#') continue;

            if (line.StartsWith("mtllib "))
            {
                // Parse MTL file reference
                mtlFile = line.Substring(7).Trim();
            }
            else if (line.StartsWith("v "))
            {
                var p = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (p.Length >= 4)
                    vertices.Add(new Vector3(
                        float.Parse(p[1], CultureInfo.InvariantCulture),
                        float.Parse(p[2], CultureInfo.InvariantCulture),
                        float.Parse(p[3], CultureInfo.InvariantCulture)));
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

        // Try to find texture from MTL file
        if (!string.IsNullOrEmpty(mtlFile))
        {
            progress?.Report(("Parsing MTL...", 0.5f));
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

        // Z-up detection and conversion
        progress?.Report(("Converting coordinates...", 0.55f));
        bool isZUp = DetectZUp(vertices);
        if (isZUp)
            for (int i = 0; i < vertices.Count; i++)
            {
                var v = vertices[i];
                vertices[i] = new Vector3(v.X, v.Z, -v.Y);
            }

        mesh.Vertices = vertices;

        progress?.Report(("Building triangles...", 0.6f));
        for (int i = 0; i < faces.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (i % 20000 == 0) progress?.Report(("Building triangles...", 0.6f + 0.3f * i / faces.Count));

            var (vIdx, uvIdx) = faces[i];
            var tri = new Triangle(vIdx[0], vIdx[1], vIdx[2]);
            if (uvIdx.Length == 3 && uvs.Count > 0)
                tri.UV = new TriangleUV(
                    uvIdx[0] < uvs.Count ? uvs[uvIdx[0]] : Vector2.Zero,
                    uvIdx[1] < uvs.Count ? uvs[uvIdx[1]] : Vector2.Zero,
                    uvIdx[2] < uvs.Count ? uvs[uvIdx[2]] : Vector2.Zero);
            mesh.Triangles.Add(tri);
        }

        progress?.Report(("Computing bounds...", 0.95f));
        mesh.ComputeBounds();
        mesh.ComputeNormals();
        progress?.Report(("Complete", 1.0f));
        return mesh;
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
                // Look for diffuse texture maps
                if (trimmed.StartsWith("map_Kd ") || trimmed.StartsWith("map_Ka ") || 
                    trimmed.StartsWith("map_d ") || trimmed.StartsWith("map_Bump "))
                {
                    string texName = trimmed.Substring(trimmed.IndexOf(' ') + 1).Trim();
                    
                    // Handle options like -s, -o, etc.
                    if (texName.StartsWith("-"))
                    {
                        var parts = texName.Split(' ');
                        texName = parts[^1]; // Take last part
                    }
                    
                    // Try absolute path first
                    if (File.Exists(texName)) return texName;
                    
                    // Try relative to MTL file
                    string candidate = Path.Combine(baseDir, texName);
                    if (File.Exists(candidate)) return candidate;
                    
                    // Try just filename in same directory
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
