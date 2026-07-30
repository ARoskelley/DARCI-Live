#nullable enable

namespace Darci.Nodes;

/// <summary>
/// The routable capability verbs, as STRINGS (doc §5.1: namespaced `domain.action`). This replaces the
/// compiled-in <see cref="Capability"/> enum as the routing key — the C1 fix and the thing that lets an
/// external node declare a capability the core has never heard of. Same house style as
/// <see cref="PacketSlots"/>: string constants, not an enum.
/// </summary>
public static class Capabilities
{
    // coding
    public const string CodingWrite = "coding.write";
    public const string CodingTest = "coding.test";

    // engineering / cad
    public const string EngineeringGeometry = "engineering.geometry";
    public const string CadGenerate = "cad.generate";

    // knowledge (KGMA)
    public const string KnowledgeAnswer = "knowledge.answer";
    public const string KnowledgeGapFill = "knowledge.gapfill";

    // innovation
    public const string InnovationSynthesize = "innovation.synthesize";

    /// <summary>Every capability the built-in nodes provide. External nodes are NOT limited to this set.</summary>
    public static readonly string[] BuiltIn =
    {
        CodingWrite, CodingTest, EngineeringGeometry, CadGenerate,
        KnowledgeAnswer, KnowledgeGapFill, InnovationSynthesize,
    };
}

/// <summary>Canonical node ids (doc §5.1 `node_id`: reverse-domain-ish, unique, immutable across versions).</summary>
public static class NodeKeys
{
    public const string Orchestrator = "darci.orchestrator";
    public const string Living = "darci.living";
    public const string Coding = "darci.coding";
    public const string Engineering = "darci.engineering";
    public const string Knowledge = "darci.knowledge";
    public const string Cad = "darci.cad";
    public const string Innovation = "darci.innovation";
}

/// <summary>
/// Validation + the TRANSITIONAL bridge between the legacy <see cref="Capability"/>/<see cref="NodeId"/>
/// enums and their canonical strings. The bridge exists so SU1–SU5 can land without a 200-call-site
/// big-bang rewrite; SU6 converts the call sites and retires the enums (F3).
/// </summary>
public static class CapabilityKey
{
    /// <summary>
    /// A capability name must be namespaced `domain.action` (doc §5.1): at least two dot-separated segments,
    /// each starting with a lowercase letter and containing only lowercase letters, digits, or underscores.
    /// Rejecting loosely-named capabilities at registration keeps the router table readable and collision-free.
    /// </summary>
    public static bool IsValidName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        var segments = name.Split('.');
        if (segments.Length < 2) return false;
        foreach (var s in segments)
        {
            if (s.Length == 0) return false;
            if (s[0] is < 'a' or > 'z') return false;
            foreach (var ch in s)
                if (ch is not (>= 'a' and <= 'z') and not (>= '0' and <= '9') and not '_') return false;
        }
        return true;
    }

    // ── legacy bridge (retired in SU6) ──

    /// <summary>Canonical string for a legacy <see cref="Capability"/> enum member.</summary>
    public static string From(Capability capability) => capability switch
    {
        Capability.WriteCode => Capabilities.CodingWrite,
        Capability.RunTests => Capabilities.CodingTest,
        Capability.DesignGeometry => Capabilities.EngineeringGeometry,
        Capability.GenerateCad => Capabilities.CadGenerate,
        Capability.AnswerKnowledge => Capabilities.KnowledgeAnswer,
        Capability.FillKnowledgeGap => Capabilities.KnowledgeGapFill,
        Capability.Innovate => Capabilities.InnovationSynthesize,
        _ => throw new ArgumentOutOfRangeException(nameof(capability), capability, "Unmapped legacy capability."),
    };

    /// <summary>Legacy enum for a canonical string, or null if the string is an external capability with no
    /// legacy equivalent (which is the normal case for collaborator nodes).</summary>
    public static Capability? ToLegacy(string? name) => name switch
    {
        Capabilities.CodingWrite => Capability.WriteCode,
        Capabilities.CodingTest => Capability.RunTests,
        Capabilities.EngineeringGeometry => Capability.DesignGeometry,
        Capabilities.CadGenerate => Capability.GenerateCad,
        Capabilities.KnowledgeAnswer => Capability.AnswerKnowledge,
        Capabilities.KnowledgeGapFill => Capability.FillKnowledgeGap,
        Capabilities.InnovationSynthesize => Capability.Innovate,
        _ => null,
    };

    /// <summary>Canonical node-id string for a legacy <see cref="NodeId"/>.</summary>
    public static string From(NodeId node) => node switch
    {
        NodeId.Orchestrator => NodeKeys.Orchestrator,
        NodeId.Living => NodeKeys.Living,
        NodeId.Coding => NodeKeys.Coding,
        NodeId.Engineering => NodeKeys.Engineering,
        NodeId.Knowledge => NodeKeys.Knowledge,
        NodeId.Cad => NodeKeys.Cad,
        NodeId.Innovation => NodeKeys.Innovation,
        _ => throw new ArgumentOutOfRangeException(nameof(node), node, "Unmapped legacy node id."),
    };

    /// <summary>Legacy <see cref="NodeId"/> for a canonical node-id string, or null for an external node.</summary>
    public static NodeId? ToLegacyNode(string? nodeId) => nodeId switch
    {
        NodeKeys.Orchestrator => NodeId.Orchestrator,
        NodeKeys.Living => NodeId.Living,
        NodeKeys.Coding => NodeId.Coding,
        NodeKeys.Engineering => NodeId.Engineering,
        NodeKeys.Knowledge => NodeId.Knowledge,
        NodeKeys.Cad => NodeId.Cad,
        NodeKeys.Innovation => NodeId.Innovation,
        _ => null,
    };
}
