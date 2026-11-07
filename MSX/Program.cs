using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using MSX;
using MSX.Models;
using System.Diagnostics;
using System.Runtime.InteropServices;
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

var app = builder.Build();

var mediaConfig = builder.Configuration
    .GetSection(MediaSettings.SectionName)
    .Get<MediaSettings>() ?? new MediaSettings();

var torrService = app.Services.GetRequiredService<TorrServerService>();
var webmarksService = app.Services.GetRequiredService<WebmarksService>();

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
        var anyImage = Directory.EnumerateFiles(folderPath, "*.*", SearchOption.TopDirectoryOnly)
            .FirstOrDefault(f => coverExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase));
        if (anyImage != null)
        {
            return BuildMediaUrl(anyImage, scheme, host);
        }
    }
    catch { }

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

// 0. Sleek Web UI Dashboard
app.MapGet("/", async (HttpContext context) =>
{
    var scheme = context.Request.Scheme;
    var host = context.Request.Host.Value ?? "localhost:5208";
    var startUrl = $"{scheme}://{host}/msx/start.json";
    var menuUrl = $"{scheme}://{host}/msx/menu.json";
    var torrVersion = await torrService.CheckTorrServerAsync(mediaConfig.TorrServer);
    var uptime = DateTime.UtcNow - startTime;

    var html = $$"""
    <!DOCTYPE html>
    <html lang="en">
    <head>
        <meta charset="UTF-8">
        <meta name="viewport" content="width=device-width, initial-scale=1.0">
        <title>{{appName}}</title>
        <style>
            :root {
                --bg: #0f172a;
                --card-bg: #1e293b;
                --accent: #38bdf8;
                --text: #f8fafc;
                --muted: #94a3b8;
                --border: #334155;
            }
            body {
                font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, sans-serif;
                background-color: var(--bg);
                color: var(--text);
                margin: 0;
                padding: 2rem;
                display: flex;
                flex-direction: column;
                align-items: center;
            }
            .container {
                max-width: 800px;
                width: 100%;
            }
            h1 {
                display: flex;
                align-items: center;
                gap: 0.5rem;
                color: var(--accent);
                margin-bottom: 0.25rem;
            }
            .lead {
                color: var(--muted);
                margin-top: 0;
                margin-bottom: 2rem;
            }
            .card {
                background: var(--card-bg);
                border: 1px solid var(--border);
                border-radius: 0.75rem;
                padding: 1.5rem;
                margin-bottom: 1.5rem;
                box-shadow: 0 4px 6px -1px rgba(0, 0, 0, 0.1);
            }
            .card h2 {
                margin-top: 0;
                font-size: 1.25rem;
                color: var(--accent);
            }
            .code-box {
                background: #090d16;
                padding: 0.75rem 1rem;
                border-radius: 0.5rem;
                font-family: monospace;
                font-size: 1.1rem;
                color: #a5f3fc;
                border: 1px dashed var(--border);
                display: flex;
                justify-content: space-between;
                align-items: center;
                margin-top: 0.5rem;
            }
            .status-badge {
                display: inline-block;
                padding: 0.25rem 0.6rem;
                border-radius: 9999px;
                font-size: 0.85rem;
                font-weight: 600;
            }
            .badge-green { background: rgba(34, 197, 94, 0.2); color: #4ade80; }
            .badge-yellow { background: rgba(234, 179, 8, 0.2); color: #facc15; }
            .badge-red { background: rgba(239, 68, 68, 0.2); color: #f87171; }
            table { width: 100%; border-collapse: collapse; margin-top: 0.5rem; }
            td { padding: 0.5rem 0; border-bottom: 1px solid var(--border); }
            td:first-child { color: var(--muted); width: 35%; }
            a { color: var(--accent); text-decoration: none; }
            a:hover { text-decoration: underline; }
            .btn {
                background: var(--accent);
                color: #0f172a;
                font-weight: 600;
                padding: 0.5rem 1rem;
                border-radius: 0.375rem;
                border: none;
                cursor: pointer;
            }
            .form-group {
                display: flex;
                gap: 0.5rem;
                margin-top: 0.75rem;
            }
            input[type="text"] {
                flex: 1;
                background: #090d16;
                border: 1px solid var(--border);
                color: var(--text);
                padding: 0.5rem 0.75rem;
                border-radius: 0.375rem;
            }
        </style>
    </head>
    <body>
        <div class="container">
            <h1>📺 {{appName}} <span style="font-size: 0.8rem; font-weight: normal; color: var(--muted)">v{{appVersion}}</span></h1>
            <p class="lead">MediaStation X content & TorrServer streaming hub</p>

            <div class="card">
                <h2>📺 Connect MediaStation X</h2>
                <p>On your Smart TV or browser (<a href="https://msx.benzac.de/" target="_blank">msx.benzac.de</a>), go to:
                <br><b>Settings ⚙️ &rarr; Start Parameter &rarr; Setup</b> and enter:</p>
                <div class="code-box">
                    <span>{{host}}/msx/start.json</span>
                </div>
            </div>

            <div class="card">
                <h2>⚡ System & Services Status</h2>
                <table>
                    <tr>
                        <td>Server Status</td>
                        <td><span class="status-badge badge-green">RUNNING</span> (Uptime: {{uptime.Hours}}h {{uptime.Minutes}}m {{uptime.Seconds}}s)</td>
                    </tr>
                    <tr>
                        <td>Platform</td>
                        <td>{{RuntimeInformation.OSDescription}} ({{RuntimeInformation.ProcessArchitecture}})</td>
                    </tr>
                    <tr>
                        <td>Media Directory</td>
                        <td><code>{{downloadsFolder}}</code></td>
                    </tr>
                    <tr>
                        <td>TorrServer</td>
                        <td>
                            {{(string.IsNullOrEmpty(torrVersion) 
                                ? $"<span class=\"status-badge badge-yellow\">NOT CONNECTED</span> ({mediaConfig.TorrServer})"
                                : $"<span class=\"status-badge badge-green\">ONLINE: {torrVersion}</span> ({mediaConfig.TorrServer})")}}
                        </td>
                    </tr>
                    <tr>
                        <td>MSX Catalogs</td>
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
    </body>
    </html>
    """;

    return Results.Content(html, "text/html");
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
