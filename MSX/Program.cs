using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using MSX;
using MSX.Models;
using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);

// Silence verbose HttpClient handler logs
builder.Logging.AddFilter("System.Net.Http", LogLevel.Warning);
builder.Logging.AddFilter("System.Net.Http.HttpClient", LogLevel.Warning);

// Register settings
builder.Services.Configure<MediaSettings>(
    builder.Configuration.GetSection(MediaSettings.SectionName));

builder.Services.AddSingleton<TorrServerService>();
builder.Services.AddSingleton<WebmarksService>();
builder.Services.AddSingleton<CoverExtractorService>();

var app = builder.Build();

var mediaConfig = builder.Configuration
    .GetSection(MediaSettings.SectionName)
    .Get<MediaSettings>() ?? new MediaSettings();

var torrService = app.Services.GetRequiredService<TorrServerService>();
var webmarksService = app.Services.GetRequiredService<WebmarksService>();
var coverExtractorService = app.Services.GetRequiredService<CoverExtractorService>();

var startTime = DateTime.UtcNow;
const string appVersion = "2.0.0";
const string appName = "ServeMSX .NET";

string configuredPath = mediaConfig.DownloadPath;
string downloadsFolder;

// Log the exact configured path
Console.WriteLine($"[Config] MediaSettings:DownloadPath = \"{configuredPath}\"");

if (!string.IsNullOrWhiteSpace(configuredPath) && Directory.Exists(configuredPath))
{
    downloadsFolder = Path.GetFullPath(configuredPath);
    Console.ForegroundColor = ConsoleColor.Green;
    Console.WriteLine($"[Storage] Media directory verified: \"{downloadsFolder}\"");
    Console.ResetColor();
}
else
{
    Console.ForegroundColor = ConsoleColor.Yellow;
    Console.WriteLine($"[Warning] Media directory NOT found: \"{configuredPath}\"");
    Console.ResetColor();

    downloadsFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Downloads",
        "TorrentNOTComplete"
    );

    if (!Directory.Exists(downloadsFolder))
    {
        Console.WriteLine($"[Storage] Fallback path missing. Creating: \"{downloadsFolder}\"");
        Directory.CreateDirectory(downloadsFolder);
    }
    else
    {
        Console.WriteLine($"[Storage] Using fallback directory: \"{downloadsFolder}\"");
    }
}

// Intercept pipeline response and log 404 Not Found to console
app.Use(async (context, next) =>
{
    await next();

    if (context.Response.StatusCode == StatusCodes.Status404NotFound)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"[404 Not Found] {context.Request.Method} {context.Request.Path}{context.Request.QueryString}");
        Console.ResetColor();
    }
});

// Serve files directly from the Downloads directory with range support (seeking)
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(downloadsFolder),
    RequestPath = "/media",
    ServeUnknownFileTypes = true // Permits .mkv, .ts, .m2ts, .torrent, etc.
});

// Run automatic cover extraction in background on app launch
_ = Task.Run(async () =>
{
    await Task.Delay(1500); // Wait briefly for server startup
    await coverExtractorService.ScanAndExtractAllAsync(downloadsFolder);
});

// File extensions setup
var videoExtensions = new HashSet<string>(
    mediaConfig.AllowedExtensions ?? [".mp4", ".mkv", ".avi", ".mov", ".webm", ".ts", ".mts", ".m2ts", ".wmv", ".flv", ".mpg", ".mpeg", ".m4v"],
    StringComparer.OrdinalIgnoreCase
);

var audioExtensions = new HashSet<string>(
    mediaConfig.AudioExtensions ?? [".mp3", ".m4a", ".flac", ".wav", ".wma", ".aac", ".ogg"],
    StringComparer.OrdinalIgnoreCase
);

var photoExtensions = new HashSet<string>(
    mediaConfig.PhotoExtensions ?? [".jpg", ".jpeg", ".png", ".webp"],
    StringComparer.OrdinalIgnoreCase
);

var coverExtensions = mediaConfig.CoverExtensions != null && mediaConfig.CoverExtensions.Length > 0
    ? mediaConfig.CoverExtensions
    : [".jpg", ".jpeg", ".png", ".webp"];

var jsonOptions = new JsonSerializerOptions
{
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = true
};

// --- Helpers ---

string FormatFileSize(long bytes)
{
    string[] suffixes = ["B", "KB", "MB", "GB", "TB"];
    int i = 0;
    double d = bytes;
    while (d >= 1024 && i < suffixes.Length - 1)
    {
        d /= 1024;
        i++;
    }
    return $"{d:0.##} {suffixes[i]}";
}

string? ResolveTargetDir(string? subPath)
{
    if (string.IsNullOrWhiteSpace(subPath))
    {
        return downloadsFolder;
    }

    var normalized = subPath.Trim().Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
    var combined = Path.GetFullPath(Path.Combine(downloadsFolder, normalized));
    var fullDownloads = Path.GetFullPath(downloadsFolder);

    if (!combined.StartsWith(fullDownloads, StringComparison.OrdinalIgnoreCase))
    {
        return null;
    }

    return combined;
}

string BuildMediaUrl(string fullFilePath, string scheme, string host)
{
    var relative = Path.GetRelativePath(downloadsFolder, fullFilePath).Replace('\\', '/');
    var safeUrlPath = string.Join("/", relative.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString));
    return $"{scheme}://{host}/media/{safeUrlPath}";
}

string BuildContentUrl(string relativePath, string scheme, string host)
{
    var relative = relativePath.Replace('\\', '/');
    var safeUrlPath = string.Join("/", relative.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString));
    return $"{scheme}://{host}/msx/content.json?path={safeUrlPath}";
}

string BuildPlayerUrl(string mediaUrl, bool isVideo, bool useHtml5x)
{
    if (useHtml5x)
    {
        return $"plugin:http://msx.benzac.de/plugins/html5x.html?url={Uri.EscapeDataString(mediaUrl)}";
    }
    return isVideo ? $"video:{mediaUrl}" : $"audio:{mediaUrl}";
}

Dictionary<string, string> GetPlayerProperties()
{
    return new Dictionary<string, string>
    {
        ["resume:key"] = "url",
        ["control:type"] = "extended"
    };
}

string? FindCoverUrl(string videoFilePath, string scheme, string host)
{
    var directory = Path.GetDirectoryName(videoFilePath);
    if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
        return null;

    var videoBaseName = Path.GetFileNameWithoutExtension(videoFilePath);

    foreach (var ext in coverExtensions)
    {
        var candidate = Path.Combine(directory, videoBaseName + ext);
        if (File.Exists(candidate))
        {
            return BuildMediaUrl(candidate, scheme, host);
        }
    }

    return null;
}

string? FindFolderCoverUrl(string folderPath, string scheme, string host)
{
    if (string.IsNullOrEmpty(folderPath) || !Directory.Exists(folderPath))
        return null;

    var folderName = Path.GetFileName(folderPath);

    var candidateNames = new[] { "folder", "poster", "cover", "default", folderName };
    foreach (var name in candidateNames)
    {
        foreach (var ext in coverExtensions)
        {
            var candidate = Path.Combine(folderPath, name + ext);
            if (File.Exists(candidate))
            {
                return BuildMediaUrl(candidate, scheme, host);
            }
        }
    }

    try
    {
        var firstVideo = Directory.EnumerateFiles(folderPath, "*.*", SearchOption.TopDirectoryOnly)
            .FirstOrDefault(f => videoExtensions.Contains(Path.GetExtension(f)) || audioExtensions.Contains(Path.GetExtension(f)));
        if (firstVideo != null)
        {
            var videoCover = FindCoverUrl(firstVideo, scheme, host);
            if (videoCover != null)
            {
                return videoCover;
            }
        }
    }
    catch { }

    return null;
}

