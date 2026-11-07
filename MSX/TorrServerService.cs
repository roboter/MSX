using MSX.Models;
using System.Net.Http.Json;
using System.Text.Json;

namespace MSX;

public sealed class TorrServerService
{
    private static readonly HttpClient SharedHttpClient = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        ConnectTimeout = TimeSpan.FromSeconds(2)
    })
    {
        Timeout = TimeSpan.FromSeconds(5)
    };

    private readonly HttpClient _httpClient;
    private string? _cachedVersion;
    private DateTime _lastCheckTime = DateTime.MinValue;
    private readonly TimeSpan _cacheDuration = TimeSpan.FromSeconds(30);

    public TorrServerService(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? SharedHttpClient;
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

    public async Task<string?> CheckTorrServerAsync(string? torrHost, bool forceRefresh = false, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(torrHost)) return null;

        var now = DateTime.UtcNow;
        if (!forceRefresh && (now - _lastCheckTime) < _cacheDuration)
        {
            return _cachedVersion;
        }

        var baseUri = NormalizeHost(torrHost);
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(1));
            var res = await _httpClient.GetStringAsync($"{baseUri}/echo", cts.Token);
            _cachedVersion = res.Trim();
            _lastCheckTime = DateTime.UtcNow;
            return _cachedVersion;
        }
        catch
        {
            _cachedVersion = null;
            _lastCheckTime = DateTime.UtcNow;
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
        catch
        {
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
        catch
        {
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
        catch
        {
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
        catch
        {
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
        catch
        {
            return false;
        }
    }
}
