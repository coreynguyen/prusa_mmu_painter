using System.Numerics;

namespace _3MFTool.Imaging;

public enum QuantizeMethod 
{ 
    Octree,      // Best for most images - groups similar colors
    KMeans,      // Good for photos - finds optimal clusters
    MedianCut,   // Good for graphics - splits by color range
    Popularity,  // Uses most frequent colors
    Uniform      // Divides color space evenly (ignores image)
}

public class QuantizeOptions
{
    // Quantization parameters
    public float ColorWeight { get; set; } = 1.0f;      // 0.5-2.0: Higher = more color separation
    public float PopularityWeight { get; set; } = 0.5f; // 0-1: Higher = favor dominant colors
    public int KMeansIterations { get; set; } = 30;
    public int SampleRate { get; set; } = 1;
    
    // Image preprocessing
    public float Gamma { get; set; } = 1.0f;            // 0.5-2.0: <1 darker, >1 lighter midtones
    public float Contrast { get; set; } = 1.0f;         // 0.5-2.0: Higher = more contrast
    public float Brightness { get; set; } = 0.0f;       // -0.5 to 0.5: Offset
    public float Saturation { get; set; } = 1.0f;       // 0-2.0: 0=grayscale, 2=vivid
    
    // Shadow/lighting compensation
    public bool NormalizeLuminance { get; set; } = false;  // Flatten all colors to same brightness
    public float ShadowLift { get; set; } = 0.0f;          // 0-1: Lift dark areas only
    public float HighlightCompress { get; set; } = 0.0f;   // 0-1: Compress bright areas
}

public static class Quantizer
{
    public static Vector3[] Quantize(byte[] rgba, int width, int height, int numColors, 
        QuantizeMethod method, QuantizeOptions? options = null)
    {
        options ??= new QuantizeOptions();
        
        // Dynamic sample rate for large images
        int totalPixels = width * height;
        int effectiveSampleRate = Math.Max(options.SampleRate, totalPixels / 100000);
        
        // Sample and preprocess colors
        var colors = SampleAndPreprocess(rgba, width, height, effectiveSampleRate, options);
        
        if (colors.Count == 0)
            return CreateDefaultPalette(numColors);

        Vector3[] palette;
        
        switch (method)
        {
            case QuantizeMethod.Octree:
                palette = QuantizeOctree(colors, numColors, options);
                break;
            case QuantizeMethod.KMeans:
                palette = QuantizeKMeans(colors, numColors, options);
                break;
            case QuantizeMethod.MedianCut:
                palette = QuantizeMedianCut(colors, numColors, options);
                break;
            case QuantizeMethod.Popularity:
                palette = QuantizePopularity(colors, numColors, options);
                break;
            case QuantizeMethod.Uniform:
            default:
                palette = QuantizeUniform(colors, numColors);
                break;
        }

        if (palette == null || palette.Length == 0)
            palette = CreateDefaultPalette(numColors);

        return EnsurePaletteSize(palette, numColors);
    }

