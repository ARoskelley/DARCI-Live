using Darci.Shared;

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
