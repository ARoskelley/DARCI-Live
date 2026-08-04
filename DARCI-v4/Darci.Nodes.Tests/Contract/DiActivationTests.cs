using Darci.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Darci.Nodes.Tests.Contract;

/// <summary>
/// REGRESSION GUARD. Every other test constructs these types by picking a constructor explicitly, so a type
/// that the DI CONTAINER cannot activate still passes the whole suite — and then the host dies at
/// <c>Host.StartAsync</c>. That is exactly what happened: <see cref="NodeRouter"/> gained a second
/// constructor and the app could not start, while 403 tests stayed green.
///
/// <para>These tests resolve through a real <see cref="ServiceProvider"/>, the way the app does.</para>
/// </summary>
public sealed class DiActivationTests : IDisposable
{
    private readonly string _dbPath;

    public DiActivationTests()
        => _dbPath = Path.Combine(Path.GetTempPath(), $"darci-di-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { }
    }

    private sealed class StubNode : INode
    {
        public NodeId Id => NodeId.Coding;
        public IReadOnlySet<Capability> Capabilities { get; } = new HashSet<Capability> { Capability.WriteCode };
        public Task<NodePacket> HandleAsync(NodePacket packet, CancellationToken ct = default) => Task.FromResult(packet);
    }

    /// <summary>Wires the node-side graph the way Program.cs does.</summary>
    private ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b.AddProvider(NullLoggerProvider.Instance));

        services.AddSingleton<INodePacketStore>(sp =>
            new SqliteNodePacketStore($"Data Source={_dbPath}", sp.GetRequiredService<ILogger<SqliteNodePacketStore>>()));
        services.AddSingleton<INodeTelemetrySink, LoggingNodeTelemetrySink>();
        services.AddSingleton<NodeDispatcher>();
        services.AddSingleton<INode, StubNode>();
        services.AddSingleton<INodeRegistry>(sp =>
        {
            var registry = new NodeRegistry(sp.GetRequiredService<ILogger<NodeRegistry>>());
            foreach (var node in sp.GetServices<INode>())
                registry.Register(LegacyPacketNodeAdapter.ForLegacyNode(node));
            return registry;
        });
        services.AddSingleton<INodeRouter>(sp => new NodeRouter(
            sp.GetRequiredService<INodeRegistry>(),
            sp.GetRequiredService<NodeDispatcher>(),
            sp.GetRequiredService<INodePacketStore>(),
            sp.GetRequiredService<ILogger<NodeRouter>>()));

        // validateOnBuild mirrors ASP.NET Core's Development-time validation.
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    [Fact]
    public void TheNodeGraph_ResolvesThroughARealContainer()
    {
        using var provider = BuildProvider();

        Assert.NotNull(provider.GetRequiredService<INodeRouter>());
        Assert.NotNull(provider.GetRequiredService<INodeRegistry>());
        Assert.NotNull(provider.GetRequiredService<NodeDispatcher>());
        Assert.NotNull(provider.GetRequiredService<INodeTelemetrySink>());
    }

    [Fact]
    public void NodeRouter_ResolvesFromTypeBasedRegistration_DespiteHavingTwoConstructors()
    {
        // THE regression, reproduced exactly: type-based registration makes the CONTAINER choose the
        // constructor. With two candidates and no [ActivatorUtilitiesConstructor] marker this throws
        // "the following constructors are ambiguous" — at host start, long after every unit test passed,
        // because tests always pick a constructor themselves.
        var services = new ServiceCollection();
        services.AddLogging(b => b.AddProvider(NullLoggerProvider.Instance));
        services.AddSingleton<INodePacketStore>(sp =>
            new SqliteNodePacketStore($"Data Source={_dbPath}", sp.GetRequiredService<ILogger<SqliteNodePacketStore>>()));
        services.AddSingleton<INodeTelemetrySink, LoggingNodeTelemetrySink>();
        services.AddSingleton<NodeDispatcher>();
        services.AddSingleton<INode, StubNode>();
        services.AddSingleton<INodeRegistry>(sp =>
        {
            var registry = new NodeRegistry(sp.GetRequiredService<ILogger<NodeRegistry>>());
            foreach (var node in sp.GetServices<INode>())
                registry.Register(LegacyPacketNodeAdapter.ForLegacyNode(node));
            return registry;
        });
        services.AddSingleton<INodeRouter, NodeRouter>();   // ← the container must disambiguate

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        Assert.NotNull(provider.GetRequiredService<INodeRouter>());
    }

    [Fact]
    public void ModelBrokerGraph_ResolvesThroughARealContainer()
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b.AddProvider(NullLoggerProvider.Instance));
        services.AddHttpClient<OllamaModelProvider>();
        services.AddSingleton<IModelProvider>(sp => sp.GetRequiredService<OllamaModelProvider>());
        services.AddSingleton<IModelBroker>(sp => new ModelBroker(
            HostProfileLoader.FromEnvironment(),
            sp.GetServices<IModelProvider>(),
            sp.GetRequiredService<ILogger<ModelBroker>>()));

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

        var broker = provider.GetRequiredService<IModelBroker>();
        Assert.NotNull(broker.ResolveModelName(ModelClasses.ChatBalanced));
    }
}
