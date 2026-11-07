using Microsoft.Extensions.FileProviders;
using MSX;
using MSX.Models;
using System.Text.Json;
using System.Text.Json.Serialization;


var builder = WebApplication.CreateBuilder(args);

// Register settings
builder.Services.Configure<MediaSettings>(
    builder.Configuration.GetSection(MediaSettings.SectionName));

var app = builder.Build();

var mediaConfig = builder.Configuration
    .GetSection(MediaSettings.SectionName)
    .Get<MediaSettings>() ?? new MediaSettings();

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

    // Fallback to ~/Downloads/TorrentNOTComplete or ~/Downloads
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
    ServeUnknownFileTypes = true // Permits .mkv and non-standard web video containers
});

app.MapGet("/", () => "Hello World!");

app.MapGet("/cover",  async (HttpContext context) =>
{
    var topicUrl = "https://nnmclub.to/forum/viewtopic.php?t=1898399";
    var coverUrl = await NnmClubCoverScraper.GetCoverUrlAsync(topicUrl);

    if (!string.IsNullOrEmpty(coverUrl))
    {
        Console.WriteLine($"Discovered Cover URL: {coverUrl}");
    }
    else
    {
        Console.WriteLine("Cover not found.");
    }
});

// 1. Serve start.json with dynamic {SERVER} replacement
app.MapGet("/msx/start.json", async (HttpContext context) =>
{
    var host = context.Request.Host.Value;
    var scheme = context.Request.Scheme;

    // Check content root first, then binary folder, then fallback to embedded string
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
        // Safe in-memory fallback: never throws 404
        template = """
        {
          "name": "My Example Content",
          "version": "1.0.0",
          "parameter": "menu:{SCHEME}://{SERVER}/msx/menu.json"
        }
        """;
    }

    var resolved = template
        .Replace("{SERVER}", host)
        .Replace("{SCHEME}", scheme);

    return Results.Content(resolved, "application/json");
});

// Filter out .avi if present and match case-insensitively
var configuredAllowed = mediaConfig.AllowedExtensions?
    .Where(ext => !ext.Equals(".avi", StringComparison.OrdinalIgnoreCase))
    .ToArray();

var allowedExtensions = new HashSet<string>(
    (configuredAllowed != null && configuredAllowed.Length > 0)
        ? configuredAllowed
        : [".mp4", ".mkv", ".mov", ".webm"],
    StringComparer.OrdinalIgnoreCase
);

// Validate CoverExtensions with fallback
var coverExtensions = (mediaConfig.CoverExtensions != null && mediaConfig.CoverExtensions.Length > 0)
    ? mediaConfig.CoverExtensions
    : [".jpg", ".jpeg", ".png", ".webp"];

var jsonOptions = new JsonSerializerOptions
{
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = true
};

// Helper: Safely resolve relative path to absolute directory within downloadsFolder
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

// Helper: Build safe, slash-preserving media URLs
string BuildMediaUrl(string fullFilePath, string scheme, string host)
{
    var relative = Path.GetRelativePath(downloadsFolder, fullFilePath).Replace('\\', '/');
    var safeUrlPath = string.Join("/", relative.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString));
    return $"{scheme}://{host}/media/{safeUrlPath}";
}

// Helper: Build safe, slash-preserving folder content URLs
string BuildContentUrl(string relativePath, string scheme, string host)
{
    var relative = relativePath.Replace('\\', '/');
    var safeUrlPath = string.Join("/", relative.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString));
    return $"{scheme}://{host}/msx/content.json?path={safeUrlPath}";
}

// Helper: Discover matching file-level cover art (VideoName.ext -> VideoName.jpg)
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

