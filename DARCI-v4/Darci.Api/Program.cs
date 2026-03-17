using Darci.Brain;
using Darci.Cloud;
using Darci.Core;
using Darci.Engineering;
using Darci.Api;
using Darci.Research;
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
    c.SwaggerDoc("v1", new() { Title = "DARCI API", Version = "v4.0" });
});

// HTTP client for Ollama
builder.Services.AddHttpClient<IOllamaClient, OllamaClient>();

// HTTP client for Python CAD service
builder.Services.AddHttpClient<ICadBridge, CadBridge>();
builder.Services.AddHttpClient<IEngineeringAssemblySimulationClient, EngineeringAssemblySimulationClient>();
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

// === v4 Brain Services ===

// State encoder: stateless, safe as singleton
builder.Services.AddSingleton<IStateEncoder, StateEncoder>();

// Experience buffer: SQLite ring buffer at same DB path as the rest of DARCI
// Constructor: (string connectionString, int maxCapacity = 10_000, ILogger? logger = null)
builder.Services.AddSingleton<ExperienceBuffer>(sp =>
    new ExperienceBuffer(
        connectionString,
        logger: sp.GetRequiredService<ILogger<ExperienceBuffer>>()));

// Decision Network: ONNX inference — falls back gracefully if model file is absent
var modelPath = Path.Combine(AppContext.BaseDirectory, "Models", "darci_policy.onnx");
builder.Services.AddSingleton<IDecisionNetwork>(sp =>
    new OnnxDecisionNetwork(
        sp.GetRequiredService<ILogger<OnnxDecisionNetwork>>(),
        sp.GetRequiredService<ExperienceBuffer>(),
        modelPath));

// Core DARCI components (singletons - one consciousness)
builder.Services.AddSingleton<Awareness>();
builder.Services.AddSingleton<State>();
// Constructor: (ILogger, IToolkit, IGoalManager, IStateEncoder, ExperienceBuffer, IDecisionNetwork, EngineeringGoalDetector?)
builder.Services.AddSingleton<Decision>(sp =>
    new Decision(
        sp.GetRequiredService<ILogger<Decision>>(),
        sp.GetRequiredService<IToolkit>(),
        sp.GetRequiredService<IGoalManager>(),
        sp.GetRequiredService<IStateEncoder>(),
        sp.GetRequiredService<ExperienceBuffer>(),
        sp.GetRequiredService<IDecisionNetwork>(),
        sp.GetRequiredService<EngineeringGoalDetector>()));

// DARCI herself - the background service
// Constructor: (ILogger, Awareness, Decision, State, IToolkit, IStateEncoder, ExperienceBuffer, EngineeringOrchestrator?)
builder.Services.AddSingleton<Darci.Core.Darci>(sp =>
    new Darci.Core.Darci(
        sp.GetRequiredService<ILogger<Darci.Core.Darci>>(),
        sp.GetRequiredService<Awareness>(),
        sp.GetRequiredService<Decision>(),
        sp.GetRequiredService<State>(),
        sp.GetRequiredService<IToolkit>(),
        sp.GetRequiredService<IStateEncoder>(),
        sp.GetRequiredService<ExperienceBuffer>(),
        sp.GetRequiredService<EngineeringOrchestrator>()));
builder.Services.AddHostedService(sp => sp.GetRequiredService<Darci.Core.Darci>());

// Controllers
builder.Services.AddControllers();

// === Research Store ===
builder.Services.AddSingleton<IResearchStore>(sp =>
    new ResearchStore(
        connectionString,
        sp.GetRequiredService<ILogger<ResearchStore>>()));

// === Engineering Services ===

// HTTP client for the Python geometry workbench on port 8001
builder.Services.AddHttpClient<IEngineeringTool, GeometryWorkbenchClient>();

// Geometry ONNX network (falls back to random exploration if model not present)
var geometryModelPath = Path.Combine(AppContext.BaseDirectory, "Models", "geometry_policy.onnx");
builder.Services.AddSingleton<IEngineeringNetwork>(sp =>
    new OnnxGeometryNetwork(
        sp.GetRequiredService<ILogger<OnnxGeometryNetwork>>(),
        geometryModelPath));

// Engineering goal detector (keyword-based)
builder.Services.AddSingleton<EngineeringGoalDetector>();

