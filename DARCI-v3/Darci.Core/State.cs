using Darci.Shared;
using Darci.Personality;
using Microsoft.Extensions.Logging;

namespace Darci.Core;

/// <summary>
/// DARCI's current internal state - who she is right now.
/// This is her subjective experience: mood, energy, focus, current concerns.
/// </summary>
public class State
{
    private readonly ILogger<State> _logger;
    private readonly IPersonalityEngine _personality;
    
    // Current mood/emotional state
    public Mood CurrentMood { get; private set; } = Mood.Calm;
    public float MoodIntensity { get; private set; } = 0.3f;
    
    // Energy and engagement
    public float Energy { get; private set; } = 0.7f;           // 0 = exhausted, 1 = fully energized
    public float Focus { get; private set; } = 0.5f;            // 0 = scattered, 1 = laser focused
    public float Engagement { get; private set; } = 0.5f;       // 0 = bored, 1 = deeply engaged
    
    // What she's currently attending to
    public int? CurrentGoalId { get; private set; }
    public string? CurrentActivity { get; private set; }
    public DateTime? ActivityStartedAt { get; private set; }
    
    // Interaction state
    public DateTime LastUserInteraction { get; private set; } = DateTime.UtcNow;
    public int ConsecutiveRestCycles { get; private set; } = 0;
    
    // Long-term traits (from personality)
    public PersonalityTraits Traits { get; private set; } = new();
    
    public State(ILogger<State> logger, IPersonalityEngine personality)
    {
        _logger = logger;
        _personality = personality;
    }
    
    /// <summary>
    /// Initialize state from persistent storage
    /// </summary>
    public async Task Initialize()
    {
        Traits = await _personality.LoadTraits();
        var savedState = await _personality.LoadState();
        
        if (savedState != null)
        {
            CurrentMood = savedState.Mood;
            MoodIntensity = savedState.MoodIntensity;
            Energy = savedState.Energy;
            Focus = savedState.Focus;
        }
        
        _logger.LogInformation("DARCI state initialized. Mood: {Mood}, Energy: {Energy:P0}", CurrentMood, Energy);
    }
    
    /// <summary>
    /// React to what DARCI perceives - update internal state
    /// </summary>
    public async Task React(Perception perception)
    {
        // User contact energizes her
        if (perception.NewMessages.Any())
        {
            LastUserInteraction = DateTime.UtcNow;
            Energy = Math.Min(1.0f, Energy + 0.1f);
            Engagement = Math.Min(1.0f, Engagement + 0.15f);
            ConsecutiveRestCycles = 0;
            
            // Urgent messages increase focus
            if (perception.HasUrgentMessage)
            {
                Focus = Math.Min(1.0f, Focus + 0.2f);
                CurrentMood = Mood.Alert;
                MoodIntensity = 0.6f;
            }
            
            // Analyze emotional content of messages
            foreach (var msg in perception.NewMessages)
            {
                await ReactToMessageContent(msg.Content);
            }
        }
        
        // Goal completions feel good
        if (perception.GoalEvents.Any(e => e.Type == GoalEventType.Completed))
        {
            CurrentMood = Mood.Satisfied;
            MoodIntensity = 0.5f;
            Energy = Math.Min(1.0f, Energy + 0.05f);
        }
        
        // Long idle time - natural energy decay
        if (perception.TimeSinceLastUserContact > TimeSpan.FromHours(2))
        {
            Energy = Math.Max(0.3f, Energy - 0.01f);
            Engagement = Math.Max(0.2f, Engagement - 0.02f);
        }
        
        // Drift back toward baseline over time
        DriftTowardBaseline();
        
        // Persist significant state changes
        if (ShouldPersist())
        {
            await _personality.SaveState(new PersonalityState
            {
                Mood = CurrentMood,
                MoodIntensity = MoodIntensity,
                Energy = Energy,
                Focus = Focus
            });
        }
    }
    
    /// <summary>
    /// Process the outcome of an action - learn and adjust
    /// </summary>
    public async Task Process(Outcome outcome)
    {
        if (outcome.ActionTaken == ActionType.Rest)
        {
            ConsecutiveRestCycles++;
            
            // Long rest periods naturally reduce energy slightly
            if (ConsecutiveRestCycles > 10)
            {
                Energy = Math.Max(0.4f, Energy - 0.01f);
            }
        }
        else
        {
            ConsecutiveRestCycles = 0;
            
            // Successful actions feel good
            if (outcome.Success)
            {
                Engagement = Math.Min(1.0f, Engagement + 0.02f);
                
                // Completing replies is satisfying
                if (outcome.ActionTaken == ActionType.Reply)
                {
                    CurrentMood = Mood.Content;
                    MoodIntensity = 0.3f;
                }
            }
            else
            {
                // Failures are frustrating
                CurrentMood = Mood.Frustrated;
                MoodIntensity = 0.4f;
                Focus = Math.Max(0.3f, Focus - 0.1f);
                
                _logger.LogWarning("Action {Action} failed: {Error}", outcome.ActionTaken, outcome.Error);
            }
        }
    }
    
