using Darci.Brain;
using Darci.Coding;
using Darci.Nodes;
using Darci.Cloud;
using Darci.Core;
using Darci.Engineering;
using Darci.Api;
using Darci.Research;
using Darci.Research.Agents;
using Darci.Research.Agents.Agents;
using Darci.Shared;
using Darci.Goals;
using Darci.Memory;
using Darci.Memory.Graph;
using Darci.Memory.Confidence;
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

// Load local secrets (gitignored — never committed)
builder.Configuration.AddJsonFile("appsettings.Secrets.json", optional: true, reloadOnChange: false);

// === Configuration ===
var configuredDbPath = Environment.GetEnvironmentVariable("DARCI_DB_PATH")
    ?? builder.Configuration["Darci:DbPath"];
var dbPath = string.IsNullOrWhiteSpace(configuredDbPath)
    ? Path.Combine(builder.Environment.ContentRootPath, "..", "Data", "darci.db")
    : configuredDbPath;
if (!Path.IsPathRooted(dbPath))
{
    dbPath = Path.Combine(builder.Environment.ContentRootPath, dbPath);
}

dbPath = Path.GetFullPath(dbPath);
Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
var connectionString = $"Data Source={dbPath}";
var telegramBotToken = Environment.GetEnvironmentVariable("DARCI_TELEGRAM_BOT_TOKEN") ?? "";
var telegramChatId = Environment.GetEnvironmentVariable("DARCI_TELEGRAM_CHAT_ID")
    ?? builder.Configuration["Notifications:Telegram:ChatId"]
    ?? "";

// === Services Registration ===

// Swagger for API exploration
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new() { Title = "DARCI API", Version = "v4.0" });
});

// === Phase 2: the MODEL BROKER (doc §6.2) ===
// The single place inference happens and the single place a model CLASS becomes a concrete model. Callers
// ask for a class (chat.balanced, code.generate, embed.text, …); host-profile.json decides what that means
// on this machine. An unsatisfiable class fails HERE, at startup, with a named error — not mid-task with a
// 404 on every generation (which is exactly how the gemma4:e4b typo behaved).
var hostProfilePath = Environment.GetEnvironmentVariable("DARCI_HOST_PROFILE")
    ?? Path.Combine(builder.Environment.ContentRootPath, "..", HostProfileLoader.FileName);
var (hostProfile, hostProfileFromFile) = HostProfileLoader.LoadOrDefault(Path.GetFullPath(hostProfilePath));

builder.Services.AddSingleton(hostProfile);
builder.Services.AddHttpClient<OllamaModelProvider>();
builder.Services.AddSingleton<IModelProvider>(sp => sp.GetRequiredService<OllamaModelProvider>());
builder.Services.AddSingleton<ModelCallStoreSink>();
builder.Services.AddSingleton<IModelCallSink>(sp => sp.GetRequiredService<ModelCallStoreSink>());
builder.Services.AddSingleton<IModelBroker>(sp => new ModelBroker(
    hostProfile,
    sp.GetServices<IModelProvider>(),
    sp.GetRequiredService<ILogger<ModelBroker>>(),
    sp.GetRequiredService<IModelCallSink>()));

// Both legacy model interfaces are now thin ADAPTERS over the broker (Phase 2 fork F4): keeping them means
// ~57 existing call sites are untouched, while the hardcoded-model bypass inside OllamaClient is gone.
builder.Services.AddSingleton<IOllamaClient, OllamaClient>();

// HTTP clients for external research APIs
builder.Services.AddHttpClient("tavily");
builder.Services.AddHttpClient("firecrawl");

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
        text => ollama.GetEmbedding(text),
        prompt => ollama.Generate(prompt),
        sp.GetRequiredService<IKnowledgeGraph>(),
        sp.GetRequiredService<IConfidenceTracker>());
});

// Goals (singleton)
builder.Services.AddSingleton<IGoalManager>(sp =>
    new GoalManager(
        sp.GetRequiredService<ILogger<GoalManager>>(),
        connectionString));

// Toolkit (singleton)
builder.Services.AddSingleton<Toolkit>();
builder.Services.AddSingleton<IToolkit>(sp => sp.GetRequiredService<Toolkit>());
builder.Services.AddSingleton<IResearchToolbox, ResearchToolbox>();

