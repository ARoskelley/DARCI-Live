"""Geometry Engine — manages the live CadQuery workplane and mesh."""

import concurrent

import cadquery as cq
import trimesh
import numpy as np
import concurrent.futures
from threading import Lock
from typing import Optional

from .mesh_analyzer import MeshAnalyzer
from .state_encoder import StateEncoder
from .action_executor import ActionExecutor
from .validator import Validator
from .constraint_checker import ConstraintChecker
from .reference_comparator import ReferenceComparator


class GeometryEngine:
    """Stateful 3D geometry manager.

    Thread-safe: mutations acquire the lock, reads are lock-free
    (reads of immutable numpy arrays are fine in CPython).
    """

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

        self._prev_metrics: Optional[dict] = None

    # ------------------------------------------------------------------ #
    # Public interface                                                     #
    # ------------------------------------------------------------------ #

    def reset(self, reference_path: Optional[str] = None,
              constraints: Optional[dict] = None,
              targets: Optional[dict] = None):
        """Reset to empty state, optionally loading reference geometry."""
        with self._lock:
            self._workplane = None
            self._mesh = None
            self._history = []
            self._step_count = 0
            self._prev_metrics = None
            self._constraints = constraints or {}
            self.state_encoder.reset()

            if reference_path:
                self._load_reference(reference_path)

    def execute_action(self, action_id: int, parameters: np.ndarray) -> dict:
        """Execute an action and return results dict."""
        with self._lock:
            self._push_history()

            try:
                with concurrent.futures.ThreadPoolExecutor(max_workers=1) as executor:
                    future = executor.submit(self.action_executor.execute, action_id, parameters, self)
                    success, error = future.result(timeout=10)  # 10 second max per action
            except concurrent.futures.TimeoutError:
                success, error = False, "Action timed out (geometry kernel hung)"
            except Exception as e:
                success, error = False, str(e)

            if success:
                self._update_mesh()
                self._step_count += 1

            state = self.state_encoder.encode(self)
            metrics = self._compute_metrics()
            reward_components = self._compute_reward(metrics, success)
            self._prev_metrics = metrics

            # Update encoder context for next step
            total_reward = float(sum(reward_components.values()))
            self.state_encoder.update_context(success, total_reward)

            return {
                "state": state.tolist(),
                "metrics": metrics,
                "success": success,
                "error_message": error,
                "reward_components": reward_components,
            }

    def get_state(self) -> np.ndarray:
        """Get current 64-dim state vector."""
        return self.state_encoder.encode(self)

    def get_action_mask(self) -> np.ndarray:
        """Return bool[20] action mask."""
        mask = np.ones(20, dtype=bool)

        if self._workplane is None:
            mask[:] = False
            mask[3] = True   # add_cylinder
            mask[4] = True   # add_box

        if self._mesh is not None:
            if not self._mesh.is_watertight:
                mask[7] = False   # shell
                mask[15] = False  # thicken_wall

        # Control actions always available
        mask[18] = True   # validate
        mask[19] = True   # finalize

        # Consecutive failures: restrict to repair + control
        if self.state_encoder._consecutive_failures > 5:
            repair_control = set(range(15, 20))
            for i in range(20):
                if i not in repair_control:
                    mask[i] = False
            if self._workplane is None:
                mask[3] = True
                mask[4] = True

        return mask

    def validate(self) -> dict:
        """Run full validation suite."""
        return self.validator.validate(self)

    def undo(self) -> bool:
        """Revert to previous state."""
        with self._lock:
            if not self._history:
                return False
            self._workplane = self._history.pop()
            self._update_mesh()
            self._step_count = max(0, self._step_count - 1)
            return True

    def export(self, fmt: str, output_dir: str = "models") -> dict:
        """Export current geometry to STEP and/or STL."""
        import os
        os.makedirs(output_dir, exist_ok=True)
        result = {}
        if self._workplane is None:
            return result
        if fmt in ("step", "both"):
            path = os.path.join(output_dir, "output.step")
            cq.exporters.export(self._workplane, path)
            result["step_path"] = path
        if fmt in ("stl", "both"):
            path = os.path.join(output_dir, "output.stl")
            cq.exporters.export(self._workplane, path)
            result["stl_path"] = path
        return result

    def load_reference(self, path: str):
        """Load reference geometry for comparison metrics."""
        with self._lock:
            self._load_reference(path)

    # ------------------------------------------------------------------ #
    # Properties                                                           #
    # ------------------------------------------------------------------ #

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

    # ------------------------------------------------------------------ #
    # Private                                                              #
    # ------------------------------------------------------------------ #

    def _update_mesh(self):
        """Re-tessellate CadQuery B-rep → trimesh."""
        if self._workplane is None:
            self._mesh = None
            self.mesh_analyzer = None
            return
        try:
            solid = self._workplane.val()
            vertices, triangles = solid.tessellate(0.1)
            verts = np.array([(v.x, v.y, v.z) for v in vertices])
            faces = np.array([(t[0], t[1], t[2]) for t in triangles])
            self._mesh = trimesh.Trimesh(vertices=verts, faces=faces, process=False)
            self.mesh_analyzer = MeshAnalyzer(self._mesh)
        except Exception:
            self._mesh = None
            self.mesh_analyzer = None

    def _push_history(self):
        # Always push current state (including None) so the first action is undoable
        self._history.append(self._workplane)
        if len(self._history) > self._max_history:
            self._history.pop(0)

    def _load_reference(self, path: str):
        try:
            if path.lower().endswith(('.step', '.stp')):
                result = cq.importers.importStep(path)
                solid = result.val()
                vertices, triangles = solid.tessellate(0.1)
                verts = np.array([(v.x, v.y, v.z) for v in vertices])
                faces = np.array([(t[0], t[1], t[2]) for t in triangles])
                self._reference_mesh = trimesh.Trimesh(vertices=verts, faces=faces, process=False)
            elif path.lower().endswith('.stl'):
                self._reference_mesh = trimesh.load(path)
            else:
                self._reference_mesh = None
        except Exception:
            self._reference_mesh = None

    def _compute_metrics(self) -> dict:
        if self._mesh is None:
            return {"has_geometry": 0.0}

        metrics: dict = {"has_geometry": 1.0}
        analyzer = self.mesh_analyzer

        if analyzer:
            metrics.update(analyzer.basic_metrics())
            metrics.update(analyzer.wall_thickness_analysis(n_samples=100))
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
        """Decomposed reward components."""
        reward: dict = {}
        reward["action_success"] = 0.1 if action_success else -0.3

        if not action_success or self._prev_metrics is None:
            return reward

        prev = self._prev_metrics

        if "min_wall_thickness" in metrics and "min_wall_thickness" in prev:
            delta = metrics["min_wall_thickness"] - prev["min_wall_thickness"]
            reward["wall_improvement"] = float(np.clip(delta * 2.0, -0.5, 0.5))

        if "printability_score" in metrics and "printability_score" in prev:
            delta = metrics["printability_score"] - prev["printability_score"]
            reward["printability_improvement"] = float(np.clip(delta * 2.0, -0.5, 0.5))

        if "hausdorff_distance" in metrics and "hausdorff_distance" in prev:
            delta = prev["hausdorff_distance"] - metrics["hausdorff_distance"]
            reward["reference_improvement"] = float(np.clip(delta * 2.0, -0.5, 0.5))

        if "n_satisfied" in metrics and "n_satisfied" in prev:
            delta = metrics["n_satisfied"] - prev["n_satisfied"]
            reward["constraint_progress"] = float(delta * 0.8)

        is_watertight = float(metrics.get("is_watertight", 0))
        was_watertight = float(prev.get("is_watertight", 0))
        if was_watertight and not is_watertight:
            reward["integrity_loss"] = -1.0
        elif is_watertight:
            reward["integrity_maintained"] = 0.05

        if metrics == prev:
            reward["no_change"] = -0.2

        return reward
