using System.Net;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.FileProviders;
using Quaymaster.Api;
using Quaymaster.Api.Cli;
using Quaymaster.Api.Endpoints;
using Quaymaster.Api.Http;
using Quaymaster.Application.Configuration;
using Quaymaster.Infrastructure;
using Quaymaster.Infrastructure.Hosting;

// Operator helper commands run before any host is built (no config, no database).
if (CliRunner.IsCommand(args))
{
    return await CliRunner.RunAsync(
        args,
        Console.In,
        Console.Out,
        Console.Error,
        Environment.GetEnvironmentVariable,
        () => Console.IsInputRedirected ? null : ReadPasswordInteractively());
}

var builder = WebApplication.CreateBuilder(args);

// Ensure console logging is enabled early so we see startup logs
builder.Logging.ClearProviders();
builder.Logging.AddConsole(o => o.FormatterName = ScrubbingConsoleFormatter.FormatterName);
builder.Logging.AddConsoleFormatter<ScrubbingConsoleFormatter, Microsoft.Extensions.Logging.Console.ConsoleFormatterOptions>();

// --- Configuration: the full QM_* surface, validated once, failing fast with every problem listed. -----------------
QmConfig config;
try
{
    config = ApiConfiguration.Load(builder.Configuration, builder.Environment);
}
catch (ConfigurationException e)
{
    await Console.Error.WriteLineAsync(e.Message + Environment.NewLine + Environment.NewLine + "See .env.example for every supported variable.");
    return 2;
}

builder.Logging.SetMinimumLevel(config.LogLevel switch
{
    "fatal" => LogLevel.Critical,
    "error" => LogLevel.Error,
    "warn" => LogLevel.Warning,
    "debug" => LogLevel.Debug,
    "trace" => LogLevel.Trace,
    "silent" => LogLevel.None,
    _ => LogLevel.Information,
});

builder.WebHost.ConfigureKestrel(kestrel =>
{
    kestrel.AddServerHeader = false;
    kestrel.Limits.MaxRequestBodySize = RequestLimits.MaxBodyBytes;
    if (IPAddress.TryParse(config.Host, out var address))
    {
        kestrel.Listen(address, config.Port);
    }
    else if (string.Equals(config.Host, "localhost", StringComparison.OrdinalIgnoreCase))
    {
        kestrel.ListenLocalhost(config.Port);
    }
    else
    {
        kestrel.Listen(Dns.GetHostAddresses(config.Host)[0], config.Port);
    }
});

builder.Services.AddQuaymasterInfrastructure(config);
builder.Services.AddQuaymasterApi(config);
builder.Services.AddOpenApi();

var app = builder.Build();

// --- Request pipeline (order matters; documented in docs/PROGRESS.md "Security pipeline") ------------------------------
//  1. ExceptionHandling   every exception -> error envelope; nothing escapes it
//  2. ForwardedHeaders    only with QM_TRUST_PROXY: client IP from X-Forwarded-For of trusted proxies
//  3. SecurityHeaders     CSP, nosniff, frame/referrer/permissions policies, HSTS, no-store for /api
//  4. Routing             endpoint metadata is available to the guard
//  5. ApiGuard            /api/*: session auth (401), exact Origin + X-QM-CSRF on state changes (403)
//  6. StaticFiles         the React build (QM_WEB_DIR)
//  7. Endpoints           /healthz, /api/v1/*, unknown-route envelope, SPA fallback
//     Rate limiting (login/setup) runs inside the use cases, after body validation and before password hashing.
app.UseMiddleware<ExceptionHandlingMiddleware>();
if (config.TrustProxy.Count > 0)
{
    app.UseForwardedHeaders();
}

app.UseMiddleware<SecurityHeadersMiddleware>();
// Static files run before routing: the static-file middleware stands down once an endpoint (the SPA fallback) is selected.
var webDir = SiteEndpoints.ResolveWebDir(config, app.Environment.ContentRootPath);
if (webDir is not null)
{
    app.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = new PhysicalFileProvider(webDir),
        OnPrepareResponse = context =>
            context.Context.Response.Headers.CacheControl =
                context.File.PhysicalPath?.Contains($"{Path.DirectorySeparatorChar}assets{Path.DirectorySeparatorChar}", StringComparison.Ordinal) == true
                    ? "public, max-age=31536000, immutable"
                    : "no-cache",
    });
}

app.UseRouting();
app.UseMiddleware<ResponseScrubMiddleware>(); // F-01: learned-secret scrub of JSON API responses
app.UseMiddleware<ApiGuardMiddleware>();

var api = app.MapGroup("/api/v1");
AuthEndpoints.Map(api);
UsersEndpoints.Map(api);
DockerEndpoints.Map(api);
UpdateEndpoints.Map(api);
MaintenanceEndpoints.Map(api);
NotificationEndpoints.Map(api);
MiscEndpoints.Map(api);
WebApiEndpoints.Map(api);
StreamEndpoints.Map(api);
SiteEndpoints.Map(app, webDir);

// The machine-readable API description is a development aid, not part of the public surface.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

// Run startup initialization (database + first-run) BEFORE accepting requests.
// This ensures /healthz only returns ok after the DB is ready and setup token is issued.
using (var scope = app.Services.CreateScope())
{
    var initializer = scope.ServiceProvider.GetRequiredService<StartupInitializer>();
    await initializer.StartAsync(CancellationToken.None);
}

// Fail fast on a second instance / bad data directory before binding the port.
await app.RunAsync();
return 0;

static string? ReadPasswordInteractively()
{
    string Read(string prompt)
    {
        Console.Error.Write(prompt);
        var buffer = new System.Text.StringBuilder();
        ConsoleKeyInfo key;
        while ((key = Console.ReadKey(intercept: true)).Key != ConsoleKey.Enter)
        {
            if (key.Key == ConsoleKey.Backspace)
            {
                if (buffer.Length > 0)
                {
                    buffer.Length--;
                }
            }
            else
            {
                buffer.Append(key.KeyChar);
            }
        }

        Console.Error.WriteLine();
        return buffer.ToString();
    }

    var first = Read("Password (min 12 characters): ");
    var second = Read("Repeat: ");
    return first == second ? first : throw new ArgumentException("The passwords do not match");
}

// Exposed so integration tests (WebApplicationFactory) can reach the builder.
public partial class Program;
