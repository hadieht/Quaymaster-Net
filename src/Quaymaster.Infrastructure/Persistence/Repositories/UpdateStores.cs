using Microsoft.EntityFrameworkCore;
using Quaymaster.Application.Registry;
using Quaymaster.Application.Updates;
using Quaymaster.Domain.Updates;

namespace Quaymaster.Infrastructure.Persistence.Repositories;

// Tables owned by the Baseline migration (image_checks, staged_images, rollback_points, quarantine, registry_credentials,
// secret_store), accessed with parameterised SQL like the other Docker stores: no entity mapping, no model-snapshot churn.

#pragma warning disable CA1812 // row types are instantiated by EF
internal sealed class ImageCheckRow
{
    public string Ref { get; set; } = "";

    public string State { get; set; } = "";

    public string? Reason { get; set; }

    public string? LocalDigest { get; set; }

    public string? RemoteDigest { get; set; }

    public string? Source { get; set; }

    public long CheckedAt { get; set; }

    public long ExpiresAt { get; set; }
}

internal sealed class StagedRow
{
    public string Tag { get; set; } = "";

    public string Ref { get; set; } = "";

    public string Digest { get; set; } = "";

    public string? ImageId { get; set; }

    public long CreatedAt { get; set; }
}

internal sealed class RollbackRow
{
    public string Tag { get; set; } = "";

    public string RunId { get; set; } = "";

    public string? Project { get; set; }

    public string? Service { get; set; }

    public string? ContainerName { get; set; }

    public string Ref { get; set; } = "";

    public string ImageId { get; set; } = "";

    public long CreatedAt { get; set; }

    public long ExpiresAt { get; set; }

    public long Released { get; set; }
}

internal sealed class QuarantineSqlRow
{
    public string Target { get; set; } = "";

    public string Digest { get; set; } = "";

    public string? RunId { get; set; }

    public string? Reason { get; set; }

    public long CreatedAt { get; set; }

    public long? ClearedAt { get; set; }
}

internal sealed class CredentialRow
{
    public string Host { get; set; } = "";

    public string Username { get; set; } = "";

    public long UpdatedAt { get; set; }
}

internal sealed class CiphertextRow
{
    public string Ciphertext { get; set; } = "";
}
#pragma warning restore CA1812

internal sealed class ImageChecksStore(IDbContextFactory<AppDbContext> factory) : IImageChecksStore
{
    public async Task<ImageCheck?> GetAsync(string key, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var r = await db.Database.SqlQuery<ImageCheckRow>(
            $"SELECT ref AS Ref, state AS State, reason AS Reason, local_digest AS LocalDigest, remote_digest AS RemoteDigest, source AS Source, checked_at AS CheckedAt, expires_at AS ExpiresAt FROM image_checks WHERE ref = {key}")
            .FirstOrDefaultAsync(ct);
        return r is null ? null : new ImageCheck(r.Ref, r.State, r.Reason, r.LocalDigest, r.RemoteDigest, r.Source, r.CheckedAt, r.ExpiresAt);
    }

    public async Task PutAsync(ImageCheck c, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(c);
        await using var db = await factory.CreateDbContextAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $@"INSERT INTO image_checks (ref, state, reason, local_digest, remote_digest, source, checked_at, expires_at)
               VALUES ({c.Ref}, {c.State}, {c.Reason}, {c.LocalDigest}, {c.RemoteDigest}, {c.Source}, {c.CheckedAt}, {c.ExpiresAt})
               ON CONFLICT(ref) DO UPDATE SET state=excluded.state, reason=excluded.reason, local_digest=excluded.local_digest,
                 remote_digest=excluded.remote_digest, source=excluded.source, checked_at=excluded.checked_at, expires_at=excluded.expires_at",
            ct);
    }

    public async Task InvalidateAsync(string? key, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        if (key is not null)
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM image_checks WHERE ref = {key}", ct);
        }
        else
        {
            await db.Database.ExecuteSqlRawAsync("DELETE FROM image_checks", ct);
        }
    }
}

internal sealed class StagedStore(IDbContextFactory<AppDbContext> factory) : IStagedStore
{
    private static StagedImage Map(StagedRow r) => new(r.Tag, r.Ref, r.Digest, r.ImageId, r.CreatedAt);

