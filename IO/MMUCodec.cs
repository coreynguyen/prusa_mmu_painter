using System.Text;
using _3MFTool.Models;

namespace _3MFTool.IO;

/// <summary>
/// Encode/decode PrusaSlicer MMU segmentation format.
/// 
/// Format (read backwards):
/// - Each hex nibble: lower 2 bits = split type, upper 2 bits = color/special
/// - Split types: 0=leaf, 1=2 children, 2=3 children, 3=4 children  
/// - For leaves with color=3 in upper bits, next nibble + 3 = actual color
/// </summary>
public static class MMUCodec
{
    public static List<SubTriangle> Decode(string encoded)
    {
        if (string.IsNullOrEmpty(encoded))
            return new List<SubTriangle> { new SubTriangle() };

        try
        {
            encoded = encoded.ToUpperInvariant().Trim();
            int pos = encoded.Length - 1;

            int GetNibble()
            {
                while (pos >= 0)
                {
                    char c = encoded[pos--];
                    if (c == ' ') continue;
                    if (c >= '0' && c <= '9') return c - '0';
                    if (c >= 'A' && c <= 'F') return c - 'A' + 10;
                }
                throw new InvalidOperationException("End of string");
            }

            TreeNode? ParseNode()
            {
                int code = GetNibble();
                int numSplit = code & 0b11;
                int upper = code >> 2;

                if (numSplit == 0)
                {
                    // Leaf node
                    int color = upper;
                    if (color == 3)
                    {
                        try { color = GetNibble() + 3; }
                        catch { }
                    }
                    return new TreeNode { Color = color, IsLeaf = true };
                }
                else
                {
                    // Split node
                    var node = new TreeNode { Special = upper, IsLeaf = false };
                    int numChildren = numSplit + 1;
                    for (int i = 0; i < numChildren; i++)
                        node.Children[i] = ParseNode();
                    return node;
                }
            }

            var root = ParseNode();
            if (root != null)
            {
                var result = new List<SubTriangle>();
                var rootCorners = new[] { BarycentricCoord.Corner0, BarycentricCoord.Corner1, BarycentricCoord.Corner2 };
                CollectLeaves(root, rootCorners, 0, result);
                if (result.Count > 0) return result;
            }
        }
        catch { }

        return new List<SubTriangle> { new SubTriangle() };
    }

    private static void CollectLeaves(TreeNode node, BarycentricCoord[] corners, int depth, List<SubTriangle> result)
    {
        if (node.IsLeaf)
        {
            result.Add(new SubTriangle((BarycentricCoord[])corners.Clone(), node.Color, depth));
            return;
        }

        var v0 = corners[0];
        var v1 = corners[1];
        var v2 = corners[2];
        var t01 = BarycentricCoord.Midpoint(v0, v1);
        var t12 = BarycentricCoord.Midpoint(v1, v2);
        var t20 = BarycentricCoord.Midpoint(v2, v0);

        int numChildren = node.Children.Count(c => c != null);
        int ss = node.Special;

        BarycentricCoord[][] layouts;
        if (numChildren == 2)
        {
            layouts = ss switch
            {
                0 => new[] { new[] { t12, v2, v0 }, new[] { v0, v1, t12 } },
                1 => new[] { new[] { t20, v0, v1 }, new[] { v1, v2, t20 } },
                _ => new[] { new[] { t01, v1, v2 }, new[] { v2, v0, t01 } }
            };
        }
        else if (numChildren == 3)
        {
            layouts = ss switch
            {
                0 => new[] { new[] { v1, v2, t20 }, new[] { t01, v1, t20 }, new[] { v0, t01, t20 } },
                1 => new[] { new[] { v2, v0, t01 }, new[] { t12, v2, t01 }, new[] { v1, t12, t01 } },
                _ => new[] { new[] { v0, v1, t12 }, new[] { t20, v0, t12 }, new[] { v2, t20, t12 } }
            };
        }
        else // 4 children (full subdivision)
        {
            layouts = new[] {
                new[] { t01, t12, t20 },
                new[] { t12, v2, t20 },
                new[] { t01, v1, t12 },
                new[] { v0, t01, t20 }
            };
        }

        int idx = 0;
        for (int i = 0; i < 4 && idx < layouts.Length; i++)
        {
            if (node.Children[i] != null)
                CollectLeaves(node.Children[i]!, layouts[idx++], depth + 1, result);
        }
    }

