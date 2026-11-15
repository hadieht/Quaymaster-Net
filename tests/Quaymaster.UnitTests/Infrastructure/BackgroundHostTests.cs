using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Quaymaster.Application.Background;
using Quaymaster.Application.Configuration;
using Quaymaster.Infrastructure.Hosting;

namespace Quaymaster.UnitTests.Infrastructure;

public sealed class BackgroundHostTests
{
    private sealed class CountingTask(string name, Func<CancellationToken, Task>? body = null) : IQMBackgroundTask
    {
        public int Runs { get; private set; }

        public string Name => name;

        public Task RunAsync(CancellationToken stoppingToken)
        {
            Runs++;
            return body is null ? Task.CompletedTask : body(stoppingToken);
        }
    }

    private static QmConfig Config(bool disable) => QmConfigLoader.Load(
        new Dictionary<string, string?>
        {
            ["QM_SECRET_KEY"] = Convert.ToBase64String(Enumerable.Range(1, 32).Select(i => (byte)(i * 7)).ToArray()),
            ["QM_DISABLE_BACKGROUND"] = disable ? "true" : "false",
        },
        new NoFiles());

    private sealed class NoFiles : Quaymaster.Application.Abstractions.IFileReader
    {
        public string ReadAllText(string path) => throw new FileNotFoundException(path);
    }

    private static async Task RunAsync(QmBackgroundHost host)
    {
        await host.StartAsync(CancellationToken.None);
        await (host.ExecuteTask ?? Task.CompletedTask);
        await host.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Registered_Tasks_Are_Started()
    {
        var a = new CountingTask("a");
        var b = new CountingTask("b");

        await RunAsync(new QmBackgroundHost([a, b], Config(disable: false), NullLogger<QmBackgroundHost>.Instance));

        a.Runs.Should().Be(1);
        b.Runs.Should().Be(1);
    }

    [Fact]
    public async Task QM_DISABLE_BACKGROUND_Starts_Nothing()
    {
        var a = new CountingTask("a");

        await RunAsync(new QmBackgroundHost([a], Config(disable: true), NullLogger<QmBackgroundHost>.Instance));

        a.Runs.Should().Be(0);
    }

    [Fact]
    public async Task A_Crashing_Task_Is_Isolated_From_Its_Siblings_And_The_Host()
    {
        QmBackgroundHost.RestartDelay = TimeSpan.FromMilliseconds(10);
        try
        {
            var healthy = new CountingTask("healthy");
            var n = 0;
            var crashing = new CountingTask("crashing", _ => ++n == 1 ? throw new InvalidOperationException("boom") : Task.CompletedTask);

            var act = () => RunAsync(new QmBackgroundHost([crashing, healthy], Config(disable: false), NullLogger<QmBackgroundHost>.Instance));

            await act.Should().NotThrowAsync();
            healthy.Runs.Should().Be(1, "a sibling's crash does not touch it");
            crashing.Runs.Should().Be(2, "the crashed task is started again (F-08)");
        }
        finally
        {
            QmBackgroundHost.RestartDelay = TimeSpan.FromSeconds(5);
        }
    }

    [Fact]
    public async Task F08_A_Crashed_Task_Is_Restarted_With_Back_Off_Instead_Of_Staying_Dead()
    {
        QmBackgroundHost.RestartDelay = TimeSpan.FromMilliseconds(10);
        try
        {
            var attempts = 0;
            var flaky = new CountingTask("flaky", _ => ++attempts < 3 ? throw new InvalidOperationException("429") : Task.CompletedTask);

            await RunAsync(new QmBackgroundHost([flaky], Config(disable: false), NullLogger<QmBackgroundHost>.Instance));

            flaky.Runs.Should().Be(3, "two crashes, then a normal run");
        }
        finally
        {
            QmBackgroundHost.RestartDelay = TimeSpan.FromSeconds(5);
        }
    }

    [Fact]
    public async Task Tasks_Observe_The_Stopping_Token()
    {
        var running = new TaskCompletionSource();
        var task = new CountingTask("loop", async ct =>
        {
            running.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
        });
        var host = new QmBackgroundHost([task], Config(disable: false), NullLogger<QmBackgroundHost>.Instance);

        await host.StartAsync(CancellationToken.None);
        await running.Task;
        await host.StopAsync(CancellationToken.None);

        host.ExecuteTask!.IsCompleted.Should().BeTrue();
    }
}