// Response delivery + notification fanout
builder.Services.AddSingleton<IResponseStore, InMemoryResponseStore>();
builder.Services.AddSingleton<INotificationLogStore, InMemoryNotificationLogStore>();
builder.Services.AddSingleton(new DarciNotificationPreferences
{
    EmailEnabled = string.Equals(
        Environment.GetEnvironmentVariable("DARCI_EMAIL_ENABLED")
            ?? builder.Configuration["Notifications:Email:Enabled"],
        "true",
        StringComparison.OrdinalIgnoreCase),
    TelegramEnabled = string.Equals(
        Environment.GetEnvironmentVariable("DARCI_TELEGRAM_ENABLED")
            ?? builder.Configuration["Notifications:Telegram:Enabled"],
        "true",
        StringComparison.OrdinalIgnoreCase),
    EmailTo = Environment.GetEnvironmentVariable("DARCI_EMAIL_TO")
        ?? builder.Configuration["Notifications:Email:To"]
        ?? "",
    EmailFrom = Environment.GetEnvironmentVariable("DARCI_EMAIL_FROM")
        ?? builder.Configuration["Notifications:Email:From"]
        ?? "",
    SmtpHost = Environment.GetEnvironmentVariable("DARCI_SMTP_HOST")
        ?? builder.Configuration["Notifications:Email:SmtpHost"]
        ?? "",
    SmtpPort = int.TryParse(
        Environment.GetEnvironmentVariable("DARCI_SMTP_PORT")
            ?? builder.Configuration["Notifications:Email:SmtpPort"],
        out var smtpPort)
        ? smtpPort
        : 587,
    SmtpUseSsl = !string.Equals(
        Environment.GetEnvironmentVariable("DARCI_SMTP_USE_SSL")
            ?? builder.Configuration["Notifications:Email:SmtpUseSsl"],
        "false",
        StringComparison.OrdinalIgnoreCase),
    SmtpUsername = Environment.GetEnvironmentVariable("DARCI_SMTP_USERNAME")
        ?? builder.Configuration["Notifications:Email:SmtpUsername"]
        ?? "",
    SmtpPassword = Environment.GetEnvironmentVariable("DARCI_SMTP_PASSWORD")
        ?? builder.Configuration["Notifications:Email:SmtpPassword"]
        ?? "",
    TelegramBotToken = telegramBotToken,
    TelegramChatId = telegramChatId,
    TelegramInboundEnabled = string.Equals(
        Environment.GetEnvironmentVariable("DARCI_TELEGRAM_INBOUND_ENABLED")
            ?? builder.Configuration["Notifications:Telegram:InboundEnabled"],
        "true",
        StringComparison.OrdinalIgnoreCase),
    TelegramInboundUserId = Environment.GetEnvironmentVariable("DARCI_TELEGRAM_INBOUND_USER_ID")
        ?? builder.Configuration["Notifications:Telegram:InboundUserId"]
        ?? "Tinman"
});
builder.Services.AddSingleton<INotificationProvider, EmailNotificationProvider>();
builder.Services.AddSingleton<INotificationProvider, TelegramNotificationProvider>();
builder.Services.AddSingleton<INotificationService, NotificationService>();
builder.Services.AddHostedService<ResponseDispatcher>();
builder.Services.AddHostedService<TelegramInboundService>();

// === Optional NLP Service ===
builder.Services.AddHttpClient("nlp");
builder.Services.AddSingleton<INlpClient>(sp =>
{
    var enabled = string.Equals(
        Environment.GetEnvironmentVariable("DARCI_NLP_ENABLED")
            ?? builder.Configuration["Nlp:Enabled"]
            ?? builder.Configuration["Lizzy:Enabled"],
        "true",
        StringComparison.OrdinalIgnoreCase);

    if (!enabled)
    {
        return NoopNlpClient.Instance;
    }

    var baseUrl = Environment.GetEnvironmentVariable("DARCI_NLP_BASE_URL")
        ?? builder.Configuration["Nlp:BaseUrl"]
        ?? builder.Configuration["Lizzy:BaseUrl"]
        ?? "http://localhost:5200";

    var client = new OptionalHttpNlpClient(
        sp.GetRequiredService<IHttpClientFactory>().CreateClient("nlp"),
        sp.GetRequiredService<ILogger<OptionalHttpNlpClient>>(),
        baseUrl,
        enabled);

    _ = Task.Run(async () =>
    {
        var reachable = await client.PingAsync();
        var logger = sp.GetRequiredService<ILogger<OptionalHttpNlpClient>>();
        if (reachable)
        {
            logger.LogInformation("Optional NLP service is up at {BaseUrl}.", baseUrl);
        }
    });

    return client;
});

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
// Constructor: (ILogger, IToolkit, IGoalManager, IStateEncoder, ExperienceBuffer, IDecisionNetwork, EngineeringGoalDetector?, IConfidenceTracker?, GoalDecomposer?)
// The last three parameters are nullable — use GetService<> so they resolve to null
// rather than throwing if a dependency isn't registered.
builder.Services.AddSingleton<Decision>(sp =>
    new Decision(
        sp.GetRequiredService<ILogger<Decision>>(),
        sp.GetRequiredService<IToolkit>(),
        sp.GetRequiredService<IGoalManager>(),
        sp.GetRequiredService<IStateEncoder>(),
        sp.GetRequiredService<ExperienceBuffer>(),
        sp.GetRequiredService<IDecisionNetwork>(),
        sp.GetService<EngineeringGoalDetector>(),
        sp.GetService<IConfidenceTracker>(),
        sp.GetService<GoalDecomposer>(),
        sp.GetService<CodingGoalDetector>()));

// DARCI herself - the background service
// BomGenerator + AutonomousBundler (autonomous engineering path)
builder.Services.AddSingleton<BomGenerator>();
builder.Services.AddSingleton<IAutonomousBundler>(sp =>
    new AutonomousBundler(
        builder.Environment.ContentRootPath,
        sp.GetRequiredService<ILogger<AutonomousBundler>>()));

// GoalDecomposer (LLM-driven step population after goal creation)
builder.Services.AddSingleton<GoalDecomposer>();

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
        sp.GetRequiredService<EngineeringOrchestrator>(),
        sp.GetRequiredService<IDeepResearchOrchestrator>(),
        sp.GetRequiredService<ConstraintExtractor>(),
        sp.GetRequiredService<IAutonomousBundler>(),
        sp.GetRequiredService<BomGenerator>(),
        sp.GetRequiredService<IGoalManager>(),
        sp.GetService<INodeRouter>()));
builder.Services.AddHostedService(sp => sp.GetRequiredService<Darci.Core.Darci>());

// Controllers
builder.Services.AddControllers();

// === Research Store ===
builder.Services.AddSingleton<IResearchStore>(sp =>
    new ResearchStore(
        connectionString,
        sp.GetRequiredService<ILogger<ResearchStore>>()));

builder.Services.AddSingleton<IKnowledgeGraph>(sp =>
    new KnowledgeGraph(
        connectionString,
        sp.GetRequiredService<ILogger<KnowledgeGraph>>()));

