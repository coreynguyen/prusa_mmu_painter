using System.Numerics;

namespace _3MFTool.Imaging;

public enum QuantizeMethod { Uniform, KMeans, MedianCut, Octree }

public static class Quantizer
{
    public static Vector3[] Quantize(byte[] rgba, int width, int height, int numColors, QuantizeMethod method)
    {
        // Extract unique colors
        var colors = new List<Vector3>();
        for (int i = 0; i < rgba.Length; i += 4)
        {
            if (rgba[i + 3] < 128) continue; // Skip transparent
            colors.Add(new Vector3(rgba[i] / 255f, rgba[i + 1] / 255f, rgba[i + 2] / 255f));
        }

        if (colors.Count == 0)
            return DefaultPalette(numColors);

        return method switch
        {
            QuantizeMethod.Uniform => UniformQuantize(colors, numColors),
            QuantizeMethod.KMeans => KMeansQuantize(colors, numColors),
            QuantizeMethod.MedianCut => MedianCutQuantize(colors, numColors),
            QuantizeMethod.Octree => OctreeQuantize(colors, numColors),
            _ => UniformQuantize(colors, numColors)
        };
    }

    private static Vector3[] DefaultPalette(int n)
    {
        var result = new Vector3[n];
        for (int i = 0; i < n; i++)
        {
            float hue = (float)i / n;
            result[i] = HsvToRgb(hue, 0.8f, 0.9f);
        }
        return result;
    }

    #region Uniform Quantization
    private static Vector3[] UniformQuantize(List<Vector3> colors, int numColors)
    {
        // Divide color space uniformly
        int levels = (int)MathF.Ceiling(MathF.Pow(numColors, 1f / 3f));
        var palette = new List<Vector3>();

        for (int r = 0; r < levels && palette.Count < numColors; r++)
        for (int g = 0; g < levels && palette.Count < numColors; g++)
        for (int b = 0; b < levels && palette.Count < numColors; b++)
        {
            palette.Add(new Vector3(
                (r + 0.5f) / levels,
                (g + 0.5f) / levels,
                (b + 0.5f) / levels));
        }

        return palette.Take(numColors).ToArray();
    }
    #endregion

    #region K-Means Quantization
    private static Vector3[] KMeansQuantize(List<Vector3> colors, int numColors, int maxIterations = 20)
    {
        if (colors.Count <= numColors)
            return colors.Concat(Enumerable.Repeat(Vector3.One * 0.5f, numColors - colors.Count)).Take(numColors).ToArray();

        // Initialize centroids with k-means++ 
        var centroids = new Vector3[numColors];
        var rng = new Random(42);
        
        centroids[0] = colors[rng.Next(colors.Count)];
        for (int i = 1; i < numColors; i++)
        {
            var distances = colors.Select(c => 
                Enumerable.Range(0, i).Min(j => Vector3.DistanceSquared(c, centroids[j]))).ToList();
            float total = distances.Sum();
            float threshold = (float)rng.NextDouble() * total;
            float cumulative = 0;
            for (int j = 0; j < colors.Count; j++)
            {
                cumulative += distances[j];
                if (cumulative >= threshold) { centroids[i] = colors[j]; break; }
            }
        }

        // Iterate
        var assignments = new int[colors.Count];
        for (int iter = 0; iter < maxIterations; iter++)
        {
            // Assign colors to nearest centroid
            bool changed = false;
            for (int i = 0; i < colors.Count; i++)
            {
                int nearest = 0;
                float nearestDist = float.MaxValue;
                for (int j = 0; j < numColors; j++)
                {
                    float d = Vector3.DistanceSquared(colors[i], centroids[j]);
                    if (d < nearestDist) { nearestDist = d; nearest = j; }
                }
                if (assignments[i] != nearest) { assignments[i] = nearest; changed = true; }
            }

            if (!changed) break;

            // Update centroids
            var sums = new Vector3[numColors];
            var counts = new int[numColors];
            for (int i = 0; i < colors.Count; i++)
            {
                sums[assignments[i]] += colors[i];
                counts[assignments[i]]++;
            }
            for (int i = 0; i < numColors; i++)
                if (counts[i] > 0) centroids[i] = sums[i] / counts[i];
        }

        return centroids;
    }
    #endregion

    #region Median Cut Quantization
    private static Vector3[] MedianCutQuantize(List<Vector3> colors, int numColors)
    {
        if (colors.Count <= numColors)
            return colors.Concat(Enumerable.Repeat(Vector3.One * 0.5f, numColors - colors.Count)).Take(numColors).ToArray();

        var boxes = new List<List<Vector3>> { colors.ToList() };

        while (boxes.Count < numColors)
        {
            // Find box with largest range
            int maxIdx = 0;
            float maxRange = 0;
            int maxAxis = 0;

            for (int i = 0; i < boxes.Count; i++)
            {
                if (boxes[i].Count <= 1) continue;
                var (axis, range) = GetLargestAxis(boxes[i]);
                if (range > maxRange) { maxRange = range; maxIdx = i; maxAxis = axis; }
            }

            if (boxes[maxIdx].Count <= 1) break;

            // Split along median
            var box = boxes[maxIdx];
            box.Sort((a, b) => GetComponent(a, maxAxis).CompareTo(GetComponent(b, maxAxis)));
            int mid = box.Count / 2;
            boxes[maxIdx] = box.Take(mid).ToList();
            boxes.Add(box.Skip(mid).ToList());
        }

        // Get average color of each box
        return boxes.Select(box => box.Aggregate(Vector3.Zero, (a, b) => a + b) / box.Count).ToArray();
    }

