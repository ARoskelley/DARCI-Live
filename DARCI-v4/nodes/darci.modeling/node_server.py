"""The 3D-modeling node: an OUT-OF-PROCESS DARCI node, in Python.

Speaks the three endpoints of the node contract (doc section 5.2) and nothing else:

    GET  /health    -> {"status": "ok"} when able to serve
    GET  /manifest  -> this folder's darci-node.json, byte for byte
    POST /invoke    -> one invocation envelope in, one result envelope out

Run it BEFORE the core (decision D3: the core connects to nodes that already exist, it never launches
them):

    .venv/Scripts/python -m uvicorn node_server:app --host 127.0.0.1 --port 7801

Two things in here are easy to get wrong and are therefore done deliberately.

**The manifest is served verbatim.** The core's handshake fetches /manifest, parses it, re-serializes it
and compares the SHA-256 against the on-disk file it already read. Regenerating the JSON here - even
with identical values - risks a different property order inside the free-form input_schema, which the
core preserves as a raw document. A mismatch is not a warning: the node is refused registration, on the
grounds that it is claiming a surface nobody reviewed. Reading the bytes and returning them removes the
entire failure mode.

**The payload is a flat map of string to string, both directions.** Not nested JSON. The structured spec
therefore travels as one JSON-encoded string in the "spec" slot, and every value coming back is a string.
The manifest's schemas say so rather than pretending otherwise.
"""

from __future__ import annotations

import hashlib
import hmac
import json
import os
import sys
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

from fastapi import FastAPI, Header, Request, Response
from fastapi.responses import JSONResponse

NODE_DIR = Path(__file__).resolve().parent
MANIFEST_PATH = NODE_DIR / "darci-node.json"

#: Exports land here, not in the legacy service's directory. Set BEFORE importing cad_engine, whose
#: CAD_OUTPUT_DIR is read and created at import time.
OUTPUT_DIR = Path(os.getenv("DARCI_MODELING_OUTPUT_DIR") or (NODE_DIR / "output"))
OUTPUT_DIR.mkdir(parents=True, exist_ok=True)
os.environ.setdefault("CAD_OUTPUT_DIR", str(OUTPUT_DIR))

# The CAD engine is IMPORTED from the existing service rather than reimplemented. It already carries the
# parts that are expensive to get right - AST script filtering, restricted builtins, an execution timeout
# with a Windows fallback, and mesh validation - and duplicating them would mean two sandboxes to keep
# honest. Darci.Python itself is left untouched: this node adds a second consumer, not a fork.
_LEGACY_CAD = NODE_DIR.parents[1] / "Darci.Python"
if str(_LEGACY_CAD) not in sys.path:
    sys.path.insert(0, str(_LEGACY_CAD))

import spec_to_script  # noqa: E402  (local module; after the path setup above for symmetry)

#: Read once at import. A node whose manifest changed under it should be restarted, not quietly serve a
#: surface the core verified against a different file.
MANIFEST_BYTES = MANIFEST_PATH.read_bytes()
MANIFEST = json.loads(MANIFEST_BYTES)

NODE_ID: str = MANIFEST["node_id"]
CONTRACT_VERSION: str = MANIFEST["contract_version"]
CAPABILITIES: set[str] = {c["name"] for c in MANIFEST["capabilities"]}

#: Set this to the same value as the core's DARCI_NODE_SHARED_SECRET to make X-Darci-Token verifiable.
#: Left unset, the token is accepted unchecked - see _check_token.
SHARED_SECRET = os.getenv("DARCI_NODE_SHARED_SECRET") or ""

app = FastAPI(title=MANIFEST["display_name"], version=MANIFEST["node_version"])


# ── section 5.2: GET /health ─────────────────────────────────────────────────────────────────────

@app.get("/health")
def health() -> dict[str, str]:
    """Able to serve, not merely running.

    The core treats a 200 carrying anything other than status "ok" as NOT healthy, which is the right
    reading: a process that is up but cannot do the work should not be registered. So this checks the
    one thing that would actually stop it working - that the CAD kernel loaded.
    """
    if not _kernel_available():
        return JSONResponse(  # type: ignore[return-value]
            status_code=200,
            content={"status": "degraded", "detail": "the CadQuery kernel failed to import"},
        )
    return {"status": "ok", "node_id": NODE_ID, "node_version": MANIFEST["node_version"]}