builder.Services.AddSingleton<IConfidenceTracker>(sp =>
    new ConfidenceTracker(
        connectionString,
        sp.GetRequiredService<IKnowledgeGraph>(),
        sp.GetRequiredService<ILogger<ConfidenceTracker>>()));

builder.Services.AddSingleton<WebResearchAgent>();
builder.Services.AddSingleton<GraphResearchAgent>();
builder.Services.AddSingleton<ReasoningAgent>();
builder.Services.AddSingleton<PubMedAgent>();
builder.Services.AddSingleton<IResearchAgentFactory, ResearchAgentFactory>();
builder.Services.AddSingleton<KnowledgeAssessor>();
builder.Services.AddSingleton<ConstraintExtractor>();
builder.Services.AddSingleton<DeepResearchOrchestrator>(sp => new DeepResearchOrchestrator(
    sp.GetRequiredService<IResearchStore>(),
    sp.GetRequiredService<IResearchAgentFactory>(),
    sp.GetRequiredService<IKnowledgeGraph>(),
    sp.GetRequiredService<IConfidenceTracker>(),
    sp.GetRequiredService<IResearchToolbox>(),
    sp.GetRequiredService<KnowledgeAssessor>(),
    sp.GetRequiredService<ILogger<DeepResearchOrchestrator>>()));
builder.Services.AddSingleton<IDeepResearchOrchestrator>(sp =>
    sp.GetRequiredService<DeepResearchOrchestrator>());

// Phase 2: rigid KG/DR pipeline — admin/KG (assessor) + review agent + compiler agent, behind the
// KnowledgeNode, producing a structured KnowledgeResponse.
builder.Services.AddSingleton<IKnowledgeAssessor>(sp => sp.GetRequiredService<KnowledgeAssessor>());
builder.Services.AddSingleton<IKnowledgeReviewAgent, OllamaKnowledgeReviewAgent>();
builder.Services.AddSingleton<IKnowledgeCompilerAgent, OllamaKnowledgeCompilerAgent>();
builder.Services.AddSingleton(new KnowledgePipelineOptions());
builder.Services.AddSingleton<IKnowledgePipeline, KnowledgePipeline>();

// Innovation node — escalated to by KGMA when KG + deep research are exhausted. Persists capped Innovated
// hypotheses (never promoted). Phase B generator + Phase D loop:
//   Phase B: the synthesizer (generator) and the SEPARATE plausibility reviewer (IKnowledgeReviewAgent).
//   Phase D: the diverse-candidate loop governor — N=3 diverse candidates → ONE comparative screen (critic)
//            → falsify the top survivors (reviewer). Progress-driven with a HARD budget backstop; N is
//            adaptive so it degrades gracefully on a single local-Ollama build.
builder.Services.AddSingleton<IInnovationSynthesizer, OllamaInnovationSynthesizer>();
builder.Services.AddSingleton<IInnovationCritic, OllamaInnovationCritic>();
builder.Services.AddSingleton(new InnovationLoopOptions());
builder.Services.AddSingleton<IInnovationLoop, InnovationLoopGovernor>();

// === Node Packet Protocol (Phase 0) ===
// Shared envelope + state machine + watchdog. Currently only the coding node emits packets, but the
// store/watchdog are app-wide so later nodes (engineering, knowledge, living loop) plug straight in.
builder.Services.AddSingleton<INodePacketStore>(sp =>
    new SqliteNodePacketStore(
        connectionString,
        sp.GetRequiredService<ILogger<SqliteNodePacketStore>>()));
builder.Services.AddSingleton<NodeWatchdog>();
builder.Services.AddHostedService<NodeWatchdogService>();

// Nodes registered with the router (Step B). CodingNode takes a Lazy<ICodingAgentLoop> to break the
// DI cycle (router → CodingNode → loop → router). New nodes (engineering, biomed) register here.
builder.Services.AddSingleton(sp => new Lazy<ICodingAgentLoop>(sp.GetRequiredService<ICodingAgentLoop>));
builder.Services.AddSingleton<INode, CodingNode>();
builder.Services.AddSingleton<INode, KnowledgeNode>();
builder.Services.AddSingleton<INode, InnovationNode>();

// === Phase 1 core carve: string-keyed node registry + ONE dispatch point ===
// Routing resolves a STRING capability verb from a manifest (not the compiled-in Capability enum), so an
// external node can declare a capability this core was never built with. Every node-capability invocation
// flows through NodeDispatcher, which emits the per-invocation telemetry record. (Model calls are NOT in
// scope here — those belong to the Phase 2 model broker.)
var nodesDirectory = Path.GetFullPath(
    Environment.GetEnvironmentVariable("DARCI_NODES_PATH")
    ?? Path.Combine(builder.Environment.ContentRootPath, "..", "nodes"));

// Telemetry lands in its OWN database (decision D6): different access pattern, different retention, and
// telemetry volume has no business in the knowledge graph or the trust ledger. Records reach it through a
// non-blocking queue, so a disk write can never slow down — or take down — the dispatch path.
var telemetryDbPath = Environment.GetEnvironmentVariable("DARCI_TELEMETRY_DB_PATH")
    ?? Path.Combine(Path.GetDirectoryName(dbPath)!, "telemetry.db");
telemetryDbPath = Path.GetFullPath(telemetryDbPath);
Directory.CreateDirectory(Path.GetDirectoryName(telemetryDbPath)!);
var telemetryConnectionString = $"Data Source={telemetryDbPath}";

builder.Services.AddSingleton<ITelemetryStore>(sp =>
    new SqliteTelemetryStore(telemetryConnectionString, sp.GetRequiredService<ILogger<SqliteTelemetryStore>>()));
