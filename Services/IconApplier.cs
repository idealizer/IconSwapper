using System.Text.Json;
using IconSwapper.Models;
using Microsoft.Win32;

namespace IconSwapper.Services;

public static class IconApplier
{
    public static string? LoadWarning { get; private set; }

    public static ApplyReport Apply(ApplyRequest request)
    {
        var report = new ApplyReport { ShortcutsRequested = request.UpdateShortcuts };
        var checkedSet = request.Extensions
            .Select(detail => detail.Extension)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var store = Load();
        var planned = new List<(string Extension, string Key)>();
        var noted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var detail in request.Extensions)
        {
            planned.Add((detail.Extension, ExtensionKey(detail.Extension)));
            foreach (var progId in detail.ProgIds)
            {
                if (!IconPaths.IsSafeProgId(progId))
                    continue;
                var users = request.ExtensionsUsing(progId);
                var missing = users.Where(user => !checkedSet.Contains(user)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (missing.Count > 0)
                {
                    if (noted.Add(progId))
                    {
                        report.Notes.Add(
                            "Left " + progId + " unchanged so " + JoinEnglish(missing) + " keep their icons.");
                    }
                    continue;
                }

                planned.Add((detail.Extension, ProgIdKey(progId)));
            }
        }

        var iconValue = IconPaths.IconValue(request.IcoPath);
        var uniqueKeys = planned.Select(item => item.Key).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var key in uniqueKeys)
        {
            EnsureIconKey(key);
            if (!store.Keys.TryGetValue(key, out var state))
            {
                state = ReadState(key);
                if (state.Unsupported)
                {
                    report.Notes.Add("Left one icon unchanged because it is not stored as text.");
                    continue;
                }

                store.Keys[key] = state;
            }

            if (state.Unsupported)
                continue;
            WriteIcon(key, iconValue);
        }

        foreach (var group in planned.GroupBy(item => item.Extension, StringComparer.OrdinalIgnoreCase))
        {
            var extension = group.Key;
            var created = store.Extensions.TryGetValue(extension, out var existing)
                ? existing.CreatedExtensionKey
                : !ExtensionKeyExists(extension);
            var usable = group
                .Select(item => item.Key)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(key => store.Keys.TryGetValue(key, out var state) && !state.Unsupported)
                .ToList();
            store.Extensions[extension] = new ExtensionRecord
            {
                IcoPath = request.IcoPath,
                KeyPaths = usable,
                AppliedUtc = DateTime.UtcNow.ToString("o"),
                CreatedExtensionKey = created,
            };
            report.Extensions.Add(extension);
        }

        if (request.UpdateShortcuts)
        {
            foreach (var exe in request.ShortcutExes.Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var exeKey = IconPaths.LongPath(exe);
                if (!store.Shortcuts.TryGetValue(exeKey, out var shortcut))
                {
                    shortcut = new ShortcutRecord();
                    store.Shortcuts[exeKey] = shortcut;
                }

                shortcut.IcoPath = request.IcoPath;
                var updated = ShortcutUpdater.Apply(exe, request.IcoPath, shortcut.Links);
                report.ShortcutsUpdated += updated.Updated;
                report.ShortcutsSkipped.AddRange(updated.Skipped);
                if (shortcut.Links.Count == 0)
                    store.Shortcuts.Remove(exeKey);
                if (updated.Updated == 0 && !File.Exists(exe))
                    report.Notes.Add(Path.GetFileName(exe) + " was not found, so its shortcuts were left alone.");
            }
        }

