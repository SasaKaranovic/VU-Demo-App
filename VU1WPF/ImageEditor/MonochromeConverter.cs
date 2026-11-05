using System;

namespace VU1WPF.ImageEditor
{
    /// <summary>How a grayscale image is reduced to 1-bit (black/white).</summary>
    public enum MonochromeMode
    {
        /// <summary>Hard threshold. Best for line art, text and the bundled dial templates.</summary>
        Threshold,
        /// <summary>Floyd–Steinberg error diffusion. Best for photos / gradients.</summary>
        FloydSteinberg,
        /// <summary>Atkinson dithering. Lighter, higher-contrast look (classic Mac style).</summary>
        Atkinson,
    }

    /// <summary>
    /// Pure image math used by the dial image pipeline (no WPF dependencies, easy to unit test).
    /// All buffers are 8-bit grayscale, row-major, <c>width * height</c> bytes, 0 = black, 255 = white.
    /// </summary>
    public static class MonochromeConverter
    {
        /// <summary>
        /// Converts premultiplied BGRA32 pixels to grayscale (ITU-R BT.601 luma), compositing
        /// transparent pixels over white.
        /// </summary>
        public static byte[] BgraToGray(byte[] bgra, int width, int height)
        {
            if (bgra.Length < width * height * 4) throw new ArgumentException("Pixel buffer too small", nameof(bgra));

            var gray = new byte[width * height];
            for (int i = 0, p = 0; i < gray.Length; i++, p += 4)
            {
                int b = bgra[p], g = bgra[p + 1], r = bgra[p + 2], a = bgra[p + 3];
                // Premultiplied over white: c + (255 - a)
                int white = 255 - a;
                r += white; g += white; b += white;
                gray[i] = (byte)Math.Clamp((r * 299 + g * 587 + b * 114 + 500) / 1000, 0, 255);
            }
            return gray;
        }

        /// <summary>
        /// Reduces a grayscale buffer to pure black/white (values 0 or 255).
        /// </summary>
        /// <param name="threshold">0..255. Pixels brighter than or equal to this become white.
        /// For dithering modes it acts as a brightness bias (128 = neutral).</param>
        /// <param name="invert">Invert the image before conversion.</param>
        public static byte[] ToMonochrome(byte[] gray, int width, int height, MonochromeMode mode, int threshold = 128, bool invert = false)
        {
            if (gray.Length < width * height) throw new ArgumentException("Pixel buffer too small", nameof(gray));
            threshold = Math.Clamp(threshold, 1, 255);

            int n = width * height;
            var output = new byte[n];

            if (mode == MonochromeMode.Threshold)
            {
                for (int i = 0; i < n; i++)
                {
                    int v = invert ? 255 - gray[i] : gray[i];
                    output[i] = v >= threshold ? (byte)255 : (byte)0;
                }
                return output;
            }

            // Error diffusion works on a float copy. The threshold slider shifts brightness so the
            // same control is useful for dithered images too.
            float bias = 128 - threshold;
            var buf = new float[n];
            for (int i = 0; i < n; i++)
            {
                int v = invert ? 255 - gray[i] : gray[i];
                buf[i] = v + bias;
            }

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int i = y * width + x;
                    float old = buf[i];
                    byte nv = old >= 128 ? (byte)255 : (byte)0;
                    output[i] = nv;
                    float err = old - nv;

                    if (mode == MonochromeMode.FloydSteinberg)
                    {
                        Spread(buf, width, height, x + 1, y,     err * 7f / 16f);
                        Spread(buf, width, height, x - 1, y + 1, err * 3f / 16f);
                        Spread(buf, width, height, x,     y + 1, err * 5f / 16f);
                        Spread(buf, width, height, x + 1, y + 1, err * 1f / 16f);
                    }
                    else // Atkinson: spreads 6/8 of the error
                    {
                        float e = err / 8f;
                        Spread(buf, width, height, x + 1, y,     e);
                        Spread(buf, width, height, x + 2, y,     e);
                        Spread(buf, width, height, x - 1, y + 1, e);
                        Spread(buf, width, height, x,     y + 1, e);
                        Spread(buf, width, height, x + 1, y + 1, e);
                        Spread(buf, width, height, x,     y + 2, e);
                    }
                }
            }

            return output;
        }

        private static void Spread(float[] buf, int width, int height, int x, int y, float amount)
        {
            if ((uint)x >= (uint)width || (uint)y >= (uint)height) return;
            buf[y * width + x] += amount;
        }

        /// <summary>
        /// Draws a premultiplied-BGRA overlay (text / freehand) onto a monochrome base.
        /// Overlay pixels with alpha &gt;= 50% replace the base with pure black or white, so
        /// text and strokes stay crisp instead of being dithered.
        /// </summary>
        public static byte[] ComposeOverlay(byte[] monoBase, byte[] overlayBgra, int width, int height)
        {
            int n = width * height;
            if (monoBase.Length < n) throw new ArgumentException("Base buffer too small", nameof(monoBase));
            if (overlayBgra.Length < n * 4) throw new ArgumentException("Overlay buffer too small", nameof(overlayBgra));

            var output = new byte[n];
            for (int i = 0, p = 0; i < n; i++, p += 4)
            {
                int a = overlayBgra[p + 3];
                if (a >= 128)
                {
                    // Un-premultiply and take luma of the overlay colour
                    int b = overlayBgra[p] * 255 / a;
                    int g = overlayBgra[p + 1] * 255 / a;
                    int r = overlayBgra[p + 2] * 255 / a;
                    int luma = (r * 299 + g * 587 + b * 114) / 1000;
                    output[i] = luma >= 128 ? (byte)255 : (byte)0;
                }
                else
                {
                    output[i] = monoBase[i];
                }
            }
            return output;
        }

        /// <summary>
        /// Computes where a source image of <paramref name="srcW"/>x<paramref name="srcH"/> should be drawn
        /// on a <paramref name="dstW"/>x<paramref name="dstH"/> canvas. Returns (x, y, width, height).
        /// </summary>
        public static (double x, double y, double w, double h) ComputePlacement(int srcW, int srcH, int dstW, int dstH, ImageFitMode fit)
        {
            if (srcW <= 0 || srcH <= 0) return (0, 0, dstW, dstH);

            switch (fit)
            {
                case ImageFitMode.Stretch:
                    return (0, 0, dstW, dstH);

                case ImageFitMode.Center:
                    // 1:1 pixels, centered (cropped if larger)
                    return (Math.Floor((dstW - srcW) / 2.0), Math.Floor((dstH - srcH) / 2.0), srcW, srcH);

                case ImageFitMode.Fill:
                {
                    double scale = Math.Max((double)dstW / srcW, (double)dstH / srcH);
                    double w = srcW * scale, h = srcH * scale;
                    return ((dstW - w) / 2, (dstH - h) / 2, w, h);
                }

                case ImageFitMode.Fit:
                default:
                {
                    double scale = Math.Min((double)dstW / srcW, (double)dstH / srcH);
                    double w = srcW * scale, h = srcH * scale;
                    return ((dstW - w) / 2, (dstH - h) / 2, w, h);
                }
            }
        }
    }

    /// <summary>How the base image is placed on the 200x144 dial display.</summary>
    public enum ImageFitMode
    {
        /// <summary>Scale to fit entirely, white bars where needed.</summary>
        Fit,
        /// <summary>Scale to cover the whole display, cropping the overflow.</summary>
        Fill,
        /// <summary>Stretch to 200x144 ignoring aspect ratio.</summary>
        Stretch,
        /// <summary>No scaling, centered.</summary>
        Center,
    }
}
