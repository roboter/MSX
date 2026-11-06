namespace MSX;

using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using HtmlAgilityPack;

public static class NnmClubCoverScraper
{
    static NnmClubCoverScraper()
    {
        // Ensure legacy Windows-1251 and KOI8-R are supported
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    private static readonly HttpClient HttpClient = new(new HttpClientHandler
    {
        AllowAutoRedirect = true,
        AutomaticDecompression = System.Net.DecompressionMethods.All
    })
    {
        DefaultRequestHeaders =
        {
            { "User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36" },
            { "Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,image/webp,*/*;q=0.8" },
            { "Accept-Language", "ru-RU,ru;q=0.9,en-US;q=0.8,en;q=0.7" }
        }
    };

    public static async Task<string?> GetCoverUrlAsync(string topicUrl, CancellationToken cancellationToken = default)
    {
        try
        {
            // 1. Fetch raw bytes directly to prevent HttpClient from choking on the Content-Type header
            byte[] pageBytes = await HttpClient.GetByteArrayAsync(topicUrl, cancellationToken);

            var doc = new HtmlDocument();

            using var memoryStream = new MemoryStream(pageBytes);
            // Load via Stream with Windows-1251 encoding
            doc.Load(memoryStream, Encoding.GetEncoding("windows-1251"));

            // 2. Select post body
            var postBody = doc.DocumentNode.SelectSingleNode("//div[contains(@class, 'postbody')]")
                           ?? doc.DocumentNode.SelectSingleNode("//td[contains(@class, 'message')]");

            if (postBody == null)
            {
                return null;
            }

            // 3. Locate the primary cover element
            var coverImg = postBody.SelectSingleNode(".//var[contains(@class, 'postImg')]/img")
                           ?? postBody.SelectSingleNode(".//img[contains(@class, 'postImg')]")
                           ?? postBody.SelectSingleNode(".//var[contains(@class, 'postImg')]")
                           ?? postBody.SelectSingleNode(".//img[contains(@class, 'postImgAligned')]");

            if (coverImg != null)
            {
                var src = coverImg.GetAttributeValue("data-src", null)
                          ?? coverImg.GetAttributeValue("title", null)
                          ?? coverImg.GetAttributeValue("src", null);

                if (src is not null && IsValidImageUrl(src))
                {
                    return NormalizeUrl(src);
                }
            }

            // 4. Fallback: Search link hrefs inside the post pointing to images
            var imgLinks = postBody.SelectNodes(".//a[@href]");
            if (imgLinks != null)
            {
                foreach (var link in imgLinks)
                {
                    var href = link.GetAttributeValue("href", string.Empty);
                    if (Regex.IsMatch(href, @"\.(jpe?g|png|webp)(\?.*)?$", RegexOptions.IgnoreCase))
                    {
                        return NormalizeUrl(href);
                    }
                }
            }

            // 5. Fallback: First valid img node excluding smilies
            var anyImg = postBody.SelectNodes(".//img")?
                .FirstOrDefault(img =>
                {
                    var s = img.GetAttributeValue("src", string.Empty);
                    return !s.Contains("images/smiles") && !s.Contains("icon_") && IsValidImageUrl(s);
                });

            return anyImg != null ? NormalizeUrl(anyImg.GetAttributeValue("src", string.Empty)) : null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Scraper] Failed to fetch cover from {topicUrl}: {ex.Message}");
            return null;
        }
    }

    private static bool IsValidImageUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        return url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
               url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
               url.StartsWith("//");
    }

    private static string NormalizeUrl(string url)
    {
        if (url.StartsWith("//"))
        {
            return "https:" + url;
        }
        return url;
    }

}