using Microsoft.Extensions.FileProviders;
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

// Fallback to home Downloads directory if config path doesn't exist
var downloadsFolder = Directory.Exists(mediaConfig.DownloadPath)
    ? mediaConfig.DownloadPath
    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

Directory.CreateDirectory(downloadsFolder);
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

app.MapGet("/menu.json", (HttpContext context) =>
{
    var scheme = context.Request.Scheme;
    var host = context.Request.Host.Value;
    var allowedExtensions = new HashSet<string>(mediaConfig.AllowedExtensions, StringComparer.OrdinalIgnoreCase);

    var videoItems = new List<MsxMediaItem>();

    if (Directory.Exists(downloadsFolder))
    {
        var files = Directory.EnumerateFiles(downloadsFolder, "*.*", SearchOption.TopDirectoryOnly)
            .Where(f => allowedExtensions.Contains(Path.GetExtension(f)))
            .OrderByDescending(File.GetLastWriteTimeUtc);

        foreach (var file in files)
        {
            var fileName = Path.GetFileName(file);
            var safeUrlEncoded = Uri.EscapeDataString(fileName);

            videoItems.Add(new MsxMediaItem
            {
                Title = Path.GetFileNameWithoutExtension(fileName),
                Action = $"video:{scheme}://{host}/media/{safeUrlEncoded}"
            });
        }
    }

    var msxCatalog = new MsxRoot
    {
        Headline = "Raspberry Pi 5 Media",
        Menu =
        [
            new()
            {
                Icon = "movie",
                Label = "Downloads",
                Data = new MsxMenuData
                {
                    Type = "pages",
                    Template = new MsxTemplate
                    {
                        Type = "separate",
                        Layout = "0,0,2,4",
                        Icon = "msx-white-soft:movie",
                        Color = "msx-glass"
                    },
                    Items = videoItems
                }
            }
        ]
    };

    return Results.Json(msxCatalog, new JsonSerializerOptions
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true
    });
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
  
    var videoItems = new List<MsxMediaItem>();

    // 1. Create a case-insensitive hash set from the configured extensions
    var allowedExtensions = new HashSet<string>(
        mediaConfig.AllowedExtensions ?? [".mp4", ".mkv", ".avi", ".mov", ".webm"],
        StringComparer.OrdinalIgnoreCase
    );

    // 2. Filter files ignoring case
    if (Directory.Exists(downloadsFolder))
    {
        var files = Directory.EnumerateFiles(downloadsFolder, "*.*", SearchOption.TopDirectoryOnly)
            .Where(f => allowedExtensions.Contains(Path.GetExtension(f)))
            .OrderByDescending(File.GetLastWriteTimeUtc);

        foreach (var file in files)
        {
            var fileName = Path.GetFileName(file);
            var safeUrlEncoded = Uri.EscapeDataString(fileName);

            videoItems.Add(new MsxMediaItem
            {
                Title = Path.GetFileNameWithoutExtension(fileName),
                Action = $"video:{scheme}://{host}/media/{safeUrlEncoded}"
            });
        }
    }

    var msxCatalog = new MsxRoot
    {
        Headline = "My Example Content",
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
                        Color = "msx-glass"
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
