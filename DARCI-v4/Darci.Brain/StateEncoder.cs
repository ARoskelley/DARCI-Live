namespace Darci.Brain;

/// <summary>
/// Converts an <see cref="EncoderInput"/> into a 29-dimensional float vector
/// that feeds into the Decision Network.
///
/// Encoding rules (per ARCHITECTURE.md §3):
///   - Most dimensions: clamped to [0, 1]
///   - mood_valence (index 3) and msg_sentiment (index 22): clamped to [-1, 1]
///   - Message context (indices 20–27): all zero when no messages are waiting
///
/// This class is stateless and safe to use as a singleton.
/// </summary>
public sealed class StateEncoder : IStateEncoder
{
    public int Dimensions => 29;

    // Named index constants so changes to the vector layout are a single-point edit.
    // Grouped to match the three sections in ARCHITECTURE.md §3.
    private static class Idx
    {
        // Internal State
        public const int Energy            = 0;
        public const int Focus             = 1;
        public const int Engagement        = 2;
        public const int MoodValence       = 3;
        public const int MoodIntensity     = 4;
        public const int Confidence        = 5;
        public const int Warmth            = 6;
        public const int Curiosity         = 7;

        // Situational Awareness
        public const int MessagesWaiting   = 8;
        public const int HasUrgent         = 9;
        public const int TimeSinceUser     = 10;
        public const int TimeSinceAction   = 11;
        public const int ActiveGoals       = 12;
        public const int GoalsPending      = 13;
        public const int PendingMemories   = 14;
        public const int CompletedTasks    = 15;
        public const int IsQuietHours      = 16;
        public const int GoalActive        = 17;
        public const int ConsecRests       = 18;
        public const int TrustLevel        = 19;

        // Message Context
        public const int MsgLength         = 20;
        public const int MsgHasQuestion    = 21;
        public const int MsgSentiment      = 22;
        public const int IntentConversation = 23;
        public const int IntentRequest     = 24;
        public const int IntentResearch    = 25;
        public const int IntentFeedback    = 26;
        public const int MemoryRelevance   = 27;
        public const int ResearchTopicConfidence = 28;
    }

    public float[] Encode(EncoderInput input)
    {
        var s = new float[Dimensions];

        // === Internal State (0–7) ===
        s[Idx.Energy]        = Clamp01(input.Energy);
        s[Idx.Focus]         = Clamp01(input.Focus);
        s[Idx.Engagement]    = Clamp01(input.Engagement);
        s[Idx.MoodValence]   = ClampN11(input.MoodValence);
        s[Idx.MoodIntensity] = Clamp01(input.MoodIntensity);
        s[Idx.Confidence]    = Clamp01(input.Confidence);
        s[Idx.Warmth]        = Clamp01(input.Warmth);
        s[Idx.Curiosity]     = Clamp01(input.Curiosity);

        // === Situational Awareness (8–19) ===
        // Max-normalise counts by their expected maximums so the network
        // always sees values in [0, 1] regardless of scale.
        s[Idx.MessagesWaiting] = Clamp01(input.MessagesWaiting / 10f);
        s[Idx.HasUrgent]       = input.HasUrgentMessage ? 1f : 0f;
        s[Idx.TimeSinceUser]   = Clamp01((float)(input.TimeSinceUserContact.TotalHours / 24.0));
        s[Idx.TimeSinceAction] = Clamp01((float)(input.TimeSinceLastAction.TotalMinutes / 60.0));
        s[Idx.ActiveGoals]     = Clamp01(input.ActiveGoalsCount / 20f);

        // Fraction of active goals with pending steps (avoids division by zero).
        s[Idx.GoalsPending] = input.ActiveGoalsCount > 0
            ? Clamp01(input.GoalsWithPendingSteps / (float)input.ActiveGoalsCount)
            : 0f;

        s[Idx.PendingMemories] = Clamp01(input.PendingMemories / 10f);
        s[Idx.CompletedTasks]  = Clamp01(input.CompletedTasksWaiting / 10f);
        s[Idx.IsQuietHours]    = input.IsQuietHours ? 1f : 0f;
        s[Idx.GoalActive]      = input.HasActiveGoal ? 1f : 0f;
        s[Idx.ConsecRests]     = Clamp01(input.ConsecutiveRestCycles / 100f);
        s[Idx.TrustLevel]      = Clamp01(input.UserTrustLevel);

        // === Message Context (20–27) ===
        // Only populated when there is at least one message waiting.
        // If the queue is empty the slice stays all-zero, which is a valid
        // signal to the network ("nothing to reply to").
        if (input.MessagesWaiting > 0)
        {
            s[Idx.MsgLength]          = Clamp01(input.TopMessageLength / 1000f);
            s[Idx.MsgHasQuestion]     = input.TopMessageHasQuestion ? 1f : 0f;
            s[Idx.MsgSentiment]       = ClampN11(input.TopMessageSentiment);
            s[Idx.IntentConversation] = Clamp01(input.IntentConversation);
            s[Idx.IntentRequest]      = Clamp01(input.IntentRequest);
            s[Idx.IntentResearch]     = Clamp01(input.IntentResearch);
            s[Idx.IntentFeedback]     = Clamp01(input.IntentFeedback);
            s[Idx.MemoryRelevance]    = Clamp01(input.MemoryRelevance);
        }

        s[Idx.ResearchTopicConfidence] = Clamp01(input.ResearchTopicConfidence);

        return s;
    }

    private static float Clamp01(float v)  => Math.Clamp(v, 0f, 1f);
    private static float ClampN11(float v) => Math.Clamp(v, -1f, 1f);
}
