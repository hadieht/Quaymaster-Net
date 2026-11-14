using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Quaymaster.Application.Abstractions;
using Quaymaster.Application.Configuration;
using Quaymaster.Infrastructure.Files;

namespace Quaymaster.Infrastructure.Persistence;

/// <summary>
/// Prepares the data directory (0700), the database file (0600, WAL) and applies pending migrations. Refuses to start
/// on a database whose schema is newer than this build, and takes a <c>VACUUM INTO</c> backup before upgrading an
/// existing database.
/// </summary>
internal sealed class DatabaseInitializer(
    QmConfig config,
    IDbContextFactory<AppDbContext> factory,
    ILogger<DatabaseInitializer> log) : IDatabaseInitializer
{
    public async Task InitializeAsync(CancellationToken ct)
    {
        FilePermissions.EnsureDirectory(config.DataDir);
        EnsureDatabaseFile(config.DatabaseFile);

        await using var db = await factory.CreateDbContextAsync(ct);
        var known = db.Database.GetMigrations().ToHashSet(StringComparer.Ordinal);
        var applied = (await db.Database.GetAppliedMigrationsAsync(ct)).ToList();
        var newer = applied.Where(m => !known.Contains(m)).ToList();
        if (newer.Count > 0)
        {
            throw new InvalidOperationException(
                $"Database schema ({newer[^1]}) is newer than this build supports. Refusing to start; upgrade Quaymaster or restore a backup.");
        }

        var pending = (await db.Database.GetPendingMigrationsAsync(ct)).ToList();
        if (pending.Count == 0)
        {
            return;
        }

        if (applied.Count > 0)
        {
            var backup = Path.Combine(config.DataDir, $"backup-pre-{pending[0]}.db");
            await db.Database.ExecuteSqlRawAsync("VACUUM INTO {0}", [backup], ct);
            FilePermissions.Restrict(backup);
            Log.DatabaseBackedUp(log, backup);
        }

        await db.Database.MigrateAsync(ct);
        Log.MigrationsApplied(log, pending.Count, pending[^1]);
    }

    private static void EnsureDatabaseFile(string file)
    {
        if (File.Exists(file))
        {
            FilePermissions.Restrict(file);
            return;
        }

        // Create it ourselves with 0600 so SQLite (and its -wal/-shm companions, which inherit the mode) never
        // exists with looser permissions, not even for a moment.
        using var stream = new FileStream(file, FilePermissions.Options(FileMode.CreateNew, FileAccess.Write));
    }
}
