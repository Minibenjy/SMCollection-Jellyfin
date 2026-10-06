using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Security.Cryptography;
using System.Text;

namespace Jellyfin.Plugin.ArtworkRefresher.Core;

/// <summary>
/// Signs the address of a rotating image: the signature authorises one item and image type until a time.
/// The address of an image can then be fetched without a token (an image tag cannot send one), but only
/// by a browser that was first told about it through the authenticated "available" call. It stops
/// guessing item ids and stops anonymous visitors from making the server download images.
/// </summary>
public static class RotationSigner
{
    /// <summary>How long a signature stays valid.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(2);

    /// <summary>Creates a new random signing key.</summary>
    /// <returns>The key as text.</returns>
    public static string NewKey() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    /// <summary>Signs an item and type.</summary>
    /// <param name="key">The signing key.</param>
    /// <param name="itemId">The item id.</param>
    /// <param name="type">The image type name.</param>
    /// <param name="now">The time now.</param>
    /// <returns>The token: expiry (unix seconds), a dot and the signature.</returns>
    public static string Sign(string key, Guid itemId, string type, DateTimeOffset now)
    {
        var expires = now.Add(Lifetime).ToUnixTimeSeconds();
        return expires.ToString(System.Globalization.CultureInfo.InvariantCulture) + "." + Mac(key, itemId, type, expires);
    }

    /// <summary>Checks a token.</summary>
    /// <param name="key">The signing key.</param>
    /// <param name="itemId">The item id.</param>
    /// <param name="type">The image type name.</param>
    /// <param name="token">The token.</param>
    /// <param name="now">The time now.</param>
    /// <returns>True when the token is genuine for this item and type and has not expired.</returns>
    public static bool Validate(string? key, Guid itemId, string type, string? token, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(token))
        {
            return false;
        }

        var dot = token.IndexOf('.', StringComparison.Ordinal);
        if (dot <= 0 || !long.TryParse(token.AsSpan(0, dot), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var expires))
        {
            return false;
        }

        if (expires < now.ToUnixTimeSeconds())
        {
            return false;
        }

        var expected = Encoding.ASCII.GetBytes(Mac(key, itemId, type, expires));
        var given = Encoding.ASCII.GetBytes(token[(dot + 1)..]);
        return CryptographicOperations.FixedTimeEquals(expected, given);
    }

    private static string Mac(string key, Guid itemId, string type, long expires)
    {
        var data = Encoding.UTF8.GetBytes(itemId.ToString("N", System.Globalization.CultureInfo.InvariantCulture) + "|" + type.ToLowerInvariant() + "|" + expires.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return Convert.ToHexString(HMACSHA256.HashData(Convert.FromBase64String(key), data)).ToLowerInvariant();
    }
}

/// <summary>
/// Remembers for a few minutes which image was drawn for a request id, so the browser's preload and the
/// real display of the same address show the same image instead of drawing twice.
/// </summary>
public sealed class PickMemo
{
    private readonly Dictionary<string, (string Value, DateTimeOffset Expires)> _items = [];
    private readonly Lock _sync = new();
    private readonly TimeSpan _lifetime;
    private readonly int _capacity;

    /// <summary>Initializes a new instance of the <see cref="PickMemo"/> class.</summary>
    /// <param name="lifetime">How long an entry is kept.</param>
    /// <param name="capacity">The most entries kept.</param>
    public PickMemo(TimeSpan lifetime, int capacity = 2000)
    {
        _lifetime = lifetime;
        _capacity = Math.Max(1, capacity);
    }

    /// <summary>Gets the value for a key, when it is still remembered.</summary>
    /// <param name="key">The key.</param>
    /// <param name="now">The time now.</param>
    /// <param name="value">The value.</param>
    /// <returns>True when found.</returns>
    public bool TryGet(string key, DateTimeOffset now, out string value)
    {
        lock (_sync)
        {
            if (_items.TryGetValue(key, out var e) && e.Expires > now)
            {
                value = e.Value;
                return true;
            }

            value = string.Empty;
            return false;
        }
    }

    /// <summary>Remembers a value.</summary>
    /// <param name="key">The key.</param>
    /// <param name="value">The value.</param>
    /// <param name="now">The time now.</param>
    public void Set(string key, string value, DateTimeOffset now)
    {
        lock (_sync)
        {
            if (_items.Count >= _capacity)
            {
                foreach (var k in _items.Where(kv => kv.Value.Expires <= now).Select(kv => kv.Key).ToList())
                {
                    _items.Remove(k);
                }

                if (_items.Count >= _capacity)
                {
                    _items.Remove(_items.OrderBy(kv => kv.Value.Expires).First().Key);
                }
            }

            _items[key] = (value, now.Add(_lifetime));
        }
    }
}
