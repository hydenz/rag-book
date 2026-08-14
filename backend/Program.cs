using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Npgsql;
using OpenAI;
using Pgvector.Npgsql;
using RagBook.Api.Models;
using RagBook.Api.Services;

var builder = WebApplication.CreateBuilder(args);

var port = Environment.GetEnvironmentVariable("PORT") ?? "3001";
// 0.0.0.0, not localhost: Render (and most PaaS) proxy in from outside the
// container, so the app has to listen on all interfaces to be reachable.
builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

var apiKey = string.IsNullOrWhiteSpace(builder.Configuration["OpenAI:ApiKey"])
    ? Environment.GetEnvironmentVariable("OPENAI_API_KEY")
    : builder.Configuration["OpenAI:ApiKey"];

if (string.IsNullOrWhiteSpace(apiKey))
{
    throw new InvalidOperationException(
        "OpenAI:ApiKey is not configured. Set it via user secrets " +
        "(dotnet user-secrets set \"OpenAI:ApiKey\" \"sk-...\"), in " +
        "appsettings.Development.json, or the OPENAI_API_KEY environment variable.");
}

builder.Services.AddCors(options =>
    options.AddDefaultPolicy(policy =>
        policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));

builder.Services.AddSingleton(new OpenAIClient(apiKey));

builder.Services.AddSingleton(sp =>
{
    var connectionString = ResolveConnectionString(builder.Configuration);
    var dataSourceBuilder = new NpgsqlDataSourceBuilder(connectionString);
    dataSourceBuilder.UseVector();
    return dataSourceBuilder.Build();
});

// Render (and most PaaS Postgres add-ons) hand you a connection URI via
// DATABASE_URL (postgres://user:pass@host:port/db) rather than an
// appsettings-style connection string. Prefer it when present; fall back to
// ConnectionStrings:Default for local dev (docker-compose).
static string ResolveConnectionString(IConfiguration configuration)
{
    var databaseUrl = Environment.GetEnvironmentVariable("DATABASE_URL");
    if (string.IsNullOrWhiteSpace(databaseUrl))
    {
        return configuration.GetConnectionString("Default")
            ?? throw new InvalidOperationException(
                "ConnectionStrings:Default is not configured (and DATABASE_URL is not set).");
    }

    var uri = new Uri(databaseUrl);
    var userInfo = uri.UserInfo.Split(':', 2);

    return new NpgsqlConnectionStringBuilder
    {
        Host = uri.Host,
        Port = uri.Port > 0 ? uri.Port : 5432,
        Username = Uri.UnescapeDataString(userInfo[0]),
        Password = userInfo.Length > 1 ? Uri.UnescapeDataString(userInfo[1]) : "",
        Database = uri.AbsolutePath.TrimStart('/'),
        SslMode = SslMode.Require,
    }.ConnectionString;
}

builder.Services.AddSingleton<RagService>();

// Cost control: OpenAI-backed endpoints are rate limited per client IP so a stray
// loop (or a stranger) can't rack up spend. Everything else is unlimited.
const string OpenAiRateLimitPolicy = "openai-endpoints";
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy(OpenAiRateLimitPolicy, httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 3,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            }));
});

var app = builder.Build();

app.UseCors();
app.UseRateLimiter();

// Serve the built frontend (frontend/dist, copied to wwwroot at container
// build time — see Dockerfile) alongside the API, so this one service is the
// whole deploy on Render.
app.UseDefaultFiles();
app.UseStaticFiles();

var rag = app.Services.GetRequiredService<RagService>();
try
{
    await rag.InitializeAsync();
    app.Logger.LogInformation("Database schema ready.");
}
catch (Exception ex)
{
    app.Logger.LogWarning("Postgres not reachable yet: {Message}", ex.Message);
}

app.MapGet("/api/health", () => Results.Ok(new { ok = true }));

// Every story/chat endpoint is scoped to a session so one visitor regenerating
// or chatting doesn't affect anyone else — see frontend's src/lib/session.ts,
// which generates a random id per browser and sends it on every request.
const string SessionHeader = "X-Session-Id";
static bool TryGetSessionId(HttpRequest request, out string sessionId)
{
    sessionId = request.Headers[SessionHeader].ToString();
    return !string.IsNullOrWhiteSpace(sessionId) && sessionId.Length <= 128;
}

// Rate limited too: GetStoryOrGenerateAsync auto-generates when a session has
// no story yet, so without this an attacker could mint a fresh X-Session-Id
// per request and get unlimited free generations — the per-session cooldown
// alone doesn't stop that, since a new session has never generated before.
app.MapGet("/api/story", async (HttpRequest request, RagService service) =>
{
    if (!TryGetSessionId(request, out var sessionId))
        return Results.Json(new { error = $"Missing or invalid {SessionHeader} header" }, statusCode: StatusCodes.Status400BadRequest);

    try
    {
        var story = await service.GetStoryOrGenerateAsync(sessionId);
        return Results.Ok(new { story });
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"story error: {ex}");
        return Results.Json(new { error = "Failed to load story" }, statusCode: StatusCodes.Status500InternalServerError);
    }
}).RequireRateLimiting(OpenAiRateLimitPolicy);

app.MapPost("/api/story/generate", async (HttpRequest request, RagService service) =>
{
    if (!TryGetSessionId(request, out var sessionId))
        return Results.Json(new { error = $"Missing or invalid {SessionHeader} header" }, statusCode: StatusCodes.Status400BadRequest);

    try
    {
        var story = await service.GenerateStoryAsync(sessionId);
        return Results.Ok(new { story });
    }
    catch (InvalidOperationException ex)
    {
        // Regenerate cooldown — not an error, just too soon.
        return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status429TooManyRequests);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"generate story error: {ex}");
        return Results.Json(new { error = "Failed to generate story" }, statusCode: StatusCodes.Status500InternalServerError);
    }
}).RequireRateLimiting(OpenAiRateLimitPolicy);

app.MapGet("/api/chunks", async (HttpRequest request, RagService service) =>
{
    if (!TryGetSessionId(request, out var sessionId))
        return Results.Json(new { error = $"Missing or invalid {SessionHeader} header" }, statusCode: StatusCodes.Status400BadRequest);

    try
    {
        var chunks = await service.GetChunksAsync(sessionId);
        return Results.Ok(new { count = chunks.Count, chunks });
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"chunks error: {ex}");
        return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status500InternalServerError);
    }
});

app.MapPost("/api/chat", async (HttpRequest request, ChatRequest chatRequest, RagService service) =>
{
    if (!TryGetSessionId(request, out var sessionId))
        return Results.Json(new { error = $"Missing or invalid {SessionHeader} header" }, statusCode: StatusCodes.Status400BadRequest);

    if (string.IsNullOrWhiteSpace(chatRequest.Message))
    {
        return Results.Json(new { error = "message is required" }, statusCode: StatusCodes.Status400BadRequest);
    }

    try
    {
        var response = await service.ChatWithRagAsync(sessionId, chatRequest.Message, chatRequest.History ?? []);
        return Results.Ok(response);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"chat error: {ex}");
        return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status500InternalServerError);
    }
}).RequireRateLimiting(OpenAiRateLimitPolicy);

// Anything else that isn't an API route or a static asset is the SPA shell —
// let the frontend's own client-side handling take it from there.
app.MapFallbackToFile("index.html");

app.Run();
