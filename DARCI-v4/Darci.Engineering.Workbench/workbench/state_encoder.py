"""64-dimension state encoder for the Geometry Workbench.

Index layout follows ENGINEERING_ARCHITECTURE.md §2.2:
  0-11   Global geometry
 12-21   Wall analysis
 22-31   Printability
 32-39   Mesh quality
 40-49   Reference comparison
 50-55   Task context
 56-63   Constraint satisfaction
"""

import time
import numpy as np
from typing import Optional


class StateEncoder:
    DIMENSIONS = 64

    # Normalization constants
    MAX_EXPECTED_SIZE = 500.0       # mm
    MAX_EXPECTED_AREA = 250_000.0   # mm²
    MAX_EXPECTED_FACES = 5_000
    MAX_EXPECTED_EDGES = 10_000
    TARGET_THICKNESS = 2.0          # mm
    MAX_THICKNESS = 20.0            # mm
    MAX_STEPS = 200
    MAX_TIME = 600.0                # seconds
    MAX_CONSTRAINTS = 20
    MAX_WALL_VIOLATIONS = 50
    MAX_OVERHANG_DEG = 90.0
    MIN_PRINTABLE_SIZE = 0.4        # mm
    MAX_BRIDGE_SPAN = 50.0          # mm
    MAX_ISLANDS = 10
    MAX_BUILD_HEIGHT = 300.0        # mm
    MAX_HAUSDORFF = 100.0           # mm
    MAX_FEAT_DIFF = 20
    MAX_CONSECUTIVE_FAIL = 10

    def __init__(self):
        self._session_start: float = time.time()
        self._last_action_success: float = 0.0
        self._last_reward: float = 0.0
        self._consecutive_failures: int = 0
        self._reward_history: list = []  # rolling for trend

    # Called by the engine after each action
    def update_context(self, success: bool, reward: float):
        self._last_action_success = 1.0 if success else 0.0
        self._last_reward = float(np.clip(reward, -1, 1))
        if not success:
            self._consecutive_failures += 1
        else:
            self._consecutive_failures = 0
        self._reward_history.append(reward)
        if len(self._reward_history) > 10:
            self._reward_history.pop(0)

    def reset(self):
        self._session_start = time.time()
        self._last_action_success = 0.0
        self._last_reward = 0.0
        self._consecutive_failures = 0
        self._reward_history = []

    def encode(self, engine) -> np.ndarray:
        """Encode the full engine state as a 64-dim float32 vector."""
        state = np.zeros(self.DIMENSIONS, dtype=np.float32)

        mesh = engine.current_mesh
        if mesh is None:
            self._encode_task_context(state, engine)
            return state

        self._encode_global_geometry(state, mesh)
        self._encode_wall_analysis(state, engine.mesh_analyzer)
        self._encode_printability(state, engine.mesh_analyzer)
        self._encode_mesh_quality(state, mesh, engine.mesh_analyzer)
        self._encode_reference_comparison(state, mesh, engine.reference_mesh, engine)
        self._encode_task_context(state, engine)
        self._encode_constraints(state, engine)

        return state

    # ------------------------------------------------------------------ #
    # Section encoders                                                     #
    # ------------------------------------------------------------------ #

    def _encode_global_geometry(self, state: np.ndarray, mesh):
        """Indices 0–11."""
        bbox = mesh.bounding_box.extents
        state[0] = np.clip(bbox[0] / self.MAX_EXPECTED_SIZE, 0, 1)
        state[1] = np.clip(bbox[1] / self.MAX_EXPECTED_SIZE, 0, 1)
        state[2] = np.clip(bbox[2] / self.MAX_EXPECTED_SIZE, 0, 1)

        bbox_vol = float(bbox[0] * bbox[1] * bbox[2]) or 1e-6
        state[3] = float(np.clip(mesh.volume / bbox_vol, 0, 1)) if mesh.is_volume else 0.0
        state[4] = float(np.clip(mesh.area / self.MAX_EXPECTED_AREA, 0, 1))

        if mesh.center_mass is not None:
            bbox_center = np.array(mesh.bounding_box.centroid)
            bbox_ext = np.array(bbox) + 1e-6
            rel_com = (np.array(mesh.center_mass) - bbox_center) / bbox_ext
            state[5] = float(np.clip(rel_com[0], -1, 1))
            state[6] = float(np.clip(rel_com[1], -1, 1))
            state[7] = float(np.clip(rel_com[2], -1, 1))

        state[8] = float(np.clip(len(mesh.faces) / self.MAX_EXPECTED_FACES, 0, 1))
        state[9] = float(np.clip(len(mesh.edges_unique) / self.MAX_EXPECTED_EDGES, 0, 1))
        state[10] = 1.0 if mesh.is_watertight else 0.0
        state[11] = self._symmetry_score(mesh)

    def _encode_wall_analysis(self, state: np.ndarray, analyzer):
        """Indices 12–21."""
        if analyzer is None:
            return
        m = analyzer.wall_thickness_analysis()
        state[12] = float(np.clip(m["min_wall_thickness"] / self.TARGET_THICKNESS, 0, 1))
        state[13] = float(np.clip(m["max_wall_thickness"] / self.MAX_THICKNESS, 0, 1))
        state[14] = float(np.clip(m["mean_wall_thickness"] / self.TARGET_THICKNESS, 0, 1))
        state[15] = float(np.clip(m["wall_std_dev"] / (self.TARGET_THICKNESS + 1e-6), 0, 1))
        state[16] = float(np.clip(m["pct_below_min"], 0, 1))
        state[17] = float(np.clip(m["pct_above_max"], 0, 1))
        state[18] = float(np.clip(m["thinnest_region_x"], -1, 1))
        state[19] = float(np.clip(m["thinnest_region_y"], -1, 1))
        state[20] = float(np.clip(m["thinnest_region_z"], -1, 1))
        state[21] = float(np.clip(m["wall_violation_count"] / self.MAX_WALL_VIOLATIONS, 0, 1))

    def _encode_printability(self, state: np.ndarray, analyzer):
        """Indices 22–31."""
        if analyzer is None:
            return
        p = analyzer.printability_analysis()
        state[22] = float(np.clip(p["max_overhang_angle"] / self.MAX_OVERHANG_DEG, 0, 1))
        state[23] = float(np.clip(p["overhang_area_pct"], 0, 1))
        state[24] = float(np.clip(p["support_volume_ratio"], 0, 1))
        state[25] = float(np.clip(p["min_feature_size"] / self.MIN_PRINTABLE_SIZE, 0, 1))
        state[26] = float(np.clip(p["bridge_max_span"] / self.MAX_BRIDGE_SPAN, 0, 1))
        state[27] = float(np.clip(p["n_islands"] / self.MAX_ISLANDS, 0, 1))
        state[28] = float(np.clip(p["build_height"] / self.MAX_BUILD_HEIGHT, 0, 1))
        state[29] = float(np.clip(p["first_layer_area"] / max(p["first_layer_area"] + 1e-3, 1), 0, 1))
        state[30] = float(p["has_enclosed_voids"])
        state[31] = float(np.clip(p["printability_score"], 0, 1))

    def _encode_mesh_quality(self, state: np.ndarray, mesh, analyzer):
        """Indices 32–39."""
        if analyzer is None:
            return
        q = analyzer.mesh_quality()
        state[32] = float(np.clip(q["mesh_face_count"] / self.MAX_EXPECTED_FACES, 0, 1))
        state[33] = float(np.clip(q["min_aspect_ratio"] / 100.0, 0, 1))
        state[34] = float(np.clip(q["mean_aspect_ratio"] / 100.0, 0, 1))
        state[35] = float(np.clip(q["pct_degenerate"], 0, 1))
        state[36] = float(np.clip(q["max_edge_length"], 0, 1))  # already /diag
        state[37] = float(np.clip(q["min_edge_length"], 0, 1))
        state[38] = float(np.clip(q["edge_length_ratio"], 0, 1))
        state[39] = float(q["mesh_is_valid"])

    def _encode_reference_comparison(self, state: np.ndarray, mesh, ref_mesh, engine):
        """Indices 40–49."""
        if ref_mesh is None:
            state[40] = 0.0
            return
        state[40] = 1.0

        try:
            from .reference_comparator import ReferenceComparator
            rc = engine.reference_comparator
            m = rc.compare(mesh, ref_mesh)
            state[41] = float(np.clip(m.get("hausdorff_distance", 0) / self.MAX_HAUSDORFF, 0, 1))
            state[42] = float(np.clip(m.get("mean_surface_deviation", 0) / self.MAX_HAUSDORFF, 0, 1))
            ref_vol = float(ref_mesh.volume) or 1e-6
            state[43] = float(np.clip(mesh.volume / ref_vol, 0, 2) / 2.0) if mesh.is_volume else 0.0
            state[44] = float(np.clip(m.get("bbox_similarity", 0), 0, 1))
            state[45] = float(np.clip(m.get("feature_count_diff", 0) / self.MAX_FEAT_DIFF, 0, 1))
            state[46] = float(np.clip(m.get("topology_match", 0), 0, 1))
            state[47] = float(np.clip(m.get("n_missing_features", 0) / self.MAX_FEAT_DIFF, 0, 1))
            state[48] = float(np.clip(m.get("n_extra_features", 0) / self.MAX_FEAT_DIFF, 0, 1))
            state[49] = float(np.clip(m.get("alignment_score", 0), 0, 1))
        except Exception:
            pass

    def _encode_task_context(self, state: np.ndarray, engine):
        """Indices 50–55."""
        state[50] = float(np.clip(engine.step_count / self.MAX_STEPS, 0, 1))
        elapsed = time.time() - self._session_start
        state[51] = float(np.clip(elapsed / self.MAX_TIME, 0, 1))
        state[52] = self._last_action_success
        state[53] = float(np.clip(self._last_reward, -1, 1))
        state[54] = float(np.clip(self._consecutive_failures / self.MAX_CONSECUTIVE_FAIL, 0, 1))

        # Rolling reward trend
        if len(self._reward_history) >= 2:
            trend = float(np.mean(np.diff(self._reward_history)))
            state[55] = float(np.clip(trend, -1, 1))

    def _encode_constraints(self, state: np.ndarray, engine):
        """Indices 56–63."""
        constraints = engine.constraints
        n_total = len(constraints)
        if n_total == 0:
            return

        state[56] = float(np.clip(n_total / self.MAX_CONSTRAINTS, 0, 1))

        # Use the constraint checker results if mesh exists
        if engine.current_mesh is not None:
            try:
                result = engine.constraint_checker.check(engine.current_mesh, constraints)
                n_sat = result.get("n_satisfied", 0)
                n_viol = result.get("n_violated", 0)
                state[57] = float(np.clip(n_sat / max(n_total, 1), 0, 1))
                state[58] = float(np.clip(n_viol / max(n_total, 1), 0, 1))
                state[59] = float(np.clip(result.get("worst_violation_mag", 0), 0, 1))
                state[60] = float(np.clip(result.get("total_violation_mag", 0), 0, 1))
                state[61] = float(np.clip(result.get("constraint_type_dim", 0), 0, 1))
                state[62] = float(np.clip(result.get("constraint_type_tol", 0), 0, 1))
                state[63] = float(np.clip(result.get("constraint_type_feat", 0), 0, 1))
            except Exception:
                pass

    # ------------------------------------------------------------------ #
    # Helpers                                                              #
    # ------------------------------------------------------------------ #

    def _symmetry_score(self, mesh) -> float:
        """Bilateral symmetry approximation — compare to XZ-reflected version."""
        try:
            verts = np.array(mesh.vertices)
            # Reflect across YZ plane (X → -X)
            reflected = verts.copy()
            reflected[:, 0] = -reflected[:, 0]
            # Rough measure: compare bounding boxes
            bb1 = verts.max(axis=0) - verts.min(axis=0)
            bb2 = reflected.max(axis=0) - reflected.min(axis=0)
            diff = np.abs(bb1 - bb2) / (bb1 + 1e-6)
            return float(np.clip(1.0 - float(np.mean(diff)), 0, 1))
        except Exception:
            return 0.0