builder.Services.AddSingleton<TelemetryStoreSink>();
builder.Services.AddSingleton<INodeTelemetrySink>(sp => new CompositeNodeTelemetrySink(new INodeTelemetrySink[]
{
    new LoggingNodeTelemetrySink(sp.GetRequiredService<ILogger<LoggingNodeTelemetrySink>>()),
    sp.GetRequiredService<TelemetryStoreSink>(),
}));
builder.Services.AddSingleton(sp => new NodeDispatcher(
    sp.GetRequiredService<ILogger<NodeDispatcher>>(),
    sp.GetRequiredService<INodeTelemetrySink>(),
    hostProfile));
builder.Services.AddSingleton<NodeManifestLoader>();
builder.Services.AddSingleton<INodeRegistrationStore>(sp =>
    new SqliteNodeRegistrationStore(connectionString, sp.GetRequiredService<ILogger<SqliteNodeRegistrationStore>>()));

builder.Services.AddSingleton<INodeRegistry>(sp =>
{
    var log = sp.GetRequiredService<ILogger<Program>>();
    var registry = new NodeRegistry(sp.GetRequiredService<ILogger<NodeRegistry>>());

    // Manifests are the source of truth for the capability surface. A manifest reviewed and merged into the
    // repo IS the human-authored capability grant (Phase E §14c); there is no runtime self-registration.
    var manifests = sp.GetRequiredService<NodeManifestLoader>().LoadAll(nodesDirectory)
        .ToDictionary(m => m.Manifest.NodeId, StringComparer.Ordinal);

    foreach (var node in sp.GetServices<INode>())
    {
        var nodeKey = CapabilityKey.From(node.Id);
        if (manifests.TryGetValue(nodeKey, out var loaded))
        {
            registry.Register(new LegacyPacketNodeAdapter(node, loaded.Manifest));
        }
        else
        {
            // No manifest on disk: fall back to a synthesized one so the node still routes, but say so —
            // a built-in node without a reviewed manifest is a gap to close, not a normal state.
            log.LogWarning("Node {NodeId} has no darci-node.json under {Dir}; using a synthesized manifest.",
                nodeKey, nodesDirectory);
            registry.Register(LegacyPacketNodeAdapter.ForLegacyNode(node));
        }
    }

    foreach (var orphan in manifests.Keys.Where(k => registry.ResolveNode(k) is null))
        log.LogWarning("Manifest for {NodeId} declares capabilities but no in-process node implements it; not routable.",
            orphan);

    return registry;
});

// Explicit factory, not AddSingleton<INodeRouter, NodeRouter>(): NodeRouter has a second (convenience)
// constructor for packet-native nodes, and letting the container pick is how the host ends up failing at
// start with "the following constructors are ambiguous".
builder.Services.AddSingleton<INodeRouter>(sp => new NodeRouter(
    sp.GetRequiredService<INodeRegistry>(),
    sp.GetRequiredService<NodeDispatcher>(),
    sp.GetRequiredService<INodePacketStore>(),
    sp.GetRequiredService<ILogger<NodeRouter>>()));

// Gap-driven action: persist gaps, decide immediate-fill vs deferred auto-goal. The handler routes via
// a Lazy<INodeRouter> to break the cycle (KnowledgeNode → GapHandler → router → KnowledgeNode).
builder.Services.AddSingleton<IGapStore>(sp =>
    new SqliteGapStore(connectionString, sp.GetRequiredService<ILogger<SqliteGapStore>>()));
builder.Services.AddSingleton(new GapHandlerOptions());
builder.Services.AddSingleton<IGapGoalSink, GoalManagerGapSink>();
builder.Services.AddSingleton(sp => new Lazy<INodeRouter>(sp.GetRequiredService<INodeRouter>));
builder.Services.AddSingleton<IGapHandler, GapHandler>();

// Innovation Phase A: provenance-tagged innovated-knowledge store + the empirical outcome-feedback
// loop (coding terminal → automatic staged promotion / retraction, all bounded by the ≤0.4 cap).
builder.Services.AddSingleton<IInnovatedKnowledgeStore>(sp =>
    new SqliteInnovatedKnowledgeStore(connectionString, sp.GetRequiredService<ILogger<SqliteInnovatedKnowledgeStore>>()));
builder.Services.AddSingleton(new OutcomeFeedbackOptions());
builder.Services.AddSingleton<IOutcomeFeedbackSink, InnovatedKnowledgeOutcomeSink>();

// Phase C: the human gate — durable ProposalStore + decision service. Approval is the one path that
// writes a human-authored ledger event (the only way to lift innovated knowledge above the cap).
builder.Services.AddSingleton<IProposalStore>(sp =>
    new SqliteProposalStore(connectionString, sp.GetRequiredService<ILogger<SqliteProposalStore>>()));
builder.Services.AddSingleton<IHumanGate, HumanGateService>();

// Phase E (sub-unit 1): validation-campaign store — pre-registered protocols + per-step evidence; the
// verdict is a pure function over (criteria × evidence). Rides the human gate for authorization/promotion.
builder.Services.AddSingleton<IValidationCampaignStore>(sp =>
    new SqliteValidationCampaignStore(connectionString, sp.GetRequiredService<ILogger<SqliteValidationCampaignStore>>()));

// Phase E (sub-unit 2): the campaign lifecycle — protocol critic (falsify the DESIGN) + the coordinator
// that drafts, authorizes, runs steps as child packets, computes the mechanical verdict, and files the
// promotion touch. The human gate resolves ICampaignCoordinator optionally, so it is registered here.
builder.Services.AddSingleton<IProtocolCritic, OllamaProtocolCritic>();
builder.Services.AddSingleton(new CampaignEligibilityOptions());

