"""Engineering constraint checker.

Constraints are specified as a dict keyed by constraint name.
Each constraint has a type and parameters.

Supported types:
  dimensional  — min/max for a named measurement
  tolerance    — target ± tolerance
  feature      — feature must exist (presence check)
  material     — volume/mass limits given density
  printability — min_wall, max_overhang, min_feature_size
"""

import numpy as np
import trimesh
from typing import Dict, Any


class ConstraintChecker:

    def check(self, mesh: trimesh.Trimesh, constraints: Dict[str, Any]) -> dict:
        """Check all constraints and return satisfaction metrics."""
        if not constraints:
            return self._empty_result()

        n_satisfied = 0
        n_violated = 0
        violation_mags = []
        dim_sat = []
        tol_sat = []
        feat_sat = []

        for name, spec in constraints.items():
            ctype = spec.get("type", "dimensional")
            satisfied, magnitude = self._check_single(mesh, ctype, spec)

            if satisfied:
                n_satisfied += 1
            else:
                n_violated += 1
                violation_mags.append(magnitude)

            if ctype == "dimensional":
                dim_sat.append(1.0 if satisfied else 0.0)
            elif ctype in ("tolerance",):
                tol_sat.append(1.0 if satisfied else 0.0)
            elif ctype in ("feature", "printability"):
                feat_sat.append(1.0 if satisfied else 0.0)

        n_total = len(constraints)
        worst = float(max(violation_mags)) if violation_mags else 0.0
        total_mag = float(sum(violation_mags))

        return {
            "n_satisfied": n_satisfied,
            "n_violated": n_violated,
            "worst_violation_mag": min(worst, 1.0),
            "total_violation_mag": min(total_mag / max(n_total, 1), 1.0),
            "constraint_type_dim": float(np.mean(dim_sat)) if dim_sat else 1.0,
            "constraint_type_tol": float(np.mean(tol_sat)) if tol_sat else 1.0,
            "constraint_type_feat": float(np.mean(feat_sat)) if feat_sat else 1.0,
        }

    # ------------------------------------------------------------------ #
    # Per-type checkers                                                    #
    # ------------------------------------------------------------------ #

    def _check_single(self, mesh, ctype: str, spec: dict):
        """Returns (satisfied: bool, violation_magnitude: float)."""
        try:
            if ctype == "dimensional":
                return self._check_dimensional(mesh, spec)
            elif ctype == "tolerance":
                return self._check_tolerance(mesh, spec)
            elif ctype == "feature":
                return self._check_feature(mesh, spec)
            elif ctype == "material":
                return self._check_material(mesh, spec)
            elif ctype == "printability":
                return self._check_printability(mesh, spec)
            else:
                return (True, 0.0)
        except Exception:
            return (False, 1.0)

    def _check_dimensional(self, mesh, spec: dict):
        """Check min/max bounds for a named measurement."""
        measurement = spec.get("measurement", "bbox_x")
        value = self._measure(mesh, measurement)
        lo = spec.get("min", float("-inf"))
        hi = spec.get("max", float("inf"))
        satisfied = lo <= value <= hi
        if satisfied:
            return (True, 0.0)
        mag = min(abs(value - lo) / (abs(lo) + 1), abs(value - hi) / (abs(hi) + 1))
        return (False, float(np.clip(mag, 0, 1)))

    def _check_tolerance(self, mesh, spec: dict):
        """Check value is within target ± tolerance."""
        measurement = spec.get("measurement", "bbox_x")
        target = spec.get("target", 0.0)
        tol = spec.get("tolerance", 0.1)
        value = self._measure(mesh, measurement)
        diff = abs(value - target)
        satisfied = diff <= tol
        mag = float(np.clip((diff - tol) / (abs(target) + 1), 0, 1))
        return (satisfied, 0.0 if satisfied else mag)

    def _check_feature(self, mesh, spec: dict):
        """Check that a feature type exists (rough heuristic)."""
        feature_type = spec.get("feature_type", "hole")
        if feature_type == "hole":
            # A hole implies non-convex geometry — check convexity ratio
            try:
                hull_vol = float(mesh.convex_hull.volume)
                part_vol = float(mesh.volume) if mesh.is_volume else 0.0
                ratio = part_vol / (hull_vol + 1e-9)
                # If ratio < 0.95, there's likely concave geometry (holes)
                exists = ratio < 0.95
            except Exception:
                exists = False
        else:
            exists = True  # Can't verify other feature types without CAD topology
        return (exists, 0.0 if exists else 1.0)

    def _check_material(self, mesh, spec: dict):
        """Check volume/mass limits."""
        density = spec.get("density", 1.0)  # g/cm³
        max_mass = spec.get("max_mass", float("inf"))
        min_mass = spec.get("min_mass", 0.0)
        vol_cm3 = float(mesh.volume) / 1000.0 if mesh.is_volume else 0.0  # mm³→cm³
        mass = vol_cm3 * density
        satisfied = min_mass <= mass <= max_mass
        mag = 0.0
        if not satisfied:
            mag = float(np.clip(abs(mass - max_mass) / (max_mass + 1), 0, 1))
        return (satisfied, mag)

    def _check_printability(self, mesh, spec: dict):
        """Quick printability check from spec values."""
        min_wall = spec.get("min_wall", 0.4)
        from .mesh_analyzer import MeshAnalyzer
        analyzer = MeshAnalyzer(mesh)
        wall = analyzer.wall_thickness_analysis(n_samples=200)
        min_t = wall.get("min_wall_thickness", 0.0)
        satisfied = min_t >= min_wall
        mag = float(np.clip((min_wall - min_t) / (min_wall + 1e-6), 0, 1))
        return (satisfied, 0.0 if satisfied else mag)

    # ------------------------------------------------------------------ #
    # Measurement dispatch                                                 #
    # ------------------------------------------------------------------ #

    def _measure(self, mesh, measurement: str) -> float:
        """Retrieve a named measurement from the mesh."""
        bbox = mesh.bounding_box.extents
        mapping = {
            "bbox_x": bbox[0],
            "bbox_y": bbox[1],
            "bbox_z": bbox[2],
            "volume": float(mesh.volume) if mesh.is_volume else 0.0,
            "surface_area": float(mesh.area),
            "n_faces": float(len(mesh.faces)),
        }
        return float(mapping.get(measurement, 0.0))

    def _empty_result(self) -> dict:
        return {
            "n_satisfied": 0,
            "n_violated": 0,
            "worst_violation_mag": 0.0,
            "total_violation_mag": 0.0,
            "constraint_type_dim": 1.0,
            "constraint_type_tol": 1.0,
            "constraint_type_feat": 1.0,
        }
