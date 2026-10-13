"""Contract conformance for the node's three endpoints.

These are the tests that decide whether a collaborator's node is actually a DARCI node. They assert the
wire shape the core's HttpNodeAdapter will parse - snake_case keys, a flat string payload, an echoed
trace_id - rather than anything about CAD.
"""

from __future__ import annotations

import hashlib
import importlib
import json
import sys
from datetime import datetime, timedelta, timezone
from pathlib import Path

import pytest
from fastapi.testclient import TestClient

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

import node_server  # noqa: E402

client = TestClient(node_server.app)

FUTURE = (datetime.now(timezone.utc) + timedelta(minutes=5)).isoformat()

#: A spec that actually builds, so the envelope tests exercise the success path rather than the
#: validator. Deliberately the smallest real part, not a stand-in.
VALID_SPEC = {"shape": "box", "dimensions_mm": {"length": 20, "width": 15, "height": 5}}


def envelope(capability: str = "model.build", payload: dict | None = None, deadline: str | None = None) -> dict:
    """A minimal invocation shaped like the one HttpNodeAdapter.ToWire emits."""
    return {
        "envelope_version": "0.1.1",
        "trace_id": "t-" + "0" * 30,
        "goal_id": "g-1",
        "capability": capability,
        "issued_at": datetime.now(timezone.utc).isoformat(),
        "deadline_at": deadline or FUTURE,
        "principal": {"trust": "operator", "id": "tinman"},
        "taint": {"level": "clean", "sources": []},
        "broker": {"url": None, "token": None},
        "intent": "test",
        "payload": payload if payload is not None else {"spec": json.dumps(VALID_SPEC)},
    }


# ── GET /health ──────────────────────────────────────────────────────────────────────────────────

def test_health_reports_ok_when_the_kernel_is_loadable():
    response = client.get("/health")

    assert response.status_code == 200
    # The core reads exactly this field and accepts nothing but "ok". A 200 saying anything else means
    # up-but-not-ready, which must NOT register.
    assert response.json()["status"] == "ok"


# ── GET /manifest ────────────────────────────────────────────────────────────────────────────────

def test_manifest_is_served_byte_for_byte_from_disk():
    on_disk = (Path(node_server.__file__).parent / "darci-node.json").read_bytes()

    response = client.get("/manifest")

    assert response.status_code == 200
    # Byte equality, not JSON equality. The core hashes what it parses, and the free-form input_schema
    # keeps its original property order - so "same values, different order" is a registration failure.
    assert response.content == on_disk
    assert hashlib.sha256(response.content).hexdigest() == hashlib.sha256(on_disk).hexdigest()


def test_the_manifest_declares_what_this_node_actually_serves():
    manifest = client.get("/manifest").json()

    assert manifest["node_id"] == "darci.modeling"
    assert manifest["endpoint"].startswith("http://127.0.0.1:")   # loopback, or discovery refuses it
    assert [c["name"] for c in manifest["capabilities"]] == ["model.build"]
    # Iteration 1 is deterministic: no model class, so no broker callback - which does not exist yet.
    assert manifest["requires"]["model_classes"] == []


# ── POST /invoke ─────────────────────────────────────────────────────────────────────────────────

def test_invoke_returns_a_well_formed_result_envelope():
    response = client.post("/invoke", json=envelope())

    assert response.status_code == 200
    body = response.json()
    assert body["outcome"] == "ok"
    assert body["trace_id"] == envelope()["trace_id"]
    assert body["envelope_version"] == "0.1.1"
    assert body["taint"]["level"] == "clean"
    # Flat string map, both directions - a nested value here would not survive the core's parse.
    assert all(isinstance(v, str) for v in body["payload"].values())


def test_a_build_produces_real_step_and_stl_files():
    spec = {"shape": "plate", "dimensions_mm": {"length": 60, "width": 40, "thickness": 5},
            "features": [{"type": "hole", "diameter_mm": 6, "at_mm": [0, 0]}]}

    body = client.post("/invoke", json=envelope(payload={"spec": json.dumps(spec), "name": "unit-plate"})).json()

    assert body["outcome"] == "ok", body
    step = Path(body["payload"]["step_path"])
    stl = Path(body["payload"]["stl_path"])
    assert step.exists() and "ISO-10303-21" in step.read_text(encoding="utf-8", errors="replace")[:200]
    assert stl.exists() and stl.stat().st_size > 0

    # Measured from the exported mesh, not asserted from the request.
    assert body["payload"]["bbox_mm"] == "60.0x40.0x5.0"
    assert body["payload"]["watertight"] == "true"
    assert float(body["payload"]["volume_mm3"]) == pytest.approx(60 * 40 * 5 - 3.14159 * 9 * 5, rel=0.01)


def test_only_the_requested_formats_come_back():
    spec = json.dumps({"shape": "box", "dimensions_mm": {"length": 10, "width": 10, "height": 10}})

    body = client.post("/invoke", json=envelope(payload={"spec": spec, "formats": "stl"})).json()

    assert "stl_path" in body["payload"]
    assert "step_path" not in body["payload"]