// Phase E (sub-unit 3): the objective/sandbox PoC gate — routes a candidate to the coding node for a
// sandboxed dry-run and attaches WEIGHT-CAPPED self-generated evidence before the human sees a proposal.
builder.Services.AddSingleton(new SandboxPoCOptions());
builder.Services.AddSingleton<ISandboxPoCGate, SandboxPoCGate>();

// Phase E (sub-unit 4): data-only tooling proposals — demand-driven, rate-limited, critic-reviewed. Never
// self-modifies, never registers a node at runtime (compile-time only); the capability boundary is the
// trust boundary (§0a). A blocked campaign step emits one; a human builds the node, then the campaign resumes.
builder.Services.AddSingleton<IToolingCritic, OllamaToolingCritic>();
builder.Services.AddSingleton(new ToolingProposalOptions());
builder.Services.AddSingleton<IToolingProposalEmitter, ToolingProposalEmitter>();

// Priority-ordered work scheduler (seam for a future resource-allocation scheduler). Human-initiated work
// outranks auto-drafted. Today a simple in-memory priority queue; the interface is what callers depend on.
builder.Services.AddSingleton<IWorkScheduler, PriorityWorkQueue>();

builder.Services.AddSingleton<ICampaignCoordinator, CampaignCoordinator>();

// Auto-draft eligibility sweep — a standalone hosted service (like the node watchdog), NOT called from the
// innovation node. It only auto-DRAFTS campaigns (low priority); every one still parks for human
// authorization. Disabled by default; opt in via CampaignSweepOptions.Enabled.
builder.Services.AddSingleton(new CampaignSweepOptions());
builder.Services.AddSingleton<CampaignEligibilitySweep>();
builder.Services.AddHostedService<CampaignEligibilitySweepService>();

// === Coding Workspace Services ===
builder.Services.AddSingleton<ICodingWorkspaceStore>(sp =>
    new CodingWorkspaceStore(
        connectionString,
        sp.GetRequiredService<ILogger<CodingWorkspaceStore>>()));

// Model router — now a thin adapter over the model broker (maps ModelTaskType → model class).
builder.Services.AddSingleton<ModelRouter>();
builder.Services.AddSingleton<IModelRouter>(sp => sp.GetRequiredService<ModelRouter>());

builder.Services.AddSingleton<ICodingContextBuilder>(sp =>
    new CodingContextBuilder(
        sp.GetRequiredService<ICodingWorkspaceStore>(),
        sp.GetRequiredService<IModelRouter>(),
        sp.GetRequiredService<IKnowledgeGraph>(),
        sp.GetRequiredService<ILogger<CodingContextBuilder>>()));

builder.Services.AddSingleton<IWorkspaceEmbeddingService, WorkspaceEmbeddingService>();
builder.Services.AddSingleton<IKgEnrichmentService, KgEnrichmentService>();

builder.Services.AddSingleton<IWorkspaceScanner>(sp =>
    new WorkspaceScanner(
        sp.GetRequiredService<ICodingWorkspaceStore>(),
        sp.GetRequiredService<IWorkspaceEmbeddingService>(),
        sp.GetRequiredService<IKgEnrichmentService>(),
        sp.GetRequiredService<ILogger<WorkspaceScanner>>()));

// Workspace-selection seam: lets the coding node pick or create a workspace for an autonomous
// coding goal. Auto-created workspaces land under the gitignored DARCI-v4/Workspaces directory.
var codingWorkspacesRoot = Environment.GetEnvironmentVariable("DARCI_CODING_WORKSPACES_ROOT")
    ?? Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath, "..", "Workspaces"));
builder.Services.AddSingleton<IWorkContextResolver>(sp =>
    new CodingWorkspaceResolver(
        sp.GetRequiredService<ICodingWorkspaceStore>(),
        sp.GetRequiredService<IModelRouter>(),
        sp.GetRequiredService<IWorkspaceScanner>(),
        codingWorkspacesRoot,
        sp.GetRequiredService<ILogger<CodingWorkspaceResolver>>()));

builder.Services.AddSingleton<ISafeCommandRunner, SafeCommandRunner>();
builder.Services.AddSingleton<IGitCheckpointService, GitCheckpointService>();

builder.Services.AddSingleton<ICodingTaskService>(sp =>
    new CodingTaskService(
        sp.GetRequiredService<ICodingWorkspaceStore>(),
        sp.GetRequiredService<ICodingContextBuilder>(),
        sp.GetRequiredService<IModelRouter>()));

builder.Services.AddSingleton<IRoadblockDetector, RoadblockDetector>();
builder.Services.AddSingleton<PatchApplier>();
builder.Services.AddSingleton<ICodingAgentLoop, CodingAgentLoop>();

// === Engineering Services ===

// HTTP client for the Python geometry workbench on port 8001
builder.Services.AddHttpClient<IEngineeringTool, GeometryWorkbenchClient>();

// Geometry ONNX network (falls back to random exploration if model not present)
var geometryModelPath = Path.Combine(AppContext.BaseDirectory, "Models", "geometry_policy.onnx");
if (!File.Exists(geometryModelPath))
{
    var startupLogger = LoggerFactory.Create(b => b.AddConsole()).CreateLogger("Startup");
    startupLogger.LogWarning(
        "Geometry policy model not found at '{Path}'. " +
        "Engineering goals will use random exploration until a trained model is placed there. " +
        "Run Darci.Engineering.Training to produce geometry_policy.onnx, then copy it to Models/.",
        geometryModelPath);
}
builder.Services.AddSingleton<IEngineeringNetwork>(sp =>
    new OnnxGeometryNetwork(
        sp.GetRequiredService<ILogger<OnnxGeometryNetwork>>(),
        geometryModelPath));