def _kernel_available() -> bool:
    try:
        _engine()
    except Exception:
        return False
    return True


def _engine():
    """The legacy CAD engine, imported lazily so an import failure is a 'degraded' health answer rather
    than a process that will not start."""
    from cad import cad_engine

    return cad_engine


# ── section 5.2: GET /manifest ───────────────────────────────────────────────────────────────────

@app.get("/manifest")
def manifest() -> Response:
    """The on-disk file, unmodified. See the module docstring for why this is not regenerated."""
    return Response(content=MANIFEST_BYTES, media_type="application/json")


# ── section 5.2: POST /invoke ────────────────────────────────────────────────────────────────────

@app.post("/invoke")
async def invoke(request: Request, x_darci_token: str | None = Header(default=None)) -> Response:
    if not _check_token(x_darci_token):
        # 401 rather than a result envelope: this is not an outcome of the work, it is a refusal to
        # start it. The core maps any non-2xx to DEPENDENCY_UNAVAILABLE, which is the honest reading.
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

    # The deadline is binding (section 5.3) and the core enforces it from its side too. Checked here as
    # well because a node that starts a two-minute solid operation it can never deliver has burned the
    # time for nothing.
    expired = _deadline_passed(envelope.get("deadline_at"))
    if expired:
        return _error(trace_id, "DEADLINE_EXCEEDED",
                      "The invocation deadline had already passed when the node received it.",
                      retryable=True)

    if capability == "model.build":
        return _build_model(trace_id, payload)

    return _error(trace_id, "NOT_IMPLEMENTED", f"'{capability}' is declared but not wired.", retryable=False)


def _build_model(trace_id: str, payload: dict[str, str]) -> Response:
    """Spec in, geometry out: script -> sandboxed execution -> STEP/STL export -> mesh validation."""
    spec_raw = payload.get("spec")
    if not spec_raw:
        return _error(trace_id, "INVALID_INPUT", "payload.spec is required (a JSON object as a string).",
                      retryable=False)

    try:
        spec = json.loads(spec_raw)
    except json.JSONDecodeError as e:
        return _error(trace_id, "INVALID_INPUT", f"payload.spec is not valid JSON: {e}", retryable=False)

    if not isinstance(spec, dict):
        return _error(trace_id, "INVALID_INPUT",
                      f"payload.spec must encode a JSON object, got {type(spec).__name__}.", retryable=False)

    try:
        script = spec_to_script.build_script(spec)
    except spec_to_script.SpecError as e:
        # The caller can fix this. Deliberately distinct from an engine failure, which they cannot.
        return _error(trace_id, "INVALID_INPUT", str(e), retryable=False)

    formats = {f.strip().lower() for f in (payload.get("formats") or "step,stl").split(",") if f.strip()}
    unknown = formats - {"step", "stl"}
    if unknown:
        return _error(trace_id, "INVALID_INPUT",
                      f"unsupported format(s) {sorted(unknown)}; supported: step, stl.", retryable=False)
    if not formats:
        return _error(trace_id, "INVALID_INPUT", "payload.formats named no format.", retryable=False)

    engine = _engine()
    base = _safe_stem(payload.get("name") or "model")
    script_sha = hashlib.sha256(script.encode("utf-8")).hexdigest()

    try:
        workplane = engine.execute_cadquery_script(script)
    except engine.ScriptSecurityError as e:
        return _error(trace_id, "PERMISSION_DENIED", f"the generated script was refused: {e}", retryable=False)
    except engine.ExecutionTimeout as e:
        return _error(trace_id, "DEADLINE_EXCEEDED", str(e), retryable=True)
    except Exception as e:
        return _error(trace_id, "INTERNAL", f"the CAD kernel failed to build the solid: {e}", retryable=False)

    import cadquery as cq

    result: dict[str, str] = {"script_sha256": script_sha, "shape": str(spec.get("shape", ""))}

    try:
        # STEP first: it is the format an engineer hands to a machinist, and the one the legacy pipeline
        # never produced. STL is the mesh, and is also what validation reads back.
        if "step" in formats:
            step_path = engine.safe_export_path(f"{base}.step")
            cq.exporters.export(workplane, str(step_path), exportType="STEP")
            result["step_path"] = str(step_path)

        stl_path = engine.safe_export_path(f"{base}.stl")
        cq.exporters.export(workplane, str(stl_path), exportType="STL")
        if "stl" in formats:
            result["stl_path"] = str(stl_path)
    except Exception as e:
        return _error(trace_id, "INTERNAL", f"export failed: {e}", retryable=False)

    # Validation reads the exported mesh rather than the kernel's own shape, so it measures the artifact
    # that actually leaves this node.
    try:
        import trimesh

        mesh = trimesh.load_mesh(str(stl_path))
        if not isinstance(mesh, trimesh.Trimesh):
            return _error(trace_id, "INTERNAL", "the export produced a scene rather than a single solid.",
                          retryable=False)

        validation = engine.validate_mesh(mesh)
        bbox = validation.bounding_box_mm
        result.update({
            "bbox_mm": f"{bbox.get('x')}x{bbox.get('y')}x{bbox.get('z')}",
            "volume_mm3": f"{validation.volume_cc * 1000.0:.3f}",
            "watertight": "true" if validation.is_watertight else "false",
            "triangle_count": str(validation.triangle_count),
            "validation_passed": "true" if validation.passed else "false",
        })
        if validation.warnings:
            result["warnings"] = "; ".join(validation.warnings)
    except Exception as e:
        # The model exists and is on disk; only the measurement failed. Saying so beats discarding a
        # good part because a mesh library tripped.
        result["validation_error"] = str(e)

    result["renders"] = _render_count(engine, stl_path)
    return _ok(trace_id, result)


