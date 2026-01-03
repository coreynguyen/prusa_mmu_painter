using System.Numerics;

namespace _3MFTool.Models;

public class Brush
{
    public float Radius { get; set; } = 0.5f;
    public bool AutoSubdivide { get; set; } = true;
    public bool UseNormalMasking { get; set; } = true; // Prevents painting through mesh
    
    public Vector3 Position { get; private set; }
    public Vector3 Normal { get; private set; }
    public int HoverTriangleIndex { get; private set; } = -1;

    private SpatialGrid? _spatialGrid;
    private Mesh? _mesh;
    private float _meshSize = 1f;

    public void SetMesh(Mesh? mesh, SpatialGrid? grid)
    {
        _mesh = mesh;
        _spatialGrid = grid;
        if (mesh != null)
        {
            _meshSize = mesh.BoundingRadius * 2f;
            if (_meshSize < 0.001f) _meshSize = 1f;
        }
        else
        {
            _meshSize = 1f;
        }
    }

    public void SetPosition(Vector3 position, Vector3 normal, int triangleIndex)
    {
        Position = position;
        Normal = normal;
        HoverTriangleIndex = triangleIndex;
    }

    /// <summary>
    /// Dynamic depth based on brush/mesh ratio
    /// </summary>
    private int GetMaxDepth()
    {
        float ratio = Radius / _meshSize;
        if (ratio < 0.003f) return 14;  // Extremely tiny brush
        if (ratio < 0.005f) return 13;
        if (ratio < 0.01f) return 12;
        if (ratio < 0.02f) return 11;
        if (ratio < 0.05f) return 10;
        if (ratio < 0.10f) return 8;
        return 6;
    }

    /// <summary>
    /// Target triangle size - subdivide until triangles are this size
    /// </summary>
    private float GetTargetSize()
    {
        return Radius * 0.3f;  // Subdivide until sub-triangles are ~30% of brush size
    }

    public List<int> FindAffectedTriangles(bool ignoreMask = false)
    {
        return FindAffectedTrianglesAt(Position, ignoreMask);
    }

    public List<int> FindAffectedTrianglesAt(Vector3 pos, bool ignoreMask = false)
    {
        var result = new HashSet<int>();
        if (_mesh == null || _spatialGrid == null) return result.ToList();

        float radiusSq = Radius * Radius;
        
        // High-poly optimization: tighter search radius for small brushes
        float searchRadius = Math.Max(Radius * 2f, _meshSize * 0.05f);
        var candidates = _spatialGrid.FindTrianglesInRadius(pos, searchRadius);
        
        // Pre-calculate normal check
        bool checkNormal = UseNormalMasking && Normal != Vector3.Zero;

        foreach (int triIdx in candidates)
        {
            var tri = _mesh.Triangles[triIdx];
            if (!ignoreMask && tri.Masked) continue;
            
            // Normal check: skip triangles facing away from brush
            if (checkNormal)
            {
                // Dot > 0 means facing same direction as brush normal
                if (Vector3.Dot(tri.Normal, Normal) < 0.1f) continue;
            }

            var v0 = _mesh.Vertices[tri.Indices.V0];
            var v1 = _mesh.Vertices[tri.Indices.V1];
            var v2 = _mesh.Vertices[tri.Indices.V2];

            float distSq = PointToTriangleDistSq(pos, v0, v1, v2);
            if (distSq <= radiusSq)
                result.Add(triIdx);
        }
        
        // Fallback: always include hover triangle if we're close enough
        if (HoverTriangleIndex >= 0 && HoverTriangleIndex < _mesh.Triangles.Count)
        {
            var hoverTri = _mesh.Triangles[HoverTriangleIndex];
            if (ignoreMask || !hoverTri.Masked)
            {
                var v0 = _mesh.Vertices[hoverTri.Indices.V0];
                var v1 = _mesh.Vertices[hoverTri.Indices.V1];
                var v2 = _mesh.Vertices[hoverTri.Indices.V2];
                float distSq = PointToTriangleDistSq(pos, v0, v1, v2);
                if (distSq <= radiusSq * 4) // Generous radius for hover
                    result.Add(HoverTriangleIndex);
            }
        }
        
        return result.ToList();
    }

