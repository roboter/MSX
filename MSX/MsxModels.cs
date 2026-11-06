using System.Text.Json.Serialization;

namespace MSX.Models;

public sealed class MsxRoot
{
    [JsonPropertyName("headline")]
    public string Headline { get; set; } = "TorrentBot Media Library";

    [JsonPropertyName("menu")]
    public List<MsxMenuItem> Menu { get; set; } = new();
}

public sealed class MsxMenuItem
{
    [JsonPropertyName("icon")]
    public string Icon { get; set; } = "movie";

    [JsonPropertyName("label")]
    public string Label { get; set; } = string.Empty;

    [JsonPropertyName("data")]
    public MsxMenuData Data { get; set; } = new();
}

public sealed class MsxMenuData
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "pages";

    [JsonPropertyName("template")]
    public MsxTemplate Template { get; set; } = new();

    [JsonPropertyName("items")]
    public List<MsxMediaItem> Items { get; set; } = new();
}

public sealed class MsxTemplate
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "separate";

    [JsonPropertyName("layout")]
    public string Layout { get; set; } = "0,0,2,4";

    [JsonPropertyName("icon")]
    public string Icon { get; set; } = "msx-white-soft:movie";

    [JsonPropertyName("color")]
    public string Color { get; set; } = "msx-glass";

    [JsonPropertyName("imageFiller")]
    public string ImageFiller { get; set; } = "cover";
}

public sealed class MsxMediaItem
{
    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("action")]
    public string Action { get; set; } = string.Empty;

    [JsonPropertyName("image")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Image { get; set; }
}