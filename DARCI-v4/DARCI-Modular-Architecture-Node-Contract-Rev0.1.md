# DARCI — Modular Architecture & Node Contract

**Rev 0.1.1 (Draft)** · July 2026
**Audience:** DARCI core maintainer, external node authors (human or coding agent)
**Status:** Contract frozen for v0.1 implementation. Open decisions listed in §10.
**Changes since 0.1:** see the [Rev 0.1.1 change note](#rev-011-change-note) — one addition, `outcome: "blocked"`.

---

## 0. Purpose

This document defines how DARCI is split into a **stable reasoning core** and a set of **replaceable nodes**, and specifies the contract a node must satisfy to interoperate with the core.

It has two readers in mind:

1. **A human collaborator** who wants to build a DARCI capability without reading the core's source.
2. **A coding agent** handed this document and told "build a node that does X." Everything needed to produce a conformant node should be derivable from §5, §6, and Appendix A/B without further context.

Non-goal: this is not a description of what DARCI *does*. It is a description of the seams.

---

## 1. Design principles

These constraints drive every decision below. If a proposed change violates one, the change is wrong.

| # | Principle | Consequence |
|---|---|---|
| P1 | **The core is small and boring.** | Capability lives in nodes. The core routes, brokers, and enforces — it does not reason about domains. |
| P2 | **Nodes are language-agnostic.** | Contract is transport + JSON, not a .NET interface. A Python node is a first-class node. |
| P3 | **No node touches a resource directly.** | Neo4j, model inference, filesystem, network — all mediated by core brokers. |
| P4 | **Hardware variance is a config concern, not a code concern.** | Nodes request model *classes*, never model names. Host profile resolves them. |
| P5 | **Telemetry is not optional.** | Every node invocation emits a standard record. This is the substrate for later distillation work; retrofitting it is not viable. |
| P6 | **Untrusted content is tracked, not trusted.** | Taint propagates. Tainted content cannot silently trigger privileged capability. |
| P7 | **Setup is one command.** | A collaborator who cannot run DARCI in under ten minutes is not a collaborator. |

---

## 2. Topology

```
                    ┌───────────────────────────────┐
   operator ───────▶│         DARCI CORE            │
   (Telegram, CLI)  │                               │
                    │  intent router                │
                    │  goal / task lifecycle        │
                    │  node registry + dispatch     │
                    │  trust & taint enforcement    │
                    │                               │
                    │  ┌──────── BROKERS ────────┐  │
                    │  │ memory │ model │ telem  │  │
                    │  └────┬───────┬───────┬────┘  │
                    └───────┼───────┼───────┼───────┘
                            │       │       │
                    ┌───────▼──┐ ┌──▼────┐ ┌▼──────────┐
                    │  Neo4j   │ │Ollama │ │ telemetry │
                    │  (KGMA)  │ │Claude │ │   store   │
                    └──────────┘ └───────┘ └───────────┘
                            ▲
                            │  brokered access only
        ┌───────────────────┴────────────────────────┐
        │                                            │
   ┌────▼─────┐  ┌──────────┐  ┌──────────┐  ┌───────▼──────┐
   │  Lizzy   │  │ research │  │ program. │  │  (your node) │
   │ (intent) │  │   env    │  │   env    │  │              │
   └──────────┘  └──────────┘  └──────────┘  └──────────────┘
      NODE        ENVIRONMENT   ENVIRONMENT        NODE
```

Everything below the core communicates over the node contract. The core never imports node code.

---

## 3. What the core owns and never delegates

The core is the only component permitted to:

- Resolve intent to capability and select a node (routing)
- Create, mutate, and close goals and tasks
- Read or write the knowledge graph
- Invoke a model provider
- Grant or deny a permission
- Assign, propagate, and check taint
- Write telemetry records
- Hold secrets

If a node needs any of these, it asks the core. This is what makes the system auditable and what keeps the safety boundary in one place rather than scattered across every contributor's code.

---

## 4. Node taxonomy

Three kinds, in increasing order of privilege and complexity. All three implement the same base contract (§5).

**4.1 Capability Node** — stateless, request/response, no durable storage of its own. Given input, returns output. Example: Lizzy (intent classification), a summarizer, a unit converter, a scanner. This is the default and what most collaborators should build.

**4.2 Environment Node** — owns a durable workspace and has a lifecycle beyond a single request. Example: the programming environment, a research environment, a future model-authoring environment. Implements the base contract plus §8. Higher privilege, requires explicit operator grant.

**4.3 Adapter Node** — wraps an external system that DARCI does not control (a vendor API, a shop tool, a hardware device). Structurally identical to a capability node; called out separately because adapters are the most common source of untrusted content and must declare `emits_untrusted: true`.

---

## 5. The Node Contract (v0.1)

### 5.1 Manifest

Every node ships a `darci-node.json` at its root. The core reads this at registration. It is the single source of truth about what a node is and needs.

```json
{
  "contract_version": "0.1",
  "node_id": "example.summarize",
  "display_name": "Document Summarizer",
  "node_version": "1.0.0",
  "kind": "capability",
  "endpoint": "http://localhost:8412",
  "capabilities": [
    {
      "name": "summarize.text",
      "description": "Condense a text document to a target length.",
      "input_schema":  { "$ref": "./schemas/summarize.in.json" },
      "output_schema": { "$ref": "./schemas/summarize.out.json" },
      "typical_latency_ms": 2000,
      "deadline_ms": 30000
    }
  ],
  "requires": {
    "model_classes": ["chat.balanced"],
    "memory_scopes": ["read:documents"],
    "permissions": [],
    "emits_untrusted": false
  },
  "health": "/health",
  "author": "…",
  "repository": "…"
}
```

Field notes:

- `node_id` — reverse-domain-ish, globally unique, immutable across versions.
- `capabilities[].name` — the routable verb. The core's router maps intents to these. Namespace them (`domain.action`).
- `input_schema` / `output_schema` — JSON Schema. **Required.** The core validates both directions and rejects non-conformant payloads. This is what lets a coding agent write against the contract without guessing.
- `requires.model_classes` — see §6.2. Declaring a class the host cannot satisfy causes registration to fail loudly at startup, not silently at runtime.
- `requires.memory_scopes` — see §6.1. Least privilege; ask for the narrowest scope that works.
- `emits_untrusted` — set `true` if any output can contain content the node did not author (fetched web pages, inbound messages, file contents). See §7.

### 5.2 Required endpoints

A node MUST expose exactly three HTTP endpoints. No others are called by the core.

| Method | Path | Purpose |
|---|---|---|
| `GET` | `/health` | Liveness + readiness. `200` with `{"status":"ok"}` when able to serve. |
| `GET` | `/manifest` | Returns the manifest. Must match the on-disk file. |
| `POST` | `/invoke` | Executes a capability. Body and response per §5.3. |

That is the entire surface. Anything a node wants from DARCI happens through the callback URL supplied in the request envelope (§6), not through additional endpoints.

### 5.3 Request / response envelope

Every `/invoke` request:

```json
{
  "envelope_version": "0.1",
  "trace_id": "01J8...",
  "goal_id": "goal_7f3a",
  "capability": "summarize.text",
  "issued_at": "2026-07-24T18:04:11Z",
  "deadline_at": "2026-07-24T18:04:41Z",
  "principal": {
    "trust": "operator",
    "id": "tinman"
  },
  "taint": {
    "level": "clean",
    "sources": []
  },
  "broker": {
    "url": "http://localhost:8080/broker",
    "token": "…scoped, single-request…"
  },
  "payload": { }
}
```

Every response:

```json
{
  "envelope_version": "0.1.1",
  "trace_id": "01J8...",
  "outcome": "ok",
  "taint": {
    "level": "clean",
    "sources": []
  },
  "confidence": 0.91,
  "payload": { },
  "error": null,
  "dependency": null
}
```

Rules:

- `trace_id` MUST be echoed unchanged. It is how telemetry correlates. **It is a per-invocation id and MUST NEVER be used as a correlation/evidence key** — `goal_id` is the correlation root. Keying anything durable off `trace_id` silently breaks the outcome-feedback loop, because no later invocation will ever present the same value.
- `goal_id` is the correlation root: the id that ties an invocation to the goal's whole causal chain, including work that happens later under a different invocation. Evidence links (e.g. an innovated hypothesis served into a run) are recorded against it.
- `deadline_at` is binding. A node that cannot finish in time MUST return `DEADLINE_EXCEEDED` rather than running long. The core will abandon the call at the deadline regardless.
- `broker.token` is single-request and scoped to exactly what the manifest declared. It expires with the deadline.
- `confidence` is optional but strongly encouraged — it is one of the more useful signals for later distillation targeting. Omit rather than fabricate.
- `taint` in the response MUST be at least as high as the request's, and MUST be raised if the node introduced outside content.

### 5.4 Outcome + error taxonomy

`outcome` is one of `ok` | `error` | **`blocked`** *(added in 0.1.1)*.

**`blocked`** means: *my bounded work completed cleanly, and the GOAL now depends on something outside this
invocation.* It is **not** an error and **not** a retry request. The node returns promptly; the **core**,
which owns the goal/task lifecycle (§3), decides what to do — typically parking the durable work record until
the dependency resolves. When `blocked`, the `dependency` object is:

```json
{ "kind": "human-decision", "detail": "awaiting campaign authorization", "reference_id": "proposal_123" }
```

| `dependency.kind` | Meaning | Core's usual response |
|---|---|---|
| `human-decision` | A human must approve/authorize before the goal can proceed | Park the work record; surface a proposal to the operator |
| `missing-environment` | Nothing exists that can run this step yet | Park; record a gap and (optionally) a tooling proposal |
| `pending-outcome` | Waiting on a real-world result that has not happened yet | Park; resolve when the outcome arrives |

When `error`, the `error` object is:

```json
{ "code": "DEPENDENCY_UNAVAILABLE", "message": "…", "retryable": true }
```

| Code | Retryable | Meaning |
|---|---|---|
| `INVALID_INPUT` | no | Payload failed the node's own validation. |
| `PERMISSION_DENIED` | no | Broker refused a request the node made. |
| `MODEL_UNAVAILABLE` | maybe | Requested model class could not be served. |
| `DEPENDENCY_UNAVAILABLE` | yes | External system the node depends on is down. |
| `DEADLINE_EXCEEDED` | yes | Ran out of time. |
| `NOT_IMPLEMENTED` | no | Capability declared but not yet built. |
| `INTERNAL` | maybe | Unclassified node failure. |

The core will not invent retries for `retryable: false`. Classify honestly. **Do not use
`DEPENDENCY_UNAVAILABLE` to mean "waiting on a human or a future outcome"** — that code means *an external
system I depend on is down, try me again*. Use `outcome: "blocked"` instead.

---

### Rev 0.1.1 change note

**One addition: `outcome: "blocked"` with a structured `dependency` object (§5.3, §5.4).**

Rev 0.1 gave a node only `ok` | `error`. That forced a node whose own work had finished, but whose *goal* was
now waiting on something external, to misreport itself. The nearest 0.1 code, `DEPENDENCY_UNAVAILABLE`, is
marked `retryable: true` and means "the thing I call is down" — so the core would either retry pointlessly or
record a failure that never happened. Neither is true of a hypothesis awaiting human authorization.

This matters because DARCI already has three real waits of exactly this shape: the **human gate** (a promotion
or campaign authorization pending an operator decision — which today parks a work record for as long as it
takes, surviving restarts), a **validation step with no environment to run in**, and a **pending real-world
outcome** that the evidence loop will resolve later. A stateless `ok|error` contract cannot express any of
them without lying.

Getting this into the contract *before* Phase 1 freezes §5/§6 avoids a painful retrofit: adding a third
outcome later would be a breaking envelope change requiring every node to bump `contract_version`.

**Scope note.** Phase 1 is in-process, and long-lived waiting stays **core-side**: the core parks the durable
work record (`NodePacket` → `AwaitingDependency`, lease cleared, watchdog carve-out) exactly as it does today.
`blocked` does not change Phase 1 behavior — it makes the *contract* honest for the out-of-process future, so
a collaborator's node has a truthful way to say "your goal is waiting on a human, and that is not my failure."

### 5.5 Lifecycle

1. **Discovery** — core scans `nodes/` for `darci-node.json` files at startup.
2. **Validation** — manifest schema checked; declared `model_classes` and `memory_scopes` checked against host profile and policy. Failure here is fatal and named.
3. **Handshake** — core polls `/health` until ready or timeout; fetches `/manifest` and verifies it matches the on-disk copy.
4. **Registered** — capabilities enter the router table.
5. **Degraded** — health check fails; capabilities are removed from routing, core continues, operator is notified. **The core never dies because a node died.**
6. **Shutdown** — core stops routing; environments get a drain window (§8).

### 5.6 Versioning

- `contract_version` is the envelope + manifest format. Core declares a supported range. Mismatch = refuse to register, with a clear message.
- `node_version` is the node's own semver. Breaking a capability's schema requires a major bump *and* a new capability name (`summarize.text` → `summarize.text.v2`) so both can be routed during migration.

---

## 6. Brokered services

Nodes reach these through `POST {broker.url}` with `broker.token`. One endpoint, discriminated by `service`.

### 6.1 Memory broker (KGMA)

Nodes never speak Cypher and never hold Neo4j credentials.

```json
{ "service": "memory", "op": "query", "scope": "read:documents",
  "selector": { "type": "Document", "where": { "id": "doc_419" } } }
```

Scopes are declared in the manifest and enforced per-request. Writes require `write:*` scope and are attributed to the node in the graph, so provenance survives. A node asking outside its declared scope gets `PERMISSION_DENIED` — and that denial is logged, which is how you notice a misbehaving or compromised node.

### 6.2 Model broker

**Nodes request a capability class, never a model name.** This is the single most important rule for collaborator portability.

```json
{ "service": "model", "op": "complete", "class": "chat.balanced",
  "messages": [ … ], "max_tokens": 800 }
```

Classes for v0.1:

| Class | Intent | Your 3070 Ti profile | Beefier host profile |
|---|---|---|---|
| `chat.fast` | Short, latency-sensitive | small local Ollama | small local |
| `chat.balanced` | General work | mid local Ollama | mid/large local |
| `chat.deep` | Hard reasoning, long context | Claude API | large local or API |
| `classify.intent` | Structured labels | Lizzy / ONNX | same |
| `embed.text` | Vectors | local embedder | local embedder |
| `code.generate` | Code synthesis | Claude API | local coding model |

Each host ships a `host-profile.json` mapping every class to a concrete provider. A collaborator with different hardware edits one file; no node code changes. A host that cannot satisfy a class a node requires fails at registration with a named error, not mid-task.

The broker is also where token accounting and model-level telemetry happen — another reason nodes don't get direct provider access.

### 6.3 Telemetry

Emitted by the core (not the node) for every invocation, so it cannot be skipped or faked:

```json
{ "trace_id": "…", "goal_id": "…", "node_id": "…", "node_version": "…",
  "capability": "…", "started_at": "…", "duration_ms": 1840,
  "model_class": "chat.balanced", "model_resolved": "…",
  "tokens_in": 1204, "tokens_out": 310,
  "outcome": "ok", "error_code": null, "confidence": 0.91,
  "taint_level": "clean", "host_profile_id": "tinman-3070ti" }
```

Nodes MAY add structured detail via a `telemetry_extra` object on the response. They cannot suppress the base record.

**Why day one:** the distillation and specialization work discussed for later phases is only decidable against real data — which roles are hot, which are slow, which have low confidence, which burn the most tokens for the least value. Turning this on after the fact means a year of blind guessing. Turning it on now means the decision makes itself.

### 6.4 Secrets & config

Nodes get non-secret config through `/invoke` payloads or their own local config file. Nodes never receive DARCI's credentials. A node needing its own third-party key manages it itself and declares that in its README — the core does not vault it in v0.1.

---

## 7. Trust and taint

Four principal trust levels: `system` > `operator` > `collaborator` > `untrusted`.

Taint levels: `clean` → `derived` → `untrusted`.

Rules:

1. Content originating outside DARCI (web fetch, inbound Telegram, file upload, third-party API) enters as `untrusted`.
2. Any node output computed from tainted input is at least `derived`. The node is responsible for declaring this honestly; adapters MUST set `emits_untrusted: true` in their manifest.
3. **Tainted content cannot select a capability.** Routing decisions are made from operator intent, never from text that arrived from outside. This closes the obvious prompt-injection path where fetched content instructs DARCI to do something.
4. Privileged operations — memory writes, environment execution, external sends — invoked on a path carrying `untrusted` taint require explicit operator confirmation.
5. Taint is recorded in telemetry, so injection attempts are visible after the fact even when they fail.

This is deliberately conservative for v0.1. It will produce some friction. Loosen it with evidence, not with impatience.

---

## 8. Environment contract (extends §5)

An Environment Node additionally:

- Declares `"kind": "environment"` and a `workspace_root`.
- Exposes `POST /session` (open), `DELETE /session/{id}` (close), and accepts a `session_id` in the `/invoke` envelope.
- Persists state only under its `workspace_root`. Nothing outside.
- Declares resource ceilings in the manifest (`max_disk_mb`, `max_runtime_s`, `network: none|allowlist|full`).
- Honors a drain window on shutdown: finish or checkpoint in-flight work, then exit.
- Treats everything inside the workspace as at least `derived` taint. Code and artifacts generated inside an environment are not clean by default.

Environments are the highest-privilege node type. Grant them explicitly, one at a time, and keep the list short.

---

## 9. Migration plan

Strangler-fig: contracts first, then wrap, then extract. No big-bang rewrite. Each phase leaves DARCI working.

**Phase 0 — Freeze the contract.** This document. Version it in the repo. Anything built afterward targets it.

**Phase 1 — Carve the core, in-process.** Introduce the node registry, envelope, and dispatch *inside* the existing .NET solution. Existing subsystems become in-process nodes behind adapters. No behavior change, no new processes. Goal: every capability call in DARCI now flows through one dispatch point.

**Phase 2 — Stand up the brokers.** Memory broker in front of Neo4j; model broker in front of Ollama/Claude. Rewrite every existing call site to go through them. Introduce `host-profile.json`. Turn on telemetry here. This is the phase that pays for itself later — do not shortcut it.

**Phase 3 — First out-of-process node.** Lizzy. She is already a standalone REST service, so the delta is a manifest, the envelope, and health/manifest endpoints. This proves the boundary end-to-end with the lowest-risk candidate.

**Phase 4 — Extract the programming environment.** First Environment Node. Exercises §8, sessions, resource ceilings, and taint on generated artifacts.

**Phase 5 — Turnkey bootstrap.** `docker compose up` brings up core + Neo4j + Lizzy + one example node, seeded and working, on a clean machine. Write the ten-minute quickstart. **This is the gate for collaborator outreach** — not Phase 4.

**Phase 6 — Publish.** Contract doc + `darci-node-template` repo (Python and C# starters, both under 100 lines) + the example node. Then reach out.

Ordering rationale: Phases 1–2 are invisible to everyone but you and are the ones that are miserable to retrofit. Phases 3–4 prove the design against real code. Phase 5 is what prevents a repeat of the earlier handoff that stalled on setup.

---

## 10. Open decisions

These are genuinely yours to make; the doc assumes the recommendation but does not depend on it.

| # | Decision | Recommendation | Why |
|---|---|---|---|
| D1 | Transport: HTTP+JSON vs gRPC | HTTP+JSON for v0.1 | Lowest barrier for Python/JS collaborators. Revisit if per-call latency becomes the bottleneck; the envelope maps cleanly to protobuf later. |
| D2 | Node discovery: static manifests vs self-registration | Static `nodes/` scan | Deterministic, reviewable, no rogue registration. Self-registration is a Phase 6+ convenience. |
| D3 | Does the core launch node processes? | No — compose orchestrates, core connects | Keeps the core out of process supervision. Simpler to reason about and to debug. |
| D4 | Core↔node auth on localhost | Shared secret in the broker token | Cheap, and forces the token-scoping discipline you'll want when something runs remote. |
| D5 | Schema language | JSON Schema in the manifest | Language-neutral, machine-readable, and directly usable by a coding agent generating a node. |
| D6 | Where telemetry lands | Local store (SQLite or Postgres), separate from KGMA | Different access pattern, different retention, and you don't want telemetry volume in the graph. |

---

## Appendix A — Minimal conformant node

Manifest (`darci-node.json`):

```json
{
  "contract_version": "0.1",
  "node_id": "example.echo",
  "display_name": "Echo",
  "node_version": "0.1.0",
  "kind": "capability",
  "endpoint": "http://localhost:8500",
  "capabilities": [{
    "name": "example.echo",
    "description": "Returns its input. Reference implementation.",
    "input_schema":  { "type": "object", "required": ["text"],
                       "properties": { "text": { "type": "string" } } },
    "output_schema": { "type": "object", "required": ["text"],
                       "properties": { "text": { "type": "string" } } },
    "typical_latency_ms": 5,
    "deadline_ms": 1000
  }],
  "requires": {
    "model_classes": [], "memory_scopes": [],
    "permissions": [], "emits_untrusted": false
  },
  "health": "/health"
}
```

Service (pseudocode — any language, any framework):

```
GET  /health    -> 200 {"status": "ok"}
GET  /manifest  -> 200 <contents of darci-node.json>

POST /invoke:
  req = parse(body)
  assert req.envelope_version == "0.1"
  validate(req.payload, input_schema)          # else INVALID_INPUT
  if now() > req.deadline_at: return DEADLINE_EXCEEDED

  result = { "text": req.payload.text }

  return {
    "envelope_version": "0.1",
    "trace_id": req.trace_id,                  # echoed unchanged
    "outcome": "ok",
    "taint": req.taint,                        # unchanged: added nothing
    "payload": result,
    "error": null
  }
```

---

## Appendix B — Implementation checklist

For a human or coding agent building a node. A node is conformant when all of these hold.

- [ ] `darci-node.json` present at repo root, validates against the manifest schema
- [ ] `node_id` unique; capability names namespaced `domain.action`
- [ ] JSON Schema supplied for **both** input and output of every capability
- [ ] `GET /health` returns `200 {"status":"ok"}` only when actually ready to serve
- [ ] `GET /manifest` returns bytes identical to the on-disk manifest
- [ ] `POST /invoke` echoes `trace_id` unchanged in every response, including errors
- [ ] Input validated against the declared schema; `INVALID_INPUT` on failure
- [ ] `deadline_at` respected — returns `DEADLINE_EXCEEDED` rather than overrunning
- [ ] Errors use the §5.4 taxonomy with an honest `retryable` flag
- [ ] No direct Neo4j access — memory only via the broker, within declared scopes
- [ ] No hardcoded model names — inference only via the broker, by class
- [ ] `requires` in the manifest lists everything the node actually uses, and nothing more
- [ ] `emits_untrusted: true` if any output can carry outside content
- [ ] Response `taint` ≥ request `taint`; raised when outside content was introduced
- [ ] `confidence` emitted where meaningful, omitted where it would be fabricated
- [ ] Node fails safe: if it cannot serve, it fails health rather than returning wrong answers
- [ ] README states how to run it, its config, and any third-party credentials it needs
- [ ] Starts clean from `docker compose up` with no manual steps

---

*Rev 0.1 — draft for review. Sections 5 and 6 are the contract proper and should be treated as frozen once Phase 1 begins; everything else is guidance.*
