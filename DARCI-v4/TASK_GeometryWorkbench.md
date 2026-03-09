# Claude Code Task: Build the Geometry Workbench Python Service

## Context

Read `DARCI-v4/ENGINEERING_ARCHITECTURE.md` before starting. This task builds
the Python service described in sections 2 and 7. It is the first engineering
tool — the foundation for DARCI's 3D reasoning capability.

The service wraps CadQuery (for parametric geometry operations) and trimesh
(for mesh analysis) behind a stateful FastAPI REST API. DARCI's C# runtime
calls this service through HTTP. The neural network sends action IDs and
parameters; the service executes them on a live 3D model and returns the
new state vector and quality metrics.

## Project Location

```
DARCI-v4/Darci.Engineering.Workbench/
├── requirements.txt
├── main.py
├── workbench/
│   ├── __init__.py
│   ├── engine.py
│   ├── state_encoder.py
│   ├── action_executor.py
│   ├── mesh_analyzer.py
│   ├── validator.py
│   ├── constraint_checker.py
│   └── reference_comparator.py
├── models/
│   └── .gitkeep
└── tests/
    └── test_workbench.py
```

## Dependencies (requirements.txt)

```
fastapi>=0.104.0
uvicorn>=0.24.0
cadquery>=2.4.0
trimesh>=4.0.0
numpy>=1.24.0
scipy>=1.11.0
pydantic>=2.0.0
```

## File-by-File Implementation Guide

### main.py — FastAPI Application

The entry point. Defines all REST endpoints and manages the workbench session.

```python
from fastapi import FastAPI, HTTPException
from pydantic import BaseModel
import numpy as np

app = FastAPI(title="DARCI Geometry Workbench", version="1.0.0")

# Single workbench instance (stateful)
from workbench.engine import GeometryEngine
engine = GeometryEngine()
```

**Endpoints to implement:**

```
POST /workbench/reset
    Body: { reference_path?: string, constraints?: dict, targets?: dict }
    → Resets engine. Optionally loads a reference STEP/STL.
    → Returns: { state: float[64], action_mask: bool[20] }

GET /workbench/state
    → Returns: { state: float[64], step_count: int, is_active: bool }

GET /workbench/action-mask
    → Returns: { mask: bool[20], valid_action_names: string[] }

POST /workbench/execute
    Body: { action_id: int, parameters: float[6] }
    → Executes the action on the current geometry.
    → Returns: {
        state: float[64],
        metrics: dict<string, float>,
        success: bool,
        error_message?: string,
        reward_components: dict<string, float>
    }

POST /workbench/validate
    → Runs full validation suite.
    → Returns: {
        passed: bool,
        overall_score: float,
        category_scores: dict<string, float>,
        violations: list[{ category, severity, description, value, threshold, location }]
    }

GET /workbench/metrics
    → Returns current quality metrics without running full validation.

POST /workbench/export
    Body: { format: "step" | "stl" | "both" }
    → Returns: { step_path?: string, stl_path?: string }

GET /workbench/health
    → Returns: { status: "alive", has_geometry: bool, step_count: int }

POST /workbench/undo
    → Reverts last action. Returns new state.

POST /workbench/batch-execute
    Body: { actions: list[{ action_id: int, parameters: float[6] }] }
    → Executes multiple actions sequentially. Returns list of results.
    → Used for fast simulation training.

POST /workbench/load-reference
    Body: { path: string } or multipart file upload
    → Loads reference geometry for comparison metrics.
```

### workbench/engine.py — Geometry Engine

The core. Manages the CadQuery workplane and provides the action execution layer.

**Key responsibilities:**
- Maintain the current CadQuery `Workplane` or `Assembly` object
- Keep a history stack for undo
- Convert between CadQuery B-rep and trimesh triangle mesh
- Provide thread-safe access (lock on mutations, concurrent reads OK)

