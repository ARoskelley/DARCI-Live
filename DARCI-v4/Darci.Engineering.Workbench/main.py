"""DARCI Geometry Workbench — FastAPI service.

Run:
    uvicorn main:app --host 127.0.0.1 --port 8001
"""

from __future__ import annotations

import os
from typing import Optional, List

import numpy as np
from fastapi import FastAPI, HTTPException, UploadFile, File
from pydantic import BaseModel, Field

from workbench.engine import GeometryEngine

app = FastAPI(title="DARCI Geometry Workbench", version="1.0.0")

engine = GeometryEngine()

# --------------------------------------------------------------------------- #
# Request / response models                                                    #
# --------------------------------------------------------------------------- #


class ResetRequest(BaseModel):
    reference_path: Optional[str] = None
    constraints: Optional[dict] = None
    targets: Optional[dict] = None


class ExecuteRequest(BaseModel):
    action_id: int = Field(ge=0, le=19)
    parameters: List[float] = Field(default_factory=list)


class BatchActionItem(BaseModel):
    action_id: int = Field(ge=0, le=19)
    parameters: List[float] = Field(default_factory=list)


class BatchExecuteRequest(BaseModel):
    actions: List[BatchActionItem]


class ExportRequest(BaseModel):
    format: str = Field(pattern="^(step|stl|both)$")


class LoadReferenceRequest(BaseModel):
    path: str


# --------------------------------------------------------------------------- #
# Endpoints                                                                    #
# --------------------------------------------------------------------------- #


@app.post("/workbench/reset")
def reset(req: ResetRequest):
    """Reset engine. Optionally load a reference STEP/STL and set constraints."""
    engine.reset(
        reference_path=req.reference_path,
        constraints=req.constraints,
        targets=req.targets,
    )
    state = engine.get_state().tolist()
    mask = engine.get_action_mask().tolist()
    return {"state": state, "action_mask": mask}


@app.get("/workbench/state")
def get_state():
    """Return current state vector."""
    return {
        "state": engine.get_state().tolist(),
        "step_count": engine.step_count,
        "is_active": engine.is_active,
    }


@app.get("/workbench/action-mask")
def get_action_mask():
    """Return valid action mask and names."""
    mask = engine.get_action_mask()
    action_names = [
        "extrude", "cut", "revolve", "add_cylinder", "add_box",
        "fillet_edges", "chamfer_edges", "shell", "add_hole", "add_boss",
        "add_rib", "translate_feature", "scale_feature", "mirror",
        "pattern_linear", "thicken_wall", "smooth_region", "remove_feature",
        "validate", "finalize",
    ]
    valid_names = [name for name, m in zip(action_names, mask) if m]
    return {"mask": mask.tolist(), "valid_action_names": valid_names}


@app.post("/workbench/execute")
def execute(req: ExecuteRequest):
    """Execute an action on the current geometry."""
    params = np.array(req.parameters, dtype=np.float32)
    # Pad or truncate to 6 parameters
    padded = np.zeros(6, dtype=np.float32)
    padded[:min(len(params), 6)] = params[:6]

    # Check mask
    mask = engine.get_action_mask()
    if not mask[req.action_id]:
        raise HTTPException(
            status_code=400,
            detail=f"Action {req.action_id} is masked (not valid in current state)",
        )

    result = engine.execute_action(req.action_id, padded)
    return result


@app.post("/workbench/validate")
def validate():
    """Run full validation suite."""
    return engine.validate()


@app.get("/workbench/metrics")
def get_metrics():
    """Return current quality metrics without full validation."""
    mesh = engine.current_mesh
    if mesh is None:
        return {"has_geometry": False}

    analyzer = engine.mesh_analyzer
    metrics: dict = {"has_geometry": True}

    if analyzer:
        metrics.update(analyzer.basic_metrics())
        metrics.update(analyzer.printability_analysis())
        metrics.update(analyzer.mesh_quality())

    return metrics


@app.post("/workbench/export")
def export(req: ExportRequest):
    """Export current geometry to STEP and/or STL."""
    if not engine.is_active:
        raise HTTPException(status_code=400, detail="No geometry to export")
    output_dir = os.path.join(os.path.dirname(__file__), "models")
    return engine.export(req.format, output_dir=output_dir)


@app.get("/workbench/health")
def health():
    """Service health check."""
    return {
        "status": "alive",
        "has_geometry": engine.is_active,
        "step_count": engine.step_count,
    }


@app.post("/workbench/undo")
def undo():
    """Revert last action."""
    success = engine.undo()
    if not success:
        raise HTTPException(status_code=400, detail="Nothing to undo")
    return {
        "state": engine.get_state().tolist(),
        "step_count": engine.step_count,
    }


@app.post("/workbench/batch-execute")
def batch_execute(req: BatchExecuteRequest):
    """Execute multiple actions sequentially."""
    results = []
    for item in req.actions:
        params = np.array(item.parameters, dtype=np.float32)
        padded = np.zeros(6, dtype=np.float32)
        padded[:min(len(params), 6)] = params[:6]

        mask = engine.get_action_mask()
        if not mask[item.action_id]:
            results.append({
                "state": engine.get_state().tolist(),
                "metrics": {},
                "success": False,
                "error_message": f"Action {item.action_id} masked",
                "reward_components": {},
            })
            continue

        result = engine.execute_action(item.action_id, padded)
        results.append(result)

    return {"results": results}


@app.post("/workbench/load-reference")
async def load_reference_file(file: UploadFile = File(None), body: Optional[LoadReferenceRequest] = None):
    """Load a reference geometry from a file path or uploaded file."""
    if file is not None:
        # Save upload to models/
        output_dir = os.path.join(os.path.dirname(__file__), "models")
        os.makedirs(output_dir, exist_ok=True)
        dest = os.path.join(output_dir, file.filename or "reference.stl")
        with open(dest, "wb") as f:
            f.write(await file.read())
        engine.load_reference(dest)
        return {"loaded": True, "path": dest}
    raise HTTPException(status_code=400, detail="Provide a file upload or path")


@app.post("/workbench/load-reference-path")
def load_reference_path(req: LoadReferenceRequest):
    """Load reference geometry from a local file path."""
    if not os.path.exists(req.path):
        raise HTTPException(status_code=404, detail=f"File not found: {req.path}")
    engine.load_reference(req.path)
    return {"loaded": True, "path": req.path}
