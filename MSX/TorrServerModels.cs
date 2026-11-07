using System.Text.Json.Serialization;

namespace MSX.Models;

public sealed class TorrTorrentItem
{
    [JsonPropertyName("hash")]
    public string Hash { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("poster")]
    public string? Poster { get; set; }

    [JsonPropertyName("stat")]
    public int Stat { get; set; }

    [JsonPropertyName("stat_string")]
    public string? StatString { get; set; }

    [JsonPropertyName("torrent_size")]
    public long TorrentSize { get; set; }

    [JsonPropertyName("active_peers")]
    public int ActivePeers { get; set; }

    [JsonPropertyName("total_peers")]
    public int TotalPeers { get; set; }
}

public sealed class TorrStreamInfo
{
    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("torrent_size")]
    public long TorrentSize { get; set; }

    [JsonPropertyName("active_peers")]
    public int ActivePeers { get; set; }

    [JsonPropertyName("total_peers")]
    public int TotalPeers { get; set; }

    [JsonPropertyName("file_stats")]
    public List<TorrFileStat> FileStats { get; set; } = new();
}

public sealed class TorrFileStat
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("length")]
    public long Length { get; set; }

    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;
}