    #region Preprocessing
    private static List<Vector3> SampleAndPreprocess(byte[] rgba, int width, int height, int sampleRate, QuantizeOptions options)
    {
        var colors = new List<Vector3>();
        int totalPixels = width * height;
        
        // Precompute gamma LUT for speed
        float[] gammaLut = new float[256];
        for (int i = 0; i < 256; i++)
        {
            float v = i / 255f;
            gammaLut[i] = MathF.Pow(v, 1f / options.Gamma);
        }
        
        for (int i = 0; i < totalPixels; i += sampleRate)
        {
            int offset = i * 4;
            if (offset + 3 >= rgba.Length) break;
            if (rgba[offset + 3] < 128) continue;
            
            // Get raw color
            float r = rgba[offset] / 255f;
            float g = rgba[offset + 1] / 255f;
            float b = rgba[offset + 2] / 255f;
            
            // Apply gamma
            if (Math.Abs(options.Gamma - 1f) > 0.01f)
            {
                r = gammaLut[rgba[offset]];
                g = gammaLut[rgba[offset + 1]];
                b = gammaLut[rgba[offset + 2]];
            }
            
            // Apply contrast (around 0.5 midpoint)
            if (Math.Abs(options.Contrast - 1f) > 0.01f)
            {
                r = (r - 0.5f) * options.Contrast + 0.5f;
                g = (g - 0.5f) * options.Contrast + 0.5f;
                b = (b - 0.5f) * options.Contrast + 0.5f;
            }
            
            // Apply brightness
            if (Math.Abs(options.Brightness) > 0.01f)
            {
                r += options.Brightness;
                g += options.Brightness;
                b += options.Brightness;
            }
            
            // Apply saturation
            if (Math.Abs(options.Saturation - 1f) > 0.01f)
            {
                float gray = r * 0.299f + g * 0.587f + b * 0.114f;
                r = gray + (r - gray) * options.Saturation;
                g = gray + (g - gray) * options.Saturation;
                b = gray + (b - gray) * options.Saturation;
            }
            
            // Shadow lift - raises dark areas while preserving highlights
            if (options.ShadowLift > 0.01f)
            {
                float lum = r * 0.299f + g * 0.587f + b * 0.114f;
                // Shadow lift curve: affects dark areas more than bright
                float lift = options.ShadowLift * (1f - lum) * (1f - lum);
                r += lift;
                g += lift;
                b += lift;
            }
            
            // Highlight compress - reduces bright areas while preserving darks
            if (options.HighlightCompress > 0.01f)
            {
                float lum = r * 0.299f + g * 0.587f + b * 0.114f;
                // Compress curve: affects bright areas more
                float compress = options.HighlightCompress * lum * lum;
                r -= compress;
                g -= compress;
                b -= compress;
            }
            
            // Normalize luminance - extracts pure hue by flattening to constant brightness
            // This is the key feature for shadow removal!
            if (options.NormalizeLuminance)
            {
                // Convert to HSL
                float max = Math.Max(r, Math.Max(g, b));
                float min = Math.Min(r, Math.Min(g, b));
                float lum = (max + min) / 2f;
                
                if (max != min && lum > 0.01f && lum < 0.99f)
                {
                    float sat = (max - min) / (1f - Math.Abs(2f * lum - 1f));
                    
                    // Calculate hue
                    float hue = 0;
                    float delta = max - min;
                    if (max == r) hue = ((g - b) / delta) % 6f;
                    else if (max == g) hue = (b - r) / delta + 2f;
                    else hue = (r - g) / delta + 4f;
                    hue /= 6f;
                    if (hue < 0) hue += 1f;
                    
                    // Reconstruct with fixed luminance (0.5) and boosted saturation
                    float targetLum = 0.5f;
                    float targetSat = Math.Min(1f, sat * 1.5f); // Boost saturation
                    
                    // HSL to RGB
                    float c = (1f - Math.Abs(2f * targetLum - 1f)) * targetSat;
                    float x = c * (1f - Math.Abs((hue * 6f) % 2f - 1f));
                    float m = targetLum - c / 2f;
                    
                    int hi = (int)(hue * 6f) % 6;
                    switch (hi)
                    {
                        case 0: r = c + m; g = x + m; b = m; break;
                        case 1: r = x + m; g = c + m; b = m; break;
                        case 2: r = m; g = c + m; b = x + m; break;
                        case 3: r = m; g = x + m; b = c + m; break;
                        case 4: r = x + m; g = m; b = c + m; break;
                        default: r = c + m; g = m; b = x + m; break;
                    }
                }
            }
            
            // Clamp
            r = Math.Clamp(r, 0f, 1f);
            g = Math.Clamp(g, 0f, 1f);
            b = Math.Clamp(b, 0f, 1f);
            
            colors.Add(new Vector3(r, g, b));
        }
        
        return colors;
    }
    #endregion

    #region Helpers
    private static Vector3[] CreateDefaultPalette(int n)
    {
        var result = new Vector3[n];
        for (int i = 0; i < n; i++)
        {
            float hue = (float)i / n;
            result[i] = HsvToRgb(hue, 0.8f, 0.9f);
        }
        return result;
    }

