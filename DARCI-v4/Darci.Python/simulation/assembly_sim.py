"""
Assembly simulation utilities for DARCI engineering collections.

Focus:
- static fit checks between generated part meshes
- motion sampling checks for rotational/linear movement
- deterministic clearance and collision heuristics in 3D space
"""

from __future__ import annotations

from dataclasses import dataclass
from typing import Optional

import numpy as np
import trimesh


@dataclass
class _Issue:
    severity: str
    code: str
    message: str
    part_a: Optional[str] = None
    part_b: Optional[str] = None
    connection: Optional[str] = None

    def to_dict(self) -> dict:
        return {
            "severity": self.severity,
            "code": self.code,
            "message": self.message,
            "partA": self.part_a,
            "partB": self.part_b,
            "connection": self.connection,
        }


def simulate_assembly(
    *,
    parts: list[dict],
    connections: list[dict],
    collision_tolerance_mm: float = 0.1,
    clearance_target_mm: float = 0.2,
    sample_points_per_mesh: int = 256,
) -> dict:
    sample_points_per_mesh = int(np.clip(sample_points_per_mesh, 64, 2048))
    collision_tolerance_mm = max(0.01, float(collision_tolerance_mm))
    clearance_target_mm = max(0.01, float(clearance_target_mm))

    issues: list[_Issue] = []
    motion_checks: list[dict] = []
    transformed_meshes: dict[str, trimesh.Trimesh] = {}
    part_meta: dict[str, dict] = {}

    for part in parts:
        name = str(part.get("name") or "").strip()
        stl_path = part.get("stlPath")
        if not name:
            issues.append(_Issue(
                severity="error",
                code="part_name_missing",
                message="A simulated part is missing a name."
            ))
            continue

        if not stl_path:
            issues.append(_Issue(
                severity="error",
                code="stl_path_missing",
                message=f"Part '{name}' has no STL path for simulation.",
                part_a=name
            ))
            continue

        try:
            base_mesh = _load_mesh(str(stl_path))
            mesh = base_mesh.copy()
            mesh.apply_transform(_pose_matrix(part))
            transformed_meshes[name] = mesh
            part_meta[name] = part
        except Exception as ex:
            issues.append(_Issue(
                severity="error",
                code="mesh_load_failed",
                message=f"Failed to load part '{name}' mesh: {ex}",
                part_a=name
            ))

    names = list(transformed_meshes.keys())
    if len(names) < 2:
        issues.append(_Issue(
            severity="error",
            code="insufficient_parts",
            message="At least two valid STL parts are required for assembly simulation."
        ))
        return _build_report(
            issues=issues,
            motion_checks=motion_checks,
            static_pairs_checked=0,
            static_collision_count=0,
            global_min_clearance_mm=None,
        )

    connection_map = _build_connection_map(connections)
    static_sample_points = _effective_sample_points(sample_points_per_mesh, len(names))
    motion_sample_points = max(64, static_sample_points // 2)

    static_pairs_checked = 0
    static_collision_count = 0
    global_min_clearance = np.inf

    for i in range(len(names)):
        for j in range(i + 1, len(names)):
            part_a = names[i]
            part_b = names[j]
            mesh_a = transformed_meshes[part_a]
            mesh_b = transformed_meshes[part_b]
            static_pairs_checked += 1

            relation = connection_map.get(_pair_key(part_a, part_b), "")
            allows_contact = _allows_contact(relation)
            coarse_gap = _aabb_gap_distance(mesh_a.bounds, mesh_b.bounds)
            requires_detailed_clearance = relation != "" or coarse_gap <= max(clearance_target_mm * 3.0, 2.0)

            clearance = (
                _estimate_min_clearance_mm(mesh_a, mesh_b, static_sample_points)
                if requires_detailed_clearance
                else coarse_gap
            )
            if np.isfinite(clearance):
                global_min_clearance = min(global_min_clearance, clearance)

            overlap_tolerance = collision_tolerance_mm * (2.0 if allows_contact else 1.0)
            colliding, min_overlap, voxel_overlap = _detect_interference(
                mesh_a,
                mesh_b,
                overlap_tolerance,
            )

            if colliding:
                static_collision_count += 1
                overlap_note = (
                    f", voxelOverlap={voxel_overlap}"
                    if voxel_overlap is not None
                    else ""
                )
                issues.append(_Issue(
                    severity="error",
                    code="static_interference",
                    message=(
                        f"Static interference between '{part_a}' and '{part_b}'. "
                        f"clearance={_fmt(clearance)} mm, overlap={_fmt(min_overlap)} mm{overlap_note}."
                    ),
                    part_a=part_a,
                    part_b=part_b,
                ))
            elif clearance < clearance_target_mm and not allows_contact:
                issues.append(_Issue(
                    severity="warning",
                    code="static_low_clearance",
                    message=(
                        f"Low static clearance between '{part_a}' and '{part_b}': "
                        f"{_fmt(clearance)} mm."
                    ),
                    part_a=part_a,
                    part_b=part_b,
                ))

    for connection in connections:
        motion = _infer_motion(connection)
        motion_type = motion["type"]
        if motion_type == "none":
            continue

        from_name = str(connection.get("from") or "").strip()
        to_name = str(connection.get("to") or "").strip()
        relation = str(connection.get("relation") or "connects")
        connection_label = f"{from_name}->{to_name}:{relation}"
        moving_name = motion["moving_part"]

        if moving_name not in transformed_meshes:
            issues.append(_Issue(
                severity="error",
                code="motion_part_missing",
                message=f"Motion simulation missing part '{moving_name}' for connection {connection_label}.",
                part_a=moving_name,
                connection=connection_label,
            ))
            continue

        base_mesh = transformed_meshes[moving_name]
        base_centroid = np.asarray(base_mesh.bounding_box.centroid, dtype=float)
        partner_name = to_name if moving_name == from_name else from_name
        sampled_params = _sample_motion_values(motion)
        interaction_margin = max(
            clearance_target_mm * 4.0,
            _estimate_motion_span_mm(base_mesh, motion) + collision_tolerance_mm * 3.0,
        )
        collision_steps: list[int] = []
        min_clearance = np.inf
        potential_neighbors: list[tuple[str, trimesh.Trimesh, bool]] = []

        for other_name, other_mesh in transformed_meshes.items():
            if other_name == moving_name:
                continue
            is_partner = other_name == partner_name
            if is_partner:
                potential_neighbors.append((other_name, other_mesh, True))
                continue

            gap = _aabb_gap_distance(base_mesh.bounds, other_mesh.bounds)
            if gap <= interaction_margin:
                potential_neighbors.append((other_name, other_mesh, False))

        for idx, parameter in enumerate(sampled_params):
            moved = base_mesh.copy()
            moved.apply_transform(_motion_transform(motion, parameter, base_centroid))

            step_colliding = False
            for other_name, other_mesh, is_partner in potential_neighbors:
                coarse_gap = _aabb_gap_distance(moved.bounds, other_mesh.bounds)
                if np.isfinite(coarse_gap):
                    min_clearance = min(min_clearance, coarse_gap)
                if not is_partner and coarse_gap > max(clearance_target_mm * 3.0, 2.0):
                    continue

                clearance = _estimate_min_clearance_mm(
                    moved,
                    other_mesh,
                    motion_sample_points,
                )
                if np.isfinite(clearance):
                    min_clearance = min(min_clearance, clearance)

                partner_contact = is_partner and _allows_contact(relation)
                overlap_tolerance = collision_tolerance_mm * (2.0 if partner_contact else 1.0)
                colliding, _, _ = _detect_interference(
                    moved,
                    other_mesh,
                    overlap_tolerance,
                )
                if colliding:
                    step_colliding = True
                    break

            if step_colliding:
                collision_steps.append(idx)

        passed = len(collision_steps) == 0
        motion_checks.append({
            "from": from_name,
            "to": to_name,
            "relation": relation,
            "motionType": motion_type,
            "passed": passed,
            "minClearanceMm": _float_or_none(min_clearance),
            "collisionSteps": collision_steps,
        })

        if collision_steps:
            issues.append(_Issue(
                severity="error",
                code="motion_collision",
                message=(
                    f"Motion collision detected for {connection_label} "
                    f"at step indices: {collision_steps}."
                ),
                part_a=from_name,
                part_b=to_name,
                connection=connection_label,
            ))
        elif np.isfinite(min_clearance) and min_clearance < clearance_target_mm and not _allows_contact(relation):
            issues.append(_Issue(
                severity="warning",
                code="motion_low_clearance",
                message=(
                    f"Motion path for {connection_label} has low clearance "
                    f"({_fmt(min_clearance)} mm)."
                ),
                part_a=from_name,
                part_b=to_name,
                connection=connection_label,
            ))

    return _build_report(
        issues=issues,
        motion_checks=motion_checks,
        static_pairs_checked=static_pairs_checked,
        static_collision_count=static_collision_count,
        global_min_clearance_mm=_float_or_none(global_min_clearance),
    )


def _build_report(
    *,
    issues: list[_Issue],
    motion_checks: list[dict],
    static_pairs_checked: int,
    static_collision_count: int,
    global_min_clearance_mm: Optional[float],
) -> dict:
    passed = not any(i.severity == "error" for i in issues)
    return {
        "passed": passed,
        "staticPairsChecked": static_pairs_checked,
        "staticCollisionCount": static_collision_count,
        "globalMinClearanceMm": global_min_clearance_mm,
        "motionChecks": motion_checks,
        "issues": [i.to_dict() for i in issues],
    }


def _load_mesh(path: str) -> trimesh.Trimesh:
    loaded = trimesh.load(path, force="mesh")
    if isinstance(loaded, trimesh.Scene):
        if len(loaded.geometry) == 0:
            raise RuntimeError("scene has no geometry")
        loaded = trimesh.util.concatenate(tuple(loaded.geometry.values()))
    if not isinstance(loaded, trimesh.Trimesh):
        raise RuntimeError("file did not load as a mesh")
    if len(loaded.vertices) == 0:
        raise RuntimeError("mesh has no vertices")
    loaded.remove_unreferenced_vertices()
    return loaded


def _pose_matrix(part: dict) -> np.ndarray:
    x = float(part.get("x") or 0.0)
    y = float(part.get("y") or 0.0)
    z = float(part.get("z") or 0.0)
    rx = np.deg2rad(float(part.get("rxDeg") or 0.0))
    ry = np.deg2rad(float(part.get("ryDeg") or 0.0))
    rz = np.deg2rad(float(part.get("rzDeg") or 0.0))
    rotation = trimesh.transformations.euler_matrix(rx, ry, rz, "sxyz")
    translation = trimesh.transformations.translation_matrix([x, y, z])
    return trimesh.transformations.concatenate_matrices(translation, rotation)


def _build_connection_map(connections: list[dict]) -> dict[str, str]:
    result: dict[str, str] = {}
    for c in connections:
        a = str(c.get("from") or "").strip()
        b = str(c.get("to") or "").strip()
        if not a or not b:
            continue
        result[_pair_key(a, b)] = str(c.get("relation") or "")
    return result


def _pair_key(a: str, b: str) -> str:
    left, right = sorted([a.strip().lower(), b.strip().lower()])
    return f"{left}|{right}"


def _allows_contact(relation: str) -> bool:
    rel = (relation or "").lower()
    return any(token in rel for token in ["mesh", "mate", "bearing", "house", "retained", "hinge"])


def _infer_motion(connection: dict) -> dict:
    relation = str(connection.get("relation") or "").lower()
    motion = connection.get("motion") or {}
    motion_type = str(motion.get("type") or "").lower().strip()

    if not motion_type:
        if any(token in relation for token in ["rotate", "hinge", "mesh", "gear"]):
            motion_type = "rotational"
        elif any(token in relation for token in ["slide", "linear", "translate"]):
            motion_type = "linear"
        else:
            motion_type = "none"

    axis = _normalize_axis(motion.get("axis"), default=[0.0, 0.0, 1.0] if motion_type == "rotational" else [1.0, 0.0, 0.0])
    range_deg = float(motion.get("rangeDeg") or (360.0 if "gear" in relation else 90.0))
    range_mm = float(motion.get("rangeMm") or 10.0)
    steps = int(np.clip(int(motion.get("steps") or 9), 3, 31))
    pivot = _normalize_optional_vector(motion.get("pivotMm"))
    moving_part = str(motion.get("movingPart") or connection.get("to") or "").strip()

    return {
        "type": motion_type,
        "axis": axis,
        "range_deg": range_deg,
        "range_mm": range_mm,
        "steps": steps,
        "pivot": pivot,
        "moving_part": moving_part,
    }


def _sample_motion_values(motion: dict) -> np.ndarray:
    steps = int(motion["steps"])
    if motion["type"] == "rotational":
        half = float(motion["range_deg"]) / 2.0
    elif motion["type"] == "linear":
        half = float(motion["range_mm"]) / 2.0
    else:
        return np.zeros(steps)
    return np.linspace(-half, half, steps)


def _motion_transform(motion: dict, parameter: float, base_centroid: np.ndarray) -> np.ndarray:
    axis = np.asarray(motion["axis"], dtype=float)
    axis = axis / (np.linalg.norm(axis) + 1e-12)
    pivot = np.asarray(motion["pivot"], dtype=float) if motion["pivot"] is not None else base_centroid

    if motion["type"] == "rotational":
        angle_rad = np.deg2rad(float(parameter))
        return trimesh.transformations.rotation_matrix(angle_rad, axis, point=pivot)

    if motion["type"] == "linear":
        delta = axis * float(parameter)
        return trimesh.transformations.translation_matrix(delta)

    return np.eye(4)


def _normalize_axis(value, default: list[float]) -> list[float]:
    vector = _normalize_optional_vector(value)
    if vector is None:
        vector = np.asarray(default, dtype=float)
    norm = float(np.linalg.norm(vector))
    if norm < 1e-9:
        vector = np.asarray(default, dtype=float)
        norm = float(np.linalg.norm(vector))
    return (vector / norm).tolist()


def _normalize_optional_vector(value) -> Optional[np.ndarray]:
    if value is None:
        return None
    if not isinstance(value, (list, tuple)) or len(value) != 3:
        return None
    try:
        return np.asarray([float(value[0]), float(value[1]), float(value[2])], dtype=float)
    except Exception:
        return None


def _aabb_overlap_depth(bounds_a: np.ndarray, bounds_b: np.ndarray) -> np.ndarray:
    mins = np.maximum(bounds_a[0], bounds_b[0])
    maxs = np.minimum(bounds_a[1], bounds_b[1])
    return np.maximum(0.0, maxs - mins)


def _aabb_gap_distance(bounds_a: np.ndarray, bounds_b: np.ndarray) -> float:
    axis_gap = np.maximum(
        np.maximum(bounds_a[0] - bounds_b[1], bounds_b[0] - bounds_a[1]),
        0.0,
    )
    return float(np.linalg.norm(axis_gap))


def _effective_sample_points(base_samples: int, part_count: int) -> int:
    count = max(2, int(part_count))
    if count <= 6:
        scale = 1.0
    elif count <= 10:
        scale = 0.75
    elif count <= 16:
        scale = 0.55
    else:
        scale = 0.4
    return int(np.clip(round(base_samples * scale), 64, 2048))


def _estimate_motion_span_mm(mesh: trimesh.Trimesh, motion: dict) -> float:
    if motion["type"] == "linear":
        return abs(float(motion["range_mm"]))

    if motion["type"] == "rotational":
        radius = float(np.linalg.norm(mesh.extents) * 0.5)
        angle_rad = np.deg2rad(abs(float(motion["range_deg"])))
        return radius * angle_rad

    return 0.0


def _detect_interference(
    mesh_a: trimesh.Trimesh,
    mesh_b: trimesh.Trimesh,
    overlap_tolerance_mm: float,
) -> tuple[bool, float, Optional[int]]:
    overlap = _aabb_overlap_depth(mesh_a.bounds, mesh_b.bounds)
    min_overlap = float(np.min(overlap))
    if min_overlap <= overlap_tolerance_mm:
        return False, min_overlap, 0

    voxel_pitch = float(np.clip(max(0.5, overlap_tolerance_mm * 2.0), 0.5, 2.5))
    voxel_overlap = _voxel_intersection_count(mesh_a, mesh_b, voxel_pitch)
    if voxel_overlap is None:
        # Conservative fallback if voxel check is unavailable or too expensive.
        return True, min_overlap, None
    return voxel_overlap > 0, min_overlap, voxel_overlap


def _voxel_intersection_count(
    mesh_a: trimesh.Trimesh,
    mesh_b: trimesh.Trimesh,
    pitch: float,
    max_voxels: int = 120_000,
) -> Optional[int]:
    try:
        vox_a = mesh_a.voxelized(pitch).fill()
        vox_b = mesh_b.voxelized(pitch).fill()
        points_a = np.round(np.asarray(vox_a.points) / pitch).astype(np.int32)
        points_b = np.round(np.asarray(vox_b.points) / pitch).astype(np.int32)

        if len(points_a) == 0 or len(points_b) == 0:
            return 0

        if len(points_a) > max_voxels:
            idx_a = np.linspace(0, len(points_a) - 1, num=max_voxels, dtype=np.int64)
            points_a = points_a[idx_a]
        if len(points_b) > max_voxels:
            idx_b = np.linspace(0, len(points_b) - 1, num=max_voxels, dtype=np.int64)
            points_b = points_b[idx_b]

        set_a = {tuple(v) for v in points_a.tolist()}
        set_b = {tuple(v) for v in points_b.tolist()}
        return int(len(set_a.intersection(set_b)))
    except Exception:
        return None


def _estimate_min_clearance_mm(
    mesh_a: trimesh.Trimesh,
    mesh_b: trimesh.Trimesh,
    sample_points: int,
) -> float:
    points_a = _sample_vertices(np.asarray(mesh_a.vertices), sample_points)
    points_b = _sample_vertices(np.asarray(mesh_b.vertices), sample_points)
    if len(points_a) == 0 or len(points_b) == 0:
        return float("inf")

    d_ab = _min_pairwise_distance(points_a, points_b)
    d_ba = _min_pairwise_distance(points_b, points_a)
    return float(min(d_ab, d_ba))


def _sample_vertices(vertices: np.ndarray, limit: int) -> np.ndarray:
    if len(vertices) <= limit:
        return vertices
    idx = np.linspace(0, len(vertices) - 1, num=limit, dtype=np.int64)
    return vertices[idx]


def _min_pairwise_distance(points_a: np.ndarray, points_b: np.ndarray) -> float:
    best_sq = np.inf
    chunk = 64
    for start in range(0, len(points_a), chunk):
        block = points_a[start:start + chunk]
        deltas = block[:, None, :] - points_b[None, :, :]
        dist_sq = np.sum(deltas * deltas, axis=2)
        local_best = float(np.min(dist_sq))
        if local_best < best_sq:
            best_sq = local_best
    return float(np.sqrt(best_sq))


def _fmt(value: float) -> str:
    return f"{value:.3f}"


def _float_or_none(value: float) -> Optional[float]:
    if value is None or not np.isfinite(value):
        return None
    return float(value)
