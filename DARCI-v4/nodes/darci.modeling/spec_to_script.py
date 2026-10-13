"""Turn a structured design spec into a CadQuery script, deterministically.

No model is consulted. The spec fully determines the geometry, which is what lets this node declare
`model_classes: []` and makes every build reproducible from its inputs alone. Text-to-CAD belongs behind
the broker callback, and that callback is still a no-op in the core (BrokerRef.None) - so generating code
from a description here would mean dialling a model directly behind the core's back, which is exactly the
bypass the legacy cadcoder already represents and this node exists not to repeat.

The script is executed by the legacy engine's sandbox (AST filtering, restricted builtins, a timeout), so
it must obey that sandbox's contract: assign the final geometry to `result`, import nothing but cadquery
and math.
"""

from __future__ import annotations

from typing import Any

SHAPES = ("box", "cylinder", "plate", "bracket", "flange")


class SpecError(ValueError):
    """The spec is wrong in a way the caller can fix. Maps to INVALID_INPUT."""


def build_script(spec: dict[str, Any]) -> str:
    """Validate a spec and return the CadQuery script that realises it."""
    shape = str(spec.get("shape", "")).strip().lower()
    if not shape:
        raise SpecError(f"spec.shape is required; expected one of {list(SHAPES)}.")
    if shape not in SHAPES:
        raise SpecError(f"spec.shape '{shape}' is not supported; expected one of {list(SHAPES)}.")

    dims = spec.get("dimensions_mm")
    if not isinstance(dims, dict):
        raise SpecError("spec.dimensions_mm is required and must be a JSON object.")

    features = spec.get("features", [])
    if not isinstance(features, list):
        raise SpecError("spec.features must be a list when present.")

    builder = {
        "box": _box,
        "cylinder": _cylinder,
        "plate": _plate,
        "bracket": _bracket,
        "flange": _flange,
    }[shape]
    return builder(dims, features)


# ── shapes ───────────────────────────────────────────────────────────────────────────────────────

def _box(dims: dict, features: list) -> str:
    length = _dim(dims, "length", "box")
    width = _dim(dims, "width", "box")
    height = _dim(dims, "height", "box")

    lines = [f"result = cq.Workplane('XY').box({length}, {width}, {height})"]
    lines += _holes(features, length, width, on_face=">Z")
    return "\n".join(lines)


def _cylinder(dims: dict, features: list) -> str:
    diameter = _dim(dims, "diameter", "cylinder")
    height = _dim(dims, "height", "cylinder")
    _reject_unsupported_features(features, "cylinder", allowed={"bore"})

    lines = [f"result = cq.Workplane('XY').circle({diameter / 2}).extrude({height})"]
    for feature in features:
        bore = _positive(feature, "diameter_mm", "bore")
        if bore >= diameter:
            raise SpecError(f"bore diameter {bore}mm must be smaller than the cylinder's {diameter}mm.")
        lines.append(f"result = result.faces('>Z').workplane().hole({bore})")
    return "\n".join(lines)


def _plate(dims: dict, features: list) -> str:
    length = _dim(dims, "length", "plate")
    width = _dim(dims, "width", "plate")
    thickness = _dim(dims, "thickness", "plate")

    lines = [f"result = cq.Workplane('XY').box({length}, {width}, {thickness})"]
    lines += _holes(features, length, width, on_face=">Z")
    return "\n".join(lines)


def _bracket(dims: dict, features: list) -> str:
    """An L-bracket: a base plate with an upstand along one edge."""
    length = _dim(dims, "length", "bracket")
    width = _dim(dims, "width", "bracket")
    height = _dim(dims, "height", "bracket")
    thickness = _dim(dims, "thickness", "bracket")

    if thickness >= height:
        raise SpecError(f"bracket thickness {thickness}mm must be less than its height {height}mm.")
    if thickness >= width:
        raise SpecError(f"bracket thickness {thickness}mm must be less than its width {width}mm.")

    # Two boxes unioned at the origin corner, rather than a swept profile: fewer kernel operations, and
    # the resulting solid is trivially watertight.
    lines = [
        f"base = cq.Workplane('XY').box({length}, {width}, {thickness}, centered=(True, True, False))",
        f"upstand = (cq.Workplane('XY')",
        f"    .box({length}, {thickness}, {height}, centered=(True, True, False))",
        f"    .translate((0, {-(width / 2) + (thickness / 2)}, 0)))",
        "result = base.union(upstand)",
    ]
    lines += _holes(features, length, width, on_face=">Z")
    return "\n".join(lines)


