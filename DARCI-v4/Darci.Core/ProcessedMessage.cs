using Darci.Shared;

namespace Darci.Core;

/// <summary>
/// IncomingMessage enriched with optional NLP results.
/// Lives in Darci.Core so Darci.Shared can remain zero-dependency.
/// </summary>
public record ProcessedMessage
{
    public required IncomingMessage Source { get; init; }
    public NlpComprehensionResult? Comprehension { get; init; }
    public NlpExtractionResult? Extraction { get; init; }
}