// Engineering orchestrator — wires workbench + network together
builder.Services.AddSingleton<EngineeringOrchestrator>(sp =>
    new EngineeringOrchestrator(
        sp.GetRequiredService<ILogger<EngineeringOrchestrator>>(),
        sp.GetRequiredService<IEngineeringTool>(),
        sp.GetRequiredService<IEngineeringNetwork>()));

// === AWS Cloud Relay (optional — off when credentials not set) ===
var cloudConfig = CloudConfig.FromEnvironment();
builder.Services.AddSingleton(cloudConfig);
builder.Services.AddSingleton<IS3FileStore>(sp =>
    new S3FileStore(cloudConfig, sp.GetRequiredService<ILogger<S3FileStore>>()));
builder.Services.AddSingleton<IMessageInbox, AwarenessMessageInbox>();
builder.Services.AddSingleton<IMessageOutbox, ResponseStoreOutbox>();
builder.Services.AddHostedService<SqsRelayService>();

// === SignalR (real-time hub for mobile app) ===
builder.Services.AddSignalR();
builder.Services.AddSingleton<DarciHubNotifier>();

// === CORS (allow mobile app connections) ===
builder.Services.AddCors(opt =>
{
    opt.AddPolicy("DarciApp", policy =>
        policy
            .SetIsOriginAllowed(_ => true)   // Android emulator / LAN IP
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials());            // required for SignalR negotiate
});

var app = builder.Build();

// === Initialize Brain (creates DB tables if needed) ===
var experienceBuffer = app.Services.GetRequiredService<ExperienceBuffer>();
await experienceBuffer.InitializeAsync();

// === Initialize Research store ===
var researchStore = app.Services.GetRequiredService<IResearchStore>();
await researchStore.InitializeAsync();

// === Middleware ===
app.UseCors("DarciApp");
app.UseDefaultFiles();    // serves wwwroot/index.html at /
app.UseStaticFiles();     // serves wwwroot/** (includes /app/index.html)

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.MapControllers();

// SignalR hub
app.MapHub<DarciHub>("/hub");

// === Minimal API Endpoints ===

// Health check
app.MapGet("/", () => "DARCI v4.0 - Neural Autonomous Consciousness");

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

// Send a CAD request through DARCI's message pipeline
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

// Direct engineering workbench execution with artifact bundling
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
        MaxIterations = request.MaxIterations ?? 3,
        StrictToolValidation = request.StrictValidation ?? true
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

