#nullable enable

using Darci.Nodes;
using Microsoft.Extensions.Logging;

namespace Darci.Coding;

/// <summary>
/// Routes generation/embedding requests by task type.
///
/// <para><b>Phase 2 (P2a.3):</b> this is now a THIN ADAPTER over <see cref="IModelBroker"/> — it maps
/// <see cref="ModelTaskType"/> to a model CLASS and lets the host profile decide the concrete model. It no
/// longer reads model names from the environment or speaks HTTP itself, which is what removes the second
/// inference path. Kept as a named interface (fork F4) so ~57 existing call sites stay untouched.</para>
///
/// <para>The sampling parameters and soft-failure semantics below are deliberately identical to the
/// pre-broker implementation (temperature 0.4, num_predict 4096, empty string on failure, caller
/// cancellation propagates) so this swap changes no behavior.</para>
/// </summary>
public sealed class ModelRouter : IModelRouter
{
    private const double CodingTemperature = 0.4;
    private const int CodingMaxTokens = 4096;

    private readonly IModelBroker _broker;
    private readonly ILogger<ModelRouter> _logger;

    public ModelRouter(IModelBroker broker, ILogger<ModelRouter> logger)
    {
        _broker = broker;
        _logger = logger;

        _logger.LogInformation(
            "ModelRouter on profile '{Profile}' — general={General}, coding={Coding}, fast={Fast}, embed={Embed}",
            broker.Profile.ProfileId,
            broker.ResolveModelName(ModelClasses.ChatBalanced),
            broker.ResolveModelName(ModelClasses.CodeGenerate),
            broker.ResolveModelName(ModelClasses.CodeFast),
            broker.ResolveModelName(ModelClasses.EmbedText));
    }

    /// <summary>Task type → model class. This mapping is the whole adapter.</summary>
    internal static string ClassFor(ModelTaskType taskType) => taskType switch
    {
        ModelTaskType.Coding => ModelClasses.CodeGenerate,
        ModelTaskType.FastCoding => ModelClasses.CodeFast,
        ModelTaskType.Embedding => ModelClasses.EmbedText,
        _ => ModelClasses.ChatBalanced,
    };

    public async Task<string> GenerateAsync(string prompt, ModelTaskType taskType = ModelTaskType.General, CancellationToken ct = default)
    {
        var result = await _broker.CompleteAsync(
            new ModelRequest(ClassFor(taskType), prompt)
            {
                Temperature = CodingTemperature,
                MaxTokens = CodingMaxTokens,
                Purpose = $"coding.{taskType}",
            },
            ct);

        if (result.Succeeded) return result.Text;

        // Soft failure, exactly as before: the agent loop retries the step rather than dying. (Genuine
        // caller cancellation is rethrown by the broker and never reaches here.)
        _logger.LogWarning("ModelRouter.Generate failed for class {Class} (model {Model}): {Error}",
            result.ModelClass, result.ResolvedModel, result.Error);
        return "";
    }

    public async Task<float[]> GetEmbeddingAsync(string text, CancellationToken ct = default)
    {
        var result = await _broker.EmbedAsync(new EmbeddingRequest(text) { Purpose = "coding.embed" }, ct);

        if (result.Succeeded) return result.Vector as float[] ?? result.Vector.ToArray();

        _logger.LogDebug("ModelRouter.GetEmbedding failed (non-fatal): {Error}", result.Error);
        return Array.Empty<float>();
    }
}
