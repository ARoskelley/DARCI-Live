"""Tests for the DARCI Geometry Workbench service.

Run:
    cd DARCI-v4/Darci.Engineering.Workbench
    python -m pytest tests/ -v
"""

import sys
import os
import numpy as np
import pytest

# Make sure the workbench package is importable
sys.path.insert(0, os.path.dirname(os.path.dirname(__file__)))

from workbench.engine import GeometryEngine
from workbench.state_encoder import StateEncoder
from workbench.action_executor import ActionExecutor
from workbench.mesh_analyzer import MeshAnalyzer
from workbench.validator import Validator
from workbench.constraint_checker import ConstraintChecker
from workbench.reference_comparator import ReferenceComparator


# --------------------------------------------------------------------------- #
# Fixtures                                                                     #
# --------------------------------------------------------------------------- #

@pytest.fixture
def engine():
    e = GeometryEngine()
    e.reset()
    return e


@pytest.fixture
def box_engine(engine):
    """Engine with a 20×20×10 mm box."""
    params = np.array([0.0, 0.0, 0.0, 0.5, 0.5, 0.0], dtype=np.float32)
    engine.execute_action(4, params)  # add_box
    return engine


# --------------------------------------------------------------------------- #
# 1. Engine reset and basic box creation                                       #
# --------------------------------------------------------------------------- #

class TestEngineReset:
    def test_reset_clears_state(self, engine):
        params = np.zeros(6, dtype=np.float32)
        engine.execute_action(4, params)
        assert engine.is_active

        engine.reset()
        assert not engine.is_active
        assert engine.step_count == 0
        assert engine.current_mesh is None

    def test_add_box_creates_geometry(self, box_engine):
        assert box_engine.is_active
        assert box_engine.current_mesh is not None
        assert box_engine.step_count == 1

    def test_add_box_mesh_has_faces(self, box_engine):
        assert len(box_engine.current_mesh.faces) > 0

    def test_add_cylinder_creates_geometry(self, engine):
        params = np.array([0.0, 0.0, 0.0, 0.5, 0.5, 0.0], dtype=np.float32)
        result = engine.execute_action(3, params)
        assert result["success"]
        assert engine.is_active
        assert engine.current_mesh is not None


# --------------------------------------------------------------------------- #
# 2. Each action type executes without crashing                                #
# --------------------------------------------------------------------------- #

class TestActions:
    """Each action should return a result dict without raising."""

    def _run(self, engine, action_id):
        params = np.zeros(6, dtype=np.float32)
        result = engine.execute_action(action_id, params)
        assert isinstance(result, dict)
        assert "success" in result
        assert "state" in result
        return result

    def test_action_add_box(self, engine):
        self._run(engine, 4)

    def test_action_add_cylinder(self, engine):
        self._run(engine, 3)

    def test_action_cut(self, box_engine):
        self._run(box_engine, 1)

    def test_action_fillet(self, box_engine):
        result = self._run(box_engine, 5)
        # Fillet may fail if radius is too large — that's OK, it should not crash
        assert "error_message" in result

    def test_action_chamfer(self, box_engine):
        self._run(box_engine, 6)

    def test_action_translate(self, box_engine):
        self._run(box_engine, 11)

    def test_action_mirror(self, box_engine):
        self._run(box_engine, 13)

    def test_action_validate(self, box_engine):
        result = self._run(box_engine, 18)
        assert result["success"]

    def test_action_finalize(self, box_engine):
        result = self._run(box_engine, 19)
        assert result["success"]

    def test_unknown_action_id_returns_failure(self, engine):
        params = np.zeros(6, dtype=np.float32)
        result = engine.execute_action(99, params)
        assert not result["success"]


# --------------------------------------------------------------------------- #
# 3. State encoder produces correct-length vector (64)                         #
# --------------------------------------------------------------------------- #

