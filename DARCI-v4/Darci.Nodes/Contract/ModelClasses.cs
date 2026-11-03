#nullable enable

namespace Darci.Nodes;

/// <summary>
/// The model CAPABILITY CLASSES (doc §6.2). Callers request a class — never a model name — and the host
/// profile resolves it to a concrete provider + model.
///
/// <para>P4/§6.2: <i>"Nodes request a capability class, never a model name. This is the single most important
/// rule for collaborator portability."</i> A collaborator with different hardware edits one file; no code
/// changes. It is also what makes the "which model ran this?" question answerable in telemetry.</para>
/// </summary>
public static class ModelClasses
{
    /// <summary>Short, latency-sensitive work.</summary>
    public const string ChatFast = "chat.fast";

    /// <summary>General work — the default when a caller expresses no preference.</summary>
    public const string ChatBalanced = "chat.balanced";

    /// <summary>Hard reasoning / long context.</summary>
    public const string ChatDeep = "chat.deep";

    /// <summary>Structured labels / intent classification.</summary>
    public const string ClassifyIntent = "classify.intent";

    /// <summary>Vectors.</summary>
    public const string EmbedText = "embed.text";

    /// <summary>Code synthesis.</summary>
    public const string CodeGenerate = "code.generate";

    /// <summary>
    /// Fast/iterative code work. NOT in the doc's original six — added deliberately (Phase 2 fork F2) because
    /// the codebase already distinguishes fast from full coding (<c>ModelTaskType.FastCoding</c>), and
    /// collapsing them into <see cref="CodeGenerate"/> would be a silent behavior change.
    /// </summary>
    public const string CodeFast = "code.fast";

    /// <summary>Every class a host profile must bind. A profile missing one fails validation loudly.</summary>
    public static readonly string[] All =
    {
        ChatFast, ChatBalanced, ChatDeep, ClassifyIntent, EmbedText, CodeGenerate, CodeFast,
    };

    /// <summary>Classes that produce vectors rather than text.</summary>
    public static bool IsEmbedding(string modelClass) => modelClass == EmbedText;

    public static bool IsKnown(string? modelClass) =>
        modelClass is not null && Array.Exists(All, c => string.Equals(c, modelClass, StringComparison.Ordinal));
}
