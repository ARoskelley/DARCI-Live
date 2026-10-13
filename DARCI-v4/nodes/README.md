# Adding your own node

A node is a separate program that does one kind of work and tells DARCI how to ask for it. The core
compiles none of your code: it finds your node by a manifest file, checks it is alive, verifies the
manifest, and then calls it over HTTP. Any language works — the first external node in this repo,
`darci.modeling`, is Python.

Nothing here is aspirational. It is written from building that node, including the parts that went wrong.

## The contract

Your node serves three endpoints and nothing else:

| Endpoint | Purpose |
|---|---|
| `GET /health` | `200 {"status":"ok"}` when able to serve. A 200 saying anything else means *up but not ready*, and the core will not register you. |
| `GET /manifest` | Your `darci-node.json`, **byte for byte**. |
| `POST /invoke` | One invocation envelope in, one result envelope out. |

The core calls nothing else, and **the core never dies because a node died**. If you are absent, slow,
unreachable, or lying about your manifest, you are skipped with a named reason and DARCI starts anyway.
Your capability is simply not available, and work that needs it is recorded as *blocked* rather than
failed.

## 1. Pick a capability name

`domain.action`, lowercase, namespaced — `model.build`, `coding.write`, `acme.summarize`. One node owns a
capability; if a built-in already serves it, you are skipped rather than silently preferred.

## 2. Write `nodes/<your.node.id>/darci-node.json`

```json
{
  "contract_version": "0.1.1",
  "node_id": "acme.summarize",
  "display_name": "Summariser",
  "node_version": "0.1.0",
  "kind": "capability",
  "endpoint": "http://127.0.0.1:7802",
  "health": "/health",
  "capabilities": [
    {
      "name": "summary.write",
      "description": "What this does, in one sentence.",
      "input_schema":  { "type": "object", "required": ["text"], "properties": { "text": { "type": "string" } } },
      "output_schema": { "type": "object", "properties": { "summary": { "type": "string" } } },
      "typical_latency_ms": 2000,
      "deadline_ms": 60000
    }
  ],
  "requires": {
    "model_classes": [],
    "memory_scopes": [],
    "permissions": [],
    "emits_untrusted": false
  },
  "author": "you",
  "repository": "https://github.com/you/your-node"
}
```

What the fields actually control:

- **`endpoint`** must be loopback (`127.0.0.1` or `localhost`). Remote endpoints are refused until the
  broker-token work lands. Pick a port nothing else uses; `darci.modeling` took 7801.
- **`deadline_ms`** is **binding**. The core abandons the call at that point whatever you are doing. It is
  also not overridable by the caller, by design — this number is the reviewed one.
- **`kind: "environment"`** additionally requires `workspace_root`. Use `"capability"` unless you own
  durable workspace state.
- **`emits_untrusted: true`** if your output contains anything you fetched from outside — scraped text, a
  model's words, a third-party API's response. Geometry you computed from the caller's own numbers is not
  untrusted. Get this right; it propagates.
- **`requires.model_classes`** declares which model tiers you need. Leave it empty if you are
  deterministic. There is currently **no broker callback** (`BrokerRef` is a declared no-op), so a node
  cannot yet ask the core for inference. Calling a model provider directly from your node would bypass the
  broker, and that is the thing the broker exists to prevent.

## 3. Serve the manifest verbatim

```python
MANIFEST_BYTES = (NODE_DIR / "darci-node.json").read_bytes()

@app.get("/manifest")
def manifest():
    return Response(content=MANIFEST_BYTES, media_type="application/json")
```

**Read the bytes. Do not rebuild the JSON.** The core hashes the on-disk file and the one you serve and
refuses to register you if they differ — the on-disk manifest is the reviewed capability grant, and a node
reporting something else is claiming a surface nobody approved. `input_schema` is kept as a raw document,
so even re-emitting the same values in a different key order is a mismatch. The refusal looks like:

```
Node acme.summarize at http://127.0.0.1:7802 did not complete the handshake
(/manifest does not match the on-disk file (disk 9D6EBFE02519, node B7E20F4A30E0)); not registered.
```

If you change the manifest, restart the node too — it holds the bytes it read at startup.

## 4. Handle `/invoke`

The request (snake_case throughout):

```json
{
  "envelope_version": "0.1.1",
  "trace_id": "…", "goal_id": "…",
  "capability": "summary.write",
  "issued_at": "…", "deadline_at": "…",
  "principal": {"trust": "operator", "id": "tinman"},
  "taint": {"level": "clean", "sources": []},
  "broker": {"url": null, "token": null},
  "intent": "…",
  "payload": {"text": "…"}
}
```

Your reply:

```json
{
  "envelope_version": "0.1.1",
  "trace_id": "…the same one…",
  "outcome": "ok",
  "taint": {"level": "clean", "sources": []},
  "payload": {"summary": "…"}
}
```

Four things worth knowing before you design your input:

- **`payload` is a flat map of string to string, both directions.** Not nested JSON. A structured argument
  travels as a JSON-encoded string in one slot — `darci.modeling` takes its whole design spec as `spec`.
  Say so in your schema rather than describing an object the transport cannot carry.
- **Echo `trace_id` unchanged.** The core trusts its own copy, but a node that scrambles it is lying in
  telemetry.
- **Taint is monotonic.** You can raise it, never lower it.
- **Unknown `outcome` strings are treated as errors**, not optimistic success.

### Failing well

| Situation | Reply |
|---|---|
| The caller's input is wrong | `{"outcome":"error","error":{"code":"INVALID_INPUT","message":"spec.shape is required","retryable":false}}` |
| You need something you do not have | `{"outcome":"blocked","dependency":{"kind":"missing-environment","detail":"no CAD kernel on this host"}}` |
| A human must decide | `{"outcome":"blocked","dependency":{"kind":"human-decision","detail":"…"}}` |

Error codes: `INVALID_INPUT`, `PERMISSION_DENIED`, `MODEL_UNAVAILABLE`, `DEPENDENCY_UNAVAILABLE`,
`DEADLINE_EXCEEDED`, `NOT_IMPLEMENTED`. Anything else becomes `INTERNAL`.

**Return a 200 with an error envelope, not a 500.** An HTTP error tells the core only that you are broken;
an error envelope tells it what the caller should change. Reserve non-2xx for refusing the request
outright — `darci.modeling` returns 401 for a bad token and nothing else.

**`blocked` is not failure.** It means bounded work happened and something outside the invocation is now
needed. Reporting it as an error feeds phantom negative evidence into the confidence machinery. (The core
had a bug here until an external node hit it: parking a packet straight from `Routed` threw, and the
router wrote the packet down as `Failed`. In-process nodes never triggered it because they move themselves
to `Working` first.)

## 5. Verify the token, if you can

The core sends `X-Darci-Token` on every `/invoke`. By default that value is minted at the core's startup,
and since the core connects to nodes that are *already running*, your node cannot know it — the header is
decoration. Set `DARCI_NODE_SHARED_SECRET` to the same value in **both** processes and the check becomes
real:

```python
if SHARED_SECRET and not hmac.compare_digest(token or "", SHARED_SECRET):
    return JSONResponse(status_code=401, content={"error": "X-Darci-Token missing or incorrect"})
```

With no secret configured, accept the call. Refusing would make your node unusable in the default setup
rather than secure. The core's boot log says which posture is live.

## 6. Run it, then start the core

Order matters: **the core does not launch nodes.** Start yours first.

```
.venv/Scripts/python -m uvicorn node_server:app --host 127.0.0.1 --port 7802
```

Then boot the core and look for:

```
Registered OUT-OF-PROCESS node acme.summarize at http://127.0.0.1:7802 serving [summary.write].
Node discovery complete: 3 in-process, 1 out-of-process, 0 skipped.
```

`GET /nodes` shows you with `"transport": "http"`, and — more usefully when something is wrong — a
`skipped` map saying exactly why you were not registered.

## 7. Ask it to do something

```
POST http://localhost:5081/nodes/invoke
{
  "capability": "summary.write",
  "payload": {"text": "…"},
  "intent": "why you are asking"
}
```

Returns the work record: `state` (`Succeeded`, `Failed`, `Blocked`, `AwaitingDependency`), the merged
`payload`, and the node's own account in `lastDecision` / `error`.

## Testing yours

Test the envelope, not just your logic. The tests that matter are the ones that catch a contract
violation: health returning exactly `ok`, the manifest being byte-identical to the file, `trace_id`
echoed, every payload value a string, a bad input producing a structured error rather than a 500, and an
expired deadline refused before any work starts. `nodes/darci.modeling/tests/` has all of these if you
want a template.

## Gotchas, in the order they bit

1. **Regenerating the manifest instead of serving the file** — a SHA mismatch and a refused registration.
2. **Forgetting the payload is flat strings** — a nested object silently does not survive the round trip.
3. **Returning 500 on bad input** — the core can only call that `DEPENDENCY_UNAVAILABLE`; the caller learns
   nothing.
4. **Changing the manifest without restarting the node** — the node still serves the old bytes.
5. **Assuming the core will start your process** — it will not. It health-checks an address.
6. **Treating an optional sub-feature as fatal** — `darci.modeling` cannot render previews headlessly, so
   it reports `renders: 0/3` and still returns the model. A missing extra must not fail the work.
