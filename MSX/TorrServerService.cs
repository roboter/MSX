using MSX.Models;
using System.Net.Http.Json;
using System.Text.Json;

namespace MSX;

public sealed class TorrServerService
{
    private readonly HttpClient _httpClient;

    public TorrServerService(HttpClient httpClient)
    {
        _httpClient = httpClient;
        _httpClient.Timeout = TimeSpan.FromSeconds(10);
    }

    public static string NormalizeHost(string host)
    {
        if (string.IsNullOrWhiteSpace(host)) return string.Empty;
        var trimmed = host.Trim();
        if (!trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = "http://" + trimmed;
        }
        return trimmed.TrimEnd('/');
    }

    public async Task<string?> CheckTorrServerAsync(string? torrHost, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(torrHost)) return null;
        var baseUri = NormalizeHost(torrHost);
        try
        {
            var res = await _httpClient.GetStringAsync($"{baseUri}/echo", ct);
            return res.Trim();
        }
        catch
        {
            return null;
        }
    }

    public async Task<List<TorrTorrentItem>> GetTorrentsAsync(string? torrHost, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(torrHost)) return [];
        var baseUri = NormalizeHost(torrHost);

        try
        {
            var req = JsonContent.Create(new { action = "list" });
            var response = await _httpClient.PostAsync($"{baseUri}/torrents", req, ct);
            if (!response.IsSuccessStatusCode) return [];

            var torrents = await response.Content.ReadFromJsonAsync<List<TorrTorrentItem>>(cancellationToken: ct);
            return torrents ?? [];
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[TorrServer] Error fetching torrents: {ex.Message}");
            return [];
        }
    }

    public async Task<TorrStreamInfo?> GetStreamInfoAsync(string? torrHost, string link, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(torrHost)) return null;
        var baseUri = NormalizeHost(torrHost);

        try
        {
            var url = $"{baseUri}/stream/?stat&link={Uri.EscapeDataString(link)}";
            return await _httpClient.GetFromJsonAsync<TorrStreamInfo>(url, ct);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[TorrServer] Error fetching stream info for {link}: {ex.Message}");
            return null;
        }
    }

    public async Task<bool> AddTorrentAsync(string? torrHost, string link, string? title = null, string? poster = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(torrHost)) return false;
        var baseUri = NormalizeHost(torrHost);

        try
        {
            var req = JsonContent.Create(new
            {
                action = "add",
                link,
                title,
                poster,
                save_to_db = true
            });
            var response = await _httpClient.PostAsync($"{baseUri}/torrents", req, ct);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[TorrServer] Error adding torrent: {ex.Message}");
            return false;
        }
    }

    public async Task<bool> RemoveTorrentAsync(string? torrHost, string hash, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(torrHost)) return false;
        var baseUri = NormalizeHost(torrHost);

        try
        {
            var req = JsonContent.Create(new { action = "rem", hash });
            var response = await _httpClient.PostAsync($"{baseUri}/torrents", req, ct);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[TorrServer] Error removing torrent: {ex.Message}");
            return false;
        }
    }

    public async Task<bool> DropTorrentAsync(string? torrHost, string hash, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(torrHost)) return false;
        var baseUri = NormalizeHost(torrHost);

        try
        {
            var req = JsonContent.Create(new { action = "drop", hash });
            var response = await _httpClient.PostAsync($"{baseUri}/torrents", req, ct);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[TorrServer] Error dropping torrent: {ex.Message}");
            return false;
        }
    }
}