```python
import cadquery as cq
import trimesh
import numpy as np
from threading import Lock
from typing import Optional
from .mesh_analyzer import MeshAnalyzer
from .state_encoder import StateEncoder
from .action_executor import ActionExecutor
from .validator import Validator
from .constraint_checker import ConstraintChecker
from .reference_comparator import ReferenceComparator


class GeometryEngine:
    def __init__(self):
        self._workplane: Optional[cq.Workplane] = None
        self._mesh: Optional[trimesh.Trimesh] = None
        self._reference_mesh: Optional[trimesh.Trimesh] = None
        self._history: list = []
        self._step_count: int = 0
        self._constraints: dict = {}
        self._lock = Lock()
        self._max_history = 50

        self.state_encoder = StateEncoder()
        self.action_executor = ActionExecutor()
        self.mesh_analyzer: Optional[MeshAnalyzer] = None
        self.validator = Validator()
        self.constraint_checker = ConstraintChecker()
        self.reference_comparator = ReferenceComparator()

        # Track metrics for reward computation
        self._prev_metrics: Optional[dict] = None

    def reset(self, reference_path=None, constraints=None, targets=None):
        """Reset to empty state, optionally loading reference geometry."""
        with self._lock:
            self._workplane = None
            self._mesh = None
            self._history = []
            self._step_count = 0
            self._prev_metrics = None
            self._constraints = constraints or {}

            if reference_path:
                self._load_reference(reference_path)

    def execute_action(self, action_id: int, parameters: np.ndarray) -> dict:
        """
        Execute an action and return results.

        1. Save current state to history (for undo)
        2. Map network parameters to physical dimensions
        3. Apply the CadQuery operation
        4. Re-tessellate to update mesh
        5. Compute new state vector
        6. Compute reward components from metric deltas
        """
        with self._lock:
            # Save state for undo
            self._push_history()

            # Execute
            success, error = self.action_executor.execute(
                action_id, parameters, self
            )

            if success:
                self._update_mesh()
                self._step_count += 1

            # Compute new state and reward
            state = self.state_encoder.encode(self)
            metrics = self._compute_metrics()
            reward_components = self._compute_reward(metrics, success)
            self._prev_metrics = metrics

            return {
                "state": state.tolist(),
                "metrics": metrics,
                "success": success,
                "error_message": error,
                "reward_components": reward_components,
            }

    def get_state(self) -> np.ndarray:
        """Get current 64-dimensional state vector."""
        return self.state_encoder.encode(self)

    def get_action_mask(self) -> np.ndarray:
        """Get valid actions mask."""
        mask = np.ones(20, dtype=bool)

        if self._workplane is None:
            # No geometry — only creation actions allowed
            mask[:] = False
            mask[3] = True   # add_cylinder
            mask[4] = True   # add_box

        if self._mesh is not None:
            if not self._mesh.is_watertight:
                mask[7] = False   # shell requires watertight
                mask[15] = False  # thicken_wall requires watertight

        # Control actions always available
        mask[18] = True  # validate
        mask[19] = True  # finalize

        # If consecutive failures > 5, limit to repair + control
        # (tracked in state_encoder)

        return mask

    @property
    def is_active(self) -> bool:
        return self._workplane is not None

    @property
    def current_mesh(self) -> Optional[trimesh.Trimesh]:
        return self._mesh

    @property
    def current_workplane(self) -> Optional[cq.Workplane]:
        return self._workplane

    @current_workplane.setter
    def current_workplane(self, wp: cq.Workplane):
        self._workplane = wp

    @property
    def reference_mesh(self) -> Optional[trimesh.Trimesh]:
        return self._reference_mesh

    @property
    def constraints(self) -> dict:
        return self._constraints

    @property
    def step_count(self) -> int:
        return self._step_count

    def _update_mesh(self):
        """Re-tessellate CadQuery B-rep to trimesh."""
        if self._workplane is None:
            self._mesh = None
            return

        try:
            # CadQuery → STL vertices/faces → trimesh
            # Use CadQuery's tessellation
            solid = self._workplane.val()
            vertices, triangles = solid.tessellate(0.1)  # tolerance in mm

            verts = np.array([(v.x, v.y, v.z) for v in vertices])
            faces = np.array([(t[0], t[1], t[2]) for t in triangles])

            self._mesh = trimesh.Trimesh(vertices=verts, faces=faces)
            self.mesh_analyzer = MeshAnalyzer(self._mesh)
        except Exception:
            self._mesh = None
            self.mesh_analyzer = None

    def _push_history(self):
        """Save current state for undo."""
        if self._workplane is not None:
            # Deep copy the workplane (CadQuery objects are immutable-ish)
            self._history.append(self._workplane)
            if len(self._history) > self._max_history:
                self._history.pop(0)

    def undo(self) -> bool:
        """Revert to previous state."""
        with self._lock:
            if not self._history:
                return False
            self._workplane = self._history.pop()
            self._update_mesh()
            self._step_count = max(0, self._step_count - 1)
            return True

    def _load_reference(self, path: str):
        """Load reference geometry for comparison."""
        try:
            if path.endswith('.step') or path.endswith('.stp'):
                result = cq.importers.importStep(path)
                # Tessellate to mesh
                solid = result.val()
                vertices, triangles = solid.tessellate(0.1)
                verts = np.array([(v.x, v.y, v.z) for v in vertices])
                faces = np.array([(t[0], t[1], t[2]) for t in triangles])
                self._reference_mesh = trimesh.Trimesh(vertices=verts, faces=faces)
            elif path.endswith('.stl'):
                self._reference_mesh = trimesh.load(path)
        except Exception as e:
            self._reference_mesh = None

    def _compute_metrics(self) -> dict:
        """Compute all quality metrics for current geometry."""
        if self._mesh is None:
            return {"has_geometry": 0.0}

        metrics = {}
        analyzer = self.mesh_analyzer

        if analyzer:
            metrics.update(analyzer.basic_metrics())
            metrics.update(analyzer.wall_thickness_analysis())
            metrics.update(analyzer.printability_analysis())
            metrics.update(analyzer.mesh_quality())

        if self._reference_mesh is not None:
            metrics.update(
                self.reference_comparator.compare(self._mesh, self._reference_mesh)
            )

        if self._constraints:
            metrics.update(
                self.constraint_checker.check(self._mesh, self._constraints)
            )

        return metrics

    def _compute_reward(self, metrics: dict, action_success: bool) -> dict:
        """
        Compute decomposed reward components.
        Returns individual components so the training script can weight them.
        """
        reward = {}

        # Action success/failure
        reward["action_success"] = 0.1 if action_success else -0.3

        if not action_success or self._prev_metrics is None:
            return reward

        prev = self._prev_metrics

        # Wall thickness improvement
        if "min_wall_thickness" in metrics and "min_wall_thickness" in prev:
            delta = metrics["min_wall_thickness"] - prev["min_wall_thickness"]
            reward["wall_improvement"] = np.clip(delta * 2.0, -0.5, 0.5)

        # Printability improvement
        if "printability_score" in metrics and "printability_score" in prev:
            delta = metrics["printability_score"] - prev["printability_score"]
            reward["printability_improvement"] = np.clip(delta * 2.0, -0.5, 0.5)

        # Reference match improvement
        if "hausdorff_distance" in metrics and "hausdorff_distance" in prev:
            # Lower Hausdorff = better, so reward decrease
            delta = prev["hausdorff_distance"] - metrics["hausdorff_distance"]
            reward["reference_improvement"] = np.clip(delta * 2.0, -0.5, 0.5)

        # Constraint satisfaction
        if "n_satisfied" in metrics and "n_satisfied" in prev:
            delta = metrics["n_satisfied"] - prev["n_satisfied"]
            reward["constraint_progress"] = delta * 0.8

        # Watertight integrity
        is_watertight = metrics.get("is_watertight", 0)
        was_watertight = prev.get("is_watertight", 0)
        if was_watertight and not is_watertight:
            reward["integrity_loss"] = -1.0
        elif is_watertight:
            reward["integrity_maintained"] = 0.05

        # No-op penalty
        if metrics == prev:
            reward["no_change"] = -0.2

        return reward
```

