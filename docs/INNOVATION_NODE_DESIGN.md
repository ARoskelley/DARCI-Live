# DARCI Innovation / Ideation Node — Design Doc

**Status:** DRAFT — design only, nothing implemented. Uncommitted, for Tinman's review.
**Author:** drafted 2026-06-27 from a codebase audit + prior-art research.
**Scope:** How to architect an innovation/ideation node that activates when *known* knowledge (KG + deep research) fails, synthesizes a candidate solution, vets it, gates it through the human, and learns from whether it actually worked.

---

## 0. One-paragraph summary

When the knowledge node (KGMA) exhausts KG + deep research and still can't answer, it escalates to an **Innovation capability** it orchestrates. Innovation works *with* KGMA — pulling from the KG, DB, and prior research — to synthesize a **candidate intersection/hypothesis**, runs it past a **structurally sycophancy-resistant critic** (internal vet), and only then routes a **proposal** to the **UI node** for human approval. The packet stays under KGMA's lease the whole time; the human wait is modeled as a **graceful pause**, not a held lease. On approval, the idea is written into KG/DB **tagged as innovated/unverified** (never as established fact), and the **actual coding outcome flows back** as evidence. The whole thing runs as a **progress-driven loop** (continue while confidence climbs or new info appears; stop on plateau; hard budget backstop) rather than a fixed number of passes.

---

## 0a. GOVERNING INVARIANT (the top-level principle)

> **Automatic processes may only APPEND EVIDENCE and move entries toward LESS trust. Every UPWARD crossing of a behavioral boundary — the `IsLow`/`IsGap` 0.4 line, `IsTrustedAsFact`, persistence of a *new* hypothesis, or extension of DARCI's capability surface — is a HUMAN-AUTHORED event.**

Everything in this doc is an instance of the same shape: **accumulate → package → propose → human event → state change.** Machines gather evidence and *propose*; only a human authorizes an upward step. Downward moves (demotion, retraction on failure) stay automatic and fast, because losing trust cheaply is safe; gaining it cheaply is not.

Enforced in code (Phase A): the innovated-knowledge store *rejects* any update that raises an entry's trust rank unless the accompanying ledger event kind is human-authored; automatic outcome processing only appends evidence, nudges within-cap score (ranking, never trust), and demotes on failure. See §4, §5, and the invariant test.

---

## 1. Where this sits in the current codebase

The node protocol from Phases 0–2 already gives us almost all the primitives. Innovation is an *extension*, not a new stack.

