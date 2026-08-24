# DARCI × AI Society — Plan Reconciled Against Current Core

**Reconciled at:** commit `8ba29f3` (2026-06-25) — 2026-08-23
**Source:** "DARCI × AI Society — Fall Semester Working Plan" (planning sketch)
**Method:** every claim below verified against code in this repo; file/line evidence cited.

---

## Summary

The plan is directionally sound. Two items block workstream 2 outright, one workstream is
materially larger than scoped, and two premises reference things that don't exist in `main`.

| # | Finding | Severity | Affects |
|---|---|---|---|
| 1 | Transport contradiction (REST vs in-process) | **Blocking** | Workstream 2 |
| 2 | `Capability` is a closed C# enum | **Blocking** | Workstream 2 |
| 3 | No Docker exists at all | Scope error | Workstream 1 |
| 4 | Neo4j / KGMA absent from `main` | Premise error | Workstream 2 |
| 5 | Usage is not terminal-driven | Premise error | Workstream 3 |

---

## 1. Transport contradiction — BLOCKING

**Plan says:** out-of-process REST, consistent with the Lizzy pattern, "the decisive argument
being that it lets Python developers write nodes."

**Code/design says:** `docs/NODE_PACKET_PROTOCOL.md` §6.1 recommends **in-process** objects
passed through `INodeRouter`, with an SQS-backed transport shaped to "drop in later."

These are incompatible defaults, and §6.1 is listed as an **open decision awaiting Tinman's
call**. It has not been answered.

This is not a detail. It decides whether the society's Python-capable members can contribute
nodes at all, which is the stated purpose of workstream 2.

**Action:** answer §6.1 before any contract work begins.

---

## 2. Capabilities are a closed enum — BLOCKING

`DARCI-v4/Darci.Nodes/NodePrimitives.cs` defines:

- `public enum NodeId` (line 11)
- `public enum Capability` (line 26)
- `public enum NodeState` (line 42)

**Plan says:** "Nodes declare capabilities via manifest; core routes against declared
capabilities."

A C# `enum` is a compile-time closed set. An out-of-process node — in Python or anything else —
cannot introduce a new `Capability` value. As currently built, adding a node requires editing
core source and recompiling the core.