### workbench/state_encoder.py — 64-Dimension State Vector

Implements the encoding table from ENGINEERING_ARCHITECTURE.md §2.2.
Each method fills a section of the 64-dimension vector.

```python
import numpy as np
from typing import Optional

class StateEncoder:
    DIMENSIONS = 64

    # Normalization constants (adjust based on expected part scales)
    MAX_EXPECTED_SIZE = 500.0     # mm — largest expected single dimension
    MAX_EXPECTED_AREA = 250000.0  # mm² — largest expected surface area
    MAX_EXPECTED_FACES = 5000
    MAX_EXPECTED_EDGES = 10000
    TARGET_THICKNESS = 2.0        # mm — default target wall thickness
    MAX_STEPS = 200
    MAX_TIME = 600.0              # seconds
    MAX_CONSTRAINTS = 20

    def encode(self, engine) -> np.ndarray:
        """Encode the full engine state as a 64-dim float vector."""
        state = np.zeros(self.DIMENSIONS, dtype=np.float32)

        mesh = engine.current_mesh
        if mesh is None:
            # Only task context is meaningful when no geometry
            self._encode_task_context(state, engine)
            return state

        self._encode_global_geometry(state, mesh)
        self._encode_wall_analysis(state, engine.mesh_analyzer)
        self._encode_printability(state, engine.mesh_analyzer)
        self._encode_mesh_quality(state, mesh)
        self._encode_reference_comparison(state, mesh, engine.reference_mesh, engine)
        self._encode_task_context(state, engine)
        self._encode_constraints(state, engine)

        return state

    def _encode_global_geometry(self, state: np.ndarray, mesh):
        """Indices 0-11: bounding box, volume, surface area, etc."""
        bbox = mesh.bounding_box.extents  # [x_size, y_size, z_size]
        state[0] = np.clip(bbox[0] / self.MAX_EXPECTED_SIZE, 0, 1)
        state[1] = np.clip(bbox[1] / self.MAX_EXPECTED_SIZE, 0, 1)
        state[2] = np.clip(bbox[2] / self.MAX_EXPECTED_SIZE, 0, 1)

        bbox_vol = bbox[0] * bbox[1] * bbox[2]
        state[3] = np.clip(mesh.volume / max(bbox_vol, 1e-6), 0, 1) if mesh.volume else 0

        state[4] = np.clip(mesh.area / self.MAX_EXPECTED_AREA, 0, 1) if mesh.area else 0

        # Center of mass relative to bbox center
        if mesh.center_mass is not None:
            bbox_center = mesh.bounding_box.centroid
            rel_com = (mesh.center_mass - bbox_center) / (bbox + 1e-6)
            state[5] = np.clip(rel_com[0], -1, 1)
            state[6] = np.clip(rel_com[1], -1, 1)
            state[7] = np.clip(rel_com[2], -1, 1)

        state[8] = np.clip(len(mesh.faces) / self.MAX_EXPECTED_FACES, 0, 1)
        state[9] = np.clip(len(mesh.edges_unique) / self.MAX_EXPECTED_EDGES, 0, 1)
        state[10] = 1.0 if mesh.is_watertight else 0.0
        # state[11] = symmetry_score — computed separately

    # ... implement all other _encode_ methods following the same pattern
    # Each fills its designated indices in the state array.
    # See ENGINEERING_ARCHITECTURE.md §2.2 for the complete index table.
```

