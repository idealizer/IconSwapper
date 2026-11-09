using System.Text;

namespace IconSwapper.Services;

public static class IconPaths
{
    public static bool IsSafeExtension(string extension)
    {
        if (extension.Length < 2 || extension.Length > 40 || extension[0] != '.')
            return false;
        for (var i = 1; i < extension.Length; i++)
        {
            var c = extension[i];
            var ok = c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_' or '-';
            if (!ok)
                return false;
        }
        return true;
    }

    public static string? NormalizeExtension(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        var text = raw.Trim().ToLowerInvariant();
        if (!text.StartsWith('.'))
            text = "." + text;
        return IsSafeExtension(text) ? text : null;
    }

    public static bool IsSafeProgId(string progId)
    {
        if (string.IsNullOrWhiteSpace(progId) || progId.Length > 220)
            return false;
        if (progId.Contains("..", StringComparison.Ordinal))
            return false;
        if (progId.StartsWith('\\') || progId.StartsWith('/'))
            return false;
        if (progId.Contains('/') || progId.Contains(':'))
            return false;
        foreach (var c in progId)
        {
            if (char.IsControl(c))
                return false;
        }
        return true;
    }

    public static string? ParseExe(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return null;
        var text = command.Trim();
        if (text.StartsWith('"'))
        {
            var end = text.IndexOf('"', 1);
            if (end > 1)
                return Environment.ExpandEnvironmentVariables(text[1..end]);
        }

        var exe = text.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (exe >= 0)
            return Environment.ExpandEnvironmentVariables(text[..(exe + 4)].Trim().Trim('"'));

        var space = text.IndexOf(' ');
        return Environment.ExpandEnvironmentVariables(space < 0 ? text.Trim('"') : text[..space].Trim('"'));
    }

    public static (string Path, int Index) ParseIconSpec(string spec)
    {
        var text = spec.Trim();
        var comma = text.LastIndexOf(',');
        var index = 0;
        var path = text;
        if (comma > 0 && int.TryParse(text[(comma + 1)..], out var parsed))
        {
            index = parsed;
            path = text[..comma];
        }

        path = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
        return (path, index);
    }

    public static string AppDataRoot()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var current = Path.Combine(local, "IconSwapper");
        var legacy = Path.Combine(local, "IconCraft");
        if (!Directory.Exists(current) && Directory.Exists(legacy))
        {
            try
            {
                Directory.Move(legacy, current);
            }
            catch (IOException)
            {
                CopyTree(legacy, current);
            }

            var assignments = Path.Combine(current, "assignments.json");
            if (File.Exists(assignments))
            {
                var text = File.ReadAllText(assignments);
                File.WriteAllText(assignments, text.Replace("IconCraft", "IconSwapper", StringComparison.Ordinal));
            }
        }

        Directory.CreateDirectory(current);
        return current;
    }

    public static string IconValue(string icoPath) => $"\"{icoPath}\",0";

    public static bool SamePath(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            return false;
        return string.Equals(LongPath(left), LongPath(right), StringComparison.OrdinalIgnoreCase);
    }

    public static string LongPath(string path)
    {
        path = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
        try
        {
            path = Path.GetFullPath(path);
        }
        catch
        {
            return path;
        }

        if (!File.Exists(path) && !Directory.Exists(path))
            return path;

        var buffer = new StringBuilder(1024);
        var length = GetLongPathName(path, buffer, (uint)buffer.Capacity);
        return length == 0 ? path : buffer.ToString();
    }

    private static void CopyTree(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
        }

        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            CopyTree(directory, Path.Combine(destination, Path.GetFileName(directory)));
        }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern uint GetLongPathName(string shortPath, StringBuilder longPath, uint buffer);
}