    private static (int axis, float range) GetLargestAxis(List<Vector3> colors)
    {
        float minR = float.MaxValue, maxR = float.MinValue;
        float minG = float.MaxValue, maxG = float.MinValue;
        float minB = float.MaxValue, maxB = float.MinValue;

        foreach (var c in colors)
        {
            minR = MathF.Min(minR, c.X); maxR = MathF.Max(maxR, c.X);
            minG = MathF.Min(minG, c.Y); maxG = MathF.Max(maxG, c.Y);
            minB = MathF.Min(minB, c.Z); maxB = MathF.Max(maxB, c.Z);
        }

        float rangeR = maxR - minR, rangeG = maxG - minG, rangeB = maxB - minB;
        if (rangeR >= rangeG && rangeR >= rangeB) return (0, rangeR);
        if (rangeG >= rangeB) return (1, rangeG);
        return (2, rangeB);
    }

    private static float GetComponent(Vector3 v, int axis) => axis switch { 0 => v.X, 1 => v.Y, _ => v.Z };
    #endregion

    #region Octree Quantization
    private static Vector3[] OctreeQuantize(List<Vector3> colors, int numColors)
    {
        var root = new OctreeNode();
        foreach (var c in colors)
            root.Insert(c, 0);

        // Reduce until we have desired number of leaves
        while (root.LeafCount > numColors)
            root.Reduce();

        var palette = new List<Vector3>();
        root.GetPalette(palette);
        
        while (palette.Count < numColors)
            palette.Add(Vector3.One * 0.5f);

        return palette.Take(numColors).ToArray();
    }

    private class OctreeNode
    {
        public Vector3 ColorSum;
        public int PixelCount;
        public OctreeNode?[] Children = new OctreeNode?[8];
        public bool IsLeaf => Children.All(c => c == null) && PixelCount > 0;

        public int LeafCount => IsLeaf ? 1 : Children.Where(c => c != null).Sum(c => c!.LeafCount);

        public void Insert(Vector3 color, int depth)
        {
            if (depth >= 8)
            {
                ColorSum += color;
                PixelCount++;
                return;
            }

            int idx = GetChildIndex(color, depth);
            Children[idx] ??= new OctreeNode();
            Children[idx]!.Insert(color, depth + 1);
        }

        public void Reduce()
        {
            // Find deepest non-leaf node and merge its children
            var deepest = FindDeepestReducible(0);
            if (deepest == null) return;

            foreach (var child in deepest.Children.Where(c => c != null))
            {
                deepest.ColorSum += child!.ColorSum;
                deepest.PixelCount += child.PixelCount;
            }
            Array.Clear(deepest.Children);
        }

        private OctreeNode? FindDeepestReducible(int depth)
        {
            OctreeNode? deepest = null;
            int deepestLevel = -1;

            foreach (var child in Children.Where(c => c != null))
            {
                if (child!.IsLeaf) continue;
                
                var childDeepest = child.FindDeepestReducible(depth + 1);
                if (childDeepest != null && depth + 1 > deepestLevel)
                {
                    deepest = childDeepest;
                    deepestLevel = depth + 1;
                }
            }

            if (deepest == null && !IsLeaf && Children.Any(c => c?.IsLeaf == true))
                return this;

            return deepest;
        }

        public void GetPalette(List<Vector3> palette)
        {
            if (IsLeaf && PixelCount > 0)
            {
                palette.Add(ColorSum / PixelCount);
                return;
            }
            foreach (var child in Children.Where(c => c != null))
                child!.GetPalette(palette);
        }

        private static int GetChildIndex(Vector3 color, int depth)
        {
            int shift = 7 - depth;
            int r = ((int)(color.X * 255) >> shift) & 1;
            int g = ((int)(color.Y * 255) >> shift) & 1;
            int b = ((int)(color.Z * 255) >> shift) & 1;
            return (r << 2) | (g << 1) | b;
        }
    }
    #endregion

    private static Vector3 HsvToRgb(float h, float s, float v)
    {
        int hi = (int)(h * 6) % 6;
        float f = h * 6 - hi;
        float p = v * (1 - s);
        float q = v * (1 - f * s);
        float t = v * (1 - (1 - f) * s);
        return hi switch
        {
            0 => new Vector3(v, t, p),
            1 => new Vector3(q, v, p),
            2 => new Vector3(p, v, t),
            3 => new Vector3(p, q, v),
            4 => new Vector3(t, p, v),
            _ => new Vector3(v, p, q)
        };
    }
}
