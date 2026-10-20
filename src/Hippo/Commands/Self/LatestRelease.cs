using System.Net.Http.Headers;
using System.Text.RegularExpressions;

namespace Hippo.Commands.Self;

/// <summary>
/// Which release is the latest, read as the install scripts read it when run by hand: from the redirect GitHub
/// answers <c>releases/latest</c> with, which ends in the release's tag. It is not metered and skips prereleases.
/// The request goes through a handler that follows no redirects, since following one lands on the tag page, which
/// has no <c>Location</c>. Under <c>self update</c> the script never looks the release up itself, since hippo sets
/// <c>HIPPO_VERSION</c>, so where hippo looks is hippo's own rule: see <see cref="Url"/>.
/// </summary>
internal static partial class LatestRelease
{
    public const string GitHubUrl = "https://github.com/alisterpineda/hippo/releases/latest";

    private const string DownloadSuffix = "/releases/download";

    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    /// <summary>What a lookup found: the version, or why there is none, in one line.</summary>
    public sealed record Result(SemVer? Version, string? Failure);

    /// <summary>The URL to look the latest release up at: <paramref name="downloadBase"/>, the value of
    /// <c>HIPPO_DOWNLOAD_BASE</c>, with its trailing <c>/releases/download</c> replaced by <c>/releases/latest</c>,
    /// mirroring how the scripts build both from one repository URL; GitHub's when it is unset or ends some other
    /// way. The scripts themselves always look up on GitHub, so this rule is hippo's alone; the script headers and
    /// docs/dev/releasing.md record it.</summary>
    public static string Url(string? downloadBase) =>
        downloadBase is not null && downloadBase.EndsWith(DownloadSuffix, StringComparison.Ordinal)
            ? downloadBase[..^DownloadSuffix.Length] + "/releases/latest"
            : GitHubUrl;

    /// <summary>The version in <paramref name="location"/>, a redirect's target, absolute or relative, when it ends in
    /// <c>/tag/v&lt;version&gt;</c>; null otherwise.</summary>
    public static SemVer? VersionIn(Uri? location)
    {
        if (location is null)
        {
            return null;
        }
        var match = Tag().Match(location.OriginalString);
        return match.Success ? SemVer.TryParse(match.Groups["version"].Value) : null;
    }

    /// <summary>The handler the real lookup sends through. A fake one cannot show the setting that matters, so this
    /// is tested on its own.</summary>
    public static HttpMessageHandler CreateHandler() => new SocketsHttpHandler { AllowAutoRedirect = false };

    /// <summary>A client over <paramref name="handler"/> that gives up after <see cref="Timeout"/> and names hippo and
    /// its version, as GitHub asks.</summary>
    public static HttpClient CreateClient(HttpMessageHandler handler, SemVer current)
    {
        var client = new HttpClient(handler) { Timeout = Timeout };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("hippo", current.ToString()));
        return client;
    }

    /// <summary>Sends one HEAD to <paramref name="url"/> through <paramref name="handler"/> and reads the version from
    /// the redirect. Only a 301 or 302 whose <c>Location</c> ends in a tag counts; anything else, a failed request, a
    /// timeout or a URL the client refuses to send to is a failure with a one-line reason.</summary>
    public static Result Find(HttpMessageHandler handler, string url, SemVer current)
    {
        using var client = CreateClient(handler, current);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, url);
            using var response = client.SendAsync(request).GetAwaiter().GetResult();
            var status = (int)response.StatusCode;
            if (status is not (301 or 302))
            {
                return new Result(null, $"{url} answered HTTP {status} rather than a redirect to the release");
            }
            if (response.Headers.Location is not { } location)
            {
                return new Result(null, $"{url} redirected without saying where");
            }
            return VersionIn(location) is { } version
                ? new Result(version, null)
                : new Result(null, $"{url} redirected to {location.OriginalString}, which names no release");
        }
        // The last three are the client's refusals of the URL itself: not absolute, or a scheme it cannot send to.
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or NotSupportedException or UriFormatException)
        {
            return new Result(null, $"could not reach {url}: {ex.Message}");
        }
        catch (OperationCanceledException)
        {
            return new Result(null, $"no answer from {url} within {Timeout.TotalSeconds:0} s");
        }
    }

    [GeneratedRegex(@"/tag/v(?<version>[^/]+)$")]
    private static partial Regex Tag();
}
