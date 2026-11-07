using System.Text.RegularExpressions;

namespace MSX;

public record ParsedMediaInfo(string Title, int? Year, int? Season, bool IsTv);

public static class MovieTitleParser
{
    private static readonly Regex TrackerPrefixRegex = new(
        @"^\[(?:new-rutor\.org|rutor\.info|rutor\.org|nnmclub\.to|rutracker\.org|kinozal\.tv|torrent)\]\s*",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex YearRegex = new(
        @"\b(19\d\d|20\d\d)\b",
        RegexOptions.Compiled);

    private static readonly Regex SeasonRegex = new(
        @"\b(?:S(\d{1,2})|Season\s*(\d{1,2})|Сезон\s*(\d{1,2}))\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex SceneNoiseRegex = new(
        @"\b(?:1080p|720p|2160p|4k|uhd|hd|bdrip|web-?dl(?:rip)?|webrip|hdtv|remux|dv|hdr10\+?|hdr|p7|torrnado|selezen|il68k|new-team|eniahd|tvshows|avc|x264|h264|h\.264|hevc|h\.265|x265|av1|xvid|divx|ac3|dts|eac3|ddp|dub|mvo(?:-rgb)?|dvo|vo|sub|nf|amzn|dsnp|ma|atvp|apple\s*tv|line|clean)\b.*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static ParsedMediaInfo Parse(string rawName)
    {
        // 1. Remove quotes and extensions
        var name = rawName.Trim('\'', '"', ' ');
        while (Path.HasExtension(name))
        {
            name = Path.GetFileNameWithoutExtension(name);
        }

        // 2. Strip tracker prefix like [new-rutor.org]
        name = TrackerPrefixRegex.Replace(name, "").Trim();

        int? year = null;
        int? season = null;
        bool isTv = false;

        // 3. Extract Season if present
        var seasonMatch = SeasonRegex.Match(name);
        if (seasonMatch.Success)
        {
            isTv = true;
            for (int i = 1; i <= 3; i++)
            {
                if (seasonMatch.Groups[i].Success && int.TryParse(seasonMatch.Groups[i].Value, out var s))
                {
                    season = s;
                    break;
                }
            }
        }

        // 4. Split title at year or season or scene noise
        var yearMatch = YearRegex.Match(name);
        string titlePart = name;

        if (yearMatch.Success)
        {
            year = int.Parse(yearMatch.Value);
            titlePart = name.Substring(0, yearMatch.Index);
        }
        else if (seasonMatch.Success)
        {
            titlePart = name.Substring(0, seasonMatch.Index);
        }
        else
        {
            var noiseMatch = SceneNoiseRegex.Match(name);
            if (noiseMatch.Success)
            {
                titlePart = name.Substring(0, noiseMatch.Index);
            }
        }

        // 5. Clean punctuation in titlePart (dots, underscores, brackets to spaces)
        titlePart = Regex.Replace(titlePart, @"[\._\(\)\[\]\-]+", " ");
        titlePart = Regex.Replace(titlePart, @"\s+", " ").Trim();

        // Fallback if empty
        if (string.IsNullOrWhiteSpace(titlePart))
        {
            titlePart = Regex.Replace(name, @"[\._\-]+", " ").Trim();
        }

        return new ParsedMediaInfo(titlePart, year, season, isTv);
    }
}
