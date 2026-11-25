namespace BiliDownloader.Services.Download;

/// <summary>Preserves the original signed media address, followed by API-provided backups.</summary>
public static class CdnUrlHelper
{
    public static List<string> FilterAndSortUrls(string baseUrl, List<string>? backupUrls)
        => new[] { baseUrl }.Concat(backupUrls ?? [])
            .Where(url => Uri.TryCreate(url, UriKind.Absolute, out var uri)
                && (uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)))
            .Distinct(StringComparer.Ordinal)
            .ToList();
}