def test_an_unsupported_format_is_rejected_by_name():
    spec = json.dumps({"shape": "box", "dimensions_mm": {"length": 10, "width": 10, "height": 10}})

    body = client.post("/invoke", json=envelope(payload={"spec": spec, "formats": "3mf"})).json()

    assert body["error"]["code"] == "INVALID_INPUT"
    assert "3mf" in body["error"]["message"]


def test_a_missing_renderer_never_fails_the_build():
    # The orthographic preview needs an OpenGL context this box has no reason to have. The build must
    # still succeed and still report its geometry; only the preview count degrades.
    spec = json.dumps({"shape": "box", "dimensions_mm": {"length": 20, "width": 20, "height": 20}})

    body = client.post("/invoke", json=envelope(payload={"spec": spec})).json()

    assert body["outcome"] == "ok"
    assert "renders" in body["payload"]          # reported either way, never thrown
    assert body["payload"]["watertight"] == "true"


def test_a_geometrically_impossible_spec_is_invalid_input_not_a_crash():
    spec = json.dumps({"shape": "plate", "dimensions_mm": {"length": 20, "width": 20, "thickness": 3},
                       "features": [{"type": "hole", "diameter_mm": 30, "at_mm": [0, 0]}]})

    body = client.post("/invoke", json=envelope(payload={"spec": spec})).json()

    assert body["outcome"] == "error"
    assert body["error"]["code"] == "INVALID_INPUT"


def test_a_caller_supplied_filename_cannot_escape_the_output_directory():
    spec = json.dumps({"shape": "box", "dimensions_mm": {"length": 10, "width": 10, "height": 10}})

    body = client.post("/invoke", json=envelope(
        payload={"spec": spec, "name": "../../../../etc/passwd"})).json()

    assert body["outcome"] == "ok"
    written = Path(body["payload"]["stl_path"]).resolve()
    assert written.parent == node_server.OUTPUT_DIR.resolve(), written


def test_an_unknown_capability_is_not_implemented_rather_than_a_crash():
    body = client.post("/invoke", json=envelope(capability="model.teleport")).json()

    assert body["outcome"] == "error"
    assert body["error"]["code"] == "NOT_IMPLEMENTED"
    assert body["error"]["retryable"] is False


@pytest.mark.parametrize("payload,reason", [
    ({}, "missing spec"),
    ({"spec": "{not json"}, "unparseable spec"),
    ({"spec": "[1, 2, 3]"}, "spec is not an object"),
])
def test_a_bad_spec_is_a_structured_error_not_a_500(payload, reason):
    response = client.post("/invoke", json=envelope(payload=payload))

    assert response.status_code == 200, reason
    body = response.json()
    assert body["outcome"] == "error"
    assert body["error"]["code"] == "INVALID_INPUT"


def test_an_already_expired_deadline_is_refused_before_any_work():
    past = (datetime.now(timezone.utc) - timedelta(seconds=1)).isoformat()

    body = client.post("/invoke", json=envelope(deadline=past)).json()

    assert body["outcome"] == "error"
    assert body["error"]["code"] == "DEADLINE_EXCEEDED"


def test_a_body_that_is_not_json_is_rejected():
    response = client.post("/invoke", content=b"not json", headers={"Content-Type": "application/json"})

    assert response.status_code == 400


# ── X-Darci-Token (fork F3) ──────────────────────────────────────────────────────────────────────

def test_without_a_configured_secret_the_token_is_not_checked(monkeypatch):
    # The default posture: the core mints a random per-boot value this process cannot know, so refusing
    # it would make the node unusable rather than secure.
    monkeypatch.setattr(node_server, "SHARED_SECRET", "")

    assert client.post("/invoke", json=envelope()).json()["outcome"] == "ok"
    assert client.post("/invoke", json=envelope(), headers={"X-Darci-Token": "anything"}).status_code == 200


def test_with_a_configured_secret_a_wrong_or_missing_token_is_refused(monkeypatch):
    monkeypatch.setattr(node_server, "SHARED_SECRET", "s3cret")

    assert client.post("/invoke", json=envelope()).status_code == 401
    assert client.post("/invoke", json=envelope(), headers={"X-Darci-Token": "wrong"}).status_code == 401
    assert client.post("/invoke", json=envelope(), headers={"X-Darci-Token": "s3cret"}).status_code == 200


def test_the_module_reloads_its_secret_from_the_environment(monkeypatch):
    # Guards the wiring itself: the constant has to come from DARCI_NODE_SHARED_SECRET, or configuring
    # the pair would silently do nothing.
    monkeypatch.setenv("DARCI_NODE_SHARED_SECRET", "from-env")
    reloaded = importlib.reload(node_server)
    try:
        assert reloaded.SHARED_SECRET == "from-env"
    finally:
        monkeypatch.delenv("DARCI_NODE_SHARED_SECRET", raising=False)
        importlib.reload(node_server)
