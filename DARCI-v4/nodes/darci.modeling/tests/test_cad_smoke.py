"""M0 GATE — does the CAD kernel actually run on this box?

A pip resolve proves wheels exist; it does not prove OCP loads, that the kernel can build a solid,
or that it can write the two formats the node promises. Everything above this is contingent on it,
so it is a test rather than a note in a report.
"""
import math
from pathlib import Path

import cadquery as cq


def test_the_native_kernel_loads():
    # OCP is the OpenCascade binding — a large native extension, and the part a pip resolve cannot
    # vouch for. Importing a geometry primitive from it proves the DLLs actually loaded, which
    # importing the `OCP` package alone does not.
    from importlib.metadata import version

    from OCP.gp import gp_Pnt

    assert gp_Pnt(1.0, 2.0, 3.0).X() == 1.0
    assert cq.__version__
    assert version("cadquery-ocp")


def test_a_box_exports_to_both_step_and_stl(tmp_path: Path):
    # 40 x 25 x 10 mm, with a hole, so the export is exercised on something with a curved face
    # rather than six flat planes.
    result = (
        cq.Workplane("XY")
        .box(40, 25, 10)
        .faces(">Z")
        .workplane()
        .hole(8)
    )

    step = tmp_path / "smoke.step"
    stl = tmp_path / "smoke.stl"
    cq.exporters.export(result, str(step), exportType="STEP")
    cq.exporters.export(result, str(stl), exportType="STL")

    # STEP is the one that matters for engineering handoff and is the format the legacy service never
    # emitted, so assert its content rather than only its size.
    assert step.exists() and step.stat().st_size > 0
    head = step.read_text(encoding="utf-8", errors="replace")[:200]
    assert "ISO-10303-21" in head, f"not a STEP file: {head!r}"

    assert stl.exists() and stl.stat().st_size > 0

    # And that the kernel computed real geometry, not an empty shape: 40*25*10 minus a d8 bore.
    expected = 40 * 25 * 10 - math.pi * 4**2 * 10
    assert result.val().Volume() == __import__("pytest").approx(expected, rel=0.01)
