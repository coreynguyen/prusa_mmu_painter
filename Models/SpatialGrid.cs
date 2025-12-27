using System.Numerics;
using System.Runtime.CompilerServices;

namespace _3MFTool.Models;

public class SpatialGrid
{
    private readonly Dictionary<long, List<int>> _grid = new();
    private readonly Mesh _mesh;
    private float _cellSize;
    private Vector3 _origin;
    private bool _isBuilt;

    public SpatialGrid(Mesh mesh) => _mesh = mesh;

    public void Build()
    {
        _grid.Clear();
        if (_mesh.Vertices.Count == 0 || _mesh.Triangles.Count == 0) { _isBuilt = false; return; }

        var extent = _mesh.BoundsMax - _mesh.BoundsMin;
        float maxExtent = MathF.Max(extent.X, MathF.Max(extent.Y, extent.Z));
        int cellsPerAxis = Math.Max(10, (int)MathF.Ceiling(MathF.Pow(_mesh.Triangles.Count / 50f, 1f / 3f)));
        _cellSize = Math.Max(0.001f, maxExtent / cellsPerAxis);
        _origin = _mesh.BoundsMin - new Vector3(_cellSize);

        for (int i = 0; i < _mesh.Triangles.Count; i++)
        {
            var tri = _mesh.Triangles[i];
            var v0 = _mesh.Vertices[tri.Indices.V0];
            var v1 = _mesh.Vertices[tri.Indices.V1];
            var v2 = _mesh.Vertices[tri.Indices.V2];

            var minP = Vector3.Min(Vector3.Min(v0, v1), v2);
            var maxP = Vector3.Max(Vector3.Max(v0, v1), v2);
            var minCell = WorldToCell(minP);
            var maxCell = WorldToCell(maxP);

            for (int x = minCell.X; x <= maxCell.X; x++)
            for (int y = minCell.Y; y <= maxCell.Y; y++)
            for (int z = minCell.Z; z <= maxCell.Z; z++)
            {
                long key = CellKey(x, y, z);
                if (!_grid.TryGetValue(key, out var list))
                    _grid[key] = list = new List<int>(8);
                if (!list.Contains(i)) list.Add(i);
            }
        }
        _isBuilt = true;
    }

    public List<int> FindTrianglesInRadius(Vector3 point, float radius)
    {
        var result = new List<int>();
        if (!_isBuilt) return result;

        var minCell = WorldToCell(point - new Vector3(radius));
        var maxCell = WorldToCell(point + new Vector3(radius));
        var seen = new HashSet<int>();
        float radiusSq = radius * radius;

        for (int x = minCell.X; x <= maxCell.X; x++)
        for (int y = minCell.Y; y <= maxCell.Y; y++)
        for (int z = minCell.Z; z <= maxCell.Z; z++)
        {
            if (!_grid.TryGetValue(CellKey(x, y, z), out var list)) continue;
            foreach (int triIdx in list)
            {
                if (!seen.Add(triIdx)) continue;
                var tri = _mesh.Triangles[triIdx];
                var v0 = _mesh.Vertices[tri.Indices.V0];
                var v1 = _mesh.Vertices[tri.Indices.V1];
                var v2 = _mesh.Vertices[tri.Indices.V2];
                var centroid = (v0 + v1 + v2) / 3f;
                
                // Include if any vertex or centroid is close, or if point is near triangle
                if (Vector3.DistanceSquared(point, v0) <= radiusSq * 4 ||
                    Vector3.DistanceSquared(point, v1) <= radiusSq * 4 ||
                    Vector3.DistanceSquared(point, v2) <= radiusSq * 4 ||
                    Vector3.DistanceSquared(point, centroid) <= radiusSq * 4)
                {
                    result.Add(triIdx);
                }
            }
        }
        return result;
    }

    public (int TriangleIndex, float Distance, Vector3 HitPoint)? Raycast(Vector3 origin, Vector3 direction)
    {
        if (!_isBuilt) return null;
        direction = Vector3.Normalize(direction);

        int bestTri = -1;
        float bestDist = float.MaxValue;
        Vector3 bestHit = Vector3.Zero;

        // Test all triangles in cells along the ray
        var seen = new HashSet<int>();
        float maxDist = _mesh.BoundingRadius * 3;
        float step = _cellSize * 0.5f;

        for (float t = 0; t < maxDist; t += step)
        {
            var p = origin + direction * t;
            var cell = WorldToCell(p);

            for (int dx = -1; dx <= 1; dx++)
            for (int dy = -1; dy <= 1; dy++)
            for (int dz = -1; dz <= 1; dz++)
            {
                if (!_grid.TryGetValue(CellKey(cell.X + dx, cell.Y + dy, cell.Z + dz), out var list)) continue;
                foreach (int triIdx in list)
                {
                    if (!seen.Add(triIdx)) continue;
                    var tri = _mesh.Triangles[triIdx];
                    if (RayTriangleIntersect(origin, direction,
                        _mesh.Vertices[tri.Indices.V0],
                        _mesh.Vertices[tri.Indices.V1],
                        _mesh.Vertices[tri.Indices.V2],
                        out float dist, out var hit) && dist > 0.0001f && dist < bestDist)
                    {
                        bestDist = dist;
                        bestTri = triIdx;
                        bestHit = hit;
                    }
                }
            }

            if (bestTri >= 0 && t > bestDist + step * 2) break;
        }

        return bestTri >= 0 ? (bestTri, bestDist, bestHit) : null;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private (int X, int Y, int Z) WorldToCell(Vector3 p)
    {
        var local = p - _origin;
        return ((int)MathF.Floor(local.X / _cellSize), (int)MathF.Floor(local.Y / _cellSize), (int)MathF.Floor(local.Z / _cellSize));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long CellKey(int x, int y, int z)
    {
        const long O = 1 << 20;
        return ((long)(x + O) << 42) | ((long)(y + O) << 21) | (long)(z + O);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool RayTriangleIntersect(Vector3 origin, Vector3 dir, Vector3 v0, Vector3 v1, Vector3 v2, out float t, out Vector3 hit)
    {
        const float EPS = 1e-7f;
        t = 0; hit = Vector3.Zero;
        var e1 = v1 - v0; var e2 = v2 - v0;
        var h = Vector3.Cross(dir, e2);
        float a = Vector3.Dot(e1, h);
        if (MathF.Abs(a) < EPS) return false;
        float f = 1f / a;
        var s = origin - v0;
        float u = f * Vector3.Dot(s, h);
        if (u < 0 || u > 1) return false;
        var q = Vector3.Cross(s, e1);
        float v = f * Vector3.Dot(dir, q);
        if (v < 0 || u + v > 1) return false;
        t = f * Vector3.Dot(e2, q);
        if (t > EPS) { hit = origin + dir * t; return true; }
        return false;
    }
}
