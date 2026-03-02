"""
CadCoder adapter routes.
Provides a local endpoint that can:
1) generate CadQuery code via Ollama
2) fall back to deterministic typed scripts
"""

from __future__ import annotations

import json
import math
import os
import re
import urllib.error
import urllib.request
from typing import Dict, Optional

from fastapi import APIRouter, Header, HTTPException
from pydantic import BaseModel

router = APIRouter(prefix="/cadcoder", tags=["CadCoder"])


class CadCoderGenerateRequest(BaseModel):
    prompt: str
    part_type: Optional[str] = None
    parameters: Optional[Dict[str, float]] = None
    dimensions: Optional[Dict[str, Optional[float]]] = None


class CadCoderGenerateResponse(BaseModel):
    success: bool
    provider: str = "cadcoder-adapter"
    source: str = ""
    script: Optional[str] = None
    error: Optional[str] = None


@router.post("/generate", response_model=CadCoderGenerateResponse)
async def cadcoder_generate(
    req: CadCoderGenerateRequest,
    authorization: Optional[str] = Header(default=None),
):
    _validate_adapter_auth(authorization)

    prompt = req.prompt or ""
    parameters = req.parameters or {}
    dimensions = req.dimensions or {}
    inferred_type = _infer_part_type(prompt, req.part_type)

    upstream_prompt = _build_upstream_prompt(
        prompt=prompt,
        part_type=inferred_type,
        parameters=parameters,
        dimensions=dimensions,
    )

    source = ""
    script = ""
    last_error = ""

    provider = (os.getenv("CADCODER_PROVIDER", "ollama") or "ollama").strip().lower()
    if provider in ("ollama", "auto"):
        try:
            script = _generate_via_ollama(upstream_prompt)
            if script:
                source = f"ollama:{_ollama_model()}"
        except Exception as ex:
            last_error = str(ex)

    if not script:
        script = _build_deterministic_script(
            description=prompt,
            part_type=inferred_type,
            parameters=parameters,
            dimensions=dimensions,
        )
        if script:
            source = "deterministic"

    if not script:
        return CadCoderGenerateResponse(
            success=False,
            source=source or provider,
            error=last_error or "No CAD script generated.",
        )

    return CadCoderGenerateResponse(
        success=True,
        source=source,
        script=script,
    )


def _validate_adapter_auth(authorization: Optional[str]) -> None:
    required_key = (os.getenv("CADCODER_ADAPTER_API_KEY") or "").strip()
    if not required_key:
        return

    if not authorization:
        raise HTTPException(status_code=401, detail="Missing authorization header.")

    auth = authorization.strip()
    token = auth[7:].strip() if auth.lower().startswith("bearer ") else auth
    if token != required_key:
        raise HTTPException(status_code=401, detail="Invalid bearer token.")


def _build_upstream_prompt(
    prompt: str,
    part_type: str,
    parameters: Dict[str, float],
    dimensions: Dict[str, Optional[float]],
) -> str:
    dim_parts = []
    for key in ("length_mm", "width_mm", "height_mm"):
        value = dimensions.get(key)
        if value is not None:
            dim_parts.append(f"{key}={_fmt(value)}")

    param_parts = [f"{k}={_fmt(v)}" for k, v in parameters.items()]

    dim_line = f"\nDimensions: {', '.join(dim_parts)}" if dim_parts else ""
    type_line = f"\nPart type: {part_type}" if part_type else ""
    param_line = f"\nParameters: {', '.join(param_parts)}" if param_parts else ""

    return (
        "Create a CadQuery Python script for this engineering request.\n"
        "Rules:\n"
        "1) Output only Python code.\n"
        "2) Import only cadquery as cq and math.\n"
        "3) Assign final watertight solid to variable named result.\n"
        "4) Use millimeters.\n\n"
        f"Request: {prompt}{dim_line}{type_line}{param_line}\n"
    )


