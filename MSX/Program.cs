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

// 2. Serve the dynamic menu catalog
app.MapGet("/msx/menu.json", (HttpContext context) =>
{
    var scheme = context.Request.Scheme;
    var host = context.Request.Host.Value;

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

    // Helper: Build safe, slash-preserving media URLs
    string BuildMediaUrl(string fullFilePath)
    {
        var relative = Path.GetRelativePath(downloadsFolder, fullFilePath).Replace('\\', '/');
        var safeUrlPath = string.Join("/", relative.Split('/').Select(Uri.EscapeDataString));
        return $"{scheme}://{host}/media/{safeUrlPath}";
    }

    // Helper: Discover matching file-level cover art (VideoName.ext -> VideoName.jpg)
    string? FindCoverUrl(string videoFilePath)
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
                return BuildMediaUrl(candidate);
            }
        }

        return null;
    }

    var videoItems = new List<MsxMediaItem>();

    if (Directory.Exists(downloadsFolder))
    {
        var files = Directory.EnumerateFiles(downloadsFolder, "*.*", SearchOption.AllDirectories)
            .Where(f => allowedExtensions.Contains(Path.GetExtension(f)))
            .OrderByDescending(File.GetLastWriteTimeUtc);

        foreach (var file in files)
        {
            var directoryPath = Path.GetDirectoryName(file);
            var parentFolder = string.IsNullOrEmpty(directoryPath) ? null : Path.GetFileName(directoryPath);
            var fileNameWithoutExt = Path.GetFileNameWithoutExtension(file);

            var title = string.IsNullOrEmpty(parentFolder) || parentFolder.Equals(Path.GetFileName(downloadsFolder), StringComparison.OrdinalIgnoreCase)
                ? fileNameWithoutExt
                : $"{parentFolder} - {fileNameWithoutExt}";

            videoItems.Add(new MsxMediaItem
            {
                Title = title,
                Action = $"video:{BuildMediaUrl(file)}",
                Image = FindCoverUrl(file)
            });
        }
    }

    var msxCatalog = new MsxRoot
    {
        Headline = "TorrentBot Media Library",
        Menu = new List<MsxMenuItem>
        {
            new()
            {
                Icon = "movie",
                Label = "Videos",
                Data = new MsxMenuData
                {
                    Type = "pages",
                    Template = new MsxTemplate
                    {
                        Type = "separate",
                        Layout = "0,0,2,4",
                        Icon = "msx-white-soft:movie",
                        Color = "msx-glass",
                        ImageFiller = "cover"
                    },
                    Items = videoItems
                }
            }
        }
    };

    return Results.Json(msxCatalog, new JsonSerializerOptions
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true
    });
});

await app.RunAsync();
