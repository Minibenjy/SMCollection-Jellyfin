using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AiAssistant.Security;

/// <summary>
/// AES-256-GCM credential store backed by a key file outside the plugin's XML configuration.
/// </summary>
/// <remarks>
/// The plugin configuration XML is what an administrator reads, edits and backs up
/// from the dashboard, so no secret is ever written there. Credentials live in a
/// separate vault file, encrypted with a key generated on first run and stored with
/// owner-only permissions.
/// </remarks>
public sealed class EncryptedCredentialStore : ICredentialStore, IDisposable
{
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int KeySize = 32;

    private readonly ILogger<EncryptedCredentialStore> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly string _vaultPath;
    private readonly string _keyPath;

    private Dictionary<string, string>? _cache;

    /// <summary>
    /// Initializes a new instance of the <see cref="EncryptedCredentialStore"/> class.
    /// </summary>
    /// <param name="paths">Server application paths.</param>
    /// <param name="logger">Logger.</param>
    public EncryptedCredentialStore(IServerApplicationPaths paths, ILogger<EncryptedCredentialStore> logger)
    {
        _logger = logger;

        var dir = Path.Combine(paths.DataPath, "ai-assistant");
        Directory.CreateDirectory(dir);
        RestrictToOwner(dir);

        _vaultPath = Path.Combine(dir, "credentials.json");
        _keyPath = Path.Combine(dir, "vault.key");
    }

    /// <inheritdoc />
    public async Task SetAsync(Guid userId, string providerId, string secret, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var vault = await LoadAsync(cancellationToken).ConfigureAwait(false);
            vault[KeyFor(userId, providerId)] = Encrypt(secret);
            await SaveAsync(vault, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<string?> GetAsync(Guid userId, string providerId, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var vault = await LoadAsync(cancellationToken).ConfigureAwait(false);
            if (!vault.TryGetValue(KeyFor(userId, providerId), out var payload))
            {
                return null;
            }

            try
            {
                return Decrypt(payload);
            }
            catch (CryptographicException ex)
            {
                // A rotated or replaced key file makes existing records unreadable.
                // Fail closed and let the user re-enter the credential.
                _logger.LogError(ex, "Stored credential for provider {Provider} could not be decrypted.", providerId);
                return null;
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<string?> GetHintAsync(Guid userId, string providerId, CancellationToken cancellationToken)
    {
        var secret = await GetAsync(userId, providerId, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(secret))
        {
            return null;
        }

        return secret.Length <= 4
            ? new string('*', secret.Length)
            : string.Concat("…", secret.AsSpan(secret.Length - 4));
    }

    /// <inheritdoc />
    public async Task DeleteAsync(Guid userId, string providerId, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var vault = await LoadAsync(cancellationToken).ConfigureAwait(false);
            if (vault.Remove(KeyFor(userId, providerId)))
            {
                await SaveAsync(vault, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose() => _lock.Dispose();

    private static string KeyFor(Guid userId, string providerId)
        => string.Create(CultureInfo.InvariantCulture, $"{userId:N}:{providerId}");

    private async Task<Dictionary<string, string>> LoadAsync(CancellationToken cancellationToken)
    {
        if (_cache is not null)
        {
            return _cache;
        }

        if (!File.Exists(_vaultPath))
        {
            return _cache = new Dictionary<string, string>(StringComparer.Ordinal);
        }

        var json = await File.ReadAllTextAsync(_vaultPath, cancellationToken).ConfigureAwait(false);
        _cache = JsonSerializer.Deserialize<Dictionary<string, string>>(json)
                 ?? new Dictionary<string, string>(StringComparer.Ordinal);
        return _cache;
    }

    private async Task SaveAsync(Dictionary<string, string> vault, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(vault);
        var tmp = _vaultPath + ".tmp";
        await File.WriteAllTextAsync(tmp, json, cancellationToken).ConfigureAwait(false);
        RestrictToOwner(tmp);
        File.Move(tmp, _vaultPath, overwrite: true);
        _cache = vault;
    }

    private byte[] GetOrCreateKey()
    {
        if (File.Exists(_keyPath))
        {
            var existing = Convert.FromBase64String(File.ReadAllText(_keyPath).Trim());
            if (existing.Length == KeySize)
            {
                return existing;
            }

            throw new CryptographicException("Vault key file is present but malformed.");
        }

        var key = RandomNumberGenerator.GetBytes(KeySize);
        File.WriteAllText(_keyPath, Convert.ToBase64String(key));
        RestrictToOwner(_keyPath);
        _logger.LogInformation("Generated a new credential vault key at {Path}.", _keyPath);
        return key;
    }

    private string Encrypt(string plaintext)
    {
        var key = GetOrCreateKey();
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var plain = Encoding.UTF8.GetBytes(plaintext);
        var cipher = new byte[plain.Length];
        var tag = new byte[TagSize];

        using (var aes = new AesGcm(key, TagSize))
        {
            aes.Encrypt(nonce, plain, cipher, tag);
        }

        var packed = new byte[NonceSize + TagSize + cipher.Length];
        nonce.CopyTo(packed, 0);
        tag.CopyTo(packed, NonceSize);
        cipher.CopyTo(packed, NonceSize + TagSize);
        return Convert.ToBase64String(packed);
    }

    private string Decrypt(string payload)
    {
        var key = GetOrCreateKey();
        var packed = Convert.FromBase64String(payload);
        if (packed.Length < NonceSize + TagSize)
        {
            throw new CryptographicException("Credential record is truncated.");
        }

        var nonce = packed.AsSpan(0, NonceSize);
        var tag = packed.AsSpan(NonceSize, TagSize);
        var cipher = packed.AsSpan(NonceSize + TagSize);
        var plain = new byte[cipher.Length];

        using (var aes = new AesGcm(key, TagSize))
        {
            aes.Decrypt(nonce, cipher, tag, plain);
        }

        return Encoding.UTF8.GetString(plain);
    }

    private void RestrictToOwner(string path)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

        try
        {
            var mode = Directory.Exists(path)
                ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                : UnixFileMode.UserRead | UnixFileMode.UserWrite;
            File.SetUnixFileMode(path, mode);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            _logger.LogWarning(ex, "Could not restrict permissions on {Path}; check it manually.", path);
        }
    }
}