That is the exact failure mode workstream 2 exists to prevent ("lets people contribute without
Tinman babysitting every integration").

**Action:** if the answer to §6.1 is out-of-process, `Capability` must become an open set
(string identifiers plus a runtime registry, or a manifest-driven capability table). This is a
change to already-written Phase 0 code, so it is cheaper to decide now than after nodes exist.

---

## 3. No Docker exists — workstream 1 is under-scoped

**Verified:** zero `Dockerfile*` and zero `docker-compose*` files anywhere in the repo.

**Plan says:** the bootstrapper is "not new architecture — it's packaging and onboarding on top
of what already exists," shelling out to Docker Compose.

There is no container story to shell out to. Standing one up means containerizing:

- the .NET 8 API (`Darci.Api`)
- the Python sidecar (`Darci.Python`, uvicorn — CAD + assembly simulation)
- ONNX model files (`darci_policy.onnx`, `geometry_policy.onnx`) and their volume layout
- the SQLite database path (`DARCI_DB_PATH`, default `DARCI-v4/Data/darci.db`)
- Ollama — external, and the hard part, because GPU passthrough differs per host OS

Current startup is `Start-DARCI.ps1` + locally installed .NET 8 SDK + locally running Ollama.

**Action:** re-estimate workstream 1. Compose authoring and GPU passthrough are the real work;
the .NET launcher wrapping it is the small part. Consider whether a scripted native install
(extending `Start-DARCI.ps1` and `Test-DARCIEnvironment.ps1`, both of which already exist and
already do prerequisite checking) reaches "usable by a team lead" faster than containerizing.

---

## 4. Neo4j / KGMA are not in `main`

**Verified:** zero references to `neo4j` or `kgma` in any `.cs`, `.json`, or `.example` file
on `main`.

**Plan says:** "Nodes never touch Neo4j directly — only KGMA does."

The knowledge graph on `main` is SQLite-backed: `Darci.Memory.Graph.KnowledgeGraph`, constructed
with the same `connectionString` as everything else (`Program.cs:280`). Neo4j environment
variables exist only on the `origin/feat/node-packet-protocol` branch.

**Action:** either treat that rule as forward-looking and say so explicitly in the contract docs,
or land the Neo4j work on `main` before publishing a contract that depends on it. Do not ship a
contract rule referencing a component contributors cannot see.

---

## 5. Usage is not terminal-driven

**Verified:** `DARCI-v4/Darci.Api/wwwroot/app` exists and is served — `Program.cs` calls
`UseDefaultFiles()` and `UseStaticFiles()`, README documents the UI at
`http://localhost:5081/app/`. A SignalR hub is mapped at `/hub` (`Program.cs:410`) and
`TelegramInboundService` is registered as a hosted service.

**Plan says:** "Current usage is terminal-driven; Telegram is effectively dormant."

Telegram being dormant is plausible. "Terminal-driven" is not — there is a working browser UI.

**Action:** the desktop client would be a *third* surface. It may still be the right society
project for the reasons given (splittable across skill levels, clean interface boundary, nothing
sensitive handed over) — but justify it on those grounds, not on "the core has no GUI."

---

## What already exists that workstream 2 should build on

`Darci.Nodes` is real, tested, and wired — not a greenfield:

- `NodePacket` (`NodePacket.cs:10`), `NodePacketStatus` (:147),
  `InvalidNodeTransitionException` (:163)
- `INodePacketStore` (`INodePacketStore.cs:10`) / `SqliteNodePacketStore` (362 lines)
- `NodeWatchdog` (`NodeWatchdog.cs:16`)
- `NodeLogEntry`, `PacketPayload` (`NodePrimitives.cs:112`, :135)
- Registered `Program.cs:313–318`, initialized `:438–440`, REST endpoints from `:1251`
- `Darci.Nodes.Tests` — 4 test files, in `DARCI.sln`

**Missing:** `INodeRouter`. Zero references anywhere. The packet, store, state machine, and
watchdog exist; routing does not. That is the actual next build step, and §6.1 and §6.3 gate it.

---

## The hardware question already has partial data

Plan's open question: *"What's the minimum viable tier — what's the smallest hardware that gets
a usable core?"*

`DARCI_CODING_ENVIRONMENT_LOG.md` sixth pass (2026-06-11) records the answer being worse than
hoped: DARCI's autonomous core (`gemma4`) and the coding loop (`qwen2.5-coder:7b`) contending on
a single local Ollama drove individual generations to many minutes and full runs past **1.5
hours, on Tinman's own development machine**. The recommended fix — pausing or yielding core
autonomy during a coding loop, or a dedicated Ollama instance — was **never implemented**
(verified: no mutex, semaphore, or focus-mode logic in `Darci.Coding/*.cs` or `Program.cs`).

**Why this matters to the plan:** the reference-build premise depends on team leads standing up
cores that are pleasant enough to poke at. A core that takes 90 minutes to do anything fails at
first contact, and it fails on *their* hardware, which is worse than Tinman's.

**Action:** treat model-use serialization as a **prerequisite to the bootstrapper's tiering
logic**, not a later optimization. The tier recommendation is only meaningful once concurrent
model load is bounded.

---

## Also relevant: the core cannot invoke coding

`NODE_PACKET_PROTOCOL.md` §1.1 records that there is **no `ActionType.Code`** — the coding
subsystem is unreachable from the living loop's `Decide`, and only runs when something POSTs to
REST. The doc calls this "the single biggest structural gap."

This bears on what a "node" means to contributors. If the core cannot autonomously route work to
a node, then nodes are externally-triggered services, and the contract should say so plainly.
§6.2 ("does coding join the living loop?") is the governing open decision.

---

## Revised sequencing

| Order | Item | Owner | Change from original |
|---|---|---|---|
| 0 | Answer §6.1, §6.2, §6.3 | Tinman | **New** — gates everything in workstream 2 |
| 1 | Bound concurrent model use (focus mode / second Ollama) | Tinman | **New** — prerequisite to tiering |
| 2 | Bootstrapper | Tinman | Re-estimate; no Docker baseline exists |
| 3 | `INodeRouter` + capability model | Tinman | Was folded into "node contract" |
| 4 | Node contract + docs + hello-world template | Tinman | Unchanged in intent |
| 5 | Desktop client | Society | Unchanged; re-justify rationale |
| 6 | Mobile client | Tinman (personal) | Unchanged |

The three starter node ideas (summariser / file watcher / external source query) remain well
chosen — they stress the contract in genuinely different directions. Keep the rule about not
publishing until three dissimilar nodes exist.

---

## Still deferred / undecided

Carried forward from the original, unchanged:

- Model download strategy (bootstrapper pulls vs. Ollama on first run)
- Whether the desktop client is one project or split into UI / plumbing tracks
- Group size — unknown until fall attendance
- Audio pipeline for mobile

Added by this reconciliation:

- Open vs. closed `Capability` set (falls out of §6.1)
- Whether Neo4j lands on `main` before the contract publishes
- Native scripted install vs. containerization for the bootstrapper

---

## Documentation drift note

`DARCI_CODING_ENVIRONMENT_LOG.md` ends at the sixth pass (2026-06-11), but commits run through
2026-06-25 — the entire `Darci.Nodes` layer, `CodingNodeTracker`, the `NodeWatchdogService`
wiring, and a 530-line expansion of `Darci.Coding.Tests` are undocumented there. Separately,
`NODE_PACKET_PROTOCOL.md` is still headed *"DRAFT — not implemented. Do not commit until Tinman
signs off"* while Phase 0 is substantially built and committed.

Both should be trued up before contributors read them, or the society will onboard against a
description of the system that no longer matches the system.