// Multi-part collection execution
app.MapPost("/engineering/collection", async (
    EngineeringCollectionRequest request,
    Toolkit toolkit,
    IWebHostEnvironment env,
    IEngineeringAssemblySimulationClient simulationClient,
    CancellationToken ct) =>
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
            MaxIterations = maxIterations,
            StrictToolValidation = part.StrictValidation ?? request.StrictValidation ?? true
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
            ValidationSummary = result.ValidationSummary,
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

    var simulationConnections = (request.Connections ?? new List<EngineeringCollectionConnectionRequest>())
        .Select(c => new EngineeringAssemblySimulationConnection
        {
            From = c.From,
            To = c.To,
            Relation = c.Relation ?? "connects",
            Motion = c.Motion == null
                ? null
                : new EngineeringAssemblyMotionSpec
                {
                    Type = c.Motion.Type,
                    Axis = c.Motion.Axis,
                    RangeDeg = c.Motion.RangeDeg,
                    RangeMm = c.Motion.RangeMm,
                    Steps = c.Motion.Steps,
                    PivotMm = c.Motion.PivotMm,
                    MovingPart = c.Motion.MovingPart
                }
        })
        .ToList();

    var collectionName = string.IsNullOrWhiteSpace(request.Name)
        ? "engineering-collection"
        : request.Name.Trim();

    var validation = EngineeringAssemblyValidator.Validate(partArtifacts, connections);
    var runSimulation = request.RunSimulation ?? true;
    EngineeringAssemblySimulationReport? simulation = null;
    if (runSimulation && partArtifacts.Count(p => p.Success) >= 2)
    {
        var simulationParts = partArtifacts
            .Select(p => new EngineeringAssemblySimulationPart
            {
                Name = p.Name,
                PartType = p.PartType,
                StlPath = Directory.GetFiles(p.PartDir, "*.stl", SearchOption.TopDirectoryOnly).FirstOrDefault(),
                X = p.X ?? 0.0,
                Y = p.Y ?? 0.0,
                Z = p.Z ?? 0.0,
                RxDeg = p.RxDeg ?? 0.0,
                RyDeg = p.RyDeg ?? 0.0,
                RzDeg = p.RzDeg ?? 0.0
            })
            .ToList();

        var requestedSamples = request.SimulationSamples ?? 256;
        var adaptiveCap = partArtifacts.Count switch
        {
            <= 6 => 1024,
            <= 10 => 768,
            <= 16 => 512,
            _ => 384
        };
        var effectiveSamples = Math.Clamp(requestedSamples, 64, adaptiveCap);

        simulation = await simulationClient.Simulate(new EngineeringAssemblySimulationRequest
        {
            Parts = simulationParts,
            Connections = simulationConnections,
            CollisionToleranceMm = request.CollisionToleranceMm ?? 0.1,
            ClearanceTargetMm = request.ClearanceTargetMm ?? 0.2,
            SamplePointsPerMesh = effectiveSamples
        }, ct);
    }

    var collection = EngineeringCollectionBundler.Create(
        env.ContentRootPath,
        collectionName,
        partArtifacts,
        connections,
        validation,
        simulation,
        request.CreateZip ?? true);

    var allSuccess = partArtifacts.All(p => p.Success);
    var simulationPassed = simulation?.Passed ?? true;
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
            },
            simulation
        },
        parts = partArtifacts.Select(p => new
        {
            p.Name,
            p.PartType,
            p.Success,
            p.GenerationSource,
            p.ProviderAttempts,
            p.ValidationSummary,
            p.PartDir,
            p.Files,
            p.BoundingBoxMm,
            p.TriangleCount,
            p.Error
        }),
        simulation
    };

    return (allSuccess && validation.Passed && simulationPassed)
        ? Results.Ok(response)
        : Results.UnprocessableEntity(response);
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

// === v4 Brain / Neural Monitoring Endpoints ===

// Brain status: network availability, experience buffer, training progress
app.MapGet("/brain/status", async (ExperienceBuffer buffer, IStateEncoder encoder, IDecisionNetwork network) =>
{
    var experienceCount = await buffer.CountAsync();
    return Results.Ok(new
    {
        version = "v4.0",
        phase = network.IsAvailable ? "Phase 3 — Neural Decision Making" : "Phase 1 — Data Collection",
        stateVectorDimensions = encoder.Dimensions,
        experienceBufferCount = experienceCount,
        networkAvailable = network.IsAvailable,
        networkEpsilon = network.Epsilon,
        networkTrainingSteps = network.TrainingSteps,
        networkMode = network.IsAvailable ? "live" : "fallback"
    });
});

// Hot-swap the neural model without restarting DARCI
app.MapPost("/brain/load-model", async (IDecisionNetwork network) =>
{
    var path = Path.Combine(AppContext.BaseDirectory, "Models", "darci_policy.onnx");
    if (!File.Exists(path))
        return Results.NotFound(new { error = "No model file found at expected path", path });

    await network.LoadModelAsync(path);
    return Results.Ok(new
    {
        message = "Model loaded successfully",
        isAvailable = network.IsAvailable
    });
});

// Recent decision log entries (for shadow-mode analysis and behavioral cloning)
app.MapGet("/brain/decisions", async (ExperienceBuffer buffer, int? limit) =>
{
    var actualLimit = Math.Clamp(limit ?? 50, 1, 500);
    var decisions = await buffer.GetRecentDecisionsAsync(actualLimit);
    return Results.Ok(new
    {
        count = decisions.Count,
        decisions = decisions.Select(d => new
        {
            d.Timestamp,
            d.ActionChosen,
            actionName = ((BrainAction)d.ActionChosen).ToString(),
            d.NetworkDecision,
            d.Confidence,
            stateVector = d.StateVector
        })
    });
});

// Experience buffer statistics
app.MapGet("/brain/experiences", async (ExperienceBuffer buffer) =>
{
    var count = await buffer.CountAsync();
    return Results.Ok(new
    {
        storedExperiences = count,
        bufferCapacity = 10_000,
        fillPercent = Math.Round(count / 10_000.0 * 100, 1)
    });
});

