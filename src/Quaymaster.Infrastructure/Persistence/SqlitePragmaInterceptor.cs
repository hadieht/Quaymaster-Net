using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Quaymaster.Infrastructure.Persistence;

/// <summary>
/// Applies the original connection pragmas on every opened connection: WAL journal, NORMAL sync, foreign keys on
/// and a 5 s busy timeout. (The original used one connection with a busy timeout; here the pool hands out connections,
/// and SQLite serialises writers using the same busy timeout.)
/// EF Core opens an extra read-only connection to probe for the database file; the journal mode is a write, so it is
/// skipped there.
/// </summary>
internal sealed class SqlitePragmaInterceptor : DbConnectionInterceptor
{
    private const string Common = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;";
    private const string Writable = "PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL; " + Common;

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        using var command = connection.CreateCommand();
        command.CommandText = PragmasFor(connection);
        command.ExecuteNonQuery();
    }

    public override async Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = PragmasFor(connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string PragmasFor(DbConnection connection) =>
        new SqliteConnectionStringBuilder(connection.ConnectionString).Mode == SqliteOpenMode.ReadOnly ? Common : Writable;
}