string? FindFolderBackgroundUrl(string folderPath, string scheme, string host)
{
    if (string.IsNullOrEmpty(folderPath) || !Directory.Exists(folderPath))
        return null;

    foreach (var ext in coverExtensions)
    {
        var bg = Path.Combine(folderPath, "background" + ext);
        if (File.Exists(bg))
        {
            return BuildMediaUrl(bg, scheme, host);
        }
    }
    return null;
}

bool HasMediaFiles(string dirPath)
{
    try
    {
        return Directory.EnumerateFiles(dirPath, "*.*", SearchOption.AllDirectories)
            .Any(f =>
            {
                var ext = Path.GetExtension(f);
                return videoExtensions.Contains(ext) ||
                       audioExtensions.Contains(ext) ||
                       photoExtensions.Contains(ext) ||
                       ext.Equals(".torrent", StringComparison.OrdinalIgnoreCase);
            });
    }
    catch
    {
        return false;
    }
}

List<MsxMediaItem> GetDirectoryItems(string targetDir, string scheme, string host)
{
    var items = new List<MsxMediaItem>();

    if (!Directory.Exists(targetDir))
        return items;

    // 1. Folders: stay folders, naturally sorted by name
    try
    {
        var subDirs = Directory.EnumerateDirectories(targetDir, "*", SearchOption.TopDirectoryOnly)
            .Where(d => !Path.GetFileName(d).StartsWith('.') && HasMediaFiles(d))
            .OrderBy(d => Path.GetFileName(d), NaturalStringComparer.Instance);

        foreach (var dir in subDirs)
        {
            var dirName = Path.GetFileName(dir);
            var relPath = Path.GetRelativePath(downloadsFolder, dir);

            items.Add(new MsxMediaItem
            {
                Title = dirName,
                Icon = "msx-white-soft:folder",
                Action = $"content:{BuildContentUrl(relPath, scheme, host)}",
                Image = FindFolderCoverUrl(dir, scheme, host)
            });
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[Storage] Error enumerating directories in {targetDir}: {ex.Message}");
    }

    // 2. Files: sorted naturally by name
    try
    {
        var allFilesInDir = Directory.EnumerateFiles(targetDir, "*.*", SearchOption.TopDirectoryOnly)
            .Where(f => !Path.GetFileName(f).StartsWith('.'))
            .ToList();

        var videoBaseNames = allFilesInDir
            .Where(f => videoExtensions.Contains(Path.GetExtension(f)))
            .Select(f => Path.GetFileNameWithoutExtension(f))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var audioBaseNames = allFilesInDir
            .Where(f => audioExtensions.Contains(Path.GetExtension(f)))
            .Select(f => Path.GetFileNameWithoutExtension(f))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var targetDirName = Path.GetFileName(targetDir);
        var standardCoverNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "cover", "folder", "poster", "default", "background", "fanart", targetDirName
        };

        var files = allFilesInDir
            .OrderBy(f => Path.GetFileNameWithoutExtension(f), NaturalStringComparer.Instance);

        foreach (var file in files)
        {
            var ext = Path.GetExtension(file);
            var fileNameWithoutExt = Path.GetFileNameWithoutExtension(file);
            var fileInfo = new FileInfo(file);
            var fileSize = fileInfo.Exists ? FormatFileSize(fileInfo.Length) : string.Empty;

            if (ext.Equals(".torrent", StringComparison.OrdinalIgnoreCase))
            {
                var torrentMediaUrl = BuildMediaUrl(file, scheme, host);
                items.Add(new MsxMediaItem
                {
                    Title = fileNameWithoutExt,
                    Icon = "msx-yellow:offline-bolt",
                    Action = $"content:{scheme}://{host}/msx/torr?link={Uri.EscapeDataString(torrentMediaUrl)}",
                    ExtensionLabel = fileSize,
                    Group = "Torrents"
                });
            }
            else if (videoExtensions.Contains(ext))
            {
                var mediaUrl = BuildMediaUrl(file, scheme, host);
                items.Add(new MsxMediaItem
                {
                    Title = fileNameWithoutExt,
                    PlayerLabel = fileNameWithoutExt,
                    Action = BuildPlayerUrl(mediaUrl, isVideo: true, mediaConfig.EnableHtml5x),
                    Image = FindCoverUrl(file, scheme, host),
                    ExtensionLabel = fileSize,
                    Properties = GetPlayerProperties(),
                    Group = "Video"
                });
            }
            else if (audioExtensions.Contains(ext))
            {
                var mediaUrl = BuildMediaUrl(file, scheme, host);
                items.Add(new MsxMediaItem
                {
                    Title = fileNameWithoutExt,
                    PlayerLabel = fileNameWithoutExt,
                    Icon = "msx-white-soft:audiotrack",
                    Action = BuildPlayerUrl(mediaUrl, isVideo: false, mediaConfig.EnableHtml5x),
                    Image = FindCoverUrl(file, scheme, host),
                    ExtensionLabel = fileSize,
                    Properties = GetPlayerProperties(),
                    Group = "Audio"
                });
            }
            else if (photoExtensions.Contains(ext))
            {
                // If it is a cover for a video, an audio file, or a folder poster, ignore it as a separate piece
                if (videoBaseNames.Contains(fileNameWithoutExt) ||
                    audioBaseNames.Contains(fileNameWithoutExt) ||
                    standardCoverNames.Contains(fileNameWithoutExt) ||
                    videoBaseNames.Count > 0)
                {
                    continue;
                }

                var mediaUrl = BuildMediaUrl(file, scheme, host);
                items.Add(new MsxMediaItem
                {
                    Title = fileNameWithoutExt,
                    Icon = "msx-white-soft:photo-library",
                    Action = $"image:{mediaUrl}",
                    Image = mediaUrl,
                    ExtensionLabel = fileSize,
                    Group = "Photos"
                });
            }
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[Storage] Error enumerating files in {targetDir}: {ex.Message}");
    }

    return items;
}

MsxMenuData BuildMenuData(string targetDir, string scheme, string host)
{
    var isRoot = targetDir.Equals(downloadsFolder, StringComparison.OrdinalIgnoreCase);
    var layout = mediaConfig.CompressedLayout ? "0,0,16,1" : "0,0,2,4";

    return new MsxMenuData
    {
        Type = "pages",
        Headline = isRoot ? null : Path.GetFileName(targetDir),
        Background = FindFolderBackgroundUrl(targetDir, scheme, host),
        Template = new MsxTemplate
        {
            Type = "separate",
            Layout = layout,
            Icon = "msx-white-soft:movie",
            Color = "msx-glass",
            ImageFiller = "cover",
            Properties = GetPlayerProperties()
        },
        Items = GetDirectoryItems(targetDir, scheme, host)
    };
}

// --- Endpoints ---

string GetDirectMediaUrl(string? action)
{
    if (string.IsNullOrEmpty(action)) return "";
    if (action.StartsWith("video:")) return action["video:".Length..];
    if (action.StartsWith("audio:")) return action["audio:".Length..];
    if (action.StartsWith("image:")) return action["image:".Length..];
    if (action.Contains("url="))
    {
        var idx = action.IndexOf("url=");
        var raw = action[(idx + 4)..];
        var amp = raw.IndexOf('&');
        if (amp >= 0) raw = raw[..amp];
        return Uri.UnescapeDataString(raw);
    }
    return action;
}

string GetFolderRelPath(string? action)
{
    if (string.IsNullOrEmpty(action)) return "";
    var idx = action.IndexOf("?path=");
    if (idx >= 0)
    {
        var raw = action[(idx + 6)..];
        var amp = raw.IndexOf('&');
        if (amp >= 0) raw = raw[..amp];
        return Uri.UnescapeDataString(raw);
    }
    return "";
}

string GetTorrentLink(string? action)
{
    if (string.IsNullOrEmpty(action)) return "";
    var idx = action.IndexOf("link=");
    if (idx >= 0)
    {
        var raw = action[(idx + 5)..];
        var amp = raw.IndexOf('&');
        if (amp >= 0) raw = raw[..amp];
        return Uri.UnescapeDataString(raw);
    }
    return "";
}

// 0. Sleek Web UI Catalog & Dashboard
app.MapGet("/", async (HttpContext context, string? path) =>
{
    var scheme = context.Request.Scheme;
    var host = context.Request.Host.Value ?? "localhost:5208";
    var startUrl = $"{scheme}://{host}/msx/start.json";
    var menuUrl = $"{scheme}://{host}/msx/menu.json";
    var torrVersion = await torrService.CheckTorrServerAsync(mediaConfig.TorrServer);
    var uptime = DateTime.UtcNow - startTime;

    var targetDir = ResolveTargetDir(path) ?? downloadsFolder;
    if (!Directory.Exists(targetDir)) targetDir = downloadsFolder;

    var isRoot = targetDir.Equals(downloadsFolder, StringComparison.OrdinalIgnoreCase);
    var relCurrent = Path.GetRelativePath(downloadsFolder, targetDir).Replace('\\', '/');
    if (relCurrent == ".") relCurrent = "";

    var items = GetDirectoryItems(targetDir, scheme, host);

    // Breadcrumbs
    var breadcrumbsSb = new StringBuilder();
    breadcrumbsSb.Append("<a href=\"/\" class=\"crumb\">🏠 Library</a>");
    if (!string.IsNullOrEmpty(relCurrent))
    {
        var segments = relCurrent.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var accumulated = "";
        foreach (var seg in segments)
        {
            accumulated = string.IsNullOrEmpty(accumulated) ? seg : $"{accumulated}/{seg}";
            breadcrumbsSb.Append($" <span class=\"sep\">/</span> <a href=\"/?path={Uri.EscapeDataString(accumulated)}\" class=\"crumb\">{WebUtility.HtmlEncode(seg)}</a>");
        }
    }

    var parentPath = "";
    if (!isRoot && !string.IsNullOrEmpty(relCurrent))
    {
        var lastSlash = relCurrent.LastIndexOf('/');
        parentPath = lastSlash > 0 ? relCurrent[..lastSlash] : "";
    }

    // Media Cards
    var cardsSb = new StringBuilder();
    foreach (var item in items)
    {
        var title = WebUtility.HtmlEncode(item.Title ?? "Untitled");
        var image = !string.IsNullOrEmpty(item.Image) ? WebUtility.HtmlEncode(item.Image) : null;
        var size = WebUtility.HtmlEncode(item.ExtensionLabel ?? "");
        var isFolder = item.Icon?.Contains("folder") == true || item.Action?.Contains("/msx/content.json?path=") == true;
        var isTorrent = item.Group?.Equals("Torrents", StringComparison.OrdinalIgnoreCase) == true;
        var isAudio = item.Group?.Equals("Audio", StringComparison.OrdinalIgnoreCase) == true;
        var isPhoto = item.Group?.Equals("Photos", StringComparison.OrdinalIgnoreCase) == true;
        var isVideo = !isFolder && !isTorrent && !isAudio && !isPhoto;

        if (isFolder)
        {
            var relFolder = GetFolderRelPath(item.Action);
            cardsSb.AppendLine($$"""
            <a href="/?path={{Uri.EscapeDataString(relFolder)}}" class="media-card folder-card" data-title="{{title.ToLower()}}">
                <div class="poster-box">
                    {{(image != null ? $"<img src=\"{image}\" class=\"poster-img\" loading=\"lazy\" onerror=\"this.style.display='none';this.nextElementSibling.style.display='flex';\" />" : "")}}
                    <div class="poster-placeholder" style="{{(image != null ? "display:none;" : "")}}">📁</div>
                    <span class="badge badge-folder">FOLDER</span>
                </div>
                <div class="media-info">
                    <div class="media-title" title="{{title}}">{{title}}</div>
                    <div class="media-sub"><span class="folder-hint">Open Folder &rarr;</span></div>
                </div>
            </a>
            """);
        }
        else if (isVideo)
        {
            var mediaUrl = GetDirectMediaUrl(item.Action);
            var safeTitleJs = title.Replace("'", "\\'").Replace("\"", "&quot;");
            cardsSb.AppendLine($$"""
            <div class="media-card video-card" data-title="{{title.ToLower()}}">
                <div class="poster-box" onclick="playVideo('{{mediaUrl}}', '{{safeTitleJs}}')">
                    {{(image != null ? $"<img src=\"{image}\" class=\"poster-img\" loading=\"lazy\" onerror=\"this.style.display='none';this.nextElementSibling.style.display='flex';\" />" : "")}}
                    <div class="poster-placeholder" style="{{(image != null ? "display:none;" : "")}}">🎬</div>
                    <div class="play-overlay"><span>▶</span></div>
                    <span class="badge badge-video">VIDEO</span>
                    {{(!string.IsNullOrEmpty(size) ? $"<span class=\"badge badge-size\">{size}</span>" : "")}}
                </div>
                <div class="media-info">
                    <div class="media-title" title="{{title}}">{{title}}</div>
                    <div class="media-actions">
                        <button type="button" class="btn-play" onclick="playVideo('{{mediaUrl}}', '{{safeTitleJs}}')">▶ Play</button>
                        <a href="{{mediaUrl}}" download class="btn-download" title="Direct Download / Stream">⬇</a>
                    </div>
                </div>
            </div>
            """);
        }
        else if (isTorrent)
        {
            var torrentUrl = GetTorrentLink(item.Action);
            cardsSb.AppendLine($$"""
            <div class="media-card torrent-card" data-title="{{title.ToLower()}}">
                <div class="poster-box">
                    {{(image != null ? $"<img src=\"{image}\" class=\"poster-img\" loading=\"lazy\" onerror=\"this.style.display='none';this.nextElementSibling.style.display='flex';\" />" : "")}}
                    <div class="poster-placeholder" style="{{(image != null ? "display:none;" : "")}}">⚡</div>
                    <span class="badge badge-torrent">TORRENT</span>
                    {{(!string.IsNullOrEmpty(size) ? $"<span class=\"badge badge-size\">{size}</span>" : "")}}
                </div>
                <div class="media-info">
                    <div class="media-title" title="{{title}}">{{title}}</div>
                    <div class="media-actions">
                        <a href="{{torrentUrl}}" download class="btn-play" style="background:#eab308;color:#000;">⬇ .torrent</a>
                    </div>
                </div>
            </div>
            """);
        }
        else if (isAudio)
        {
            var audioUrl = GetDirectMediaUrl(item.Action);
            cardsSb.AppendLine($$"""
            <div class="media-card audio-card" data-title="{{title.ToLower()}}">
                <div class="poster-box">
                    {{(image != null ? $"<img src=\"{image}\" class=\"poster-img\" loading=\"lazy\" onerror=\"this.style.display='none';this.nextElementSibling.style.display='flex';\" />" : "")}}
                    <div class="poster-placeholder" style="{{(image != null ? "display:none;" : "")}}">🎵</div>
                    <span class="badge badge-audio">AUDIO</span>
                    {{(!string.IsNullOrEmpty(size) ? $"<span class=\"badge badge-size\">{size}</span>" : "")}}
                </div>
                <div class="media-info">
                    <div class="media-title" title="{{title}}">{{title}}</div>
                    <audio controls src="{{audioUrl}}" style="width:100%;height:32px;margin-top:0.4rem;"></audio>
                </div>
            </div>
            """);
        }
        else if (isPhoto)
        {
            var photoUrl = GetDirectMediaUrl(item.Action);
            cardsSb.AppendLine($$"""
            <a href="{{photoUrl}}" target="_blank" class="media-card photo-card" data-title="{{title.ToLower()}}">
                <div class="poster-box">
                    <img src="{{photoUrl}}" class="poster-img" loading="lazy" />
                    <span class="badge badge-photo">PHOTO</span>
                </div>
                <div class="media-info">
                    <div class="media-title" title="{{title}}">{{title}}</div>
                </div>
            </a>
            """);
        }
    }

    var html = $$"""
    <!DOCTYPE html>
    <html lang="en">
    <head>
        <meta charset="UTF-8">
        <meta name="viewport" content="width=device-width, initial-scale=1.0">
        <title>{{appName}} - Media Catalog</title>
        <style>
            :root {
                --bg: #0b0f19;
                --header-bg: rgba(13, 20, 36, 0.85);
                --card-bg: #151d2f;
                --card-hover: #1b253b;
                --accent: #38bdf8;
                --accent-hover: #0ea5e9;
                --text: #f8fafc;
                --muted: #94a3b8;
                --border: #243048;
            }
            * { box-sizing: border-box; }
            body {
                font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, sans-serif;
                background-color: var(--bg);
                color: var(--text);
                margin: 0;
                padding: 0;
            }
            .header {
                background: var(--header-bg);
                border-bottom: 1px solid var(--border);
                padding: 0.85rem 2rem;
                display: flex;
                justify-content: space-between;
                align-items: center;
                gap: 1rem;
                flex-wrap: wrap;
                position: sticky;
                top: 0;
                z-index: 50;
                backdrop-filter: blur(12px);
            }
            .logo-title {
                font-size: 1.25rem;
                font-weight: 700;
                color: var(--accent);
                display: flex;
                align-items: center;
                gap: 0.5rem;
                text-decoration: none;
            }
            .logo-version {
                font-size: 0.75rem;
                color: var(--muted);
                font-weight: normal;
                background: #1e293b;
                padding: 0.15rem 0.4rem;
                border-radius: 4px;
            }
            .search-box {
                flex: 1;
                max-width: 480px;
                position: relative;
            }
            .search-input {
                width: 100%;
                background: #070a12;
                border: 1px solid var(--border);
                color: var(--text);
                padding: 0.6rem 1rem 0.6rem 2.4rem;
                border-radius: 9999px;
                font-size: 0.95rem;
                outline: none;
                transition: border-color 0.2s, box-shadow 0.2s;
            }
            .search-input:focus {
                border-color: var(--accent);
                box-shadow: 0 0 0 3px rgba(56, 189, 248, 0.2);
            }
            .search-icon {
                position: absolute;
                left: 0.85rem;
                top: 50%;
                transform: translateY(-50%);
                color: var(--muted);
                font-size: 0.95rem;
                pointer-events: none;
            }
            .header-actions {
                display: flex;
                align-items: center;
                gap: 0.75rem;
            }
            .container {
                max-width: 1600px;
                margin: 0 auto;
                padding: 2rem;
            }
            .nav-bar {
                display: flex;
                justify-content: space-between;
                align-items: center;
                gap: 1rem;
                margin-bottom: 1.5rem;
                flex-wrap: wrap;
            }
            .breadcrumbs {
                display: flex;
                align-items: center;
                gap: 0.4rem;
                font-size: 1.1rem;
                font-weight: 600;
            }
            .crumb {
                color: var(--text);
                text-decoration: none;
                padding: 0.25rem 0.5rem;
                border-radius: 4px;
                transition: background 0.15s, color 0.15s;
            }
            .crumb:hover {
                background: #1e293b;
                color: var(--accent);
            }
            .sep { color: var(--muted); }
            .nav-right {
                display: flex;
                align-items: center;
                gap: 0.75rem;
            }
            .item-count-badge {
                background: #1e293b;
                color: var(--accent);
                font-size: 0.85rem;
                font-weight: 600;
                padding: 0.3rem 0.75rem;
                border-radius: 9999px;
                border: 1px solid var(--border);
            }
            .btn-back {
                display: inline-flex;
                align-items: center;
                gap: 0.4rem;
                padding: 0.4rem 0.9rem;
                background: #1e293b;
                color: var(--text);
                border: 1px solid var(--border);
                border-radius: 6px;
                text-decoration: none;
                font-size: 0.9rem;
                transition: background 0.15s;
            }
            .btn-back:hover { background: #334155; }
            .catalog-grid {
                display: grid;
                grid-template-columns: repeat(auto-fill, minmax(200px, 1fr));
                gap: 1.5rem;
                margin-bottom: 3.5rem;
            }
            .media-card {
                background: var(--card-bg);
                border: 1px solid var(--border);
                border-radius: 0.75rem;
                overflow: hidden;
                display: flex;
                flex-direction: column;
                text-decoration: none;
                color: inherit;
                transition: transform 0.2s, box-shadow 0.2s, border-color 0.2s;
                cursor: pointer;
            }
            .media-card:hover {
                transform: translateY(-5px);
                box-shadow: 0 14px 28px rgba(0, 0, 0, 0.45);
                border-color: var(--accent);
                background: var(--card-hover);
            }
            .poster-box {
                aspect-ratio: 2 / 3;
                width: 100%;
                position: relative;
                background: #060911;
                overflow: hidden;
                display: flex;
                align-items: center;
                justify-content: center;
            }
            .poster-img {
                width: 100%;
                height: 100%;
                object-fit: cover;
                display: block;
                transition: transform 0.3s;
            }
            .media-card:hover .poster-img {
                transform: scale(1.05);
            }
            .poster-placeholder {
                font-size: 3.5rem;
                display: flex;
                align-items: center;
                justify-content: center;
                width: 100%;
                height: 100%;
                background: linear-gradient(135deg, #101726, #1a243a);
                user-select: none;
            }
            .badge {
                position: absolute;
                padding: 0.2rem 0.5rem;
                border-radius: 4px;
                font-size: 0.7rem;
                font-weight: 700;
                letter-spacing: 0.5px;
                text-transform: uppercase;
                backdrop-filter: blur(8px);
                z-index: 2;
            }
            .badge-folder { top: 0.5rem; left: 0.5rem; background: rgba(56, 189, 248, 0.35); color: #7dd3fc; border: 1px solid rgba(56, 189, 248, 0.4); }
            .badge-video { top: 0.5rem; left: 0.5rem; background: rgba(34, 197, 94, 0.35); color: #86efac; border: 1px solid rgba(34, 197, 94, 0.4); }
            .badge-torrent { top: 0.5rem; left: 0.5rem; background: rgba(234, 179, 8, 0.35); color: #fde047; border: 1px solid rgba(234, 179, 8, 0.4); }
            .badge-audio { top: 0.5rem; left: 0.5rem; background: rgba(168, 85, 247, 0.35); color: #d8b4fe; border: 1px solid rgba(168, 85, 247, 0.4); }
            .badge-photo { top: 0.5rem; left: 0.5rem; background: rgba(236, 72, 153, 0.35); color: #f472b6; border: 1px solid rgba(236, 72, 153, 0.4); }
            .badge-size { top: 0.5rem; right: 0.5rem; background: rgba(0, 0, 0, 0.75); color: #f8fafc; font-family: monospace; border: 1px solid rgba(255, 255, 255, 0.15); }
            .play-overlay {
                position: absolute;
                inset: 0;
                background: rgba(0, 0, 0, 0.4);
                display: flex;
                align-items: center;
                justify-content: center;
                opacity: 0;
                transition: opacity 0.2s;
                z-index: 1;
            }
            .play-overlay span {
                width: 48px;
                height: 48px;
                border-radius: 50%;
                background: var(--accent);
                color: #0f172a;
                display: flex;
                align-items: center;
                justify-content: center;
                font-size: 1.25rem;
                font-weight: bold;
                box-shadow: 0 4px 15px rgba(56, 189, 248, 0.4);
            }
            .media-card:hover .play-overlay { opacity: 1; }
            .media-info {
                padding: 0.9rem 1rem;
                display: flex;
                flex-direction: column;
                gap: 0.5rem;
                flex: 1;
            }
            .media-title {
                font-size: 0.95rem;
                font-weight: 600;
                line-height: 1.35;
                display: -webkit-box;
                -webkit-line-clamp: 2;
                -webkit-box-orient: vertical;
                overflow: hidden;
                text-overflow: ellipsis;
                color: #f1f5f9;
            }
            .media-sub {
                font-size: 0.8rem;
                color: var(--muted);
                display: flex;
                align-items: center;
                justify-content: space-between;
                margin-top: auto;
            }
            .folder-hint { color: var(--accent); font-weight: 500; }
            .media-actions {
                display: flex;
                gap: 0.4rem;
                margin-top: auto;
            }
            .btn-play {
                flex: 1;
                background: var(--accent);
                color: #0b0f19;
                border: none;
                border-radius: 4px;
                padding: 0.45rem 0.6rem;
                font-weight: 600;
                font-size: 0.85rem;
                cursor: pointer;
                text-align: center;
                text-decoration: none;
                transition: background 0.15s;
            }
            .btn-play:hover { background: var(--accent-hover); }
            .btn-download {
                background: #1e293b;
                color: var(--text);
                border: 1px solid var(--border);
                border-radius: 4px;
                padding: 0.45rem 0.65rem;
                font-size: 0.85rem;
                text-decoration: none;
                display: flex;
                align-items: center;
                justify-content: center;
                transition: background 0.15s;
            }
            .btn-download:hover { background: #334155; }
            .empty-box {
                text-align: center;
                padding: 4rem 2rem;
                background: var(--card-bg);
                border: 1px dashed var(--border);
                border-radius: 0.75rem;
                color: var(--muted);
            }
            .empty-box h3 { color: var(--text); margin-bottom: 0.5rem; }
            /* Modal player */
            .modal-overlay {
                position: fixed;
                inset: 0;
                background: rgba(0, 0, 0, 0.85);
                z-index: 100;
                display: none;
                align-items: center;
                justify-content: center;
                padding: 1.5rem;
                backdrop-filter: blur(8px);
            }
            .modal-content {
                background: #101726;
                border: 1px solid var(--border);
                border-radius: 0.75rem;
                max-width: 1000px;
                width: 100%;
                overflow: hidden;
                display: flex;
                flex-direction: column;
                box-shadow: 0 25px 50px -12px rgba(0, 0, 0, 0.6);
            }
            .modal-header {
                display: flex;
                justify-content: space-between;
                align-items: center;
                padding: 1rem 1.25rem;
                border-bottom: 1px solid var(--border);
            }
            .modal-title {
                font-weight: 600;
                font-size: 1.1rem;
                overflow: hidden;
                text-overflow: ellipsis;
                white-space: nowrap;
                max-width: 90%;
            }
            .modal-close {
                background: none;
                border: none;
                color: var(--muted);
                font-size: 1.6rem;
                cursor: pointer;
                line-height: 1;
            }
            .modal-close:hover { color: var(--text); }
            .modal-body { background: #000; display: flex; justify-content: center; align-items: center; }
            .modal-video { width: 100%; max-height: 72vh; outline: none; }
            .modal-footer {
                padding: 0.75rem 1.25rem;
                border-top: 1px solid var(--border);
                display: flex;
                justify-content: space-between;
                align-items: center;
                font-size: 0.85rem;
                color: var(--muted);
            }
            /* Settings & Management section */
            .settings-section {
                border-top: 1px solid var(--border);
                padding-top: 2.5rem;
                margin-top: 3rem;
            }
            .settings-grid {
                display: grid;
                grid-template-columns: repeat(auto-fit, minmax(320px, 1fr));
                gap: 1.5rem;
            }
            .card {
                background: var(--card-bg);
                border: 1px solid var(--border);
                border-radius: 0.75rem;
                padding: 1.5rem;
                box-shadow: 0 4px 6px -1px rgba(0, 0, 0, 0.1);
            }
            .card h2 {
                margin-top: 0;
                font-size: 1.2rem;
                color: var(--accent);
                display: flex;
                align-items: center;
                gap: 0.5rem;
            }
            .code-box {
                background: #070a12;
                padding: 0.75rem 1rem;
                border-radius: 0.5rem;
                font-family: monospace;
                font-size: 1rem;
                color: #a5f3fc;
                border: 1px dashed var(--border);
                display: flex;
                justify-content: space-between;
                align-items: center;
                margin-top: 0.5rem;
                word-break: break-all;
            }
            .btn {
                background: var(--accent);
                color: #0b0f19;
                font-weight: 600;
                padding: 0.5rem 1rem;
                border-radius: 0.375rem;
                border: none;
                cursor: pointer;
                transition: background 0.15s;
                text-decoration: none;
                display: inline-block;
            }
            .btn:hover { background: var(--accent-hover); }
            .status-badge {
                display: inline-block;
                padding: 0.2rem 0.55rem;
                border-radius: 9999px;
                font-size: 0.8rem;
                font-weight: 600;
            }
            .badge-green { background: rgba(34, 197, 94, 0.2); color: #4ade80; }
            .badge-yellow { background: rgba(234, 179, 8, 0.2); color: #facc15; }
            table { width: 100%; border-collapse: collapse; margin-top: 0.5rem; font-size: 0.9rem; }
            td { padding: 0.5rem 0; border-bottom: 1px solid var(--border); }
            td:first-child { color: var(--muted); width: 35%; }
            a { color: var(--accent); text-decoration: none; }
            a:hover { text-decoration: underline; }
            .form-group { display: flex; gap: 0.5rem; margin-top: 0.75rem; }
            input[type="text"] {
                flex: 1;
                background: #070a12;
                border: 1px solid var(--border);
                color: var(--text);
                padding: 0.5rem 0.75rem;
                border-radius: 0.375rem;
                outline: none;
            }
            input[type="text"]:focus { border-color: var(--accent); }
        </style>
    </head>
    <body>
        <header class="header">
            <a href="/" class="logo-title">
                📺 {{appName}} <span class="logo-version">v{{appVersion}}</span>
            </a>
            <div class="search-box">
                <span class="search-icon">🔍</span>
                <input type="text" id="search-input" class="search-input" placeholder="Search catalog..." oninput="filterMedia()" />
            </div>
            <div class="header-actions">
                <a href="#settings" class="btn" style="background: #1e293b; color: var(--text); border: 1px solid var(--border);">⚙️ Tools & MSX Setup</a>
            </div>
        </header>

        <main class="container">
            <div class="nav-bar">
                <div class="breadcrumbs">
                    {{breadcrumbsSb}}
                </div>
                <div class="nav-right">
                    {{(!isRoot ? $"<a href=\"/?path={Uri.EscapeDataString(parentPath)}\" class=\"btn-back\">⬅ Up to Parent</a>" : "")}}
                    <span id="item-count" class="item-count-badge">{{items.Count}} items</span>
                </div>
            </div>

            {{(items.Count > 0 
                ? $"<div class=\"catalog-grid\">{cardsSb}</div>" 
                : "<div class=\"empty-box\"><h3>No media found</h3><p>There are no media files in this folder.</p><a href=\"/\" class=\"btn\" style=\"margin-top:1rem;\">Back to Home</a></div>")}}

            <section id="settings" class="settings-section">
                <h2 style="color: var(--accent); margin-bottom: 1.5rem; font-size: 1.4rem;">⚙️ Management & MediaStation X Setup</h2>
                <div class="settings-grid">
                    <div class="card">
                        <h2>📺 MediaStation X Setup</h2>
                        <p style="color: var(--muted); font-size: 0.9rem; margin-top: 0;">On your Smart TV or browser (<a href="https://msx.benzac.de/" target="_blank">msx.benzac.de</a>), go to:<br><b>Settings ⚙️ &rarr; Start Parameter &rarr; Setup</b> and enter:</p>
                        <div class="code-box">
                            <span>{{host}}/msx/start.json</span>
                            <button type="button" class="btn" style="padding: 0.25rem 0.6rem; font-size: 0.8rem;" onclick="copyText(this, '{{host}}/msx/start.json')">Copy</button>
                        </div>
                    </div>

                    <div class="card">
                        <h2>🎨 Cover Art Extraction</h2>
                        <p style="color: var(--muted); font-size: 0.9rem; margin-top: 0;">Extracts covers from video files (embedded streams / frame snapshots) and queries Wikipedia/TMDB/TVMaze with strict title validation.</p>
                        <div style="display: flex; gap: 0.6rem; flex-wrap: wrap;">
                            <form method="POST" action="/covers/scan">
                                <button type="submit" class="btn">⚡ Scan Missing Covers</button>
                            </form>
                            <form method="POST" action="/covers/scan?force=true">
                                <button type="submit" class="btn" style="background: #f59e0b;">🔄 Force Refresh All Covers</button>
                            </form>
                        </div>
                    </div>

                    <div class="card">
                        <h2>⚡ System & Services Status</h2>
                        <table>
                            <tr>
                                <td>Status</td>
                                <td><span class="status-badge badge-green">RUNNING</span> (Uptime: {{uptime.Hours}}h {{uptime.Minutes}}m)</td>
                            </tr>
                            <tr>
                                <td>TorrServer</td>
                                <td>
                                    {{(string.IsNullOrEmpty(torrVersion) 
                                        ? $"<span class=\"status-badge badge-yellow\">NOT CONNECTED</span>" 
                                        : $"<span class=\"status-badge badge-green\">ONLINE: {torrVersion}</span>")}}
                                </td>
                            </tr>
                            <tr>
                                <td>Media Dir</td>
                                <td><code style="color:#7dd3fc; font-size:0.85rem;">{{downloadsFolder}}</code></td>
                            </tr>
                            <tr>
                                <td>Catalogs</td>
                                <td>
                                    <a href="{{menuUrl}}" target="_blank">menu.json</a> &bull; 
                                    <a href="/msx/torr" target="_blank">torr.json</a> &bull; 
                                    <a href="/msx/webmarks" target="_blank">webmarks.json</a>
                                </td>
                            </tr>
                        </table>
                    </div>

                    <div class="card">
                        <h2>🔖 Add Webmark Stream / YouTube</h2>
                        <form method="POST" action="/webmarks/add">
                            <div class="form-group">
                                <input type="text" name="title" placeholder="Title (e.g. My HLS Stream)" required>
                                <input type="text" name="url" placeholder="URL or YouTube ID" required>
                                <button type="submit" class="btn">Add</button>
                            </div>
                        </form>
                    </div>
                </div>
            </section>
        </main>

        <!-- Video Player Modal -->
        <div id="video-modal" class="modal-overlay" onclick="handleBackdropClick(event)">
            <div class="modal-content">
                <div class="modal-header">
                    <div id="modal-title" class="modal-title">Video Player</div>
                    <button type="button" class="modal-close" onclick="closeModal()">✖</button>
                </div>
                <div class="modal-body">
                    <video id="modal-video" class="modal-video" controls autoplay></video>
                </div>
                <div class="modal-footer">
                    <span>Press <kbd style="background:#1e293b;padding:0.1rem 0.3rem;border-radius:3px;">ESC</kbd> to close</span>
                    <a id="modal-direct" href="#" target="_blank" class="btn" style="padding:0.3rem 0.75rem; font-size:0.85rem;">Open Stream Directly &rarr;</a>
                </div>
            </div>
        </div>

        <script>
            function filterMedia() {
                const q = document.getElementById('search-input').value.toLowerCase().trim();
                const cards = document.querySelectorAll('.media-card');
                let count = 0;
                cards.forEach(card => {
                    const title = card.getAttribute('data-title') || '';
                    if (!q || title.includes(q)) {
                        card.style.display = '';
                        count++;
                    } else {
                        card.style.display = 'none';
                    }
                });
                document.getElementById('item-count').textContent = count + ' items';
            }

            function playVideo(url, title) {
                const modal = document.getElementById('video-modal');
                const video = document.getElementById('modal-video');
                const titleEl = document.getElementById('modal-title');
                const directLink = document.getElementById('modal-direct');
                titleEl.textContent = title;
                video.src = url;
                directLink.href = url;
                modal.style.display = 'flex';
                video.play();
            }

            function closeModal() {
                const modal = document.getElementById('video-modal');
                const video = document.getElementById('modal-video');
                video.pause();
                video.src = '';
                modal.style.display = 'none';
            }

            function handleBackdropClick(e) {
                if (e.target.id === 'video-modal') {
                    closeModal();
                }
            }

            window.addEventListener('keydown', e => {
                if (e.key === 'Escape') closeModal();
            });

            function copyText(btn, text) {
                navigator.clipboard.writeText(text).then(() => {
                    const old = btn.textContent;
                    btn.textContent = 'Copied! ✓';
                    setTimeout(() => btn.textContent = old, 2000);
                });
            }
        </script>
    </body>
    </html>
    """;

    return Results.Content(html, "text/html");
});

// Endpoint to trigger manual cover scan from browser (supports ?force=true to overwrite wrong covers)
app.MapMethods("/covers/scan", new[] { "GET", "POST" }, (HttpContext context) =>
{
    var force = context.Request.Query.ContainsKey("force") ||
                (context.Request.HasFormContentType && context.Request.Form.ContainsKey("force"));
    _ = Task.Run(() => coverExtractorService.ScanAndExtractAllAsync(downloadsFolder, overwrite: force));
    return Results.Redirect("/");
});

// Quick endpoint to add webmarks via browser
app.MapPost("/webmarks/add", (HttpContext context) =>
{
    var form = context.Request.Form;
    var title = form["title"].ToString();
    var url = form["url"].ToString();
    if (!string.IsNullOrWhiteSpace(url))
    {
        webmarksService.AddWebmark(title, url);
    }
    return Results.Redirect("/");
});

// 1. Serve start.json with dynamic {SERVER} & {SCHEME} replacement
app.MapGet("/msx/start.json", async (HttpContext context) =>
{
    var host = context.Request.Host.Value ?? "localhost:5208";
    var scheme = context.Request.Scheme;

    string template;
    var candidatePath = Path.Combine(AppContext.BaseDirectory, "start.json");
    var rootPath = Path.Combine(Directory.GetCurrentDirectory(), "start.json");

    if (File.Exists(candidatePath))
    {
        template = await File.ReadAllTextAsync(candidatePath);
    }
    else if (File.Exists(rootPath))
    {
        template = await File.ReadAllTextAsync(rootPath);
    }
    else
    {
        template = """
        {
          "name": "ServeMSX",
          "version": "2.0.0",
          "parameter": "menu:{SCHEME}://{SERVER}/msx/menu.json"
        }
        """;
    }

    var resolved = template
        .Replace("{SERVER}", host)
        .Replace("{SCHEME}", scheme);

    return Results.Content(resolved, "application/json");
});

// 2. Serve the dynamic menu catalog
app.MapGet("/msx/menu.json", async (HttpContext context, string? path) =>
{
    var scheme = context.Request.Scheme;
    var host = context.Request.Host.Value ?? "localhost:5208";

    var targetDir = ResolveTargetDir(path);
    if (targetDir == null || !Directory.Exists(targetDir))
    {
        return Results.NotFound();
    }

    var isRoot = targetDir.Equals(downloadsFolder, StringComparison.OrdinalIgnoreCase);

    if (!isRoot)
    {
        var folderCatalog = new MsxRoot
        {
            Headline = Path.GetFileName(targetDir),
            Menu =
            [
                new()
                {
                    Icon = "movie",
                    Label = Path.GetFileName(targetDir),
                    Data = BuildMenuData(targetDir, scheme, host)
                }
            ]
        };
        return Results.Json(folderCatalog, jsonOptions);
    }

    // Root Menu
    var menuList = new List<MsxMenuItem>
    {
        // 1. Local Media Library
        new()
        {
            Icon = "video-library",
            Label = "My Media",
            ExtensionIcon = "folder-open",
            Data = BuildMenuData(downloadsFolder, scheme, host)
        }
    };

    // 2. TorrServer Integration
    var torrAvailable = await torrService.CheckTorrServerAsync(mediaConfig.TorrServer);
    if (!string.IsNullOrEmpty(torrAvailable))
    {
        menuList.Add(new MsxMenuItem
        {
            Icon = "bolt",
            ExtensionIcon = "cloud-queue",
            Label = "My Torrents",
            Action = $"content:{scheme}://{host}/msx/torr"
        });
    }

    // 3. Webmarks / Bookmarks
    menuList.Add(new MsxMenuItem
    {
        Icon = "bookmarks",
        ExtensionIcon = "cloud-queue",
        Label = "Bookmarks",
        Action = $"content:{scheme}://{host}/msx/webmarks"
    });

    // Separator
    menuList.Add(new MsxMenuItem { Type = "separator" });

    // 4. Settings
    menuList.Add(new MsxMenuItem
    {
        Id = "stg",
        Icon = "settings",
        Label = "Settings",
        Action = $"content:{scheme}://{host}/msx/settings"
    });

    object? readyAction = null;
    if (mediaConfig.VideoWall >= 1 && mediaConfig.VideoWall <= 6)
    {
        readyAction = new Dictionary<string, string>
        {
            ["action"] = $"execute:service:video:data:{scheme}://{host}/msx/videowall"
        };
    }

    var msxCatalog = new MsxRoot
    {
        Name = appName,
        Version = appVersion,
        Headline = "TorrentBot Media Library",
        Ready = readyAction,
        Menu = menuList
    };

    return Results.Json(msxCatalog, jsonOptions);
});

// 3. Serve folder content page for navigation into subdirectories
app.MapGet("/msx/content.json", (HttpContext context, string? path) =>
{
    var scheme = context.Request.Scheme;
    var host = context.Request.Host.Value ?? "localhost:5208";

    var targetDir = ResolveTargetDir(path);
    if (targetDir == null || !Directory.Exists(targetDir))
    {
        return Results.NotFound();
    }

    var menuData = BuildMenuData(targetDir, scheme, host);
    return Results.Json(menuData, jsonOptions);
});

app.MapGet("/msx/content", (HttpContext context, string? path) =>
{
    var scheme = context.Request.Scheme;
    var host = context.Request.Host.Value ?? "localhost:5208";

    var targetDir = ResolveTargetDir(path);
    if (targetDir == null || !Directory.Exists(targetDir))
    {
        return Results.NotFound();
    }

    var menuData = BuildMenuData(targetDir, scheme, host);
    return Results.Json(menuData, jsonOptions);
});

// 4. TorrServer Integration Endpoint: /msx/torr
app.MapGet("/msx/torr", async (HttpContext context, string? link, string? del, string? drop, string? add, string? ttl, string? img) =>
{
    var scheme = context.Request.Scheme;
    var host = context.Request.Host.Value ?? "localhost:5208";

    // Handle Delete
    if (!string.IsNullOrEmpty(del))
    {
        await torrService.RemoveTorrentAsync(mediaConfig.TorrServer, del);
        return Results.Json(new { response = new { status = 200, data = new { action = "reload:content" } } });
    }

    // Handle Drop
    if (!string.IsNullOrEmpty(drop))
    {
        await torrService.DropTorrentAsync(mediaConfig.TorrServer, drop);
        return Results.Json(new { response = new { status = 200, data = new { action = "reload:content" } } });
    }

    // Handle Add
    if (!string.IsNullOrEmpty(add))
    {
        await torrService.AddTorrentAsync(mediaConfig.TorrServer, add, ttl, img);
        return Results.Json(new { response = new { status = 200, data = new { action = "info:Torrent added" } } });
    }

    // Inspect stream files for a specific torrent link or hash
    if (!string.IsNullOrEmpty(link))
    {
        var streamInfo = await torrService.GetStreamInfoAsync(mediaConfig.TorrServer, link);
        if (streamInfo == null)
        {
            return Results.Json(new MsxMenuData
            {
                Type = "pages",
                Headline = "TorrServer Stream",
                Items =
                [
                    new MsxMediaItem
                    {
                        Title = "Torrent loading or offline...",
                        Icon = "error",
                        Action = "back"
                    }
                ]
            }, jsonOptions);
        }

        var torrentTitle = streamInfo.Title;
        var torrBase = TorrServerService.NormalizeHost(mediaConfig.TorrServer);
        var streamItems = new List<MsxMediaItem>();

        foreach (var file in streamInfo.FileStats.OrderBy(f => f.Path, NaturalStringComparer.Instance))
        {
            var fileName = Path.GetFileName(file.Path);
            var ext = Path.GetExtension(fileName);
            var isVideo = videoExtensions.Contains(ext);
            var isAudio = audioExtensions.Contains(ext);

            if (isVideo || isAudio)
            {
                var safePath = string.Join("/", file.Path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString));
                var streamUrl = $"{torrBase}/stream/{safePath}?play&link={Uri.EscapeDataString(link)}&index={file.Id}";

                streamItems.Add(new MsxMediaItem
                {
                    Title = fileName,
                    PlayerLabel = fileName,
                    Icon = isVideo ? "msx-white-soft:movie" : "msx-white-soft:audiotrack",
                    Group = isVideo ? "Video" : "Audio",
                    ExtensionLabel = FormatFileSize(file.Length),
                    Action = BuildPlayerUrl(streamUrl, isVideo, mediaConfig.EnableHtml5x),
                    Properties = GetPlayerProperties()
                });
            }
        }

        var contentData = new MsxMenuData
        {
            Type = "pages",
            Headline = torrentTitle,
            Extension = $"{FormatFileSize(streamInfo.TorrentSize)} | {streamInfo.ActivePeers}/{streamInfo.TotalPeers} peers",
            Template = new MsxTemplate
            {
                Type = "separate",
                Layout = "0,0,2,4",
                Icon = "msx-white-soft:movie",
                Color = "msx-glass",
                ImageFiller = "cover",
                Properties = GetPlayerProperties()
            },
            Items = streamItems
        };

        return Results.Json(contentData, jsonOptions);
    }

    // List all torrents in TorrServer
    var torrents = await torrService.GetTorrentsAsync(mediaConfig.TorrServer);
    var items = new List<MsxMediaItem>();
    string[] statusColors = ["yellow", "yellow", "green", "white-soft", "yellow", "red"];

    foreach (var t in torrents)
    {
        var color = (t.Stat >= 0 && t.Stat < statusColors.Length) ? statusColors[t.Stat] : "white-soft";
        var statusText = t.StatString ?? $"Status {t.Stat}";

        items.Add(new MsxMediaItem
        {
            Id = t.Hash,
            Title = t.Title,
            Headline = t.Title,
            Image = t.Poster,
            Icon = "msx-glass:bolt",
            TitleFooter = $"{{col:msx-{color}}}{{ico:flag}} {statusText}{{br}}{{ico:attach-file}} {FormatFileSize(t.TorrentSize)}{{tb}}{{ico:arrow-upward}} {t.ActivePeers}/{t.TotalPeers}",
            Action = $"content:{scheme}://{host}/msx/torr?link={t.Hash}"
        });
    }

    var listData = new MsxMenuData
    {
        Type = "pages",
        Headline = "My Torrents",
        Extension = $"TorrServer: {mediaConfig.TorrServer}",
        Template = new MsxTemplate
        {
            Type = "separate",
            Layout = "0,0,2,4",
            Icon = "bolt",
            Color = "msx-glass",
            ImageFiller = "cover",
            ImageWidth = 1.25
        },
        Items = items
    };

    return Results.Json(listData, jsonOptions);
});

