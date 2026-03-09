"""Full validation suite for the Geometry Workbench.

Aggregates mesh, printability, constraint, and reference checks
into a single structured validation report.
"""

import numpy as np
import trimesh
from typing import Optional


class Validator:
    """Runs all checks and produces a decomposed validation report."""

    # Thresholds
    MIN_WALL_THICKNESS = 0.4       # mm
    MAX_OVERHANG_ANGLE = 60.0      # degrees
    MIN_PRINTABILITY_SCORE = 0.6
    MAX_DEGENERATE_PCT = 0.01      # 1%

    def validate(self, engine) -> dict:
        """Run full validation on the current engine state."""
        violations = []
        category_scores = {}

        mesh = engine.current_mesh
        if mesh is None:
            return {
                "passed": False,
                "overall_score": 0.0,
                "category_scores": {"geometry": 0.0},
                "violations": [
                    {
                        "category": "geometry",
                        "severity": "error",
                        "description": "No geometry loaded",
                        "value": None,
                        "threshold": None,
                        "location": None,
                    }
                ],
            }

        analyzer = engine.mesh_analyzer

        # --- Geometry integrity ---
        geo_score, geo_viols = self._check_geometry(mesh)
        category_scores["geometry"] = geo_score
        violations.extend(geo_viols)

        # --- Wall thickness ---
        if analyzer:
            wall_score, wall_viols = self._check_walls(analyzer)
            category_scores["wall_thickness"] = wall_score
            violations.extend(wall_viols)

            # --- Printability ---
            print_score, print_viols = self._check_printability(analyzer)
            category_scores["printability"] = print_score
            violations.extend(print_viols)

            # --- Mesh quality ---
            mq_score, mq_viols = self._check_mesh_quality(analyzer)
            category_scores["mesh_quality"] = mq_score
            violations.extend(mq_viols)

        # --- Constraints ---
        if engine.constraints:
            cons_score, cons_viols = self._check_constraints(mesh, engine)
            category_scores["constraints"] = cons_score
            violations.extend(cons_viols)

        # --- Reference comparison ---
        if engine.reference_mesh is not None:
            ref_score, ref_viols = self._check_reference(mesh, engine)
            category_scores["reference"] = ref_score
            violations.extend(ref_viols)

        # Overall score
        scores = list(category_scores.values())
        overall = float(np.mean(scores)) if scores else 0.0

        # Passed if no errors
        has_errors = any(v["severity"] == "error" for v in violations)
        passed = not has_errors and overall >= 0.6

        return {
            "passed": passed,
            "overall_score": overall,
            "category_scores": category_scores,
            "violations": violations,
        }

    # ------------------------------------------------------------------ #
    # Category checks                                                      #
    # ------------------------------------------------------------------ #

    def _check_geometry(self, mesh):
        violations = []
        score = 1.0

        if not mesh.is_watertight:
            violations.append(self._viol(
                "geometry", "error", "Mesh is not watertight (not a closed solid)"
            ))
            score -= 0.5

        if mesh.is_empty:
            violations.append(self._viol("geometry", "error", "Mesh is empty"))
            score = 0.0

        return max(score, 0.0), violations

    def _check_walls(self, analyzer):
        violations = []
        m = analyzer.wall_thickness_analysis()
        score = 1.0

        min_t = m.get("min_wall_thickness", 0.0)
        if min_t < self.MIN_WALL_THICKNESS and min_t > 0:
            violations.append(self._viol(
                "wall_thickness", "error",
                f"Minimum wall thickness {min_t:.2f}mm is below {self.MIN_WALL_THICKNESS}mm",
                value=min_t, threshold=self.MIN_WALL_THICKNESS,
                location=[m.get("thinnest_region_x", 0), m.get("thinnest_region_y", 0), m.get("thinnest_region_z", 0)],
            ))
            score -= 0.4

        pct_below = m.get("pct_below_min", 0.0)
        if pct_below > 0.1:
            violations.append(self._viol(
                "wall_thickness", "warning",
                f"{pct_below*100:.1f}% of walls are below minimum thickness",
                value=pct_below, threshold=0.1,
            ))
            score -= 0.2

        return max(score, 0.0), violations

    def _check_printability(self, analyzer):
        violations = []
        p = analyzer.printability_analysis()
        score = 1.0

        max_overhang = p.get("max_overhang_angle", 0.0)
        if max_overhang > self.MAX_OVERHANG_ANGLE:
            violations.append(self._viol(
                "printability", "warning",
                f"Max overhang angle {max_overhang:.1f}° exceeds {self.MAX_OVERHANG_ANGLE}°",
                value=max_overhang, threshold=self.MAX_OVERHANG_ANGLE,
            ))
            score -= 0.3

        ps = p.get("printability_score", 1.0)
        if ps < self.MIN_PRINTABILITY_SCORE:
            violations.append(self._viol(
                "printability", "warning",
                f"Printability score {ps:.2f} is below threshold {self.MIN_PRINTABILITY_SCORE}",
                value=ps, threshold=self.MIN_PRINTABILITY_SCORE,
            ))
            score -= 0.2

        if p.get("has_enclosed_voids", 0) > 0:
            violations.append(self._viol(
                "printability", "warning",
                "Part has enclosed voids that will trap support material",
            ))
            score -= 0.15

        return max(score, 0.0), violations

    def _check_mesh_quality(self, analyzer):
        violations = []
        q = analyzer.mesh_quality()
        score = 1.0

        pct_degen = q.get("pct_degenerate", 0.0)
        if pct_degen > self.MAX_DEGENERATE_PCT:
            violations.append(self._viol(
                "mesh_quality", "warning",
                f"{pct_degen*100:.2f}% degenerate triangles found",
                value=pct_degen, threshold=self.MAX_DEGENERATE_PCT,
            ))
            score -= 0.2

        if not q.get("mesh_is_valid", True):
            violations.append(self._viol(
                "mesh_quality", "error", "Mesh has self-intersections or invalid normals"
            ))
            score -= 0.4

        return max(score, 0.0), violations

    def _check_constraints(self, mesh, engine):
        violations = []
        result = engine.constraint_checker.check(mesh, engine.constraints)
        n_violated = result.get("n_violated", 0)
        n_total = len(engine.constraints)
        score = result.get("constraint_type_dim", 1.0)

        if n_violated > 0:
            violations.append(self._viol(
                "constraints", "error",
                f"{n_violated}/{n_total} engineering constraints violated",
                value=float(n_violated), threshold=0.0,
            ))

        return max(score, 0.0), violations

    def _check_reference(self, mesh, engine):
        violations = []
        score = 1.0
        try:
            m = engine.reference_comparator.compare(mesh, engine.reference_mesh)
            h = m.get("hausdorff_distance", 0)
            if h > 5.0:  # > 5mm hausdorff
                violations.append(self._viol(
                    "reference", "warning",
                    f"Hausdorff distance {h:.2f}mm from reference",
                    value=h, threshold=5.0,
                ))
                score -= 0.3
        except Exception:
            pass
        return max(score, 0.0), violations

    # ------------------------------------------------------------------ #
    # Helpers                                                              #
    # ------------------------------------------------------------------ #

    def _viol(self, category, severity, description, value=None, threshold=None, location=None):
        return {
            "category": category,
            "severity": severity,
            "description": description,
            "value": value,
            "threshold": threshold,
            "location": location,
        }
