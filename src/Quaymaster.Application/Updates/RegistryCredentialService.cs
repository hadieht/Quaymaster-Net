using Quaymaster.Application.Auth;
using Quaymaster.Application.Errors;
using Quaymaster.Application.Registry;
using Quaymaster.Application.Safety;
using Quaymaster.Domain.Registry;

namespace Quaymaster.Application.Updates;

public interface IRegistryCredentialService
{
    Task<RegistryCredentialsDto> ListAsync(CancellationToken ct);

    Task<RegistryCredentialPutDto> PutAsync(string host, string username, string password, Actor actor, CancellationToken ct);

    Task DeleteAsync(string host, Actor actor, CancellationToken ct);
}

/// <summary>
/// Registry credentials (admin only). The password is encrypted at rest (AES-256-GCM, key derived from QM_SECRET_KEY) and is
/// NEVER returned by any endpoint or written to the audit log: listing shows host, username and update time only.
/// </summary>
public sealed class RegistryCredentialService(IRegistryCredentialStore store, IImageChecksStore checks, IMutationGateway gateway, TimeProvider time) : IRegistryCredentialService
{
    public async Task<RegistryCredentialsDto> ListAsync(CancellationToken ct) =>
        new([.. (await store.ListAsync(ct)).Select(c => new RegistryCredentialDto(c.Host, c.Username, ImageReferences.Iso(c.UpdatedAt)))]);

    public async Task<RegistryCredentialPutDto> PutAsync(string host, string username, string password, Actor actor, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(actor);
        await store.PutAsync(host.ToLowerInvariant(), username, password, time.GetUtcNow().ToUnixTimeMilliseconds(), ct);
        await checks.InvalidateAsync(null, ct);
        await gateway.RecordAsync(new AuditScope(actor, "registry.credentials.put", host, new { username }), Domain.Auditing.AuditOutcome.Success, new { username }, null, ct);
        return new RegistryCredentialPutDto(host, username);
    }

    public async Task DeleteAsync(string host, Actor actor, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (!await store.DeleteAsync(host.ToLowerInvariant(), ct))
        {
            throw AppException.NotFound("Credential");
        }

        await checks.InvalidateAsync(null, ct);
        await gateway.RecordAsync(new AuditScope(actor, "registry.credentials.delete", host, null), Domain.Auditing.AuditOutcome.Success, null, null, ct);
    }
}
