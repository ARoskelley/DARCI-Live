#nullable enable

namespace Darci.Coding;

/// <summary>
/// Task types used to route prompts to the appropriate Ollama model.
/// </summary>
public enum ModelTaskType
{
    General,
    Coding,
    FastCoding,
    Embedding
}

/// <summary>
/// Routes text generation and embedding requests to the correct Ollama model
/// based on task type. Model names are read from environment variables:
///   DARCI_OLLAMA_MODEL (general), DARCI_OLLAMA_CODING_MODEL,
///   DARCI_OLLAMA_FAST_CODING_MODEL, DARCI_OLLAMA_EMBED_MODEL.
/// </summary>
public interface IModelRouter
{
    /// <summary>Generates text using the model appropriate for the given task type.</summary>
    Task<string> GenerateAsync(string prompt, ModelTaskType taskType = ModelTaskType.General, CancellationToken ct = default);

    /// <summary>Computes a float embedding vector for the given text. Returns empty array on failure.</summary>
    Task<float[]> GetEmbeddingAsync(string text, CancellationToken ct = default);
}