def _generate_via_ollama(prompt: str) -> str:
    endpoint = (
        os.getenv("CADCODER_OLLAMA_URL")
        or os.getenv("OLLAMA_BASE_URL")
        or "http://127.0.0.1:11434/api/generate"
    )

    payload = {
        "model": _ollama_model(),
        "prompt": prompt,
        "stream": False,
    }

    req = urllib.request.Request(
        endpoint,
        data=json.dumps(payload).encode("utf-8"),
        headers={"Content-Type": "application/json"},
        method="POST",
    )

    try:
        with urllib.request.urlopen(req, timeout=45) as response:
            body = response.read().decode("utf-8")
    except urllib.error.HTTPError as ex:
        error_body = ex.read().decode("utf-8", errors="ignore")
        raise RuntimeError(f"Ollama HTTP {ex.code}: {error_body[:220]}") from ex
    except urllib.error.URLError as ex:
        raise RuntimeError(f"Ollama unavailable: {ex.reason}") from ex

    try:
        parsed = json.loads(body)
    except json.JSONDecodeError as ex:
        raise RuntimeError("Ollama response was not valid JSON.") from ex

    raw = parsed.get("response", "")
    return _extract_script(raw)


def _ollama_model() -> str:
    return (os.getenv("CADCODER_OLLAMA_MODEL") or "gemma2:9b").strip()


def _extract_script(output: str) -> str:
    if not output:
        return ""

    trimmed = output.strip()
    fenced = re.search(r"```python\s*(.*?)```", trimmed, re.IGNORECASE | re.DOTALL)
    if fenced:
        trimmed = fenced.group(1).strip()
    elif trimmed.startswith("```"):
        generic = re.search(r"```\s*(.*?)```", trimmed, re.DOTALL)
        if generic:
            trimmed = generic.group(1).strip()

    if "result" not in trimmed or "cq." not in trimmed:
        return ""

    return _normalize_script(trimmed)


def _normalize_script(script: str) -> str:
    return (
        script.replace(".Circle(", ".circle(")
        .replace(".Rect(", ".rect(")
        .replace(".Box(", ".box(")
        .replace("cq.Circle(", "cq.Workplane(\"XY\").circle(")
        .replace("cq.Rect(", "cq.Workplane(\"XY\").rect(")
        .replace("cq.Box(", "cq.Workplane(\"XY\").box(")
    )


def _build_deterministic_script(
    description: str,
    part_type: str,
    parameters: Dict[str, float],
    dimensions: Dict[str, Optional[float]],
) -> str:
    kind = _infer_part_type(description, part_type)

    if kind == "gear":
        return _build_gear_script(parameters, dimensions)
    if kind == "shaft":
        return _build_shaft_script(parameters, dimensions)
    if kind == "bearing":
        return _build_bearing_script(parameters, dimensions)
    if kind == "pin":
        return _build_pin_script(parameters, dimensions)
    if kind == "housing":
        return _build_housing_script(parameters, dimensions)
    if kind == "plate":
        return _build_plate_script(parameters, dimensions)
    if kind == "bracket":
        return _build_bracket_script(dimensions)

    return _build_box_script(dimensions)


def _build_box_script(dimensions: Dict[str, Optional[float]]) -> str:
    length = max(8.0, _d(dimensions, "length_mm", 30.0))
    width = max(8.0, _d(dimensions, "width_mm", 20.0))
    height = max(4.0, _d(dimensions, "height_mm", 10.0))

    return f"""import cadquery as cq
import math

length = {_fmt(length)}
width = {_fmt(width)}
height = {_fmt(height)}

result = cq.Workplane("XY").box(length, width, height)
"""


def _build_bracket_script(dimensions: Dict[str, Optional[float]]) -> str:
    length = max(40.0, _d(dimensions, "length_mm", 60.0))
    width = max(20.0, _d(dimensions, "width_mm", 30.0))
    height = max(20.0, _d(dimensions, "height_mm", 40.0))
    thickness = max(4.0, min(length, width, height) * 0.12)
    hole_d = max(4.0, thickness * 0.8)
    offset = max(thickness * 1.8, hole_d * 1.4)

    return f"""import cadquery as cq
import math

length = {_fmt(length)}
width = {_fmt(width)}
height = {_fmt(height)}
thickness = {_fmt(thickness)}
hole_dia = {_fmt(hole_d)}
offset = {_fmt(offset)}

base = cq.Workplane("XY").box(length, width, thickness)
upright = cq.Workplane("XY").box(thickness, width, height).translate(
    (-(length / 2.0) + (thickness / 2.0), 0, (height / 2.0) - (thickness / 2.0))
)
part = base.union(upright)
part = part.faces(">Z").workplane().pushPoints([
    (-(length / 2.0) + offset, 0),
    ((length / 2.0) - offset, 0)
]).hole(hole_dia)

result = part
"""


