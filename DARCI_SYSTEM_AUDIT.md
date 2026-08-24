# DARCI System Audit — Process Separation & Node Functioning

**Audited at:** `8ba29f3` + uncommitted focus-mode work — 2026-08-23
**Question asked:** does the interconnected node system work as intended?
**Short answer:** there is no interconnection yet. The node layer is a single-producer audit log
with a sweeper that cannot enforce and can be silently overwritten. Nothing routes.

---

## 1. Process separation — what actually runs where

### One .NET process does almost everything

`Darci.Api` (default port 5081) hosts **five** background services in-process:

| Hosted service | `Program.cs` | Role |
|---|---|---|
| `Darci.Core.Darci` | :278 | the living loop (Perceive→Feel→Decide→Act→Reflect) |
| `NodeWatchdogService` | :326 | 60-second packet sweep |
| `ResponseDispatcher` | :170 | outbound message fanout |
| `TelegramInboundService` | :171 | Telegram polling |
| `SqsRelayService` | :408 | **conditional** — only when cloud config is present |

The coding agent loop is *not* a hosted service. It runs fire-and-forget as
`Task.Run` inside `CodingAgentLoop.StartLoop`, tracked in a
`ConcurrentDictionary<string, Task>` (`CodingAgentLoop.cs:46`).

### Genuinely separate processes

| Process | Port | Transport | Status |
|---|---|---|---|
| **Ollama** | 11434 | HTTP | required; shared by living loop *and* coding loop |
| **Python sidecar** (`Darci.Python`, uvicorn) | 8000 | HTTP | optional; CAD + assembly simulation |
| **NLP adapter** | 5200 | HTTP | optional, **off by default** (`NoopNlpClient`) |

### What this means

"Separation of processes" is currently: **one .NET process, plus Ollama, plus an optional Python
sidecar.** Living loop, coding loop, node store, and watchdog all share a process, a SQLite file,
and — until the focus-mode work — a single Ollama instance with no coordination.

The only cross-process boundaries that exist are HTTP to Ollama and HTTP to the Python sidecar.
Everything else is direct method calls.

---

## 2. Node system — load-bearing or instrumentation?

### Instrumentation. Conclusively.

**Producers — exactly one.** `CodingNodeTracker`, driven from `CodingAgentLoop` at five sites:

| Call | `CodingAgentLoop.cs` |
|---|---|
| `BeginAsync` | :175 |
| `RecordAsync` | :399 (once per plan step) |
| `CompleteAsync` | :182, :470 |
| `AbortAsync` | :103 |

**Consumers — read-only.** Three `MapGet` endpoints (`/nodes/packets/{id}`,
`/nodes/packets/{id}/status`, `/nodes/correlations/{correlationId}`) plus `NodeWatchdog`.
There is no `MapPost` on any node route.

**Nothing else references the store.** Grepping `INodePacketStore` across the solution returns
only: DI registration, startup init, the three GET endpoints, `CodingAgentLoop`'s constructor
parameter, `CodingNodeTracker`, and `NodeWatchdog`. `Darci.Core`, `Darci.Engineering`, and
`Darci.Research` never touch packets.

**No control flow branches on packet state.** Nothing reads a packet to decide what to do next.

> **Consequence:** delete the entire `Darci.Nodes` wiring and DARCI's behaviour is unchanged.
> Only the record of it disappears. That is the definition of instrumentation, not infrastructure.

`NodeId` declares six participants — `Orchestrator`, `Living`, `Coding`, `Engineering`,
`Knowledge`, `Cad`. **Only `Coding` is ever written.** The other five are enum values nothing
produces.

### The one real cross-node handoff is untyped

`RoadblockDetector.CheckAndResearchAsync` → `IDeepResearchOrchestrator.RunDeepResearchAsync`
returns a **string**, concatenated into `CodingTaskRecord.RoadblockResearch`. It does not go
through a packet. `NODE_PACKET_PROTOCOL.md` §1.2 says this itself.

---

## 3. Three defects in the orphan-prevention machinery

The stated purpose of `Darci.Nodes` is making orphaning "structurally impossible"
(`NodeWatchdog.cs:8-14`). All three defects below undercut that claim.

### 3.1 The watchdog annotates; it does not enforce

`NodeWatchdog.AbortAsync` (:72-95) does exactly two things: `packet.Transition(...)` and
`_store.SavePacketAsync(...)`. It is a database write.

There is no `CancellationTokenSource.Cancel()`, no process signal, no interaction with
`CodingAgentLoop._runningTasks`. Marking a packet `Aborted` **does not stop the work.** A hung
coding run keeps running, keeps calling Ollama, and — since the focus-mode change — keeps holding
the model focus lease, while the database reports it aborted.

### 3.2 Coding runs are structurally uncancellable

`CodingAgentLoop.cs:96` — `await RunLoopAsync(taskId, options, CancellationToken.None)`.
Hardcoded `None`. `_runningTasks` stores `Task`, not `CancellationTokenSource`. There is no
cancel or stop endpoint anywhere in `Program.cs`.

**The only way to stop a running coding task is to kill the process.** This is not theoretical —
`DARCI_CODING_ENVIRONMENT_LOG.md` sixth pass records exactly that: *"Live API is currently STOPPED
(killed to halt the runaway loop)."* The code explains why that was the only option available.