    private static Vector3[] EnsurePaletteSize(Vector3[] palette, int targetSize)
    {
        if (palette.Length == targetSize) return palette;

        var result = new Vector3[targetSize];
        int copyCount = Math.Min(palette.Length, targetSize);
        for (int i = 0; i < copyCount; i++)
            result[i] = ClampColor(palette[i]);
        
        for (int i = copyCount; i < targetSize; i++)
        {
            var baseColor = palette[i % palette.Length];
            float offset = 0.1f * (i / palette.Length + 1);
            result[i] = ClampColor(new Vector3(baseColor.X + offset, baseColor.Y - offset, baseColor.Z + offset));
        }
        return result;
    }

    private static Vector3 ClampColor(Vector3 c) => new Vector3(
        Math.Clamp(float.IsNaN(c.X) ? 0.5f : c.X, 0, 1),
        Math.Clamp(float.IsNaN(c.Y) ? 0.5f : c.Y, 0, 1),
        Math.Clamp(float.IsNaN(c.Z) ? 0.5f : c.Z, 0, 1));

    private static Vector3 HsvToRgb(float h, float s, float v)
    {
        int hi = (int)(h * 6) % 6;
        float f = h * 6 - hi;
        float p = v * (1 - s);
        float q = v * (1 - f * s);
        float t = v * (1 - (1 - f) * s);
        return hi switch { 0 => new Vector3(v, t, p), 1 => new Vector3(q, v, p), 2 => new Vector3(p, v, t), 
                          3 => new Vector3(p, q, v), 4 => new Vector3(t, p, v), _ => new Vector3(v, p, q) };
    }

    // Color distance with perceptual weighting
    private static float ColorDistanceSq(Vector3 a, Vector3 b, float colorWeight)
    {
        // Apply color weight to increase/decrease sensitivity
        float dr = (a.X - b.X) * colorWeight;
        float dg = (a.Y - b.Y) * colorWeight;
        float db = (a.Z - b.Z) * colorWeight;
        // Perceptual weighting (human eye more sensitive to green)
        return dr * dr * 0.299f + dg * dg * 0.587f + db * db * 0.114f;
    }
    #endregion

    #region Uniform
    private static Vector3[] QuantizeUniform(List<Vector3> colors, int numColors)
    {
        Vector3 min = new Vector3(1, 1, 1);
        Vector3 max = new Vector3(0, 0, 0);
        foreach (var c in colors) { min = Vector3.Min(min, c); max = Vector3.Max(max, c); }

        Vector3 range = max - min;
        if (range.X < 0.01f) range.X = 1f;
        if (range.Y < 0.01f) range.Y = 1f;
        if (range.Z < 0.01f) range.Z = 1f;

        int levels = (int)MathF.Ceiling(MathF.Pow(numColors, 1f / 3f));
        var palette = new List<Vector3>();

        for (int r = 0; r < levels && palette.Count < numColors; r++)
        for (int g = 0; g < levels && palette.Count < numColors; g++)
        for (int b = 0; b < levels && palette.Count < numColors; b++)
            palette.Add(new Vector3(min.X + range.X * (r + 0.5f) / levels,
                                    min.Y + range.Y * (g + 0.5f) / levels,
                                    min.Z + range.Z * (b + 0.5f) / levels));
        return palette.ToArray();
    }
    #endregion

