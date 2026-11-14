using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Crypto.Generators;
using Quaymaster.Application.Abstractions;
using Quaymaster.Application.Errors;

namespace Quaymaster.Infrastructure.Security;

/// <summary>
/// scrypt (RFC 7914) password hashing, byte-compatible with the original server: r=8, p=1, 64-byte key, 16-byte salt,
/// passwords normalised to NFKC, format <c>scrypt$N$r$p$saltBase64$hashBase64</c> (parameters travel with the hash so
/// they can change later). The primitive is BouncyCastle's <see cref="SCrypt"/>. Hashing is concurrency-limited: one
/// hash at a time, a short queue, then 429 - so a flood of logins cannot allocate N parallel 128 MiB buffers.
/// </summary>
public sealed class ScryptPasswordHasher : IPasswordHasher, IDisposable
{
    private const int BlockSize = 8;
    private const int Parallelism = 1;
    private const int KeyLength = 64;
    private const int SaltLength = 16;
    private const long MaxMemory = 256L * 1024 * 1024;
    private const int MaxQueue = 8;

    private readonly int _n;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _waiting;

    public ScryptPasswordHasher(int log2N)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(log2N, 10);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(log2N, 20);
        _n = 1 << log2N;
    }

    public async Task<string> HashAsync(string password, CancellationToken ct = default)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltLength);
        var key = await RunGatedAsync(() => Derive(password, salt, _n, BlockSize, Parallelism), ct);
        return string.Join('$', "scrypt", _n, BlockSize, Parallelism, Convert.ToBase64String(salt), Convert.ToBase64String(key));
    }

    public async Task<bool> VerifyAsync(string password, string storedHash, CancellationToken ct = default)
    {
        var parsed = Parse(storedHash);
        if (parsed is null)
        {
            return false;
        }

        var key = await RunGatedAsync(() => Derive(password, parsed.Salt, parsed.N, parsed.R, parsed.P), ct);
        return CryptographicOperations.FixedTimeEquals(key, parsed.Hash);
    }

    public async Task DummyVerifyAsync(string password, CancellationToken ct = default)
    {
        var salt = new byte[SaltLength];
        Array.Fill(salt, (byte)7);
        _ = await RunGatedAsync(() => Derive(password, salt, _n, BlockSize, Parallelism), ct);
    }

    public bool IsValidHash(string storedHash) => Parse(storedHash) is not null;

    public void Dispose() => _gate.Dispose();

    private static byte[] Derive(string password, byte[] salt, int n, int r, int p) =>
        SCrypt.Generate(Encoding.UTF8.GetBytes(password.Normalize(NormalizationForm.FormKC)), salt, n, r, p, KeyLength);

    private async Task<byte[]> RunGatedAsync(Func<byte[]> work, CancellationToken ct)
    {
        if (!await _gate.WaitAsync(0, ct))
        {
            if (Interlocked.Increment(ref _waiting) > MaxQueue)
            {
                Interlocked.Decrement(ref _waiting);
                throw AppException.RateLimited("Too many concurrent login attempts, try again shortly", TimeSpan.FromSeconds(5));
            }

            try
            {
                await _gate.WaitAsync(ct);
            }
            finally
            {
                Interlocked.Decrement(ref _waiting);
            }
        }

        try
        {
            return await Task.Run(work, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    private sealed record ParsedHash(int N, int R, int P, byte[] Salt, byte[] Hash);

    /// <summary>Strict parse: refuses parameters that are out of range or would not fit the memory cap.</summary>
    private static ParsedHash? Parse(string stored)
    {
        var parts = stored.Split('$');
        if (parts.Length != 6 || parts[0] != "scrypt")
        {
            return null;
        }

        if (!int.TryParse(parts[1], out var n) || !int.TryParse(parts[2], out var r) || !int.TryParse(parts[3], out var p))
        {
            return null;
        }

        if (n < 1024 || (n & (n - 1)) != 0 || n > 1 << 20 || r < 1 || r > 32 || p < 1 || p > 16)
        {
            return null;
        }

        if (128L * n * r > MaxMemory)
        {
            return null;
        }

        var salt = new byte[parts[4].Length];
        var hash = new byte[parts[5].Length];
        if (!Convert.TryFromBase64String(parts[4], salt, out var saltLength) || !Convert.TryFromBase64String(parts[5], hash, out var hashLength))
        {
            return null;
        }

        if (saltLength < 8 || hashLength != KeyLength)
        {
            return null;
        }

        return new ParsedHash(n, r, p, salt[..saltLength], hash[..hashLength]);
    }
}