        Save(store);
        report.Message = BuildMessage(report);
        return report;
    }

    public static string RestoreExtension(string extension)
    {
        extension = IconPaths.NormalizeExtension(extension)
            ?? throw new InvalidOperationException("That is not a file extension.");
        var store = Load();
        if (!store.Extensions.TryGetValue(extension, out var record))
            return extension + " is already using its original icon.";

        store.Extensions.Remove(extension);
        foreach (var key in record.KeyPaths)
        {
            var stillUsed = store.Extensions.Values.Any(other =>
                other.KeyPaths.Contains(key, StringComparer.OrdinalIgnoreCase));
            if (stillUsed || !store.Keys.TryGetValue(key, out var state))
                continue;
            RestoreKey(key, state);
            store.Keys.Remove(key);
        }

        if (record.CreatedExtensionKey)
            RemoveEmptyExtensionKey(extension);

        var ico = record.IcoPath;
        DeleteIcoIfUnused(store, ico);
        Save(store);
        return "Restored " + extension + ".";
    }

    public static string RestoreShortcuts(string exePath)
    {
        var store = Load();
        var exeKey = IconPaths.LongPath(exePath);
        if (!store.Shortcuts.TryGetValue(exeKey, out var record))
            return "Those shortcuts are already using their original icons.";

        var errors = ShortcutUpdater.Restore(record.Links);
        store.Shortcuts.Remove(exeKey);
        DeleteIcoIfUnused(store, record.IcoPath);
        Save(store);
        if (errors.Count == 0)
            return "Restored shortcuts for " + Path.GetFileName(exePath) + ".";
        return "Restored some shortcuts for " + Path.GetFileName(exePath) + ". Could not change " + string.Join(", ", errors) + ".";
    }

    public static bool HasExtension(string extension)
    {
        extension = IconPaths.NormalizeExtension(extension) ?? extension;
        return Load().Extensions.ContainsKey(extension);
    }

    public static IReadOnlyList<LibraryEntry> List()
    {
        var store = Load();
        var entries = new List<LibraryEntry>();
        foreach (var pair in store.Extensions.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase))
        {
            entries.Add(new LibraryEntry
            {
                Id = pair.Key,
                Title = pair.Key,
                Subtitle = "Set " + FormatWhen(pair.Value.AppliedUtc),
                IconSpec = string.IsNullOrWhiteSpace(pair.Value.IcoPath) ? null : pair.Value.IcoPath + ",0",
                IsShortcut = false,
            });
        }

        foreach (var pair in store.Shortcuts.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase))
        {
            var count = pair.Value.Links.Count;
            entries.Add(new LibraryEntry
            {
                Id = pair.Key,
                Title = Path.GetFileNameWithoutExtension(pair.Key) + " shortcuts",
                Subtitle = count == 1 ? "1 shortcut" : count + " shortcuts",
                IconSpec = string.IsNullOrWhiteSpace(pair.Value.IcoPath) ? null : pair.Value.IcoPath + ",0",
                IsShortcut = true,
            });
        }

        return entries;
    }

    private static string BuildMessage(ApplyReport report)
    {
        var parts = new List<string>();
        if (report.Extensions.Count > 0)
            parts.Add("Updated " + JoinEnglish(report.Extensions) + ".");
        else
            parts.Add("No file types were changed.");

        if (report.ShortcutsRequested)
        {
            if (report.ShortcutsUpdated > 0)
            {
                parts.Add(report.ShortcutsUpdated == 1
                    ? "1 shortcut now uses the new icon."
                    : report.ShortcutsUpdated + " shortcuts now use the new icon.");
            }
            else
            {
                parts.Add("No matching shortcuts were found in your Start menu, desktop, or taskbar.");
            }

            if (report.ShortcutsSkipped.Count > 0)
                parts.Add("Skipped " + JoinEnglish(report.ShortcutsSkipped.Distinct().Take(4).ToList()) + ".");
        }

        if (report.Notes.Count > 0)
            parts.Add(string.Join(" ", report.Notes.Take(4)));
        return string.Join(" ", parts);
    }

    private static string JoinEnglish(IReadOnlyList<string> items)
    {
        if (items.Count == 0)
            return "";
        if (items.Count == 1)
            return items[0];
        if (items.Count == 2)
            return items[0] + " and " + items[1];
        return string.Join(", ", items.Take(items.Count - 1)) + ", and " + items[^1];
    }

    private static string FormatWhen(string utc)
    {
        if (!DateTime.TryParse(utc, null, System.Globalization.DateTimeStyles.RoundtripKind, out var parsed))
            return "earlier";
        return parsed.ToLocalTime().ToString("g");
    }

    private static KeyRecord ReadState(string subKey)
    {
        using var key = Registry.CurrentUser.OpenSubKey(subKey);
        if (key == null)
            return new KeyRecord { Existed = false, ValueKind = (int)RegistryValueKind.String };
        try
        {
            var kind = key.GetValueKind("");
            if (kind is not RegistryValueKind.String and not RegistryValueKind.ExpandString)
                return new KeyRecord { Existed = true, Unsupported = true, ValueKind = (int)kind };
            var raw = key.GetValue("", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
            return new KeyRecord { Existed = true, PreviousValue = raw, ValueKind = (int)kind };
        }
        catch (InvalidOperationException)
        {
            return new KeyRecord { Existed = true, ValueKind = (int)RegistryValueKind.String };
        }
    }

    private static void WriteIcon(string subKey, string iconValue)
    {
        using var key = Registry.CurrentUser.CreateSubKey(subKey, writable: true)
            ?? throw new InvalidOperationException("Could not save the icon.");
        key.SetValue("", iconValue, RegistryValueKind.String);
    }

    private static void RestoreKey(string subKey, KeyRecord record)
    {
        if (!record.Existed)
        {
            try
            {
                Registry.CurrentUser.DeleteSubKey(subKey, throwOnMissingSubKey: false);
            }
            catch (InvalidOperationException)
            {
                // Leave a key that gained extra values after we created it.
            }
            return;
        }

        using var key = Registry.CurrentUser.CreateSubKey(subKey, writable: true);
        if (key == null)
            return;
        if (record.PreviousValue == null)
        {
            key.DeleteValue("", throwOnMissingValue: false);
            return;
        }

        var kind = record.ValueKind == (int)RegistryValueKind.ExpandString
            ? RegistryValueKind.ExpandString
            : RegistryValueKind.String;
        key.SetValue("", record.PreviousValue, kind);
    }

    private static bool ExtensionKeyExists(string extension)
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Classes\" + extension);
        return key != null;
    }

    private static void RemoveEmptyExtensionKey(string extension)
    {
        var path = @"Software\Classes\" + extension;
        var empty = false;
        using (var key = Registry.CurrentUser.OpenSubKey(path, writable: true))
        {
            empty = key != null && key.SubKeyCount == 0 && key.ValueCount == 0;
        }

        if (empty)
            Registry.CurrentUser.DeleteSubKey(path, throwOnMissingSubKey: false);
    }

    private static void DeleteIcoIfUnused(AssignmentStore store, string? ico)
    {
        if (string.IsNullOrWhiteSpace(ico))
            return;
        var root = Path.GetFullPath(IconDirectory());
        string full;
        try
        {
            full = Path.GetFullPath(ico);
        }
        catch
        {
            return;
        }

        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            return;
        var used = store.Extensions.Values.Any(record => string.Equals(record.IcoPath, ico, StringComparison.OrdinalIgnoreCase))
            || store.Shortcuts.Values.Any(record => string.Equals(record.IcoPath, ico, StringComparison.OrdinalIgnoreCase));
        if (used || !File.Exists(full))
            return;
        try
        {
            File.Delete(full);
        }
        catch
        {
            // Explorer may still be showing the file. The registry no longer points at it.
        }
    }

    private static string ExtensionKey(string extension) => @"Software\Classes\" + extension + @"\DefaultIcon";

    private static string ProgIdKey(string progId) => @"Software\Classes\" + progId + @"\DefaultIcon";

    private static void EnsureIconKey(string key)
    {
        if (!key.StartsWith(@"Software\Classes\", StringComparison.OrdinalIgnoreCase)
            || !key.EndsWith(@"\DefaultIcon", StringComparison.OrdinalIgnoreCase)
            || key.Contains("..", StringComparison.Ordinal))
            throw new InvalidOperationException("Refusing to write outside per-user file icons.");
    }

    private static string StorePath() => Path.Combine(IconPaths.AppDataRoot(), "assignments.json");

    private static string IconDirectory() => Path.Combine(IconPaths.AppDataRoot(), "icons");

    private static AssignmentStore Load()
    {
        var path = StorePath();
        if (!File.Exists(path))
            return new AssignmentStore();
        try
        {
            var json = File.ReadAllText(path);
            var store = JsonSerializer.Deserialize<AssignmentStore>(json, JsonOptions) ?? new AssignmentStore();
            store.Extensions = new Dictionary<string, ExtensionRecord>(store.Extensions ?? [], StringComparer.OrdinalIgnoreCase);
            store.Keys = new Dictionary<string, KeyRecord>(store.Keys ?? [], StringComparer.OrdinalIgnoreCase);
            store.Shortcuts = new Dictionary<string, ShortcutRecord>(store.Shortcuts ?? [], StringComparer.OrdinalIgnoreCase);
            foreach (var record in store.Extensions.Values)
            {
                record.KeyPaths ??= [];
            }
            foreach (var record in store.Shortcuts.Values)
            {
                record.Links ??= [];
            }
            return store;
        }
        catch (JsonException)
        {
            try
            {
                File.Copy(path, path + ".bak", overwrite: true);
            }
            catch
            {
                // The unreadable file stays put if it cannot be copied.
            }

            LoadWarning = "The saved icon list was unreadable, so IconSwapper started a new one.";
            return new AssignmentStore();
        }
    }

    private static void Save(AssignmentStore store)
    {
        var path = StorePath();
        var json = JsonSerializer.Serialize(store, JsonOptions);
        File.WriteAllText(path, json);
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private sealed class AssignmentStore
    {
        public int Version { get; set; } = 1;
        public Dictionary<string, ExtensionRecord> Extensions { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, KeyRecord> Keys { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, ShortcutRecord> Shortcuts { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class ExtensionRecord
    {
        public string IcoPath { get; set; } = "";
        public List<string> KeyPaths { get; set; } = [];
        public string AppliedUtc { get; set; } = "";
        public bool CreatedExtensionKey { get; set; }
    }

    private sealed class KeyRecord
    {
        public bool Existed { get; set; }
        public string? PreviousValue { get; set; }
        public int ValueKind { get; set; }
        public bool Unsupported { get; set; }
    }

    private sealed class ShortcutRecord
    {
        public string IcoPath { get; set; } = "";
        public List<LinkRecord> Links { get; set; } = [];
    }
}
