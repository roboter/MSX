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

await app.RunAsync();