    /// <summary>
    /// Apply paint with dynamic subdivision
    /// </summary>
    public void ApplyPaint(int triangleIndex, PaintTool tool, int colorIndex)
    {
        if (_mesh == null || triangleIndex < 0 || triangleIndex >= _mesh.Triangles.Count) return;

        var tri = _mesh.Triangles[triangleIndex];
        if (tri.Masked && tool != PaintTool.Unmask && tool != PaintTool.Mask) return;

        var v0 = _mesh.Vertices[tri.Indices.V0];
        var v1 = _mesh.Vertices[tri.Indices.V1];
        var v2 = _mesh.Vertices[tri.Indices.V2];

        float radiusSq = Radius * Radius;
        float targetSize = GetTargetSize();
        float targetSizeSq = targetSize * targetSize;
        int maxDepth = GetMaxDepth();

        var resultList = new List<SubTriangle>(tri.PaintData.Count * 4);
        var stack = new Stack<(BarycentricCoord[] bary, int extId, int depth, bool masked)>(512);

        foreach (var sub in tri.PaintData)
            stack.Push((sub.BaryCorners, sub.ExtruderId, sub.Depth, sub.Masked));

        float px = Position.X, py = Position.Y, pz = Position.Z;

        while (stack.Count > 0)
        {
            var (bary, extId, depth, masked) = stack.Pop();
            var b0 = bary[0]; var b1 = bary[1]; var b2 = bary[2];
            
            // World positions of sub-triangle vertices
            float p0x = b0.U*v0.X + b0.V*v1.X + b0.W*v2.X;
            float p0y = b0.U*v0.Y + b0.V*v1.Y + b0.W*v2.Y;
            float p0z = b0.U*v0.Z + b0.V*v1.Z + b0.W*v2.Z;
            
            float p1x = b1.U*v0.X + b1.V*v1.X + b1.W*v2.X;
            float p1y = b1.U*v0.Y + b1.V*v1.Y + b1.W*v2.Y;
            float p1z = b1.U*v0.Z + b1.V*v1.Z + b1.W*v2.Z;
            
            float p2x = b2.U*v0.X + b2.V*v1.X + b2.W*v2.X;
            float p2y = b2.U*v0.Y + b2.V*v1.Y + b2.W*v2.Y;
            float p2z = b2.U*v0.Z + b2.V*v1.Z + b2.W*v2.Z;

            // Distance squared from brush to each vertex
            float d0 = (p0x-px)*(p0x-px) + (p0y-py)*(p0y-py) + (p0z-pz)*(p0z-pz);
            float d1 = (p1x-px)*(p1x-px) + (p1y-py)*(p1y-py) + (p1z-pz)*(p1z-pz);
            float d2 = (p2x-px)*(p2x-px) + (p2y-py)*(p2y-py) + (p2z-pz)*(p2z-pz);
            
            bool in0 = d0 <= radiusSq;
            bool in1 = d1 <= radiusSq;
            bool in2 = d2 <= radiusSq;

            // Centroid
            float cx = (p0x + p1x + p2x) / 3f;
            float cy = (p0y + p1y + p2y) / 3f;
            float cz = (p0z + p1z + p2z) / 3f;
            float dc = (cx-px)*(cx-px) + (cy-py)*(cy-py) + (cz-pz)*(cz-pz);
            bool centroidIn = dc <= radiusSq;

            // **CRITICAL**: Check if brush center is within radius of this sub-triangle surface
            // This catches small brush on large triangle!
            float distToSurface = PointToTriangleDistSqInline(
                px, py, pz,
                p0x, p0y, p0z,
                p1x, p1y, p1z,
                p2x, p2y, p2z);
            bool brushTouchesTriangle = distToSurface <= radiusSq;

            // CASE A: Fully inside brush - paint it
            if (in0 && in1 && in2)
            {
                var (newExt, newMask) = ApplyTool(tool, colorIndex, extId, masked);
                resultList.Add(new SubTriangle(bary, newExt, depth, newMask));
                continue;
            }

            // CASE B: Completely outside - no vertex in brush AND brush doesn't touch surface
            if (!in0 && !in1 && !in2 && !centroidIn && !brushTouchesTriangle)
            {
                resultList.Add(new SubTriangle(bary, extId, depth, masked));
                continue;
            }

            // Sub-triangle size
            float e0Sq = (p1x-p0x)*(p1x-p0x) + (p1y-p0y)*(p1y-p0y) + (p1z-p0z)*(p1z-p0z);
            float e1Sq = (p2x-p1x)*(p2x-p1x) + (p2y-p1y)*(p2y-p1y) + (p2z-p1z)*(p2z-p1z);
            float e2Sq = (p0x-p2x)*(p0x-p2x) + (p0y-p2y)*(p0y-p2y) + (p0z-p2z)*(p0z-p2z);
            float sizeSq = MathF.Max(e0Sq, MathF.Max(e1Sq, e2Sq));

            // CASE C: At subdivision limit - decide by whether brush touches
            if (depth >= maxDepth || sizeSq <= targetSizeSq)
            {
                if (brushTouchesTriangle || centroidIn || in0 || in1 || in2)
                {
                    var (newExt, newMask) = ApplyTool(tool, colorIndex, extId, masked);
                    resultList.Add(new SubTriangle(bary, newExt, depth, newMask));
                }
                else
                {
                    resultList.Add(new SubTriangle(bary, extId, depth, masked));
                }
                continue;
            }

            // CASE D: Need to subdivide
            if (AutoSubdivide)
            {
                var m01 = new BarycentricCoord((b0.U+b1.U)*0.5f, (b0.V+b1.V)*0.5f, (b0.W+b1.W)*0.5f);
                var m12 = new BarycentricCoord((b1.U+b2.U)*0.5f, (b1.V+b2.V)*0.5f, (b1.W+b2.W)*0.5f);
                var m02 = new BarycentricCoord((b0.U+b2.U)*0.5f, (b0.V+b2.V)*0.5f, (b0.W+b2.W)*0.5f);

                int nd = depth + 1;
                stack.Push((new[] { m01, m12, m02 }, extId, nd, masked));
                stack.Push((new[] { b0, m01, m02 }, extId, nd, masked));
                stack.Push((new[] { m01, b1, m12 }, extId, nd, masked));
                stack.Push((new[] { m02, m12, b2 }, extId, nd, masked));
            }
            else
            {
                // No subdivision allowed - just paint if touching
                if (brushTouchesTriangle || centroidIn)
                {
                    var (newExt, newMask) = ApplyTool(tool, colorIndex, extId, masked);
                    resultList.Add(new SubTriangle(bary, newExt, depth, newMask));
                }
                else
                {
                    resultList.Add(new SubTriangle(bary, extId, depth, masked));
                }
            }
        }

        tri.PaintData = resultList;
    }

