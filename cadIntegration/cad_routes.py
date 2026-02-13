"""
DARCI CAD Routes — FastAPI endpoints called by CadBridge.cs
"""

from fastapi import APIRouter
from pydantic import BaseModel
from typing import Optional
from dataclasses import asdict

from cad.cad_engine import generate_and_validate, DimensionSpec

router = APIRouter(prefix="/cad", tags=["CAD"])


# ─── Request / Response Models ───────────────

class CadGenerateRequest(BaseModel):
    script: str
    filename: str = "output.stl"
    dimensions: Optional[dict] = None


class CadGenerateResponse(BaseModel):
    success: bool
    stl_path: Optional[str] = None
    render_images: dict = {}
    validation: Optional[dict] = None
    script_used: str = ""
    error: Optional[str] = None
    iteration: int = 0


class CadFeedbackRequest(BaseModel):
    original_request: str
    cad_result: dict


class CadFeedbackResponse(BaseModel):
    feedback_prompt: str


# ─── Endpoints ───────────────────────────────

@router.post("/generate", response_model=CadGenerateResponse)
async def cad_generate(req: CadGenerateRequest):
    """Execute a CadQuery script, validate, and render."""
    spec = None
    if req.dimensions:
        spec = DimensionSpec(
            length_mm=req.dimensions.get("length_mm"),
            width_mm=req.dimensions.get("width_mm"),
            height_mm=req.dimensions.get("height_mm"),
            features=req.dimensions.get("features", {}),
        )

    result = generate_and_validate(
        script=req.script, filename=req.filename,
        dimension_spec=spec, iteration=0)

    return CadGenerateResponse(
        success=result.success, stl_path=result.stl_path,
        render_images=result.render_images,
        validation=asdict(result.validation) if result.validation else None,
        script_used=result.script_used,
        error=result.error, iteration=result.iteration)


@router.post("/feedback-prompt", response_model=CadFeedbackResponse)
async def cad_feedback_prompt(req: CadFeedbackRequest):
    """Build a structured self-evaluation prompt from a generation result."""
    validation = req.cad_result.get("validation", {})
    error = req.cad_result.get("error")
    script = req.cad_result.get("script_used", "")
    iteration = req.cad_result.get("iteration", 0)

    sections = []
    sections.append(
        f"## CAD Review — Iteration {iteration + 1}\n\n"
        f"**Original request:** {req.original_request}\n")

    if error:
        sections.append(
            f"**EXECUTION ERROR:** {error}\n\n"
            f"Fix the script. Make the minimum change needed.\n")
    else:
        passed = validation.get("passed", False)
        bb = validation.get("bounding_box_mm", {})
        dim_errors = validation.get("dimension_errors", [])
        warnings = validation.get("warnings", [])
        watertight = validation.get("is_watertight", False)

        if passed:
            sections.append(
                "ALL CHECKS PASSED.\n\n"
                "**If correct, respond with ONLY the word `APPROVED`.** "
                "Do not change working code.\n")
        else:
            if not watertight:
                sections.append("Mesh is NOT watertight.\n")
            if dim_errors:
                sections.append("Dimension mismatches:\n")
                for de in dim_errors:
                    sections.append(
                        f"  - {de['dimension']}: expected {de['expected_mm']}mm, "
                        f"got {de['actual_mm']}mm (off by {de['error_mm']}mm)\n")
            if warnings:
                for w in warnings:
                    sections.append(f"  Warning: {w}\n")
            sections.append(
                f"\nBounding box: {bb.get('x', '?')} x {bb.get('y', '?')} x "
                f"{bb.get('z', '?')} mm\n")

    sections.append(f"\n**Current script:**\n```python\n{script}\n```\n")
    sections.append(
        "\nIf changes needed, output ONLY the corrected CadQuery script "
        "(assign to `result`). If correct, respond with ONLY `APPROVED`.\n")

    return CadFeedbackResponse(feedback_prompt="\n".join(sections))
