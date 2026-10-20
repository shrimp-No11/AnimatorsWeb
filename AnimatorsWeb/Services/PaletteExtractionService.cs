using AnimatorsWeb.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace AnimatorsWeb.Services
{
    public class PaletteExtractionService
    {
        private readonly HttpClient _http;

        public PaletteExtractionService(HttpClient http) => _http = http;

        public async Task<DesignPalette> ExtractFromUrlAsync(string url)
        {
            var bytes = await _http.GetByteArrayAsync(url);
            return ExtractFromBytes(bytes);
        }

        public DesignPalette ExtractFromBytes(byte[] bytes)
        {
            using var image = Image.Load<Rgba32>(bytes);
            image.Mutate(x => x.Resize(new ResizeOptions
            {
                Size = new Size(64, 64),
                Mode = ResizeMode.Max
            }));

            var pixels = new List<(byte, byte, byte)>();
            for (int y = 0; y < image.Height; y++)
                for (int x = 0; x < image.Width; x++)
                {
                    var p = image[x, y];
                    if (p.A < 128) continue;
                    pixels.Add((p.R, p.G, p.B));
                }

            if (pixels.Count == 0) return new DesignPalette();

            var buckets = KMeans(pixels, 6, 10);
            return BuildPalette(buckets);
        }

        // === K-means ===
        private static List<ColorBucket> KMeans(List<(byte R, byte G, byte B)> pixels, int k, int iterations)
        {
            var rnd = new Random(42);
            var initial = pixels.OrderBy(_ => rnd.Next()).Take(k)
                .Select(p => new double[] { p.R, p.G, p.B }).ToArray();

            var centers = initial;
            List<(byte, byte, byte)>[] clusters = new List<(byte, byte, byte)>[k];

            for (int iter = 0; iter < iterations; iter++)
            {
                for (int i = 0; i < k; i++) clusters[i] = new();

                foreach (var p in pixels)
                {
                    int best = 0;
                    double bestDist = double.MaxValue;
                    for (int i = 0; i < k; i++)
                    {
                        var dr = p.R - centers[i][0];
                        var dg = p.G - centers[i][1];
                        var db = p.B - centers[i][2];
                        var d = dr * dr + dg * dg + db * db;
                        if (d < bestDist) { bestDist = d; best = i; }
                    }
                    clusters[best].Add(p);
                }

                for (int i = 0; i < k; i++)
                {
                    if (clusters[i].Count == 0) continue;
                    centers[i][0] = clusters[i].Average(p => (double)p.Item1);
                    centers[i][1] = clusters[i].Average(p => (double)p.Item2);
                    centers[i][2] = clusters[i].Average(p => (double)p.Item3);
                }
            }

            var result = new List<ColorBucket>();
            for (int i = 0; i < k; i++)
            {
                if (clusters[i].Count == 0) continue;
                result.Add(new ColorBucket
                {
                    R = (byte)Math.Round(centers[i][0]),
                    G = (byte)Math.Round(centers[i][1]),
                    B = (byte)Math.Round(centers[i][2]),
                    Count = clusters[i].Count
                });
            }
            return result;
        }

        // === Подбор ролей цветов ===
        private static DesignPalette BuildPalette(List<ColorBucket> buckets)
        {
            var palette = new DesignPalette();
            if (buckets.Count == 0) return palette;

            var byLight = buckets.OrderBy(b => Luminance(b.R, b.G, b.B)).ToList();
            var bySat = buckets.OrderByDescending(b => Saturation(b.R, b.G, b.B)).ToList();

            // Text: самый тёмный
            var textC = byLight.First();
            // Background: самый светлый, но осветляем до читаемого
            var bgC = byLight.Last();

            // Primary: самый насыщенный, желательно средней яркости
            var primaryC = bySat.FirstOrDefault(b =>
            {
                var l = Luminance(b.R, b.G, b.B);
                return l > 0.1 && l < 0.7;
            }) ?? bySat.First();

            // Accent: второй по насыщенности, не равен primary
            var accentC = bySat.FirstOrDefault(b =>
                b != primaryC) ?? bySat.First();

            // Нормализация
            var bg = (0xFA, 0xF7, 0xF2); // дефолтный светлый
            var bgExtracted = (bgC.R, bgC.G, bgC.B);

            // Background: если извлечённый слишком тёмный — используем светлый тон из палитры
            var bgLum = Luminance(bgC.R, bgC.G, bgC.B);
            var finalBg = bgLum > 0.75
                ? bgExtracted
                : Lighten(primaryC.R, primaryC.G, primaryC.B, 0.9);

            var text = ((byte, byte, byte))(textC.R, textC.G, textC.B);
            text = AdjustForContrast(text, finalBg, 7.0, preferDark: true);

            var primary = ((byte, byte, byte))(primaryC.R, primaryC.G, primaryC.B);
            primary = AdjustForContrast(primary, finalBg, 3.0, preferDark: true);

            var primaryHover = Darken(primary.Item1, primary.Item2, primary.Item3, 0.15);

            var accent = ((byte, byte, byte))(accentC.R, accentC.G, accentC.B);

            var card = Lighten(finalBg.Item1, finalBg.Item2, finalBg.Item3, 0.4);

            return new DesignPalette
            {
                Background = ToHex(finalBg),
                Text = ToHex(text),
                Primary = ToHex(primary),
                PrimaryHover = ToHex(primaryHover),
                Accent = ToHex(accent),
                Card = ToHex(card)
            };
        }

        // === Утилиты ===
        private static double Luminance(byte r, byte g, byte b)
        {
            double Lin(byte c)
            {
                var v = c / 255.0;
                return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
            }
            return 0.2126 * Lin(r) + 0.7152 * Lin(g) + 0.0722 * Lin(b);
        }

        private static double Saturation(byte r, byte g, byte b)
        {
            double max = Math.Max(r, Math.Max(g, b));
            double min = Math.Min(r, Math.Min(g, b));
            return max == 0 ? 0 : (max - min) / max;
        }

        private static double Contrast((byte R, byte G, byte B) c1, (byte R, byte G, byte B) c2)
        {
            var l1 = Luminance(c1.R, c1.G, c1.B);
            var l2 = Luminance(c2.R, c2.G, c2.B);
            var lighter = Math.Max(l1, l2);
            var darker = Math.Min(l1, l2);
            return (lighter + 0.05) / (darker + 0.05);
        }

        private static (byte, byte, byte) AdjustForContrast(
            (byte R, byte G, byte B) fg, (byte R, byte G, byte B) bg,
            double minRatio, bool preferDark)
        {
            var current = fg;
            for (int i = 0; i < 20; i++)
            {
                if (Contrast(current, bg) >= minRatio) return current;

                current = preferDark
                    ? ((byte)(current.R * 0.88), (byte)(current.G * 0.88), (byte)(current.B * 0.88))
                    : ((byte)Math.Min(255, current.R + 15),
                       (byte)Math.Min(255, current.G + 15),
                       (byte)Math.Min(255, current.B + 15));
            }
            return current;
        }

        private static (byte, byte, byte) Darken(byte r, byte g, byte b, double amount) =>
            ((byte)(r * (1 - amount)), (byte)(g * (1 - amount)), (byte)(b * (1 - amount)));

        private static (byte, byte, byte) Lighten(byte r, byte g, byte b, double amount) =>
            ((byte)Math.Min(255, r + (255 - r) * amount),
             (byte)Math.Min(255, g + (255 - g) * amount),
             (byte)Math.Min(255, b + (255 - b) * amount));

        private static string ToHex((byte R, byte G, byte B) c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

        private class ColorBucket
        {
            public byte R { get; set; }
            public byte G { get; set; }
            public byte B { get; set; }
            public int Count { get; set; }
        }
    }
}