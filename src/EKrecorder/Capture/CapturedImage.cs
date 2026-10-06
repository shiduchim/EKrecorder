using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;

namespace EKrecorder.Capture;

/// <summary>A CPU copy of a captured frame (or part of one): 32-bit BGRA, top row first.</summary>
internal sealed class CapturedImage
{
    private CapturedImage(int width, int height, int stride, byte[] pixels)
    {
        Width = width;
        Height = height;
        Stride = stride;
        Pixels = pixels;
    }

    public int Width { get; }

    public int Height { get; }

    public int Stride { get; }

    public byte[] Pixels { get; }

    public static CapturedImage FromSoftwareBitmap(SoftwareBitmap bitmap)
    {
        if (bitmap.BitmapPixelFormat != BitmapPixelFormat.Bgra8)
        {
            throw new NotSupportedException($"Unexpected pixel format {bitmap.BitmapPixelFormat}.");
        }

        int width = bitmap.PixelWidth;
        int height = bitmap.PixelHeight;
        int packed = checked(width * height * 4);
        // Room for padded rows, in case the copy keeps a row stride wider than width * 4.
        var raw = new byte[checked(packed + (height * 256))];
        Windows.Storage.Streams.IBuffer buffer = raw.AsBuffer(0, 0, raw.Length);
        bitmap.CopyToBuffer(buffer);
        int written = checked((int)buffer.Length);
        int row = width * 4;
        int stride;
        if (written % height == 0 && written / height >= row)
        {
            stride = written / height; // packed rows, or every row padded the same
        }
        else if (height > 1 && (written - row) % (height - 1) == 0 && (written - row) / (height - 1) >= row)
        {
            stride = (written - row) / (height - 1); // padded rows except the last one
        }
        else
        {
            throw new InvalidOperationException($"SoftwareBitmap.CopyToBuffer wrote {written} bytes for {width}x{height}.");
        }

        return new CapturedImage(width, height, stride, raw);
    }

    /// <summary>A copy with premultiplied overlay pixels painted on top, with the overlay's top-left at <paramref name="at"/>.</summary>
    public CapturedImage WithOverlay(Overlays.OverlayBitmap overlay, Point at)
    {
        var pixels = new byte[Width * Height * 4];
        for (int y = 0; y < Height; y++)
        {
            Buffer.BlockCopy(Pixels, y * Stride, pixels, y * Width * 4, Width * 4);
        }

        for (int y = 0; y < overlay.Height; y++)
        {
            for (int x = 0; x < overlay.Width; x++)
            {
                int targetX = at.X + x;
                int targetY = at.Y + y;
                if (targetX < 0 || targetY < 0 || targetX >= Width || targetY >= Height)
                {
                    continue;
                }

                int source = ((y * overlay.Width) + x) * 4;
                int target = ((targetY * Width) + targetX) * 4;
                int inverseAlpha = 255 - overlay.Pixels[source + 3];
                for (int channel = 0; channel < 3; channel++)
                {
                    pixels[target + channel] = (byte)(overlay.Pixels[source + channel] + (pixels[target + channel] * inverseAlpha / 255));
                }
            }
        }

        return new CapturedImage(Width, Height, Width * 4, pixels);
    }

    public bool Contains(Point point) => point.X >= 0 && point.Y >= 0 && point.X < Width && point.Y < Height;

    public (byte B, byte G, byte R) Bgr(int x, int y)
    {
        int index = (y * Stride) + (x * 4);
        return (Pixels[index], Pixels[index + 1], Pixels[index + 2]);
    }

    /// <summary>Copies a region (clipped to the image).</summary>
    public CapturedImage Crop(Rectangle region)
    {
        Rectangle clipped = Rectangle.Intersect(region, new Rectangle(0, 0, Width, Height));
        if (clipped.Width <= 0 || clipped.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(region), $"{region} is outside the {Width}x{Height} frame.");
        }

        var pixels = new byte[clipped.Width * clipped.Height * 4];
        for (int y = 0; y < clipped.Height; y++)
        {
            Buffer.BlockCopy(Pixels, ((clipped.Y + y) * Stride) + (clipped.X * 4), pixels, y * clipped.Width * 4, clipped.Width * 4);
        }

        return new CapturedImage(clipped.Width, clipped.Height, clipped.Width * 4, pixels);
    }

    /// <summary>Saves as PNG; <paramref name="zoom"/> enlarges with hard pixel edges so single pixels stay visible.</summary>
    public void SavePng(string path, int zoom = 1)
    {
        using var bitmap = new Bitmap(Width, Height, PixelFormat.Format32bppRgb);
        BitmapData data = bitmap.LockBits(new Rectangle(0, 0, Width, Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
        try
        {
            for (int y = 0; y < Height; y++)
            {
                Marshal.Copy(Pixels, y * Stride, data.Scan0 + (y * data.Stride), Width * 4);
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        if (zoom <= 1)
        {
            bitmap.Save(path, ImageFormat.Png);
            return;
        }

        using var zoomed = new Bitmap(Width * zoom, Height * zoom, PixelFormat.Format32bppRgb);
        using (Graphics g = Graphics.FromImage(zoomed))
        {
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.DrawImage(bitmap, new Rectangle(0, 0, zoomed.Width, zoomed.Height));
        }

        zoomed.Save(path, ImageFormat.Png);
    }
}