    #region K-Means (with weights)
    private static Vector3[] QuantizeKMeans(List<Vector3> colors, int numColors, QuantizeOptions options)
    {
        if (colors.Count <= numColors)
        {
            var result = new Vector3[numColors];
            for (int i = 0; i < numColors; i++)
                result[i] = i < colors.Count ? colors[i] : new Vector3(0.5f, 0.5f, 0.5f);
            return result;
        }

        var rng = new Random(42);
        var centroids = new Vector3[numColors];
        var centroidCounts = new int[numColors];
        
        // K-means++ initialization
        centroids[0] = colors[rng.Next(colors.Count)];
        for (int i = 1; i < numColors; i++)
        {
            float totalDist = 0;
            var distances = new float[colors.Count];
            
            for (int j = 0; j < colors.Count; j++)
            {
                float minDist = float.MaxValue;
                for (int k = 0; k < i; k++)
                    minDist = Math.Min(minDist, ColorDistanceSq(colors[j], centroids[k], options.ColorWeight));
                distances[j] = minDist;
                totalDist += minDist;
            }
            
            if (totalDist < 0.0001f) { centroids[i] = colors[rng.Next(colors.Count)]; continue; }
            
            float threshold = (float)rng.NextDouble() * totalDist;
            float cumulative = 0;
            for (int j = 0; j < colors.Count; j++)
            {
                cumulative += distances[j];
                if (cumulative >= threshold) { centroids[i] = colors[j]; break; }
            }
        }

        var counts = new int[numColors];
        var sums = new Vector3[numColors];

        for (int iter = 0; iter < options.KMeansIterations; iter++)
        {
            Array.Clear(counts);
            Array.Clear(sums);

            // Assign each color to nearest centroid (using color weight)
            for (int i = 0; i < colors.Count; i++)
            {
                int nearest = 0;
                float nearestDist = float.MaxValue;
                for (int j = 0; j < numColors; j++)
                {
                    float d = ColorDistanceSq(colors[i], centroids[j], options.ColorWeight);
                    if (d < nearestDist) { nearestDist = d; nearest = j; }
                }
                counts[nearest]++;
                sums[nearest] += colors[i];
            }

            bool changed = false;
            for (int j = 0; j < numColors; j++)
            {
                if (counts[j] > 0)
                {
                    var newCentroid = sums[j] / counts[j];
                    if (Vector3.DistanceSquared(newCentroid, centroids[j]) > 0.0001f) changed = true;
                    centroids[j] = newCentroid;
                    centroidCounts[j] = counts[j];
                }
            }
            if (!changed) break;
        }

        // Apply popularity weight: sort by count
        var indexed = new List<(Vector3 color, int count)>();
        for (int i = 0; i < numColors; i++)
            indexed.Add((centroids[i], centroidCounts[i]));
        
        if (options.PopularityWeight > 0.01f)
            indexed.Sort((a, b) => b.count.CompareTo(a.count));

        var finalPalette = new Vector3[numColors];
        for (int i = 0; i < numColors; i++)
            finalPalette[i] = indexed[i].color;
        
        return finalPalette;
    }
    #endregion

