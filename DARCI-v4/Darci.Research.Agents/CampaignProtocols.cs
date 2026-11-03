#nullable enable

using Darci.Nodes;

namespace Darci.Research.Agents;

/// <summary>
/// Default pre-registered protocols for AUTO-DRAFTED campaigns (the sweep can't invent bespoke criteria).
/// Deliberately conservative: a sandbox build/dry-run for every domain, plus an external-research
/// corroboration for sensitive domains. The human still reviews and authorizes this design (and the
/// protocol critic falsifies it) before anything runs — auto-draft only automates the DRAFT step.
/// </summary>
public static class CampaignProtocols
{
    public static IReadOnlyList<ValidationStep> Default(KnowledgeDomain domain)
    {
        var steps = new List<ValidationStep>
        {
            new("sandbox", ValidationStepKind.SandboxTest, Capability.RunTests, NodeId.Coding,
                new SuccessCriteria("pass_rate", Comparator.GreaterOrEqual, 0.9), "sandbox build + test"),
        };

        if (domain == KnowledgeDomain.Sensitive)
            steps.Add(new ValidationStep("corroboration", ValidationStepKind.ExternalResearchCheck, Capability.AnswerKnowledge, NodeId.Knowledge,
                new SuccessCriteria("corroborations", Comparator.GreaterOrEqual, 2), "external literature corroboration"));

        return steps;
    }
}
