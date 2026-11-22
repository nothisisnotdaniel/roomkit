using System.Text.RegularExpressions;

namespace RoomKit;

/// <summary>The DNS-rebinding defence: a request is handled only when its whole <c>Host</c> header is exactly
/// <c>127.0.0.1:&lt;port&gt;</c>, <c>localhost:&lt;port&gt;</c> (the app's <see cref="ServerEndpoint.Port"/>) or the configured tailnet name (no port), ignoring letter case.
/// No wildcard, no suffix match and no trimming of a trailing dot.</summary>
internal static partial class HostCheck
{
    /// <summary>The config key (<c>--TailnetHost &lt;name&gt;</c>) for the <c>tailscale serve</c> name.</summary>
    public const string ConfigKey = "TailnetHost";

    /// <summary>The fixed body of a refused request.</summary>
    public const string RefusedBody = "{\"error\":\"bad_host\"}";

    /// <summary>The fixed text written when <c>TailnetHost</c> is not a valid name (the value is never echoed).</summary>
    public const string InvalidTailnetText = "TailnetHost must be a name like mac.tailnet.ts.net.";

    /// <summary>The longest DNS name.</summary>
    private const int MaxNameLength = 253;

    private static string[] LoopbackHosts =>
    [
        "127.0.0.1:" + ServerEndpoint.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
        "localhost:" + ServerEndpoint.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
    ];

    /// <summary>True when <paramref name="host"/> (the raw <c>Host</c> header) is one of the loopback names or
    /// equals <paramref name="tailnetHost"/> (when one is configured), ordinal ignoring case. A missing or empty
    /// host is refused.</summary>
    public static bool IsAllowed(string? host, string? tailnetHost = null)
    {
        if (string.IsNullOrEmpty(host))
        {
            return false;
        }

        foreach (string allowed in LoopbackHosts)
        {
            if (string.Equals(host, allowed, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return !string.IsNullOrEmpty(tailnetHost) && string.Equals(host, tailnetHost, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True for a lower-case DNS name of 1-63-character labels (<c>a-z</c>, <c>0-9</c>, <c>-</c>, no label
    /// starting or ending with <c>-</c>) ending in <c>.ts.net</c> with at least two labels before it.</summary>
    public static bool IsValidTailnetHost(string? value)
    {
        return value is not null && value.Length <= MaxNameLength && TailnetPattern().IsMatch(value);
    }

    /// <summary>Sends the fixed <c>421</c> refusal (the security headers are already set).</summary>
    public static async Task WriteRefused(HttpResponse response)
    {
        response.StatusCode = StatusCodes.Status421MisdirectedRequest;
        response.Headers.CacheControl = "no-store";
        response.ContentType = "application/json; charset=utf-8";
        await response.WriteAsync(RefusedBody);
    }

    [GeneratedRegex(@"\A(?:[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.){2,}ts\.net\z", RegexOptions.CultureInvariant)]
    private static partial Regex TailnetPattern();
}
