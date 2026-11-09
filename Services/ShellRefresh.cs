using System.Diagnostics;
using System.Runtime.InteropServices;

namespace IconSwapper.Services;

public static class ShellRefresh
{
    public static void Notify()
    {
        SHChangeNotify(AssocChanged, Flush, IntPtr.Zero, IntPtr.Zero);
    }

    public static void RestartExplorer()
    {
        foreach (var process in Process.GetProcessesByName("explorer"))
        {
            try
            {
                process.Kill(entireProcessTree: false);
            }
            catch
            {
                // Explorer may already be closing.
            }
        }

        Thread.Sleep(500);
        if (Process.GetProcessesByName("explorer").Length > 0)
            return;

        Process.Start(new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"),
            UseShellExecute = true,
        });
    }

    private const int AssocChanged = 0x08000000;
    private const uint Flush = 0x1000;

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);
}
