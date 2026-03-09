"""Shape comparison between current mesh and a reference mesh.

Metrics:
- hausdorff_distance      worst-case surface deviation
- mean_surface_deviation  average deviation
- volume_ratio            current/reference volume
- bbox_similarity         bounding box overlap score
- feature_count_diff      |n_faces_curr - n_faces_ref|
- topology_match          rough topology similarity
- n_missing_features      (heuristic)
- n_extra_features        (heuristic)
- alignment_score         ICP-based alignment quality
"""

import numpy as np
import trimesh
from typing import Optional

try:
    from scipy.spatial import cKDTree
    _HAS_SCIPY = True
except ImportError:
    _HAS_SCIPY = False


class ReferenceComparator:
    """Compare a mesh to a reference mesh and return named metrics."""

    N_SAMPLE_POINTS = 500   # points to sample for distance computation

    def compare(self, current: trimesh.Trimesh, reference: trimesh.Trimesh) -> dict:
        """Return comparison metrics dict."""
        try:
            curr_pts = self._sample(current)
            ref_pts = self._sample(reference)

            hausdorff, mean_dev = self._surface_distances(curr_pts, ref_pts)
            vol_ratio = self._volume_ratio(current, reference)
            bbox_sim = self._bbox_similarity(current, reference)
            feat_diff, n_missing, n_extra = self._feature_counts(current, reference)
            topo_match = self._topology_match(current, reference)
            align_score = self._alignment_score(curr_pts, ref_pts)

            return {
                "hausdorff_distance": hausdorff,
                "mean_surface_deviation": mean_dev,
                "volume_ratio": vol_ratio,
                "bbox_similarity": bbox_sim,
                "feature_count_diff": feat_diff,
                "topology_match": topo_match,
                "n_missing_features": n_missing,
                "n_extra_features": n_extra,
                "alignment_score": align_score,
            }
        except Exception:
            return self._empty_metrics()

    # ------------------------------------------------------------------ #
    # Private helpers                                                      #
    # ------------------------------------------------------------------ #

    def _sample(self, mesh: trimesh.Trimesh) -> np.ndarray:
        """Sample surface points from a mesh."""
        try:
            pts, _ = trimesh.sample.sample_surface(mesh, self.N_SAMPLE_POINTS)
            return pts
        except Exception:
            return np.zeros((1, 3))

    def _surface_distances(self, pts_a: np.ndarray, pts_b: np.ndarray):
        """Compute hausdorff and mean distances A→B."""
        if not _HAS_SCIPY:
            return 0.0, 0.0
        try:
            tree_b = cKDTree(pts_b)
            dists_ab, _ = tree_b.query(pts_a)
            tree_a = cKDTree(pts_a)
            dists_ba, _ = tree_a.query(pts_b)
            hausdorff = float(max(np.max(dists_ab), np.max(dists_ba)))
            mean_dev = float(np.mean(np.concatenate([dists_ab, dists_ba])))
            return hausdorff, mean_dev
        except Exception:
            return 0.0, 0.0

    def _volume_ratio(self, current: trimesh.Trimesh, reference: trimesh.Trimesh) -> float:
        # Use abs() — trimesh computes volume even for non-watertight meshes (may be signed)
        ref_vol = abs(float(reference.volume)) or 1e-6
        curr_vol = abs(float(current.volume))
        return float(np.clip(curr_vol / (ref_vol + 1e-9), 0, 2))

    def _bbox_similarity(self, current: trimesh.Trimesh, reference: trimesh.Trimesh) -> float:
        """Intersection-over-union of bounding boxes."""
        try:
            c_min, c_max = current.bounds[0], current.bounds[1]
            r_min, r_max = reference.bounds[0], reference.bounds[1]
            inter_min = np.maximum(c_min, r_min)
            inter_max = np.minimum(c_max, r_max)
            inter_dims = np.maximum(0, inter_max - inter_min)
            inter_vol = float(np.prod(inter_dims))
            c_vol = float(np.prod(c_max - c_min)) or 1e-6
            r_vol = float(np.prod(r_max - r_min)) or 1e-6
            union_vol = c_vol + r_vol - inter_vol
            return float(np.clip(inter_vol / (union_vol + 1e-9), 0, 1))
        except Exception:
            return 0.0

    def _feature_counts(self, current: trimesh.Trimesh, reference: trimesh.Trimesh):
        """Rough feature count comparison using connected face components."""
        try:
            curr_comps = len(trimesh.graph.connected_components(current.face_adjacency, min_len=1))
            ref_comps = len(trimesh.graph.connected_components(reference.face_adjacency, min_len=1))
            diff = abs(curr_comps - ref_comps)
            missing = max(0, ref_comps - curr_comps)
            extra = max(0, curr_comps - ref_comps)
            return float(diff), float(missing), float(extra)
        except Exception:
            return 0.0, 0.0, 0.0

    def _topology_match(self, current: trimesh.Trimesh, reference: trimesh.Trimesh) -> float:
        """Euler characteristic similarity."""
        try:
            c_euler = current.euler_number
            r_euler = reference.euler_number
            diff = abs(c_euler - r_euler)
            return float(np.clip(1.0 - diff / max(abs(r_euler) + 1, 1), 0, 1))
        except Exception:
            return 0.0

    def _alignment_score(self, pts_a: np.ndarray, pts_b: np.ndarray) -> float:
        """Approximate alignment quality — lower mean distance after centroid alignment."""
        if not _HAS_SCIPY:
            return 0.0
        try:
            pts_a_c = pts_a - pts_a.mean(axis=0)
            pts_b_c = pts_b - pts_b.mean(axis=0)
            tree = cKDTree(pts_b_c)
            dists, _ = tree.query(pts_a_c)
            mean_dist = float(np.mean(dists))
            # Normalise by reference span
            span = float(np.max(np.linalg.norm(pts_b_c, axis=1))) + 1e-6
            return float(np.clip(1.0 - mean_dist / span, 0, 1))
        except Exception:
            return 0.0

    def _empty_metrics(self) -> dict:
        return {
            "hausdorff_distance": 0.0,
            "mean_surface_deviation": 0.0,
            "volume_ratio": 0.0,
            "bbox_similarity": 0.0,
            "feature_count_diff": 0.0,
            "topology_match": 0.0,
            "n_missing_features": 0.0,
            "n_extra_features": 0.0,
            "alignment_score": 0.0,
        }
