using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace IconSwapper.Services;

public static class IconLoader
{
    public static BitmapSource? LoadSpec(string? spec, int size)
    {
        if (string.IsNullOrWhiteSpace(spec))
            return null;

        try
        {
            var (path, index) = IconPaths.ParseIconSpec(spec);
            if (string.IsNullOrWhiteSpace(path))
                return null;
            if (path.EndsWith(".ico", StringComparison.OrdinalIgnoreCase) && File.Exists(path) && index == 0)
                return LoadFile(path, size);

            var extracted = Extract(path, index, size) ?? (index != 0 ? Extract(path, 0, size) : null);
            if (extracted != null)
                return extracted;
            if (File.Exists(path))
                return LoadFile(path, size);
        }
        catch
        {
            return null;
        }

        return null;
    }

    public static BitmapSource? LoadFile(string path, int size)
    {
        var bytes = File.ReadAllBytes(path);
        using var stream = new MemoryStream(bytes);
        var image = new BitmapImage();
        image.BeginInit();
        image.StreamSource = stream;
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.DecodePixelWidth = size;
        image.EndInit();
        image.Freeze();
        return image;
    }

    private static BitmapSource? Extract(string path, int index, int size)
    {
        var handles = new IntPtr[1];
        var extracted = PrivateExtractIcons(path, index, size, size, handles, null, 1, 0);
        if (extracted == 0 || handles[0] == IntPtr.Zero)
            return FromShell(path, size);

        try
        {
            var source = Imaging.CreateBitmapSourceFromHIcon(
                handles[0],
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        finally
        {
            _ = DestroyIcon(handles[0]);
        }
    }

    private static BitmapSource? FromShell(string path, int size)
    {
        var info = new ShFileInfo();
        var flags = ShgfiIcon | (size <= 20 ? ShgfiSmallIcon : ShgfiLargeIcon);
        var result = SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf<ShFileInfo>(), flags);
        if (result == IntPtr.Zero || info.hIcon == IntPtr.Zero)
            return null;
        try
        {
            var source = Imaging.CreateBitmapSourceFromHIcon(
                info.hIcon,
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        finally
        {
            _ = DestroyIcon(info.hIcon);
        }
    }

    private const uint ShgfiIcon = 0x100;
    private const uint ShgfiSmallIcon = 0x1;
    private const uint ShgfiLargeIcon = 0x0;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint PrivateExtractIcons(
        string fileName,
        int iconIndex,
        int cx,
        int cy,
        IntPtr[]? icons,
        uint[]? ids,
        uint count,
        uint flags);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string path, uint attributes, ref ShFileInfo info, uint size, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileInfo
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }
}
