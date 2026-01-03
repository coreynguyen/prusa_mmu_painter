using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;
using _3MFTool.Models;

namespace _3MFTool.IO;

public class ThreeMFCache
{
    public string SourcePath { get; set; } = "";
    public string SlicerType { get; set; } = "unknown";
    public byte[]? ContentTypes { get; set; }
    public byte[]? Rels { get; set; }
    public Dictionary<string, byte[]> MetadataFiles { get; set; } = new();
    public Dictionary<string, string> ModelMetadata { get; set; } = new();
    public string ModelUnit { get; set; } = "millimeter";
    public string BuildTransform { get; set; } = "";
}

public static class ThreeMFIO
{
    private static ThreeMFCache? _cache;
    public static ThreeMFCache? CurrentCache => _cache;

    public static async Task<Mesh> LoadAsync(string filepath, IProgress<(string, float)>? progress = null, CancellationToken ct = default)
        => await Task.Run(() => Load(filepath, progress, ct), ct);

    public static Mesh Load(string filepath, IProgress<(string, float)>? progress = null, CancellationToken ct = default)
    {
        var mesh = new Mesh();
        var cache = new ThreeMFCache { SourcePath = filepath };

        progress?.Report(("Reading archive...", 0));
        using var zip = ZipFile.OpenRead(filepath);

        foreach (var e in zip.Entries)
        {
            if (e.FullName.Contains("Slic3r_PE")) { cache.SlicerType = "prusa"; break; }
            if (e.FullName.Contains("model_settings")) { cache.SlicerType = "orca"; break; }
        }

        progress?.Report(("Caching metadata...", 0.1f));
        foreach (var e in zip.Entries)
        {
            ct.ThrowIfCancellationRequested();
            if (e.FullName == "[Content_Types].xml") cache.ContentTypes = ReadEntry(e);
            else if (e.FullName == "_rels/.rels") cache.Rels = ReadEntry(e);
            else if (e.FullName.StartsWith("Metadata/")) cache.MetadataFiles[e.FullName] = ReadEntry(e);
        }

        progress?.Report(("Parsing model...", 0.2f));
        string? modelContent = null;
        foreach (var e in zip.Entries)
            if (e.FullName.EndsWith(".model", StringComparison.OrdinalIgnoreCase))
            { using var r = new StreamReader(e.Open()); modelContent = r.ReadToEnd(); break; }

        if (modelContent == null) throw new InvalidDataException("No model file in 3MF");

        var unitMatch = Regex.Match(modelContent, @"unit=""([^""]+)""");
        if (unitMatch.Success) cache.ModelUnit = unitMatch.Groups[1].Value;

        progress?.Report(("Parsing vertices...", 0.4f));
        var rawVerts = new List<Vector3>();
        foreach (Match m in Regex.Matches(modelContent, @"<vertex\s+x=""([^""]+)""\s+y=""([^""]+)""\s+z=""([^""]+)"""))
        {
            float x = float.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            float y = float.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            float z = float.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
            rawVerts.Add(new Vector3(x, z, -y)); // Z-up to Y-up
        }
        
        // Weld duplicate vertices for clean mesh topology
        progress?.Report(("Welding vertices...", 0.5f));
        var (weldedVerts, indexRemap) = WeldVertices(rawVerts, 0.0001f);
        mesh.Vertices = weldedVerts;

        progress?.Report(("Parsing triangles...", 0.6f));
        var triMatches = Regex.Matches(modelContent, @"<triangle\s+v1=""(\d+)""\s+v2=""(\d+)""\s+v3=""(\d+)""([^/]*)/?>");
        int total = triMatches.Count, count = 0;

        foreach (Match m in triMatches)
        {
            ct.ThrowIfCancellationRequested();
            if (++count % 10000 == 0) progress?.Report(("Parsing triangles...", 0.6f + 0.3f * count / total));

            // Get original indices and remap through weld table
            int v0 = int.Parse(m.Groups[1].Value);
            int v1 = int.Parse(m.Groups[2].Value);
            int v2 = int.Parse(m.Groups[3].Value);
            
            // Apply weld remapping
            if (v0 < indexRemap.Length) v0 = indexRemap[v0];
            if (v1 < indexRemap.Length) v1 = indexRemap[v1];
            if (v2 < indexRemap.Length) v2 = indexRemap[v2];
            
            // Skip degenerate triangles (collapsed by welding)
            if (v0 == v1 || v1 == v2 || v2 == v0) continue;
            
            var tri = new Triangle(v0, v1, v2);
            var mmuMatch = Regex.Match(m.Groups[4].Value, @"mmu_segmentation=""([^""]*)""");
            if (mmuMatch.Success && !string.IsNullOrEmpty(mmuMatch.Groups[1].Value))
            {
                tri.MmuSegmentation = mmuMatch.Groups[1].Value;
                var decoded = MMUCodec.Decode(tri.MmuSegmentation);
                if (decoded.Count > 0)
                    tri.PaintData = decoded;
                // else keep default PaintData with single SubTriangle
            }
            // Ensure PaintData is never empty
            if (tri.PaintData.Count == 0)
                tri.PaintData.Add(new SubTriangle());
            
            mesh.Triangles.Add(tri);
        }

        progress?.Report(("Computing bounds...", 0.95f));
        mesh.ComputeBounds();
        mesh.ComputeNormals();
        _cache = cache;
        progress?.Report(("Complete", 1.0f));
        return mesh;
    }