// Helper: Discover matching folder-level cover art
string? FindFolderCoverUrl(string folderPath, string scheme, string host)
{
    if (string.IsNullOrEmpty(folderPath) || !Directory.Exists(folderPath))
        return null;

    var folderName = Path.GetFileName(folderPath);

    // 1. Check for standard cover names or folder name
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

    // 2. Check for any image file directly in the folder
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

    // 3. Fallback: check if the first video in the folder has a matching cover
    try
    {
        var firstVideo = Directory.EnumerateFiles(folderPath, "*.*", SearchOption.TopDirectoryOnly)
            .FirstOrDefault(f => allowedExtensions.Contains(Path.GetExtension(f)));
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

// Helper: Check if directory contains any allowed media files (directly or nested)
bool HasMediaFiles(string dirPath)
{
    try
    {
        return Directory.EnumerateFiles(dirPath, "*.*", SearchOption.AllDirectories)
            .Any(f => allowedExtensions.Contains(Path.GetExtension(f)));
    }
    catch
    {
        return false;
    }
}

// Helper: Get folder and file items for a directory, keeping folders as folders and sorting files by name
List<MsxMediaItem> GetDirectoryItems(string targetDir, string scheme, string host)
{
    var items = new List<MsxMediaItem>();

    if (!Directory.Exists(targetDir))
        return items;

    // 1. Folders: stay folders, sorted naturally by name
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
        var files = Directory.EnumerateFiles(targetDir, "*.*", SearchOption.TopDirectoryOnly)
            .Where(f => !Path.GetFileName(f).StartsWith('.') && allowedExtensions.Contains(Path.GetExtension(f)))
            .OrderBy(f => Path.GetFileNameWithoutExtension(f), NaturalStringComparer.Instance);

        foreach (var file in files)
        {
            var fileNameWithoutExt = Path.GetFileNameWithoutExtension(file);

            items.Add(new MsxMediaItem
            {
                Title = fileNameWithoutExt,
                Action = $"video:{BuildMediaUrl(file, scheme, host)}",
                Image = FindCoverUrl(file, scheme, host)
            });
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
    return new MsxMenuData
    {
        Type = "pages",
        Headline = isRoot ? null : Path.GetFileName(targetDir),
        Template = new MsxTemplate
        {
            Type = "separate",
            Layout = "0,0,2,4",
            Icon = "msx-white-soft:movie",
            Color = "msx-glass",
            ImageFiller = "cover"
        },
        Items = GetDirectoryItems(targetDir, scheme, host)
    };
}

// 2. Serve the dynamic menu catalog
app.MapGet("/msx/menu.json", (HttpContext context, string? path) =>
{
    var scheme = context.Request.Scheme;
    var host = context.Request.Host.Value ?? "localhost";

    var targetDir = ResolveTargetDir(path);
    if (targetDir == null || !Directory.Exists(targetDir))
    {
        return Results.NotFound();
    }

    var isRoot = targetDir.Equals(downloadsFolder, StringComparison.OrdinalIgnoreCase);
    var headline = isRoot ? "TorrentBot Media Library" : Path.GetFileName(targetDir);
    var label = isRoot ? "Videos" : Path.GetFileName(targetDir);

    var msxCatalog = new MsxRoot
    {
        Headline = headline,
        Menu = new List<MsxMenuItem>
        {
            new()
            {
                Icon = "movie",
                Label = label,
                Data = BuildMenuData(targetDir, scheme, host)
            }
        }
    };

    return Results.Json(msxCatalog, jsonOptions);
});

// 3. Serve folder content page for navigation into subdirectories
app.MapGet("/msx/content.json", (HttpContext context, string? path) =>
{
    var scheme = context.Request.Scheme;
    var host = context.Request.Host.Value ?? "localhost";

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
    var host = context.Request.Host.Value ?? "localhost";

    var targetDir = ResolveTargetDir(path);
    if (targetDir == null || !Directory.Exists(targetDir))
    {
        return Results.NotFound();
    }

    var menuData = BuildMenuData(targetDir, scheme, host);
    return Results.Json(menuData, jsonOptions);
});

await app.RunAsync();