    public async Task PutAsync(StagedImage s, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        await using var db = await factory.CreateDbContextAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $@"INSERT INTO staged_images (tag, ref, digest, image_id, created_at) VALUES ({s.Tag}, {s.Ref}, {s.Digest}, {s.ImageId}, {s.CreatedAt})
               ON CONFLICT(tag) DO UPDATE SET image_id=excluded.image_id, created_at=excluded.created_at",
            ct);
    }

    public async Task<StagedImage?> GetAsync(string tag, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var r = await db.Database.SqlQuery<StagedRow>(
            $"SELECT tag AS Tag, ref AS Ref, digest AS Digest, image_id AS ImageId, created_at AS CreatedAt FROM staged_images WHERE tag = {tag}").FirstOrDefaultAsync(ct);
        return r is null ? null : Map(r);
    }

    public async Task<IReadOnlyList<StagedImage>> ListAsync(CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var rows = await db.Database.SqlQuery<StagedRow>(
            $"SELECT tag AS Tag, ref AS Ref, digest AS Digest, image_id AS ImageId, created_at AS CreatedAt FROM staged_images ORDER BY created_at DESC").ToListAsync(ct);
        return [.. rows.Select(Map)];
    }

    public async Task DeleteAsync(string tag, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM staged_images WHERE tag = {tag}", ct);
    }
}

internal sealed class RollbackStore(IDbContextFactory<AppDbContext> factory) : IRollbackStore
{
    private static RollbackPoint Map(RollbackRow r) =>
        new(r.Tag, r.RunId, r.Project, r.Service, r.ContainerName, r.Ref, r.ImageId, r.CreatedAt, r.ExpiresAt, r.Released == 1);

    public async Task PutAsync(RollbackPoint p, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(p);
        await using var db = await factory.CreateDbContextAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $@"INSERT OR REPLACE INTO rollback_points (tag, run_id, project, service, container_name, ref, image_id, created_at, expires_at, released)
               VALUES ({p.Tag}, {p.RunId}, {p.Project}, {p.Service}, {p.ContainerName}, {p.Ref}, {p.ImageId}, {p.CreatedAt}, {p.ExpiresAt}, 0)",
            ct);
    }

    private const string Columns = "SELECT tag AS Tag, run_id AS RunId, project AS Project, service AS Service, container_name AS ContainerName, ref AS Ref, image_id AS ImageId, created_at AS CreatedAt, expires_at AS ExpiresAt, released AS Released FROM rollback_points";

    public async Task<RollbackPoint?> GetAsync(string tag, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var r = await db.Database.SqlQueryRaw<RollbackRow>(Columns + " WHERE tag = {0}", tag).FirstOrDefaultAsync(ct);
        return r is null ? null : Map(r);
    }

    public async Task<IReadOnlyList<RollbackPoint>> ActiveAsync(CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return [.. (await db.Database.SqlQueryRaw<RollbackRow>(Columns + " WHERE released = 0").ToListAsync(ct)).Select(Map)];
    }

    public async Task ReleaseAsync(string tag, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE rollback_points SET released = 1 WHERE tag = {tag}", ct);
    }

    public async Task<IReadOnlyList<RollbackPoint>> ForRunAsync(string runId, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return [.. (await db.Database.SqlQueryRaw<RollbackRow>(Columns + " WHERE run_id = {0} ORDER BY created_at, tag", runId).ToListAsync(ct)).Select(Map)];
    }

    public async Task<IReadOnlyList<RollbackPoint>> ListAsync(int limit, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return [.. (await db.Database.SqlQueryRaw<RollbackRow>(Columns + " ORDER BY created_at DESC LIMIT {0}", limit).ToListAsync(ct)).Select(Map)];
    }
}

internal sealed class QuarantineStore(IDbContextFactory<AppDbContext> factory) : IQuarantineStore
{
    private const string Columns = "SELECT target AS Target, digest AS Digest, run_id AS RunId, reason AS Reason, created_at AS CreatedAt, cleared_at AS ClearedAt FROM quarantine";

    private static QuarantineRow Map(QuarantineSqlRow r) => new(r.Target, r.Digest, r.RunId, r.Reason, r.CreatedAt, r.ClearedAt);

