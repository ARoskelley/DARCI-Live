"""Mesh quality analysis using trimesh."""

import numpy as np
import trimesh
from typing import Optional


class MeshAnalyzer:
    """Computes quality metrics from a trimesh.Trimesh instance."""

    # Normalization constants matching StateEncoder
    MAX_EXPECTED_FACES = 5000
    MAX_EXPECTED_EDGES = 10000
    TARGET_THICKNESS = 2.0        # mm
    MAX_THICKNESS = 20.0          # mm
    MAX_WALL_VIOLATIONS = 50
    MAX_OVERHANG_AREA_MM2 = 10000.0
    MIN_PRINTABLE_SIZE = 0.4      # mm  (typical FDM minimum)
    MAX_BRIDGE_SPAN = 50.0        # mm
    MAX_ISLANDS = 10
    MAX_BUILD_HEIGHT = 300.0      # mm
    BBOX_DIAGONAL_FALLBACK = 1.0

    def __init__(self, mesh: trimesh.Trimesh):
        self.mesh = mesh
        self._bbox_diagonal = float(np.linalg.norm(mesh.bounding_box.extents)) or 1.0

    # ------------------------------------------------------------------ #
    # Public API                                                           #
    # ------------------------------------------------------------------ #

    def basic_metrics(self) -> dict:
        m = self.mesh
        bbox = m.bounding_box.extents
        bbox_vol = float(bbox[0] * bbox[1] * bbox[2]) or 1e-6

        return {
            "volume": float(m.volume) if m.is_volume else 0.0,
            "surface_area": float(m.area),
            "bbox_x": float(bbox[0]),
            "bbox_y": float(bbox[1]),
            "bbox_z": float(bbox[2]),
            "fill_ratio": float(m.volume / bbox_vol) if m.is_volume else 0.0,
            "n_faces": int(len(m.faces)),
            "n_edges": int(len(m.edges_unique)),
            "is_watertight": float(m.is_watertight),
        }

    def wall_thickness_analysis(self, n_samples: int = 1000) -> dict:
        """Ray-cast thickness sampling across the mesh surface."""
        mesh = self.mesh
        if len(mesh.faces) == 0:
            return self._empty_wall_metrics()

        try:
            # Sample random points on the surface
            points, face_indices = trimesh.sample.sample_surface(mesh, n_samples)
            normals = mesh.face_normals[face_indices]

            # Cast rays inward (along -normal) and measure hit distance
            origins = points + normals * 1e-4          # nudge off surface
            directions = -normals

            locations, index_ray, _ = mesh.ray.intersects_location(
                ray_origins=origins,
                ray_directions=directions,
                multiple_hits=False,
            )

            if len(locations) == 0:
                return self._empty_wall_metrics()

            # Thickness = distance from origin to opposite surface hit
            hit_origins = origins[index_ray]
            thicknesses = np.linalg.norm(locations - hit_origins, axis=1)

            # Filter obviously bad rays (> 5× bbox diagonal)
            max_t = self._bbox_diagonal * 5.0
            thicknesses = thicknesses[thicknesses < max_t]

            if len(thicknesses) == 0:
                return self._empty_wall_metrics()

            min_t = TARGET_THICKNESS = self.TARGET_THICKNESS
            max_allowed = self.MAX_THICKNESS

            min_wall = float(np.min(thicknesses))
            max_wall = float(np.max(thicknesses))
            mean_wall = float(np.mean(thicknesses))
            std_wall = float(np.std(thicknesses))
            pct_below = float(np.mean(thicknesses < min_t))
            pct_above = float(np.mean(thicknesses > max_allowed))
            n_violations = int(np.sum(thicknesses < min_t))

            # Find location of thinnest wall
            thin_idx = np.argmin(thicknesses)
            thin_orig = hit_origins[thin_idx] if thin_idx < len(hit_origins) else np.zeros(3)
            bbox_center = np.array(mesh.bounding_box.centroid)
            bbox_ext = np.array(mesh.bounding_box.extents) + 1e-6
            thin_rel = (thin_orig - bbox_center) / bbox_ext

            return {
                "min_wall_thickness": min_wall,
                "max_wall_thickness": max_wall,
                "mean_wall_thickness": mean_wall,
                "wall_std_dev": std_wall,
                "pct_below_min": pct_below,
                "pct_above_max": pct_above,
                "thinnest_region_x": float(np.clip(thin_rel[0], -1, 1)),
                "thinnest_region_y": float(np.clip(thin_rel[1], -1, 1)),
                "thinnest_region_z": float(np.clip(thin_rel[2], -1, 1)),
                "wall_violation_count": n_violations,
            }

        except Exception:
            return self._empty_wall_metrics()

    def printability_analysis(
        self,
        build_dir: list = None,
        overhang_threshold: float = 45.0,
    ) -> dict:
        """Analyse additive manufacturing printability."""
        if build_dir is None:
            build_dir = [0.0, 0.0, 1.0]

        mesh = self.mesh
        build_vec = np.array(build_dir, dtype=float)
        build_vec /= np.linalg.norm(build_vec) + 1e-9

        try:
            # Overhang detection: faces whose normal has negative Z component
            normals = mesh.face_normals
            dot = normals @ build_vec          # cos(angle with up)
            angles_deg = np.degrees(np.arccos(np.clip(-dot, -1, 1)))

            # An overhang face has angle < (90 - overhang_threshold) from horizontal
            # i.e. the normal points downward more than threshold degrees from build_dir
            overhang_mask = dot < -np.cos(np.radians(90 - overhang_threshold))
            overhang_faces = np.sum(overhang_mask)
            total_faces = len(mesh.faces)

            # Estimate overhang area
            face_areas = mesh.area_faces
            overhang_area = float(np.sum(face_areas[overhang_mask]))
            total_area = float(mesh.area) or 1e-6

            # Worst (steepest) overhang
            if overhang_faces > 0:
                worst_angle = float(np.max(angles_deg[overhang_mask]))
            else:
                worst_angle = 0.0

            # Volume / support estimate: rough — proportional to overhang area × build height
            build_height = float(mesh.bounding_box.extents[2])
            support_vol_ratio = float((overhang_area / total_area) * 0.3)  # heuristic

            # Minimum feature size: shortest edge
            edges = mesh.edges_unique
            verts = mesh.vertices
            edge_lengths = np.linalg.norm(verts[edges[:, 1]] - verts[edges[:, 0]], axis=1)
            min_feat = float(np.min(edge_lengths)) if len(edge_lengths) > 0 else 0.0

            # Bridge detection (simplified): horizontal spans not touching Z-base
            # Approximate: look for horizontal faces far from Z=min
            z_min = float(mesh.bounds[0, 2])
            face_centers = mesh.triangles_center
            face_z = face_centers[:, 2]
            horiz_mask = np.abs(dot) > 0.95  # nearly horizontal faces
            elevated_horiz = horiz_mask & (face_z > z_min + 1.0)
            if np.any(elevated_horiz):
                bridge_span = float(np.max(face_areas[elevated_horiz])) ** 0.5  # rough
            else:
                bridge_span = 0.0

            # Islands: connected components
            components = trimesh.graph.connected_components(mesh.face_adjacency, min_len=1)
            n_islands = len(components)

            # First layer area: faces at Z~min
            first_layer_mask = np.abs(face_z - z_min) < 0.5
            first_layer_area = float(np.sum(face_areas[first_layer_mask]))

            # Enclosed voids: check for interior volumes (watertight sub-meshes)
            has_voids = float(not mesh.is_watertight and mesh.is_volume)

            # Composite printability score (0–1)
            score_overhang = 1.0 - float(overhang_faces) / max(total_faces, 1)
            score_wall = float(np.mean(edge_lengths >= self.MIN_PRINTABLE_SIZE)) if len(edge_lengths) > 0 else 0.0
            score_watertight = 1.0 if mesh.is_watertight else 0.5
            printability_score = float(np.mean([score_overhang, score_wall, score_watertight]))

            return {
                "max_overhang_angle": worst_angle,
                "overhang_area_pct": overhang_area / total_area,
                "support_volume_ratio": support_vol_ratio,
                "min_feature_size": min_feat,
                "bridge_max_span": bridge_span,
                "n_islands": n_islands,
                "build_height": build_height,
                "first_layer_area": first_layer_area,
                "has_enclosed_voids": has_voids,
                "printability_score": printability_score,
            }

        except Exception:
            return self._empty_printability_metrics()

    def mesh_quality(self) -> dict:
        """Triangle mesh quality statistics."""
        mesh = self.mesh
        try:
            verts = mesh.vertices
            faces = mesh.faces

            v0 = verts[faces[:, 0]]
            v1 = verts[faces[:, 1]]
            v2 = verts[faces[:, 2]]

            e0 = np.linalg.norm(v1 - v0, axis=1)
            e1 = np.linalg.norm(v2 - v1, axis=1)
            e2 = np.linalg.norm(v0 - v2, axis=1)

            all_edges = np.concatenate([e0, e1, e2])
            diag = self._bbox_diagonal or 1.0

            # Aspect ratio: max_edge / min_edge per triangle
            stacked = np.stack([e0, e1, e2], axis=1)
            max_e = np.max(stacked, axis=1)
            min_e = np.min(stacked, axis=1) + 1e-12
            aspect = max_e / min_e

            # Degenerate: area ≈ 0
            areas = mesh.area_faces
            pct_degen = float(np.mean(areas < 1e-8))

            # Mesh validity check
            is_valid = float(mesh.is_watertight and not mesh.is_empty)

            return {
                "mesh_face_count": len(faces),
                "min_aspect_ratio": float(np.min(aspect)),
                "mean_aspect_ratio": float(np.mean(aspect)),
                "pct_degenerate": pct_degen,
                "max_edge_length": float(np.max(all_edges)) / diag,
                "min_edge_length": float(np.min(all_edges)) / diag,
                "edge_length_ratio": float(np.min(all_edges)) / (float(np.max(all_edges)) + 1e-12),
                "mesh_is_valid": is_valid,
            }

        except Exception:
            return {
                "mesh_face_count": 0,
                "min_aspect_ratio": 0.0,
                "mean_aspect_ratio": 0.0,
                "pct_degenerate": 1.0,
                "max_edge_length": 0.0,
                "min_edge_length": 0.0,
                "edge_length_ratio": 0.0,
                "mesh_is_valid": 0.0,
            }

    # ------------------------------------------------------------------ #
    # Helpers                                                              #
    # ------------------------------------------------------------------ #

    def _empty_wall_metrics(self) -> dict:
        return {
            "min_wall_thickness": 0.0,
            "max_wall_thickness": 0.0,
            "mean_wall_thickness": 0.0,
            "wall_std_dev": 0.0,
            "pct_below_min": 1.0,
            "pct_above_max": 0.0,
            "thinnest_region_x": 0.0,
            "thinnest_region_y": 0.0,
            "thinnest_region_z": 0.0,
            "wall_violation_count": 0,
        }

    def _empty_printability_metrics(self) -> dict:
        return {
            "max_overhang_angle": 0.0,
            "overhang_area_pct": 0.0,
            "support_volume_ratio": 0.0,
            "min_feature_size": 0.0,
            "bridge_max_span": 0.0,
            "n_islands": 1,
            "build_height": 0.0,
            "first_layer_area": 0.0,
            "has_enclosed_voids": 0.0,
            "printability_score": 0.0,
        }