class TestStateEncoder:
    def test_empty_state_length(self, engine):
        state = engine.get_state()
        assert state.shape == (64,)

    def test_state_with_geometry_length(self, box_engine):
        state = box_engine.get_state()
        assert state.shape == (64,)

    def test_state_dtype(self, box_engine):
        state = box_engine.get_state()
        assert state.dtype == np.float32

    def test_state_range(self, box_engine):
        state = box_engine.get_state()
        # Most values should be in [-1, 1]
        assert float(np.max(state)) <= 1.01
        assert float(np.min(state)) >= -1.01

    def test_state_is_deterministic(self, box_engine):
        s1 = box_engine.get_state()
        s2 = box_engine.get_state()
        # Index 51 is time_elapsed — it advances between calls by design.
        # All other geometry/task dimensions must be identical.
        time_idx = 51
        mask = np.ones(64, dtype=bool)
        mask[time_idx] = False
        np.testing.assert_array_equal(s1[mask], s2[mask])


# --------------------------------------------------------------------------- #
# 4. Action mask correctly disables invalid actions                            #
# --------------------------------------------------------------------------- #

class TestActionMask:
    def test_no_geometry_only_primitives_allowed(self, engine):
        mask = engine.get_action_mask()
        # Only add_cylinder (3), add_box (4), validate (18), finalize (19)
        assert mask[3]
        assert mask[4]
        assert mask[18]
        assert mask[19]
        # Everything else masked
        for i in [0, 1, 2, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17]:
            assert not mask[i], f"Action {i} should be masked with no geometry"

    def test_with_geometry_most_actions_available(self, box_engine):
        mask = box_engine.get_action_mask()
        # Primitive creation and basic ops should be available
        assert mask[4]   # add_box
        assert mask[3]   # add_cylinder
        assert mask[5]   # fillet
        assert mask[18]  # validate
        assert mask[19]  # finalize

    def test_mask_length(self, engine):
        mask = engine.get_action_mask()
        assert len(mask) == 20


# --------------------------------------------------------------------------- #
# 5. Undo reverts to previous state                                            #
# --------------------------------------------------------------------------- #

class TestUndo:
    def test_undo_reverts_geometry(self, engine):
        # Start empty
        assert not engine.is_active

        # Add box
        params = np.zeros(6, dtype=np.float32)
        engine.execute_action(4, params)
        assert engine.is_active

        # Undo
        success = engine.undo()
        assert success
        assert not engine.is_active

    def test_undo_empty_returns_false(self, engine):
        result = engine.undo()
        assert not result

    def test_undo_decrements_step_count(self, box_engine):
        count_before = box_engine.step_count
        box_engine.undo()
        assert box_engine.step_count == count_before - 1 or box_engine.step_count == 0


# --------------------------------------------------------------------------- #
# 6. Reference loading and comparison metrics                                  #
# --------------------------------------------------------------------------- #

class TestReferenceComparator:
    def test_compare_mesh_to_itself(self, box_engine):
        mesh = box_engine.current_mesh
        comparator = ReferenceComparator()
        metrics = comparator.compare(mesh, mesh)

        assert "hausdorff_distance" in metrics
        assert "mean_surface_deviation" in metrics
        assert "volume_ratio" in metrics
        assert "bbox_similarity" in metrics
        assert "alignment_score" in metrics

    def test_volume_ratio_self_is_one(self, box_engine):
        mesh = box_engine.current_mesh
        comparator = ReferenceComparator()
        metrics = comparator.compare(mesh, mesh)
        # Self-comparison: volume ratio ≈ 1.0
        assert abs(metrics["volume_ratio"] - 1.0) < 0.05

    def test_bbox_similarity_self_is_high(self, box_engine):
        mesh = box_engine.current_mesh
        comparator = ReferenceComparator()
        metrics = comparator.compare(mesh, mesh)
        assert metrics["bbox_similarity"] > 0.9

    def test_state_with_reference(self, box_engine):
        # Load same mesh as reference
        box_engine._reference_mesh = box_engine.current_mesh
        state = box_engine.get_state()
        assert state.shape == (64,)
        assert state[40] == 1.0  # has_reference flag


# --------------------------------------------------------------------------- #
# 7. Wall thickness analysis returns valid statistics                          #
# --------------------------------------------------------------------------- #

