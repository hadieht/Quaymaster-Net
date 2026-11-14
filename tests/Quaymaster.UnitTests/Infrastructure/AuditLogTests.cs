using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using Quaymaster.Application.Abstractions;
using Quaymaster.Application.Audit;
using Quaymaster.Domain.Auditing;

namespace Quaymaster.UnitTests.Infrastructure;

public sealed class AuditLogTests
{
    private sealed class CapturingRepository : IAuditRepository
    {
        public List<AuditEntry> Added { get; } = [];

        public DateTimeOffset? PrunedBefore { get; private set; }

        public Task AddAsync(AuditEntry entry, CancellationToken ct)
        {
            Added.Add(entry);
            return Task.CompletedTask;
        }

        public Task<int> PruneAsync(DateTimeOffset olderThan, CancellationToken ct)
        {
            PrunedBefore = olderThan;
            return Task.FromResult(3);
        }
    }

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 14, 12, 0, 0, TimeSpan.Zero));
    private readonly CapturingRepository _repository = new();

    [Fact]
    public async Task Records_Are_Stamped_And_Params_Are_Serialised_Without_Nulls()
    {
        var log = new AuditLog(_repository, _time, 365);

        await log.RecordAsync(new AuditRecord("root", "user.update", AuditOutcome.Success, "1.2.3.4", "bob", new { role = "viewer", logsRead = (bool?)null }));

        var entry = _repository.Added.Should().ContainSingle().Which;
        entry.Timestamp.Should().Be(_time.GetUtcNow());
        entry.Actor.Should().Be("root");
        entry.Ip.Should().Be("1.2.3.4");
        entry.Target.Should().Be("bob");
        entry.Outcome.Should().Be(AuditOutcome.Success);
        JsonDocument.Parse(entry.ParamsJson!).RootElement.GetRawText().Should().Be("{\"role\":\"viewer\"}");
    }

    [Fact]
    public async Task Records_Without_Params_Store_Null()
    {
        await new AuditLog(_repository, _time, 365).RecordAsync(new AuditRecord("a", "auth.logout", AuditOutcome.Success));

        _repository.Added.Single().ParamsJson.Should().BeNull();
    }

    [Fact]
    public async Task Prune_Uses_The_Configured_Retention()
    {
        var removed = await new AuditLog(_repository, _time, 90).PruneAsync();

        removed.Should().Be(3);
        _repository.PrunedBefore.Should().Be(_time.GetUtcNow().AddDays(-90));
    }

    [Fact]
    public void The_Audit_Abstractions_Offer_No_Update_Or_Delete()
    {
        typeof(IAuditLog).GetMethods().Select(m => m.Name).Should().BeEquivalentTo(["RecordAsync", "PruneAsync"]);
        typeof(IAuditRepository).GetMethods().Select(m => m.Name).Should().BeEquivalentTo(["AddAsync", "PruneAsync"]);
    }
}
