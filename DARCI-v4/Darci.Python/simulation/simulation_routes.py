"""
Assembly simulation API routes.
"""

from typing import Optional

from fastapi import APIRouter
from pydantic import BaseModel, Field

from simulation.assembly_sim import simulate_assembly

router = APIRouter(prefix="/simulation", tags=["Simulation"])


class MotionSpec(BaseModel):
    type: Optional[str] = None
    axis: Optional[list[float]] = None
    rangeDeg: Optional[float] = None
    rangeMm: Optional[float] = None
    steps: Optional[int] = None
    pivotMm: Optional[list[float]] = None
    movingPart: Optional[str] = None


class SimulationPart(BaseModel):
    name: str
    partType: Optional[str] = None
    stlPath: Optional[str] = None
    x: float = 0.0
    y: float = 0.0
    z: float = 0.0
    rxDeg: float = 0.0
    ryDeg: float = 0.0
    rzDeg: float = 0.0


class SimulationConnection(BaseModel):
    from_: str = Field(alias="from")
    to: str
    relation: str = "connects"
    motion: Optional[MotionSpec] = None


class SimulationIssue(BaseModel):
    severity: str
    code: str
    message: str
    partA: Optional[str] = None
    partB: Optional[str] = None
    connection: Optional[str] = None


class MotionCheck(BaseModel):
    from_: str = Field(alias="from")
    to: str
    relation: str
    motionType: str
    passed: bool
    minClearanceMm: Optional[float] = None
    collisionSteps: list[int] = []


class AssemblySimulationRequest(BaseModel):
    parts: list[SimulationPart]
    connections: list[SimulationConnection] = []
    collisionToleranceMm: float = 0.1
    clearanceTargetMm: float = 0.2
    samplePointsPerMesh: int = 256


class AssemblySimulationResponse(BaseModel):
    passed: bool
    staticPairsChecked: int
    staticCollisionCount: int
    globalMinClearanceMm: Optional[float] = None
    motionChecks: list[MotionCheck] = []
    issues: list[SimulationIssue] = []


@router.post("/assembly", response_model=AssemblySimulationResponse)
async def simulate_assembly_route(req: AssemblySimulationRequest):
    def dump(model):
        if hasattr(model, "model_dump"):
            return model.model_dump(by_alias=True)
        return model.dict(by_alias=True)

    report = simulate_assembly(
        parts=[dump(p) for p in req.parts],
        connections=[dump(c) for c in req.connections],
        collision_tolerance_mm=req.collisionToleranceMm,
        clearance_target_mm=req.clearanceTargetMm,
        sample_points_per_mesh=req.samplePointsPerMesh,
    )
    return AssemblySimulationResponse(**report)