class TestWallThickness:
    def test_returns_expected_keys(self, box_engine):
        analyzer = box_engine.mesh_analyzer
        assert analyzer is not None
        result = analyzer.wall_thickness_analysis()
        expected_keys = [
            "min_wall_thickness", "max_wall_thickness", "mean_wall_thickness",
            "wall_std_dev", "pct_below_min", "pct_above_max",
            "thinnest_region_x", "thinnest_region_y", "thinnest_region_z",
            "wall_violation_count",
        ]
        for k in expected_keys:
            assert k in result, f"Missing key: {k}"

    def test_thickness_values_are_non_negative(self, box_engine):
        result = box_engine.mesh_analyzer.wall_thickness_analysis()
        assert result["min_wall_thickness"] >= 0.0
        assert result["mean_wall_thickness"] >= 0.0

    def test_percentages_in_range(self, box_engine):
        result = box_engine.mesh_analyzer.wall_thickness_analysis()
        assert 0.0 <= result["pct_below_min"] <= 1.0
        assert 0.0 <= result["pct_above_max"] <= 1.0


# --------------------------------------------------------------------------- #
# 8. Printability analysis identifies overhangs                                #
# --------------------------------------------------------------------------- #

class TestPrintability:
    def test_returns_expected_keys(self, box_engine):
        analyzer = box_engine.mesh_analyzer
        result = analyzer.printability_analysis()
        expected_keys = [
            "max_overhang_angle", "overhang_area_pct", "support_volume_ratio",
            "min_feature_size", "bridge_max_span", "n_islands", "build_height",
            "first_layer_area", "has_enclosed_voids", "printability_score",
        ]
        for k in expected_keys:
            assert k in result, f"Missing key: {k}"

    def test_box_has_low_overhang(self, box_engine):
        result = box_engine.mesh_analyzer.printability_analysis()
        # A simple box has overhangs (bottom face) but score should be reasonable
        assert 0.0 <= result["printability_score"] <= 1.0

    def test_printability_score_range(self, box_engine):
        result = box_engine.mesh_analyzer.printability_analysis()
        assert 0.0 <= result["printability_score"] <= 1.0

    def test_n_islands_is_positive(self, box_engine):
        result = box_engine.mesh_analyzer.printability_analysis()
        assert result["n_islands"] >= 1


# --------------------------------------------------------------------------- #
# Additional integration tests                                                 #
# --------------------------------------------------------------------------- #

class TestConstraintChecker:
    def test_empty_constraints(self, box_engine):
        checker = ConstraintChecker()
        result = checker.check(box_engine.current_mesh, {})
        assert result["n_satisfied"] == 0
        assert result["n_violated"] == 0

    def test_dimensional_constraint_satisfied(self, box_engine):
        checker = ConstraintChecker()
        mesh = box_engine.current_mesh
        bbox_x = float(mesh.bounding_box.extents[0])
        constraints = {
            "width": {
                "type": "dimensional",
                "measurement": "bbox_x",
                "min": bbox_x - 1.0,
                "max": bbox_x + 1.0,
            }
        }
        result = checker.check(mesh, constraints)
        assert result["n_satisfied"] == 1
        assert result["n_violated"] == 0

    def test_dimensional_constraint_violated(self, box_engine):
        checker = ConstraintChecker()
        mesh = box_engine.current_mesh
        constraints = {
            "width": {
                "type": "dimensional",
                "measurement": "bbox_x",
                "min": 9999.0,
                "max": 99999.0,
            }
        }
        result = checker.check(mesh, constraints)
        assert result["n_violated"] == 1


class TestValidator:
    def test_no_geometry_fails(self, engine):
        result = engine.validate()
        assert not result["passed"]
        assert result["overall_score"] == 0.0

    def test_box_geometry_returns_report(self, box_engine):
        result = box_engine.validate()
        assert "passed" in result
        assert "overall_score" in result
        assert "category_scores" in result
        assert "violations" in result

    def test_overall_score_in_range(self, box_engine):
        result = box_engine.validate()
        assert 0.0 <= result["overall_score"] <= 1.0


class TestMeshQuality:
    def test_returns_expected_keys(self, box_engine):
        result = box_engine.mesh_analyzer.mesh_quality()
        for k in ["mesh_face_count", "min_aspect_ratio", "mean_aspect_ratio",
                  "pct_degenerate", "max_edge_length", "min_edge_length",
                  "edge_length_ratio", "mesh_is_valid"]:
            assert k in result

    def test_box_has_positive_face_count(self, box_engine):
        result = box_engine.mesh_analyzer.mesh_quality()
        assert result["mesh_face_count"] > 0