### workbench/action_executor.py — Action Execution

Maps action IDs + parameters to CadQuery operations.

**CRITICAL:** This is where network outputs become geometry.
Parameters are in [-1, 1] and must be mapped to physical dimensions.
Use the current part's bounding box as the scale reference.

```python
import cadquery as cq
import numpy as np

class ActionExecutor:
    """
    Executes actions on the geometry engine.

    Each action method:
    1. Receives parameters in [-1, 1] range
    2. Maps them to physical dimensions based on current part scale
    3. Applies the CadQuery operation
    4. Returns (success: bool, error: Optional[str])

    If the operation fails (invalid geometry), return (False, error_message).
    The engine will revert to previous state.
    """

    def execute(self, action_id: int, params: np.ndarray, engine) -> tuple:
        """Dispatch to the appropriate action handler."""
        handlers = {
            0: self._extrude,
            1: self._cut,
            2: self._revolve,
            3: self._add_cylinder,
            4: self._add_box,
            5: self._fillet_edges,
            6: self._chamfer_edges,
            7: self._shell,
            8: self._add_hole,
            9: self._add_boss,
            10: self._add_rib,
            11: self._translate_feature,
            12: self._scale_feature,
            13: self._mirror,
            14: self._pattern_linear,
            15: self._thicken_wall,
            16: self._smooth_region,
            17: self._remove_feature,
            18: self._validate,
            19: self._finalize,
        }

        handler = handlers.get(action_id)
        if handler is None:
            return (False, f"Unknown action ID: {action_id}")

        try:
            return handler(params, engine)
        except Exception as e:
            return (False, str(e))

    def _get_scale(self, engine) -> float:
        """Get the current part's characteristic dimension for parameter mapping."""
        if engine.current_mesh is None:
            return 10.0  # default 10mm for new parts
        bbox = engine.current_mesh.bounding_box.extents
        return max(bbox[0], bbox[1], bbox[2], 1.0)

    def _map_length(self, raw: float, scale: float) -> float:
        """Map [-1, 1] to [0.1mm, scale * 0.5mm]."""
        return 0.1 + (raw + 1) / 2 * scale * 0.5

    def _map_position(self, raw: float, scale: float) -> float:
        """Map [-1, 1] to [-scale*0.6, +scale*0.6]."""
        return raw * scale * 0.6

    def _map_radius(self, raw: float, scale: float) -> float:
        """Map [-1, 1] to [0.05mm, scale * 0.2mm]."""
        return 0.05 + (raw + 1) / 2 * scale * 0.2

    def _map_angle(self, raw: float) -> float:
        """Map [-1, 1] to [-180, +180] degrees."""
        return raw * 180.0

    # === Primitive Creation ===

    def _add_box(self, params: np.ndarray, engine) -> tuple:
        """Action 4: Add a box primitive."""
        scale = self._get_scale(engine)
        cx = self._map_position(params[0], scale)
        cy = self._map_position(params[1], scale)
        cz = self._map_position(params[2], scale)
        sx = self._map_length(params[3], scale)
        sy = self._map_length(params[4], scale)
        sz = self._map_length(params[5] if len(params) > 5 else 0.5, scale)

        if engine.current_workplane is None:
            # First geometry — create from scratch
            wp = cq.Workplane("XY").box(sx, sy, sz).translate((cx, cy, cz))
        else:
            # Add to existing — boolean union
            box = cq.Workplane("XY").box(sx, sy, sz).translate((cx, cy, cz))
            wp = engine.current_workplane.union(box)

        engine.current_workplane = wp
        return (True, None)

    def _add_cylinder(self, params: np.ndarray, engine) -> tuple:
        """Action 3: Add a cylinder primitive."""
        scale = self._get_scale(engine)
        cx = self._map_position(params[0], scale)
        cy = self._map_position(params[1], scale)
        cz = self._map_position(params[2], scale)
        radius = self._map_radius(params[3], scale)
        height = self._map_length(params[4], scale)

        if engine.current_workplane is None:
            wp = cq.Workplane("XY").cylinder(height, radius).translate((cx, cy, cz))
        else:
            cyl = cq.Workplane("XY").cylinder(height, radius).translate((cx, cy, cz))
            wp = engine.current_workplane.union(cyl)

        engine.current_workplane = wp
        return (True, None)

    def _fillet_edges(self, params: np.ndarray, engine) -> tuple:
        """Action 5: Fillet edges near a selection point."""
        if engine.current_workplane is None:
            return (False, "No geometry to fillet")

        scale = self._get_scale(engine)
        radius = self._map_radius(params[0], scale)

        # Select edges nearest to the selection point
        # CadQuery edge selection — use .edges() with nearest-point filtering
        try:
            wp = engine.current_workplane.edges().fillet(radius)
            engine.current_workplane = wp
            return (True, None)
        except Exception as e:
            return (False, f"Fillet failed: {e}")

    # ... Implement all 20 actions following the same pattern.
    # Each:
    #   1. Maps parameters using _map_* methods
    #   2. Applies CadQuery operation
    #   3. Updates engine.current_workplane
    #   4. Returns (success, error_message)
    #
    # For edge/face selection (actions that need to pick a specific edge):
    #   Use the selector point (params as x,y,z) to find the nearest
    #   edge/face via CadQuery's .edges(NearestToPointSelector(x,y,z))
    #   or similar. This is how the network "points at" geometry.

    def _validate(self, params: np.ndarray, engine) -> tuple:
        """Action 18: Trigger full validation (no geometry change)."""
        return (True, None)

    def _finalize(self, params: np.ndarray, engine) -> tuple:
        """Action 19: Declare part complete."""
        return (True, None)
```

