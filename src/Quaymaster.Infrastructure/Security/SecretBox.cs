using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using Quaymaster.Application.Registry;

namespace Quaymaster.Infrastructure.Security;

/// <summary>
/// AES-256-GCM encryption for secrets at rest (spec section 5, RT-37). Wire-compatible with the original SecretBox.
/// <list type="bullet">
/// <item>key: HKDF-SHA256 derived from QM_SECRET_KEY (never stored)</item>
/// <item>IV: random 96 bits per message</item>
/// <item>AAD: <c>table:column:rowId</c>, so ciphertexts cannot be swapped between rows</item>
/// <item>format: <c>v1.&lt;keyId&gt;.&lt;iv&gt;.&lt;tag&gt;.&lt;ciphertext&gt;</c> (base64url); keyId allows future key rotation</item>
/// </list>
/// </summary>
public sealed class SecretBox : ISecretBox
{
    private readonly byte[] _key;

    public SecretBox(ReadOnlySpan<byte> masterKey)
    {
        if (masterKey.Length < 32)
        {
            throw new ArgumentException("secret key must be at least 32 bytes", nameof(masterKey));
        }

        _key = HKDF.DeriveKey(HashAlgorithmName.SHA256, masterKey.ToArray(), 32, Encoding.UTF8.GetBytes("quaymaster"), Encoding.UTF8.GetBytes("secrets-v1"));
        KeyId = Convert.ToHexStringLower(SHA256.HashData(_key))[..8];
    }

    public string KeyId { get; }

    public string Encrypt(string plaintext, string aad)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        ArgumentNullException.ThrowIfNull(aad);
        var iv = RandomNumberGenerator.GetBytes(12);
        var data = Encoding.UTF8.GetBytes(plaintext);
        var ct = new byte[data.Length];
        var tag = new byte[16];
        using var gcm = new AesGcm(_key, 16);
        gcm.Encrypt(iv, data, ct, tag, Encoding.UTF8.GetBytes(aad));
        return string.Join('.', "v1", KeyId, Base64Url.EncodeToString(iv), Base64Url.EncodeToString(tag), Base64Url.EncodeToString(ct));
    }

    public string Decrypt(string payload, string aad)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(aad);
        var parts = payload.Split('.');
        if (parts.Length != 5 || parts[0] != "v1")
        {
            throw new CryptographicException("unsupported ciphertext format");
        }

        if (parts[1] != KeyId)
        {
            throw new CryptographicException("ciphertext was encrypted with a different QM_SECRET_KEY");
        }

        var iv = Base64Url.DecodeFromChars(parts[2]);
        var tag = Base64Url.DecodeFromChars(parts[3]);
        var ct = Base64Url.DecodeFromChars(parts[4]);
        var plain = new byte[ct.Length];
        using var gcm = new AesGcm(_key, 16);
        gcm.Decrypt(iv, ct, tag, plain, Encoding.UTF8.GetBytes(aad));
        return Encoding.UTF8.GetString(plain);
    }
}
