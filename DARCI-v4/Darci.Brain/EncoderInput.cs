namespace Darci.Brain;

/// <summary>
/// All values required to encode DARCI's current state into a 29-dimensional vector.
/// This is the bridge between Core's rich object model and Brain's numerical world.
///
/// Populated by Darci.Core from its State + Perception objects, then handed to
/// IStateEncoder.Encode(). Brain never touches Core types directly — it only
/// sees this flat, numeric structure.
/// </summary>
public record EncoderInput
{
    // =========================================================
    // Internal State — indices 0–7
    // How DARCI feels right now.
    // =========================================================

    /// <summary>[0] 0 = exhausted, 1 = fully energized.</summary>
    public float Energy { get; init; }

    /// <summary>[1] 0 = scattered, 1 = laser focused.</summary>
    public float Focus { get; init; }

    /// <summary>[2] 0 = bored, 1 = deeply engaged.</summary>
    public float Engagement { get; init; }

    /// <summary>
    /// [3] Mood valence: -1 (very negative) to +1 (very positive).
    /// Caller maps Mood enum:
    ///   Calm=0.0, Alert=0.2, Curious=0.4, Content=0.5,
    ///   Satisfied=0.7, Reflective=0.1, Playful=0.6, Frustrated=-0.5
    /// </summary>
    public float MoodValence { get; init; }

    /// <summary>[4] 0 = barely feeling anything, 1 = intense emotion.</summary>
    public float MoodIntensity { get; init; }

    /// <summary>[5] Self-assessed confidence, 0–1.</summary>
    public float Confidence { get; init; }

    /// <summary>[6] Current warmth/empathy level, 0–1.</summary>
    public float Warmth { get; init; }

    /// <summary>[7] Current intellectual curiosity, 0–1.</summary>
    public float Curiosity { get; init; }

    // =========================================================
    // Situational Awareness — indices 8–19
    // What's happening in DARCI's environment.
    // =========================================================

    /// <summary>[8] Number of unprocessed messages in queue.</summary>
    public int MessagesWaiting { get; init; }

    /// <summary>[9] Whether any waiting message has urgency >= Now.</summary>
    public bool HasUrgentMessage { get; init; }

    /// <summary>[10] How long since the user last sent any message.</summary>
    public TimeSpan TimeSinceUserContact { get; init; }

    /// <summary>[11] How long since DARCI last took any action.</summary>
    public TimeSpan TimeSinceLastAction { get; init; }

    /// <summary>[12] Total number of active goals.</summary>
    public int ActiveGoalsCount { get; init; }

    /// <summary>[13] Number of active goals that have at least one pending step.</summary>
    public int GoalsWithPendingSteps { get; init; }

    /// <summary>[14] Number of memory items waiting to be processed/consolidated.</summary>
    public int PendingMemories { get; init; }

    /// <summary>[15] Number of completed async tasks waiting to be acknowledged.</summary>
    public int CompletedTasksWaiting { get; init; }

    /// <summary>[16] Whether the current time falls within quiet hours (do-not-disturb).</summary>
    public bool IsQuietHours { get; init; }

    /// <summary>[17] Whether DARCI is currently assigned to a specific goal.</summary>
    public bool HasActiveGoal { get; init; }

    /// <summary>[18] How many consecutive rest cycles have elapsed without action.</summary>
    public int ConsecutiveRestCycles { get; init; }

    /// <summary>[19] User trust level from personality traits, 0–1.</summary>
    public float UserTrustLevel { get; init; }

    // =========================================================
    // Message Context — indices 20–28
    // Characterises the top waiting message without using English.
    // All values are 0 when there are no messages waiting.
    // =========================================================

    /// <summary>[20] Character length of the top waiting message.</summary>
    public int TopMessageLength { get; init; }

    /// <summary>[21] Whether the top message contains a question mark.</summary>
    public bool TopMessageHasQuestion { get; init; }

    /// <summary>
    /// [22] Sentiment of the top message: -1 (negative) to +1 (positive).
    /// 0 when unknown or no message. Populated by a lightweight classifier
    /// or LLM sentiment call — not hardcoded keyword matching.
    /// </summary>
    public float TopMessageSentiment { get; init; }

    /// <summary>[23] Classifier confidence that intent is casual conversation.</summary>
    public float IntentConversation { get; init; }

    /// <summary>[24] Classifier confidence that intent is an action request.</summary>
    public float IntentRequest { get; init; }

    /// <summary>[25] Classifier confidence that intent is a research query.</summary>
    public float IntentResearch { get; init; }

    /// <summary>[26] Classifier confidence that intent is feedback/evaluation.</summary>
    public float IntentFeedback { get; init; }

    /// <summary>
    /// [27] Similarity score of the top message against recalled memories (0–1).
    /// 0 when no memories were checked. Populated by the memory subsystem.
    /// </summary>
    public float MemoryRelevance { get; init; }

    /// <summary>
    /// [28] Confidence in the currently relevant research topic, 0–1.
    /// Defaults toward 0.5 when no topic-specific synthesis is available.
    /// </summary>
    public float ResearchTopicConfidence { get; init; } = 0.5f;
}