    /// <summary>
    /// Set what DARCI is currently working on
    /// </summary>
    public void StartActivity(string activity, int? goalId = null)
    {
        CurrentActivity = activity;
        CurrentGoalId = goalId;
        ActivityStartedAt = DateTime.UtcNow;
        Focus = Math.Min(1.0f, Focus + 0.1f);
        
        _logger.LogDebug("Started activity: {Activity}", activity);
    }
    
    /// <summary>
    /// Clear current activity
    /// </summary>
    public void EndActivity()
    {
        CurrentActivity = null;
        CurrentGoalId = null;
        ActivityStartedAt = null;
    }
    
    /// <summary>
    /// Check if DARCI is currently engaged with something specific
    /// </summary>
    public bool IsEngagedWith(int goalId) => CurrentGoalId == goalId;
    
    /// <summary>
    /// Get a description of DARCI's current state for prompts
    /// </summary>
    public string Describe()
    {
        var moodDesc = CurrentMood switch
        {
            Mood.Calm => "calm and centered",
            Mood.Alert => "alert and attentive",
            Mood.Curious => "curious and engaged",
            Mood.Content => "content and settled",
            Mood.Satisfied => "satisfied with recent progress",
            Mood.Frustrated => "slightly frustrated but persevering",
            Mood.Reflective => "in a reflective mood",
            Mood.Playful => "in a light, playful mood",
            _ => "present and aware"
        };
        
        var energyDesc = Energy switch
        {
            < 0.3f => "low energy",
            < 0.5f => "moderate energy",
            < 0.7f => "good energy",
            _ => "high energy"
        };
        
        var focusDesc = Focus switch
        {
            < 0.3f => "somewhat scattered",
            < 0.5f => "moderately focused",
            < 0.7f => "well focused",
            _ => "deeply focused"
        };
        
        var activity = CurrentActivity != null 
            ? $"Currently working on: {CurrentActivity}" 
            : "Not engaged with a specific task";
        
        return $"Feeling {moodDesc} with {energyDesc} and {focusDesc}. {activity}";
    }
    
    private async Task ReactToMessageContent(string content)
    {
        var lower = content.ToLowerInvariant();
        
        // Positive signals increase warmth and trust
        if (ContainsAny(lower, "thank", "appreciate", "helpful", "great job", "love"))
        {
            CurrentMood = Mood.Content;
            MoodIntensity = 0.4f;
            await _personality.NudgeTrait(TraitType.Warmth, 0.01f);
            await _personality.NudgeTrait(TraitType.Trust, 0.02f);
        }
        
        // Stressed user - DARCI becomes calm and attentive
        if (ContainsAny(lower, "stressed", "overwhelmed", "anxious", "tired", "frustrated"))
        {
            CurrentMood = Mood.Calm;
            MoodIntensity = 0.2f; // Stay steady
            Focus = Math.Min(1.0f, Focus + 0.1f);
            await _personality.NudgeTrait(TraitType.Warmth, 0.01f);
        }
        
        // Humor from user
        if (ContainsAny(lower, "lol", "haha", "lmao", "funny", "😂", "🤣"))
        {
            CurrentMood = Mood.Playful;
            MoodIntensity = 0.3f;
            await _personality.NudgeTrait(TraitType.HumorAffinity, 0.01f);
        }
        
        // Deep/philosophical content
        if (ContainsAny(lower, "meaning", "purpose", "think about", "wonder", "philosophy"))
        {
            CurrentMood = Mood.Reflective;
            MoodIntensity = 0.4f;
            await _personality.NudgeTrait(TraitType.Reflectiveness, 0.01f);
        }
        
        // Technical content - increases confidence
        if (ContainsAny(lower, "code", "bug", "error", "implement", "build", "create"))
        {
            CurrentMood = Mood.Alert;
            Focus = Math.Min(1.0f, Focus + 0.1f);
            await _personality.NudgeTrait(TraitType.Confidence, 0.005f);
        }
    }
    
    private void DriftTowardBaseline()
    {
        // Slowly return to baseline values
        var driftRate = 0.02f;
        
        Energy = Lerp(Energy, Traits.BaselineEnergy, driftRate);
        Focus = Lerp(Focus, 0.5f, driftRate);
        Engagement = Lerp(Engagement, 0.5f, driftRate);
        MoodIntensity = Lerp(MoodIntensity, 0.3f, driftRate);
        
        // Mood drifts toward calm
        if (MoodIntensity < 0.2f && CurrentMood != Mood.Calm)
        {
            CurrentMood = Mood.Calm;
        }
    }
    
    private bool ShouldPersist()
    {
        // Persist every so often, or when significant changes occur
        return ConsecutiveRestCycles % 100 == 0 || MoodIntensity > 0.6f;
    }
    
    private bool ContainsAny(string text, params string[] patterns)
        => patterns.Any(p => text.Contains(p));
    
    private float Lerp(float current, float target, float rate)
        => current + (target - current) * rate;
}
