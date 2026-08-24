# DARCI Node Packet Protocol — Design Doc

**Status:** PARTIALLY IMPLEMENTED — Phase 0 is built and committed. See §6 for which decisions are
resolved and which remain open. Superseded on transport by the Dispatch Brief of 2026-08-23.

> **Reading note (2026-08-23):** this document was drafted 2026-06-26 as a pre-implementation
> design. Phase 0 has since shipped: `Darci.Nodes` provides `NodePacket`, `INodePacketStore` /
> `SqliteNodePacketStore`, `NodeWatchdog`, and the `NodeState` machine, registered in
> `Darci.Api/Program.cs` and covered by `Darci.Nodes.Tests`. What has *not* shipped is routing —
> there is no `INodeRouter` in the codebase. Treat §1–§5 as accurate background and §6 as the
> live decision record below.
**Author:** drafted from a codebase audit on 2026-06-26.
**Scope:** A shared envelope + addressing/routing layer for passing work between DARCI's specialized "nodes" (living loop, coding, engineering, KG/deep-research), plus a rigid redesign of the KG/DR node.

---

## 1. Current-state audit — how data actually moves today

DARCI today is **four subsystems, each with its own bespoke data carrier, joined by direct method calls.** There is no shared envelope and no append-only cross-node log. Each boundary does an ad-hoc type conversion. Confidence, status, and decision history are re-invented per subsystem.

### 1.1 The living loop (`Darci.Core`)

`Darci.cs` is a `BackgroundService` running `Perceive → Feel → Decide → Act → Reflect → Record`:

