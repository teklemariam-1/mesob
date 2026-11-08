using System.Security.Cryptography;
using System.Text.Json;

namespace Mesob.Interop;

/// <summary>
/// Keeps small secrets (device key, studio key, title keys) on disk, locked to this PC.
/// Prefers a non-exportable RSA key inside the TPM ("Microsoft Platform Crypto Provider");
/// falls back to Windows DPAPI on PCs without TPM 2.0. <see cref="HardwareBacked"/> tells which.
/// </summary>
public sealed class SecureStore
{
    private const string TpmProvider = "Microsoft Platform Crypto Provider";
    private readonly string _dir;
    private readonly string _keyName;
    private static readonly byte[] Entropy = "mesob/securestore/v1"u8.ToArray();

    public bool HardwareBacked { get; }

    public SecureStore(string dir, string appName)
    {
        _dir = dir;
        Directory.CreateDirectory(dir);
        _keyName = $"Mesob-{appName}-{Environment.UserName}";
        HardwareBacked = TryTpmKey() != null;
    }

    private CngKey? TryTpmKey()
    {
        try
        {
            var provider = new CngProvider(TpmProvider);
            if (CngKey.Exists(_keyName, provider)) return CngKey.Open(_keyName, provider);
            return CngKey.Create(CngAlgorithm.Rsa, _keyName, new CngKeyCreationParameters
            {
                Provider = provider,
                KeyUsage = CngKeyUsages.Decryption,
                ExportPolicy = CngExportPolicies.None,
            });
        }
        catch (CryptographicException) { return null; }
        catch (PlatformNotSupportedException) { return null; }
    }

    private record Blob(string Method, string Data);

    public byte[] Protect(byte[] secret)
    {
        if (TryTpmKey() is { } key)
        {
            using var rsa = new RSACng(key);
            return Wrap("tpm", rsa.Encrypt(secret, RSAEncryptionPadding.OaepSHA1));
        }
        return Wrap("dpapi", ProtectedData.Protect(secret, Entropy, DataProtectionScope.CurrentUser));
    }

    public byte[] Unprotect(byte[] blob)
    {
        var b = JsonSerializer.Deserialize<Blob>(blob) ?? throw new CryptographicException("damaged secret");
        var data = Convert.FromBase64String(b.Data);
        if (b.Method == "tpm")
        {
            var key = TryTpmKey() ?? throw new CryptographicException("This secret was locked by a TPM that is no longer available.");
            using var rsa = new RSACng(key);
            return rsa.Decrypt(data, RSAEncryptionPadding.OaepSHA1);
        }
        return ProtectedData.Unprotect(data, Entropy, DataProtectionScope.CurrentUser);
    }

    private static byte[] Wrap(string method, byte[] data) => JsonSerializer.SerializeToUtf8Bytes(new Blob(method, Convert.ToBase64String(data)));

    /// <summary>Save a named secret (stored locked).</summary>
    public void Save(string name, byte[] secret) => File.WriteAllBytes(Path.Combine(_dir, name + ".locked"), Protect(secret));

    /// <summary>Load a named secret, or null if it doesn't exist.</summary>
    public byte[]? Load(string name)
    {
        var p = Path.Combine(_dir, name + ".locked");
        return File.Exists(p) ? Unprotect(File.ReadAllBytes(p)) : null;
    }
}
