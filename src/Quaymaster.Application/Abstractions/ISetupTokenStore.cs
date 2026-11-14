namespace Quaymaster.Application.Abstractions;

/// <summary>Persists the one-time setup token next to the database (<c>&lt;data&gt;/setup-token</c>, mode 0600).</summary>
public interface ISetupTokenStore
{
    /// <summary>Writes the token and returns the file path (for the operator message).</summary>
    string Write(string token);

    void Delete();
}
