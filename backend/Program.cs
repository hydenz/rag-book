using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Npgsql;
using OpenAI;
using Pgvector.Npgsql;
using RagBook.Api.Models;
using RagBook.Api.Services;

var builder = WebApplication.CreateBuilder(args);

var port = Environment.GetEnvironmentVariable("PORT") ?? "3001";
builder.WebHost.UseUrls($"http://localhost:{port}");

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
    var connectionString = builder.Configuration.GetConnectionString("Default")
        ?? throw new InvalidOperationException("ConnectionStrings:Default is not configured.");

    var dataSourceBuilder = new NpgsqlDataSourceBuilder(connectionString);
    dataSourceBuilder.UseVector();
    return dataSourceBuilder.Build();
});

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

app.MapGet("/api/story", async (RagService service) =>
{
    try
    {
        var story = await service.GetStoryOrGenerateAsync();
        return Results.Ok(new { story });
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"story error: {ex}");
        return Results.Json(new { error = "Failed to load story" }, statusCode: StatusCodes.Status500InternalServerError);
    }
});

app.MapPost("/api/story/generate", async (RagService service) =>
{
    try
    {
        var story = await service.GenerateStoryAsync();
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

app.MapGet("/api/chunks", async (RagService service) =>
{
    try
    {
        var chunks = await service.GetChunksAsync();
        return Results.Ok(new { count = chunks.Count, chunks });
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"chunks error: {ex}");
        return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status500InternalServerError);
    }
});

app.MapPost("/api/chat", async (ChatRequest request, RagService service) =>
{
    if (string.IsNullOrWhiteSpace(request.Message))
    {
        return Results.Json(new { error = "message is required" }, statusCode: StatusCodes.Status400BadRequest);
    }

    try
    {
        var response = await service.ChatWithRagAsync(request.Message, request.History ?? []);
        return Results.Ok(response);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"chat error: {ex}");
        return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status500InternalServerError);
    }
}).RequireRateLimiting(OpenAiRateLimitPolicy);

app.Run();
