namespace IconSwapper.Models;

public sealed class SearchResult
{
    public required string Title { get; init; }
    public required string Subtitle { get; init; }
    public string? IconSpec { get; init; }
    public required IReadOnlyList<string> Extensions { get; init; }
    public string? ExePath { get; init; }
    public bool IsApp { get; init; }
}

public sealed class ExtensionDetail
{
    public required string Extension { get; init; }
    public string? EffectiveProgId { get; init; }
    public string? TypeName { get; init; }
    public string? IconSpec { get; init; }
    public string? HandlerExe { get; init; }
    public string? HandlerName { get; init; }
    public required IReadOnlyList<string> ProgIds { get; init; }
}

public sealed class ExtensionOption
{
    public required ExtensionDetail Detail { get; init; }
    public required bool Shared { get; init; }
    public string Note { get; init; } = "";
}

public sealed class ApplyRequest
{
    public required IReadOnlyList<ExtensionDetail> Extensions { get; init; }
    public required Func<string, IReadOnlyList<string>> ExtensionsUsing { get; init; }
    public required string IcoPath { get; init; }
    public required bool UpdateShortcuts { get; init; }
    public required IReadOnlyList<string> ShortcutExes { get; init; }
}

public sealed class ApplyReport
{
    public List<string> Extensions { get; } = [];
    public List<string> Notes { get; } = [];
    public int ShortcutsUpdated { get; set; }
    public List<string> ShortcutsSkipped { get; } = [];
    public bool ShortcutsRequested { get; set; }
    public string Message { get; set; } = "";
}

public sealed class LibraryEntry
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required string Subtitle { get; init; }
    public string? IconSpec { get; init; }
    public required bool IsShortcut { get; init; }
}

public static class Presets
{
    public static readonly IReadOnlyList<Preset> All =
    [
        new("Documents", [".doc", ".docx", ".docm", ".dot", ".dotx", ".dotm", ".rtf", ".odt"]),
        new("Spreadsheets", [".xls", ".xlsx", ".xlsm", ".xlsb", ".ods", ".csv", ".tsv"]),
        new("Presentations", [".ppt", ".pptx", ".pptm", ".potx", ".ppsx"]),
        new("PDF", [".pdf"]),
    ];

    public sealed record Preset(string Name, string[] Extensions);
}
