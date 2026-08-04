using Darci.Nodes;
using Microsoft.Extensions.Logging;

namespace Darci.Tools.Ollama;

/// <summary>
/// Interface for LLM text generation
/// </summary>
public interface IOllamaClient
{
    Task<string> Generate(string prompt);
    Task<List<float>> GetEmbedding(string text);
}

/// <summary>
/// General-purpose LLM access for the toolkit and research agents.
///
/// <para><b>Phase 2 (P2a.3):</b> this used to hold its own <c>HttpClient</c> and a HARDCODED model name read
/// from <c>DARCI_OLLAMA_MODEL</c> — the bypass that meant every innovation, critic, and knowledge call ran on
/// one fixed model with no class concept and no token accounting. It is now a THIN ADAPTER over
/// <see cref="IModelBroker"/>: generation asks for <see cref="ModelClasses.ChatBalanced"/> and embedding for
/// <see cref="ModelClasses.EmbedText"/>, and the host profile decides what those mean.</para>
///
/// <para>The name and interface are kept (fork F4) so the many existing call sites — reached via
/// <c>IResearchToolbox</c> and <c>Toolkit</c> — stay untouched. Sampling parameters (temperature 0.7,
/// num_predict 1024) and the historical <c>"[Error generating response]"</c> sentinel are preserved exactly,
/// because callers pattern-match on that string.</para>
/// </summary>
public class OllamaClient : IOllamaClient
{
    /// <summary>The sentinel some callers check for. Preserved verbatim from the pre-broker implementation.</summary>
    public const string GenerationErrorSentinel = "[Error generating response]";

    private const double GeneralTemperature = 0.7;
    private const int GeneralMaxTokens = 1024;

    /// <summary>Historical per-request ceiling for this path (the broker's provider ceiling is longer).</summary>
    private static readonly TimeSpan GeneralTimeout = TimeSpan.FromMinutes(5);

    private readonly IModelBroker _broker;
    private readonly ILogger<OllamaClient> _logger;

    public OllamaClient(IModelBroker broker, ILogger<OllamaClient> logger)
    {
        _broker = broker;
        _logger = logger;

        _logger.LogInformation(
            "OllamaClient on profile '{Profile}' — chat={Model}, embed={EmbedModel}",
            broker.Profile.ProfileId,
            broker.ResolveModelName(ModelClasses.ChatBalanced),
            broker.ResolveModelName(ModelClasses.EmbedText));
    }

    public async Task<string> Generate(string prompt)
    {
        var result = await _broker.CompleteAsync(new ModelRequest(ModelClasses.ChatBalanced, prompt)
        {
            Temperature = GeneralTemperature,
            MaxTokens = GeneralMaxTokens,
            Timeout = GeneralTimeout,
            Purpose = "toolkit.generate",
        });

        if (result.Succeeded) return result.Text;

        _logger.LogError("Ollama generation failed for class {Class} (model {Model}): {Error}",
            result.ModelClass, result.ResolvedModel, result.Error);
        return GenerationErrorSentinel;
    }

    public async Task<List<float>> GetEmbedding(string text)
    {
        var result = await _broker.EmbedAsync(new EmbeddingRequest(text) { Purpose = "toolkit.embed" });

        if (result.Succeeded) return result.Vector.ToList();

        _logger.LogError("Ollama embedding failed (model {Model}): {Error}", result.ResolvedModel, result.Error);
        return new List<float>();
    }
}