// Engineering goal detector (keyword-based)
builder.Services.AddSingleton<EngineeringGoalDetector>();

// Coding goal detector (keyword-based) — lets the living loop route coding goals to the coding node
builder.Services.AddSingleton<CodingGoalDetector>();

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
if (cloudConfig.IsConfigured)
{
    builder.Services.AddHostedService<SqsRelayService>();
}

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

var knowledgeGraph = app.Services.GetRequiredService<IKnowledgeGraph>();
await knowledgeGraph.InitializeAsync();

var confidenceTracker = app.Services.GetRequiredService<IConfidenceTracker>();
await confidenceTracker.InitializeAsync();

var codingStore = app.Services.GetRequiredService<ICodingWorkspaceStore>();
await codingStore.InitializeAsync();

// === Initialize Node Packet store + reap any packets orphaned by a previous run ===
var nodePacketStore = app.Services.GetRequiredService<INodePacketStore>();
await nodePacketStore.InitializeAsync();
var gapStore = app.Services.GetRequiredService<IGapStore>();
await gapStore.InitializeAsync();
var innovatedStore = app.Services.GetRequiredService<IInnovatedKnowledgeStore>();
await innovatedStore.InitializeAsync();
var proposalStore = app.Services.GetRequiredService<IProposalStore>();
await proposalStore.InitializeAsync();
var validationCampaignStore = app.Services.GetRequiredService<IValidationCampaignStore>();
await validationCampaignStore.InitializeAsync();

// Phase 1: resolve the node registry (this is where manifest validation failures surface — loudly, at
// startup) and record the registered capability surface for audit (Phase E §14c). A changed manifest hash
// is flagged, so widening what DARCI can do leaves a durable trace.
var nodeRegistrationStore = app.Services.GetRequiredService<INodeRegistrationStore>();
await nodeRegistrationStore.InitializeAsync();
var telemetryStore = app.Services.GetRequiredService<ITelemetryStore>();
await telemetryStore.InitializeAsync();
app.Logger.LogInformation("Telemetry database: {Path}", telemetryDbPath);
var nodeRegistry = app.Services.GetRequiredService<INodeRegistry>();
foreach (var registration in nodeRegistry.Registrations)
{
    await nodeRegistrationStore.RecordAsync(new NodeRegistrationRecord
    {
        NodeId = registration.NodeId,
        NodeVersion = registration.Manifest.NodeVersion,
        ContractVersion = registration.Manifest.ContractVersion,
        ManifestSha256 = registration.ManifestSha256,
        Capabilities = registration.Manifest.Capabilities.Select(c => c.Name).ToList(),
        RegisteredAt = registration.RegisteredAt,
    });
}
app.Logger.LogInformation("Node registry ready: {Count} node(s) serving [{Capabilities}].",
    nodeRegistry.Registrations.Count, string.Join(", ", nodeRegistry.RoutableCapabilities));

// Which model profile is live, and what each class actually resolves to. Worth logging plainly: silent
// model-name drift is the failure mode this whole broker exists to prevent.
var activeProfile = app.Services.GetRequiredService<IModelBroker>().Profile;
app.Logger.LogInformation("Model profile '{Profile}' active ({Source}): {Bindings}",
    activeProfile.ProfileId,
    hostProfileFromFile ? Path.GetFullPath(hostProfilePath) : "synthesized from DARCI_OLLAMA_* env vars",
    string.Join(", ", ModelClasses.All.Select(c => $"{c}→{activeProfile.Resolve(c)!.Model}")));
