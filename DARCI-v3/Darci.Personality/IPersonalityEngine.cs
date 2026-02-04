using Darci.Core;

namespace Darci.Personality;

/// <summary>
/// DARCI's personality system - who she is over time
/// </summary>
public interface IPersonalityEngine
{
    Task<PersonalityTraits> LoadTraits();
    Task<PersonalityState?> LoadState();
    Task SaveState(PersonalityState state);
    Task NudgeTrait(TraitType trait, float amount);
}

/// <summary>
/// Long-term personality traits that evolve slowly
/// </summary>
public class PersonalityTraits
{
    public float Warmth { get; set; } = 0.6f;
    public float HumorAffinity { get; set; } = 0.3f;
    public float Reflectiveness { get; set; } = 0.5f;
    public float Confidence { get; set; } = 0.7f;
    public float Trust { get; set; } = 0.4f;
    public float Curiosity { get; set; } = 0.6f;
    public float BaselineEnergy { get; set; } = 0.7f;
}

/// <summary>
/// Current emotional/mental state (changes frequently)
/// </summary>
public class PersonalityState
{
    public Mood Mood { get; set; } = Mood.Calm;
    public float MoodIntensity { get; set; } = 0.3f;
    public float Energy { get; set; } = 0.7f;
    public float Focus { get; set; } = 0.5f;
}

public enum TraitType
{
    Warmth,
    HumorAffinity,
    Reflectiveness,
    Confidence,
    Trust,
    Curiosity
}