### workbench/mesh_analyzer.py — Mesh Quality Analysis

Implements the analysis methods that populate the state vector.
Uses trimesh for all computation.

**Must implement these analysis methods:**
- `basic_metrics()` → volume, area, bbox, watertight, face/edge counts
- `wall_thickness_analysis(n_samples=1000)` → ray-cast thickness sampling
- `printability_analysis(build_dir=[0,0,1], overhang_threshold=45)` → overhang, support, bridges
- `mesh_quality()` → aspect ratios, edge lengths, degenerate triangles

See ENGINEERING_ARCHITECTURE.md §2.2 for the exact metrics and their
normalization ranges.

The wall thickness analysis is the most important and complex:
cast rays from sampled surface points inward along -normal direction,
measure distance to the opposing surface. This gives a statistical
picture of material distribution.

### workbench/validator.py — Full Validation Suite

Runs all checks and produces a decomposed validation report.
Calls mesh_analyzer, constraint_checker, and reference_comparator.

### workbench/constraint_checker.py — Engineering Constraints

Checks the current geometry against a set of engineering constraints
specified as JSON. Constraints can include:
- Dimensional: min/max for any measurement
- Tolerance: deviation from target value within +/- range
- Feature: specific features that must exist (holes, bosses, etc.)
- Material: volume/mass limits given material density
- Printability: min wall, max overhang, min feature size