    #region Median Cut (with weights)
    private static Vector3[] QuantizeMedianCut(List<Vector3> colors, int numColors, QuantizeOptions options)
    {
        // Optimization: Cap sample size for MedianCut
        // Sorting 100k+ items many times is too slow. 15k is sufficient for color accuracy.
        if (colors.Count > 15000)
        {
            var reduced = new List<Vector3>(15000);
            double step = (double)colors.Count / 15000;
            for (double i = 0; i < colors.Count; i += step)
                reduced.Add(colors[(int)i]);
            colors = reduced;
        }
        
        if (colors.Count <= numColors)
        {
            var result = new Vector3[numColors];
            for (int i = 0; i < numColors; i++)
                result[i] = i < colors.Count ? colors[i] : new Vector3(0.5f, 0.5f, 0.5f);
            return result;
        }

        var boxes = new List<(List<Vector3> colors, int count)> { (new List<Vector3>(colors), colors.Count) };

        while (boxes.Count < numColors)
        {
            int splitIdx = -1;
            float maxScore = -1f;
            int splitAxis = 0;

            for (int i = 0; i < boxes.Count; i++)
            {
                var box = boxes[i].colors;
                if (box.Count < 2) continue;

                Vector3 min = new Vector3(float.MaxValue);
                Vector3 max = new Vector3(float.MinValue);
                foreach (var c in box) { min = Vector3.Min(min, c); max = Vector3.Max(max, c); }

                // Apply color weight to range calculation
                float rangeR = (max.X - min.X) * options.ColorWeight;
                float rangeG = (max.Y - min.Y) * options.ColorWeight;
                float rangeB = (max.Z - min.Z) * options.ColorWeight;

                float maxAxisRange = Math.Max(rangeR, Math.Max(rangeG, rangeB));
                // Score includes popularity weight
                float score = maxAxisRange * MathF.Pow(box.Count, options.PopularityWeight);
                
                if (score > maxScore)
                {
                    maxScore = score;
                    splitIdx = i;
                    if (rangeR >= rangeG && rangeR >= rangeB) splitAxis = 0;
                    else if (rangeG >= rangeB) splitAxis = 1;
                    else splitAxis = 2;
                }
            }

            if (splitIdx == -1) break; // No splittable boxes left

            var toSplit = boxes[splitIdx].colors;
            if (toSplit.Count < 2) break;

            toSplit.Sort((a, b) => GetComponent(a, splitAxis).CompareTo(GetComponent(b, splitAxis)));
            
            int mid = toSplit.Count / 2;
            var left = toSplit.GetRange(0, mid);
            var right = toSplit.GetRange(mid, toSplit.Count - mid);

            boxes[splitIdx] = (left, left.Count);
            boxes.Add((right, right.Count));
        }

        // Sort by count if popularity weight is set
        if (options.PopularityWeight > 0.01f)
            boxes.Sort((a, b) => b.count.CompareTo(a.count));

        var palette = new Vector3[boxes.Count];
        for (int i = 0; i < boxes.Count; i++)
        {
            var box = boxes[i].colors;
            if (box.Count == 0) { palette[i] = new Vector3(0.5f, 0.5f, 0.5f); continue; }
            Vector3 sum = Vector3.Zero;
            foreach (var c in box) sum += c;
            palette[i] = sum / box.Count;
        }
        return palette;
    }

    private static float GetComponent(Vector3 v, int axis) => axis switch { 0 => v.X, 1 => v.Y, _ => v.Z };
    #endregion

    #region Popularity (with weights)
    private static Vector3[] QuantizePopularity(List<Vector3> colors, int numColors, QuantizeOptions options)
    {
        // Build histogram using 15-bit color buckets
        var buckets = new Dictionary<int, (Vector3 sum, int count)>();
        
        foreach (var color in colors)
        {
            int key = ((int)(color.X * 31) << 10) | ((int)(color.Y * 31) << 5) | (int)(color.Z * 31);
            if (buckets.TryGetValue(key, out var existing))
                buckets[key] = (existing.sum + color, existing.count + 1);
            else
                buckets[key] = (color, 1);
        }

        var sorted = new List<(Vector3 color, int count)>();
        foreach (var kv in buckets)
            sorted.Add((kv.Value.sum / kv.Value.count, kv.Value.count));
        
        sorted.Sort((a, b) => b.count.CompareTo(a.count));

        // Filter similar colors based on color weight
        var palette = new List<Vector3>();
        float minDistThreshold = 0.02f / (options.ColorWeight * options.ColorWeight);
        
        foreach (var item in sorted)
        {
            bool tooClose = false;
            foreach (var existing in palette)
            {
                if (Vector3.DistanceSquared(item.color, existing) < minDistThreshold)
                { tooClose = true; break; }
            }
            if (!tooClose)
            {
                palette.Add(item.color);
                if (palette.Count >= numColors) break;
            }
        }
        return palette.ToArray();
    }
    #endregion