    public async Task<QuarantineRow?> FindAsync(string target, string digest, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var r = await db.Database.SqlQueryRaw<QuarantineSqlRow>(Columns + " WHERE target = {0} AND digest = {1}", target, digest).FirstOrDefaultAsync(ct);
        return r is null ? null : Map(r);
    }

    public async Task UpsertAsync(QuarantineRow row, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(row);
        await using var db = await factory.CreateDbContextAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $@"INSERT INTO quarantine (target, digest, run_id, reason, created_at, cleared_at) VALUES ({row.Target}, {row.Digest}, {row.RunId}, {row.Reason}, {row.CreatedAt}, {row.ClearedAt})
               ON CONFLICT(target, digest) DO UPDATE SET cleared_at = excluded.cleared_at, run_id = excluded.run_id, reason = excluded.reason, created_at = excluded.created_at",
            ct);
    }

    public async Task<IReadOnlyList<QuarantineRow>> ListActiveAsync(CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return [.. (await db.Database.SqlQueryRaw<QuarantineSqlRow>(Columns + " WHERE cleared_at IS NULL ORDER BY created_at DESC").ToListAsync(ct)).Select(Map)];
    }

    public async Task<int> ClearAsync(string target, string? digest, long now, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return digest is not null
            ? await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE quarantine SET cleared_at = {now} WHERE target = {target} AND digest = {digest} AND cleared_at IS NULL", ct)
            : await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE quarantine SET cleared_at = {now} WHERE target = {target} AND cleared_at IS NULL", ct);
    }
}

/// <summary>Registry credentials: metadata in <c>registry_credentials</c>, the password AES-GCM encrypted in <c>secret_store</c> (<c>registry:&lt;host&gt;</c>).</summary>
internal sealed class RegistryCredentialStore(IDbContextFactory<AppDbContext> factory, ISecretBox box) : IRegistryCredentialStore
{
    private static string SecretName(string host) => $"registry:{host}";

    private static string Aad(string name) => $"secret_store:ciphertext:{name}";

    public async Task PutAsync(string host, string username, string password, long now, CancellationToken ct)
    {
        var name = SecretName(host);
        var ciphertext = box.Encrypt(password, Aad(name));
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO secret_store (name, ciphertext, updated_at) VALUES ({name}, {ciphertext}, {now}) ON CONFLICT(name) DO UPDATE SET ciphertext=excluded.ciphertext, updated_at=excluded.updated_at",
            ct);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO registry_credentials (host, username, updated_at) VALUES ({host}, {username}, {now}) ON CONFLICT(host) DO UPDATE SET username=excluded.username, updated_at=excluded.updated_at",
            ct);
        await tx.CommitAsync(ct);
    }

    public async Task<RegistryCredential?> GetAsync(string host, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var h = host.ToLowerInvariant();
        var row = await db.Database.SqlQuery<CredentialRow>($"SELECT host AS Host, username AS Username, updated_at AS UpdatedAt FROM registry_credentials WHERE host = {h}").FirstOrDefaultAsync(ct);
        if (row is null)
        {
            return null;
        }

        var name = SecretName(h);
        var secret = await db.Database.SqlQuery<CiphertextRow>($"SELECT ciphertext AS Ciphertext FROM secret_store WHERE name = {name}").FirstOrDefaultAsync(ct);
        return secret is null ? null : new RegistryCredential(row.Username, box.Decrypt(secret.Ciphertext, Aad(name)));
    }

    public async Task<IReadOnlyList<(string Host, string Username, long UpdatedAt)>> ListAsync(CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var rows = await db.Database.SqlQuery<CredentialRow>($"SELECT host AS Host, username AS Username, updated_at AS UpdatedAt FROM registry_credentials ORDER BY host").ToListAsync(ct);
        return [.. rows.Select(r => (r.Host, r.Username, r.UpdatedAt))];
    }

    public async Task<bool> DeleteAsync(string host, CancellationToken ct)
    {
        var name = SecretName(host);
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM secret_store WHERE name = {name}", ct);
        var n = await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM registry_credentials WHERE host = {host}", ct);
        await tx.CommitAsync(ct);
        return n > 0;
    }
}