// Clear experience buffer (development / reset use only)
app.MapDelete("/brain/experiences", async (ExperienceBuffer buffer) =>
{
    await buffer.ClearAsync();
    return Results.Ok(new { message = "Experience buffer cleared." });
});

// === Research Endpoints ===

// List sessions
app.MapGet("/research/sessions", async (IResearchStore store, string? status, int? limit) =>
{
    var sessions = await store.GetSessionsAsync(status, limit ?? 50);
    return Results.Ok(sessions);
});

// Get a single session summary
app.MapGet("/research/sessions/{id}", async (IResearchStore store, string id) =>
{
    var summary = await store.GetSessionSummaryAsync(id);
    return summary is null ? Results.NotFound() : Results.Ok(summary);
});

// Create a research session
app.MapPost("/research/sessions", async (IResearchStore store, CreateSessionRequest request) =>
{
    var session = await store.CreateSessionAsync(
        request.Title, request.Description, request.CreatedBy ?? "DARCI", request.Tags);
    return Results.Created($"/research/sessions/{session.Id}", session);
});

// Complete / fail a session
app.MapPatch("/research/sessions/{id}/complete", async (IResearchStore store, string id, string? status) =>
{
    var ok = await store.CompleteSessionAsync(id, status ?? "completed");
    return ok ? Results.Ok(new { sessionId = id, status = status ?? "completed" }) : Results.NotFound();
});

// Add a result to a session
app.MapPost("/research/sessions/{id}/results", async (IResearchStore store, string id, AddResultRequest request) =>
{
    var result = await store.AddResultAsync(
        id, request.Source, request.Content,
        request.ResultType ?? "text", request.Tags, request.RelevanceScore ?? 0f);
    return Results.Created($"/research/sessions/{id}/results/{result.Id}", result);
});

// Get results for a session
app.MapGet("/research/sessions/{id}/results", async (IResearchStore store, string id, string? type) =>
{
    var results = await store.GetResultsAsync(id, type);
    return Results.Ok(results);
});

// Search across all results
app.MapGet("/research/search", async (IResearchStore store, string q, int? limit) =>
{
    if (string.IsNullOrWhiteSpace(q)) return Results.BadRequest(new { error = "q is required" });
    var results = await store.SearchResultsAsync(q, limit ?? 20);
    return Results.Ok(results);
});

// List all files (optionally scoped to session)
app.MapGet("/research/files", async (IResearchStore store, string? sessionId) =>
{
    var files = await store.GetFilesAsync(sessionId);
    return Results.Ok(files);
});

// Download a research file
app.MapGet("/research/files/{id}/download", async (IResearchStore store, string id) =>
{
    var file = await store.GetFileAsync(id);
    if (file is null || !File.Exists(file.FilePath))
        return Results.NotFound(new { error = "File not found" });

    var bytes = await File.ReadAllBytesAsync(file.FilePath);
    return Results.File(bytes, file.ContentType, file.Filename);
});

// Register a file artifact (external agents call this after writing the file to disk)
app.MapPost("/research/sessions/{id}/files", async (
    IResearchStore store, DarciHubNotifier notifier, string id, RegisterFileRequest request) =>
{
    var file = await store.RegisterFileAsync(
        id, request.Filename, request.ContentType, request.FilePath, request.SizeBytes);

    // Push notification to all connected mobile clients
    await notifier.NotifyFileReady(file.Id, file.Filename, id,
        $"/research/files/{file.Id}/download");

    return Results.Created($"/research/files/{file.Id}/download", file);
});

// Delete a file record (does NOT delete the file on disk)
app.MapDelete("/research/files/{id}", async (IResearchStore store, string id) =>
{
    var ok = await store.DeleteFileAsync(id);
    return ok ? Results.Ok(new { deleted = id }) : Results.NotFound();
});

// === Engineering Neural Endpoints ===

// Neural engineering status
app.MapGet("/engineering/neural/status", async (IEngineeringTool workbench, IEngineeringNetwork network) =>
{
    var healthy = await workbench.IsHealthyAsync();
    return Results.Ok(new
    {
        workbenchHealthy = healthy,
        networkAvailable = network.IsAvailable,
        toolId           = workbench.ToolId,
        stateDimensions  = workbench.StateDimensions,
        actionCount      = workbench.ActionCount,
    });
});

