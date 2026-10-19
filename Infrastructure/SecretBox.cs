using System.Security.Cryptography;
using System.Text;

namespace InstaMind.Infrastructure;

/// <summary>
/// Encrypts secrets (access token, app secret, API key) before they hit disk.
/// The 32-byte master key lives next to the data in data/secret.key and is
/// generated on first run, so the JSON file alone is useless if copied.
/// </summary>
public sealed class SecretBox
{
    private const string Prefix = "enc:v1:";
    private readonly byte[] _key;

    public SecretBox(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        var keyPath = Path.Combine(dataDirectory, "secret.key");
        if (File.Exists(keyPath))
        {
            _key = File.ReadAllBytes(keyPath);
        }
        else
        {
            _key = RandomNumberGenerator.GetBytes(32);
            File.WriteAllBytes(keyPath, _key);
        }

        if (_key.Length != 32)
        {
            throw new InvalidOperationException("data/secret.key must contain exactly 32 bytes.");
        }
    }

    public string Protect(string plain)
    {
        if (string.IsNullOrEmpty(plain)) return "";
        if (plain.StartsWith(Prefix, StringComparison.Ordinal)) return plain;

        var nonce = RandomNumberGenerator.GetBytes(12);
        var plainBytes = Encoding.UTF8.GetBytes(plain);
        var cipher = new byte[plainBytes.Length];
        var tag = new byte[16];

        using var aes = new AesGcm(_key, 16);
        aes.Encrypt(nonce, plainBytes, cipher, tag);

        var payload = new byte[nonce.Length + tag.Length + cipher.Length];
        Buffer.BlockCopy(nonce, 0, payload, 0, nonce.Length);
        Buffer.BlockCopy(tag, 0, payload, nonce.Length, tag.Length);
        Buffer.BlockCopy(cipher, 0, payload, nonce.Length + tag.Length, cipher.Length);

        return Prefix + Convert.ToBase64String(payload);
    }

    public string Unprotect(string value)
    {
        if (string.IsNullOrEmpty(value) || !value.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return value;
        }

        try
        {
            var payload = Convert.FromBase64String(value[Prefix.Length..]);
            var nonce = payload.AsSpan(0, 12);
            var tag = payload.AsSpan(12, 16);
            var cipher = payload.AsSpan(28);
            var plain = new byte[cipher.Length];

            using var aes = new AesGcm(_key, 16);
            aes.Decrypt(nonce, cipher, tag, plain);
            return Encoding.UTF8.GetString(plain);
        }
        catch
        {
            // Wrong/deleted key file: treat as "not configured" instead of crashing.
            return "";
        }
    }

    public const string Mask = "••••••••";
}