// The startup sweep must run AFTER the proposal store is initialized so its carve-out can recognise
// packets legitimately parked pending a human decision (and not reap them as orphans).
var nodeWatchdog = app.Services.GetRequiredService<NodeWatchdog>();
await nodeWatchdog.SweepStartupOrphansAsync();

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
app.MapGet("/responses", (IResponseStore responses, CancellationToken ct) =>
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

    string partsRoot;
    try
    {
        var repoRoot = EngineeringOutputBundler.ResolveRepoRoot(env.ContentRootPath)
            ?? throw new InvalidOperationException("Could not resolve repo root from content root path.");
        partsRoot = Path.Combine(repoRoot, "tmp", "engineering", "_collections_parts");
        Directory.CreateDirectory(partsRoot);
    }
    catch (Exception ex)
    {
        return Results.Problem(
            title: "Failed to initialize collection output directory",
            detail: ex.Message,
            statusCode: 500);
    }

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

// Get agent jobs for a session
app.MapGet("/research/sessions/{id}/agents", async (IResearchStore store, string id) =>
{
    var jobs = await store.GetAgentJobsAsync(id);
    return Results.Ok(jobs);
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

// Run deep research directly and return the full structured outcome
app.MapPost("/research/deep", async (
    DeepResearchRequest request,
    IDeepResearchOrchestrator orchestrator,
    CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(request.Question))
    {
        return Results.BadRequest(new { error = "question is required" });
    }

var outcome = await orchestrator.RunDeepResearchAsync(request.Question, request.UserId ?? "Tinman", ct);
    return outcome.IsSuccess ? Results.Ok(outcome) : Results.UnprocessableEntity(outcome);
});

// === Coding Workspace Endpoints ===

app.MapPost("/coding/workspaces/import", async Task<IResult> (
    CodingWorkspaceImportRequest request,
    IWorkspaceScanner scanner,
    CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(request.RootPath))
    {
        return Results.BadRequest(new { error = "rootPath is required" });
    }

    try
    {
        var result = await scanner.ImportAsync(request, ct);
        return Results.Created($"/coding/workspaces/{result.Workspace.Id}", result);
    }
    catch (Exception ex) when (ex is ArgumentException or DirectoryNotFoundException or InvalidOperationException)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.MapGet("/coding/workspaces", async Task<IResult> (
    ICodingWorkspaceStore store,
    int? limit,
    CancellationToken ct) =>
{
    var workspaces = await store.GetWorkspacesAsync(limit ?? 50, ct);
    return Results.Ok(workspaces);
});

app.MapGet("/coding/workspaces/{id}", async Task<IResult> (
    ICodingWorkspaceStore store,
    string id,
    CancellationToken ct) =>
{
    var workspace = await store.GetWorkspaceAsync(id, ct);
    return workspace is null ? Results.NotFound() : Results.Ok(workspace);
});

app.MapGet("/coding/workspaces/{id}/files", async Task<IResult> (
    ICodingWorkspaceStore store,
    string id,
    int? limit,
    CancellationToken ct) =>
{
    var workspace = await store.GetWorkspaceAsync(id, ct);
    if (workspace is null)
    {
        return Results.NotFound(new { error = "Workspace not found" });
    }

    var files = await store.GetFilesAsync(id, limit ?? 500, ct);
    return Results.Ok(files);
});

app.MapGet("/coding/workspaces/{id}/context", async Task<IResult> (
    ICodingContextBuilder contextBuilder,
    string id,
    string? query,
    int? limit,
    CancellationToken ct) =>
{
    try
    {
        var package = await contextBuilder.BuildAsync(id, query, limit ?? 8, ct);
        return Results.Ok(package);
    }
    catch (InvalidOperationException ex)
    {
        return Results.NotFound(new { error = ex.Message });
    }
});

app.MapPost("/coding/workspaces/{id}/commands", async Task<IResult> (
    ISafeCommandRunner runner,
    string id,
    CodingCommandRequest request,
    CancellationToken ct) =>
{
    try
    {
        var result = await runner.RunAsync(id, request, ct);
        return result.WasAllowed ? Results.Ok(result) : Results.BadRequest(result);
    }
    catch (Exception ex) when (ex is ArgumentException or DirectoryNotFoundException or InvalidOperationException)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.MapGet("/coding/workspaces/{id}/commands", async Task<IResult> (
    ICodingWorkspaceStore store,
    string id,
    int? limit,
    CancellationToken ct) =>
{
    var workspace = await store.GetWorkspaceAsync(id, ct);
    if (workspace is null)
    {
        return Results.NotFound(new { error = "Workspace not found" });
    }

    var runs = await store.GetCommandRunsAsync(id, limit ?? 50, ct);
    return Results.Ok(runs);
});

app.MapPost("/coding/tasks", async Task<IResult> (
    ICodingTaskService taskService,
    CreateCodingTaskRequest request,
    CancellationToken ct) =>
{
    try
    {
        var task = await taskService.CreateTaskAsync(request, ct);
        return Results.Created($"/coding/tasks/{task.Id}", task);
    }
    catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.MapGet("/coding/tasks", async Task<IResult> (
    ICodingWorkspaceStore store,
    string? workspaceId,
    int? limit,
    CancellationToken ct) =>
{
    var tasks = await store.GetTasksAsync(workspaceId, limit ?? 50, ct);
    return Results.Ok(tasks);
});

app.MapGet("/coding/tasks/{id}", async Task<IResult> (
    ICodingWorkspaceStore store,
    string id,
    CancellationToken ct) =>
{
    var task = await store.GetTaskAsync(id, ct);
    return task is null ? Results.NotFound() : Results.Ok(task);
});

// Run the coding agent loop for a task (async — returns 202 immediately).
app.MapPost("/coding/tasks/{id}/run", async Task<IResult> (
    ICodingAgentLoop loop,
    ICodingWorkspaceStore store,
    string id,
    RunCodingTaskRequest? request,
    CancellationToken ct) =>
{
    var task = await store.GetTaskAsync(id, ct);
    if (task is null) return Results.NotFound(new { error = "Task not found." });
    if (loop.IsRunning(id)) return Results.Conflict(new { error = "Loop is already running for this task." });

    var started = loop.StartLoop(id, request);
    return started
        ? Results.Accepted(null, new { taskId = id, message = "Coding agent loop started." })
        : Results.Conflict(new { error = "Failed to start loop (possible race condition)." });
});

// Get current run status for a task.
app.MapGet("/coding/tasks/{id}/status", async Task<IResult> (
    ICodingAgentLoop loop,
    string id,
    CancellationToken ct) =>
{
    var status = await loop.GetStatusAsync(id, ct);
    return status is null ? Results.NotFound() : Results.Ok(status);
});

// === Node Packet endpoints (Phase 0) ===
// Pollable state surface (decision 1): callers poll packet state rather than firing check requests.
// A coding task's packet shares its id as correlation id, so GET by correlation = the task's packet.
app.MapGet("/nodes/packets/{id}", async Task<IResult> (
    INodePacketStore store,
    string id,
    CancellationToken ct) =>
{
    var packet = await store.GetPacketAsync(id, ct);
    return packet is null ? Results.NotFound() : Results.Ok(packet);
});

app.MapGet("/nodes/packets/{id}/status", async Task<IResult> (
    INodePacketStore store,
    string id,
    CancellationToken ct) =>
{
    var status = await store.GetStatusAsync(id, ct);
    return status is null ? Results.NotFound() : Results.Ok(status);
});

// Retrievability for learning (decision 3): all packets sharing a correlation id (parent + children).
app.MapGet("/nodes/correlations/{correlationId}", async Task<IResult> (
    INodePacketStore store,
    string correlationId,
    CancellationToken ct) =>
{
    var packets = await store.GetByCorrelationAsync(correlationId, ct);
    return Results.Ok(packets);
});

// === Human gate (Phase C) — the interface a UI/FRIDAY channel calls ===
// List proposals awaiting a human decision.
app.MapGet("/human/proposals", async Task<IResult> (
    IHumanGate gate,
    int? limit,
    CancellationToken ct) =>
{
    var pending = await gate.ListPendingAsync(limit ?? 100, ct);
    return Results.Ok(pending);
});

// Submit a human decision. Approval of a promotion is the ONE path that raises trust above the cap.
app.MapPost("/human/proposals/{id}/decision", async Task<IResult> (
    IHumanGate gate,
    string id,
    HumanDecisionRequest request,
    CancellationToken ct) =>
{
    var result = await gate.DecideAsync(id, request.Approve, request.Note, request.DecidedBy, ct);
    return result.Applied ? Results.Ok(result) : Results.BadRequest(result);
});

// Create a git checkpoint for a workspace.
app.MapPost("/coding/workspaces/{id}/checkpoint", async Task<IResult> (
    IGitCheckpointService checkpoints,
    string id,
    CheckpointRequest request,
    CancellationToken ct) =>
{
    try
    {
        var checkpoint = await checkpoints.CreateCheckpointAsync(
            id, request.TaskId ?? "", request.Message ?? "Manual checkpoint", ct);
        return checkpoint is null
            ? Results.UnprocessableEntity(new { error = "Could not create checkpoint (git may not be initialised)." })
            : Results.Created($"/coding/workspaces/{id}/checkpoints/{checkpoint.Id}", checkpoint);
    }
    catch (Exception ex) when (ex is InvalidOperationException)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

// Rollback a workspace to its latest checkpoint.
app.MapPost("/coding/workspaces/{id}/rollback", async Task<IResult> (
    IGitCheckpointService checkpoints,
    string id,
    RollbackRequest request,
    CancellationToken ct) =>
{
    var ok = await checkpoints.RollbackToCheckpointAsync(
        id, request.TaskId ?? "", request.CheckpointId ?? "", ct);
    return ok
        ? Results.Ok(new { workspaceId = id, rolledBack = true })
        : Results.UnprocessableEntity(new { error = "Rollback failed. No checkpoint found or git reset failed." });
});

// Knowledge graph search
app.MapGet("/knowledge/entities/search", async (
    IKnowledgeGraph graph,
    string q,
    string? domain,
    int? limit,
    CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(q))
    {
        return Results.BadRequest(new { error = "q is required" });
    }

    var entities = await graph.SearchEntitiesAsync(q, domain, limit ?? 20, ct);
    return Results.Ok(entities);
});

app.MapGet("/knowledge/entities/{id}", async (IKnowledgeGraph graph, string id, CancellationToken ct) =>
{
    var entity = await graph.GetEntityAsync(id, ct);
    return entity is null ? Results.NotFound() : Results.Ok(entity);
});

app.MapGet("/knowledge/entities/{id}/neighbours", async (
    IKnowledgeGraph graph,
    string id,
    int? depth,
    string? relationType,
    CancellationToken ct) =>
{
    var neighbours = await graph.GetNeighboursAsync(id, depth ?? 1, relationType, ct);
    return Results.Ok(neighbours);
});

app.MapGet("/knowledge/path", async (
    IKnowledgeGraph graph,
    string fromEntityId,
    string toEntityId,
    int? maxHops,
    CancellationToken ct) =>
{
    var path = await graph.FindPathAsync(fromEntityId, toEntityId, maxHops ?? 5, ct);
    return path.IsEmpty ? Results.NotFound(path) : Results.Ok(path);
});

app.MapGet("/confidence/claims/uncertain", async (
    IConfidenceTracker confidence,
    float? threshold,
    string? domain,
    int? limit,
    CancellationToken ct) =>
{
    var claims = await confidence.GetUncertainClaimsAsync(threshold ?? 0.4f, domain, limit ?? 30, ct);
    return Results.Ok(claims);
});

app.MapGet("/confidence/entities/{entityId}/claims", async (
    IConfidenceTracker confidence,
    string entityId,
    int? limit,
    CancellationToken ct) =>
{
    var claims = await confidence.GetClaimsForEntityAsync(entityId, limit ?? 50, ct);
    return Results.Ok(claims);
});

app.MapGet("/confidence/contradictions", async (
    IConfidenceTracker confidence,
    string? domain,
    CancellationToken ct) =>
{
    var contradictions = await confidence.GetUnresolvedContradictionsAsync(domain, ct);
    return Results.Ok(contradictions);
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
    CloudConfig cfg,
    IS3FileStore s3, IResearchStore store, DarciHubNotifier notifier,
    UploadFileRequest req, CancellationToken ct) =>
{
    if (!cfg.IsConfigured)
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);

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
app.MapGet("/cloud/files", async (CloudConfig cfg, IS3FileStore s3, string? sessionId, CancellationToken ct) =>
{
    if (!cfg.IsConfigured)
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);

    var files = await s3.ListFilesAsync(sessionId, ct);
    return Results.Ok(files);
});

app.Run();

// === Request/Response Models ===

public record HumanDecisionRequest(bool Approve, string? Note = null, string? DecidedBy = null);
public record MessageRequest(string Message, string? UserId = null, bool Urgent = false);
public record GoalRequest(string Title, string? Description, string? UserId, GoalType? Type, GoalPriority? Priority);
public record RegisterFileRequest(string Filename, string ContentType, string FilePath, long SizeBytes);
public record UploadFileRequest(string SessionId, string Filename, string ContentType, string LocalPath);
public record DeepResearchRequest(string Question, string? UserId = null);
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

public record CheckpointRequest(string? TaskId = null, string? Message = null);
public record RollbackRequest(string? TaskId = null, string? CheckpointId = null);
