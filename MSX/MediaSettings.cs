namespace MSX;

public sealed class MediaSettings
{
    public const string SectionName = "MediaSettings";

    public string DownloadPath { get; set; } = "/home/roboter/Downloads";

    /// <summary>
    /// Address of TorrServer instance (e.g. "http://localhost:8090" or "192.168.1.100:8090").
    /// Defaults to empty (disabled) so it does not attempt connection when TorrServer is not running.
    /// </summary>
    public string TorrServer { get; set; } = string.Empty;

    /// <summary>
    /// Optional The Movie Database (TMDB) API Key for automatic poster discovery
    /// </summary>
    public string TmdbApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Supported video file extensions
    /// </summary>
    public string[] AllowedExtensions { get; set; } =
    [
        ".mp4", ".mkv", ".avi", ".mov", ".webm",
        ".ts", ".mts", ".m2ts", ".wmv", ".flv",
        ".mpg", ".mpeg", ".m4v"
    ];

    /// <summary>
    /// Supported audio file extensions
    /// </summary>
    public string[] AudioExtensions { get; set; } =
    [
        ".mp3", ".m4a", ".flac", ".wav", ".wma", ".aac", ".ogg"
    ];

    /// <summary>
    /// Supported photo file extensions
    /// </summary>
    public string[] PhotoExtensions { get; set; } =
    [
        ".jpg", ".jpeg", ".png", ".webp"
    ];

    /// <summary>
    /// Cover artwork extensions
    /// </summary>
    public string[] CoverExtensions { get; set; } =
    [
        ".jpg", ".jpeg", ".png", ".webp"
    ];

    /// <summary>
    /// Background video wallpaper preset (0 = None, 1..6 = Atmos 1..6)
    /// </summary>
    public int VideoWall { get; set; } = 0;

    /// <summary>
    /// Whether to wrap playback URLs with the MSX HTML5X plugin for enhanced codec compatibility
    /// </summary>
    public bool EnableHtml5x { get; set; } = false;

    /// <summary>
    /// Whether to render compressed grid layout (more columns on screen)
    /// </summary>
    public bool CompressedLayout { get; set; } = false;
}
