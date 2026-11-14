using Quaymaster.Application.Abstractions;

namespace Quaymaster.Infrastructure.Files;

internal sealed class FileReader : IFileReader
{
    public string ReadAllText(string path) => File.ReadAllText(path);
}

/// <summary>Writes the setup token to <c>&lt;data&gt;/setup-token</c> with mode 0600 (the directory is 0700).</summary>
internal sealed class SetupTokenFile(string dataDir) : ISetupTokenStore
{
    private readonly string _path = Path.Combine(dataDir, "setup-token");

    public string Write(string token)
    {
        FilePermissions.EnsureDirectory(dataDir);
        using (var stream = new FileStream(_path, FilePermissions.Options(FileMode.Create, FileAccess.Write)))
        using (var writer = new StreamWriter(stream))
        {
            writer.Write(token + "\n");
        }

        FilePermissions.Restrict(_path);
        return _path;
    }

    public void Delete()
    {
        if (File.Exists(_path))
        {
            File.Delete(_path);
        }
    }
}

/// <summary>Prints the setup banner to stderr, outside the logging pipeline so redaction can never mask the token.</summary>
internal sealed class ConsoleSetupAnnouncer : ISetupAnnouncer
{
    public void Announce(string message) => Console.Error.Write(message);
}
