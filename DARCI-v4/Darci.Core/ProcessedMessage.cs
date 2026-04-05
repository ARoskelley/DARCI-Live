using Darci.Shared;
using Lizzy.Core.Models;

namespace Darci.Core;

/// <summary>
/// IncomingMessage enriched with Lizzy NLP results.
/// Lives in Darci.Core so it can reference Lizzy.Core.Models without
/// pulling Lizzy into Darci.Shared (which must remain zero-dependency).
/// </summary>
public record ProcessedMessage
{
    public required IncomingMessage Source { get; init; }
    public ComprehensionResult? Comprehension { get; init; }
    public ExtractionResult? Extraction { get; init; }
}
