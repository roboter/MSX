namespace MSX
{
    public sealed class MediaSettings
    {
        public const string SectionName = "MediaSettings";
        public string DownloadPath { get; set; } = "/home/roboter/Downloads";
        public string[] AllowedExtensions { get; set; } = [".mp4", ".mkv", ".avi", ".mov", ".webm"];

        public string[] CoverExtensions { get; set; } =
    [
        ".jpg",
        ".jpeg",
        ".png",
        ".webp"
    ];
    }
}
