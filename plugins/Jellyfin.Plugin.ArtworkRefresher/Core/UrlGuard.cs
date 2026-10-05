using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;

namespace Jellyfin.Plugin.ArtworkRefresher.Core;

/// <summary>
/// Network safety rules for downloads: https only, no credentials in the address, no private
/// or local destinations, and a host allow-list per provider.
/// </summary>
public static class UrlGuard
{
    /// <summary>The hosts each provider may serve images and data from. Built in on purpose.</summary>
    public static readonly IReadOnlyDictionary<string, string[]> BuiltInHosts = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
    {
        [SourceIds.Tmdb] = ["api.themoviedb.org", "image.tmdb.org"],
        [SourceIds.Fanart] = ["webservice.fanart.tv", "assets.fanart.tv", "fanart.tv"],
        [SourceIds.Wikimedia] = ["commons.wikimedia.org", "upload.wikimedia.org", "wikipedia.org"],
        [SourceIds.OpenLibrary] = ["openlibrary.org", "covers.openlibrary.org", "archive.org"],
        [SourceIds.CoverArtArchive] = ["coverartarchive.org", "archive.org"],
        [SourceIds.GoogleCse] = ["www.googleapis.com"],
    };

    /// <summary>
    /// Checks whether an address is acceptable for a source.
    /// </summary>
    /// <param name="uri">The address.</param>
    /// <param name="sourceId">The provider id.</param>
    /// <param name="anyPublicHost">True when the host is not restricted to the provider list (search results).</param>
    /// <param name="extraHosts">Extra allowed hosts, only used when not empty.</param>
    /// <param name="reason">Why it was refused.</param>
    /// <returns>True when acceptable.</returns>
    public static bool IsAllowed(Uri uri, string sourceId, bool anyPublicHost, IReadOnlyCollection<string> extraHosts, out string reason)
    {
        reason = string.Empty;
        if (!uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps)
        {
            reason = "not https";
            return false;
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            reason = "credentials in address";
            return false;
        }

        var host = uri.IdnHost.TrimEnd('.').ToLowerInvariant();
        if (host.Length == 0 || host == "localhost" || host.EndsWith(".localhost", StringComparison.Ordinal)
            || host.EndsWith(".local", StringComparison.Ordinal) || host.EndsWith(".internal", StringComparison.Ordinal))
        {
            reason = "local host name";
            return false;
        }

        if (IPAddress.TryParse(host, out var literal))
        {
            if (IsPrivate(literal))
            {
                reason = "private address";
                return false;
            }

            if (!anyPublicHost)
            {
                reason = "ip literal not allowed";
                return false;
            }

            return true;
        }

        if (anyPublicHost)
        {
            return true;
        }

        var allowed = BuiltInHosts.TryGetValue(sourceId, out var builtIn) ? builtIn.AsEnumerable() : [];
        allowed = allowed.Concat(extraHosts);
        if (allowed.Any(a => HostMatches(host, a)))
        {
            return true;
        }

        reason = "host not allowed for " + sourceId;
        return false;
    }

    /// <summary>
    /// Tells whether an IP address is private, loopback, link-local or otherwise not public.
    /// </summary>
    /// <param name="address">The address.</param>
    /// <returns>True when it must not be contacted.</returns>
    public static bool IsPrivate(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any)
            || address.Equals(IPAddress.None))
        {
            return true;
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast || address.IsIPv6Teredo)
            {
                return true;
            }

            var b6 = address.GetAddressBytes();
            return (b6[0] & 0xFE) == 0xFC; // fc00::/7 unique local
        }

        var b = address.GetAddressBytes();
        return b[0] == 10
               || b[0] == 127
               || b[0] == 0
               || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
               || (b[0] == 192 && b[1] == 168)
               || (b[0] == 169 && b[1] == 254)
               || (b[0] == 100 && b[1] >= 64 && b[1] <= 127)
               || (b[0] == 192 && b[1] == 0 && b[2] == 0)
               || (b[0] == 198 && (b[1] == 18 || b[1] == 19))
               || b[0] >= 224;
    }

    private static bool HostMatches(string host, string allowed)
    {
        allowed = allowed.Trim().TrimEnd('.').ToLowerInvariant();
        return host == allowed || host.EndsWith("." + allowed, StringComparison.Ordinal);
    }
}