    private static (int extId, bool masked) ApplyTool(PaintTool tool, int colorVal, int currentExt, bool currentMask)
    {
        return tool switch
        {
            PaintTool.Paint => currentMask ? (currentExt, currentMask) : (colorVal, currentMask),
            PaintTool.Erase => currentMask ? (currentExt, currentMask) : (0, currentMask),
            PaintTool.Mask => (currentExt, true),
            PaintTool.Unmask => (currentExt, false),
            _ => (currentExt, currentMask)
        };
    }

    /// <summary>
    /// Inline point-to-triangle distance for hot path
    /// Fixed: Sliver triangles now fallback to vertex distance instead of returning infinity
    /// </summary>
    private static float PointToTriangleDistSqInline(
        float px, float py, float pz,
        float t0x, float t0y, float t0z,
        float t1x, float t1y, float t1z,
        float t2x, float t2y, float t2z)
    {
        // Edge vectors
        float e0x = t1x - t0x, e0y = t1y - t0y, e0z = t1z - t0z;
        float e1x = t2x - t0x, e1y = t2y - t0y, e1z = t2z - t0z;
        float vpx = px - t0x, vpy = py - t0y, vpz = pz - t0z;

        float d00 = e0x*e0x + e0y*e0y + e0z*e0z;
        float d01 = e0x*e1x + e0y*e1y + e0z*e1z;
        float d11 = e1x*e1x + e1y*e1y + e1z*e1z;
        float d20 = vpx*e0x + vpy*e0y + vpz*e0z;
        float d21 = vpx*e1x + vpy*e1y + vpz*e1z;

        float denom = d00 * d11 - d01 * d01;
        
        // BUG FIX: Sliver/degenerate triangle detection
        // Instead of returning infinity (which makes triangle unpaintable),
        // fallback to minimum vertex distance
        if (MathF.Abs(denom) < 1e-10f)
        {
            float dV0 = (px-t0x)*(px-t0x) + (py-t0y)*(py-t0y) + (pz-t0z)*(pz-t0z);
            float dV1 = (px-t1x)*(px-t1x) + (py-t1y)*(py-t1y) + (pz-t1z)*(pz-t1z);
            float dV2 = (px-t2x)*(px-t2x) + (py-t2y)*(py-t2y) + (pz-t2z)*(pz-t2z);
            return MathF.Min(dV0, MathF.Min(dV1, dV2));
        }

        float invDenom = 1f / denom;
        float v = (d11 * d20 - d01 * d21) * invDenom;
        float w = (d00 * d21 - d01 * d20) * invDenom;

        // Inside triangle
        if (v >= 0 && w >= 0 && v + w <= 1)
        {
            float cx = t0x + e0x * v + e1x * w;
            float cy = t0y + e0y * v + e1y * w;
            float cz = t0z + e0z * v + e1z * w;
            return (px-cx)*(px-cx) + (py-cy)*(py-cy) + (pz-cz)*(pz-cz);
        }

        // Check edges
        float t01 = d00 > 1e-10f ? Math.Clamp(d20 / d00, 0, 1) : 0;
        float c01x = t0x + e0x * t01, c01y = t0y + e0y * t01, c01z = t0z + e0z * t01;
        float dist01 = (px-c01x)*(px-c01x) + (py-c01y)*(py-c01y) + (pz-c01z)*(pz-c01z);

        float t02 = d11 > 1e-10f ? Math.Clamp(d21 / d11, 0, 1) : 0;
        float c02x = t0x + e1x * t02, c02y = t0y + e1y * t02, c02z = t0z + e1z * t02;
        float dist02 = (px-c02x)*(px-c02x) + (py-c02y)*(py-c02y) + (pz-c02z)*(pz-c02z);

        float e2x = t2x - t1x, e2y = t2y - t1y, e2z = t2z - t1z;
        float v1px = px - t1x, v1py = py - t1y, v1pz = pz - t1z;
        float d22 = e2x*e2x + e2y*e2y + e2z*e2z;
        float d2p = v1px*e2x + v1py*e2y + v1pz*e2z;
        float t12 = d22 > 1e-10f ? Math.Clamp(d2p / d22, 0, 1) : 0;
        float c12x = t1x + e2x * t12, c12y = t1y + e2y * t12, c12z = t1z + e2z * t12;
        float dist12 = (px-c12x)*(px-c12x) + (py-c12y)*(py-c12y) + (pz-c12z)*(pz-c12z);

        return MathF.Min(dist01, MathF.Min(dist02, dist12));
    }

    private static float PointToTriangleDistSq(Vector3 p, Vector3 v0, Vector3 v1, Vector3 v2)
    {
        return PointToTriangleDistSqInline(p.X, p.Y, p.Z, v0.X, v0.Y, v0.Z, v1.X, v1.Y, v1.Z, v2.X, v2.Y, v2.Z);
    }
}