def _build_plate_script(parameters: Dict[str, float], dimensions: Dict[str, Optional[float]]) -> str:
    length = max(20.0, _d(dimensions, "length_mm", _p(parameters, "length_mm", 100.0)))
    width = max(20.0, _d(dimensions, "width_mm", _p(parameters, "width_mm", 60.0)))
    thickness = max(2.0, _d(dimensions, "height_mm", _p(parameters, "thickness_mm", 6.0)))
    hole_d = max(2.0, _p(parameters, "hole_diameter_mm", 6.0))
    inset = max(hole_d * 1.3, _p(parameters, "hole_inset_mm", 12.0))

    return f"""import cadquery as cq
import math

length = {_fmt(length)}
width = {_fmt(width)}
thickness = {_fmt(thickness)}
hole_dia = {_fmt(hole_d)}
inset = {_fmt(inset)}

result = cq.Workplane("XY").box(length, width, thickness)
result = result.faces(">Z").workplane().pushPoints([
    (-(length/2.0)+inset, -(width/2.0)+inset),
    ((length/2.0)-inset, -(width/2.0)+inset),
    (-(length/2.0)+inset, (width/2.0)-inset),
    ((length/2.0)-inset, (width/2.0)-inset)
]).hole(hole_dia)
"""


def _build_pin_script(parameters: Dict[str, float], dimensions: Dict[str, Optional[float]]) -> str:
    length = max(4.0, _d(dimensions, "length_mm", _p(parameters, "length_mm", 20.0)))
    diameter = max(1.0, _d(dimensions, "width_mm", _p(parameters, "diameter_mm", 3.0)))
    chamfer = min(diameter * 0.25, max(0.0, _p(parameters, "chamfer_mm", 0.3)))

    return f"""import cadquery as cq
import math

length = {_fmt(length)}
diameter = {_fmt(diameter)}
chamfer = {_fmt(chamfer)}

result = cq.Workplane("XY").circle(diameter / 2.0).extrude(length)
if chamfer > 0.01:
    result = result.faces(">Z").edges().chamfer(chamfer)
    result = result.faces("<Z").edges().chamfer(chamfer)
"""


def _build_shaft_script(parameters: Dict[str, float], dimensions: Dict[str, Optional[float]]) -> str:
    length = max(10.0, _d(dimensions, "length_mm", _p(parameters, "length_mm", 120.0)))
    diameter = max(2.0, _d(dimensions, "width_mm", _p(parameters, "diameter_mm", 12.0)))
    shoulder_d = max(diameter, _p(parameters, "shoulder_diameter_mm", diameter * 1.35))
    shoulder_l = max(0.0, _p(parameters, "shoulder_length_mm", min(length * 0.25, 24.0)))

    return f"""import cadquery as cq
import math

length = {_fmt(length)}
diameter = {_fmt(diameter)}
shoulder_diameter = {_fmt(shoulder_d)}
shoulder_length = {_fmt(shoulder_l)}

shaft = cq.Workplane("XY").circle(diameter / 2.0).extrude(length)
if shoulder_length > 0.01:
    shoulder = cq.Workplane("XY").circle(shoulder_diameter / 2.0).extrude(shoulder_length)
    shoulder = shoulder.translate((0, 0, (length - shoulder_length) / 2.0))
    shaft = shaft.union(shoulder)

result = shaft
"""


def _build_bearing_script(parameters: Dict[str, float], dimensions: Dict[str, Optional[float]]) -> str:
    outer = max(4.0, _d(dimensions, "length_mm", _p(parameters, "outer_diameter_mm", 22.0)))
    width = max(2.0, _d(dimensions, "height_mm", _p(parameters, "width_mm", 7.0)))
    inner = max(1.0, _p(parameters, "inner_diameter_mm", max(outer * 0.45, 4.0)))
    if inner >= outer - 1.0:
        inner = outer - 1.0

    return f"""import cadquery as cq
import math

outer_d = {_fmt(outer)}
inner_d = {_fmt(inner)}
width = {_fmt(width)}

ring = cq.Workplane("XY").circle(outer_d / 2.0).extrude(width)
result = ring.faces(">Z").workplane().hole(inner_d)
"""


