namespace Quaymaster.Application.Abstractions;

/// <summary>scrypt password hashing (RFC 7914). Format: <c>scrypt$N$r$p$saltBase64$hashBase64</c>.</summary>
public interface IPasswordHasher
{
    Task<string> HashAsync(string password, CancellationToken ct = default);

    /// <summary>Constant-time verification. A malformed stored hash verifies as false.</summary>
    Task<bool> VerifyAsync(string password, string storedHash, CancellationToken ct = default);

    /// <summary>Spends comparable time for unknown users so timing does not reveal which accounts exist.</summary>
    Task DummyVerifyAsync(string password, CancellationToken ct = default);

    /// <summary>True when <paramref name="storedHash"/> has the scrypt format with acceptable parameters.</summary>
    bool IsValidHash(string storedHash);
}

/// <summary>Where the one-time setup token is announced (stderr by default; replaced in tests).</summary>
public interface ISetupAnnouncer
{
    void Announce(string message);
}

/// <summary>Creates and migrates the database before anything uses it.</summary>
public interface IDatabaseInitializer
{
    Task InitializeAsync(CancellationToken ct);
}

/// <summary>Reads a file on behalf of configuration (secret-key / admin-password files). Abstracted for tests.</summary>
public interface IFileReader
{
    string ReadAllText(string path);
}
