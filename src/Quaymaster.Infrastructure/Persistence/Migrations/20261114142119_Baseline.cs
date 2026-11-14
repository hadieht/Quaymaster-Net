using System.Reflection;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Quaymaster.Infrastructure.Persistence.Migrations;

/// <summary>
/// The original server's complete schema (see Baseline.sql, embedded): 24 tables reproduced 1:1 from its DDL, so
/// CHECK constraints, COLLATE NOCASE, WITHOUT ROWID, AUTOINCREMENT and the audit append-only triggers are
/// identical. Idempotent: a data directory written by the original server is accepted as-is (only
/// __EFMigrationsHistory is added).
///
/// The EF model snapshot only describes the tables that have entities so far (users, sessions, audit_log). When a
/// later phase maps another baseline table, run <c>dotnet ef migrations add</c> and DELETE the generated
/// CreateTable/CreateIndex calls for that table (it already exists) - the snapshot update is what you want.
/// </summary>
public partial class Baseline : Migration
{
    private static readonly string[] Tables =
    [
        "ignored_targets", "notification_dedupe", "notification_deliveries", "notification_channels", "run_steps",
        "runs", "schedules", "windows", "policies", "quarantine", "rollback_points", "staged_images",
        "image_checks", "registry_credentials", "kv", "secret_store", "schedule_occurrences", "intents",
        "operations", "stats_rollups", "events", "audit_log", "sessions", "users",
    ];

    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        using var stream = typeof(Baseline).Assembly.GetManifestResourceStream("Quaymaster.Infrastructure.Persistence.Migrations.Baseline.sql")
            ?? throw new InvalidOperationException("Embedded resource Baseline.sql is missing");
        using var reader = new StreamReader(stream);
        migrationBuilder.Sql(reader.ReadToEnd());
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        foreach (var table in Tables)
        {
            migrationBuilder.Sql($"DROP TABLE IF EXISTS {table};");
        }
    }
}
