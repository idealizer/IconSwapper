using System.Windows.Media.Imaging;

namespace IconSwapper.Models;

public sealed class SearchHit : Observable
{
    public required string Title { get; init; }
    public required string Subtitle { get; init; }
    public string? IconSpec { get; init; }
    public required IReadOnlyList<string> Extensions { get; init; }
    public string? ExePath { get; init; }
    public bool IsApp { get; init; }

    private BitmapSource? _icon;
    public BitmapSource? Icon
    {
        get => _icon;
        set => Set(ref _icon, value);
    }
}

public sealed class ExtensionChoice : Observable
{
    public required ExtensionDetail Detail { get; init; }
    public string Note { get; init; } = "";

    private bool _isChecked = true;
    public bool IsChecked
    {
        get => _isChecked;
        set => Set(ref _isChecked, value);
    }

    public string Extension => Detail.Extension;
}

public sealed class LibraryItem : Observable
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required string Subtitle { get; init; }
    public string? IconSpec { get; init; }
    public required bool IsShortcut { get; init; }

    private BitmapSource? _icon;
    public BitmapSource? Icon
    {
        get => _icon;
        set => Set(ref _icon, value);
    }
}
