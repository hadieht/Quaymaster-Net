using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Quaymaster.Infrastructure.Persistence;

/// <summary>
/// Applies the original connection pragmas on every opened connection: WAL journal, NORMAL sync, foreign keys on
/// and a 5 s busy timeout. (The original used one connection with a busy timeout; here the pool hands out connections,
/// and SQLite serialises writers using the same busy timeout.)
/// </summary>
internal sealed class SqlitePragmaInterceptor : DbConnectionInterceptor
{
    private const string Pragmas =
        "PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL; PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;";

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        using var command = connection.CreateCommand();
        command.CommandText = Pragmas;
        command.ExecuteNonQuery();
    }

    public override async Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = Pragmas;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
