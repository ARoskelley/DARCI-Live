using Darci.Core;
using Darci.Api;
using Darci.Shared;
using Darci.Goals;
using Darci.Memory;
using Darci.Personality;
using Darci.Tools;
using Darci.Tools.Cad;
using Darci.Tools.Engineering;
using Darci.Tools.Engineering.Providers;
using Darci.Tools.Notifications;
using Darci.Tools.Ollama;

var builder = WebApplication.CreateBuilder(args);
EnvironmentFileLoader.Load(
    builder.Environment.ContentRootPath,
    ".env.local",
    ".env.engineering.local");

// === Configuration ===
var dbPath = Path.Combine(AppContext.BaseDirectory, "Data", "darci.db");
Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
var connectionString = $"Data Source={dbPath}";
var telegramBotToken = Environment.GetEnvironmentVariable("DARCI_TELEGRAM_BOT_TOKEN") ?? "";
var telegramChatId = Environment.GetEnvironmentVariable("DARCI_TELEGRAM_CHAT_ID") ?? "8587072376";

// === Services Registration ===

// Swagger for API exploration
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new() { Title = "DARCI API", Version = "v3.0" });
});

// HTTP client for Ollama
builder.Services.AddHttpClient<IOllamaClient, OllamaClient>();

// HTTP client for Python CAD service
builder.Services.AddHttpClient<ICadBridge, CadBridge>();
builder.Services.AddHttpClient<KittyCadEngineeringProvider>();
builder.Services.AddHttpClient<CadCoderEngineeringProvider>();
builder.Services.AddSingleton<IEngineeringCadProvider>(sp => sp.GetRequiredService<KittyCadEngineeringProvider>());
builder.Services.AddSingleton<IEngineeringCadProvider>(sp => sp.GetRequiredService<CadCoderEngineeringProvider>());
builder.Services.AddSingleton<IEngineeringWorkbench, CadQueryEngineeringWorkbench>();

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

// Response delivery + notification fanout
builder.Services.AddSingleton<IResponseStore, InMemoryResponseStore>();
builder.Services.AddSingleton<INotificationLogStore, InMemoryNotificationLogStore>();
builder.Services.AddSingleton(new DarciNotificationPreferences
{
    // Hardcoded single-user preferences/contact info for now.
    EmailEnabled = true,
    TelegramEnabled = true,
    EmailTo = "aroskelley2112@gmail.com",
    EmailFrom = "aroskelley2112@gmail.com",
    SmtpHost = "smtp.gmail.com",
    SmtpPort = 587,
    SmtpUseSsl = true,
    SmtpUsername = "aroskelley2112@gmail.com",
    SmtpPassword = Environment.GetEnvironmentVariable("DARCI_SMTP_PASSWORD") ?? "smtp-password",
    TelegramBotToken = telegramBotToken,
    TelegramChatId = telegramChatId,
    TelegramInboundEnabled = true,
    TelegramInboundUserId = "Tinman"
});
builder.Services.AddSingleton<INotificationProvider, EmailNotificationProvider>();
builder.Services.AddSingleton<INotificationProvider, TelegramNotificationProvider>();
builder.Services.AddSingleton<INotificationService, NotificationService>();
builder.Services.AddHostedService<ResponseDispatcher>();
builder.Services.AddHostedService<TelegramInboundService>();

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
        Source = "api",
        Urgency = request.Urgent ? Urgency.Now : Urgency.Soon,
        ReceivedAt = DateTime.UtcNow
    };
    
    await awareness.NotifyMessage(message);
    
    return Results.Accepted(null, new { message = "Message received", id = message.Id });
});

// Get pending responses (poll endpoint)
app.MapGet("/responses", async (IResponseStore responses, CancellationToken ct) =>
{
    var pending = new List<OutgoingMessage>();
    
    while (responses.TryRead(out var msg))
    {
        pending.Add(msg);
    }
    
    return pending;
});

// Long-poll for responses (waits for a response or timeout)
app.MapGet("/responses/wait", async (IResponseStore responses, CancellationToken ct) =>
{
    try
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(30));
        
        var msg = await responses.ReadAsync(cts.Token);
        return Results.Ok(msg);
    }
    catch (OperationCanceledException)
    {
        return Results.NoContent();
    }
});

// Recent notification delivery log
app.MapGet("/notifications/log", (INotificationLogStore logs, int? limit) =>
{
    var actualLimit = Math.Clamp(limit ?? 50, 1, 500);
    return Results.Ok(logs.GetRecent(actualLimit));
});