    #region Octree (completely rewritten - no black texture)
    private static Vector3[] QuantizeOctree(List<Vector3> colors, int numColors, QuantizeOptions options)
    {
        if (colors.Count == 0) return CreateDefaultPalette(numColors);

        // Use histogram-based approach instead of tree (more reliable)
        // This avoids all the tree reduction bugs that cause black textures
        
        // Build color histogram with weighted buckets
        int bucketBits = 4; // 4 bits per channel = 4096 buckets
        int bucketLevels = 1 << bucketBits;
        float bucketScale = bucketLevels - 1;
        
        // Apply color weight by adjusting bucket granularity
        if (options.ColorWeight > 1.2f) bucketBits = 5; // More buckets for higher weight
        else if (options.ColorWeight < 0.8f) bucketBits = 3; // Fewer buckets for lower weight
        
        bucketLevels = 1 << bucketBits;
        bucketScale = bucketLevels - 1;
        
        var buckets = new Dictionary<int, (Vector3 sum, int count)>();
        
        foreach (var c in colors)
        {
            int r = (int)(c.X * bucketScale);
            int g = (int)(c.Y * bucketScale);
            int b = (int)(c.Z * bucketScale);
            int key = (r << (bucketBits * 2)) | (g << bucketBits) | b;
            
            if (buckets.TryGetValue(key, out var existing))
                buckets[key] = (existing.sum + c, existing.count + 1);
            else
                buckets[key] = (c, 1);
        }

        // Convert to list
        var colorList = new List<(Vector3 color, int count)>();
        foreach (var kv in buckets)
        {
            if (kv.Value.count > 0)
                colorList.Add((kv.Value.sum / kv.Value.count, kv.Value.count));
        }

        // If we have fewer unique colors than requested, just return them
        if (colorList.Count <= numColors)
        {
            var result = new Vector3[colorList.Count];
            for (int i = 0; i < colorList.Count; i++)
                result[i] = colorList[i].color;
            return result;
        }

        // Iteratively merge closest colors until we have numColors
        while (colorList.Count > numColors)
        {
            // Find two closest colors
            int mergeA = 0, mergeB = 1;
            float minDist = float.MaxValue;
            
            for (int i = 0; i < colorList.Count; i++)
            {
                for (int j = i + 1; j < colorList.Count; j++)
                {
                    float dist = ColorDistanceSq(colorList[i].color, colorList[j].color, options.ColorWeight);
                    if (dist < minDist)
                    {
                        minDist = dist;
                        mergeA = i;
                        mergeB = j;
                    }
                }
            }

            // Merge them (weighted average)
            var a = colorList[mergeA];
            var b = colorList[mergeB];
            int totalCount = a.count + b.count;
            var merged = (a.color * a.count + b.color * b.count) / totalCount;
            
            // Remove both and add merged
            colorList.RemoveAt(mergeB); // Remove higher index first
            colorList.RemoveAt(mergeA);
            colorList.Add((merged, totalCount));
        }

        // Sort by popularity if weight > 0
        if (options.PopularityWeight > 0.01f)
            colorList.Sort((a, b) => b.count.CompareTo(a.count));

        var palette = new Vector3[colorList.Count];
        for (int i = 0; i < colorList.Count; i++)
            palette[i] = colorList[i].color;
        
        return palette;
    }
    #endregion

