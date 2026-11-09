using IconSwapper.Models;
using Microsoft.Win32;

namespace IconSwapper.Services;

public sealed class AssociationReader
{
    private readonly Dictionary<string, ExtensionEntry> _extensions;
    private readonly List<AppEntry> _apps;
    private readonly Dictionary<string, string?> _commandCache = new(StringComparer.OrdinalIgnoreCase);

    private AssociationReader(Dictionary<string, ExtensionEntry> extensions, List<AppEntry> apps)
    {
        _extensions = extensions;
        _apps = apps;
    }

    public static AssociationReader Load()
    {
        var extensions = new Dictionary<string, ExtensionEntry>(StringComparer.OrdinalIgnoreCase);
        ReadClassDefaults(extensions);
        ReadUserChoices(extensions);
        var apps = new List<AppEntry>();
        ReadCapabilities(Registry.LocalMachine, extensions, apps);
        ReadCapabilities(Registry.CurrentUser, extensions, apps);
        ReadApplicationKeys(apps);
        return new AssociationReader(extensions, apps);
    }

    public IReadOnlyList<SearchResult> Search(string query)
    {
        var hits = new List<SearchResult>();
        var extension = ExtensionQuery(query);
        if (extension != null)
            hits.Add(MakeExtensionHit(extension));

        var text = query.Trim();
        if (text.Length == 0)
            return hits;

        var apps = _apps
            .Where(app => MatchesApp(app, text))
            .OrderByDescending(app => string.Equals(FileStem(app.ExePath), text, StringComparison.OrdinalIgnoreCase))
            .ThenBy(app => app.Name, StringComparer.OrdinalIgnoreCase)
            .Take(40);
        foreach (var app in apps)
            hits.Add(MakeAppHit(app));
        return hits;
    }

    public IReadOnlyList<ExtensionOption> BuildChoices(IEnumerable<string> requested)
    {
        var ordered = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in requested)
        {
            var extension = IconPaths.NormalizeExtension(raw);
            if (extension == null || !seen.Add(extension))
                continue;
            ordered.Add(extension);
        }

        var details = ordered.Select(Describe).ToList();
        var progIds = details.SelectMany(detail => detail.ProgIds).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var extras = new List<string>();
        foreach (var pair in _extensions)
        {
            var effective = EffectiveProgId(pair.Key);
            if (effective == null || !progIds.Contains(effective) || seen.Contains(pair.Key))
                continue;
            seen.Add(pair.Key);
            extras.Add(pair.Key);
        }

        extras.Sort(StringComparer.OrdinalIgnoreCase);
        var options = new List<ExtensionOption>();
        foreach (var detail in details)
        {
            options.Add(new ExtensionOption
            {
                Detail = detail,
                Shared = false,
                Note = detail.TypeName ?? "",
            });
        }

        foreach (var extension in extras)
        {
            var detail = Describe(extension);
            var owner = details.FirstOrDefault(item =>
                item.EffectiveProgId != null &&
                string.Equals(item.EffectiveProgId, detail.EffectiveProgId, StringComparison.OrdinalIgnoreCase));
            options.Add(new ExtensionOption
            {
                Detail = detail,
                Shared = true,
                Note = owner == null
                    ? "Shares an icon with the selection"
                    : "Shares an icon with " + owner.Extension,
            });
        }

