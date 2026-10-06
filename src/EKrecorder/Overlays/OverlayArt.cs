using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.Runtime.InteropServices;

namespace EKrecorder.Overlays;

/// <summary>Premultiplied 32-bit BGRA pixels, top row first: the format UpdateLayeredWindow takes.</summary>
internal sealed record OverlayBitmap(int Width, int Height, byte[] Pixels);

/// <summary>Draws the pixels for EKrecorder's overlays.</summary>
internal static class OverlayArt
{
    /// <summary>"Recording, all healthy" blue. Also used for the Identify badges.</summary>
    public static readonly Color IndicatorBlue = Color.FromArgb(0x1E, 0x6B, 0xFF);

    /// <summary>
    /// A right triangle whose right angle is the bottom-right pixel corner; the slanted edge runs from the top-right
    /// corner to the bottom-left corner. The slanted edge is anti-aliased with 4×4 supersampling.
    /// </summary>
    public static OverlayBitmap CornerTriangle(int size, Color color)
    {
        const int Samples = 4;
        var pixels = new byte[size * size * 4];
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                int covered = 0;
                for (int sy = 0; sy < Samples; sy++)
                {
                    for (int sx = 0; sx < Samples; sx++)
                    {
                        double px = x + ((sx + 0.5) / Samples);
                        double py = y + ((sy + 0.5) / Samples);
                        if (px + py >= size)
                        {
                            covered++;
                        }
                    }
                }

                WritePremultiplied(pixels, ((y * size) + x) * 4, color, covered * 255 / (Samples * Samples));
            }
        }

        return new OverlayBitmap(size, size, pixels);
    }

    public static OverlayBitmap Solid(int width, int height, Color color)
    {
        var pixels = new byte[width * height * 4];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            WritePremultiplied(pixels, i, color, 255);
        }

        return new OverlayBitmap(width, height, pixels);
    }

    /// <summary>The Identify badge: a blue rounded square with the EKrecorder monitor number.</summary>
    public static OverlayBitmap IdentifyBadge(int number, int size, double scale)
    {
        using var bitmap = new Bitmap(size, size, PixelFormat.Format32bppPArgb);
        using (Graphics g = Graphics.FromImage(bitmap))
        {
            g.Clear(Color.Transparent);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

            using (GraphicsPath shape = RoundedSquare(size, (float)(22 * scale)))
            using (var fill = new SolidBrush(IndicatorBlue))
            {
                g.FillPath(fill, shape);
            }

            using var white = new SolidBrush(Color.White);
            using var centered = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            using (var numberFont = new Font("Segoe UI", (float)(112 * scale), FontStyle.Bold, GraphicsUnit.Pixel))
            {
                g.DrawString(number.ToString(CultureInfo.InvariantCulture), numberFont, white,
                    new RectangleF(0, -(float)(10 * scale), size, size), centered);
            }

            using (var captionFont = new Font("Segoe UI", (float)(15 * scale), FontStyle.Regular, GraphicsUnit.Pixel))
            {
                g.DrawString($"EKrecorder · Monitor {number}", captionFont, white,
                    new RectangleF(0, size - (float)(44 * scale), size, (float)(30 * scale)), centered);
            }
        }

        return FromPremultipliedBitmap(bitmap);
    }

    private static OverlayBitmap FromPremultipliedBitmap(Bitmap bitmap)
    {
        int width = bitmap.Width;
        int height = bitmap.Height;
        var pixels = new byte[width * height * 4];
        BitmapData data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
        try
        {
            for (int y = 0; y < height; y++)
            {
                Marshal.Copy(data.Scan0 + (y * data.Stride), pixels, y * width * 4, width * 4);
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        return new OverlayBitmap(width, height, pixels);
    }

    private static GraphicsPath RoundedSquare(int size, float radius)
    {
        float diameter = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(0, 0, diameter, diameter, 180, 90);
        path.AddArc(size - diameter, 0, diameter, diameter, 270, 90);
        path.AddArc(size - diameter, size - diameter, diameter, diameter, 0, 90);
        path.AddArc(0, size - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static void WritePremultiplied(byte[] pixels, int index, Color color, int alpha)
    {
        pixels[index] = (byte)(color.B * alpha / 255);
        pixels[index + 1] = (byte)(color.G * alpha / 255);
        pixels[index + 2] = (byte)(color.R * alpha / 255);
        pixels[index + 3] = (byte)alpha;
    }
}
