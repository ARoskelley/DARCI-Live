using Darci.Core;
using Darci.Shared;
using Darci.Goals;
using Darci.Memory;
using Darci.Personality;
using Darci.Tools;
using Darci.Tools.Ollama;

var builder = WebApplication.CreateBuilder(args);

// === Configuration ===
var dbPath = Path.Combine(AppContext.BaseDirectory, "Data", "darci.db");
Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
var connectionString = $"Data Source={dbPath}";

// === Services Registration ===

// Swagger for API exploration
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new() { Title = "DARCI API", Version = "v3.0" });
});

// HTTP client for Ollama
builder.Services.AddHttpClient<IOllamaClient, OllamaClient>();

// Personality (singleton - one DARCI)
builder.Services.AddSingleton<IPersonalityEngine>(sp =>
    new PersonalityEngine(
        sp.GetRequiredService<ILogger<PersonalityEngine>>(),
        connectionString));

// Memory (singleton)
builder.Services.AddSingleton<IMemoryStore>(sp =>
{
    var ollama = sp.GetRequiredService<IOllamaClient>();
    return new MemoryStore(
        sp.GetRequiredService<ILogger<MemoryStore>>(),
        connectionString,
        text => ollama.GetEmbedding(text));
});

// Goals (singleton)
builder.Services.AddSingleton<IGoalManager>(sp =>
    new GoalManager(
        sp.GetRequiredService<ILogger<GoalManager>>(),
        connectionString));

// Toolkit (singleton)
builder.Services.AddSingleton<Toolkit>();
builder.Services.AddSingleton<IToolkit>(sp => sp.GetRequiredService<Toolkit>());

// Core DARCI components (singletons - one consciousness)
builder.Services.AddSingleton<Awareness>();
builder.Services.AddSingleton<State>();
builder.Services.AddSingleton<Decision>();

// DARCI herself - the background service
builder.Services.AddSingleton<Darci.Core.Darci>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<Darci.Core.Darci>());

// Controllers
builder.Services.AddControllers();

var app = builder.Build();

// === Middleware ===
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.MapControllers();

// === Minimal API Endpoints ===

// Health check
app.MapGet("/", () => "DARCI v3.0 - Autonomous Consciousness");

// Get DARCI's status
app.MapGet("/status", (Darci.Core.Darci darci) => darci.GetStatus());

// Send a message to DARCI
app.MapPost("/message", async (MessageRequest request, Awareness awareness) =>
{
    var message = new IncomingMessage
    {
        Content = request.Message,
        UserId = request.UserId ?? "Tinman",
        Urgency = request.Urgent ? Urgency.Now : Urgency.Soon,
        ReceivedAt = DateTime.UtcNow
    };
    
    await awareness.NotifyMessage(message);
    
    return Results.Accepted(null, new { message = "Message received", id = message.Id });
});

// Get pending responses (poll endpoint)
app.MapGet("/responses", async (Toolkit toolkit, CancellationToken ct) =>
{
    var responses = new List<OutgoingMessage>();
    
    // Collect available messages (non-blocking)
    while (toolkit.OutgoingMessages.TryRead(out var msg))
    {
        responses.Add(msg);
    }
    
    return responses;
});

// Long-poll for responses (waits for a response or timeout)
app.MapGet("/responses/wait", async (Toolkit toolkit, CancellationToken ct) =>
{
    try
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(30)); // 30 second timeout
        
        var msg = await toolkit.OutgoingMessages.ReadAsync(cts.Token);
        return Results.Ok(msg);
    }
    catch (OperationCanceledException)
    {
        return Results.NoContent();
    }
});

// Get active goals
app.MapGet("/goals", async (IGoalManager goals, string? userId) =>
{
    return await goals.GetActiveGoals(userId);
});

// Create a goal manually
app.MapPost("/goals", async (GoalRequest request, IGoalManager goals) =>
{
    var goal = await goals.CreateGoal(new GoalCreation
    {
        Title = request.Title,
        Description = request.Description ?? "",
        UserId = request.UserId ?? "Tinman",
        Type = request.Type ?? GoalType.Task,
        Priority = request.Priority ?? GoalPriority.Medium
    });
    
    return Results.Created($"/goals/{goal.Id}", goal);
});

app.Run();

// === Request/Response Models ===

public record MessageRequest(string Message, string? UserId = null, bool Urgent = false);
public record GoalRequest(string Title, string? Description, string? UserId, GoalType? Type, GoalPriority? Priority);