def _build_housing_script(parameters: Dict[str, float], dimensions: Dict[str, Optional[float]]) -> str:
    length = max(20.0, _d(dimensions, "length_mm", _p(parameters, "length_mm", 80.0)))
    width = max(20.0, _d(dimensions, "width_mm", _p(parameters, "width_mm", 50.0)))
    height = max(20.0, _d(dimensions, "height_mm", _p(parameters, "height_mm", 55.0)))
    wall = max(2.0, _p(parameters, "wall_mm", 4.0))
    bore = max(4.0, _p(parameters, "center_bore_mm", min(length, width) * 0.35))

    return f"""import cadquery as cq
import math

length = {_fmt(length)}
width = {_fmt(width)}
height = {_fmt(height)}
wall = {_fmt(wall)}
center_bore = {_fmt(bore)}

outer = cq.Workplane("XY").box(length, width, height)
inner = cq.Workplane("XY").box(max(length - 2.0 * wall, 2.0), max(width - 2.0 * wall, 2.0), max(height - wall, 2.0))
inner = inner.translate((0, 0, wall / 2.0))
part = outer.cut(inner)
result = part.faces(">Z").workplane().hole(center_bore)
"""


def _build_gear_script(parameters: Dict[str, float], dimensions: Dict[str, Optional[float]]) -> str:
    teeth = max(8, int(round(_p(parameters, "teeth", 20.0))))
    module = max(0.6, _p(parameters, "module", 2.0))
    face_width = max(2.0, _d(dimensions, "height_mm", _p(parameters, "face_width_mm", 10.0)))
    bore = max(1.0, _p(parameters, "bore_diameter_mm", 8.0))
    pressure = max(14.5, min(30.0, _p(parameters, "pressure_angle_deg", 20.0)))

    pitch = teeth * module
    root_d = max(2.0, pitch - 2.5 * module)
    outer_d = pitch + 2.0 * module
    tooth_h = max(0.8, (outer_d - root_d) / 2.0)
    tooth_w = max(0.8, (math.pi * pitch / teeth) * 0.45)

    return f"""import cadquery as cq
import math

teeth = {teeth}
face_width = {_fmt(face_width)}
bore_diameter = {_fmt(bore)}
pressure_angle_deg = {_fmt(pressure)}
root_d = {_fmt(root_d)}
outer_d = {_fmt(outer_d)}
tooth_h = {_fmt(tooth_h)}
tooth_w = {_fmt(tooth_w)}

root = cq.Workplane("XY").circle(root_d / 2.0).extrude(face_width)
tooth = cq.Workplane("XY").rect(tooth_w, tooth_h).extrude(face_width)
tooth = tooth.translate((root_d / 2.0 + tooth_h / 2.0, 0, 0))

gear = root
for i in range(teeth):
    gear = gear.union(tooth.rotate((0,0,0), (0,0,1), i * (360.0 / teeth)))

result = gear.faces(">Z").workplane().hole(bore_diameter)
"""


def _d(dimensions: Dict[str, Optional[float]], key: str, fallback: float) -> float:
    value = dimensions.get(key)
    return float(value) if value is not None else fallback


def _p(parameters: Dict[str, float], key: str, fallback: float) -> float:
    value = parameters.get(key)
    return float(value) if value is not None else fallback


def _fmt(value: float) -> str:
    return f"{float(value):0.3f}"


def _infer_part_type(description: str, explicit_type: Optional[str]) -> str:
    normalized = _normalize_part_type(explicit_type)
    if normalized:
        return normalized

    text = (description or "").strip().lower()
    if not text:
        return ""

    if _contains_any(text, "housing", "axle box", "axlebox", "gearbox", "enclosure", "case"):
        return "housing"
    if _contains_any(text, "bearing", "bushing"):
        return "bearing"
    if _contains_any(text, "driveshaft", "drive shaft", "shaft", "axle", "spindle", "rod"):
        return "shaft"
    if _contains_any(text, "pin", "dowel", "retaining pin", "roll pin"):
        return "pin"
    if _contains_any(text, "gear", "sprocket", "toothed wheel"):
        return "gear"
    if _contains_any(text, "plate", "panel", "flange"):
        return "plate"
    if _contains_any(text, "bracket", "mount"):
        return "bracket"
    return ""


def _normalize_part_type(part_type: Optional[str]) -> str:
    if not part_type:
        return ""

    normalized = part_type.strip().lower()
    aliases = {
        "axle": "shaft",
        "axle-shaft": "shaft",
        "driveshaft": "shaft",
        "drive-shaft": "shaft",
        "rod": "shaft",
        "dowel": "pin",
        "bushing": "bearing",
        "gearbox": "housing",
        "axlebox": "housing",
        "axle-box": "housing",
        "enclosure": "housing",
        "case": "housing",
        "mount": "bracket",
    }
    return aliases.get(normalized, normalized)


def _contains_any(text: str, *tokens: str) -> bool:
    for token in tokens:
        if token in text:
            return True
    return False
