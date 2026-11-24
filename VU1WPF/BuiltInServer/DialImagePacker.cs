using System;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace VU1WPF.BuiltInServer
{
    /// <summary>
    /// Converts an image to the e-ink display's 1-bit format (port of dial_driver.img_to_binary):
    /// 200 x 144 pixels, grayscale, pixel &gt; 127 = 1 (white). The data is sent column by column
    /// (left to right); each column is 144 bits top-to-bottom packed MSB-first into 18 bytes.
    /// Total: 200 * 18 = 3600 bytes.
    /// </summary>
    public static class DialImagePacker
    {
        public const int Width = 200;
        public const int Height = 144;
        public const int BytesPerColumn = Height / 8;
        public const int PackedSize = Width * BytesPerColumn;

        /// <summary>Decodes PNG/JPG/BMP bytes and packs them. Images of another size are scaled to 200x144.</summary>
        public static byte[] Pack(byte[] imageBytes)
        {
            using var ms = new MemoryStream(imageBytes, writable: false);
            var decoder = BitmapDecoder.Create(ms, BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile,
                                               BitmapCacheOption.OnLoad);
            BitmapSource source = decoder.Frames[0];

            if (source.PixelWidth != Width || source.PixelHeight != Height)
            {
                source = new TransformedBitmap(source, new ScaleTransform(
                    (double)Width / source.PixelWidth, (double)Height / source.PixelHeight));
            }

            var gray = new FormatConvertedBitmap(source, PixelFormats.Gray8, null, 0);
            var pixels = new byte[Width * Height];
            gray.CopyPixels(pixels, Width, 0);
            return PackGray(pixels);
        }

        /// <summary>Packs a 200x144 Gray8 buffer (row-major).</summary>
        public static byte[] PackGray(byte[] gray)
        {
            if (gray.Length < Width * Height) throw new ArgumentException("Buffer must be 200x144 bytes", nameof(gray));

            var packed = new byte[PackedSize];
            int o = 0;
            for (int x = 0; x < Width; x++)
            {
                for (int yByte = 0; yByte < BytesPerColumn; yByte++)
                {
                    int b = 0;
                    for (int bit = 0; bit < 8; bit++)
                    {
                        int y = yByte * 8 + bit;
                        if (gray[y * Width + x] > 127) b |= 0x80 >> bit;
                    }
                    packed[o++] = (byte)b;
                }
            }
            return packed;
        }
    }
}