### workbench/reference_comparator.py — Shape Comparison

Compares current mesh to a reference mesh. Key metrics:
- Hausdorff distance (worst-case deviation)
- Mean surface deviation (average deviation)
- Volume ratio
- Bounding box similarity
- ICP-based alignment score

Uses trimesh + scipy for Hausdorff and ICP alignment.

## Testing

Create `tests/test_workbench.py` with tests for:
1. Engine reset and basic box creation
2. Each action type executes without crashing
3. State encoder produces correct-length vector (64)
4. Action mask correctly disables invalid actions
5. Undo reverts to previous state
6. Reference loading and comparison metrics
7. Wall thickness analysis returns valid statistics
8. Printability analysis identifies overhangs

## Running the Service

```bash
cd DARCI-v4/Darci.Engineering.Workbench
pip install -r requirements.txt
uvicorn main:app --host 127.0.0.1 --port 8001
```

## Key Design Principles

1. **No English in the numerical path.** The state vector and action IDs are
   pure numbers. Text is only used for error messages and endpoint labels.

2. **Every action must be recoverable.** If an action fails, the engine reverts
   cleanly. The network should never be able to corrupt the geometry state.

3. **Metrics are deterministic.** Given the same geometry, the state encoder
   always produces the same vector. No randomness in the measurement path.

4. **Parameter mapping is relative to part scale.** A "length" parameter of 0.5
   means different physical dimensions for a 10mm part vs a 500mm part.
   The network learns to think in proportions, not absolute millimeters.

5. **Edge/face selection is position-based.** The network outputs an XYZ point;
   the engine selects the nearest geometric entity. This is robust to topology
   changes between operations.
