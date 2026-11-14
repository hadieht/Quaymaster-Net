using Microsoft.EntityFrameworkCore;
using Quaymaster.Domain.Auditing;
using Quaymaster.Domain.Identity;

namespace Quaymaster.Infrastructure.Persistence;

/// <summary>
/// The single EF Core entry point to the SQLite database. The schema is owned by the migrations (the Baseline
/// migration replicates the original 24-table schema 1:1, including CHECK constraints, triggers and WITHOUT ROWID
/// tables); this model describes only the tables that have been ported to entities so far. Timestamps are stored the
/// way the original did: epoch milliseconds in INTEGER columns.
/// </summary>
public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();

    public DbSet<Session> Sessions => Set<Session>();

    public DbSet<AuditEntry> AuditLog => Set<AuditEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<User>(e =>
        {
            e.ToTable("users", t => t.HasCheckConstraint("CK_users_role", "role IN ('admin','viewer')"));
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
            e.Property(x => x.Username).HasColumnName("username").UseCollation("NOCASE").IsRequired();
            e.HasIndex(x => x.Username).IsUnique();
            e.Property(x => x.PasswordHash).HasColumnName("password_hash").IsRequired();
            e.Property(x => x.Role).HasColumnName("role").HasConversion(r => r.ToWire(), s => ParseRole(s)).IsRequired();
            e.Property(x => x.LogsRead).HasColumnName("logs_read");
            e.Property(x => x.Disabled).HasColumnName("disabled");
            e.Property(x => x.CreatedAt).HasColumnName("created_at").HasConversion(EpochMs);
            e.Property(x => x.PasswordChangedAt).HasColumnName("password_changed_at").HasConversion(EpochMs);
        });

        modelBuilder.Entity<Session>(e =>
        {
            e.ToTable("sessions");
            e.HasKey(x => x.IdHash);
            e.Property(x => x.IdHash).HasColumnName("id_hash");
            e.Property(x => x.UserId).HasColumnName("user_id");
            e.HasIndex(x => x.UserId).HasDatabaseName("sessions_user");
            e.Property(x => x.CsrfSecret).HasColumnName("csrf_secret").IsRequired();
            e.Property(x => x.CreatedAt).HasColumnName("created_at").HasConversion(EpochMs);
            e.Property(x => x.LastSeen).HasColumnName("last_seen").HasConversion(EpochMs);
            e.Property(x => x.ExpiresAt).HasColumnName("expires_at").HasConversion(EpochMs);
            e.Property(x => x.Ip).HasColumnName("ip");
            e.Property(x => x.UserAgent).HasColumnName("user_agent");
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AuditEntry>(e =>
        {
            e.ToTable("audit_log", t => t.HasCheckConstraint("CK_audit_log_outcome", "outcome IN ('success','failure','denied')"));
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
            e.Property(x => x.Timestamp).HasColumnName("ts").HasConversion(EpochMs);
            e.HasIndex(x => x.Timestamp).HasDatabaseName("audit_ts");
            e.Property(x => x.Actor).HasColumnName("actor").IsRequired();
            e.Property(x => x.Ip).HasColumnName("ip");
            e.Property(x => x.Action).HasColumnName("action").IsRequired();
            e.Property(x => x.Target).HasColumnName("target");
            e.Property(x => x.ParamsJson).HasColumnName("params");
            e.Property(x => x.Outcome).HasColumnName("outcome").HasConversion(o => o.ToWire(), s => AuditOutcomeNames.Parse(s)).IsRequired();
            e.Property(x => x.DurationMs).HasColumnName("duration_ms");
        });
    }

    private static readonly Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<DateTimeOffset, long> EpochMs =
        new(v => v.ToUnixTimeMilliseconds(), v => DateTimeOffset.FromUnixTimeMilliseconds(v));

    private static Role ParseRole(string value) =>
        RoleNames.TryParse(value, out var role) ? role : throw new InvalidOperationException($"Unknown role '{value}' in database");
}