| Need | Already exists | File / type |
|---|---|---|
| Packet envelope + lifecycle | `NodePacket`, `NodeState` (`Created→Routed→Accepted→Working→AwaitingDependency→Succeeded/Failed/Aborted`), `NodeStateMachine` | [Darci.Nodes/NodePacket.cs](../DARCI-v4/Darci.Nodes/NodePacket.cs), [NodePrimitives.cs](../DARCI-v4/Darci.Nodes/NodePrimitives.cs) |
| Generic routing by node/capability | `INode`, `INodeRouter`, `NodeRouter` | [Darci.Nodes/INode.cs](../DARCI-v4/Darci.Nodes/INode.cs), [NodeRouter.cs](../DARCI-v4/Darci.Nodes/NodeRouter.cs) |
| The KGMA node + its rigid pipeline | `KnowledgeNode`, `KnowledgePipeline` (admin/KG → review#1 → escalate DR → compile → review#2) | [Darci.Research.Agents/KnowledgeNode.cs](../DARCI-v4/Darci.Research.Agents/KnowledgeNode.cs), [KnowledgePipeline.cs](../DARCI-v4/Darci.Research.Agents/KnowledgePipeline.cs) |
| Separate review agent (the precedent for a critic) | `IKnowledgeReviewAgent` / `OllamaKnowledgeReviewAgent` (falsification-ish `{Fulfills, MissingAspects}`) | [KnowledgeReviewAgent.cs](../DARCI-v4/Darci.Research.Agents/KnowledgeReviewAgent.cs) |
| Structuring + fluff-cut | `IKnowledgeCompilerAgent` | [KnowledgeCompilerAgent.cs](../DARCI-v4/Darci.Research.Agents/KnowledgeCompilerAgent.cs) |
| Admin/KG consult | `IKnowledgeAssessor` / `KnowledgeAssessor` | [KnowledgeAssessor.cs](../DARCI-v4/Darci.Research.Agents/KnowledgeAssessor.cs) |
| Deep research engine | `IDeepResearchOrchestrator` (agent fan-out, gap-fill, synthesis, KG ingest) | [DeepResearchOrchestrator.cs](../DARCI-v4/Darci.Research.Agents/DeepResearchOrchestrator.cs) |
| "Can't answer" + actionability | `KnowledgeResponse.Gaps`, `GapRecord`, `IGapStore`, `GapHandler` (immediate vs deferred), `IGapGoalSink` | [Models/KnowledgeContracts.cs](../DARCI-v4/Darci.Research.Agents/Models/KnowledgeContracts.cs), [GapRecord.cs](../DARCI-v4/Darci.Nodes/GapRecord.cs), [GapHandler.cs](../DARCI-v4/Darci.Nodes/GapHandler.cs) |
| Unified confidence | `Confidence` (Score/Note, `IsAssessed`/`IsLow`/`IsGap`, `Unassessed`, `Of`) | [NodePrimitives.cs](../DARCI-v4/Darci.Nodes/NodePrimitives.cs) |
| KG + claim stores (where innovated knowledge lands) | `IKnowledgeGraph` (`UpsertEntityAsync`/`UpsertRelationAsync`/`IngestMemoryAsync`/`SemanticSearchAsync`), `IConfidenceTracker` (`AddClaimAsync`, `KnowledgeClaim` with `SourceType`/`SourceQuality`/`IsUncertain`) | [Darci.Memory.Graph](../DARCI-v4/Darci.Memory.Graph), [Darci.Memory.Confidence](../DARCI-v4/Darci.Memory.Confidence) |
| Orphan reaping / lease | `NodeWatchdog` (`SweepExpiredLeasesAsync`, `SweepStartupOrphansAsync`) | [NodeWatchdog.cs](../DARCI-v4/Darci.Nodes/NodeWatchdog.cs) |
| Outcome origin (coding success/failure) | `CodingAgentLoop` terminal status + `CodingNodeTracker` packet log | [Darci.Coding/CodingAgentLoop.cs](../DARCI-v4/Darci.Coding/CodingAgentLoop.cs) |

**The one genuinely new subsystem** is the innovation generator + its provenance/outcome bookkeeping. Everything else is reuse.

---

## 2. Prior art and what we actually take from each

(Researched 2026-06; full links in §11. Pulled for fit, not padding.)

- **Blackboard architecture / Hearsay-II** — independent "knowledge sources" contribute hypotheses to a shared space, with a controller scheduling *opportunistic* (not fixed-sequence) contributions. **Take:** the KG + DB + the packet log *are* the blackboard; KGMA is the controller; KG-consult, deep-research, and innovation are knowledge sources that incrementally raise a partial solution. This justifies the "loop of decisions, not N passes" framing directly.
- **Soar / ACT-R (cognitive architectures)** — Soar's **impasse → subgoal → chunk** cycle: when no known operator resolves the state, create a subgoal; when resolved, *chunk* the result into a reusable rule. **Take:** this is almost exactly Tinman's flow. Coding stuck = impasse; KGMA→innovation = subgoal; approved+validated solution written back to KG = chunking. The provenance/outcome loop is what makes "chunking" honest.
- **Generative Agents (Park 2023) — memory stream + reflection** — periodically synthesize higher-level *insights* from retrieved memories (scored by recency × importance × relevance) and write them back, recursively. **Take:** the innovation "synthesize an intersection" step is a reflection over retrieved KG/DB/research memories; and reflections are explicitly *derived* (not raw observation) — supporting a distinct provenance kind.
- **Reflexion (verbal self-critique + episodic memory)** — store outcomes of attempts as episodic lessons that condition future attempts. **Take:** the outcome-feedback loop (§5) — coding's pass/fail becomes an episodic record that updates the innovated entry and conditions future innovation.
- **Tree-/Graph-of-Thoughts** — branch candidate reasoning paths, evaluate, backtrack/merge. **Take:** the inner generate→evaluate cycle of the loop governor; candidates are nodes, the critic scores them, plateau = no better child.
- **FunSearch / AlphaEvolve** — LLM **generator** + **automated objective evaluator** + **program database**, evolving best candidates; *separation of generator and evaluator*, and an evaluator grounded in objective scores. **Take:** the single most important structural lesson — keep the proposer and the critic separate, and ground the critic in objective gates wherever possible (does it compile/verify? does the inference follow from cited facts?). Also the "feed the best back" loop.
- **Quality-Diversity / MAP-Elites / Novelty search** — optimize for a *diverse archive* of high-fitness candidates, using behavioral novelty as a signal. **Take:** (a) generate a small *diverse* set of candidate intersections per cycle rather than one, (b) use **novelty/embedding-distance between successive proposals as the plateau detector** for the loop governor — a repeated/near-identical proposal means we've stopped exploring.
- **KG reasoning / link prediction** — proposing a plausible new edge/path between entities, scored by a plausibility model. **Take:** frame a synthesized "intersection" as a **candidate KG relation/path** with a plausibility score; insertion is gated; the score seeds the (capped) innovated confidence.

---

## 3. The node's contract (in / out)

Innovation is an `INode` (new `NodeId.Innovation`, capability `Capability.Innovate`). KGMA invokes it via the router; **the parent packet stays addressed to / leased by KGMA** (consideration #7).

### Input — `InnovationRequest`
```csharp
public sealed record InnovationRequest(
    string Question,            // the unmet question
    string Intent,              // originating goal (e.g. the coding task)
    string? FailureContext,     // why coding/KG/DR failed
    IReadOnlyList<string> Gaps, // the residual gaps from the KnowledgeResponse that triggered escalation
    IReadOnlyList<string> KnownFacts, // KG/DR facts already gathered (the substrate to recombine)
    InnovationBudget Budget);   // max cycles / wall-clock / token budget (loop backstop)
```
This is exactly what KGMA already has after `KnowledgePipeline` fails: `KnowledgeResponse.Gaps`, the assessment's supporting claims, and the deep-research findings.

### Output — `InnovationProposal` (structured, never prose — same philosophy as `KnowledgeResponse`)
```csharp
public enum ProposalStatus { Proposed, VettedInternally, AwaitingHuman, Approved, Rejected, Applied, Validated, Refuted, Unsolvable }

public sealed record InnovationProposal
{
    public ProposalStatus Status { get; init; }
    public string Hypothesis { get; init; } = "";          // the synthesized intersection / solution
    public IReadOnlyList<ReasoningLink> Reasoning { get; init; } = [];  // each step + the KG facts it cites
    public IReadOnlyList<string> Assumptions { get; init; } = [];
    public CriticVerdict Critic { get; init; }             // falsification-oriented internal review
    public Confidence Confidence { get; init; } = Confidence.Unassessed; // CAPPED until validated (see §4)
    public Provenance Provenance { get; init; } = Provenance.Innovated;
    public IReadOnlyList<string> RequiredExternalInputs { get; init; } = []; // "needs experiment/data X" (→ Gaps, §6)
    public IReadOnlyList<ProofOfConcept> Evidence { get; init; } = [];  // PoC/test evidence (resolved decision #4)
    public string CorrelationId { get; init; } = "";       // ties proposal → originating packet → outcome
}

public sealed record ReasoningLink(string Inference, IReadOnlyList<string> CitedFactIds);
public sealed record ProofOfConcept(string Environment, bool Passed, string Summary, string? Artifact);
```
- **Innovation brings evidence, it doesn't just assert (resolved decision #4).** Before a proposal reaches the human, the node can **communicate with environments** (the coding node — later engineering) to build and run a **proof-of-concept**, attaching the result as `Evidence`. A proposal with a passing PoC is far stronger than a bare claim; see §8 for how this doubles as the strongest anti-sycophancy gate. This is done **packet-routed and KGMA-orchestrated** (a child packet to the coding node under KGMA's lease) — noted coupling, but the objectivity is worth it.
- A confident, vetted, evidence-backed, human-approved proposal → written to the innovated store (provenance-tagged, §4) and the packet returns to coding.
- A proposal the node *cannot* stand behind → `Status = Unsolvable`, `RequiredExternalInputs` populated → becomes a `GapRecord` (§6). **Honest "I can't" is a first-class output.**

---

## 4. Provenance + confidence model (consideration #1)

**Problem:** innovated knowledge must never be looked up later as established fact.

**Design:** add a `Provenance` dimension orthogonal to `Confidence`, carried on every KG entry / claim that innovation writes.

```csharp
public enum Provenance {
    Verified,               // ground truth / established fact — trusted
    Researched,             // from deep research — trusted
    Innovated,              // freshly synthesized hypothesis, untested (= "Hypothesis" stage) — capped
    UnderTest,              // being empirically exercised — capped
    ProvisionallyValidated, // N successful real-world uses; bounded automatic uplift — capped (Phase A ceiling)
    HumanApproved,          // human signed off (part of the above-cap promotion pathway)
    Unverified,             // low-trust / unknown — capped
    Retracted,              // failed empirically — excluded from lookups
}
```
- `KnowledgeClaim.SourceType` already exists (`"llm"`, `"research"`) — bridge it to `Provenance`, and add a dedicated **`innovated_knowledge`** table (SQLite, alongside `node_gaps`/`node_packets`) with full audit: hypothesis, cited fact ids, proposal/approval timestamps, approving user, `CorrelationId`, current `Confidence`, `Provenance`, success/failure counts, and an **append-only revision log** so an entry can be edited/reverted if it later fails (Tinman's explicit requirement).
- **Hard confidence cap:** any *capped* provenance (`Innovated`, `UnderTest`, `ProvisionallyValidated`, `Unverified`) is clamped so it is **always `Confidence.IsLow`**; `Retracted` → excluded. Implemented as `ProvenancePolicy.InnovatedCap = 0.35` — Tinman's bound is ≤0.4, and since `IsLow` tests strictly `< 0.4`, the cap sits at 0.35 so a hypothesis is *always* low and can never present as fact. This guarantees `KnowledgeAssessor` never treats a hypothesis as sufficient on its own.
- **Lookup behavior / down-weighting:** the policy `ProvenancePolicy.IsTrustedAsFact` (`Verified`/`Researched` for now) + `Clamp` is the *single enforcement point*. Innovated entries surface only as *candidate hypotheses with a health warning*, never as answers.

This reuses the unified `Confidence` (score + gap detection) and adds the *kind* axis it was missing.

### 4a. Promotion pathway — hypotheses earn their way up via HUMAN-AUTHORED events (hybrid; refined per Fable)

A permanent cap would bury genuinely impactful innovations (medical/engineering); but per the governing invariant (§0a) trust is never granted automatically. So the lifecycle is re-semanticized **by ENTRY TRIGGER** — the stage names describe *what human authorization has occurred*, not *how many auto-successes accumulated*:

```
Innovated (≤0.35, always IsLow)         evidence accumulates automatically as a JUSTIFICATION PACKAGE
   │  human authorizes a ValidationCampaign  ── HUMAN EVENT (files via ProposalStore/UI gate)
   ▼
UnderTest (still capped)                a human-authorized campaign is ACTIVE
   │  campaign passes pre-registered criteria + human accepts mid-tier  ── HUMAN EVENT
   ▼
ProvisionallyValidated (cap LIFTS to a mid tier: 0.6 general / 0.45 sensitive)
   │  human confirms full promotion (both touches MANDATORY for medical/eng)  ── HUMAN EVENT
   ▼
HumanApproved (trusted tier — IsTrustedAsFact; always human, both domains)
```
- **Stage = human-authorization state, not an auto-counter.** `UnderTest` means *a human authorized a campaign*; `ProvisionallyValidated` means *a campaign passed and a human accepted the mid tier*; `HumanApproved` means *a human confirmed trust*. No success count ever moves an entry up a stage.
- **What accumulates automatically is only EVIDENCE** (§5) — a growing, deduped justification package attached to the entry — plus a within-cap score nudge used *only for ranking* (still `IsLow`, never trust).
- **Per-stage caps** live in `ProvenancePolicy.Clamp` (shape designed now, wired when campaigns land): `Innovated/UnderTest → 0.35`; `ProvisionallyValidated → 0.6` general / `0.45` sensitive; `HumanApproved → uncapped/trusted`.
- **BUILD-NOW vs DESIGN-ONLY:** Phase A builds only the *bottom* of this ladder — `Innovated` entries, automatic evidence accumulation, within-cap ranking score, and automatic *downward* demotion on failure. Everything that raises a stage (campaign authorization, mid-tier acceptance, full promotion) is **design-only** (§14) and lands with the ProposalStore/UI node.

---

## 5. The outcome-feedback loop (consideration #2 — the thing that makes it learn)

Reality — not the model — supplies evidence. But per §0a, that evidence only **accumulates and demotes**; it never promotes.

```
coding run under correlation-root R consumes innovated entry E (a CONSUMPTION LINK E→R was recorded when served)
  → coding reaches terminal status  → OutcomeFeedback {CorrelationId=R, success=bool, evidence}
  → sink matches E via its CONSUMPTION LINKS (not E's originating correlation):
        success  → append SUCCESS evidence (deduped by distinct root R), nudge within-cap score (ranking only),
                   provenance UNCHANGED
        failure  → append FAILURE evidence, DEMOTE ONE STAGE (+ notify if human-promoted); full Retract only from
                   the bottom stage or on repeated/severe failure
```
- **Correlation-link fix (critical bug Fable caught).** The naive design matched outcomes on the entry's *originating* `CorrelationId`. That is doubly wrong: (a) **retries of the same task** share that correlation and would each count as an independent success (inflation), and (b) **independent future consumers** of the hypothesis have *different* correlations and would never be counted (starvation). Fix: when the assessor **serves** an innovated hypothesis into a packet, record a **consumption link** `entry_id → served correlation_root`. The sink matches outcomes **on consumption links**, and counts evidence by **DISTINCT correlation root** — so retries collapse to one and independent uses each count. This turns the ledger from *starved-and-inflated* into a real, deduped justification package. *(Phase A builds the link store, the deduped sink, and the recording API; the assessor-serving call-site lands with the assessor↔innovated-store integration.)*
- **Success bar (resolved decision #5): "works AND works well"** = a reasonably optimized program that completes the requested task (may span files/tests). Phase A maps coding terminal `completed` → success; anything else → failure. Auto-measuring "optimized" is a flagged follow-up (runtime/complexity metrics, reviewer pass).
- **Within-cap score is RANKING, not trust.** Under the cap everything is `IsLow`/`IsGap`; the score nudge (`min(0.35, …)` by distinct-success count) only orders candidates so a battle-tested hypothesis outranks a fresh one when the assessor surfaces them. (Reversible — we could instead order by ledger stats and hard-freeze the score.)
- **Failure is soft but automatic.** One-stage demotion + a UI-node notification for human-promoted entries (never blocks on the human); full `Retracted` only from the bottom stage or on repeated/severe failure. Down is cheap and fast; up is a human event.
- **Origin:** `CodingAgentLoop` emits `OutcomeFeedback` (best-effort) on terminal with its correlation root; the sink is a no-op unless a consumption link matches.

---

## 6. Honest "can't solve" (consideration #6)

Innovation must be able to conclude *"no synthesis of known information solves this; it requires external data/experiment X."* This maps cleanly onto the existing `Gaps` machinery:
- `InnovationProposal.Status = Unsolvable` with `RequiredExternalInputs = ["measured EMG latency for sensor X", ...]`.
- KGMA converts these into `GapRecord`s via the existing `GapHandler` — but a new disposition: not "immediate fill" (research already failed) and not silently deferred, rather a **needs-external-input** gap that surfaces to the user/goal system as "DARCI needs X to proceed."
- Critically, the loop governor (§7) must *prefer* an honest `Unsolvable` over a low-confidence fabricated solution once progress plateaus — the sycophancy-resistant critic (§8) is what enforces this.

---

## 7. The loop governor — progress-driven, not a fixed count (consideration #3)

KGMA runs the innovation sub-loop as: **propose → vet → (maybe) refine → repeat while making progress.**

```
state: bestConfidence = 0, lastProposalEmbedding = null, cycle = 0
loop:
  cycle++
  candidates = Innovation.Generate(request, priorCandidates)   // a small DIVERSE set (QD/novelty)
  scored     = Critic.Evaluate(candidates)                      // §8, objective-grounded
  best       = argmax(scored.confidence)
  progress   = (best.confidence > bestConfidence + ε)           // confidence climbed, OR
               || (best added new cited facts / new gaps closed) // new information
  novelty    = embeddingDistance(best, lastProposal) > δ        // not a repeat of last proposal
  STOP if:
     best.confidence ≥ acceptThreshold        → go to internal vet → human gate
     OR !progress for K consecutive cycles     → plateau → emit best-so-far OR Unsolvable
     OR !novelty (repeated/near-identical)      → plateau (QD signal)
     OR Budget exceeded (cycles/time/tokens)    → hard backstop → Unsolvable or best-so-far
  else: feed best + critic's failure-modes back into the next Generate (Reflexion/ToT style)
```
- **Progress = confidence climbing OR new information added** (new cited facts / closed gaps), per Tinman.
- **Plateau detectors:** (a) no confidence improvement over K cycles; (b) **novelty collapse** — successive proposals are near-duplicates by embedding distance (the QD/novelty-search idea, using the existing embedding service). Either ends the loop honestly rather than spinning.
- **Hard backstop:** `InnovationBudget` (max cycles, wall-clock, tokens) so it can never run away — and the packet's lease (held by KGMA) is the safety net if even that fails.

---

## 8. The sycophancy-resistant critic (Tinman's special focus)

Make honesty **structural**, not hoped-for. Five layers, strongest (objective) first:

1. **Separate generator from critic.** Distinct agents, distinct prompts, ideally distinct model instances/temperatures; **the proposer never judges its own proposal.** This mirrors the existing split between `IKnowledgeCompilerAgent` (produces) and `IKnowledgeReviewAgent` (judges) — we extend that precedent. (FunSearch/AlphaEvolve: generator ≠ evaluator.)
2. **Blind the critic to social cues.** The critic prompt presents the hypothesis **anonymously and neutrally** — "Evaluate this claim" — and **never** signals that it came from the user, that the user likes it, that it's already been approved, or the proposer's own confidence/enthusiasm. (Research note: social/identity cues are a primary sycophancy trigger; the *ELEPHANT* line of work on social sycophancy.)
3. **Force falsification-oriented, structured output** — not a yes/no or an open chain-of-thought (which research shows can *intensify* sycophancy by rationalizing the framing). The critic must return:
   ```csharp
   public sealed record CriticVerdict(
       IReadOnlyList<string> FailureModes,          // "this breaks if ..."
       IReadOnlyList<string> UnsupportedInferences, // steps NOT entailed by cited facts
       IReadOnlyList<string> RequiredAssumptions,
       IReadOnlyList<string> FalsificationTests,    // "what experiment/check would prove this wrong?"
       Confidence Confidence);                       // derived from the above, not vibes
   ```
4. **Ground critique in objective gates** wherever a gate exists, so subjective judgment is *not the only filter*:
   - **Entailment gate:** does each `ReasoningLink.Inference` actually follow from its `CitedFactIds` in the KG? (link/consistency check, not opinion.)
   - **Environment/PoC gate (LOCKED direction, resolved decision #4):** innovation **communicates with environments** — routes the proposed solution to the **coding node (later engineering) for a sandboxed build-and-test** — and attaches the result as `ProofOfConcept` evidence (§3) *before the human ever sees it*. This is the AlphaEvolve "automated evaluator" move and the single strongest anti-sycophancy lever short of deployment: a critic can be flattered, a compiler cannot. Kept packet-routed and under KGMA's lease; the added coupling is an accepted trade for objectivity. (Sandbox isolation, compute budget, and how much of a PoC to require are Phase-D/E implementation details.)
5. **Empirical outcome loop (§5) is the ultimate backstop** — reality doesn't care whether the answer pleased anyone. Even a sycophantic critic gets corrected once the coding outcome flows back and retracts a bad innovated entry.

**On abliteration (explicit note):** abliteration removes *refusal* directions, not sycophancy — wrong tool here, and weight surgery risks collateral damage. Recommended levers in order: (a) the structural mitigations above; (b) **model choice** for the critic (a model measured to be less sycophantic / better at critique); (c) only if needed, **light preference fine-tuning** (e.g. DPO on "reward truthful disagreement") — not raw weight editing. Add a small **sycophancy probe set** (claims the user "likes" but are wrong) to CI so we can *measure* the critic's honesty rather than assume it.

---

## 9. The human gate that survives the user being absent (consideration #4)

A blocking proposal must never hold a lease open forever (that re-introduces the orphaning Phase 0 killed).

**Design — model human approval as an external dependency with durable parking:**
1. When a vetted proposal needs approval, KGMA transitions the packet to **`NodeState.AwaitingDependency` and clears the lease** (`LeaseExpiresAt = null`). The periodic `NodeWatchdog.SweepExpiredLeasesAsync` only reaps packets whose lease is *non-null and expired*, so a null-lease parked packet is **not** reaped. ✅
2. The proposal is persisted to a durable **`ProposalStore`** (SQLite) keyed by `CorrelationId`, status `AwaitingHuman`, and routed to the **UI node** (a `NodeId.Ui` / `Capability.HumanApproval`) which surfaces it to Tinman asynchronously (queue, not a blocking call).
3. **Restart safety:** `NodeWatchdog.SweepStartupOrphansAsync` currently aborts *all* active packets on boot — that would wrongly kill a parked proposal. **Required change:** exclude `AwaitingDependency` packets that have a matching open `ProposalStore` entry (they're legitimately parked, not orphaned). Re-link on boot instead of abort.
4. On approval/rejection, the UI node writes the verdict to the `ProposalStore`; KGMA **re-leases** the packet (AwaitingDependency → Working) and continues: approve → write innovated entry + return to coding; reject → feed rejection reason back into the loop (§7) or conclude `Unsolvable`.
5. **Stale-proposal policy:** proposals carry a soft TTL; past it the UI node can nudge/notify (reusing the existing notification toolkit) but the packet stays parked — never auto-approved, never auto-aborted.

This keeps the packet **under KGMA's ownership** the whole time (#7) while making the wait safe and resumable.

---

## 10. KGMA-orchestrated sub-loop — the end-to-end flow

```
Coding stuck → routes FillKnowledgeGap packet to KGMA (KnowledgeNode)        [exists]
  KnowledgePipeline: KG consult → review#1 → deep research → compile → review#2 [exists]
  IF answered → return to coding                                              [exists]
  ELSE (not answered, gaps remain, blocking):                                 [NEW tier]
    KGMA opens an Innovation sub-loop (packet stays Working, leased by KGMA):
      ├─ Innovation.Generate (diverse candidate intersections from KG/DB/DR)  [NEW]
      ├─ Critic.Evaluate (blind, falsification, objective gates)              [NEW, extends review pattern]
      ├─ loop governor: progress? novelty? budget?  (§7)                      [NEW]
      ├─ best vetted proposal → pause for human (UI node, §9)                 [NEW]
      │     packet → AwaitingDependency (lease cleared), proposal queued
      │     ── on approval ──> write innovated entry (provenance-tagged, §4)
      │                        re-lease, return solution packet to coding
      │     ── on reject  ──> feed back into loop OR conclude Unsolvable
      └─ if Unsolvable → emit needs-external-input GapRecord (§6) back to coding/user
  AFTER coding runs with the innovated solution:
    OutcomeFeedback (§5) → appends DEDUPED evidence (matched via consumption links) / demotes on failure
                         → NEVER auto-promotes; upward moves are human-authored campaign events (§0a, §4a, §14)
```
**Ownership:** Innovation, Critic, and UI are capabilities KGMA drives via child packets (sharing `CorrelationId`); the *parent* packet's state/lease never leaves KGMA — exactly the node-packet model.

---

## 11. Phased roadmap (smallest viable slice first)

Ordered so each phase is independently valuable and de-risks the next; safety/foundations before sophistication.

- **Phase A — Provenance + evidence/outcome plumbing (BUILT, no innovation node yet).** `Provenance` + `ProvenancePolicy` (trust rank, always-`IsLow` cap, single enforcement point); the `innovated_knowledge` store (append-only ledger, edit/revert, cap enforced on write); the **ledger event kinds** with the invariant guard (upward trust change rejected unless human-authored); **consumption-link** store + the deduped `OutcomeFeedback` sink (append evidence, within-cap ranking nudge, one-stage demotion on failure — never auto-promote); `CodingAgentLoop` outcome emission. *Why first:* makes any future innovated knowledge safe and self-correcting; no generation logic; testable in isolation. **Highest-leverage safety work.**
- **Phase B — Innovation node, single-pass.** `NodeId.Innovation` + `Capability.Innovate`, separate proposer + blind falsification critic, structured `InnovationProposal` with honest `Unsolvable`/`Gaps`; proposals file to the ProposalStore/gap system. Proves generation + critique + provenance.
- **Phase C — Human gate + ProposalStore + privileged ledger kinds (§14).** UI node, `AwaitingDependency`-without-lease pause, `NodeWatchdog` carve-out; human events (`human-*`) become the only appenders of upward-trust ledger entries. Proves human-in-the-loop without orphaning.
- **Phase D — Progress-driven loop governor.** Multi-cycle generate/critique, diverse candidates, novelty/plateau detection, budget backstop, KGMA orchestration.
- **Phase E — ValidationCampaigns + objective gates (§14).** Pre-registered criteria, critic-falsifies-protocol, campaign steps as child packets (coding sandbox / deep research), mechanical verdict, two-touch promotion, per-stage cap lifts. `ToolingProposal` as data-only.

Stop after each phase and evaluate; Phase A alone is worth shipping regardless of whether innovation is ever built.

---

## 12. Decisions — RESOLVED (Tinman, 2026-06-27)

1. **Escalation seam:** ✓ Innovation tier sits **above** `KnowledgePipeline` (a thin KGMA orchestrator); the pipeline stays the "find known knowledge" engine.
2. **Confidence cap:** ✓ `≤ 0.4` (always `IsLow`, implemented at 0.35) accepted **with a promotion pathway** (§4a) — **refined to Tinman's HYBRID per Fable's memo:** promotion is entirely **human-authored** (governing invariant §0a); automatic processing only accumulates deduped evidence + a within-cap ranking score + downward demotion. The above-cap tiers, ValidationCampaigns, and privileged ledger kinds are **design-only** (§14).
3. **Critic model:** ✓ Structural-first on the local Ollama model; add a **CI sycophancy probe set** to *measure* honesty; add a dedicated/preference-tuned critic model only if measured honesty is poor. Abliteration is the wrong tool (targets refusal, not sycophancy).
4. **Objective gate — EXPANDED & LOCKED:** ✓ Innovation **communicates with environments** (coding, later engineering) to **test/revise and attach a working PoC** before proposals reach the human (§3 `ProofOfConcept`, §8 gate #4). Packet-routed, KGMA-orchestrated; coupling accepted.
5. **Coding "success" for the outcome loop:** ✓ "**Works and works well**" — a reasonably optimized program that completes the requested task (may have tests / multiple files). Phase A maps terminal `completed` → success; else failure. Auto-measuring "optimized" is a flagged follow-up (§5).
6. **NodeWatchdog carve-out for parked proposals:** ✓ Delegated to me and **accepted** — build as designed in §9 (lease cleared + durable `ProposalStore` + startup-sweep carve-out). *(Phase C — not this commit.)*
7. **Diversity per cycle:** ⏳ Delegated to me; getting Fable's take. Left as a **design option** (§7: 3–5 candidates + novelty threshold, QD vs single-candidate ToT). Not built (Phase D).

---

## 14. Campaign machinery — BUILT in Phase E (was design-only until the ProposalStore/human gate existed)

> **STATUS — Phase E complete.** §14a–c and the §4a staged caps are implemented, tested, and pushed.
> - **Sub-unit 1:** `ValidationCampaign` + `SqliteValidationCampaignStore`; pure `CampaignProtocol.Evaluate` verdict; per-stage/per-domain caps in `ProvenancePolicy.Clamp`/`CapFor` (Innovated/UnderTest/Unverified 0.35; ProvisionallyValidated 0.6 general / 0.45 sensitive; HumanApproved uncapped); simple `DomainClassifier` (flagged for a stronger classifier).
> - **Sub-unit 2:** `CampaignCoordinator` (draft → protocol-critic falsification → `AuthorizeCampaign` proposal + parked parent → `HumanAuthorizeCampaign` → child step packets → mechanical verdict → `PromoteFromCampaign` 2nd touch). Sensitive never auto-promotes; failed criteria demote; a missing environment parks on a gap. `HumanGate` delegates to `ICampaignCoordinator`.
> - **Sub-unit 3:** `SandboxPoCGate` — weight-capped self-generated PoC evidence; provenance/confidence untouched.
> - **Sub-unit 4:** `ToolingProposal` / `ToolingProposalEmitter` (data-only, demand-driven, rate-limited, critic-reviewed); `ResumeBlockedCampaignAsync` re-drives a campaign once the human builds the missing node at compile time.

All of this is an instance of the §0a shape: **accumulate → package → propose → human event → state change.** It depends on the Phase-C human gate and is captured here so Phase A doesn't paint us into a corner.

### 14a. The ledger and privileged human events
- Each innovated entry has an **append-only ledger** of structured events. `State(entry) = f(ledger)` is a **pure, replayable function** — the entry's provenance/confidence are derivable by folding the ledger. (Phase A materializes them for convenience but the ledger is authoritative.)
- **Event kinds split by authorship:**
  - *Automatic (append evidence / move down):* `Created`, `SuccessEvidence`, `FailureEvidence`, `AutoDemotion`.
  - *Human-authored (privileged — may cross a boundary UP):* `HumanAuthorizeCampaign`, `HumanConfirmPromotion`, `HumanReject`, `HumanRetract`.
- **Only the UI-node gate may append human-authored kinds.** The store's invariant guard (built in Phase A) rejects any state change that raises trust rank unless its event kind is human-authored — so `State=f(ledger)` and the per-kind authorship rule are the *only* difference between "propose" and "authorize."
- **Eligibility thresholds PROPOSE, never flip state.** When evidence makes an entry campaign-eligible, the system files a proposal into the `ProposalStore` (§9) — it does not change the entry.

### 14b. ValidationCampaign (Fable's sketch — the antidote to "validation theater")
```
ValidationCampaign {
  EntryId; HypothesisRevisionSeq (immutable snapshot of what is being validated);
  TargetStage;                                   // the promotion sought
  Protocol: ValidationStep[] {                   // PRE-REGISTERED, fixed BEFORE running
     capability;                                 // routed as a child packet (coding sandbox / deep-research corroboration)
     SuccessCriteria;                            // objective, pinned in advance — cannot be edited after start
  };
  Authorization { human; budget };
  Verdict = pure_function(pre_registered_criteria × per_step_evidence);   // mechanical, not a vibe
}
```
- **Pre-registration is load-bearing.** The `SuccessCriteria` are fixed *before* the campaign runs; the verdict is a mechanical function over them. This blocks post-hoc rationalization ("the run sort of worked").
- **The critic must falsify the PROTOCOL, not the result.** Before authorization the critic asks *"what failure mode does this protocol NOT exercise?"* — and **the human approves the design**, not just the eventual verdict. Approving a *test plan* is far harder to sycophantically rubber-stamp than approving a conclusion.
- **Lifecycle (rides the §9 gate):** draft → **authorization request in ProposalStore** → on authorize, steps become **child packets routed by capability** under the parent correlation → mechanical **verdict** → **promotion proposal** (a *second* human touch). Medical/eng: **both** touches mandatory. General domains: the second touch **may be pre-authorized** in the campaign authorization.

### 14c. ToolingProposal (data-only — never self-modification)
- The innovate node may **propose** new node/tooling it needs (e.g. a simulation node) as **data only**: `{ purpose; capability sought; sketched INode/Capability contract; blocked campaigns/gaps as demand evidence }`.
- **Hard rules:** never self-modifies; never registers a node at runtime. **Node registration stays compile-time** — a human builds it in a normal dev session. Demand-driven (must cite ≥1 blocked step/gap), rate-limited, critic-reviewed.
- **The capability boundary is treated identically to the trust boundary** — extending DARCI's capability surface is an upward crossing, hence a human-authored event (§0a).

---

## 15. Known failure modes (named honestly)

- **Human bottleneck.** Every upward step needs a human; if Tinman is swamped, promotions/campaigns stall. *Mitigation:* measure the **`AwaitingHuman` queue age** (proposal TTL/metrics). **Pre-agreed first relaxation if it rots:** allow **automatic mid-tier (`ProvisionallyValidated`) promotion for GENERAL domains only** — never sensitive (medical/eng), never the trusted tier. This is a deliberate, bounded valve, not a default.
- **Validation theater (the weakest point).** A campaign could be designed to pass — objective-looking but toothless. *Mitigation (14b):* pre-registered criteria fixed before running + the critic **falsifies the protocol** ("what does this not test?") + the **human approves the test design**, not just the verdict. This is the primary defense and deserves ongoing scrutiny; if any single mitigation is weak, it's this one.
- **Ranking-score confusion.** The within-cap score nudge (§5) could be misread as trust. *Mitigation:* it is *always* `IsLow`, `IsTrustedAsFact` is provenance-based (not score-based), and the score is documented as ordering-only (freezable).

---

## 13. Prior-art sources

- AlphaEvolve — [arXiv 2506.13131](https://arxiv.org/abs/2506.13131), [DeepMind PDF](https://storage.googleapis.com/deepmind-media/DeepMind.com/Blog/alphaevolve-a-gemini-powered-coding-agent-for-designing-advanced-algorithms/AlphaEvolve.pdf); FunSearch (generator+evaluator+program DB).
- Generative Agents (memory stream + reflection), Park et al. 2023 — [ACM](https://dl.acm.org/doi/fullHtml/10.1145/3586183.3606763).
- Blackboard / Hearsay-II — [Wikipedia: Blackboard system](https://en.wikipedia.org/wiki/Blackboard_system), [Nii, Blackboard Systems](http://i.stanford.edu/pub/cstr/reports/cs/tr/86/1123/CS-TR-86-1123.pdf).
- Quality-Diversity / MAP-Elites / Novelty search — [MAP-Elites overview](https://www.emergentmind.com/topics/map-elites-algorithm).
- LLM sycophancy (CoT can intensify it; social sycophancy) — [Good Arguments Against the People Pleasers](https://arxiv.org/pdf/2603.16643), [ELEPHANT: social sycophancy](https://arxiv.org/pdf/2505.13995), [Sycophancy Is Not One Thing](https://arxiv.org/html/2509.21305v1).
- Also drawn from training knowledge: Soar/ACT-R (impasse→subgoal→chunk), Reflexion, Tree-of-Thoughts, Graph-of-Thoughts, KG link prediction.
