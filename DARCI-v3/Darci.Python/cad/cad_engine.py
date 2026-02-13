"""
DARCI CAD Engine
================
Sandboxed CadQuery script execution, mesh validation,
orthographic rendering, and dimensional verification.

Security mitigations (V1-V6) are documented inline.
"""

import ast
import os
import base64
import signal
import concurrent.futures
import traceback
from pathlib import Path
from dataclasses import dataclass, field
from typing import Optional

import trimesh
import numpy as np
import cadquery as cq


# ─── Configuration ───────────────────────────

CAD_OUTPUT_DIR = Path(os.getenv("CAD_OUTPUT_DIR", "/tmp/darci_cad"))
CAD_OUTPUT_DIR.mkdir(parents=True, exist_ok=True)

MAX_SCRIPT_LENGTH = 15_000
MAX_EXECUTION_SECONDS = 30
MAX_ITERATIONS = 5
DIMENSION_EPSILON = 0.05       # mm
MAX_TRIANGLE_COUNT = 500_000


# ─── Data Models ─────────────────────────────

@dataclass
class DimensionSpec:
    length_mm: Optional[float] = None
    width_mm: Optional[float] = None
    height_mm: Optional[float] = None
    features: dict = field(default_factory=dict)


@dataclass
class ValidationResult:
    is_watertight: bool = False
    triangle_count: int = 0
    bounding_box_mm: dict = field(default_factory=dict)
    dimension_errors: list = field(default_factory=list)
    volume_cc: float = 0.0
    warnings: list = field(default_factory=list)
    passed: bool = False


@dataclass
class CadResult:
    success: bool
    stl_path: Optional[str] = None
    render_images: dict = field(default_factory=dict)
    validation: Optional[ValidationResult] = None
    script_used: str = ""
    error: Optional[str] = None
    iteration: int = 0


# ─── V1: Script Safety (AST filtering) ──────

ALLOWED_IMPORTS = {"cadquery", "math", "cq"}

BLOCKED_CALLS = {
    "open", "exec", "eval", "compile", "__import__",
    "getattr", "setattr", "delattr", "globals", "locals",
    "breakpoint", "exit", "quit",
    "system", "popen", "subprocess", "socket",
}


class ScriptSecurityError(Exception):
    pass


def validate_script_safety(source: str) -> None:
    """V1: Parse the script AST and reject dangerous patterns."""
    if len(source) > MAX_SCRIPT_LENGTH:
        raise ScriptSecurityError(
            f"Script exceeds max length ({len(source)} > {MAX_SCRIPT_LENGTH})")

    try:
        tree = ast.parse(source)
    except SyntaxError as e:
        raise ScriptSecurityError(f"Script has syntax error: {e}")

    for node in ast.walk(tree):
        if isinstance(node, ast.Import):
            for alias in node.names:
                root = alias.name.split(".")[0]
                if root not in ALLOWED_IMPORTS:
                    raise ScriptSecurityError(
                        f"Import of '{alias.name}' not allowed. "
                        f"Permitted: {ALLOWED_IMPORTS}")

        if isinstance(node, ast.ImportFrom):
            if node.module:
                root = node.module.split(".")[0]
                if root not in ALLOWED_IMPORTS:
                    raise ScriptSecurityError(
                        f"Import from '{node.module}' not allowed.")

        if isinstance(node, ast.Call):
            func = node.func
            name = None
            if isinstance(func, ast.Name):
                name = func.id
            elif isinstance(func, ast.Attribute):
                name = func.attr
            if name and name.lower() in BLOCKED_CALLS:
                raise ScriptSecurityError(f"Call to '{name}' is blocked.")


# ─── V2: Sandboxed Execution (timeout + restricted namespace) ───

class ExecutionTimeout(Exception):
    pass


def _timeout_handler(signum, frame):
    raise ExecutionTimeout(f"Script exceeded {MAX_EXECUTION_SECONDS}s limit")


def _restricted_import(name, globals=None, locals=None, fromlist=(), level=0):
    root = name.split(".")[0]
    if root == "cadquery":
        return cq
    if root == "math":
        import math
        return math
    raise ImportError(f"Import of '{name}' is not allowed")


def _exec_script(source: str, restricted_globals: dict, local_ns: dict) -> None:
    exec(source, restricted_globals, local_ns)


def execute_cadquery_script(source: str) -> cq.Workplane:
    """Execute a CadQuery script in a restricted sandbox."""
    validate_script_safety(source)

    import math
    restricted_globals = {
        "__builtins__": {
            "range": range, "len": len, "int": int, "float": float,
            "str": str, "list": list, "tuple": tuple, "dict": dict,
            "min": min, "max": max, "abs": abs, "round": round,
            "enumerate": enumerate, "zip": zip,
            "True": True, "False": False, "None": None,
            "__import__": _restricted_import,
            "print": lambda *a, **kw: None,  # swallow prints
        },
        "cq": cq,
        "cadquery": cq,
        "math": math,
    }

    local_ns = {}

    if hasattr(signal, "SIGALRM"):
        old_handler = signal.signal(signal.SIGALRM, _timeout_handler)
        signal.alarm(MAX_EXECUTION_SECONDS)
        try:
            _exec_script(source, restricted_globals, local_ns)
        except ExecutionTimeout:
            raise
        except Exception as e:
            raise RuntimeError(f"CadQuery script failed: {e}")
        finally:
            signal.alarm(0)
            signal.signal(signal.SIGALRM, old_handler)
    else:
        # Windows fallback: SIGALRM is unavailable.
        try:
            with concurrent.futures.ThreadPoolExecutor(max_workers=1) as executor:
                future = executor.submit(
                    _exec_script, source, restricted_globals, local_ns)
                future.result(timeout=MAX_EXECUTION_SECONDS)
        except concurrent.futures.TimeoutError:
            raise ExecutionTimeout(
                f"Script exceeded {MAX_EXECUTION_SECONDS}s limit")
        except Exception as e:
            raise RuntimeError(f"CadQuery script failed: {e}")

    if "result" not in local_ns:
        raise RuntimeError(
            "Script must assign final geometry to `result`. "
            "Example: result = cq.Workplane('XY').box(10, 10, 5)")

    return local_ns["result"]