    public static string Encode(List<SubTriangle> paintData)
    {
        if (paintData == null || paintData.Count == 0)
            return "";

        // Single solid color - simple leaf encoding
        if (paintData.Count == 1)
        {
            int color = paintData[0].ExtruderId;
            return EncodeLeaf(color);
        }

        // Check if all same color - can be simplified to single leaf
        bool allSame = true;
        int firstColor = paintData[0].ExtruderId;
        foreach (var sub in paintData)
        {
            if (sub.ExtruderId != firstColor) { allSame = false; break; }
        }
        if (allSame)
        {
            return EncodeLeaf(firstColor);
        }

        // Build proper tree from subdivisions
        var root = BuildTree(paintData);
        if (root == null) return "";

        // Encode tree to string (builds forward, then reverses)
        var sb = new StringBuilder();
        EncodeNode(root, sb);
        
        var chars = sb.ToString().ToCharArray();
        Array.Reverse(chars);
        return new string(chars);
    }

    private static string EncodeLeaf(int color)
    {
        // Color 0 -> "0", Color 1 -> "4", Color 2 -> "8"
        if (color < 3)
            return HexChar(color << 2).ToString();
        else
            // Color 3+ uses extended encoding: first the extension nibble, then 0xC
            // When decoded (reading backwards), reader sees 0xC first (extended marker), then the actual color
            // So output should be: (color-3) followed by C, e.g., Color 3 -> "0C", Color 4 -> "1C"
            return HexChar(color - 3).ToString() + HexChar(0x0C);
    }

    /// <summary>
    /// Build tree recursively using centroid-based quadtree subdivision
    /// </summary>
    private static TreeNode? BuildTree(List<SubTriangle> paintData)
    {
        if (paintData.Count == 0) return null;
        
        return BuildTreeRecursive(
            paintData,
            BarycentricCoord.Corner0,
            BarycentricCoord.Corner1, 
            BarycentricCoord.Corner2,
            0);
    }

