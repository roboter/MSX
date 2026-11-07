using System.Text.Json;
using System.Text.Json.Serialization;

namespace MSX;

public sealed class WebmarkItem
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = "video"; // "youtube", "stream", "video"
}

public sealed class WebmarksService
{
    private readonly string _filePath;
    private readonly object _lock = new();

    public WebmarksService(string? baseDir = null)
    {
        var dir = baseDir ?? AppContext.BaseDirectory;
        _filePath = Path.Combine(dir, "webmarks.json");
        EnsureInitialized();
    }

    private void EnsureInitialized()
    {
        lock (_lock)
        {
            if (!File.Exists(_filePath))
            {
                var defaults = new List<WebmarkItem>
                {
                    new()
                    {
                        Id = "1",
                        Title = "Big Buck Bunny (HLS Stream)",
                        Url = "https://test-streams.mux.dev/x36xhzz/x36xhzz.m3u8",
                        Type = "stream"
                    },
                    new()
                    {
                        Id = "2",
                        Title = "Sintel (Direct MP4)",
                        Url = "https://commondatastorage.googleapis.com/gtv-videos-bucket/sample/Sintel.mp4",
                        Type = "video"
                    },
                    new()
                    {
                        Id = "3",
                        Title = "MediaStation X Trailer (YouTube)",
                        Url = "dQw4w9WgXcQ", // YouTube Video ID
                        Type = "youtube"
                    }
                };
                SaveWebmarks(defaults);
            }
        }
    }

    public List<WebmarkItem> GetWebmarks()
    {
        lock (_lock)
        {
            if (!File.Exists(_filePath)) return [];
            try
            {
                var json = File.ReadAllText(_filePath);
                return JsonSerializer.Deserialize<List<WebmarkItem>>(json) ?? [];
            }
            catch
            {
                return [];
            }
        }
    }

    public void AddWebmark(string title, string url, string? type = null)
    {
        lock (_lock)
        {
            var list = GetWebmarks();
            var detectedType = type;
            if (string.IsNullOrEmpty(detectedType))
            {
                if (url.Contains("youtube.com") || url.Contains("youtu.be") || (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase) && url.Length == 11))
                {
                    detectedType = "youtube";
                }
                else if (url.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase))
                {
                    detectedType = "stream";
                }
                else
                {
                    detectedType = "video";
                }
            }

            list.Add(new WebmarkItem
            {
                Id = Guid.NewGuid().ToString("N")[..8],
                Title = string.IsNullOrWhiteSpace(title) ? url : title,
                Url = url,
                Type = detectedType
            });
            SaveWebmarks(list);
        }
    }

    public bool RemoveWebmark(string id)
    {
        lock (_lock)
        {
            var list = GetWebmarks();
            var removed = list.RemoveAll(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) > 0;
            if (removed)
            {
                SaveWebmarks(list);
            }
            return removed;
        }
    }

    public void ClearWebmarks()
    {
        lock (_lock)
        {
            SaveWebmarks([]);
        }
    }

    private void SaveWebmarks(List<WebmarkItem> items)
    {
        var json = JsonSerializer.Serialize(items, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_filePath, json);
    }
}

