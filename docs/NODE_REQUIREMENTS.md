# DARCI Node Requirements

**Authoritative contract specification for anyone — human or AI agent — writing a DARCI node.**

Companion to [`DARCI-v4/nodes/README.md`](../DARCI-v4/nodes/README.md), which is the short walkthrough.
This document is the specification: every contract a node must satisfy, what is actually enforced in code,
and what is merely declared. Where the two disagree, this document is the one that was written by reading
the sources, and it names the file and type for every claim so you can check it yourself.

Contract version described: **`0.1.1`** (`NodeContractVersion.Current`).
Verified against the tree at commit `4ace1fd` on `feat/node-packet-protocol`.

---

## 0. How to read this document

Every requirement carries a status marker. Treat them as load-bearing — the difference between
"the core will reject you" and "nobody is checking yet" determines how you spend your effort.

| Marker | Meaning |
|---|---|
| **[ENFORCED]** | Code checks this. Violate it and you are rejected, skipped, or your result is reinterpreted. |
| **[REQUIRED]** | The contract requires it, and something downstream breaks if you ignore it, but no code validates it today. |
| **[RECOMMENDED]** | Good practice drawn from the reference node. Nothing depends on it. |
| **[RESERVED]** | Present in the envelope, read by nothing. Populate it correctly; do not rely on it doing anything. |
| **[DRIFT]** | A place where prose elsewhere in the repo and the code disagree. The code behaviour is given. |

A note on scope: this contract is **local-first by decision**, and §14 explains precisely what that means
for a node you want to run on another host. Read §14 before designing anything networked — the answer is
not "configure a URL".

---

## 1. Architecture overview

### 1.1 What a node is

A node is a **separate unit of capability** that declares what it can do in a manifest file and is reached
through one of two transports. The core compiles none of your code and launches none of your processes.

Two things are deliberately distinct, and conflating them is the most common design error:

- **`NodePacket`** (`Darci.Nodes/NodePacket.cs`) is the **durable work record** the core owns: a validated
  state machine, an append-only log, a lease, and the ability to park for days. It survives restarts.
- **`NodeInvocation`** (`Darci.Nodes/Contract/NodeInvocation.cs`) is the **transient message** handed to a
  node for one call. It dies at its deadline.

Your node sees invocations. It never sees the work record. From `NodeInvocation`'s own source comment:
collapsing the two "would put a 30-second deadline in front of a human gate that legitimately waits for
days."

### 1.2 The request flow

```
  caller (POST /nodes/invoke, or an internal goal/campaign flow)
      │
      ▼
  INodeRouter ─────────────── NodeRouter.DispatchAsync()
      │                        · persists the packet BEFORE anything is attempted
      │                        · resolves capability → node (or Blocked + a durable gap)
      │                        · catches a thrown node and records Failed
      ▼
  NodeDispatcher ──────────── the ONE dispatch point
      │                        · projects packet → NodeInvocation (deadline from the MANIFEST)
      │                        · opens the ModelCallScope for telemetry attribution
      │                        · emits exactly one NodeTelemetryRecord per invocation
      │                        · folds NodeResult back onto the work record
      ▼
  INodeAdapter
      ├── LegacyPacketNodeAdapter ──► in-process node (same process, sees the packet)
      └── HttpNodeAdapter ─────────► POST /invoke over HTTP to your process
                                        · enforces the deadline client-side
                                        · sends X-Darci-Token
                                        · converts every transport failure into a NodeResult
```

Sources: `Darci.Nodes/NodeRouter.cs`, `Darci.Nodes/Contract/NodeDispatcher.cs`,
`Darci.Nodes/Contract/HttpNodeAdapter.cs`.

### 1.3 The invariant that shapes everything

From `NodeDiscovery`'s class comment: **"Nothing here may prevent the core from starting."**

> §6.5 step 5: "the core never dies because a node died." An absent, slow, lying, or duplicate node is
> skipped with a named reason and the core boots.

The single exception **[ENFORCED]**: a **first-party** manifest that is invalid — one whose `node_id` names
a node compiled into this core — throws `NodeRegistrationException` and the core does not start. That is a
bug in this repository, not a stranger's problem. Your node, as a third party, can never brick the core.

The corollary you must design for: **your absence is a normal, supported state.** Work that needs you is
recorded as *blocked*, not *failed*, and a durable gap is written. See §10.

---

## 2. Operational numbers: timeouts, limits, concurrency

Every figure here was read out of the code, and several of the honest answers are **"nothing enforces
this"** — stated as such rather than replaced with a number that sounds authoritative. They are pinned by
`Darci.Nodes.Tests/Contract/OperationalLimitsTests.cs`, so if a limit ever appears, a test fails.

### 2.1 At a glance

| Question | Answer | Status |
|---|---|---|
| Health-check interval | **There is none.** Checked exactly once, at core startup. | **[ENFORCED]** by absence |
| Health-check timeout | No dedicated one. Shares the **3 s** handshake budget with the manifest fetch. | **[ENFORCED]** |
| Manifest fetch timeout | Same shared **3 s** budget — health *and* manifest together. | **[ENFORCED]** |
| `/invoke` timeout | the capability's **`deadline_ms`**, which is **required** and must be **≤ 180 000 ms**. §2.3 | **[ENFORCED]** |
| Retries on any endpoint | **None, anywhere.** No handshake retry, no invoke retry, no re-discovery. | **[ENFORCED]** by absence |
| Payload size cap | **None in the node contract.** §2.5 | not enforced |
| Max payload slots | **None.** | not enforced |
| Response body size cap | **None** — `ReadAsStringAsync` with no `MaxResponseContentBufferSize`. | not enforced |
| Concurrent `/invoke` calls | **Unbounded, and parallel.** Your node must be safe for concurrent calls. §2.4 | **[ENFORCED]** by absence |
| Rate limit | **None.** | not enforced |

### 2.2 Health and manifest: one 3-second budget, spent once

**[ENFORCED]** `NodeDiscovery` wraps the *entire* handshake — `GET /health` **then** `GET /manifest` — in a
single `CancellationTokenSource.CancelAfter(HandshakeTimeout)`, which `Program.cs` sets to **3 seconds**.
There is no per-endpoint timeout. Both calls together must finish inside it, or you are skipped with
`handshake exceeded 3s`.

**[ENFORCED]** There is no health-check *interval*, because there is no polling loop. Health is read once,
during discovery. A node that becomes unhealthy afterwards stays in the routing table and its failures
surface as invocation errors instead; `SetDegraded` (§4.6) is the only mechanism for removing it.

**[RECOMMENDED]** Answer both in single-digit milliseconds. Do not connect to a database, warm a model, or
read a large file in a health check. The reference node checks only that its kernel imported.

### 2.3 `/invoke`: `deadline_ms` is required, and 180 000 ms is the hard ceiling

**[ENFORCED]** Every capability **must** declare `deadline_ms`, and for an out-of-process node it must be
**1 … 180 000** milliseconds. There is no default and no implicit value.

| `deadline_ms` | Outcome |
|---|---|
| absent | **refused at load**, naming the capability and the maximum |
| `0` or negative | **refused at load** |
| 1 … 180 000 | **accepted — and it means exactly what it says** |
| above 180 000 (out-of-process) | **refused at load**, loudly, with the fix in the message |
| above 180 000 (in-process) | accepted — the ceiling is transport-scoped, see below |

**180 000 ms / 180 s is the absolute system ceiling.** `Program.cs` sets the node HTTP client's timeout
from the same constant that validation checks — `NodeDeadline.Max` / `NodeDeadline.MaxMs` in
`Darci.Nodes/Contract/NodeContract.cs` — so the two numbers cannot drift apart. Within that ceiling, the
real budget is **yours**, declared capability by capability, and `HttpNodeAdapter`'s
`cts.CancelAfter(DeadlineAt - UtcNow)` remains the per-invocation authority.

So, plainly: **the effective budget for an invocation is your `deadline_ms`.** There is no hidden cap
underneath the ceiling, and nothing is silently clipped.

What a refusal looks like, as it appears in the boot log and in `GET /nodes` → `skipped`:

```
capability 'acme.work' declares deadline_ms 900000, above the maximum 180000 for an out-of-process
node. The core's node HTTP client times out at 180000ms, so a longer deadline could not be honoured.
Lower deadline_ms to 180000 or less, or make the work asynchronous (return `blocked` with
`pending-outcome` and let the caller collect the result).
```

and for an absent one:

```
capability 'acme.work' does not declare deadline_ms, which is required (a positive number of
milliseconds, at most 180000).
```

> **Why it is refused rather than clipped.** This used to be a trap. The client inherited the .NET default
> of 100 s while a manifest could declare any budget it liked, so the excess was truncated at runtime and
> the overrun reported as `DEADLINE_EXCEEDED` — indistinguishable from the node simply being slow.
> `darci.modeling` declared 120 000 and quietly lost its last 20 seconds. A limit you cannot see is worse
> than a smaller limit you can, so the ceiling is now explicit, shared with the validator, and enforced
> **at load**.

**The ceiling is scoped to out-of-process nodes**, deliberately. It exists because an HTTP node is called
through a client whose timeout is `NodeDeadline.Max`; an **in-process** node touches no HTTP client, and
the first-party nodes legitimately declare far longer (`coding.write` 3 600 000, `knowledge.answer`
900 000). Applying the ceiling to them would stop the core booting — a first-party manifest failing
validation is fatal by design — over a bound that does not exist for that transport. As a node author
writing an out-of-process node, **the ceiling applies to you**.

**[RECOMMENDED]** Declare a real worst case, not the ceiling. `deadline_ms` is how long the core will wait
before abandoning your call, so padding it to 180 000 "to be safe" just means a hung node ties up a
caller for three minutes. The reference node asks for 120 000 to build a solid; the example in §15 asks
for 15 000 to count words.

**[RECOMMENDED]** If the work genuinely cannot finish inside 180 s, it is not a synchronous invocation.
Accept the request, return `blocked` with `pending-outcome`, and let the caller collect the result —
rather than trying to hold an HTTP connection open past the ceiling.

Pinned by `DeadlineContractTests` (required, the ceiling, the transport scoping, and every real manifest
in the repo) and `OperationalLimitsTests.TheCeilingIsOneConstant_SharedByTheClientAndTheValidator`.

### 2.4 Concurrency: `/invoke` is **not** serialized

**This is a correctness requirement, not a performance note.**

**[ENFORCED]** by absence: there is no lock, semaphore, queue, or max-in-flight anywhere in the dispatch
path. `NodeRouter`, `NodeDispatcher` and `HttpNodeAdapter` contain no synchronization primitives at all
(`NodeRegistry` locks, but only around its own table). Several callers can therefore be inside
`DispatchAsync` at once — concurrent `POST /nodes/invoke` requests, or internal flows dispatching in
parallel — and each becomes a concurrent `POST /invoke` to **your** node.

**Your node must be safe for concurrent `/invoke` calls.** Verified two ways: at the adapter level, eight
invocations held open simultaneously by a stub that only releases once all eight have arrived
(`InvocationsRunInParallel_SoANodeMustBeSafeForConcurrentCalls`); and end to end, eight parallel
`POST /nodes/invoke` calls against a live core and node, all returning `Succeeded`.

**[REQUIRED]** Concretely, this means:

- **No per-request global state.** Module-level mutables shared across requests are a race.
- **Do not assume one call finishes before the next starts.** They interleave.
- **Writing to a fixed output path is a race** — two invocations with different inputs will clobber each
  other. Derive the filename from the input (a hash is ideal), or accept a name slot and document that
  reusing one overwrites.
- **Guard any non-reentrant library.** Many CAD, ML and rendering libraries are not thread-safe. If yours
  is not, serialize access yourself with a lock or a single-worker queue inside the node — the core will
  not do it for you.
- **Mind your own server's worker model.** `uvicorn` with one worker runs async handlers concurrently on
  one thread, so a synchronous CPU-bound handler blocks every other request; `def` handlers in FastAPI run
  in a threadpool instead, which makes them genuinely parallel and therefore genuinely exposed to races.