def _flange(dims: dict, features: list) -> str:
    """A hubbed pipe flange: plate, concentric hub, through bore, bolt circle, fillet, chamfer.

    The one shape here that exercises what the kernel is actually for — a polar feature pattern, a
    fillet on a specific found edge, and a chamfer on another — rather than boxes with holes in them.
    """
    od = _dim(dims, "outer_diameter", "flange")
    bore = _dim(dims, "bore_diameter", "flange")
    thickness = _dim(dims, "plate_thickness", "flange")
    hub_d = _dim(dims, "hub_diameter", "flange")
    hub_h = _dim(dims, "hub_height", "flange")

    if not bore < hub_d < od:
        raise SpecError(
            f"a flange needs bore ({bore}) < hub_diameter ({hub_d}) < outer_diameter ({od}).")

    bolt_holes = _one_feature(features, "bolt_holes", required=False)
    fillet = _one_feature(features, "fillet", required=False)
    chamfer = _one_feature(features, "chamfer", required=False)
    _reject_unsupported_features(features, "flange", allowed={"bolt_holes", "fillet", "chamfer"})

    lines = [
        f"result = cq.Workplane('XY').circle({od / 2}).extrude({thickness})",
        f"result = result.faces('>Z').workplane().circle({hub_d / 2}).extrude({hub_h})",
    ]

    # The fillet goes in BEFORE the bore and the bolt holes. Filleting a face that has already been
    # drilled asks the kernel to roll a radius across the hole breakouts, which is how this fails.
    if fillet is not None:
        radius = _positive(fillet, "radius_mm", "fillet")
        if radius >= min(thickness, hub_h):
            raise SpecError(
                f"fillet radius {radius}mm must be smaller than both plate_thickness ({thickness}) and "
                f"hub_height ({hub_h}).")
        if radius >= (hub_d - bore) / 2:
            raise SpecError(
                f"fillet radius {radius}mm does not fit in the {(hub_d - bore) / 2}mm hub wall.")
        # Selected by geometry rather than by a direction selector: the junction circle and the plate's
        # outer rim share a centre, so '>Z'-style selection cannot tell them apart and the kernel
        # refuses with "no suitable edges".
        lines += [
            f"junction = [e for e in result.edges().vals() if e.geomType() == 'CIRCLE'"
            f" and abs(e.Center().z - {thickness}) < 0.001 and abs(e.radius() - {hub_d / 2}) < 0.001]",
            f"result = result.newObject(junction).fillet({radius})",
        ]

    lines.append(f"result = result.faces('>Z').workplane().circle({bore / 2}).cutThruAll()")

    if bolt_holes is not None:
        bcd = _dim(dims, "bolt_circle_diameter", "flange with bolt_holes")
        count = bolt_holes.get("count", 4)
        if not isinstance(count, int) or isinstance(count, bool) or count < 2:
            raise SpecError(f"bolt_holes count must be a whole number of at least 2, got {count!r}.")
        hole_d = _positive(bolt_holes, "diameter_mm", "bolt_holes")

        if not hub_d < bcd < od:
            raise SpecError(
                f"bolt_circle_diameter ({bcd}) must sit between the hub ({hub_d}) and the rim ({od}).")
        if bcd / 2 + hole_d / 2 > od / 2:
            raise SpecError(f"d{hole_d}mm bolt holes on a {bcd}mm circle break through the {od}mm rim.")
        if bcd / 2 - hole_d / 2 < hub_d / 2:
            raise SpecError(f"d{hole_d}mm bolt holes on a {bcd}mm circle cut into the {hub_d}mm hub.")

        lines.append(
            f"result = result.faces('<Z').workplane().polarArray({bcd / 2}, 0, 360, {count})"
            f".circle({hole_d / 2}).cutThruAll()")

    if chamfer is not None:
        size = _positive(chamfer, "size_mm", "chamfer")
        if size >= bore / 2:
            raise SpecError(f"chamfer {size}mm is too large for a {bore}mm bore.")
        lines += [
            f"mouth = [e for e in result.edges().vals() if e.geomType() == 'CIRCLE'"
            f" and abs(e.Center().z - {thickness + hub_h}) < 0.001 and abs(e.radius() - {bore / 2}) < 0.001]",
            f"result = result.newObject(mouth).chamfer({size})",
        ]

    return "\n".join(lines)