// 5. Webmarks / Stream Bookmarks: /msx/webmarks
app.MapGet("/msx/webmarks", (HttpContext context) =>
{
    var bookmarks = webmarksService.GetWebmarks();
    var items = new List<MsxMediaItem>();

    foreach (var b in bookmarks)
    {
        string action;
        string icon;
        string group;

        if (b.Type == "youtube" || b.Url.Contains("youtube.com") || b.Url.Contains("youtu.be"))
        {
            var ytId = b.Url;
            if (ytId.Contains("v=")) ytId = ytId.Split("v=")[1].Split('&')[0];
            else if (ytId.Contains("youtu.be/")) ytId = ytId.Split("youtu.be/")[1].Split('?')[0];

            action = $"video:plugin:http://msx.benzac.de/plugins/youtube.html?id={ytId}";
            icon = "msx-red:smart-display";
            group = "YouTube";
        }
        else
        {
            action = $"video:{b.Url}";
            icon = "msx-white-soft:cast";
            group = "Streams";
        }

        items.Add(new MsxMediaItem
        {
            Id = b.Id,
            Title = b.Title,
            PlayerLabel = b.Title,
            Icon = icon,
            Group = group,
            Action = action,
            Properties = GetPlayerProperties()
        });
    }

    var marksData = new MsxMenuData
    {
        Type = "pages",
        Headline = "Bookmarks",
        Template = new MsxTemplate
        {
            Type = "separate",
            Layout = "0,0,2,4",
            Icon = "msx-white-soft:cast",
            Color = "msx-glass",
            ImageFiller = "cover",
            Properties = GetPlayerProperties()
        },
        Items = items
    };

    return Results.Json(marksData, jsonOptions);
});