    #region Pixel Mapping
    /// <summary>
    /// Maps all pixels to palette indices, applying the same preprocessing as quantization.
    /// This ensures locked palette mode respects gamma, contrast, shadow lift, etc.
    /// </summary>
    public static byte[] MapPixelsToPalette(byte[] rgba, int width, int height, Vector3[] palette, QuantizeOptions? options = null)
    {
        options ??= new QuantizeOptions();
        byte[] indexMap = new byte[width * height];
        int totalPixels = width * height;
        
        // Precompute gamma LUT for speed
        float[] gammaLut = new float[256];
        bool useGamma = Math.Abs(options.Gamma - 1f) > 0.01f;
        for (int i = 0; i < 256; i++)
            gammaLut[i] = MathF.Pow(i / 255f, 1f / options.Gamma);
        
        // Process in parallel for speed
        Parallel.For(0, height, y =>
        {
            for (int x = 0; x < width; x++)
            {
                int i = y * width + x;
                int offset = i * 4;
                if (offset + 3 >= rgba.Length) continue;
                
                // Get raw color
                float r = rgba[offset] / 255f;
                float g = rgba[offset + 1] / 255f;
                float b = rgba[offset + 2] / 255f;
                
                // Apply gamma
                if (useGamma)
                {
                    r = gammaLut[rgba[offset]];
                    g = gammaLut[rgba[offset + 1]];
                    b = gammaLut[rgba[offset + 2]];
                }
                
                // Apply contrast (around 0.5 midpoint)
                if (Math.Abs(options.Contrast - 1f) > 0.01f)
                {
                    r = (r - 0.5f) * options.Contrast + 0.5f;
                    g = (g - 0.5f) * options.Contrast + 0.5f;
                    b = (b - 0.5f) * options.Contrast + 0.5f;
                }
                
                // Apply brightness
                if (Math.Abs(options.Brightness) > 0.01f)
                {
                    r += options.Brightness;
                    g += options.Brightness;
                    b += options.Brightness;
                }
                
                // Apply saturation
                if (Math.Abs(options.Saturation - 1f) > 0.01f)
                {
                    float gray = r * 0.299f + g * 0.587f + b * 0.114f;
                    r = gray + (r - gray) * options.Saturation;
                    g = gray + (g - gray) * options.Saturation;
                    b = gray + (b - gray) * options.Saturation;
                }
                
                // Shadow lift
                if (options.ShadowLift > 0.01f)
                {
                    float lum = r * 0.299f + g * 0.587f + b * 0.114f;
                    float lift = options.ShadowLift * (1f - lum) * (1f - lum);
                    r += lift; g += lift; b += lift;
                }
                
                // Highlight compress
                if (options.HighlightCompress > 0.01f)
                {
                    float lum = r * 0.299f + g * 0.587f + b * 0.114f;
                    float compress = options.HighlightCompress * lum * lum;
                    r -= compress; g -= compress; b -= compress;
                }
                
                // Normalize luminance
                if (options.NormalizeLuminance)
                {
                    float max = Math.Max(r, Math.Max(g, b));
                    float min = Math.Min(r, Math.Min(g, b));
                    float lum = (max + min) / 2f;
                    
                    if (max != min && lum > 0.01f && lum < 0.99f)
                    {
                        float sat = (max - min) / (1f - Math.Abs(2f * lum - 1f));
                        float hue = 0;
                        float delta = max - min;
                        if (max == r) hue = ((g - b) / delta) % 6f;
                        else if (max == g) hue = (b - r) / delta + 2f;
                        else hue = (r - g) / delta + 4f;
                        hue /= 6f;
                        if (hue < 0) hue += 1f;
                        
                        float targetLum = 0.5f;
                        float targetSat = Math.Min(1f, sat * 1.5f);
                        float c = (1f - Math.Abs(2f * targetLum - 1f)) * targetSat;
                        float vx = c * (1f - Math.Abs((hue * 6f) % 2f - 1f));
                        float m = targetLum - c / 2f;
                        
                        int hi = (int)(hue * 6f) % 6;
                        switch (hi)
                        {
                            case 0: r = c + m; g = vx + m; b = m; break;
                            case 1: r = vx + m; g = c + m; b = m; break;
                            case 2: r = m; g = c + m; b = vx + m; break;
                            case 3: r = m; g = vx + m; b = c + m; break;
                            case 4: r = vx + m; g = m; b = c + m; break;
                            default: r = c + m; g = m; b = vx + m; break;
                        }
                    }
                }
                
                // Clamp
                r = Math.Clamp(r, 0f, 1f);
                g = Math.Clamp(g, 0f, 1f);
                b = Math.Clamp(b, 0f, 1f);
                
                var finalColor = new Vector3(r, g, b);
                
                // Find nearest palette color using weighted distance
                int nearest = 0;
                float nearestDist = float.MaxValue;
                for (int j = 0; j < palette.Length; j++)
                {
                    float d = ColorDistanceSq(finalColor, palette[j], options.ColorWeight);
                    if (d < nearestDist) { nearestDist = d; nearest = j; }
                }
                indexMap[i] = (byte)nearest;
            }
        });
        
        return indexMap;
    }
    #endregion
}