    private static TreeNode BuildTreeRecursive(
        List<SubTriangle> subs,
        BarycentricCoord b0, BarycentricCoord b1, BarycentricCoord b2,
        int depth)
    {
        // Base case: no subs or max depth
        if (subs.Count == 0 || depth > 12)
            return new TreeNode { IsLeaf = true, Color = 0 };

        // If only one sub and it covers this region, use its color
        if (subs.Count == 1)
            return new TreeNode { IsLeaf = true, Color = subs[0].ExtruderId };

        // Check if all subs have same color
        int firstColor = subs[0].ExtruderId;
        bool allSame = true;
        foreach (var s in subs)
        {
            if (s.ExtruderId != firstColor) { allSame = false; break; }
        }
        if (allSame)
            return new TreeNode { IsLeaf = true, Color = firstColor };

        // Need to subdivide - compute midpoints
        var t01 = BarycentricCoord.Midpoint(b0, b1);
        var t12 = BarycentricCoord.Midpoint(b1, b2);
        var t20 = BarycentricCoord.Midpoint(b2, b0);

        // Four child regions - MUST MATCH DECODER ORDER: Center, Corner2, Corner1, Corner0
        // Decoder layouts for 4 children:
        //   [0] = {t01, t12, t20} = Center
        //   [1] = {t12, v2, t20} = Corner 2
        //   [2] = {t01, v1, t12} = Corner 1  
        //   [3] = {v0, t01, t20} = Corner 0
        var childRegions = new (BarycentricCoord, BarycentricCoord, BarycentricCoord)[] {
            (t01, t12, t20),  // Child 0: Center
            (t12, b2, t20),   // Child 1: Corner 2
            (t01, b1, t12),   // Child 2: Corner 1
            (b0, t01, t20)    // Child 3: Corner 0
        };

        // Partition subs into children based on centroid
        var childSubs = new List<SubTriangle>[4];
        for (int i = 0; i < 4; i++) childSubs[i] = new List<SubTriangle>();

        foreach (var sub in subs)
        {
            var centroid = GetCentroid(sub.BaryCorners);
            int bestChild = 0;
            float bestDist = float.MaxValue;
            
            for (int i = 0; i < 4; i++)
            {
                var (c0, c1, c2) = childRegions[i];
                var regionCenter = new BarycentricCoord(
                    (c0.U + c1.U + c2.U) / 3f,
                    (c0.V + c1.V + c2.V) / 3f,
                    (c0.W + c1.W + c2.W) / 3f);
                
                float dist = BaryDist(centroid, regionCenter);
                if (dist < bestDist)
                {
                    bestDist = dist;
                    bestChild = i;
                }
            }
            childSubs[bestChild].Add(sub);
        }

        // Build children
        var node = new TreeNode { IsLeaf = false, Special = 0 };
        for (int i = 0; i < 4; i++)
        {
            var (c0, c1, c2) = childRegions[i];
            node.Children[i] = BuildTreeRecursive(childSubs[i], c0, c1, c2, depth + 1);
        }

        // Try to prune if all children are same-color leaves
        int? commonColor = null;
        bool canPrune = true;
        for (int i = 0; i < 4; i++)
        {
            var child = node.Children[i];
            if (child == null || !child.IsLeaf) { canPrune = false; break; }
            if (commonColor == null) commonColor = child.Color;
            else if (child.Color != commonColor) { canPrune = false; break; }
        }
        if (canPrune && commonColor != null)
            return new TreeNode { IsLeaf = true, Color = commonColor.Value };

        return node;
    }

    private static BarycentricCoord GetCentroid(BarycentricCoord[] corners)
    {
        return new BarycentricCoord(
            (corners[0].U + corners[1].U + corners[2].U) / 3f,
            (corners[0].V + corners[1].V + corners[2].V) / 3f,
            (corners[0].W + corners[1].W + corners[2].W) / 3f);
    }

    private static float BaryDist(BarycentricCoord a, BarycentricCoord b)
    {
        float du = a.U - b.U;
        float dv = a.V - b.V;
        float dw = a.W - b.W;
        return du * du + dv * dv + dw * dw;
    }

    private static void EncodeNode(TreeNode node, StringBuilder sb)
    {
        if (node.IsLeaf)
        {
            int color = node.Color;
            if (color < 3)
            {
                sb.Append(HexChar(color << 2));
            }
            else
            {
                // Write [Marker][Extension] - when reversed becomes [Extension][Marker]
                // Reader (backwards): hits [Marker] (0xC), then reads [Extension]
                sb.Append(HexChar(0x0C));      // Marker
                sb.Append(HexChar(color - 3)); // Extension
            }
            return;
        }

        // Count non-null children
        int numChildren = node.Children.Count(c => c != null);
        if (numChildren == 0)
        {
            sb.Append('0');  // Leaf with color 0
            return;
        }

        // Write PARENT first, then children (pre-order traversal)
        // When reversed: Children will come before Parent
        // Reader (backwards): Sees Parent first, then Child 0, Child 1, etc.
        int splitType = numChildren - 1;
        if (splitType < 0) splitType = 0;
        int code = (node.Special << 2) | splitType;
        sb.Append(HexChar(code));

        // Recurse children in order 0,1,2,3
        for (int i = 0; i < 4; i++)
        {
            if (node.Children[i] != null)
                EncodeNode(node.Children[i]!, sb);
        }
    }

    private static char HexChar(int val)
    {
        val &= 0xF;
        return val < 10 ? (char)('0' + val) : (char)('A' + val - 10);
    }

    private class TreeNode
    {
        public int Color;
        public int Special;
        public bool IsLeaf;
        public TreeNode?[] Children = new TreeNode?[4];
    }
}