// 6. Background Video Wallpaper: /msx/videowall
app.MapGet("/msx/videowall", () =>
{
    if (mediaConfig.VideoWall < 1 || mediaConfig.VideoWall > 6)
    {
        return Results.Json(new { response = new { status = 200, data = new { action = "[]" } } });
    }

    var wallIdx = mediaConfig.VideoWall;
    var atmosUrl = $"http://msx.benzac.de/media/atmos{wallIdx}.mp4";

    return Results.Json(new
    {
        response = new
        {
            status = 200,
            data = new
            {
                action = $"video:auto:plugin:http://msx.benzac.de/plugins/background.html?url={Uri.EscapeDataString(atmosUrl)}",
                data = new
                {
                    playerLabel = $"Background Video {wallIdx}",
                    properties = new Dictionary<string, object>
                    {
                        ["control:type"] = "extended",
                        ["control:reuse"] = "play",
                        ["control:transparent"] = true,
                        ["label:duration"] = "{VALUE} {ico:repeat}"
                    }
                }
            }
        }
    });
});

// 7. Settings Info Panel for MSX
app.MapGet("/msx/settings", async (HttpContext context) =>
{
    var scheme = context.Request.Scheme;
    var host = context.Request.Host.Value ?? "localhost:5208";
    var torrVersion = await torrService.CheckTorrServerAsync(mediaConfig.TorrServer);
    var uptime = DateTime.UtcNow - startTime;

    var items = new List<MsxMediaItem>
    {
        new()
        {
            Type = "space",
            Layout = "0,0,16,2",
            Headline = $"{appName} v{appVersion}",
            TitleFooter = $"{{ico:http}} {host}{{br}}{{ico:hardware}} {RuntimeInformation.OSDescription}{{br}}{{ico:timer}} Uptime: {uptime.Hours}h {uptime.Minutes}m"
        },
        new()
        {
            Type = "control",
            Layout = "0,2,8,1",
            Icon = "bolt",
            Label = "TorrServer",
            ExtensionLabel = string.IsNullOrEmpty(torrVersion) ? "Offline" : torrVersion,
            Action = $"content:{scheme}://{host}/msx/torr"
        },
        new()
        {
            Type = "control",
            Layout = "8,2,8,1",
            Icon = "smart-display",
            Label = "HTML5X Player",
            ExtensionLabel = mediaConfig.EnableHtml5x ? "Enabled" : "Disabled",
            Action = "back"
        },
        new()
        {
            Type = "control",
            Layout = "0,3,8,1",
            Icon = "wallpaper",
            Label = "Video Wallpaper",
            ExtensionLabel = mediaConfig.VideoWall == 0 ? "Off" : $"Atmos {mediaConfig.VideoWall}",
            Action = "back"
        },
        new()
        {
            Type = "control",
            Layout = "8,3,8,1",
            Icon = "bookmarks",
            Label = "Bookmarks",
            Action = $"content:{scheme}://{host}/msx/webmarks"
        }
    };

    var settingsData = new MsxMenuData
    {
        Type = "pages",
        Headline = "Settings",
        Compress = true,
        Template = new MsxTemplate
        {
            Type = "separate",
            Layout = "0,0,8,1",
            Color = "msx-glass"
        },
        Items = items
    };

    return Results.Json(settingsData, jsonOptions);
});

// 8. Cover art scraper (nnm-club forum)
app.MapGet("/cover", async (HttpContext context) =>
{
    var topicUrl = "https://nnmclub.to/forum/viewtopic.php?t=1898399";
    var coverUrl = await NnmClubCoverScraper.GetCoverUrlAsync(topicUrl);

    if (!string.IsNullOrEmpty(coverUrl))
    {
        Console.WriteLine($"Discovered Cover URL: {coverUrl}");
        return Results.Ok(new { cover = coverUrl });
    }
    else
    {
        Console.WriteLine("Cover not found.");
        return Results.NotFound(new { message = "Cover not found" });
    }
});

await app.RunAsync();