def _one_feature(features: list, kind: str, required: bool) -> dict | None:
    matches = [f for f in features if isinstance(f, dict) and str(f.get("type", "")).lower() == kind]
    if len(matches) > 1:
        raise SpecError(f"a flange takes at most one '{kind}' feature, got {len(matches)}.")
    if not matches and required:
        raise SpecError(f"a flange needs a '{kind}' feature.")
    return matches[0] if matches else None


# ── features ─────────────────────────────────────────────────────────────────────────────────────

def _holes(features: list, length: float, width: float, on_face: str) -> list[str]:
    if not features:
        return []
    _reject_unsupported_features(features, "this shape", allowed={"hole"})

    lines = []
    for feature in features:
        diameter = _positive(feature, "diameter_mm", "hole")
        at = feature.get("at_mm", [0, 0])
        if not isinstance(at, (list, tuple)) or len(at) != 2:
            raise SpecError("a hole's at_mm must be a two-element [x, y] list, in mm from the centre.")
        x, y = (_number(at[0], "hole at_mm[0]"), _number(at[1], "hole at_mm[1]"))

        # Caught here rather than by the kernel: CadQuery will happily cut a hole straddling the edge and
        # hand back a shape nobody asked for.
        if abs(x) + diameter / 2 > length / 2 or abs(y) + diameter / 2 > width / 2:
            raise SpecError(
                f"a d{diameter}mm hole at ({x}, {y}) falls outside the {length}x{width}mm face.")

        # Each hole is its own statement starting from the solid, rather than one long chain. Chaining
        # them left the modelling stack empty for the second cut ("Can not return the Nth element of an
        # empty list"), because .hole() consumes the selection it was given.
        lines.append(
            f"result = result.faces('{on_face}').workplane(centerOption='CenterOfBoundBox')"
            f".center({x}, {y}).hole({diameter})"
        )
    return lines


def _reject_unsupported_features(features: list, shape: str, allowed: set[str]) -> None:
    for feature in features:
        if not isinstance(feature, dict):
            raise SpecError(f"each entry of spec.features must be a JSON object, got {type(feature).__name__}.")
        kind = str(feature.get("type", "")).strip().lower()
        if kind not in allowed:
            raise SpecError(
                f"feature type '{kind or '(missing)'}' is not supported for {shape}; "
                f"supported: {sorted(allowed)}.")


# ── primitives ───────────────────────────────────────────────────────────────────────────────────

def _dim(dims: dict, name: str, shape: str) -> float:
    if name not in dims:
        raise SpecError(f"a {shape} needs dimensions_mm.{name}.")
    return _positive(dims, name, shape)


def _positive(source: dict, key: str, what: str) -> float:
    value = _number(source.get(key), f"{what} {key}")
    if value <= 0:
        raise SpecError(f"{what} {key} must be greater than zero, got {value}.")
    return value


def _number(value: Any, what: str) -> float:
    if isinstance(value, bool) or not isinstance(value, (int, float)):
        raise SpecError(f"{what} must be a number, got {type(value).__name__}.")
    return float(value)