// Telegram inbound runtime config status (no secrets)
app.MapGet("/telegram/inbound/status", (DarciNotificationPreferences prefs) =>
{
    return Results.Ok(new
    {
        enabled = prefs.TelegramInboundEnabled,
        botTokenSet = !string.IsNullOrWhiteSpace(prefs.TelegramBotToken),
        chatId = prefs.TelegramChatId,
        userId = prefs.TelegramInboundUserId
    });
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

// === CAD Endpoints ===

// Send a CAD request through DARCI's message pipeline (goes through Perceive → Decide → Act)
app.MapPost("/cad/generate", async (CadRequest request, Awareness awareness) =>
{
    var message = new IncomingMessage
    {
        Content = request.Description,
        UserId = request.UserId ?? "Tinman",
        Source = "api",
        Urgency = request.Urgent ? Urgency.Now : Urgency.Soon,
        ReceivedAt = DateTime.UtcNow
    };
    
    // Pre-tag as CAD so Awareness doesn't need to classify
    message.Intent = new MessageIntent
    {
        Type = IntentType.CAD,
        ExtractedTopic = request.Description,
        Confidence = 1.0f
    };
    
    await awareness.NotifyMessage(message);
    
    return Results.Accepted(null, new
    {
        message = "CAD request received. Result will appear in /responses.",
        id = message.Id
    });
});

// Direct execution bypassing DARCI's decision loop (for testing)
app.MapPost("/cad/execute", async (CadExecuteRequest request, Toolkit toolkit) =>
{
    var dims = (request.LengthMm.HasValue || request.WidthMm.HasValue || request.HeightMm.HasValue)
        ? new CadDimensionSpec
        {
            LengthMm = request.LengthMm,
            WidthMm = request.WidthMm,
            HeightMm = request.HeightMm
        }
        : null;
    
    var result = await toolkit.GenerateCAD(
        request.Description,
        dims,
        request.MaxIterations ?? 3);
    
    return result.Success ? Results.Ok(result) : Results.UnprocessableEntity(result);
});

// Check if Python CAD engine is reachable
app.MapGet("/cad/health", async (Toolkit toolkit) =>
{
    var healthy = await toolkit.IsCADEngineHealthy();
    return healthy
        ? Results.Ok(new { status = "healthy", service = "cad-engine" })
        : Results.StatusCode(503);
});

// Direct engineering workbench execution with artifact bundling to tmp/engineering.
app.MapPost("/engineering/execute", async (EngineeringExecuteRequest request, Toolkit toolkit, IWebHostEnvironment env) =>
{
    var dims = (request.LengthMm.HasValue || request.WidthMm.HasValue || request.HeightMm.HasValue)
        ? new CadDimensionSpec
        {
            LengthMm = request.LengthMm,
            WidthMm = request.WidthMm,
            HeightMm = request.HeightMm
        }
        : null;

    var result = await toolkit.RunEngineeringWorkbench(new EngineeringWorkRequest
    {
        Description = request.Description,
        PartType = request.PartType,
        Parameters = request.Parameters,
        ProviderOnly = request.ProviderOnly ?? false,
        Dimensions = dims,
        MaxIterations = request.MaxIterations ?? 3
    });

    var bundle = EngineeringOutputBundler.Create(
        env.ContentRootPath,
        request.Description,
        result,
        request.CreateZip ?? true);

    var response = new
    {
        result,
        artifacts = new
        {
            outputDir = bundle.OutputDir,
            zipPath = bundle.ZipPath,
            files = bundle.FilesWritten
        }
    };

    return result.Success ? Results.Ok(response) : Results.UnprocessableEntity(response);
});

// Multi-part collection execution: generates a project folder + one collection zip.
app.MapPost("/engineering/collection", async (EngineeringCollectionRequest request, Toolkit toolkit, IWebHostEnvironment env) =>
{
    if (request.Parts == null || request.Parts.Count == 0)
    {
        return Results.BadRequest(new { error = "At least one part is required." });
    }

    var repoRoot = EngineeringOutputBundler.ResolveRepoRoot(env.ContentRootPath);
    var partsRoot = Path.Combine(repoRoot, "tmp", "engineering", "_collections_parts");
    Directory.CreateDirectory(partsRoot);

    var partArtifacts = new List<EngineeringCollectionPartArtifact>();

    foreach (var part in request.Parts)
    {
        var partName = string.IsNullOrWhiteSpace(part.Name) ? "part" : part.Name.Trim();
        var partDescription = string.IsNullOrWhiteSpace(part.Description)
            ? partName
            : $"{partName}: {part.Description}";
        var maxIterations = part.MaxIterations ?? request.DefaultMaxIterations ?? 2;

        var dims = (part.LengthMm.HasValue || part.WidthMm.HasValue || part.HeightMm.HasValue)
            ? new CadDimensionSpec
            {
                LengthMm = part.LengthMm,
                WidthMm = part.WidthMm,
                HeightMm = part.HeightMm
            }
            : null;

        var result = await toolkit.RunEngineeringWorkbench(new EngineeringWorkRequest
        {
            Description = partDescription,
            PartType = part.PartType,
            Parameters = part.Parameters,
            ProviderOnly = part.ProviderOnly ?? request.ProviderOnly ?? false,
            Dimensions = dims,
            MaxIterations = maxIterations
        });

        var partSlug = EngineeringOutputBundler.Slugify(partName);
        var partDir = Path.Combine(partsRoot, $"{DateTime.UtcNow:yyyyMMdd_HHmmss}_{partSlug}_{Guid.NewGuid().ToString("N")[..8]}");
        var partBundle = EngineeringOutputBundler.WriteToDirectory(
            partDir,
            part.Description,
            result);

        partArtifacts.Add(new EngineeringCollectionPartArtifact
        {
            Name = partName,
            Description = partDescription,
            PartType = part.PartType,
            Parameters = part.Parameters,
            Success = result.Success,
            GenerationSource = result.GenerationSource,
            ProviderAttempts = result.ProviderAttempts,
            PartDir = partBundle.OutputDir,
            Files = partBundle.FilesWritten,
            BoundingBoxMm = result.CadResult?.Validation?.BoundingBoxMm,
            TriangleCount = result.CadResult?.Validation?.TriangleCount,
            X = part.X,
            Y = part.Y,
            Z = part.Z,
            RxDeg = part.RxDeg,
            RyDeg = part.RyDeg,
            RzDeg = part.RzDeg,
            Error = result.Error
        });
    }

    var connections = (request.Connections ?? new List<EngineeringCollectionConnectionRequest>())
        .Select(c => new EngineeringAssemblyConnection
        {
            From = c.From,
            To = c.To,
            Relation = c.Relation ?? "connects"
        })
        .ToList();

    var collectionName = string.IsNullOrWhiteSpace(request.Name)
        ? "engineering-collection"
        : request.Name.Trim();

    var validation = EngineeringAssemblyValidator.Validate(partArtifacts, connections);

    var collection = EngineeringCollectionBundler.Create(
        env.ContentRootPath,
        collectionName,
        partArtifacts,
        connections,
        validation,
        request.CreateZip ?? true);

    var allSuccess = partArtifacts.All(p => p.Success);
    var response = new
    {
        collection = new
        {
            name = collectionName,
            outputDir = collection.OutputDir,
            zipPath = collection.ZipPath,
            files = collection.FilesWritten,
            partCount = partArtifacts.Count,
            successCount = partArtifacts.Count(p => p.Success),
            failureCount = partArtifacts.Count(p => !p.Success),
            validation = new
            {
                passed = validation.Passed,
                issues = validation.Issues
            }
        },
        parts = partArtifacts.Select(p => new
        {
            p.Name,
            p.PartType,
            p.Success,
            p.GenerationSource,
            p.ProviderAttempts,
            p.PartDir,
            p.Files,
            p.BoundingBoxMm,
            p.TriangleCount,
            p.Error
        })
    };

    return (allSuccess && validation.Passed) ? Results.Ok(response) : Results.UnprocessableEntity(response);
});

app.MapGet("/engineering/providers/status", async (bool? probe, CancellationToken ct) =>
{
    var includeProbes = probe ?? false;
    var providers = await EngineeringProviderStatusService.GetStatus(includeProbes, ct);
    return Results.Ok(new
    {
        probe = includeProbes,
        providers
    });
});

app.MapGet("/engineering/toolchain/setup", () =>
{
    return Results.Ok(EngineeringProviderStatusService.GetSetupGuide());
});

app.Run();

// === Request/Response Models ===

public record MessageRequest(string Message, string? UserId = null, bool Urgent = false);
public record GoalRequest(string Title, string? Description, string? UserId, GoalType? Type, GoalPriority? Priority);
public record CadRequest(string Description, string? UserId = null, bool Urgent = false);
public record CadExecuteRequest(
    string Description,
    float? LengthMm = null,
    float? WidthMm = null,
    float? HeightMm = null,
    int? MaxIterations = null);

public record EngineeringExecuteRequest(
    string Description,
    float? LengthMm = null,
    float? WidthMm = null,
    float? HeightMm = null,
    int? MaxIterations = null,
    string? PartType = null,
    Dictionary<string, double>? Parameters = null,
    bool? ProviderOnly = null,
    bool? CreateZip = true);

public record EngineeringCollectionRequest(
    string Name,
    List<EngineeringCollectionPartRequest> Parts,
    List<EngineeringCollectionConnectionRequest>? Connections = null,
    int? DefaultMaxIterations = null,
    bool? ProviderOnly = null,
    bool? CreateZip = true);

public record EngineeringCollectionPartRequest(
    string Name,
    string Description,
    float? LengthMm = null,
    float? WidthMm = null,
    float? HeightMm = null,
    int? MaxIterations = null,
    string? PartType = null,
    Dictionary<string, double>? Parameters = null,
    bool? ProviderOnly = null,
    double? X = null,
    double? Y = null,
    double? Z = null,
    double? RxDeg = null,
    double? RyDeg = null,
    double? RzDeg = null);

public record EngineeringCollectionConnectionRequest(
    string From,
    string To,
    string? Relation = null);