    public static async Task<bool> SaveAsync(string filepath, Mesh mesh, float scale = 1.0f, IProgress<(string, float)>? progress = null, CancellationToken ct = default)
        => await Task.Run(() => Save(filepath, mesh, scale, progress, ct), ct);

    public static bool Save(string filepath, Mesh mesh, float scale = 1.0f, IProgress<(string, float)>? progress = null, CancellationToken ct = default)
    {
        try
        {
            progress?.Report(("Creating archive...", 0));
            string tmp = filepath + ".tmp";

            using (var fs = new FileStream(tmp, FileMode.Create))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                var c = _cache;
                WriteEntry(zip, "[Content_Types].xml", c?.ContentTypes ?? GenContentTypes());
                WriteEntry(zip, "_rels/.rels", c?.Rels ?? GenRels());

                if (c?.MetadataFiles.Count > 0)
                    foreach (var (n, d) in c.MetadataFiles) WriteEntry(zip, n, d);

                progress?.Report(("Writing model...", 0.3f));
                WriteEntry(zip, "3D/3dmodel.model", Encoding.UTF8.GetBytes(GenModelXml(mesh, scale, c, progress, ct)));
            }

            if (File.Exists(filepath)) File.Delete(filepath);
            File.Move(tmp, filepath);
            progress?.Report(("Complete", 1.0f));
            return true;
        }
        catch { return false; }
    }

    private static string GenModelXml(Mesh mesh, float scale, ThreeMFCache? c, IProgress<(string, float)>? progress, CancellationToken ct)
    {
        var sb = new StringBuilder();
        string unit = c?.ModelUnit ?? "millimeter";

        sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        sb.Append($"<model unit=\"{unit}\" xml:lang=\"en-US\" ");
        sb.Append("xmlns=\"http://schemas.microsoft.com/3dmanufacturing/core/2015/02\" ");
        sb.AppendLine("xmlns:slic3rpe=\"http://schemas.slic3r.org/3mf/2017/06\">");
        
        // Critical metadata for PrusaSlicer to recognize paint data
        sb.AppendLine(" <metadata name=\"slic3rpe:Version3mf\">1</metadata>");
        sb.AppendLine(" <metadata name=\"slic3rpe:MmPaintingVersion\">1</metadata>");
        sb.AppendLine(" <metadata name=\"Application\">3MF Tool</metadata>");
        
        sb.AppendLine(" <resources>");
        sb.AppendLine("  <object id=\"1\" type=\"model\">");
        sb.AppendLine("   <mesh>");
        sb.AppendLine("    <vertices>");

        for (int i = 0; i < mesh.Vertices.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (i % 10000 == 0) progress?.Report(("Writing vertices...", 0.3f + 0.2f * i / mesh.Vertices.Count));
            var v = mesh.Vertices[i] * scale; // Apply export scale
            // Convert from Y-up (our internal) to Z-up (3MF standard)
            sb.AppendLine($"     <vertex x=\"{v.X.ToString(CultureInfo.InvariantCulture)}\" y=\"{(-v.Z).ToString(CultureInfo.InvariantCulture)}\" z=\"{v.Y.ToString(CultureInfo.InvariantCulture)}\"/>");
        }

        sb.AppendLine("    </vertices>");
        sb.AppendLine("    <triangles>");
        
        for (int i = 0; i < mesh.Triangles.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (i % 5000 == 0) progress?.Report(("Writing triangles...", 0.5f + 0.4f * i / mesh.Triangles.Count));
            var t = mesh.Triangles[i];
            string mmu = MMUCodec.Encode(t.PaintData);
            if (!string.IsNullOrEmpty(mmu))
                sb.AppendLine($"     <triangle v1=\"{t.Indices.V0}\" v2=\"{t.Indices.V1}\" v3=\"{t.Indices.V2}\" slic3rpe:mmu_segmentation=\"{mmu}\"/>");
            else
                sb.AppendLine($"     <triangle v1=\"{t.Indices.V0}\" v2=\"{t.Indices.V1}\" v3=\"{t.Indices.V2}\"/>");
        }

        sb.AppendLine("    </triangles>");
        sb.AppendLine("   </mesh>");
        sb.AppendLine("  </object>");
        sb.AppendLine(" </resources>");
        sb.AppendLine(" <build>");
        sb.AppendLine("  <item objectid=\"1\" printable=\"1\"/>");
        sb.AppendLine(" </build>");
        sb.AppendLine("</model>");
        return sb.ToString();
    }

    /// <summary>
    /// Weld vertices that are within tolerance of each other.
    /// Returns the new vertex list and a remap table (old index -> new index).
    /// Uses spatial hashing for O(n) performance.
    /// </summary>
    private static (List<Vector3> vertices, int[] remap) WeldVertices(List<Vector3> vertices, float tolerance)
    {
        if (vertices.Count == 0)
            return (new List<Vector3>(), Array.Empty<int>());
        
        float cellSize = tolerance * 2f;
        var grid = new Dictionary<(int, int, int), List<int>>();
        var weldedVerts = new List<Vector3>();
        int[] remap = new int[vertices.Count];
        
        for (int i = 0; i < vertices.Count; i++)
        {
            var v = vertices[i];
            int cx = (int)MathF.Floor(v.X / cellSize);
            int cy = (int)MathF.Floor(v.Y / cellSize);
            int cz = (int)MathF.Floor(v.Z / cellSize);
            
            // Check this cell and neighbors for existing vertex to weld to
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
                // Weld to existing vertex
                remap[i] = weldTo;
            }
            else
            {
                // Create new vertex
                int newIdx = weldedVerts.Count;
                weldedVerts.Add(v);
                remap[i] = newIdx;
                
                // Add to grid
                var key = (cx, cy, cz);
                if (!grid.TryGetValue(key, out var cell))
                {
                    cell = new List<int>();
                    grid[key] = cell;
                }
                cell.Add(newIdx);
            }
        }
        
        return (weldedVerts, remap);
    }

    private static byte[] GenContentTypes() => Encoding.UTF8.GetBytes(
        "<?xml version=\"1.0\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
        "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
        "<Default Extension=\"model\" ContentType=\"application/vnd.ms-package.3dmanufacturing-3dmodel+xml\"/></Types>");

    private static byte[] GenRels() => Encoding.UTF8.GetBytes(
        "<?xml version=\"1.0\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
        "<Relationship Target=\"/3D/3dmodel.model\" Id=\"rel-1\" Type=\"http://schemas.microsoft.com/3dmanufacturing/2013/01/3dmodel\"/></Relationships>");

    private static byte[] ReadEntry(ZipArchiveEntry e) { using var s = e.Open(); using var ms = new MemoryStream(); s.CopyTo(ms); return ms.ToArray(); }
    private static void WriteEntry(ZipArchive z, string n, byte[] d) { var e = z.CreateEntry(n); using var s = e.Open(); s.Write(d, 0, d.Length); }
}