def _render_count(engine, stl_path: Path) -> str:
    """Orthographic previews are a NICE-TO-HAVE and must never fail a build.

    The renderer needs an OpenGL context, which a headless box does not have. The engine already
    swallows per-view failures and returns None for each, but it is called inside its own guard anyway:
    a missing renderer must cost the caller a preview, not a part.
    """
    try:
        import trimesh

        mesh = trimesh.load_mesh(str(stl_path))
        views = engine.render_orthographic_views(mesh)
        rendered = [name for name, data in views.items() if data]
        return f"{len(rendered)}/{len(views)}"
    except Exception:
        return "0/3 (renderer unavailable)"


def _safe_stem(name: str) -> str:
    """Filenames come from the caller, so reduce to a bare stem before the engine's own traversal check."""
    stem = Path(str(name)).stem
    cleaned = "".join(c for c in stem if c.isalnum() or c in "-_")
    return cleaned or "model"


# ── envelope helpers (section 5.3) ───────────────────────────────────────────────────────────────

def _ok(trace_id: str, payload: dict[str, str]) -> Response:
    return _envelope(trace_id, "ok", payload=payload)


def _error(trace_id: str, code: str, message: str, *, retryable: bool) -> Response:
    return _envelope(trace_id, "error", error={"code": code, "message": message, "retryable": retryable})


def _envelope(trace_id: str, outcome: str, **extra: Any) -> Response:
    body: dict[str, Any] = {
        "envelope_version": CONTRACT_VERSION,
        # Echoed unchanged. The core trusts its own copy regardless, but a node that scrambles it would
        # be lying in its telemetry.
        "trace_id": trace_id,
        "outcome": outcome,
        # Clean: this node computes geometry from the caller's own spec. It reads no external source,
        # so it introduces no taint. The moment it generates code from a model, this becomes untrusted.
        "taint": {"level": "clean", "sources": []},
    }
    body.update(extra)
    return JSONResponse(status_code=200, content=body)


def _check_token(token: str | None) -> bool:
    """Verify X-Darci-Token when a shared secret is configured.

    With no secret set the core sends a value minted at its own startup, which this process - started
    first, by decision D3 - cannot possibly know. Rejecting it would make the node unusable in the
    default configuration, so an unconfigured node accepts the call: the posture is "loopback only,
    unauthenticated", the same as the rest of the local surface. Configure DARCI_NODE_SHARED_SECRET on
    both sides and the check becomes real.
    """
    if not SHARED_SECRET:
        return True
    return token is not None and hmac.compare_digest(token, SHARED_SECRET)


def _deadline_passed(deadline_at: Any) -> bool:
    if not isinstance(deadline_at, str) or not deadline_at:
        return False  # absent deadline is not an expired one
    try:
        parsed = datetime.fromisoformat(deadline_at.replace("Z", "+00:00"))
    except ValueError:
        return False
    if parsed.tzinfo is None:
        parsed = parsed.replace(tzinfo=timezone.utc)
    return datetime.now(timezone.utc) > parsed