- `Awareness.Perceive()` → `Perception` ([Darci.Core/Awareness.cs](../DARCI-v4/Darci.Core/Awareness.cs))
- `Decision.Decide(...)` → a single `DarciAction` ([Darci.Core/Decision.cs](../DARCI-v4/Darci.Core/Decision.cs))
- `Act(action)` is a **`switch` over `ActionType`** → `Outcome` ([Darci.cs:362](../DARCI-v4/Darci.Core/Darci.cs#L362))
- Reward computed, experience tuple stored in the RL ring buffer.

**Carriers:** `DarciAction` (the request) and `Outcome` (the result). These never leave the loop. The action set is: `Reply, Notify, Think, Remember, Recall, Consolidate, Research, WorkOnGoal, CreateGoal, ReadFile, WriteFile, GenerateCAD, Engineer, Engineering, Rest, Observe`.

> **Critical finding:** there is **no `ActionType.Code`**. The coding subsystem is *unreachable from the living loop.* DARCI cannot decide to write code as part of her own cognition — coding only runs when something POSTs to the REST API (which is exactly how our diagnostic tests drove it). This is the single biggest structural gap relative to Tinman's prosthetic example, where engineering must be able to "turn to coding."

### 1.2 The coding silo (`Darci.Coding`)

A self-contained REST surface wired in [Program.cs:1061–1252](../DARCI-v4/Darci.Api/Program.cs#L1061). `POST /coding/tasks/{id}/run` calls `CodingAgentLoop.StartLoop(id)` ([CodingAgentLoop.cs:59](../DARCI-v4/Darci.Coding/CodingAgentLoop.cs#L59)), which runs fire-and-forget in a `ConcurrentDictionary<string, Task>`.

**Carrier:** `CodingTaskRecord` ([CodingModels.cs:99](../DARCI-v4/Darci.Coding/CodingModels.cs#L99)). This is the closest thing DARCI has to a "packet" today — it already accumulates state across a run:
- `Status` — **a free-text string** (`"planning" | "in_progress" | "completed" | "verification-failed" | "blocked" | "no-op" | "failed"`), not an enum, no state machine.
- `Plan` — JSON-serialized `CodingPlanStep[]`, each with its own `Status` + `ConfidenceScore` + `ConfidenceNote`.
- `RoadblockResearch` — appended research text (the KG/DR handoff result, stuffed back in as a string).
- `ConfidenceScore` / `ConfidenceNote` — self-reported by the LLM (-1 sentinel = unset).
- `VerificationResult` — last test output.

The coding node calls **out** to the research node via `IRoadblockDetector.CheckAndResearchAsync` → `IDeepResearchOrchestrator.RunDeepResearchAsync` ([RoadblockDetector.cs:79](../DARCI-v4/Darci.Coding/RoadblockDetector.cs#L79)). The result comes back as a **string** and is concatenated into `RoadblockResearch`. That is the *only* existing cross-node data handoff in the coding path, and it is untyped.

### 1.3 Engineering (`Darci.Engineering`)

- `EngineeringGoalDetector.Detect(title, desc)` — keyword match → `EngineeringGoalSpec?` ([EngineeringGoalDetector.cs:37](../DARCI-v4/Darci.Engineering/EngineeringGoalDetector.cs#L37)).
- `EngineeringOrchestrator.RunAsync(spec)` → `EngineeringResult` ([EngineeringOrchestrator.cs:44](../DARCI-v4/Darci.Engineering/EngineeringOrchestrator.cs#L44)) — a neural action loop against the geometry workbench.
- Invoked from `Darci.DoNeuralEngineeringWork` ([Darci.cs:738](../DARCI-v4/Darci.Core/Darci.cs#L738)).

**Notably, engineering already does a node-to-node handoff** — but hardcoded inline: before running the workbench, it calls `_research.RunResearchAsync(..., LearnOnly)` then `_constraintExtractor.ExtractAsync(...)` and merges constraints into the spec ([Darci.cs:757–784](../DARCI-v4/Darci.Core/Darci.cs#L757)). This is *exactly* the "engineering turns to research" pattern Tinman describes — proving the pattern is real and wanted — but it is a fixed call sequence, not a routable packet. There is no way for it to instead "turn to coding."

**Carriers:** `EngineeringGoalSpec` (request, lives in `Darci.Shared`), `EngineeringResult` (result).

### 1.4 The KG / deep-research path (`Darci.Research.Agents`)

This is the most mature subsystem and **already implements ~70% of Tinman's "KG/DR node" vision.** `DeepResearchOrchestrator.RunResearchAsync` ([DeepResearchOrchestrator.cs:46](../DARCI-v4/Darci.Research.Agents/DeepResearchOrchestrator.cs#L46)) runs:

1. **Phase 1 — Knowledge Assessment** (`KnowledgeAssessor.AssessAsync`, [KnowledgeAssessor.cs:41](../DARCI-v4/Darci.Research.Agents/KnowledgeAssessor.cs#L41)): consults the KG (`SearchEntitiesAsync`), consults the confidence tracker (`SynthesizeAsync`), and for the ambiguous confidence band fires an **LLM gap classifier**. Emits `DispatchDecision` ∈ {`SkipAgents`, `RunGapFill`, `RunAgents`}. **This is already Tinman's "admin agent consults KG + review agent decides."**
2. **Phase 2 — Agent dispatch**: decomposes into sub-questions, routes each to web / pubmed / graph / reasoning agents (`SelectAgentTypeAsync`), runs them in parallel with a 90s budget.
3. **Gap-fill pass**: if < half the reports clear the quality bar, generate follow-up questions and run more agents ([DeepResearchOrchestrator.cs:302](../DARCI-v4/Darci.Research.Agents/DeepResearchOrchestrator.cs#L302)). **This is already "if enough fails happen, deploy more."**
4. **Quality gate** (≥0.35 confidence) → **synthesis** → ingestion back into the KG (`IngestMemoryAsync`) and the confidence tracker (`AddClaimAsync`).

**Carrier:** `ResearchOutcome` ([Models/ResearchOutcome.cs](../DARCI-v4/Darci.Research.Agents/Models/ResearchOutcome.cs)) — `IsSuccess, FinalAnswer, Confidence, Citations[], IsUncertain`.

**What's missing vs Tinman's spec:** (a) no **output review agent** that re-checks the synthesis *against the original request* before returning; (b) no **compiler agent** that cuts fluff into a rigid format — the diagnostic runs showed the output is a rambling prose blob ("check your SDK version…"); (c) escalation is confidence-threshold-based, not "N consecutive fails → escalate"; (d) the **input/output contract is loose** — it takes a `string question` and returns a prose `FinalAnswer`.

### 1.5 What the node-packet model replaces

| Today | Replaced by |
|---|---|
| `DarciAction`/`Outcome`, `CodingTaskRecord`, `EngineeringGoalSpec`/`EngineeringResult`, `ResearchOutcome` as **four disjoint carriers** | One `NodePacket` envelope that travels across boundaries, with typed per-node payload slots |
| String concatenation of research into `RoadblockResearch` | A typed `NodeContribution` appended to the packet log |
| Free-text `Status` string + scattered per-step status | One `NodeState` enum + an append-only `NodeLogEntry[]` decision/confidence/outcome log |
| Hardcoded inline call sequences (eng→research) | Addressed packet hops resolved by a router |
| Confidence re-invented per subsystem (`ConfidenceScore`, `Confidence`, `GraphConfidence`, RL reward) | One `Confidence` value object carried on each log entry |

**What it does *not* replace (deliberately):** the internal mechanics of each node — `CodingAgentLoop`'s retry loop, the engineering neural loop, the research agent fan-out. The packet is the *interface between* nodes, not a rewrite of their guts.

---

## 2. The Node Packet protocol

### 2.1 Core idea

A **packet** is an envelope created when a goal enters the system. It carries: the original intent, a progressively-disclosed payload (each node tacks on what the next node needs), and an **append-only log** of every decision, confidence, and success/failure. Nodes are addressed; a node only acts on a packet routed to it, does its work, appends its contribution, and either returns the packet to the sender or forwards a *slice* of it to another node.

### 2.2 C# type sketch

```csharp
namespace Darci.Nodes;

// ── Addressing ──────────────────────────────────────────────
public enum NodeId { Orchestrator, Living, Coding, Engineering, Knowledge /* KG/DR */, Cad }

// A capability a node advertises; routing can be by capability OR explicit NodeId.
public enum Capability { WriteCode, RunTests, DesignGeometry, GenerateCad, AnswerKnowledge, FillKnowledgeGap }

// ── Lifecycle state machine (fixes task orphaning, see §4) ──
public enum NodeState
{
    Created, Routed, Accepted, Working, AwaitingDependency, // blocked on another node
    Succeeded, Failed, Aborted                              // terminal
}

// ── Confidence as one shared value object ───────────────────
public readonly record struct Confidence(double Score, string? Note)   // Score in [0,1], -1 = unassessed
{
    public bool IsLow => Score >= 0 && Score < 0.4;
}

// ── Append-only log entry — the "log as they make decisions" ─
public sealed record NodeLogEntry(
    NodeId Node,
    DateTime At,
    NodeState StateAfter,
    string Decision,          // human-readable: "wrote DammValidator.cs", "escalated: CS0101 not a knowledge gap"
    Confidence Confidence,
    bool? Success,            // null while in-flight; true/false when the step resolves
    string? Error = null,
    IReadOnlyList<string>? Artifacts = null);  // files/paths this entry is authoritative for

// ── Progressive-disclosure payload ──────────────────────────
// Each node reads only the slots it needs and may add/extend slots for downstream nodes.
public sealed record PacketPayload
{
    public string Intent { get; init; } = "";                 // original goal, never mutated
    public string? SuccessCriteria { get; init; }
    public IReadOnlyDictionary<string, string> Slots { get; init; } // typed-by-convention: "workspaceId", "engineeringSpecJson", "knowledgeFindings", ...
        = new Dictionary<string, string>();
}

// ── The envelope ────────────────────────────────────────────
public sealed record NodePacket
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string CorrelationId { get; init; } = "";          // groups a packet + its spawned children
    public NodeId? Address { get; init; }                     // who should act next (null = back to orchestrator)
    public Capability? RequestedCapability { get; init; }     // alternative to explicit Address
    public NodeState State { get; init; } = NodeState.Created;
    public PacketPayload Payload { get; init; } = new();
    public IReadOnlyList<NodeLogEntry> Log { get; init; } = Array.Empty<NodeLogEntry>();
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; init; } = DateTime.UtcNow;
    public DateTime? LeaseExpiresAt { get; init; }            // watchdog: a node holds a lease while Working
}

// ── The node contract every node implements ─────────────────
public interface INode
{
    NodeId Id { get; }
    IReadOnlySet<Capability> Capabilities { get; }
    // Acts on a packet addressed to it. Returns the packet with State advanced + a log entry appended.
    // May set Address/RequestedCapability to forward a SLICE to another node.
    Task<NodePacket> HandleAsync(NodePacket packet, CancellationToken ct);
}

// ── The router/orchestrator ─────────────────────────────────
public interface INodeRouter
{
    Task<NodePacket> DispatchAsync(NodePacket packet, CancellationToken ct);  // resolve Address/Capability → node, enforce lease/watchdog
}
```

### 2.3 The progressive-disclosure / "tack on what's needed" mechanism

- `Payload.Intent` and `SuccessCriteria` are **immutable** — set at creation, never rewritten, so every node sees the true original goal (avoids the goal-drift we'd otherwise get).
- `Payload.Slots` is the extensible area. A node **reads only the slots it needs** and **adds slots for downstream consumers**. Example for the prosthetic chain:
  - Living/Orchestrator creates: `Intent="design forearm prosthetic socket"`.
  - Engineering adds: `Slots["engineeringSpecJson"]`, `Slots["targetHardware"]="myoelectric sensor X"`.
  - Coding reads `targetHardware`, adds `Slots["workspaceId"]`, and when stuck sets `RequestedCapability=FillKnowledgeGap` with `Slots["failureContext"]`.
  - Knowledge node reads `failureContext` + `Intent`, adds `Slots["knowledgeFindings"]` (structured, see §3) and returns.
- **Passing a slice, not the whole packet:** when a node forwards, it builds a *child packet* (`CorrelationId` = parent's) carrying only the slots the target needs + the immutable `Intent`. The parent packet stays in `AwaitingDependency`. When the child reaches a terminal state, the router merges the child's findings slot back into the parent and flips it to `Working`. This keeps each node's prompt/context small (directly relevant to the context-bloat we already fight in `CodingAgentLoop`'s `MaxStepPromptContextFiles=3`).

### 2.4 Addressing & routing

- **Explicit address** (`Address = NodeId.Knowledge`) for known hops — matches Tinman's "addressed to them."
- **Capability request** (`RequestedCapability = FillKnowledgeGap`) when the sender knows *what it needs* but not *who* provides it — the router resolves to a node. Recommended default; explicit address is the override.
- A node **ignores any packet not addressed to it / not matching its capability** — enforced centrally by `INodeRouter`, so nodes don't each re-implement the check.

---

## 3. The KG/DR node redesign — rigid, self-orchestrated

Tinman's pipeline, mapped onto what exists. The good news from the audit: **most stages already exist** inside `DeepResearchOrchestrator`; the work is to (a) make them a rigid, named pipeline behind a strict contract, and (b) add the two missing agents (output review + compiler).

```
KnowledgeNode.HandleAsync(packet)               // strict entry — packet addressed to NodeId.Knowledge
  └─ KnowledgeRequest (parsed from packet slots)
       ├─ 1. Admin/Intake     → normalize request, detect domain         [exists: DetectDomain]
       ├─ 2. KG Consult       → KnowledgeAssessor.AssessAsync            [EXISTS — KG + confidence + LLM gap classifier]
       ├─ 3. Review Agent     → does KG result satisfy the request?      [PARTIAL — assessor decides dispatch, but does
       │                          (Ollama yes/no against the request)      not re-validate the *final* answer]
       ├─ 4. Escalate on N fails → deploy deep-research agents           [EXISTS — agent fan-out + gap-fill pass,
       │                                                                    but threshold-based, not fail-count-based]
       ├─ 5. Compiler Agent   → cut fluff → rigid KnowledgeResponse      [MISSING — today returns prose FinalAnswer]
       ├─ 6. Output Review    → response answers Intent? else loop to 4  [MISSING]
       └─ 7. Return packet    → append KnowledgeResponse slot + log entry
```

### 3.1 Strict I/O contract

```csharp
public sealed record KnowledgeRequest(
    string Question,
    string Intent,                       // the packet's immutable goal, for the review agents
    string? FailureContext,              // e.g. coding node's error — lets the node gate (see §4.3)
    KnowledgeKind Kind);                 // FactLookup | GapFill | HowTo | CaseStudies

public enum KnowledgeKind { FactLookup, GapFill, HowTo, CaseStudies }

// Rigid, structured — NOT a prose blob. This is the compiler agent's job.
public sealed record KnowledgeResponse(
    bool Answered,
    Confidence Confidence,
    IReadOnlyList<string> Findings,          // atomic, deduped claims
    IReadOnlyList<string> ProposedPaths,     // concrete next steps (for HowTo / GapFill)
    IReadOnlyList<CaseStudy> Examples,       // for CaseStudies kind
    IReadOnlyList<ResearchCitation> Citations,
    string? Unmet);                          // what it could NOT answer — drives caller's next decision

public sealed record CaseStudy(string Summary, string? SourceRef, Confidence Confidence);
```

**Why rigid matters (evidence):** in both diagnostic runs the research output was unusable prose ("update your .NET SDK", "check disk space") because there was no compiler/review stage forcing the output to *answer the actual question*. A structured `KnowledgeResponse` with an explicit `Unmet` field lets the calling node make a *routing* decision instead of dumping a paragraph into the next LLM prompt.

### 3.2 Internal orchestration

The KG node gets its **own** mini-router (Tinman's "its own orchestration layer") so stages 3–6 can loop (review → escalate → recompile → review) up to a bounded retry count, fully inside the node, before it returns. The node is the unit of escalation; the outer protocol never sees the internal churn.

---

## 4. Reconciliation with the three tooling bugs

| Bug (from diagnostic runs) | Subsumed by re-architecture? | Recommendation |
|---|---|---|
| **File collision** — model emits `### FILE:` for context/duplicate paths, causing CS0101/CS0111 | **No.** Internal to the coding node's `PatchApplier`. | **Fix standalone now.** But add an `Artifacts` manifest to log entries (§2.2) so each node declares which paths it owns — makes the guard natural and auditable. |
| **Task orphaning** — `Status` stuck at `"in_progress"`, no running loop | **Yes — fully.** This is precisely a missing state machine. | **Let the re-architecture absorb it.** `NodeState` + `LeaseExpiresAt` watchdog in `INodeRouter` makes orphaning structurally impossible: a lease that expires → router forces `Aborted` + terminal log entry. Build this as the *first* slice (§5) since it's also the highest-value reliability fix. |
| **Research gating** — research fired on self-inflicted CS0101, returned generic advice | **Partially.** The gate becomes a *routing decision*. | **Hybrid.** The decision "is this a knowledge gap or a self-inflicted error?" belongs in the coding node *before* it sets `RequestedCapability=FillKnowledgeGap`. The classifier itself still needs building (cheap: regex on compiler error codes — `CS0101/CS0111/CS0246` = self-inflicted → re-plan locally; assertion/behavioral failure = candidate for knowledge routing). The `KnowledgeRequest.FailureContext` + `KnowledgeResponse.Unmet` fields then make the round-trip honest. |

**Net:** orphaning is absorbed (and should drive Phase 0). File collision stays a standalone fix. Research gating is half-absorbed (routing layer is the right home; classifier is new but trivial).

---

## 5. Phased roadmap — smallest first viable slice

**Phase 0 — Envelope + state machine, coding node only (no new behavior).**
Introduce `NodePacket`, `NodeState`, `NodeLogEntry`, `Confidence` in a new `Darci.Nodes` project. Make `CodingAgentLoop` *wrap* its `CodingTaskRecord` work in a packet: every status transition writes a `NodeLogEntry`; the run holds a lease. Add the watchdog. **Deliverable: task orphaning becomes impossible; we get an audit log for free.** No cross-node traffic yet. Lowest risk, highest reliability payoff. Proves the state machine.

**Phase 1 — One real handoff: coding → knowledge, via packet.**
Replace the current string-based `RoadblockDetector → RunDeepResearchAsync → RoadblockResearch` path with: coding node builds a child packet `RequestedCapability=FillKnowledgeGap` carrying `FailureContext`; `INodeRouter` dispatches to a thin `KnowledgeNode` adapter wrapping today's `DeepResearchOrchestrator`; the structured result merges back. **Add the research-gating classifier here.** Proves: addressing, slice-passing, child→parent merge, the gate. This is the minimal end-to-end proof of the protocol within the two nodes we understand best from testing.

**Phase 2 — Harden the KnowledgeNode into the rigid pipeline (§3).**
Add the compiler agent + output-review agent; convert prose `FinalAnswer` → structured `KnowledgeResponse`; add the internal mini-orchestrator with bounded review→escalate loop. Now the node honors a strict contract. Re-run the Damm/Levenshtein diagnostics to confirm research output is finally *actionable*.

**Phase 3 — Generalize to engineering + living loop.**
Give the living loop an `ActionType.Code` that emits a packet (closing the §1.1 gap), and make `EngineeringOrchestrator` consume/emit packets so its existing inline research call becomes a routed hop that *could instead* route to coding. This unlocks the full prosthetic chain: living → engineering → coding → knowledge, one protocol throughout.

Stop after each phase and evaluate — Phase 0+1 alone may move reliability enough to defer 2–3.

---

## 6. Decisions — resolution status

> **Resolved 2026-08-23** by the Dispatch Brief (DARCI × AI Society). Items 1 and 2 are settled;
> do not re-litigate. The rest remain open.
>
> **§6.1 Transport — RESOLVED: out-of-process.**
> The in-process recommendation below was written optimising for a single-developer core and
> predates the AI Society collaboration. Since the objective is that people other than Tinman
> write nodes, in-process means C#-only plus a core recompile per node — the exact failure the
> node-contract work exists to prevent. **Decision: out-of-process HTTP/REST first**, consistent
> with the Lizzy pattern. Keep the SQS-shaped seam for later.
>
> *Cascading consequence:* `Capability` (`Darci.Nodes/NodePrimitives.cs:26`) must become an **open
> set** — string identifiers plus a runtime registry, or a manifest-driven capability table. A C#
> `enum` is compile-time closed, so an out-of-process node cannot contribute a value to it.
> Reassess `NodeId` (:11) on the same grounds. This changes committed Phase 0 code and is cheaper
> now than after nodes exist.
>
> **§6.2 Does coding join the living loop? — RESOLVED: deferred past the semester.**
> There is no `ActionType.Code`, and there will not be one this semester. Nodes are
> **externally-triggered services**; the core does not autonomously route work to them. Contract
> docs must state this actual behaviour, not the intended one. The living-loop question reopens
> after the semester.
>
> **§6.3–§6.6 — still open.**
>
> Separately: the KG on `main` is SQLite-backed (`Darci.Memory.Graph.KnowledgeGraph`). Neo4j
> exists only on `origin/feat/node-packet-protocol`. Any contract rule of the form "nodes never
> touch the graph directly" must be labelled explicitly as **forward-looking**, because
> contributors cannot see the component it refers to.

### Original text (retained for context)

1. **Packet transport — in-process vs queue.** Recommend **in-process** object passed through `INodeRouter` for now (local-first, simplest), with the interface shaped so an SQS/`SqsRelayService`-backed transport can drop in later. Do you want queue-backed from day one (enables the cloud/distributed story but adds serialization + failure-mode surface)?

2. **Does coding join the living loop?** Today it's a REST silo unreachable from `Decide`. Phase 3 proposes `ActionType.Code`. Confirm you want DARCI to *autonomously* choose to code, vs coding staying an externally-triggered service.

3. **Addressing model.** Recommend **capability-based routing as default, explicit `NodeId` as override**. You said "addressed to them" — want strict explicit addressing only, or the hybrid?

4. **KG output rigidity.** Recommend the **structured `KnowledgeResponse`** (findings/paths/examples/unmet) over prose. Confirm — this is the biggest lever on research usefulness per the diagnostics.

5. **Packet persistence.** Recommend **SQLite-backed packets** (we already use SQLite throughout) so a crash mid-flight is recoverable and the watchdog survives restarts — directly reinforces the orphaning fix. Acceptable, or in-memory first?

6. **Confidence unification.** Recommend collapsing `ConfidenceScore` (coding), `Confidence` (research), `GraphConfidence` (assessment) into the one `Confidence` value object on log entries. This touches several files — want it in Phase 0 or deferred?