**[RECOMMENDED]** The simplest safe design is a **stateless** node: everything it needs comes from the
payload, nothing is cached between calls. The complete example in §15 is stateless for this reason.

### 2.5 Sizes: nothing in the contract caps a payload

**not enforced.** No code in `Darci.Nodes` checks payload size, slot count, or slot length, in either
direction. A 4 MB slot round-trips intact (`NothingInTheNodeContract_CapsThePayloadSize`).

The only real limits sit outside the contract:

| Limit | Value | Where |
|---|---|---|
| Core's inbound body (`POST /nodes/invoke`) | Kestrel's default **~30 MB** | not configured anywhere, so the framework default stands |
| Your node's inbound body | whatever **your** server imposes | FastAPI/Starlette does not cap it by default |
| Core reading your response | **unbounded** | `ReadAsStringAsync`, no buffer limit set |

**[RECOMMENDED]** Treat the payload as a control channel, not a data channel. Pass a **path or a handle**
to anything large (§11.1) rather than the bytes, keep slots small, and remember that an unbounded response
is read fully into memory by the core before it is parsed — a node returning hundreds of megabytes will
hurt the core, and nothing will stop it.

### 2.6 What this means if you are tuning

The one knob that is yours is `deadline_ms`, anywhere in 1 … 180 000 ms, and it now means what it says.
The 3-second handshake budget and the 180-second ceiling are **hardcoded** — the budget in `Program.cs`,
the ceiling in `NodeDeadline` — with no environment variable for either, so a node author cannot change
them; both are noted in §14 as things a remote transport would have to revisit.

---

## 3. Node kinds and transports

### 3.1 Kinds — `NodeKind` (`Darci.Nodes/Contract/NodeManifest.cs`)

Serialized as a camelCase string by `JsonStringEnumConverter`:

| `kind` | Meaning | Extra requirement |
|---|---|---|
| `capability` | Does work. The default and almost certainly what you want. | — |
| `environment` | Owns durable workspace state. | **[ENFORCED]** must declare `workspace_root`, else validation fails |
| `adapter` | Declared in the enum; no distinct handling in code today. | — |

### 3.2 Transports

Determined by **`endpoint`** alone (`NodeManifest.IsInProcess => string.IsNullOrWhiteSpace(Endpoint)`):

| | In-process | Out-of-process (HTTP) |
|---|---|---|
| `endpoint` | absent / null / empty | an HTTP base URL |
| Adapter | `LegacyPacketNodeAdapter` | `HttpNodeAdapter` |
| Who writes it | Tinman, compiled into the core | **you** |
| Sees the work record | yes, via the `PacketRef` side-channel | **never** — `[JsonIgnore]`, cannot cross a process |
| Moves the packet itself | yes, it transitions and returns the packet | no, it returns a payload and the dispatcher owns the state machine |
| Health-checked | no | yes, at discovery |
| Manifest verified | no | yes, SHA-compared |

**[ENFORCED]** A manifest whose `node_id` matches a compiled-in node but which declares an `endpoint` is
**skipped**: `"manifest declares endpoint '…' but the node is compiled in"`. Ambiguity about which one
serves is refused rather than guessed.

Everything from §4 onward is written for the out-of-process case, because that is the one collaborators
write. The `PacketRef` side-channel is explicitly transitional and is documented in its own source as
"REMOVE when nodes are de-legacied" — do not design against it.

---

## 4. Discovery and registration

`Darci.Nodes/Contract/NodeDiscovery.cs`, `Darci.Nodes/Contract/NodeManifestLoader.cs`, wired in
`Darci.Api/Program.cs`.

### 4.1 Where the core looks

**[ENFORCED]** `DARCI_NODES_PATH`, else `<ContentRoot>/../nodes` — resolved to a full path at startup.

**[ENFORCED]** The scan is `Directory.EnumerateFiles(dir, "darci-node.json", SearchOption.AllDirectories)`,
ordered by ordinal path. So it is **fully recursive** at any depth, and the filename must be exactly
`darci-node.json` (`NodeManifestLoader.ManifestFileName`).

> **[DRIFT]** `NodeManifestLoader.LoadAll`'s own doc comment says "one level of subdirectories, plus the
> directory itself". The code passes `SearchOption.AllDirectories`. The code wins: nesting is unlimited.

**[RECOMMENDED]** One directory per node, named for the `node_id`: `nodes/acme.summarize/darci-node.json`.

### 4.2 There is no runtime self-registration

**[ENFORCED]** by absence. A node cannot talk its way into the routing table. The capability surface comes
only from manifest files already on disk at startup. Per `NodeManifestLoader`'s class comment, the
human-authored act required by §15c **is the reviewed manifest merged into the repo**. If you add a
capability, somebody reviews a diff. There is no API for it.

### 4.3 Registration order and capability ownership

**[ENFORCED]** In-process nodes register **first**, deliberately — "first-party wins a capability collision,
and 'first' is only meaningful if it actually goes first."

**[ENFORCED]** One capability has exactly **one** owner (`NodeRegistry.Register`). A second claimant throws,
and the error text suggests a fix:

```
Capability 'model.build' is already served by node 'darci.cad'; node 'acme.modeling' cannot also
claim it. Give this node its own namespaced verb (e.g. 'modeling.build') instead.
```

Discovery converts that throw into a **skip** for third parties. A `priority` field is named in the
interface comment as the future mechanism for deliberate overrides; it is **not implemented**.

**[ENFORCED]** `node_id` must also be unique: `"Node id 'x.y' is already registered."`

### 4.4 The handshake — out-of-process only

`NodeDiscovery.HandshakeAsync`. Two steps, in order, inside **one** budget:

1. **Health.** `adapter.IsHealthyAsync()`. Failure → `"health check failed (node not running or not ready)"`.
2. **Manifest match.** `GET /manifest`, parse, hash, compare to the on-disk hash.
   - unreachable or unparseable → `"/manifest was unreachable or unparseable"`
   - parsed but different → `"/manifest does not match the on-disk file (disk 9D6EBFE02519, node B7E20F4A30E0)"`

**[ENFORCED]** The whole handshake has a single bounded budget: `NodeDiscoveryOptions.HandshakeTimeout`,
set to **3 seconds** in `Program.cs`. Exceeding it → `"handshake exceeded 3s"`. The comment explains why it
is one budget and why it is spent once: "Startup network I/O is how a core acquires a hang… A node that is
merely slow must cost this much and no more, and it must cost it ONCE."

**Practical consequence [REQUIRED]:** your `/health` and `/manifest` must answer in well under 3 seconds,
**combined**, from a cold start. Do not do expensive work in them. The reference node checks only that its
kernel imports.

**[ENFORCED]** There are **no retries** and no re-discovery. Discovery runs once, at core startup.

#### Do I have to restart the core?

The question every author hits within ten minutes, answered directly. **[ENFORCED]** in every row:

| What changed | Restart the node? | Restart the core? | Why |
|---|---|---|---|
| You started a **new** node | — | **Yes** | Discovery runs once at boot. A node that appears later is never scanned for, health-checked, or registered. |
| You added a **new `darci-node.json`** | — | **Yes** | The `nodes/` directory is scanned once, at startup. |
| You **edited** your manifest | **Yes** | **Yes** | The node serves the bytes it read at *its* startup, so it must restart to pick up the file; the core must restart to re-read the file and re-hash. Restarting only one gives you a SHA mismatch and a refused registration. |
| You changed your node's **code** but not its manifest | **Yes** | **No** | The core holds an address, not your code. Its registration is still valid, and the next `/invoke` reaches the new code. |
| Your node **crashed and you restarted it** | (already done) | **No**, if the manifest is unchanged | The registration survives; the core will simply reach the new process at the same address. The invocations that landed while it was down failed, and nothing retries them. |
| You want a node **removed** from routing | — | **No** | `SetDegraded` (§4.6) takes its capabilities out of the table while the core keeps running. |

**The order that works:** node first, then core. **[RECOMMENDED]** for a manifest edit, do all three in
sequence — edit the file, restart the node, restart the core — because any other order produces either a
stale registration or a hash mismatch.

There is no `/nodes/rediscover`, no file watcher, and no `SIGHUP` handler. The one dynamic lever that
exists is `SetDegraded`, and it only ever *removes*.

### 4.5 Every possible skip reason

The exact strings that appear in the boot log and in `GET /nodes` → `skipped`:

| Reason | Cause |
|---|---|
| `compiled in, but no manifest on disk` | in-process node with no `darci-node.json` |
| `manifest declares endpoint '…' but the node is compiled in` | §3.2 |
| `endpoint '…' is not loopback (remote nodes need the deferred broker-token work)` | §14 |
| `health check failed (node not running or not ready)` | not running, or not reporting `ok` |
| `/manifest was unreachable or unparseable` | 404/500, empty body, bad JSON |
| `/manifest does not match the on-disk file (disk …, node …)` | §6.4 |
| `handshake exceeded 3s` | too slow |
| `Capability '…' is already served by node '…'` | §4.3 |
| `Node id '…' is already registered.` | §4.3 |
| *(the loader's own message)* | malformed or invalid manifest, from a third party |

**[ENFORCED]** Nothing is ever skipped silently. Each one is a `LogWarning` and appears in `GET /nodes`.

### 4.6 Degradation after startup

**[ENFORCED]** `INodeRegistry.SetDegraded(nodeId, true)` removes a node's capabilities from the routing
table while the core keeps running. `Resolve` and `ResolveNode` both return `null` for a degraded node, so
requests for its capabilities become *blocked* exactly as if it had never registered.

---

## 5. The capability surface

### 5.1 Capability names — **[ENFORCED]** `CapabilityKey.IsValidName`

A capability name must be namespaced `domain.action`:

- at least **two** dot-separated segments;
- every segment non-empty;
- every segment **starts with a lowercase letter** `a–z`;
- every character is `a–z`, `0–9`, or `_`.

| Name | Valid | Why |
|---|---|---|
| `model.build` | ✅ | |
| `acme.summarize_text` | ✅ | underscores allowed |
| `a.b.c` | ✅ | more than two segments is fine |
| `Model.Build` | ❌ | uppercase |
| `model-build` | ❌ | no dot; hyphen not allowed |
| `build` | ❌ | not namespaced |
| `model.2d` | ❌ | segment starts with a digit |
| `model..build` | ❌ | empty segment |

The same validator guards `POST /nodes/invoke`, so an invalid name is a `400` there too.

**Important:** you are **not** limited to the core's built-in list. `Capabilities.BuiltIn` exists only to
document what ships; the routing key is a string from a manifest. This is the whole point of the design —
"a collaborator's node can declare a capability this core has never heard of and be routed to, with no core
recompile."

**[RECOMMENDED]** Namespace by your node, not by a generic domain, so you never collide with a first-party
verb you do not control. `acme.summarize` is safer than `summary.write`.

### 5.2 Node ids — **[ENFORCED]** `NodeManifest.Validate`

Weaker than capability names. Only two checks: non-empty, and **contains a `.`**. The error text suggests
reverse-domain style (`'darci.coding'`). Case and character set are unchecked.

**[REQUIRED]** `node_id` must be unique and **immutable across versions** (`NodeKeys`' own comment). The
audit trail in §6.5 is keyed on it; changing it reads as a different node.

---

## 6. The manifest contract

File: `darci-node.json`. Model: `NodeManifest` in `Darci.Nodes/Contract/NodeManifest.cs`.
Deserialized with `ManifestJson.Options`: snake_case via explicit attributes, enums as camelCase strings,
nulls omitted on write.

### 6.1 Full field reference

Top level:

| Field | Type | Status | Notes |
|---|---|---|---|
| `contract_version` | string | **[ENFORCED]** | Must be `"0.1"` or `"0.1.1"` (`NodeContractVersion.Supported`). Anything else fails validation with the supported list named. Defaults to `0.1.1` if absent. |
| `node_id` | string | **[ENFORCED]** | Required, must contain `.`. §5.2 |
| `display_name` | string | **[RECOMMENDED]** | Never validated. Shown in logs. |
| `node_version` | string | **[ENFORCED]** | Required, non-empty. Format unchecked; **[RECOMMENDED]** semver. |
| `kind` | enum string | **[ENFORCED]** | `capability` \| `environment` \| `adapter`. Defaults to `capability`. `environment` additionally requires `workspace_root`. |
| `endpoint` | string \| null | **[ENFORCED]** | Absent ⇒ in-process. Present ⇒ must be **loopback** (§14). |
| `capabilities` | array | **[ENFORCED]** | **At least one.** `"node 'x' declares no capabilities."` |
| `requires` | object | **[RESERVED]** | Real inventory, enforced by nothing. §6.3 |
| `health` | string | **[ENFORCED]** | Health path. Defaults to `/health`. Empty/whitespace falls back to `/health` at call time. |
| `author` | string \| null | **[RECOMMENDED]** | |
| `repository` | string \| null | **[RECOMMENDED]** | |
| `workspace_root` | string \| null | **[ENFORCED]** *(conditional)* | Required iff `kind` is `environment`. |
| `max_disk_mb` | int \| null | **[RESERVED]** | Parsed, never read. |
| `max_runtime_s` | int \| null | **[RESERVED]** | Parsed, never read. Not a substitute for `deadline_ms`. |
| `network` | string \| null | **[RESERVED]** | Parsed, never read. |

Each entry in `capabilities[]` — `NodeCapabilityDescriptor`:

| Field | Type | Status | Notes |
|---|---|---|---|
| `name` | string | **[ENFORCED]** | §5.1. Also **[ENFORCED]** unique within the manifest: `"capability 'x' is declared more than once by node 'y'."` |
| `description` | string | **[REQUIRED]** | Not validated, but this is what a human or an agent reads to decide whether to call you. |
| `input_schema` | JSON | **[REQUIRED]** | Carried as a raw document. **Schema enforcement is a later pass** — the source says so explicitly. Declaring it is what "lets a coding agent write against the contract". See §6.4 for its effect on hashing. |
| `output_schema` | JSON | **[REQUIRED]** | Same. |
| `typical_latency_ms` | int | **[RECOMMENDED]** | Defaults to `1000`. Read by nothing; documentation for callers. |
| `deadline_ms` | int | **[ENFORCED]** | **REQUIRED — no default.** Must be **> 0**, and for an out-of-process node **≤ 180 000** (`NodeDeadline.MaxMs`). Absent, zero, negative, or above the ceiling is refused at load with the capability and the bound named. §2.3 |

### 6.2 `deadline_ms` — the binding deadline comes from the manifest

This is the single most important number in your manifest.

**[ENFORCED]** `NodeDispatcher.Project` reads the descriptor for the resolved capability and sets
`DeadlineAt = DateTime.UtcNow.AddMilliseconds(descriptor.DeadlineMs)`. Validation guarantees every
declared capability has one (§2.3), so the fallback applies only when a packet was routed by explicit node
**address** and its capability string matches nothing the node declares — in which case there is no
declared budget and it takes the **ceiling**, 180 000 ms.

**[ENFORCED]** `HttpNodeAdapter.InvokeAsync` then enforces it from the core's side:

- if `DeadlineAt` has already passed before the call, it returns `DEADLINE_EXCEEDED` **without contacting
  you at all**;
- otherwise it computes `remaining = DeadlineAt - UtcNow` and does `cts.CancelAfter(remaining)`;
- when that fires, the result is `DEADLINE_EXCEEDED` — *"Node … did not respond before the invocation
  deadline."* — and the core stops waiting whatever you are doing.

**[ENFORCED] The caller cannot change it.** `POST /nodes/invoke` has **no deadline parameter**. From its
source comment: "The binding deadline is the one the capability's manifest declares, which is the reviewed
figure; letting an HTTP caller pass their own would quietly move a limit that exists to be reviewed."

**[ENFORCED] The deadline has no relationship to the work record's lease.** The dispatcher's "C3 firewall"
comment: it "does not park, unpark, or abort a work record, and an invocation's `DeadlineAt` has no
relationship to the record's lease."

**[RECOMMENDED]** Set it to a real worst case — not the ceiling — and check it yourself (§8.3) so you do
not start work you can never deliver.

### 6.3 `requires` — honest inventory, enforced by nothing

**[RESERVED]** in its entirety. `NodeRequires`' own comment: "populated as REAL INVENTORY in Phase 1 even
though nothing enforces it yet — this inventory is the design input for the memory/model brokers."

| Field | Populate with |
|---|---|
| `model_classes` | Model **classes**, never model names — e.g. `chat.balanced`, `code.generate`. Naming a concrete model is a contract violation even though nothing checks it. |
| `memory_scopes` | e.g. `read:documents`, `write:innovated` |
| `permissions` | e.g. `write:workspace-files` |
| `emits_untrusted` | `true` if **any** output can carry content you did not author |

**[REQUIRED]** Get `emits_untrusted` right. It propagates, and the enforcement pass is designed to be a
behaviour change in one place rather than a schema change everywhere. Geometry you computed from the
caller's own numbers is not untrusted. Scraped text, a model's words, or a third-party API response is.

**There is no broker callback yet.** `BrokerRef` is explicitly "RESERVED, NO-OP"; the envelope's
`broker.url` and `broker.token` are always `null`. A node therefore **cannot ask the core for inference
today**. Calling a model provider directly from your node bypasses the broker, which is the thing the
broker exists to prevent — so a node needing inference is a node that cannot be finished yet. Declare the
class and return `blocked` / `missing-environment`, or be deterministic.

### 6.4 The manifest hash — what is actually compared

**The rule, in one sentence.** **[ENFORCED]** The core parses both your `/manifest` body and the file on
disk, re-serializes each canonically with `ManifestJson.Options`, and compares the SHA-256 of the two — so
whitespace, the order of top-level keys and unknown extra fields are all irrelevant, but the key order
*inside* `input_schema` and `output_schema` is **not**, because those are carried as raw JSON documents
and round-trip verbatim.

Everything below is the derivation and the consequences. If you only take one instruction from this
section: **read the file's bytes at startup and serve them unmodified**, which makes the whole question
moot.

**What the code does.** Both sides compute `NodeManifest.ComputeSha256()`:

```csharp
var json = JsonSerializer.Serialize(this, ManifestJson.Options);   // the PARSED model, re-serialized
return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
```

- the **disk** side: `NodeManifestLoader.Load` parses the file, validates it, and stores
  `manifest.ComputeSha256()`;
- the **node** side: `HttpNodeAdapter.FetchManifestAsync` parses your `/manifest` body **with the same
  `ManifestJson.Options`** and the handshake calls `ComputeSha256()` on that.

So the comparison is **semantic equality of the parsed manifest**, not of the file bytes.

> **[DRIFT], now fixed.** `nodes/README.md` used to say the manifest must match "byte for byte", which is
> the safe *practice* rather than the enforced *rule*. Both documents now lead with the rule above.

What this means concretely:

| Difference between your `/manifest` body and the file | Causes a mismatch? |
|---|---|
| Indentation, newlines, trailing whitespace | **No** |
| Key order of **top-level** manifest fields | **No** |
| Unknown / extra fields not in `NodeManifest` | **No** — dropped on both sides by deserialization |
| A field present on one side and absent on the other, where the default differs | **Yes** |
| Any differing value — version, endpoint, a capability, a `deadline_ms` | **Yes** |
| Key order **inside** `input_schema` / `output_schema` | **Yes** — these are `JsonElement?` and round-trip verbatim |

That last row is why the practice still matters.

**[RECOMMENDED], and strongly:** read the file's bytes once at startup and serve them unmodified.

```python
MANIFEST_BYTES = (NODE_DIR / "darci-node.json").read_bytes()

@app.get("/manifest")
def manifest():
    return Response(content=MANIFEST_BYTES, media_type="application/json")
```

Rebuilding the JSON from a dict, a struct, or a template is how nodes fail this check — it is easy to
preserve every value and still reorder a key inside `input_schema`.

**[ENFORCED]** You hold the bytes you read at startup, so **changing the manifest means restarting the
node**. Edit the file, restart the node, restart the core — in that order.

**Why it is enforced at all.** The on-disk manifest is the reviewed capability grant. A node reporting
something different "is claiming a surface nobody approved, which is the one thing static discovery exists
to prevent."

One subtlety worth knowing: `FetchManifestAsync` deliberately uses `ManifestJson.Options` rather than the
envelope's options, and its comment records why — without the string-enum converter, a perfectly valid
`"kind": "capability"` fails to parse and the node is silently skipped as unparseable. That cost a live
boot once.

### 6.5 §15c — the "capability surface CHANGED" audit

`Darci.Nodes/Contract/SqliteNodeRegistrationStore.cs`, driven from `Program.cs` at startup.

**[ENFORCED]** After the registry resolves, the core records a `NodeRegistrationRecord` for **every**
registered node: `node_id`, `node_version`, `contract_version`, `manifest_sha256`, the capability list, and
`registered_at`.

**[ENFORCED]** If the stored hash for that `node_id` differs from the previous one, the record is marked
`SurfaceChanged` and the core logs a **warning**:

```
Capability surface CHANGED for node darci.modeling: manifest sha256 4996E778… → DA590805….
This should correspond to a reviewed manifest change (Phase E §15c).
```

It is an **audit signal, not a gate** — nothing is blocked, and nobody is asked to approve. The point is
that widening what DARCI can do leaves a durable, after-the-fact trace. Expect this line every time you
legitimately change your manifest; it is informational.

---

## 7. Endpoints your node must expose

`Darci.Nodes/Contract/HttpNodeAdapter.cs`. **[ENFORCED]** These three are the entire surface. The core
calls nothing else, ever.

| Method | Path | Purpose |
|---|---|---|
| `GET` | `/health` *(or `health` from the manifest)* | ready to serve |
| `GET` | `/manifest` | your manifest, verbatim |
| `POST` | `/invoke` | do one unit of work |

Paths are joined as `endpoint.TrimEnd('/') + "/" + path.TrimStart('/')`, so `/manifest` and `/invoke` are
always at the base URL's root. Only the health path is configurable.

### 7.1 `GET /health`

**[ENFORCED]** All of the following must hold for you to be considered healthy:

1. a **2xx** status;
2. a **non-empty** body;
3. parseable as JSON;
4. a `status` field equal to `"ok"`, compared **case-insensitively**.

Anything else — any non-2xx, an empty body, unparseable JSON, or `status` of anything other than `ok` — is
**not healthy**, and `IsHealthyAsync` also returns `false` on any thrown exception.

```json
{ "status": "ok" }
```

**[RECOMMENDED]** Report *able to serve*, not merely *running*, and use a `200` with a different status to
say "up but not ready" — which is exactly what the adapter's comment describes as the right reading. The
reference node does this:

```json
{ "status": "degraded", "detail": "the CadQuery kernel failed to import" }
```

Extra fields are ignored; the reference node also returns `node_id` and `node_version`.

### 7.2 `GET /manifest`

**[ENFORCED]** 2xx with a body that parses as a `NodeManifest`, whose canonical hash equals the on-disk
file's — see §6.4 for exactly what that compares, and note that **it is not a byte comparison**, though
serving the bytes is the reliable way to satisfy it.

### 7.3 `POST /invoke`

**[ENFORCED]** Request body: the invocation envelope of §8. Response: §9.

**[ENFORCED]** Status handling, from the core's side:

| Your response | How the core reads it |
|---|---|
| 2xx + valid result envelope | the outcome you declared |
| 2xx + empty or unparseable body | `INTERNAL`: *"returned an empty or unparseable /invoke response"* |
| **any non-2xx** | `DEPENDENCY_UNAVAILABLE`: *"Node … returned HTTP 401 from /invoke."* |
| no response before the deadline | `DEADLINE_EXCEEDED` |
| connection refused / DNS / reset | `DEPENDENCY_UNAVAILABLE`: *"Node … was unreachable: …"* |

**[REQUIRED]** Return `200` with an **error envelope** for anything that is an *outcome of the work*. A
non-2xx tells the core only that you are broken, and it collapses to `DEPENDENCY_UNAVAILABLE` — the caller
learns nothing about what to change. Reserve non-2xx for refusing to start at all; the reference node uses
exactly one, a `401` for a bad token.

### 7.4 Authentication — `X-Darci-Token`

**[ENFORCED]** `HttpNodeAdapter` sends `X-Darci-Token` on **every** `/invoke` when a secret is configured
(it always is — see below). It is **not** sent on `/health` or `/manifest`.

Two postures, and `Program.cs` logs which one is live:

| `DARCI_NODE_SHARED_SECRET` | Core sends | Your node can |
|---|---|---|
| **unset** (default) | a fresh random 16-byte hex value, minted per boot | only observe that *some* token arrived — it cannot verify it |
| **set, same value both sides** | that value | genuinely verify |

The reason the default is useless is structural, not an oversight: per decision D3 **the core does not
launch nodes**, so a secret minted at core startup is one your already-running process could never have
learned.

**[REQUIRED]** Accept the call when **you** have no secret configured; verify when you do. Refusing with no
secret configured would make your node unusable in the default setup rather than secure.

```python
def _check_token(token: str | None) -> bool:
    if not SHARED_SECRET:
        return True
    return token is not None and hmac.compare_digest(token, SHARED_SECRET)
```

**[RECOMMENDED]** Use a constant-time comparison, and return `401` with a plain JSON body rather than a
result envelope — this is a refusal to start work, not an outcome of it.

---

## 8. The invocation envelope (request)

`NodeInvocation` → `HttpNodeAdapter.WireInvocation`. **[ENFORCED]** JSON naming is
`JsonNamingPolicy.SnakeCaseLower` on write, and **case-insensitive** on read. Nulls are omitted.

### 8.1 Exact shape

```json
{
  "envelope_version": "0.1.1",
  "trace_id": "9f2c1b7e4a5d4c0e8b3f6a1d2e5c7b90",
  "goal_id": "1c51d543625644fba6e0ecef2cca5972",
  "capability": "model.build",
  "issued_at": "2026-11-02T20:07:11.4821933Z",
  "deadline_at": "2026-11-02T20:09:11.4821933Z",
  "principal": { "trust": "operator", "id": "tinman" },
  "taint": { "level": "clean", "sources": [] },
  "broker": { "url": null, "token": null },
  "intent": "DN80 flange for the test rig",
  "success_criteria": null,
  "session_id": null,
  "payload": { "spec": "{\"shape\":\"flange\"}", "name": "demo-flange" }
}
```

### 8.2 Field semantics

| Field | Status | What you must know |
|---|---|---|
| `envelope_version` | **[ENFORCED]** emitted | Always `NodeContractVersion.Current` = `0.1.1`. |
| `trace_id` | **[ENFORCED]** fresh | A **new** GUID per invocation. **Telemetry only.** Never use it as a correlation or idempotency key. |
| `goal_id` | **[ENFORCED]** | **The correlation root** — the packet's `CorrelationId`. The key the entire evidence loop hangs on. Stable across invocations that belong to one piece of work. |
| `capability` | **[ENFORCED]** | The resolved verb. May be **empty** when the packet was routed by explicit node address without declaring a capability — handle that. |
| `issued_at` | informational | UTC, ISO 8601 with `Z`. |
| `deadline_at` | **[ENFORCED]** | UTC. Binding. §6.2 |
| `principal` | **[RESERVED]** | Always `{"trust":"operator","id":"tinman"}` today. `trust` ∈ `untrusted`, `collaborator`, `operator`, `system`. Carried, not enforced. |
| `taint` | **[RESERVED]** inbound | Always `clean` today. `level` ∈ `clean`, `derived`, `untrusted`. |
| `broker` | **[RESERVED]** | Always `{"url":null,"token":null}`. §6.3 |
| `intent` | **[REQUIRED]** read-only | The originating goal, **never rewritten**. |
| `success_criteria` | optional | May be null. |
| `session_id` | optional | May be null. |
| `payload` | **[ENFORCED]** | §8.4 |

`goal_id` vs `trace_id` is not stylistic. From `NodeInvocation`'s source: using `trace_id` as a correlation
key "silently inerts the outcome-feedback loop (nothing would ever match)."

### 8.3 Checking the deadline yourself

**[RECOMMENDED]** The core enforces the deadline, so you cannot overrun it from the caller's point of view —
but a node that starts a two-minute operation it can never deliver has burned the time for nothing.

```python
def _deadline_passed(deadline_at) -> bool:
    if not isinstance(deadline_at, str) or not deadline_at:
        return False                      # an absent deadline is not an expired one
    try:
        parsed = datetime.fromisoformat(deadline_at.replace("Z", "+00:00"))
    except ValueError:
        return False                      # unparseable is not expired either
    if parsed.tzinfo is None:
        parsed = parsed.replace(tzinfo=timezone.utc)
    return datetime.now(timezone.utc) > parsed
```

**[RECOMMENDED]** Fail **open** on a missing or malformed `deadline_at`, as above. Treating absence as
expiry would make your node refuse every call from a caller that omitted it.

### 8.4 The payload is a flat map of string to string

**[ENFORCED]** In both directions. `IReadOnlyDictionary<string, string>` on the invocation
(`PacketPayload.Slots`) and on the result.

This is deliberate, and the reason is in `NodeInvocation`'s source: the payload is "deliberately kept
trivially JSON-round-trippable (ADD-3) so an in-process node cannot come to depend on a payload shape that
could not survive the out-of-process hop."

| | |
|---|---|
| ✅ | `{"spec": "{\"shape\":\"flange\",\"dimensions_mm\":{...}}"}` — a JSON **string** in one slot |
| ❌ | `{"spec": {"shape": "flange"}}` — a nested object; will not deserialize into the wire DTO |
| ❌ | `{"count": 8}` — a number; send `"8"` |
| ❌ | `{"watertight": true}` — a boolean; send `"true"` |

**[REQUIRED]** Say so in your `input_schema`. Declare every slot as `"type": "string"` and describe the
encoding, rather than documenting an object shape the transport cannot carry. The reference manifest spells
this out in its schema `description`, which is the pattern to copy.

**[RECOMMENDED]** Numbers as decimal strings; booleans as `"true"` / `"false"`; lists as a delimited
string (`"step,stl"`) or JSON-encoded. Keep slot names `snake_case`.

---

## 9. The result envelope (response)

`HttpNodeAdapter.WireResult` → `NodeResult`.

### 9.1 Exact shapes

Success:

```json
{
  "envelope_version": "0.1.1",
  "trace_id": "9f2c1b7e4a5d4c0e8b3f6a1d2e5c7b90",
  "outcome": "ok",
  "taint": { "level": "clean", "sources": [] },
  "confidence": 0.92,
  "payload": { "step_path": "C:/out/part.step", "watertight": "true" }
}
```

Error:

```json
{
  "envelope_version": "0.1.1",
  "trace_id": "…",
  "outcome": "error",
  "taint": { "level": "clean", "sources": [] },
  "error": { "code": "INVALID_INPUT", "message": "spec.shape is required", "retryable": false }
}
```

Blocked:

```json
{
  "envelope_version": "0.1.1",
  "trace_id": "…",
  "outcome": "blocked",
  "taint": { "level": "clean", "sources": [] },
  "dependency": {
    "kind": "missing-environment",
    "detail": "no CAD kernel on this host",
    "reference_id": null
  }
}
```

### 9.2 Field semantics

| Field | Status | What you must know |
|---|---|---|
| `envelope_version` | **[RECOMMENDED]** | If empty/absent the core substitutes `0.1.1`. |
| `trace_id` | **[REQUIRED]**, not trusted | Echo it unchanged. **The core ignores yours and keeps its own** — a mismatch produces only a `LogWarning` saying telemetry correlation for this invocation is unreliable. Echo it anyway; scrambling it means lying in telemetry. |
| `outcome` | **[ENFORCED]** | `ok` \| `error` \| `blocked`, trimmed and lowercased. §9.3 |
| `taint` | **[ENFORCED]** monotonic | `invocation.Taint.RaisedTo(yours)` — you can **raise** it, never lower it. Unrecognized levels fall back to `clean`. Sources: §9.6 |
| `confidence` | optional | A **number** in `[0,1]`. **Omit rather than fabricate** — omission becomes `Confidence.Unassessed`, which is explicitly not zero. |
| `payload` | **[ENFORCED]** | Flat string→string (§8.4). Merged onto the work record slot by slot; absent ⇒ empty. |
| `error` | **[ENFORCED]** *(conditional)* | Read **only** when `outcome` is `error`. §9.4 |
| `dependency` | **[ENFORCED]** *(conditional)* | Read **only** when `outcome` is `blocked`. §9.5 |
| `telemetry_extra` | **[DRIFT]** | `NodeResult` declares it; **no wire DTO carries it and no code reads it.** Sending it is harmless and has no effect. |

### 9.3 `outcome` — unknown words are errors

**[ENFORCED]** `HttpNodeAdapter.ParseOutcome`:

```csharp
"ok"      => NodeOutcome.Ok,
"blocked" => NodeOutcome.Blocked,
_         => NodeOutcome.Error,   // anything unrecognized
```

The comment states the reasoning: "A node speaking a dialect we do not understand has not demonstrated
success." A typo like `"OK "` is fine (trimmed, lowercased); `"success"`, `"done"`, or a missing `outcome`
all become **errors**.

### 9.4 Error codes — **[ENFORCED]** mapping and retry defaults

Wire strings are uppercased and trimmed before matching. **Anything unrecognized becomes `INTERNAL`.**

| `code` | Default `retryable` if you omit it |
|---|---|
| `INVALID_INPUT` | `false` |
| `PERMISSION_DENIED` | `false` |
| `NOT_IMPLEMENTED` | `false` |
| `MODEL_UNAVAILABLE` | `true` |
| `DEPENDENCY_UNAVAILABLE` | `true` |
| `DEADLINE_EXCEEDED` | `true` |
| `INTERNAL` | `true` |

`retryable` is **advisory to the core** — `NodeError`'s comment says "classify honestly". An omitted
`error` object on an `error` outcome becomes `INTERNAL: "Node reported an error with no detail."`

### 9.5 Dependency kinds — **[ENFORCED]** mapping

| `kind` on the wire | Parsed as |
|---|---|
| `human-decision` | `HumanDecision` — a person must approve or decide |
| `missing-environment` | `MissingEnvironment` — nothing here can run this |
| anything else, or absent | `PendingOutcome` — waiting on a real-world result |

Note the hyphens: `human_decision` is **not** recognized and silently becomes `PendingOutcome`. An absent
`dependency` object on a `blocked` outcome becomes
`PendingOutcome: "Node reported blocked with no detail."`

`reference_id` is optional — the id of the thing to watch (a proposal id, a campaign id, a correlation
root). Read by nothing today; **[RECOMMENDED]** to populate when you have one.

---

### 9.6 `taint` — levels, and what a `sources` entry actually is

Security-adjacent, so here is exactly what the code does rather than what the field looks like it does.

**Levels.** **[ENFORCED]** `clean` < `derived` < `untrusted`, parsed case-insensitively after trimming.
**Anything unrecognized becomes `clean`** — including a typo, so `"Untainted"` silently reads as clean.

**Monotonic, and the whole object is replaced.** **[ENFORCED]** `TaintRef.RaisedTo` is one line:

```csharp
public TaintRef RaisedTo(TaintRef other) => other.Level > Level ? other : this;
```

It returns whichever `TaintRef` has the higher level, **entire** — level *and* sources together. Sources
are therefore **never merged**, and the consequence is easy to get wrong:

| Your level vs the request's | Result level | Result sources |
|---|---|---|
| higher | yours | **yours** — the request's are not added |
| equal or lower | the request's | **the request's — yours are discarded entirely** |

Since the request is always `clean` with an empty source list today (§8.2), in practice: raise the level
and your sources are kept; report `clean` and your sources are dropped on the floor. Pinned by
`OperationalLimitsTests.RaisingTheTaintLevel_ReplacesTheSourceList_RatherThanMergingIt` and
`NotRaisingTheLevel_DiscardsTheNodesSourceList_Entirely`.

**What a `sources` entry is.** **not enforced, and not parsed.** The wire type is a plain list of strings
(`WireTaint.Sources`), read as `t.Sources ?? Array.Empty<string>()` and stored. **Nothing in the codebase
reads an individual entry** — not to validate it, not to classify it, not to display it. An empty string,
`"not a url"`, and a padded `"  x  "` all survive verbatim
(`ASourceEntry_IsNeverParsedOrValidated`).

So the format is a **convention this document sets**, not a schema the core checks. Until enforcement
lands, the useful convention is: *one entry per distinct origin of content you did not author*, each
identifying that origin as specifically as you can.

| Kind of origin | **[RECOMMENDED]** entry | Example |
|---|---|---|
| A web page or API you fetched | the **absolute URL**, no credentials or query secrets | `https://example.com/docs/spec.html` |
| A model's output | `model:` + the **class**, never a model name | `model:chat.balanced` |
| A file outside your own node | `file:` + an absolute path | `file:C:/incoming/vendor-drawing.step` |
| Another node's output you passed through | that node's `node_id` | `acme.scraper` |
| A human-supplied free-text blob | `user:` + the principal id | `user:tinman` |

**[RECOMMENDED]** rules of thumb:

- **One entry per origin**, not one per item fetched. Ten pages from one API is one entry if the API is
  the origin; ten different sites is ten entries.
- **Never put a secret in a source.** It is a free-text string that lands in a work record and will be
  read by people and logs. Strip tokens and query credentials from URLs first.
- **Carry forward what you were given.** If you raise the level, the request's sources are *not* merged
  for you (see the table above), so include anything from the inbound `taint.sources` that is still
  relevant to your output.
- **An empty list with a raised level is legal but unhelpful** — it says "something in here is untrusted"
  without saying what. Name the origin.
- **Do not use it as a log channel.** Free-form notes belong in `payload`.

**[REQUIRED]** The level is the part that will eventually be enforced, and it is the part to get right:
`untrusted` for anything you fetched or a model wrote, `derived` for your own transformation of tainted
input, `clean` only when every byte of your output derives from the caller's own payload. The complete
example in §15 is `clean` and says why in a comment — that is the standard to copy.

---

## 10. The state and result contract

### 10.1 The state machine — `NodeState`, `NodeStateMachine`

`Darci.Nodes/NodePrimitives.cs`. Your node does not set these. The dispatcher does, from your `outcome`.
You need to understand them because they determine what happens to the work afterwards.

| State | Ordinal | Terminal | Meaning |
|---|---|---|---|
| `Created` | 0 | no | envelope minted, not routed |
| `Routed` | 1 | no | addressed, awaiting pickup |
| `Accepted` | 2 | no | a node took ownership |
| `Working` | 3 | no | actively processing; **holds a lease** |
| `AwaitingDependency` | 4 | **no** | parked on a dependency; **no lease held** |
| `Succeeded` | 5 | yes | completed and verified |
| `Failed` | 6 | yes | attempted and did not succeed |
| `Aborted` | 7 | yes | reaped by the watchdog / crash recovery / cancellation |
| `Blocked` | 8 | yes | **nothing can do this work**; nothing was attempted |

**[ENFORCED]** Legal transitions (`NodeStateMachine.CanTransition`):

- a terminal state has **no** outgoing transitions — terminal is forever;
- `to == from` is allowed (idempotent rewrite);
- **any** active state may go to `Aborted`, `Failed`, or `Blocked`;
- otherwise: `Created→Routed`, `Routed→Accepted`, `Accepted→Working`,
  `Working→{AwaitingDependency, Succeeded}`, `AwaitingDependency→{Working, Succeeded}`.

`Succeeded` requires having actually worked — you cannot jump to it from `Routed`.

**[ENFORCED]** Three distinct predicates, and the distinction is the point:

```csharp
IsTerminal() => Succeeded or Failed or Aborted or Blocked
IsFailure()  => Failed or Aborted            // Blocked is NOT a failure
IsSuccess()  => Succeeded                    // Blocked is NOT a success
```

From the source: "anything asking 'did this go wrong' must ask THIS, not `!IsSuccess`. Counting Blocked as
failure would feed phantom negative evidence into the confidence and campaign paths."

> The ordinal of `Blocked` is load-bearing. It is appended at **8** because the store's "active" query is
> `state <= AwaitingDependency` (4). Inserting mid-enum would silently reclassify every stored packet.

### 10.2 How your `outcome` maps to a state — `NodeDispatcher.Fold`

| Your `outcome` | Resulting state | Path |
|---|---|---|
| `ok` | `Succeeded` | walks `Routed→Accepted→Working→Succeeded` |
| `error` | `Failed` | direct; log carries `WIRE_CODE: message` |
| `blocked` | `AwaitingDependency` | walks up to `Working`, then parks with your `detail` |

**[ENFORCED]** Your `payload` slots are merged onto the record **before** the state transition, so they are
readable whatever the outcome.

### 10.3 The two meanings of "blocked" — read this twice

The word collides, and the two are different states with different consequences.

| | A **node** reports `blocked` | The **router** finds no node |
|---|---|---|
| Resulting state | `AwaitingDependency` (4) | `Blocked` (8) |
| Terminal | **no** | **yes** |
| Meaning | you were reached, did bounded work, and the *goal* now waits on something | nothing serves this capability at all |
| Writes a gap | no | **yes** — a durable `GapRecord` |
| Who produces it | you | `NodeRouter.DispatchAsync` |

The router's version, verbatim from `NodeRouter`:

```
state: "Blocked", terminal: true
lastDecision: "No node serves this capability — nothing was attempted."
error: "No node matches address=— / capability=model.build."
```

and it writes a durable `GapRecord` with `Question = "No node serves capability 'model.build'."`,
`Missing = "a node providing capability model.build"`, `Status = Open`, keyed on the packet's
`CorrelationId`. Gap recording is **best-effort by design** — a core with no gap store "still degrades
correctly, it just has nowhere to write the note", and a failure to record one never turns a clean
degradation into an exception.

### 10.4 Why `blocked` must never be recorded as `Failed` — the Fork-1 invariant

This is the single most important semantic rule in the contract.

A node reporting `blocked` has **not failed**. It did bounded work and reported honestly that the goal now
depends on something outside the invocation. Recording that as `Failed` injects **phantom negative
evidence** into the confidence and campaign paths — the system learns that something went wrong when
nothing did.

**This was a real bug, and an out-of-process node is what exposed it.** `AwaitingDependency` is reachable
only from `Working`; the dispatcher's blocked branch parked the packet straight from `Routed`, which threw
`InvalidNodeTransitionException`, which the router caught and recorded as `Failed`. In-process nodes hid it
completely because they transition themselves to `Working` before returning. It became reachable the moment
a node ran over HTTP. Fixed in `ab1f258`; the fix walks the packet up the same way the success path does,
and `RemoteDispatchIntegrationTests` pins it.

**What this means for you [REQUIRED]:** when you cannot do the work because of something outside the call,
return `blocked` — not `error`. And never return `ok` with an empty payload to avoid looking broken.

### 10.5 The durability caveat on `blocked` — **[ENFORCED]**, and easy to miss

A packet parked at `AwaitingDependency` holds **no lease** (`NodePacket.ParkAwaitingDependency` sets
`LeaseExpiresAt = null`). The watchdog's startup sweep aborts every **active** packet — and active is
`state <= 4`, which **includes** `AwaitingDependency`.

There is exactly one carve-out (`NodeWatchdog.SweepStartupOrphansAsync`): a packet in `AwaitingDependency`
is spared **only** if `IProposalStore.HasPendingForParkedPacketAsync(packetId)` returns `true` — i.e.
something registered a live pending human proposal for it.

So:

| Your `blocked` kind | With a pending proposal | Without one |
|---|---|---|
| `human-decision` routed through the human gate | survives the restart | — |
| `missing-environment` | — | **`Aborted`** on the next core boot |
| `pending-outcome` | — | **`Aborted`** on the next core boot |

**[REQUIRED]** Do not treat `blocked` as durable parking unless a proposal backs it. For a condition that
is really "this host can never do this", `missing-environment` is still the honest answer — just expect the
packet to be reaped rather than resumed, and put the actionable half somewhere durable.

### 10.6 Failing well — a decision table

| Situation | `outcome` | Detail |
|---|---|---|
| The caller's input is wrong or missing | `error` | `INVALID_INPUT`, `retryable: false`, **name the field** |
| You do not serve the requested capability | `error` | `NOT_IMPLEMENTED`, `retryable: false` |
| Declared but not yet wired | `error` | `NOT_IMPLEMENTED`, `retryable: false` |
| A dependency you need is down but may come back | `error` | `DEPENDENCY_UNAVAILABLE`, `retryable: true` |
| The deadline had already passed on arrival | `error` | `DEADLINE_EXCEEDED`, `retryable: true` |
| Your own bug | `error` | `INTERNAL` — do not dress it up as the caller's fault |
| No environment here can ever run this | `blocked` | `missing-environment` |
| A person must approve | `blocked` | `human-decision` |
| Waiting on a real-world result | `blocked` | `pending-outcome` |
| Bad or missing token | **`401`** | the one legitimate non-2xx |
| Work done, optional extras missing | **`ok`** | report the shortfall in the payload — §11 |

**[RECOMMENDED]** Error messages name the slot and what would fix it: `"spec.shape is required"` beats
`"bad input"`. That string is what reaches the caller in `lastDecision` / `error`.

---

## 11. Outputs, artifacts, and graceful degradation

### 11.1 Where outputs go

There is **no core-managed artifact store**. The payload is string→string, so a node that produces files
returns **paths**, and owns the directory they live in.

**[RECOMMENDED]** The pattern established by the reference node:

```python
NODE_DIR   = Path(__file__).resolve().parent
OUTPUT_DIR = Path(os.getenv("DARCI_MODELING_OUTPUT_DIR") or (NODE_DIR / "output"))
OUTPUT_DIR.mkdir(parents=True, exist_ok=True)
os.environ.setdefault("CAD_OUTPUT_DIR", str(OUTPUT_DIR))   # before importing the engine
```

- a directory **inside your node's own folder**, `output/` by default;
- overridable by **your own** env var;
- created at import time, so a missing directory is never a per-call failure;
- `.gitignore` it;
- return **absolute** paths in the payload.

The `CAD_OUTPUT_DIR` line is the reusable trick: when you wrap an existing library that writes to a
location of its own choosing, **redirect it by setting its environment variable before importing it**,
rather than editing the shared library. That keeps the dependency untouched and the node's outputs inside
the node.

**[RECOMMENDED]** Sanitize any caller-supplied filename down to a bare stem before it reaches a library's
own path handling — belt and braces, since the caller controls the string:

```python
stem    = Path(str(name)).stem                                        # drops directories
cleaned = "".join(c for c in stem if c.isalnum() or c in "-_")        # drops everything else
return cleaned or "model"
```

**[REQUIRED]** Declare the write in `requires.permissions` (e.g. `write:workspace-files`). Nothing enforces
it; it is how a reviewer sees what your node touches.

### 11.2 Graceful degradation — **[REQUIRED]**

**An optional sub-feature must never fail the whole result.**

The reference node renders orthographic previews, which need an OpenGL context a headless box does not
have. The renderer is called inside its own guard and the shortfall is reported **in the payload**:

```python
def _render_count(engine, stl_path) -> str:
    try:
        ...
        return f"{len(rendered)}/{len(views)}"
    except Exception:
        return "0/3 (renderer unavailable)"
```

A live build on this host returns `renders: "0/3"` **alongside a complete model**, both exported files, and
full geometry metadata. From the source comment: "a missing renderer must cost the caller a preview, not a
part."

**[REQUIRED]** The rule generalizes: decide for each sub-feature whether it is essential or a nice-to-have.
Guard every nice-to-have, and report its absence as data rather than as an exception. If the core can only
see `ok` or `error`, a lost thumbnail must not look like a lost part.

### 11.3 Reporting metadata — **[RECOMMENDED]**

Report what you **measured**, not what you were asked for. The reference node returns measured extents,
measured volume, triangle count, `watertight`, `validation_passed`, and a `script_sha256` of the generated
script so a build is reproducible from its inputs.

Conventions that make a payload readable:

| Kind | Encoding | Example |
|---|---|---|
| Path | absolute string | `step_path` |
| Number | decimal string | `volume_mm3: "655102.972"` |
| Boolean | `"true"` / `"false"` | `watertight: "true"` |
| Composite | a documented delimited string | `bbox_mm: "200.0x199.938x58.0"` |
| Partial success | explicit ratio + reason | `renders: "0/3 (renderer unavailable)"` |
| Multiple warnings | one delimited slot | `warnings: "a; b"` |
| Non-fatal measurement failure | a separate slot | `validation_error: "…"` — files still valid |

**[RECOMMENDED]** Declare every slot in `output_schema` and say what the encoding is.

---

## 12. Determinism, idempotency, telemetry, health

### 12.1 Determinism — **[RECOMMENDED]**, and worth a lot

Nothing enforces determinism. It is still the single property that makes a node trustworthy, because it is
what lets a result be reproduced from its inputs.

The reference node states it in its capability description — *"Deterministic: the spec fully determines the
geometry, and no model class is consulted"* — backs it with `model_classes: []`, and returns
`script_sha256` so the derivation is checkable.

**[RECOMMENDED]** If you are deterministic, say so in the description, declare no model classes, and
publish a hash of whatever intermediate you generated. If you are not, say that too — a caller that knows a
node is stochastic can treat its output accordingly.

### 12.2 Idempotency — **[REQUIRED]** awareness, no mechanism

There is **no** idempotency mechanism in the contract. No idempotency key, no de-duplication, no
"already done" response.

- `trace_id` is **fresh per invocation** and must never be used for this.
- `goal_id` is stable across invocations for one piece of work — it is the closest thing to a key, and it
  is what the evidence loop counts by.
- The core **does not** automatically retry a failed invocation; the dispatcher calls the adapter once. But
  `retryable: true` is advisory to the core, and a caller or an outer flow may legitimately call again.

**[RECOMMENDED]** Make repeated invocations with identical input safe. Write outputs to a path derived from
the input rather than appending; if an operation genuinely cannot be repeated safely, say so in the
capability description and consider keying your own side state on `goal_id`.

### 12.3 Telemetry — the core's record cannot be suppressed

**[ENFORCED]** `NodeDispatcher` emits exactly **one** `NodeTelemetryRecord` per invocation, whatever
happens, including when the adapter throws. It carries `trace_id`, `goal_id`, `node_id`, `capability`,
`started_at`, elapsed ms, outcome, confidence, `error_code`, `blocked_on`, taint level, the active host
profile id, and a model roll-up.

You **cannot** turn this off, and you do not need to emit it. It lands in its own database
(`DARCI_TELEMETRY_DB_PATH`, default `telemetry.db` beside the main one) through a non-blocking queue, so a
telemetry disk write can never slow down or take down the dispatch path.

**[DRIFT]** `telemetry_extra` is declared on `NodeResult` and read by nothing; it is not on the wire DTO at
all. For out-of-process nodes it is inert. Put anything you want a human to see in `payload`.

### 12.4 Health expectations — summary

| | |
|---|---|
| **[ENFORCED]** | 2xx + non-empty + parseable JSON + `status == "ok"` (case-insensitive) |
| **[ENFORCED]** | Checked **once**, at core startup, inside the shared 3-second handshake budget |
| **[ENFORCED]** | Not polled afterwards — there is no background health loop |
| **[RECOMMENDED]** | Answer in milliseconds; check only what would actually stop you working |
| **[RECOMMENDED]** | `200` + a non-`ok` status for "up but not ready" |
| **[RECOMMENDED]** | No side effects; expect it to be called by things other than the core |

Because health is checked once, a node that becomes unhealthy **after** registration stays in the routing
table and its failures surface as invocation errors instead. That is the current design, not an oversight:
`SetDegraded` is the mechanism for taking it out.

---

## 13. The core's own endpoints

The core listens on **`http://127.0.0.1:5081`** by default.

### 13.1 `GET /nodes` — what this core can and cannot do

```json
{
  "registered": [
    {
      "nodeId": "darci.modeling",
      "version": "0.1.0",
      "kind": "Capability",
      "transport": "http",
      "endpoint": "http://127.0.0.1:7801",
      "capabilities": ["model.build"],
      "manifestSha256": "DA590805…"
    }
  ],
  "capabilities": ["coding.test", "coding.write", "model.build"],
  "skipped": { "acme.summarize": "health check failed (node not running or not ready)" },
  "summary": { "inProcess": ["darci.coding"], "remote": ["darci.modeling"], "registered": 4 }
}
```

`skipped` is the half that matters when something is wrong: without it, a node that failed its handshake is
simply invisible and the reason lives only in a startup log line that has scrolled away. **This is the
first thing to check** when your node does not appear.

### 13.2 `POST /nodes/invoke` — a thin pass-through

**[ENFORCED]** Request body (`NodeInvokeRequest`):

| Field | Required | Notes |
|---|---|---|
| `capability` | **yes** | `400` if missing or not a valid `domain.action` name |
| `payload` | no | flat string→string; defaults to empty |
| `intent` | no | defaults to `"Direct invocation of <capability>."` |
| `successCriteria` | no | |
| `correlationId` | no | defaults to a new GUID. **Pass the same one** to treat several invocations as one piece of work |

There is deliberately **no deadline parameter** (§6.2).

**[ENFORCED]** Response:

| Field | Meaning |
|---|---|
| `packetId` | the durable work record's id |
| `correlationId` | the correlation root — `goal_id` as your node saw it |
| `state` | `Succeeded` \| `Failed` \| `Blocked` \| `AwaitingDependency` \| … |
| `terminal` | whether the state is terminal |
| `confidence` | the last log entry's score, or **`null`** when unassessed — never the `-1` sentinel |
| `payload` | the merged slots, i.e. your output |
| `lastDecision` | the node's own account of what happened |
| `error` | the error text on a failure, the reason on a blocked packet |

It is a pass-through **on purpose**: the router is the one dispatch point, and re-implementing persistence,
resolution, telemetry, or the blocked-plus-gap behaviour in the endpoint would create a second, divergent
dispatch path.

**[ENFORCED]** Same posture as the rest of the local surface: **loopback-only and unauthenticated.** There
is no caller authentication on the core's HTTP surface.

---

## 14. Local versus online / remote nodes

**Read this before designing anything networked.** The honest summary: **the core will not register a
non-loopback node today, and there is no configuration switch to allow it.**

### 14.1 What is enforced

**[ENFORCED]** `NodeDiscoveryOptions.RequireLoopback` defaults to `true`. `Program.cs` constructs
`NodeDiscoveryOptions` with only `HandshakeTimeout` and `SharedSecret`, so the default stands, and
**no environment variable or config key sets it** — verified by grep: `RequireLoopback` appears only in its
own declaration and the one check that reads it.

A non-loopback endpoint is skipped:

```
endpoint 'https://nodes.example.com' is not loopback
(remote nodes need the deferred broker-token work)
```

**[ENFORCED]** `NodeDiscovery.IsLoopback` accepts an absolute URI where `Uri.IsLoopback` is true, or whose
host is literally `localhost` (case-insensitive). So `http://127.0.0.1:7801`, `http://localhost:7801`, and
`http://[::1]:7801` pass; a LAN address, a hostname, or a public URL does not.

This is a decision, not an omission. From the source: "Phase 3 is local-only by decision; a remote endpoint
needs the real broker-token work, which is deferred. Opt out deliberately, never by accident."

### 14.2 What differs for a genuinely remote node, once it is allowed

Documented so you can design for it now, and so nobody mistakes the current gap for a small one.

**Already true today and portable:**

- the three-endpoint surface, the envelope shapes, and the flat-string payload are transport-agnostic;
- the deadline is already enforced **client-side** by the core (`CancelAfter(remaining)`), so a remote node
  gains nothing and loses nothing there;
- every transport failure is **already** converted to a `NodeResult` rather than an exception, so the
  unreachable-host case is handled;
- the handshake budget is already bounded, so a slow remote host cannot hang the boot.

**What does not survive the hop, and must change:**

| Concern | Local today | What a remote node needs |
|---|---|---|
| **Transport security** | plain HTTP on loopback | TLS, and certificate validation |
| **Authentication** | `X-Darci-Token`, a shared secret, **unverifiable in the default posture** (§7.4) | the real broker-token work: scoped, rotatable, per-node credentials — this is the blocker |
| **Shared filesystem** | paths in the payload work because both processes see one disk | **breaks completely.** §11's `step_path` is meaningless to a caller on another host. Needs content transfer or a shared store the contract does not have |
| **Health** | checked once at startup | a once-at-startup check over a WAN is much weaker; re-checking and `SetDegraded` driving would matter far more |
| **Timeouts** | a 3s handshake is generous on loopback | 3s is tight across a network, and it is **hardcoded** in `Program.cs` |
| **Retries** | none, and rarely needed | transient network faults make the absence of a retry policy a real gap; `retryable` is advisory only |
| **Clock skew** | irrelevant | `deadline_at` is an absolute UTC instant. Two hosts with skewed clocks will disagree about expiry. **[RECOMMENDED]** prefer the core's enforcement and treat your own check as advisory |
| **Taint** | always `clean`, unenforced | a node reached over a network is a far stronger reason to set `emits_untrusted` honestly |
| **Loopback gate** | the check that stops all of this | must become a deliberate opt-in, not a default flip |

### 14.3 How to write a node today that is ready to go remote — **[RECOMMENDED]**

1. **Never assume a shared filesystem.** If your output is a file, also return something transport-safe —
   a size, a hash, a content type. Then "return the bytes instead" is a change in one place.
2. **Keep the payload genuinely flat and small.** It is already the rule (§8.4); over a network it is also
   a cost.
3. **Treat the deadline as the core's to enforce.** Check it advisorily, fail open on absence (§8.3), and
   never build logic that depends on your clock agreeing with the core's.
4. **Make the secret configurable from the environment**, never compiled in, so swapping to a real
   credential is configuration.
5. **Bind to loopback for now.** `--host 127.0.0.1`. Binding `0.0.0.0` does not make you registrable — the
   gate is on the manifest's `endpoint`, not on your bind address — it only widens your exposure.
6. **Be idempotent** (§12.2). Retries are what a network adds first.

### 14.4 A note on "online" nodes that call the internet

Distinct from a *remotely hosted* node: a node running **locally** that reaches out to the internet is
fully supported today, and nothing stops it. Two requirements if you do:

- **[REQUIRED]** set `requires.emits_untrusted: true` — anything you fetched is content you did not author;
- **[REQUIRED]** raise `taint` to `untrusted` on results carrying that content. Taint is monotonic and the
  core will not let you lower it, but it will not raise it for you either.

**[RECOMMENDED]** Declare the reach in `requires.permissions`, keep your own timeouts strictly inside your
`deadline_ms`, and return `DEPENDENCY_UNAVAILABLE` with `retryable: true` when the remote service is down —
not `INTERNAL`.

---

## 15. A complete, runnable node

**This is a whole node, not an excerpt.** Two files, no placeholders, no `...`, nothing left as an
exercise. It satisfies every rule in this document, and it was **verified by running it**: started under
uvicorn, health-checked, manifest-compared byte-for-byte against its own file, registered by a live core,
invoked through `POST /nodes/invoke`, and exercised on every negative path. The transcript is in §15.5.

Copy the two files into `nodes/acme.wordcount/`, run the two commands in §15.4, and you have a working
node. `DocumentedRunnableNodeTests` keeps the manifest below honest against the real loader, validator and
registry, so it cannot drift from the code it describes.

Only dependency: `fastapi` and `uvicorn`.

### 15.1 `nodes/acme.wordcount/darci-node.json`

```json
{
  "contract_version": "0.1.1",
  "node_id": "acme.wordcount",
  "display_name": "Word Count",
  "node_version": "1.0.0",
  "kind": "capability",
  "endpoint": "http://127.0.0.1:7899",
  "health": "/health",
  "capabilities": [
    {
      "name": "acme.wordcount",
      "description": "Count words, lines and characters in a block of text. Deterministic: the input fully determines the output, and no model class is consulted.",
      "input_schema": {
        "type": "object",
        "required": ["text"],
        "description": "Payloads are a flat map of string to string, so every slot is a STRING.",
        "properties": {
          "text": { "type": "string", "description": "The text to measure." },
          "ignore_case": { "type": "string", "description": "'true' or 'false'. Affects unique_words only. Defaults to 'true'." }
        }
      },
      "output_schema": {
        "type": "object",
        "description": "Also flat string to string; counts are decimal strings.",
        "properties": {
          "words": { "type": "string" },
          "lines": { "type": "string" },
          "characters": { "type": "string" },
          "unique_words": { "type": "string" },
          "longest_word": { "type": "string" },
          "input_sha256": { "type": "string", "description": "So a result is reproducible from its input." }
        }
      },
      "typical_latency_ms": 5,
      "deadline_ms": 15000
    }
  ],
  "requires": {
    "model_classes": [],
    "memory_scopes": [],
    "permissions": [],
    "emits_untrusted": false
  },
  "author": "DARCI docs",
  "repository": "https://github.com/ARoskelley/DARCI-Live"
}
```

Why it looks like this:

- `deadline_ms` is **15 000**, comfortably under the 100-second cap of §2.3, so the number here is the
  number that actually applies.
- every slot in both schemas is `"type": "string"`, because the payload is flat string→string (§8.4), and
  the schema `description` says so rather than describing an object the transport cannot carry.
- `model_classes` is empty and the description says *deterministic*, which is a claim `input_sha256`
  lets a caller check (§12.1).
- `emits_untrusted` is `false` because every byte of output derives from the caller's own payload (§9.6).

### 15.2 `nodes/acme.wordcount/node_server.py`

```python
"""acme.wordcount - a complete DARCI capability node (contract 0.1.1).

Run:  python -m uvicorn node_server:app --host 127.0.0.1 --port 7899
Then start the core. Nothing here is a sketch; every rule in NODE_REQUIREMENTS.md is satisfied.
"""
from __future__ import annotations

import hashlib
import hmac
import json
import os
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

from fastapi import FastAPI, Header, Request, Response
from fastapi.responses import JSONResponse

NODE_DIR = Path(__file__).resolve().parent

# Read the BYTES once and serve them unmodified (section 5.4). Rebuilding this JSON is the single
# most common way a node fails its handshake.
MANIFEST_BYTES = (NODE_DIR / "darci-node.json").read_bytes()
MANIFEST = json.loads(MANIFEST_BYTES)

NODE_ID = MANIFEST["node_id"]
CONTRACT_VERSION = MANIFEST["contract_version"]
CAPABILITIES = {c["name"] for c in MANIFEST["capabilities"]}
SHARED_SECRET = os.getenv("DARCI_NODE_SHARED_SECRET") or ""

# No shared mutable state: /invoke can be called in parallel (section 2.4) and this node is
# stateless, which is the easiest way to be safe under concurrency.
app = FastAPI(title=NODE_ID)


@app.get("/health")
def health() -> dict[str, str]:
    """Able to serve, not merely running. Must answer in milliseconds (section 3.4)."""
    return {"status": "ok", "node_id": NODE_ID, "node_version": MANIFEST["node_version"]}


@app.get("/manifest")
def manifest() -> Response:
    return Response(content=MANIFEST_BYTES, media_type="application/json")


@app.post("/invoke")
async def invoke(request: Request, x_darci_token: str | None = Header(default=None)) -> Response:
    # A refusal to START work, not an outcome of it: the one legitimate non-2xx (section 6.3).
    if not _token_ok(x_darci_token):
        return JSONResponse(status_code=401, content={"error": "X-Darci-Token missing or incorrect"})

    try:
        envelope = await request.json()
    except Exception:
        return JSONResponse(status_code=400, content={"error": "body was not JSON"})

    trace_id = str(envelope.get("trace_id") or "")
    capability = str(envelope.get("capability") or "")
    payload: dict[str, str] = envelope.get("payload") or {}

    if capability not in CAPABILITIES:
        return _error(trace_id, "NOT_IMPLEMENTED",
                      f"{NODE_ID} does not serve '{capability}'; it serves {sorted(CAPABILITIES)}.",
                      retryable=False)

    if _deadline_passed(envelope.get("deadline_at")):
        return _error(trace_id, "DEADLINE_EXCEEDED",
                      "The invocation deadline had already passed when the node received it.",
                      retryable=True)

    return _count(trace_id, payload)


def _count(trace_id: str, payload: dict[str, str]) -> Response:
    text = payload.get("text")
    if text is None or text == "":
        # Name the slot. This string is what reaches the caller in `error` (section 9.6).
        return _error(trace_id, "INVALID_INPUT", "payload.text is required and must not be empty.",
                      retryable=False)

    raw = payload.get("ignore_case", "true").strip().lower()
    if raw not in ("true", "false"):
        return _error(trace_id, "INVALID_INPUT",
                      f"ignore_case must be 'true' or 'false', got '{raw}'.", retryable=False)
    ignore_case = raw == "true"

    words = text.split()
    comparable = [w.lower() for w in words] if ignore_case else words

    # Every value is a STRING; counts are decimal strings (section 7.4).
    return _ok(trace_id, {
        "words": str(len(words)),
        "lines": str(len(text.splitlines())),
        "characters": str(len(text)),
        "unique_words": str(len(set(comparable))),
        "longest_word": max(words, key=len) if words else "",
        "input_sha256": hashlib.sha256(text.encode("utf-8")).hexdigest(),
    })


# -- envelope helpers (section 8) ---------------------------------------------------------------

def _ok(trace_id: str, payload: dict[str, str]) -> Response:
    return _envelope(trace_id, "ok", payload=payload)


def _error(trace_id: str, code: str, message: str, *, retryable: bool) -> Response:
    return _envelope(trace_id, "error",
                     error={"code": code, "message": message, "retryable": retryable})


def _blocked(trace_id: str, kind: str, detail: str) -> Response:
    """Unused here - this node has no external dependency - but shown because `blocked` must never
    be reported as `error` (section 9.4). Kinds are hyphenated: human-decision, missing-environment,
    pending-outcome."""
    return _envelope(trace_id, "blocked", dependency={"kind": kind, "detail": detail})


def _envelope(trace_id: str, outcome: str, **extra: Any) -> Response:
    body: dict[str, Any] = {
        "envelope_version": CONTRACT_VERSION,
        "trace_id": trace_id,                       # echoed unchanged
        "outcome": outcome,
        # Clean: this node reads only the caller's own text and no external source, so it introduces
        # no taint. Fetching anything would make this "untrusted" with the fetched URLs as sources.
        "taint": {"level": "clean", "sources": []},
    }
    body.update(extra)
    return JSONResponse(status_code=200, content=body)


def _token_ok(token: str | None) -> bool:
    """Verify when a secret is configured; accept when not - otherwise the node is unusable in the
    default posture rather than secure (section 6.4)."""
    if not SHARED_SECRET:
        return True
    return token is not None and hmac.compare_digest(token, SHARED_SECRET)


def _deadline_passed(deadline_at: Any) -> bool:
    """Advisory: the core enforces this too. Fails OPEN on absent or malformed input (section 7.3)."""
    if not isinstance(deadline_at, str) or not deadline_at:
        return False
    try:
        parsed = datetime.fromisoformat(deadline_at.replace("Z", "+00:00"))
    except ValueError:
        return False
    if parsed.tzinfo is None:
        parsed = parsed.replace(tzinfo=timezone.utc)
    return datetime.now(timezone.utc) > parsed
```

### 15.3 `nodes/acme.wordcount/.gitignore`

```gitignore
__pycache__/
.venv/
output/
```

### 15.4 Run it

```bash
# install (once)
python -m venv .venv
.venv/Scripts/pip install fastapi uvicorn          # POSIX: .venv/bin/pip

# 1. the node FIRST - the core does not launch it (§4.4)
DARCI_NODE_SHARED_SECRET=dev-secret   .venv/Scripts/python -m uvicorn node_server:app --host 127.0.0.1 --port 7899

# 2. then the core, with the SAME secret (§7.4)
DARCI_NODE_SHARED_SECRET=dev-secret dotnet run --project DARCI-v4/Darci.Api
```

### 15.5 What a verified run looks like

Every block below is real output from running exactly these files.

**Registration**, from the core's boot log:

```
Registered node acme.wordcount v1.0.0 (Capability, http://127.0.0.1:7899) serving [acme.wordcount]
  - manifest sha256 99757EDA964F1446.
Registered OUT-OF-PROCESS node acme.wordcount at http://127.0.0.1:7899 serving [acme.wordcount].
Node discovery complete: 0 in-process, 1 out-of-process, 3 skipped.
Node registry ready: 1 node(s) serving [acme.wordcount].
```

**End to end through the core:**

```bash
curl -X POST http://127.0.0.1:5081/nodes/invoke   -H "Content-Type: application/json"   -d '{"capability":"acme.wordcount",
       "payload":{"text":"the quick brown fox
The lazy dog","ignore_case":"true"},
       "intent":"verifying the documented example end to end"}'
```

```json
{
  "packetId": "0bc1093b656f4d3da9a89f2562c7d613",
  "correlationId": "09678b7e46474fc7b4980f1ecd5c86ec",
  "state": "Succeeded",
  "terminal": true,
  "confidence": null,
  "payload": {
    "text": "the quick brown fox
The lazy dog",
    "ignore_case": "true",
    "words": "7",
    "lines": "2",
    "characters": "32",
    "unique_words": "6",
    "longest_word": "quick",
    "input_sha256": "87827fc346d9ad5a19bb950f2b5da1fc5f7e2d1d92788a704865386f45d45432"
  },
  "lastDecision": "completed",
  "error": null
}
```

Two things in that response are worth noticing, because neither is obvious:

- **the response `payload` contains the request slots too** (`text`, `ignore_case`). `NodeDispatcher.Fold`
  merges your output onto the work record's existing slots rather than replacing them, so a caller sees
  input and output together. Do not name an output slot after an input slot unless you mean to overwrite
  it.
- **`confidence` is `null`**, not `0`, because this node omits it — which is correct for a deterministic
  measurement (§9.2: omit rather than fabricate).

**Concurrency** (§2.4) — eight parallel invocations through the live core, all of which succeeded:

```
Succeeded 3    Succeeded 3    Succeeded 3    Succeeded 3
Succeeded 3    Succeeded 3    Succeeded 3    Succeeded 3
```

**Every negative path**, called directly against the node:

| Request | Response |
|---|---|
| missing `payload.text` | `200` · `error` · `INVALID_INPUT` · *"payload.text is required and must not be empty."* |
| `ignore_case: "maybe"` | `200` · `error` · `INVALID_INPUT` · *"ignore_case must be 'true' or 'false', got 'maybe'."* |
| `capability: "acme.nope"` | `200` · `error` · `NOT_IMPLEMENTED` |
| `deadline_at` in 2020 | `200` · `error` · `DEADLINE_EXCEEDED` · `retryable: true` |
| **no** `deadline_at` | `200` · `ok` — absence is **not** expiry (§8.3) |
| `X-Darci-Token: wrong` | **`401`** — the one legitimate non-2xx (§7.3) |

And the manifest check that catches gotcha 1:

```bash
curl -s http://127.0.0.1:7899/manifest > served.json
cmp served.json nodes/acme.wordcount/darci-node.json && echo IDENTICAL
# -> IDENTICAL
```

### 15.6 Testing yours — **[RECOMMENDED]**

Test the **envelope**, not only your logic. The tests that earn their place are the ones that catch a
contract violation:

- `/health` returns exactly `status == "ok"`;
- `/manifest` is **byte-identical** to the file on disk (the cheapest guard against §6.4);
- the manifest validates: contract version supported, capability names valid, `deadline_ms` **present**
  and `> 0`, and — for an out-of-process node — `deadline_ms <= 180000`, which is the enforced ceiling:
  above it the manifest is refused at load rather than clipped at runtime (§2.3);
- `trace_id` is echoed unchanged;
- **every** payload value is a `str`, in every response path;
- a bad input gives `INVALID_INPUT` with a 200, not a 500;
- an unknown capability gives `NOT_IMPLEMENTED`;
- an already-expired `deadline_at` is refused before any work;
- a missing or malformed `deadline_at` is **not** treated as expired;
- a wrong token gives 401 when a secret is set, and the call succeeds when none is;
- two invocations in flight at once do not corrupt each other (§2.4);
- each guarded nice-to-have degrades without failing the result.

`nodes/darci.modeling/tests/` has all of these against a more substantial node if you want a template.

---

## 16. Author checklist

Manifest:

- [ ] file is exactly `darci-node.json`, under the nodes directory
- [ ] `contract_version` is `"0.1.1"` (or `"0.1"`)
- [ ] `node_id` is namespaced, unique, and will not change between versions
- [ ] at least one capability, each a valid `domain.action` (§5.1), each unique in the manifest
- [ ] capability names namespaced to **your** node so you cannot collide with a first-party verb
- [ ] **every capability declares `deadline_ms`** — it is required, there is no default
- [ ] every `deadline_ms` is a real worst case in **1 … 180000**; above the ceiling is refused at load (§2.3)
- [ ] `endpoint` is loopback; the port is free and nothing else claims it
- [ ] `input_schema` declares every slot as a **string** and documents the encoding
- [ ] `output_schema` declares what you return
- [ ] `requires.emits_untrusted` is honest
- [ ] `requires.permissions` lists what you write
- [ ] `kind: "environment"` ⇒ `workspace_root` present

Endpoints:

- [ ] `/health` returns `{"status":"ok"}` and answers in milliseconds
- [ ] `/health` reports able-to-serve, not merely running
- [ ] `/manifest` serves the file's **bytes**, unmodified
- [ ] `/invoke` verifies `X-Darci-Token` when a secret is configured, and accepts when not
- [ ] `/invoke` returns 200 + an error envelope for bad input, never a 500
- [ ] unknown capability ⇒ `NOT_IMPLEMENTED`
- [ ] expired `deadline_at` ⇒ refused before work; absent/malformed ⇒ **not** treated as expired

Envelope:

- [ ] `trace_id` echoed unchanged
- [ ] `outcome` is exactly `ok`, `error`, or `blocked`
- [ ] **every** payload value is a string, on every path
- [ ] error codes are from the §9.4 list; `retryable` classified honestly
- [ ] `blocked` used for external dependencies, never `error` (§10.4)
- [ ] dependency `kind` uses hyphens: `human-decision`, `missing-environment`, `pending-outcome`
- [ ] `taint` level set honestly and only ever raised; `sources` names each origin, carries forward
      anything still relevant from the request, and contains no secrets (§9.6)
- [ ] `confidence` omitted rather than fabricated

Behaviour:

- [ ] **safe for concurrent `/invoke` calls** — no shared mutable state, no fixed output path, any
      non-reentrant library guarded by your own lock (§2.4)
- [ ] optional sub-features guarded; shortfall reported in the payload, not thrown (§11.2)
- [ ] outputs in your own directory, gitignored, absolute paths returned
- [ ] caller-supplied filenames sanitized
- [ ] repeated identical invocations are safe (§12.2)
- [ ] determinism stated either way in the capability description

Operations:

- [ ] node started **before** the core; manifest edits need **both** restarted, node first (§4.4)
- [ ] manifest edited ⇒ node restarted ⇒ core restarted, in that order
- [ ] `GET /nodes` shows you under `registered` with `"transport": "http"`, and not under `skipped`
- [ ] a real `POST /nodes/invoke` returns `Succeeded`
- [ ] tests cover the envelope, not only the logic (§15.6)

---

## 17. Common mistakes, in the order they actually bit

From building `darci.modeling`, the first external node.

1. **Regenerating the manifest instead of serving the file.** A SHA mismatch and a refused registration.
   Read the bytes. (§6.4)
2. **Forgetting the payload is flat strings.** A nested object silently does not survive the round trip.
   (§8.4)
3. **Returning 500 on bad input.** The core can only call that `DEPENDENCY_UNAVAILABLE`; the caller learns
   nothing about what to fix. (§7.3)
4. **Changing the manifest without restarting the node.** It still serves the bytes it read at startup.
   (§6.4)
5. **Assuming the core will start your process.** It will not — it health-checks an address, once, at its
   own startup. (§4.4)
6. **Treating an optional sub-feature as fatal.** `darci.modeling` cannot render previews headlessly, so it
   reports `renders: "0/3"` and still returns the model. (§11.2)

Two more that are not from that list but have each cost a real boot or a real bug:

7. **Parsing the manifest without the string-enum converter.** `"kind": "capability"` fails, and the node
   is skipped as "unparseable" with no hint why. (§6.4)
8. **Treating `blocked` as a failure.** The core had this bug until an out-of-process node hit it. Do not
   reintroduce it from your side by reporting `error` when you mean `blocked`. (§10.4)

And two that nothing in the repo has hit yet only because no node has been written to provoke them:

9. **Omitting `deadline_ms`, or declaring one above 180 000.** It is required and ceilinged, and both
   are refused at load — so this costs you a registration, not a silent truncation. The error names the
   capability and the bound. (§2.3)
10. **Assuming `/invoke` is called one at a time.** Nothing serializes it. A fixed output path, a
    module-level cache, or a non-reentrant library will corrupt results under concurrent calls, and the
    corruption will look like a logic bug. (§2.4)

---

## 18. Appendix A — environment variables

| Variable | Read by | Effect |
|---|---|---|
| `DARCI_NODES_PATH` | core | Directory scanned for `darci-node.json`. Default `<ContentRoot>/../nodes`. |
| `DARCI_NODE_SHARED_SECRET` | core **and** node | Makes `X-Darci-Token` verifiable. Must be the same value in both processes. Unset ⇒ a per-boot random value no pre-started node can verify. |
| `DARCI_TELEMETRY_DB_PATH` | core | Telemetry database. Default `telemetry.db` beside the main database. |
| *(your own)* | node | **[RECOMMENDED]** one env var for your output directory, e.g. `DARCI_MODELING_OUTPUT_DIR`. |

And the operational numbers that have **no** environment variable, for completeness — all three are
hardcoded and a node author cannot change any of them:

| Number | Value | Where it is set |
|---|---|---|
| Handshake budget (health + manifest) | 3 s | `Program.cs`, `NodeDiscoveryOptions.HandshakeTimeout` |
| Deadline ceiling (HTTP cap **and** validated maximum) | 180 s | `NodeDeadline.MaxMs`; `Program.cs` sets the client's timeout from the same constant |
| Loopback requirement | on | `NodeDiscoveryOptions.RequireLoopback` default, never overridden (§14.1) |

## 19. Appendix B — enforcement summary

What will actually stop you, in the order you will meet it:

| # | Check | Where | Consequence |
|---|---|---|---|
| 1 | Manifest is valid JSON and deserializes | `NodeManifestLoader.Load` | skipped (third party) / **fatal** (first party) |
| 2 | `contract_version` supported | `NodeManifest.Validate` | skipped |
| 3 | `node_id` present and namespaced | `Validate` | skipped |
| 4 | `node_version` present | `Validate` | skipped |
| 5 | ≥ 1 capability | `Validate` | skipped |
| 6 | Capability names valid and unique in-manifest | `Validate` | skipped |
| 7 | `deadline_ms` present, `> 0`, and `<= 180000` out-of-process | `Validate` | skipped, with the capability and bound named (§2.3) |
| 8 | `environment` ⇒ `workspace_root` | `Validate` | skipped |
| 9 | Endpoint is loopback | `NodeDiscovery` | skipped |
| 10 | `/health` ⇒ 2xx + `status == "ok"` | `HttpNodeAdapter.IsHealthyAsync` | skipped |
| 11 | `/manifest` parseable | `FetchManifestAsync` | skipped |
| 12 | Manifest hash matches disk | `HandshakeAsync` | skipped |
| 13 | Handshake within 3s | `NodeDiscovery` | skipped |
| 14 | `node_id` not already registered | `NodeRegistry.Register` | skipped |
| 15 | No capability already owned | `NodeRegistry.Register` | skipped |
| 16 | — *registered* — | | |
| 17 | Capability name valid on the call | `POST /nodes/invoke` | `400` |
| 18 | Deadline not already passed | `HttpNodeAdapter.InvokeAsync` | `DEADLINE_EXCEEDED`, node never called |
| 18b | Response within the capability's `deadline_ms` (≤ the 180 s ceiling) | `InvokeAsync` + `HttpClient.Timeout`, both from `NodeDeadline` | `DEADLINE_EXCEEDED` (§2.3) |
| 19 | Response is 2xx | `InvokeAsync` | `DEPENDENCY_UNAVAILABLE` |
| 20 | Body parseable and non-empty | `InvokeAsync` | `INTERNAL` |
| 21 | `outcome` recognized | `ParseOutcome` | unknown ⇒ `Error` |
| 22 | Taint not lowered | `TaintRef.RaisedTo` | silently raised back |

## 20. Appendix C — source map

| Topic | File |
|---|---|
| Manifest model, validation, SHA | `DARCI-v4/Darci.Nodes/Contract/NodeManifest.cs` |
| Manifest loading, tolerant load | `DARCI-v4/Darci.Nodes/Contract/NodeManifestLoader.cs` |
| Discovery, handshake, loopback gate | `DARCI-v4/Darci.Nodes/Contract/NodeDiscovery.cs` |
| Registry, capability ownership, degraded | `DARCI-v4/Darci.Nodes/Contract/NodeRegistry.cs` |
| Envelopes (`NodeInvocation` / `NodeResult`) | `DARCI-v4/Darci.Nodes/Contract/NodeInvocation.cs` |
| Outcomes, error codes, dependency kinds, taint | `DARCI-v4/Darci.Nodes/Contract/NodeContract.cs` |
| HTTP transport, wire DTOs, deadline enforcement | `DARCI-v4/Darci.Nodes/Contract/HttpNodeAdapter.cs` |
| The one dispatch point, `Project` / `Fold` | `DARCI-v4/Darci.Nodes/Contract/NodeDispatcher.cs` |
| Routing, Blocked, gap recording | `DARCI-v4/Darci.Nodes/NodeRouter.cs` |
| State machine, `PacketPayload` | `DARCI-v4/Darci.Nodes/NodePrimitives.cs` |
| Work record, parking, leases | `DARCI-v4/Darci.Nodes/NodePacket.cs` |
| Orphan sweep, the parked-packet carve-out | `DARCI-v4/Darci.Nodes/NodeWatchdog.cs` |
| §15c audit record | `DARCI-v4/Darci.Nodes/Contract/SqliteNodeRegistrationStore.cs` |
| Capability name validation | `DARCI-v4/Darci.Nodes/Contract/CapabilityKeys.cs` |
| Wiring, `/nodes`, `/nodes/invoke`, env vars | `DARCI-v4/Darci.Api/Program.cs` |
| Reference node | `DARCI-v4/nodes/darci.modeling/` |
| Contract tests worth reading | `DARCI-v4/Darci.Nodes.Tests/Contract/` |
