using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace VU1WPF
{
    /// <summary>
    /// Procedurally generated brushed-aluminium textures for the two dial finishes
    /// (silver and black anodised). Generated once and cached as frozen brushes.
    /// </summary>
    public static class FinishTextures
    {
        public const string Silver = "silver";
        public const string Black = "black";

        // Texture size: 2x the on-screen housing size (230 x 236) for crisp HiDPI rendering
        private const int TexW = 460;
        private const int TexH = 472;

        private sealed record FinishSpec(
            Color[] Sheen,           // horizontal colour stops (left -> right), evenly spaced
            double TopLight,         // white overlay at the top (0..1)
            double BottomShade,      // black overlay at the bottom (0..1)
            double StreakAmount,     // strength of the brushed streaks (in 0..255 units)
            double GrainAmount,      // per-pixel grain
            Color Edge,              // outer edge colour
            Color Highlight,         // inner top highlight
            Color Grip);             // drag grip dots

        private static readonly Dictionary<string, FinishSpec> Specs = new()
        {
            [Silver] = new FinishSpec(
                new[] { C(0xB9, 0xBC, 0xC0), C(0xD9, 0xDB, 0xDE), C(0xC4, 0xC7, 0xCB), C(0xE2, 0xE4, 0xE6), C(0xB5, 0xB8, 0xBC) },
                0.30, 0.10, 7, 3, C(0x9A, 0x9D, 0xA2), Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF), Color.FromArgb(0x59, 0x00, 0x00, 0x00)),
            [Black] = new FinishSpec(
                new[] { C(0x2A, 0x2B, 0x2E), C(0x37, 0x39, 0x3C), C(0x2D, 0x2F, 0x32), C(0x3A, 0x3C, 0x3F), C(0x28, 0x29, 0x2C) },
                0.10, 0.25, 3, 1.5, C(0x16, 0x17, 0x19), Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF), Color.FromArgb(0x47, 0xFF, 0xFF, 0xFF)),
        };

        private static readonly Dictionary<string, Brush> HousingCache = new();

        private static Color C(byte r, byte g, byte b) => Color.FromRgb(r, g, b);

        private static FinishSpec Spec(string finish) =>
            Specs.TryGetValue(finish ?? "", out var s) ? s : Specs[Black];

        /// <summary>Face of the dial housing.</summary>
        public static Brush Housing(string finish)
        {
            finish = Specs.ContainsKey(finish ?? "") ? finish! : Black;
            if (!HousingCache.TryGetValue(finish, out var brush))
            {
                brush = CreateHousing(Spec(finish), seed: finish == Silver ? 17 : 29);
                HousingCache[finish] = brush;
            }
            return brush;
        }

        public static Brush Edge(string finish) => Frozen(new SolidColorBrush(Spec(finish).Edge));

        /// <summary>Thin inner bevel: light at the top fading out.</summary>
        public static Brush Bevel(string finish)
        {
            var spec = Spec(finish);
            var b = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(0, 1),
                GradientStops =
                {
                    new GradientStop(spec.Highlight, 0),
                    new GradientStop(Color.FromArgb(0, 255, 255, 255), 0.12),
                    new GradientStop(Color.FromArgb(0, 0, 0, 0), 0.85),
                    new GradientStop(Color.FromArgb(0x40, 0, 0, 0), 1),
                },
            };
            return Frozen(b);
        }

        public static Brush Grip(string finish) => Frozen(new SolidColorBrush(Spec(finish).Grip));

        /// <summary>Swatch used for the finish selector.</summary>
        public static Brush Swatch(string finish)
        {
            var b = finish == Silver
                ? new LinearGradientBrush(C(0xEE, 0xF0, 0xF2), C(0xA9, 0xAD, 0xB2), 45)
                : new LinearGradientBrush(C(0x4A, 0x4C, 0x50), C(0x1D, 0x1E, 0x20), 45);
            return Frozen(b);
        }

        private static Brush CreateHousing(FinishSpec spec, int seed)
        {
            var rnd = new Random(seed);

            // Column streaks: smoothed random walk -> long vertical "brush" marks
            var streak = new double[TexW];
            double walk = 0;
            for (int x = 0; x < TexW; x++)
            {
                walk = walk * 0.6 + (rnd.NextDouble() - 0.5) * 2;
                streak[x] = walk;
            }

            var pixels = new byte[TexW * TexH * 4];
            for (int x = 0; x < TexW; x++)
            {
                Color sheen = SampleSheen(spec.Sheen, x / (double)(TexW - 1));
                double s = streak[x] * spec.StreakAmount;

                for (int y = 0; y < TexH; y++)
                {
                    double t = y / (double)(TexH - 1);
                    // White light at the top (first 30 %), shade towards the bottom
                    double light = t < 0.3 ? spec.TopLight * (1 - t / 0.3) : 0;
                    double shade = spec.BottomShade * t;
                    double grain = (rnd.NextDouble() - 0.5) * 2 * spec.GrainAmount;

                    int p = (y * TexW + x) * 4;
                    pixels[p] = Shade(sheen.B, light, shade, s + grain);
                    pixels[p + 1] = Shade(sheen.G, light, shade, s + grain);
                    pixels[p + 2] = Shade(sheen.R, light, shade, s + grain);
                    pixels[p + 3] = 255;
                }
            }

            var bmp = BitmapSource.Create(TexW, TexH, 192, 192, PixelFormats.Bgra32, null, pixels, TexW * 4);
            bmp.Freeze();
            var brush = new ImageBrush(bmp) { Stretch = Stretch.Fill };
            brush.Freeze();
            return brush;
        }

        private static byte Shade(byte c, double light, double shade, double offset)
        {
            double v = c + offset;
            v = v + (255 - v) * light;   // lighten towards white
            v = v * (1 - shade);         // darken towards black
            return (byte)Math.Clamp(Math.Round(v), 0, 255);
        }

        private static Color SampleSheen(Color[] stops, double t)
        {
            double pos = t * (stops.Length - 1);
            int i = Math.Min((int)pos, stops.Length - 2);
            double f = pos - i;
            Color a = stops[i], b = stops[i + 1];
            return Color.FromRgb(
                (byte)(a.R + (b.R - a.R) * f),
                (byte)(a.G + (b.G - a.G) * f),
                (byte)(a.B + (b.B - a.B) * f));
        }

        private static Brush Frozen(Brush b)
        {
            b.Freeze();
            return b;
        }
    }
}
