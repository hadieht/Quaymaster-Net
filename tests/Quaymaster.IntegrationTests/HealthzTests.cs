using System.Net;
using System.Net.Http.Json;
using Quaymaster.IntegrationTests.Support;

namespace Quaymaster.IntegrationTests;

/// <summary>Boots the real API (in-memory test server) and verifies the public health contract.</summary>
public sealed class HealthzTests : IClassFixture<QmFactory>
{
    private readonly QmFactory _factory;

    public HealthzTests(QmFactory factory) => _factory = factory;

    [Fact]
    public async Task Healthz_Returns_Ok_Envelope()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/healthz");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<Dictionary<string, string>>();
        body.Should().Equal(new Dictionary<string, string> { ["status"] = "ok" });
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
    }

    [Fact]
    public async Task Unknown_Api_Route_Without_A_Session_Is_401_Not_A_Route_Oracle()
    {
        var client = _factory.NewClient();

        var response = await client.GetAsync("/api/v1/definitely-not-a-real-route", auth: false);

        response.Status.Should().Be(401);
        response.ErrorCode.Should().Be("unauthorized");
    }
}
