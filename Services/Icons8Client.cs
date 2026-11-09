using System.Net.Http;
using System.Text.Json;

namespace IconSwapper.Services;

public sealed class Icons8Icon
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Platform { get; init; }
    public string Thumbnail => Icons8Client.PngUrl(Id, 64);
    public string Tip => Name + " · " + Platform;
}

public sealed record Icons8Style(string Label, string Code);

public static class Icons8Client
{
    private const string PageSize = "30";

    public static readonly IReadOnlyList<Icons8Style> Styles =
    [
        new("Any style", ""),
        new("Color", "color"),
        new("Fluent", "fluency"),
        new("iOS filled", "ios_filled"),
        new("Windows", "win10"),
        new("Office", "office40"),
        new("Plasticine", "plasticine"),
        new("Stickers", "stickers"),
        new("Dusk", "dusk"),
        new("Papercut", "papercut"),
        new("Emoji", "emoji"),
    ];

    private static readonly HttpClient Http = CreateClient();

    public static string PngUrl(string id, int size) =>
        $"https://img.icons8.com/?size={size}&id={Uri.EscapeDataString(id)}&format=png";

    public static async Task<(List<Icons8Icon> Icons, int Total)> SearchAsync(string term, string style, int offset)
    {
        var url = "https://search.icons8.com/api/iconsets/v5/search?term=" + Uri.EscapeDataString(term)
            + "&amount=" + PageSize + "&offset=" + offset;
        if (!string.IsNullOrWhiteSpace(style))
            url += "&platform=" + Uri.EscapeDataString(style);

        using var stream = await Http.GetStreamAsync(url);
        using var json = await JsonDocument.ParseAsync(stream);
        var root = json.RootElement;
        var total = root.TryGetProperty("parameters", out var parameters)
            && parameters.TryGetProperty("countAll", out var count)
            && count.TryGetInt32(out var value) ? value : 0;

        var icons = new List<Icons8Icon>();
        if (root.TryGetProperty("icons", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in list.EnumerateArray())
            {
                var id = item.TryGetProperty("id", out var idValue) ? idValue.GetString() : null;
                if (string.IsNullOrWhiteSpace(id))
                    continue;
                icons.Add(new Icons8Icon
                {
                    Id = id,
                    Name = item.TryGetProperty("name", out var name) ? name.GetString() ?? id : id,
                    Platform = item.TryGetProperty("platform", out var platform) ? platform.GetString() ?? "" : "",
                });
            }
        }

        return (icons, total);
    }

    public static async Task<string> DownloadAsync(Icons8Icon icon)
    {
        var dir = Path.Combine(IconPaths.AppDataRoot(), "downloads");
        Directory.CreateDirectory(dir);
        var safeId = new string(icon.Id.Where(char.IsLetterOrDigit).ToArray());
        var safeName = new string(icon.Name.ToLowerInvariant()
            .Select(ch => char.IsLetterOrDigit(ch) ? ch : '-')
            .ToArray()).Trim('-');
        var file = Path.Combine(dir, $"icons8-{safeName}-{safeId}.png");
        if (!File.Exists(file))
        {
            var bytes = await Http.GetByteArrayAsync(PngUrl(icon.Id, 256));
            await File.WriteAllBytesAsync(file, bytes);
        }

        return file;
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("IconSwapper/1.1");
        return client;
    }
}
