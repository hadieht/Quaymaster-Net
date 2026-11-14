using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using System.Net;
using System.Net.Http.Json;

namespace Quaymaster.IntegrationTests;

/// <summary>
/// Boots the real API (in-memory test server) and verifies the public
/// health contract. These tests run in CI; no Docker daemon required.
/// </summary>
public class HealthzTests
{
    private static readonly WebApplicationFactory<Program> Factory = new();

    [Fact]
    public async Task Healthz_Returns_Ok_Envelope()
    {
        using var client = Factory.CreateClient();

        var response = await client.GetAsync("/healthz");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<Dictionary<string, string>>();
        body.Should().NotBeNull();
        body!["status"].Should().Be("ok");
    }

    [Fact]
    public async Task Unknown_Api_Route_Returns_Not_Found()
    {
        using var client = Factory.CreateClient();

        var response = await client.GetAsync("/api/v1/definitely-not-a-real-route");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