# ─── V3: Mesh Validation ────────────────────

def validate_mesh(
    mesh: trimesh.Trimesh,
    spec: Optional[DimensionSpec] = None
) -> ValidationResult:
    """Validate an exported mesh for CNC suitability."""
    result = ValidationResult()
    result.is_watertight = bool(mesh.is_watertight)
    result.triangle_count = len(mesh.faces)
    result.volume_cc = float(mesh.volume / 1000.0)

    areas = mesh.area_faces
    degenerate_count = int(np.sum(areas < 1e-10))
    if degenerate_count > 0:
        result.warnings.append(
            f"{degenerate_count} degenerate triangles detected")

    if not mesh.is_watertight:
        result.warnings.append(
            "Mesh is not watertight — CNC toolpath generation may fail.")

    if result.triangle_count > MAX_TRIANGLE_COUNT:
        result.warnings.append(
            f"Triangle count ({result.triangle_count}) exceeds "
            f"max ({MAX_TRIANGLE_COUNT}).")

    bb = mesh.bounding_box.extents
    result.bounding_box_mm = {
        "x": round(float(bb[0]), 3),
        "y": round(float(bb[1]), 3),
        "z": round(float(bb[2]), 3),
    }

    if spec:
        _check_dimensions(result, spec)

    result.passed = (
        result.is_watertight
        and len(result.dimension_errors) == 0
        and result.triangle_count <= MAX_TRIANGLE_COUNT
        and degenerate_count == 0
    )

    return result


def _check_dimensions(result: ValidationResult, spec: DimensionSpec):
    """V5: Check bounding box against spec with epsilon tolerance."""
    bb = result.bounding_box_mm
    checks = [
        ("length", spec.length_mm, bb.get("x")),
        ("width", spec.width_mm, bb.get("y")),
        ("height", spec.height_mm, bb.get("z")),
    ]
    for name, expected, actual in checks:
        if expected is not None and actual is not None:
            diff = abs(expected - actual)
            if diff > DIMENSION_EPSILON:
                result.dimension_errors.append({
                    "dimension": name,
                    "expected_mm": expected,
                    "actual_mm": actual,
                    "error_mm": round(diff, 4),
                })


# ─── Rendering ───────────────────────────────

def render_orthographic_views(
    mesh: trimesh.Trimesh,
    resolution=(800, 600)
) -> dict:
    """Render front/top/right views as base64 PNGs."""
    views = {}
    view_rotations = {
        "front": trimesh.transformations.rotation_matrix(0, [0, 1, 0]),
        "top": trimesh.transformations.rotation_matrix(
            -np.pi / 2, [1, 0, 0]),
        "right": trimesh.transformations.rotation_matrix(
            np.pi / 2, [0, 1, 0]),
    }

    for view_name, rotation in view_rotations.items():
        try:
            rotated = mesh.copy()
            rotated.apply_transform(rotation)
            scene = trimesh.Scene(geometry=[rotated])
            png_bytes = scene.save_image(resolution=resolution)
            views[view_name] = (
                base64.b64encode(png_bytes).decode("utf-8")
                if png_bytes else None
            )
        except Exception:
            views[view_name] = None

    return views


# ─── V6: Safe File Export ────────────────────

def safe_export_path(filename: str) -> Path:
    """Lock exports to CAD_OUTPUT_DIR, reject traversal attempts."""
    clean_name = Path(filename).name
    if ".." in clean_name or "/" in clean_name or "\\" in clean_name:
        raise ValueError(f"Invalid filename: {filename}")
    return CAD_OUTPUT_DIR / clean_name


# ─── Main Pipeline ───────────────────────────

def generate_and_validate(
    script: str,
    filename: str = "output.stl",
    dimension_spec: Optional[DimensionSpec] = None,
    iteration: int = 0,
) -> CadResult:
    """Full pipeline: execute script → export STL → validate → render."""

    if iteration >= MAX_ITERATIONS:
        return CadResult(
            success=False,
            error=f"Reached max iterations ({MAX_ITERATIONS}).",
            script_used=script,
            iteration=iteration,
        )

    try:
        workplane = execute_cadquery_script(script)
        stl_path = safe_export_path(filename)
        cq.exporters.export(workplane, str(stl_path), exportType="STL")

        mesh = trimesh.load_mesh(str(stl_path))
        if not isinstance(mesh, trimesh.Trimesh):
            return CadResult(
                success=False,
                error="Export produced a scene, not a single solid.",
                script_used=script, iteration=iteration)

        validation = validate_mesh(mesh, dimension_spec)
        renders = render_orthographic_views(mesh)

        return CadResult(
            success=True, stl_path=str(stl_path),
            render_images=renders, validation=validation,
            script_used=script, iteration=iteration)

    except ScriptSecurityError as e:
        return CadResult(success=False, error=f"SECURITY: {e}",
                         script_used=script, iteration=iteration)
    except ExecutionTimeout as e:
        return CadResult(success=False, error=str(e),
                         script_used=script, iteration=iteration)
    except Exception as e:
        return CadResult(success=False,
                         error=f"Pipeline error: {e}\n{traceback.format_exc()}",
                         script_used=script, iteration=iteration)