### 3.3 A watchdog abort can be silently reverted — race condition

The most serious finding, because it corrupts the record rather than merely failing to act.

**The numbers:**

- `CodingNodeTracker.LeaseDuration` = **15 minutes** (:23)
- Lease is renewed **only in `RecordAsync`**, which fires **once per plan step** (:399)
- `ModelRouter` HTTP timeout for a *single generation* = **12 minutes**

A single step is generation + patch apply + build. With a 12-minute generation ceiling and a
15-minute lease, the margin is **three minutes**. Under sixth-pass conditions — generations
"ballooning to many minutes", runs exceeding 1.5 hours — lease expiry mid-step is likely, not
hypothetical.

**The race:**

1. Lease expires mid-step. Watchdog (60s timer) writes `Aborted` to the database.
2. `CodingNodeTracker` holds an **in-memory cache**, `_byTask`
   (`ConcurrentDictionary<string, NodePacket>`, :27). It is never re-read from the store.
3. The step finishes. `CompleteAsync` (:106) checks `current.State.IsTerminal()` — against the
   **stale cached copy**, which still says `Working`. The guard passes.
4. It transitions `Working → Succeeded` and calls `SavePacketAsync`.
5. `SavePacketAsync` is `INSERT INTO node_packets ... ON CONFLICT(id) DO UPDATE SET`
   (`SqliteNodePacketStore.cs:122-128`) — an **unguarded upsert**, no version or state predicate.

**Result: the watchdog's abort is overwritten.** The packet reads `Succeeded`.

The inverse also occurs: if the loop dies after the sweep, a **false** abort stands for work that
was merely slow, not stuck.

Either way the node record diverges from reality **precisely in the slow-run case the subsystem
was built to handle.**

### 3.4 Compounding: unchecked enum reads

Previously reported (see `DARCI_CODING_ENVIRONMENT_LOG.md`, seventh pass). All five enum columns
persist as `INTEGER` with unchecked read casts — `(NodeState)reader.GetInt32(...)`. An
out-of-range value yields an invalid enum instance rather than throwing, and `IsTerminal()` then
misclassifies it. That makes every guard in §3.3 less trustworthy still.

---

## 4. What is actually sound

Worth stating plainly, because the above is unrelenting:

- **The state machine design is good.** `NodeStateMachine.CanTransition` (`NodePrimitives.cs:64+`)
  is correct: terminal is forever, abort is always reachable from active, transitions validated,
  `InvalidNodeTransitionException` on violation.
- **The packet envelope is a reasonable shape** — correlation id, address, capability, payload
  slots, append-only log with confidence per entry.
- **`Darci.Nodes.Tests` exists** — four files covering the state machine, watchdog, store, and
  confidence.
- **The design doc's own audit was accurate.** §1.1's finding that there is no `ActionType.Code`
  holds up; I verified it independently.
- **`CodingNodeTracker` is correctly best-effort** — null store tolerated, all failures logged at
  Debug and swallowed. It cannot break a coding run. That is the right call for instrumentation.

The bones are fine. The wiring is absent and the enforcement is fictional.

---

## 5. Answering the question directly

> *Does the interconnected node system work as intended?*

**No — because it is not yet interconnected.** What exists is Phase 0: an envelope, a store, a
state machine, and a sweeper, exercised by exactly one producer. The routing layer that would make
it a *system* (`INodeRouter`) has zero references in the codebase.

What is currently true:

- Packets are written by the coding loop and read by nobody who acts on them
- Five of six declared nodes never appear
- The watchdog can mark, but cannot stop
- Its marks can be overwritten by the very run it tried to abort

**Recommendation before any further node work — and before the desktop app:** the node layer
should not be presented to contributors as working infrastructure. It is an audit log. Either
finish it (router + enforcement + a second participant) or document it honestly as
instrumentation, because a contributor reading `NodeWatchdog`'s doc comment will reasonably
believe orphaning is impossible, and it is not.

---

## 6. Suggested order of repair

Independent of the node/society roadmap, ordered by ratio of risk removed to effort:

1. **Make coding runs cancellable.** Store a `CancellationTokenSource` per task alongside the
   `Task`; pass its token to `RunLoopAsync`; add `POST /coding/tasks/{id}/cancel`. Removes
   "kill the process" as the only remedy. Small and self-contained.
2. **Give the watchdog teeth.** On abort, cancel the corresponding CTS from (1). Turns annotation
   into enforcement.
3. **Close the overwrite race.** Either re-read packet state in `CodingNodeTracker` before
   transitioning, or add a state/version predicate to `SavePacketAsync` so a terminal row cannot
   be silently replaced. Prefer the latter — it fixes the class, not the instance.
4. **Renew the lease per generation, not per step**, or raise `LeaseDuration` above the
   `ModelRouter` timeout with real margin. The current 3-minute gap is not a margin.
5. **Checked enum reads** — parse and fail loudly on unknown values.

(1)–(3) together would make the orphan guarantee real rather than asserted. None of them require
resolving the open `NodeId` / transport decisions, so they are safe to do now.