// Run a neural engineering task manually (testing / direct dispatch)
app.MapPost("/engineering/neural/run", async (
    EngineeringGoalSpec spec, EngineeringOrchestrator orchestrator, CancellationToken ct) =>
{
    var result = await orchestrator.RunAsync(spec, ct);
    return result.Success ? Results.Ok(result) : Results.UnprocessableEntity(result);
});

// Hot-swap geometry ONNX model without restart
app.MapPost("/engineering/neural/load-model", async (IEngineeringNetwork network) =>
{
    var path = Path.Combine(AppContext.BaseDirectory, "Models", "geometry_policy.onnx");
    if (!File.Exists(path))
        return Results.NotFound(new { error = "geometry_policy.onnx not found at expected path", path });

    await network.LoadModelAsync(path);
    return Results.Ok(new { loaded = true, available = network.IsAvailable });
});

// === Cloud / AWS Endpoints ===

// Cloud relay status
app.MapGet("/cloud/status", (CloudConfig cfg) => Results.Ok(new
{
    configured      = cfg.IsConfigured,
    region          = cfg.Region,
    inboxQueue      = cfg.IsConfigured ? cfg.InboxQueueUrl : "(not set)",
    outboxQueue     = cfg.IsConfigured ? cfg.OutboxQueueUrl : "(not set)",
    filesBucket     = cfg.IsConfigured ? cfg.FilesBucket : "(not set)"
}));

// Upload a local research file to S3 and register it in the research store
// (DARCI or a research agent calls this after writing the file to disk on the host)
app.MapPost("/cloud/upload", async (
    IS3FileStore s3, IResearchStore store, DarciHubNotifier notifier,
    UploadFileRequest req, CancellationToken ct) =>
{
    if (!File.Exists(req.LocalPath))
        return Results.BadRequest(new { error = $"File not found on host: {req.LocalPath}" });

    var s3Key = await s3.UploadFileAsync(
        req.LocalPath, req.Filename, req.ContentType, req.SessionId, ct);

    var presignedUrl = s3.GetPresignedUrl(s3Key);

    var fileInfo = new System.IO.FileInfo(req.LocalPath);
    var record   = await store.RegisterFileAsync(
        req.SessionId, req.Filename, req.ContentType, req.LocalPath, fileInfo.Length);

    await notifier.NotifyFileReady(record.Id, req.Filename, req.SessionId, presignedUrl);

    return Results.Created(presignedUrl, new { s3Key, presignedUrl, fileId = record.Id });
});

// List files in S3 (optionally scoped to session)
app.MapGet("/cloud/files", async (IS3FileStore s3, string? sessionId, CancellationToken ct) =>
{
    var files = await s3.ListFilesAsync(sessionId, ct);
    return Results.Ok(files);
});

app.Run();

// === Request/Response Models ===

public record MessageRequest(string Message, string? UserId = null, bool Urgent = false);
public record GoalRequest(string Title, string? Description, string? UserId, GoalType? Type, GoalPriority? Priority);
public record RegisterFileRequest(string Filename, string ContentType, string FilePath, long SizeBytes);
public record UploadFileRequest(string SessionId, string Filename, string ContentType, string LocalPath);
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
    bool? StrictValidation = null,
    bool? CreateZip = true);

public record EngineeringCollectionRequest(
    string Name,
    List<EngineeringCollectionPartRequest> Parts,
    List<EngineeringCollectionConnectionRequest>? Connections = null,
    int? DefaultMaxIterations = null,
    bool? ProviderOnly = null,
    bool? StrictValidation = null,
    bool? RunSimulation = null,
    double? CollisionToleranceMm = null,
    double? ClearanceTargetMm = null,
    int? SimulationSamples = null,
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
    bool? StrictValidation = null,
    double? X = null,
    double? Y = null,
    double? Z = null,
    double? RxDeg = null,
    double? RyDeg = null,
    double? RzDeg = null);

public record EngineeringCollectionConnectionRequest(
    string From,
    string To,
    string? Relation = null,
    EngineeringConnectionMotionRequest? Motion = null);

public record EngineeringConnectionMotionRequest(
    string? Type = null,
    List<double>? Axis = null,
    double? RangeDeg = null,
    double? RangeMm = null,
    int? Steps = null,
    List<double>? PivotMm = null,
    string? MovingPart = null);
