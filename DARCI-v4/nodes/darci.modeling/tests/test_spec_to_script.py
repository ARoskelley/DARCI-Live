"""The spec -> script translation, and the geometry it actually produces.

Split from the transport tests on purpose: these are about whether the part is right, not whether the
envelope is.
"""

from __future__ import annotations

import math
import sys
from pathlib import Path

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

import spec_to_script  # noqa: E402
from spec_to_script import SpecError, build_script  # noqa: E402


def run(script: str):
    """Execute through the legacy engine's sandbox — the same path the node uses."""
    sys.path.insert(0, str(Path(__file__).resolve().parents[3] / "Darci.Python"))
    from cad import cad_engine

    return cad_engine.execute_cadquery_script(script)


# ── the spec is validated before the kernel ever sees it ─────────────────────────────────────────

@pytest.mark.parametrize("spec,expected", [
    ({}, "shape is required"),
    ({"shape": "sphere", "dimensions_mm": {}}, "not supported"),
    ({"shape": "box"}, "dimensions_mm is required"),
    ({"shape": "box", "dimensions_mm": {"length": 10, "width": 10}}, "dimensions_mm.height"),
    ({"shape": "box", "dimensions_mm": {"length": 10, "width": 10, "height": -1}}, "greater than zero"),
    ({"shape": "box", "dimensions_mm": {"length": 10, "width": 10, "height": "tall"}}, "must be a number"),
    ({"shape": "box", "dimensions_mm": {"length": 10, "width": 10, "height": 2}, "features": {}}, "must be a list"),
])
def test_a_bad_spec_is_rejected_with_a_fixable_message(spec, expected):
    with pytest.raises(SpecError) as caught:
        build_script(spec)

    assert expected in str(caught.value)


def test_a_hole_that_runs_off_the_edge_is_refused():
    # The kernel would cut it and hand back a shape nobody asked for.
    spec = {
        "shape": "plate",
        "dimensions_mm": {"length": 40, "width": 40, "thickness": 5},
        "features": [{"type": "hole", "diameter_mm": 10, "at_mm": [18, 0]}],
    }

    with pytest.raises(SpecError, match="falls outside"):
        build_script(spec)


def test_an_unsupported_feature_type_is_named():
    spec = {
        "shape": "plate",
        "dimensions_mm": {"length": 40, "width": 40, "thickness": 5},
        "features": [{"type": "chamfer", "size_mm": 2}],
    }

    with pytest.raises(SpecError, match="chamfer"):
        build_script(spec)


# ── and the geometry it produces is the geometry asked for ───────────────────────────────────────

def test_a_box_has_the_requested_volume():
    script = build_script({"shape": "box", "dimensions_mm": {"length": 30, "width": 20, "height": 10}})

    assert run(script).val().Volume() == pytest.approx(30 * 20 * 10, rel=1e-6)


def test_a_plate_with_two_holes_loses_exactly_their_volume():
    spec = {
        "shape": "plate",
        "dimensions_mm": {"length": 100, "width": 60, "thickness": 6},
        "features": [
            {"type": "hole", "diameter_mm": 8, "at_mm": [-30, 0]},
            {"type": "hole", "diameter_mm": 8, "at_mm": [30, 0]},
        ],
    }

    volume = run(build_script(spec)).val().Volume()

    expected = 100 * 60 * 6 - 2 * math.pi * 4**2 * 6
    assert volume == pytest.approx(expected, rel=1e-3)


def test_a_cylinder_with_a_bore_is_a_tube():
    spec = {
        "shape": "cylinder",
        "dimensions_mm": {"diameter": 40, "height": 25},
        "features": [{"type": "bore", "diameter_mm": 20}],
    }

    volume = run(build_script(spec)).val().Volume()

    expected = math.pi * (20**2 - 10**2) * 25
    assert volume == pytest.approx(expected, rel=1e-3)


def test_a_bore_wider_than_its_cylinder_is_refused():
    spec = {"shape": "cylinder", "dimensions_mm": {"diameter": 20, "height": 10},
            "features": [{"type": "bore", "diameter_mm": 25}]}

    with pytest.raises(SpecError, match="smaller than"):
        build_script(spec)


def test_a_bracket_is_a_single_watertight_solid():
    spec = {"shape": "bracket",
            "dimensions_mm": {"length": 80, "width": 50, "height": 40, "thickness": 5}}

    solid = run(build_script(spec)).val()

    # Base plus upstand, minus the overlap they share at the corner.
    expected = 80 * 50 * 5 + 80 * 5 * 40 - 80 * 5 * 5
    assert solid.Volume() == pytest.approx(expected, rel=1e-6)
    assert solid.isValid()


def test_a_bracket_thicker_than_it_is_tall_is_refused():
    spec = {"shape": "bracket", "dimensions_mm": {"length": 80, "width": 50, "height": 4, "thickness": 5}}

    with pytest.raises(SpecError, match="less than its height"):
        build_script(spec)


def test_every_generated_script_obeys_the_sandbox_contract():
    # The engine requires `result` and refuses imports; a generator that drifts from that produces
    # scripts the sandbox rejects at run time rather than here.
    for shape, dims in [
        ("box", {"length": 10, "width": 10, "height": 10}),
        ("cylinder", {"diameter": 10, "height": 10}),
        ("plate", {"length": 10, "width": 10, "thickness": 2}),
        ("bracket", {"length": 10, "width": 10, "height": 8, "thickness": 2}),
    ]:
        script = build_script({"shape": shape, "dimensions_mm": dims})
        assert "result" in script
        assert "import" not in script
        assert shape in spec_to_script.SHAPES
