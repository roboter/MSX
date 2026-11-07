using Microsoft.Extensions.Options;
using System.Diagnostics;
using System.Text.Json;

namespace MSX;

public sealed class CoverExtractorService
{
    private static readonly HttpClient SharedHttpClient = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        ConnectTimeout = TimeSpan.FromSeconds(3)
    })
    {
        Timeout = TimeSpan.FromSeconds(10)
    };

    private readonly HttpClient _httpClient;
    private readonly MediaSettings _mediaSettings;
    private static bool _ffmpegAvailable;
    private static bool _checkedFFmpeg;
    private static readonly object _ffmpegLock = new();

    public CoverExtractorService(IOptions<MediaSettings> mediaSettings, HttpClient? httpClient = null)
    {
        _mediaSettings = mediaSettings.Value;
        _httpClient = httpClient ?? SharedHttpClient;
    }

    public static bool CheckFFmpegAvailable()
    {
        lock (_ffmpegLock)
        {
            if (_checkedFFmpeg) return _ffmpegAvailable;
            try
            {
                using var proc = Process.Start(new ProcessStartInfo
                {
                    FileName = "ffmpeg",
                    Arguments = "-version",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
                proc?.WaitForExit(3000);
                _ffmpegAvailable = proc?.ExitCode == 0;
            }
            catch
            {
                _ffmpegAvailable = false;
            }
            _checkedFFmpeg = true;
            return _ffmpegAvailable;
        }
    }

    public async Task<bool> ExtractCoverWithFFmpegAsync(string videoPath, string targetJpgPath, CancellationToken ct = default)
    {
        if (!CheckFFmpegAvailable()) return false;

        // 1. Try extracting embedded cover art stream (mimetype: image/jpeg)
        try
        {
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = "ffmpeg",
                Arguments = $"-y -i \"{videoPath}\" -map 0:v:m:mimetype:image/jpeg -c copy \"{targetJpgPath}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (proc != null)
            {
                await proc.WaitForExitAsync(ct);
                if (proc.ExitCode == 0 && File.Exists(targetJpgPath) && new FileInfo(targetJpgPath).Length > 1000)
                {
                    return true;
                }
            }
        }
        catch { }

        // 2. Try extracting attached picture (common in MKV/MP4 files)
        try
        {
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = "ffmpeg",
                Arguments = $"-y -i \"{videoPath}\" -an -vcodec copy \"{targetJpgPath}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (proc != null)
            {
                await proc.WaitForExitAsync(ct);
                if (proc.ExitCode == 0 && File.Exists(targetJpgPath) && new FileInfo(targetJpgPath).Length > 1000)
                {
                    return true;
                }
            }
        }
        catch { }

        // 3. Fallback: Extract video thumbnail frame at 00:03:00
        try
        {
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = "ffmpeg",
                Arguments = $"-y -ss 00:03:00 -i \"{videoPath}\" -vframes 1 -q:v 2 \"{targetJpgPath}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (proc != null)
            {
                await proc.WaitForExitAsync(ct);
                if (proc.ExitCode == 0 && File.Exists(targetJpgPath) && new FileInfo(targetJpgPath).Length > 1000)
                {
                    return true;
                }
            }
        }
        catch { }

        // 4. Fallback: Try at 30 seconds for short videos
        try
        {
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = "ffmpeg",
                Arguments = $"-y -ss 00:00:30 -i \"{videoPath}\" -vframes 1 -q:v 2 \"{targetJpgPath}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (proc != null)
            {
                await proc.WaitForExitAsync(ct);
                if (proc.ExitCode == 0 && File.Exists(targetJpgPath) && new FileInfo(targetJpgPath).Length > 1000)
                {
                    return true;
                }
            }
        }
        catch { }

        return false;
    }

    public async Task<string?> FindOnlinePosterUrlAsync(string rawFileName, CancellationToken ct = default)
    {
        var parsed = MovieTitleParser.Parse(rawFileName);
        if (string.IsNullOrWhiteSpace(parsed.Title)) return null;

        // 1. If TV show or has season info: Query TVMaze (Free, no key required)
        if (parsed.IsTv || parsed.Season.HasValue)
        {
            try
            {
                var tvUrl = $"https://api.tvmaze.com/singlesearch/shows?q={Uri.EscapeDataString(parsed.Title)}";
                using var res = await _httpClient.GetAsync(tvUrl, ct);
                if (res.IsSuccessStatusCode)
                {
                    using var doc = await JsonDocument.ParseAsync(await res.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
                    if (doc.RootElement.TryGetProperty("image", out var imgElem))
                    {
                        if (imgElem.TryGetProperty("original", out var origElem) && origElem.GetString() is { } orig)
                            return orig;
                        if (imgElem.TryGetProperty("medium", out var medElem) && medElem.GetString() is { } med)
                            return med;
                    }
                }
            }
            catch { }
        }

        // 2. Query TMDB if key is provided
        if (!string.IsNullOrWhiteSpace(_mediaSettings.TmdbApiKey))
        {
            try
            {
                var yearParam = parsed.Year.HasValue ? $"&year={parsed.Year.Value}" : "";
                var tmdbUrl = $"https://api.themoviedb.org/3/search/multi?query={Uri.EscapeDataString(parsed.Title)}{yearParam}&api_key={_mediaSettings.TmdbApiKey}&language=ru-RU";
                using var res = await _httpClient.GetAsync(tmdbUrl, ct);
                if (res.IsSuccessStatusCode)
                {
                    using var doc = await JsonDocument.ParseAsync(await res.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
                    if (doc.RootElement.TryGetProperty("results", out var results) && results.GetArrayLength() > 0)
                    {
                        var first = results[0];
                        if (first.TryGetProperty("poster_path", out var pPath) && pPath.GetString() is { } p && !string.IsNullOrEmpty(p))
                        {
                            return $"https://image.tmdb.org/t/p/w500{p}";
                        }
                    }
                }
            }
            catch { }
        }

        // 3. Fallback: Query TVMaze for shows or movies with show counterparts
        try
        {
            var tvUrl = $"https://api.tvmaze.com/singlesearch/shows?q={Uri.EscapeDataString(parsed.Title)}";
            using var res = await _httpClient.GetAsync(tvUrl, ct);
            if (res.IsSuccessStatusCode)
            {
                using var doc = await JsonDocument.ParseAsync(await res.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
                if (doc.RootElement.TryGetProperty("image", out var imgElem))
                {
                    if (imgElem.TryGetProperty("original", out var origElem) && origElem.GetString() is { } orig)
                        return orig;
                    if (imgElem.TryGetProperty("medium", out var medElem) && medElem.GetString() is { } med)
                        return med;
                }
            }
        }
        catch { }

        return null;
    }

    public async Task<bool> EnsureCoverAsync(string videoFilePath, CancellationToken ct = default)
    {
        var dir = Path.GetDirectoryName(videoFilePath);
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return false;

        var baseName = Path.GetFileNameWithoutExtension(videoFilePath);
        var targetJpg = Path.Combine(dir, baseName + ".jpg");

        // Already has cover
        if (File.Exists(targetJpg) && new FileInfo(targetJpg).Length > 1000)
        {
            return true;
        }

        // Check if other image extensions exist
        foreach (var ext in new[] { ".png", ".jpeg", ".webp" })
        {
            var existing = Path.Combine(dir, baseName + ext);
            if (File.Exists(existing) && new FileInfo(existing).Length > 1000)
            {
                return true;
            }
        }

        // 1. Try online search first (for official high-res posters)
        try
        {
            var posterUrl = await FindOnlinePosterUrlAsync(baseName, ct);
            if (!string.IsNullOrEmpty(posterUrl))
            {
                var imageBytes = await _httpClient.GetByteArrayAsync(posterUrl, ct);
                if (imageBytes.Length > 1000)
                {
                    await File.WriteAllBytesAsync(targetJpg, imageBytes, ct);
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine($"[CoverExtractor] Downloaded online poster for '{baseName}'");
                    Console.ResetColor();
                    return true;
                }
            }
        }
        catch { }

        // 2. Try FFmpeg extraction (embedded cover or crisp video frame)
        if (CheckFFmpegAvailable())
        {
            var extracted = await ExtractCoverWithFFmpegAsync(videoFilePath, targetJpg, ct);
            if (extracted)
            {
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine($"[CoverExtractor] Extracted FFmpeg cover for '{baseName}'");
                Console.ResetColor();
                return true;
            }
        }

        return false;
    }

    public async Task EnsureFolderCoverAsync(string folderPath, CancellationToken ct = default)
    {
        var posterFile = Path.Combine(folderPath, "folder.jpg");
        if (File.Exists(posterFile) && new FileInfo(posterFile).Length > 1000) return;

        foreach (var name in new[] { "cover.jpg", "poster.jpg", Path.GetFileName(folderPath) + ".jpg" })
        {
            var p = Path.Combine(folderPath, name);
            if (File.Exists(p) && new FileInfo(p).Length > 1000) return;
        }

        var folderName = Path.GetFileName(folderPath);

        // Try online search for the folder name
        try
        {
            var posterUrl = await FindOnlinePosterUrlAsync(folderName, ct);
            if (!string.IsNullOrEmpty(posterUrl))
            {
                var imageBytes = await _httpClient.GetByteArrayAsync(posterUrl, ct);
                if (imageBytes.Length > 1000)
                {
                    await File.WriteAllBytesAsync(posterFile, imageBytes, ct);
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine($"[CoverExtractor] Downloaded folder poster for '{folderName}'");
                    Console.ResetColor();
                    return;
                }
            }
        }
        catch { }

        // Copy cover from first video if available
        try
        {
            var firstVideo = Directory.EnumerateFiles(folderPath, "*.*", SearchOption.TopDirectoryOnly)
                .FirstOrDefault(f => _mediaSettings.AllowedExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase));

            if (firstVideo != null)
            {
                var videoJpg = Path.Combine(folderPath, Path.GetFileNameWithoutExtension(firstVideo) + ".jpg");
                if (File.Exists(videoJpg) && new FileInfo(videoJpg).Length > 1000)
                {
                    File.Copy(videoJpg, posterFile, overwrite: true);
                    return;
                }
            }
        }
        catch { }
    }

    public async Task ScanAndExtractAllAsync(string downloadsFolder, CancellationToken ct = default)
    {
        if (!Directory.Exists(downloadsFolder)) return;

        Console.ForegroundColor = ConsoleColor.Magenta;
        Console.WriteLine($"[CoverExtractor] Auto-scan started for: {downloadsFolder}");
        Console.ResetColor();

        try
        {
            // 1. Scan folders for posters
            var directories = Directory.EnumerateDirectories(downloadsFolder, "*", SearchOption.AllDirectories)
                .Where(d => !Path.GetFileName(d).StartsWith('.'))
                .ToList();

            foreach (var dir in directories)
            {
                if (ct.IsCancellationRequested) break;
                await EnsureFolderCoverAsync(dir, ct);
            }

            // 2. Scan video files for covers
            var files = Directory.EnumerateFiles(downloadsFolder, "*.*", SearchOption.AllDirectories)
                .Where(f => !Path.GetFileName(f).StartsWith('.') &&
                            _mediaSettings.AllowedExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
                .ToList();

            int count = 0;
            foreach (var file in files)
            {
                if (ct.IsCancellationRequested) break;
                var created = await EnsureCoverAsync(file, ct);
                if (created) count++;
            }

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"[CoverExtractor] Auto-scan complete. Processed {files.Count} videos, extracted/verified {count} covers.");
            Console.ResetColor();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CoverExtractor] Error during scan: {ex.Message}");
        }
    }
}