        return options;
    }

    public IReadOnlyList<string> ExtensionsUsing(string progId)
    {
        var users = new List<string>();
        foreach (var extension in _extensions.Keys)
        {
            if (string.Equals(EffectiveProgId(extension), progId, StringComparison.OrdinalIgnoreCase))
                users.Add(extension);
        }
        return users;
    }

    public ExtensionDetail Describe(string extension)
    {
        extension = IconPaths.NormalizeExtension(extension) ?? extension;
        var effective = EffectiveProgId(extension);
        var progIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddProgId(progIds, effective);
        if (_extensions.TryGetValue(extension, out var entry))
        {
            AddProgId(progIds, entry.ClassProgId);
            foreach (var claim in entry.Claims)
                AddProgId(progIds, claim.ProgId);
        }

        foreach (var progId in ReadOpenWith(extension))
            AddProgId(progIds, progId);

        var exe = ResolveHandler(extension, effective, out var handlerName);
        return new ExtensionDetail
        {
            Extension = extension,
            EffectiveProgId = effective,
            TypeName = FriendlyType(effective),
            IconSpec = ReadIcon(effective, extension),
            HandlerExe = exe,
            HandlerName = handlerName,
            ProgIds = progIds.ToList(),
        };
    }

    private string? ExtensionQuery(string query)
    {
        var text = query.Trim();
        if (text.Length == 0)
            return null;
        if (text.StartsWith('.'))
            return IconPaths.NormalizeExtension(text);
        if (text.Contains(' ') || text.Contains('\\') || text.Contains('/'))
            return null;
        var candidate = IconPaths.NormalizeExtension(text);
        if (candidate != null && _extensions.ContainsKey(candidate))
            return candidate;
        return null;
    }

    private SearchResult MakeExtensionHit(string extension)
    {
        var detail = Describe(extension);
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(detail.TypeName))
            parts.Add(detail.TypeName);
        if (!string.IsNullOrWhiteSpace(detail.HandlerName))
            parts.Add("Opens with " + detail.HandlerName);
        else if (!_extensions.ContainsKey(extension))
            parts.Add("Not registered yet");
        else
            parts.Add("No open command");

        return new SearchResult
        {
            Title = extension,
            Subtitle = string.Join(" · ", parts),
            IconSpec = detail.IconSpec ?? (detail.HandlerExe == null ? null : detail.HandlerExe + ",0"),
            Extensions = [extension],
            ExePath = detail.HandlerExe,
            IsApp = false,
        };
    }

    private static SearchResult MakeAppHit(AppEntry app)
    {
        var extensions = app.Extensions.Order(StringComparer.OrdinalIgnoreCase).ToList();
        var listed = extensions.Count == 0
            ? "No registered file types"
            : string.Join(" ", extensions.Take(6)) + (extensions.Count > 6 ? " …" : "");
        var file = string.IsNullOrWhiteSpace(app.ExePath) ? "" : Path.GetFileName(app.ExePath);
        var subtitle = string.IsNullOrWhiteSpace(file) ? listed : file + " · " + listed;
        return new SearchResult
        {
            Title = app.Name,
            Subtitle = subtitle,
            IconSpec = app.ExePath == null ? null : app.ExePath + ",0",
            Extensions = extensions,
            ExePath = app.ExePath,
            IsApp = true,
        };
    }

    private string? ResolveHandler(string extension, string? effective, out string? handlerName)
    {
        var candidates = new List<(string Name, string Exe)>();
        var effectiveExe = effective == null ? null : CommandExe(effective);
        if (!string.IsNullOrWhiteSpace(effectiveExe))
        {
            handlerName = NameForExe(effectiveExe) ?? Path.GetFileNameWithoutExtension(effectiveExe);
            return effectiveExe;
        }

        if (_extensions.TryGetValue(extension, out var entry))
        {
            foreach (var claim in entry.Claims)
            {
                var exe = claim.ExePath ?? CommandExe(claim.ProgId);
                if (!string.IsNullOrWhiteSpace(exe))
                    candidates.Add((claim.AppName, exe));
            }
        }

        var distinct = candidates
            .GroupBy(item => IconPaths.LongPath(item.Exe), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
        if (distinct.Count == 1)
        {
            handlerName = distinct[0].Name;
            return distinct[0].Exe;
        }

        handlerName = distinct.Count == 0 ? null : string.Join(", ", distinct.Select(item => item.Name).Distinct());
        return null;
    }

    private string? NameForExe(string exe)
    {
        return _apps.FirstOrDefault(app => IconPaths.SamePath(app.ExePath, exe))?.Name;
    }

    private string? EffectiveProgId(string extension)
    {
        if (!_extensions.TryGetValue(extension, out var entry))
            return null;
        if (!string.IsNullOrWhiteSpace(entry.UserChoiceProgId))
            return entry.UserChoiceProgId;
        return string.IsNullOrWhiteSpace(entry.ClassProgId) ? null : entry.ClassProgId;
    }

    private string? ReadIcon(string? progId, string extension)
    {
        if (progId != null)
        {
            var icon = ReadClassesDefault(progId + @"\DefaultIcon");
            if (!string.IsNullOrWhiteSpace(icon))
                return icon;
        }

        var own = ReadClassesDefault(extension + @"\DefaultIcon");
        return string.IsNullOrWhiteSpace(own) ? null : own;
    }

    private string? FriendlyType(string? progId)
    {
        if (progId == null)
            return null;
        var name = ReadClassesDefault(progId);
        if (string.IsNullOrWhiteSpace(name) || name.StartsWith('@'))
            return null;
        return name;
    }

    private string? CommandExe(string progId)
    {
        if (_commandCache.TryGetValue(progId, out var cached))
            return cached;
        var command = ReadClassesDefault(progId + @"\shell\open\command");
        var exe = IconPaths.ParseExe(command);
        _commandCache[progId] = exe;
        return exe;
    }

    private static void AddProgId(HashSet<string> progIds, string? progId)
    {
        if (!string.IsNullOrWhiteSpace(progId) && IconPaths.IsSafeProgId(progId))
            progIds.Add(progId);
    }

    private static IEnumerable<string> ReadOpenWith(string extension)
    {
        var names = new List<string>();
        CollectNames(Registry.ClassesRoot, extension + @"\OpenWithProgids", names);
        CollectNames(
            Registry.CurrentUser,
            @"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\" + extension + @"\OpenWithProgids",
            names);
        return names;
    }

    private static void CollectNames(RegistryKey root, string path, List<string> names)
    {
        try
        {
            using var key = root.OpenSubKey(path);
            if (key == null)
                return;
            foreach (var name in key.GetValueNames())
            {
                if (!string.IsNullOrWhiteSpace(name))
                    names.Add(name);
            }
        }
        catch
        {
            // A single unreadable key should not hide the rest of the type.
        }
    }

    private static string? ReadClassesDefault(string relative)
    {
        try
        {
            using var key = Registry.ClassesRoot.OpenSubKey(relative);
            return key?.GetValue("") as string;
        }
        catch
        {
            return null;
        }
    }

    private static void ReadClassDefaults(Dictionary<string, ExtensionEntry> extensions)
    {
        string[] names;
        try
        {
            using var classes = Registry.ClassesRoot;
            names = classes.GetSubKeyNames();
        }
        catch
        {
            return;
        }

        foreach (var name in names)
        {
            var extension = IconPaths.NormalizeExtension(name);
            if (extension == null)
                continue;
            try
            {
                using var key = Registry.ClassesRoot.OpenSubKey(name);
                var progId = key?.GetValue("") as string;
                Entry(extensions, extension).ClassProgId = string.IsNullOrWhiteSpace(progId) ? null : progId;
            }
            catch
            {
                Entry(extensions, extension);
            }
        }
    }

    private static void ReadUserChoices(Dictionary<string, ExtensionEntry> extensions)
    {
        try
        {
            using var fileExts = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts");
            if (fileExts == null)
                return;
            foreach (var name in fileExts.GetSubKeyNames())
            {
                var extension = IconPaths.NormalizeExtension(name);
                if (extension == null)
                    continue;
                using var choice = fileExts.OpenSubKey(name + @"\UserChoice");
                var progId = choice?.GetValue("ProgId") as string;
                if (!string.IsNullOrWhiteSpace(progId))
                    Entry(extensions, extension).UserChoiceProgId = progId;
            }
        }
        catch
        {
            // FileExts is optional.
        }
    }

    private static void ReadCapabilities(RegistryKey hive, Dictionary<string, ExtensionEntry> extensions, List<AppEntry> apps)
    {
        try
        {
            using var registered = hive.OpenSubKey(@"Software\RegisteredApplications");
            if (registered == null)
                return;
            foreach (var name in registered.GetValueNames())
            {
                if (string.IsNullOrWhiteSpace(name))
                    continue;
                var capabilities = registered.GetValue(name) as string;
                if (string.IsNullOrWhiteSpace(capabilities))
                    continue;
                using var associations = hive.OpenSubKey(capabilities.Trim().TrimStart('\\') + @"\FileAssociations");
                if (associations == null)
                    continue;
                foreach (var extName in associations.GetValueNames())
                {
                    var extension = IconPaths.NormalizeExtension(extName);
                    var progId = associations.GetValue(extName) as string;
                    if (extension == null || string.IsNullOrWhiteSpace(progId))
                        continue;
                    var exe = IconPaths.ParseExe(ReadClassesDefault(progId + @"\shell\open\command"));
                    Entry(extensions, extension).Claims.Add(new CapabilityClaim(name, progId, exe));
                    AddApp(apps, name, exe, [extension]);
                }
            }
        }
        catch
        {
            // One hive can be unreadable without blocking the other.
        }
    }

    private static void ReadApplicationKeys(List<AppEntry> apps)
    {
        try
        {
            using var root = Registry.ClassesRoot.OpenSubKey("Applications");
            if (root == null)
                return;
            foreach (var exeName in root.GetSubKeyNames())
            {
                try
                {
                    using var app = root.OpenSubKey(exeName);
                    if (app == null)
                        continue;
                    var friendly = app.GetValue("FriendlyAppName") as string;
                    var command = app.OpenSubKey(@"shell\open\command")?.GetValue("") as string;
                    var exe = IconPaths.ParseExe(command);
                    var name = string.IsNullOrWhiteSpace(friendly) || friendly.StartsWith('@')
                        ? Path.GetFileNameWithoutExtension(exe ?? exeName)
                        : friendly;
                    var supported = app.OpenSubKey("SupportedTypes")?.GetValueNames()
                        .Select(IconPaths.NormalizeExtension)
                        .Where(item => item != null)
                        .Cast<string>()
                        .ToList() ?? [];
                    AddApp(apps, name, exe, supported);
                }
                catch
                {
                    // Skip a single unreadable application key.
                }
            }
        }
        catch
        {
            // Applications is optional.
        }
    }

    private static ExtensionEntry Entry(Dictionary<string, ExtensionEntry> extensions, string extension)
    {
        if (!extensions.TryGetValue(extension, out var entry))
        {
            entry = new ExtensionEntry(extension);
            extensions[extension] = entry;
        }
        return entry;
    }

    private static void AddApp(List<AppEntry> apps, string name, string? exe, IReadOnlyList<string> extensions)
    {
        AppEntry? match = null;
        if (!string.IsNullOrWhiteSpace(exe))
            match = apps.FirstOrDefault(app => IconPaths.SamePath(app.ExePath, exe));
        match ??= apps.FirstOrDefault(app =>
            app.Name.Equals(name, StringComparison.OrdinalIgnoreCase) &&
            (app.ExePath == null || exe == null || IconPaths.SamePath(app.ExePath, exe)));
        if (match == null)
        {
            match = new AppEntry(name, exe);
            apps.Add(match);
        }
        else if (PreferName(name, match.Name, match.ExePath ?? exe))
        {
            match.Name = name;
        }

        if (match.ExePath == null && !string.IsNullOrWhiteSpace(exe))
            match.ExePath = exe;
        foreach (var extension in extensions)
        {
            var normal = IconPaths.NormalizeExtension(extension);
            if (normal != null)
                match.Extensions.Add(normal);
        }
    }

    private static bool PreferName(string incoming, string current, string? exe)
    {
        if (string.IsNullOrWhiteSpace(current))
            return true;
        if (incoming.StartsWith('@'))
            return false;
        if (current.StartsWith('@'))
            return true;
        var file = string.IsNullOrWhiteSpace(exe) ? "" : Path.GetFileNameWithoutExtension(exe);
        return current.Equals(file, StringComparison.OrdinalIgnoreCase)
            && !incoming.Equals(file, StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchesApp(AppEntry app, string query)
    {
        if (app.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
            return true;
        if (app.ExePath?.Contains(query, StringComparison.OrdinalIgnoreCase) == true)
            return true;
        return FileStem(app.ExePath).Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private static string FileStem(string? exe)
    {
        return string.IsNullOrWhiteSpace(exe) ? "" : Path.GetFileNameWithoutExtension(exe);
    }

    private sealed class ExtensionEntry(string extension)
    {
        public string Extension { get; } = extension;
        public string? ClassProgId { get; set; }
        public string? UserChoiceProgId { get; set; }
        public List<CapabilityClaim> Claims { get; } = [];
    }

    private sealed record CapabilityClaim(string AppName, string ProgId, string? ExePath);

    private sealed class AppEntry(string name, string? exePath)
    {
        public string Name { get; set; } = name;
        public string? ExePath { get; set; } = exePath;
        public HashSet<string> Extensions { get; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
