using Microsoft.AspNetCore.OpenApi;

// Top-level bootstrap for the Quaymaster API.
//
// This file is intentionally small: it wires up services and endpoints, and
// defers all behaviour to the Application/Infrastructure layers. The
// foundation phase (see docs/PROGRESS.md) replaces the placeholder pieces
// with the full security pipeline (auth, CSRF, rate limiting, headers).
var builder = WebApplication.CreateBuilder(args);

// Strict DI: the host validates registrations, scopes and singleton lifetime
// violations at startup instead of at first use.
builder.Services.AddOpenApi();
builder.Services.AddHealthChecks();

var app = builder.Build();

// --- Health (public, no auth, no details) ----------------------------------------------------
// Contract: exactly { "status": "ok" }. Keep it this shape; the frontend and
// load balancers probe it.
app.MapGet("/healthz", () => Results.Json(new { status = "ok" }));

// --- Static frontend (SPA) --------------------------------------------------------------------
// The React build (web/dist) is served from QM_WEB_DIR, defaulting to a
// sibling web/dist next to the app. SPA fallback: non-API GETs without a file
// extension return index.html (client-side routing).
var webDir = builder.Configuration["QM_WEB_DIR"] ?? Path.Combine(app.Environment.ContentRootPath, "web", "dist");
if (Directory.Exists(webDir))
{
    app.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(webDir),
    });
    app.MapFallback(() =>
    {
        var index = Path.Combine(webDir, "index.html");
        return File.Exists(index) ? Results.File(index, "text/html") : Results.NotFound();
    });
}

// --- OpenAPI document (machine-readable API surface) --------------------------------------------
app.MapOpenApi();

app.Run();

// Exposed so integration tests (WebApplicationFactory) can reach the builder.
public partial class Program;
