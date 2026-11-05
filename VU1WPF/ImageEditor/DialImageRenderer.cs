using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Ink;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace VU1WPF.ImageEditor
{
    /// <summary>
    /// WPF side of the dial image pipeline:
    ///   base image -> fit to 200x144 -> grayscale -> 1-bit (threshold/dither)
    ///   + text and freehand strokes (always crisp black/white)
    ///   -> PNG bytes ready for the VU1 server.
    /// </summary>
    public static class DialImageRenderer
    {
        public const int W = DialDesign.DisplayWidth;
        public const int H = DialDesign.DisplayHeight;

        // ------------------------------------------------------------------ loading

        /// <summary>Decodes image bytes into a frozen bitmap (no file lock is kept).</summary>
        public static BitmapSource DecodeImage(byte[] bytes, int decodePixelWidth = 0)
        {
            using var ms = new MemoryStream(bytes, writable: false);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            if (decodePixelWidth > 0) bmp.DecodePixelWidth = decodePixelWidth;
            bmp.StreamSource = ms;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }

        public static BitmapSource? TryLoadThumbnail(string path, int width = 100)
        {
            try
            {
                return DecodeImage(File.ReadAllBytes(path), width);
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "Could not load image {Path}", path);
                return null;
            }
        }

        // ------------------------------------------------------------------ pipeline

        /// <summary>Renders the base image (or white if null) into a 200x144 grayscale buffer.</summary>
        public static byte[] RenderBaseGray(BitmapSource? source, ImageFitMode fit)
        {
            var dv = new DrawingVisual();
            RenderOptions.SetBitmapScalingMode(dv, BitmapScalingMode.HighQuality);

            using (DrawingContext dc = dv.RenderOpen())
            {
                dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, W, H));
                if (source != null)
                {
                    var (x, y, w, h) = MonochromeConverter.ComputePlacement(source.PixelWidth, source.PixelHeight, W, H, fit);
                    dc.PushClip(new RectangleGeometry(new Rect(0, 0, W, H)));
                    dc.DrawImage(source, new Rect(x, y, w, h));
                    dc.Pop();
                }
            }

            byte[] bgra = RenderToBgra(dv);
            return MonochromeConverter.BgraToGray(bgra, W, H);
        }

        /// <summary>Renders text items and freehand strokes onto a transparent 200x144 BGRA buffer.</summary>
        public static byte[] RenderOverlayBgra(StrokeCollection? strokes, IEnumerable<DesignTextItem> texts)
        {
            var dv = new DrawingVisual();
            // Aliased text gives cleaner 1-bit glyphs. (Formatting mode stays "Ideal" so the
            // layout matches the TextBlocks shown in the editor.)
            TextOptions.SetTextRenderingMode(dv, TextRenderingMode.Aliased);

            using (DrawingContext dc = dv.RenderOpen())
            {
                dc.PushClip(new RectangleGeometry(new Rect(0, 0, W, H)));

                strokes?.Draw(dc);

                foreach (var item in texts)
                {
                    if (string.IsNullOrEmpty(item.Text)) continue;
                    dc.DrawText(CreateFormattedText(item), new Point(Math.Round(item.X), Math.Round(item.Y)));
                }

                dc.Pop();
            }

            return RenderToBgra(dv);
        }

        public static FormattedText CreateFormattedText(DesignTextItem item)
        {
            var typeface = new Typeface(
                new FontFamily(item.FontFamily),
                FontStyles.Normal,
                item.Bold ? FontWeights.Bold : FontWeights.Normal,
                FontStretches.Normal);

            return new FormattedText(
                item.Text,
                CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                typeface,
                item.FontSize,
                item.White ? Brushes.White : Brushes.Black,
                1.0); // render target is 96 DPI
        }

        /// <summary>Size of the text item in dial pixels.</summary>
        public static Size MeasureText(DesignTextItem item)
        {
            if (string.IsNullOrEmpty(item.Text)) return new Size(0, 0);
            var ft = CreateFormattedText(item);
            return new Size(ft.WidthIncludingTrailingWhitespace, ft.Height);
        }

        /// <summary>Full pipeline. Returns the final 1-bit image as 8-bit gray (0/255) pixels.</summary>
        public static byte[] RenderFinal(byte[] monoBase, StrokeCollection? strokes, IEnumerable<DesignTextItem> texts)
        {
            byte[] overlay = RenderOverlayBgra(strokes, texts);
            return MonochromeConverter.ComposeOverlay(monoBase, overlay, W, H);
        }

        // ------------------------------------------------------------------ conversions

        public static BitmapSource GrayToBitmap(byte[] gray)
        {
            var bmp = BitmapSource.Create(W, H, 96, 96, PixelFormats.Gray8, null, gray, W);
            bmp.Freeze();
            return bmp;
        }

        /// <summary>
        /// Encodes the final image as PNG. 8-bit grayscale with only pure black/white values is used
        /// (rather than a 1-bit PNG) for maximum compatibility with the server's image decoder.
        /// </summary>
        public static byte[] EncodePng(byte[] gray)
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(GrayToBitmap(gray)));
            using var ms = new MemoryStream();
            encoder.Save(ms);
            return ms.ToArray();
        }

        private static byte[] RenderToBgra(Visual visual)
        {
            var rtb = new RenderTargetBitmap(W, H, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(visual);
            var pixels = new byte[W * H * 4];
            rtb.CopyPixels(pixels, W * 4, 0);
            return pixels;
        }
    }
}
