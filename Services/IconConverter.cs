using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace IconSwapper.Services;

public static class IconConverter
{
    public static readonly int[] Sizes = [16, 24, 32, 48, 64, 256];

    public static bool IsAllowed(string path)
    {
        var ext = Path.GetExtension(path);
        return ext.Equals(".png", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".ico", StringComparison.OrdinalIgnoreCase);
    }

    public static BitmapSource Decode(string path)
    {
        if (!IsAllowed(path))
            throw new InvalidOperationException("Use a PNG, JPG, or ICO file.");
        if (!File.Exists(path))
            throw new InvalidOperationException("That image file is missing.");

        var bytes = File.ReadAllBytes(path);
        using var stream = new MemoryStream(bytes);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        if (decoder.Frames.Count == 0)
            throw new InvalidOperationException("That image has no picture in it.");

        var frame = decoder.Frames.OrderByDescending(item => item.PixelWidth).First();
        BitmapSource source = frame;
        if (source.Format != PixelFormats.Pbgra32 && source.Format != PixelFormats.Bgra32)
            source = new FormatConvertedBitmap(source, PixelFormats.Pbgra32, null, 0);
        if (source.PixelWidth <= 0 || source.PixelHeight <= 0)
            throw new InvalidOperationException("That image has no picture in it.");
        if (source.CanFreeze)
            source.Freeze();
        return source;
    }

    public static BitmapSource Render(BitmapSource source, int size)
    {
        var visual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
        using (var dc = visual.RenderOpen())
        {
            var scale = Math.Min(size / (double)source.PixelWidth, size / (double)source.PixelHeight);
            var width = source.PixelWidth * scale;
            var height = source.PixelHeight * scale;
            var x = (size - width) / 2;
            var y = (size - height) / 2;
            dc.DrawImage(source, new Rect(x, y, width, height));
        }

        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    public static byte[] ToIco(string path)
    {
        var source = Decode(path);
        var pngs = Sizes.Select(size => EncodePng(Render(source, size))).ToList();
        return Pack(pngs, Sizes);
    }

    public static string Save(byte[] ico)
    {
        var dir = Path.Combine(IconPaths.AppDataRoot(), "icons");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "set-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + ".ico");
        File.WriteAllBytes(file, ico);
        return file;
    }

    private static byte[] EncodePng(BitmapSource source)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static byte[] Pack(IReadOnlyList<byte[]> pngs, IReadOnlyList<int> sizes)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            writer.Write((ushort)0);
            writer.Write((ushort)1);
            writer.Write((ushort)pngs.Count);
            var offset = 6 + (16 * pngs.Count);
            for (var i = 0; i < pngs.Count; i++)
            {
                var size = sizes[i];
                writer.Write((byte)(size >= 256 ? 0 : size));
                writer.Write((byte)(size >= 256 ? 0 : size));
                writer.Write((byte)0);
                writer.Write((byte)0);
                writer.Write((ushort)1);
                writer.Write((ushort)32);
                writer.Write(pngs[i].Length);
                writer.Write(offset);
                offset += pngs[i].Length;
            }

            foreach (var png in pngs)
                writer.Write(png);
            writer.Flush();
        }

        return stream.ToArray();
    }
}
