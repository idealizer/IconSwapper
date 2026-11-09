using System.Runtime.InteropServices;
using IconSwapper.Models;

namespace IconSwapper.Services;

public static class ShortcutUpdater
{
    public static ShortcutApplyResult Apply(string exePath, string icoPath, IList<LinkRecord> links)
    {
        var result = new ShortcutApplyResult();
        var seenFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var shell = CreateShell();
        if (shell == null)
        {
            result.Skipped.Add("Windows Script Host is unavailable, so shortcuts were left alone.");
            return result;
        }

        foreach (var root in Roots())
        {
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(root, "*.lnk", new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                    AttributesToSkip = 0,
                });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                result.Skipped.Add(root + " (" + ex.Message + ")");
                continue;
            }

            foreach (var file in files)
            {
                string fullFile;
                try
                {
                    fullFile = Path.GetFullPath(file);
                }
                catch
                {
                    continue;
                }

                if (!seenFiles.Add(fullFile))
                    continue;

                try
                {
                    dynamic shortcut = shell.CreateShortcut(file);
                    if (!MatchesExe(shortcut, file, exePath))
                        continue;

                    var known = links.FirstOrDefault(link => IconPaths.SamePath(link.Path, file));
                    string? previous = known == null ? shortcut.IconLocation : known.PreviousIcon;
                    shortcut.IconLocation = icoPath + ",0";
                    shortcut.Save();
                    if (known == null)
                        links.Add(new LinkRecord { Path = file, PreviousIcon = previous });

                    result.Updated++;
                    result.UpdatedPaths.Add(file);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or COMException)
                {
                    result.Skipped.Add(file);
                }
                catch (Exception ex)
                {
                    result.Skipped.Add(Path.GetFileName(file) + " (" + ex.Message + ")");
                }
            }
        }

        return result;
    }

    public static List<string> Restore(IEnumerable<LinkRecord> links)
    {
        var errors = new List<string>();
        var shell = CreateShell();
        if (shell == null)
        {
            errors.Add("Windows Script Host is unavailable.");
            return errors;
        }

        foreach (var link in links)
        {
            if (string.IsNullOrWhiteSpace(link.Path) || !File.Exists(link.Path))
                continue;
            try
            {
                dynamic shortcut = shell.CreateShortcut(link.Path);
                shortcut.IconLocation = link.PreviousIcon ?? "";
                shortcut.Save();
            }
            catch (Exception ex)
            {
                errors.Add(Path.GetFileName(link.Path) + " (" + ex.Message + ")");
            }
        }

        return errors;
    }

    private static bool MatchesExe(dynamic shortcut, string file, string exePath)
    {
        string target = shortcut.TargetPath ?? "";
        if (IconPaths.SamePath(target, exePath))
            return true;

        string work = shortcut.WorkingDirectory ?? "";
        var exeDir = Path.GetDirectoryName(exePath);
        if (string.IsNullOrWhiteSpace(work) || string.IsNullOrWhiteSpace(exeDir))
            return false;
        if (!IconPaths.SamePath(work.TrimEnd('\\', '/'), exeDir))
            return false;

        var stem = Path.GetFileNameWithoutExtension(exePath);
        var name = Path.GetFileNameWithoutExtension(file);
        return name.Equals(stem, StringComparison.OrdinalIgnoreCase);
    }

    private static dynamic? CreateShell()
    {
        var type = Type.GetTypeFromProgID("WScript.Shell");
        if (type == null)
            return null;
        return Activator.CreateInstance(type);
    }

    private static IEnumerable<string> Roots()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in Candidates())
        {
            if (string.IsNullOrWhiteSpace(candidate))
                continue;
            string full;
            try
            {
                full = Path.GetFullPath(candidate);
            }
            catch
            {
                continue;
            }

            if (Directory.Exists(full) && seen.Add(full))
                yield return full;
        }
    }

    private static IEnumerable<string?> Candidates()
    {
        yield return KnownDesktop();
        yield return Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        yield return Path.Combine(profile, "OneDrive", "Desktop");
        yield return Path.Combine(profile, "Desktop");
        yield return Environment.GetFolderPath(Environment.SpecialFolder.StartMenu);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.Programs);
        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            @"Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar");
        yield return Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
    }

    private static string? KnownDesktop()
    {
        var result = SHGetKnownFolderPath(FolderDesktop, 0, IntPtr.Zero, out var pointer);
        if (result != 0 || pointer == IntPtr.Zero)
            return null;
        try
        {
            return Marshal.PtrToStringUni(pointer);
        }
        finally
        {
            Marshal.FreeCoTaskMem(pointer);
        }
    }

    private static readonly Guid FolderDesktop = new("B4BFCC3A-DB2C-424C-B029-7FE99A87C641");

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath(
        [MarshalAs(UnmanagedType.LPStruct)] Guid folderId,
        uint flags,
        IntPtr token,
        out IntPtr path);
}

public sealed class ShortcutApplyResult
{
    public int Updated { get; set; }
    public List<string> Skipped { get; } = [];
    public List<string> UpdatedPaths { get; } = [];
}

public sealed class LinkRecord
{
    public string Path { get; set; } = "";
    public string? PreviousIcon { get; set; }
}
