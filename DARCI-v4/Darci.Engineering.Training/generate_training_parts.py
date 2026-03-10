#!/usr/bin/env python3
"""
DARCI v4 — Geometry Training Scenario Generator
=================================================
Creates diverse training situations for the geometry workbench network.

Three scenario types:
  1. Parametric Perturbation: Take a finished part, randomly damage it,
     train the network to fix it. The answer is known (the original part).
  2. Constrained Design: Start from a primitive, satisfy engineering specs.
  3. Shape Matching: Start from one shape, modify to match a reference.

Usage:
  python generate_training_parts.py --output scenarios/ --count 200
  python generate_training_parts.py --output scenarios/ --type perturbation --count 100

Requirements:
  pip install cadquery trimesh numpy
"""

import argparse
import json
import os
import sys
from pathlib import Path
from dataclasses import dataclass, field
from typing import Optional

import numpy as np

# Add workbench to path so we can import the engine directly
WORKBENCH_DIR = os.path.join(os.path.dirname(__file__), "..", "Darci.Engineering.Workbench")
sys.path.insert(0, WORKBENCH_DIR)

import cadquery as cq
import trimesh


# ============================================================
# Scenario Data Structures
# ============================================================

@dataclass
class TrainingScenario:
    """One training episode configuration."""
    scenario_id: str
    scenario_type: str               # "perturbation", "constrained", "matching"
    description: str
    start_geometry: Optional[str]     # CadQuery code to create starting shape
    reference_stl: Optional[str]      # Path to reference STL (for matching/perturbation)
    constraints: dict = field(default_factory=dict)
    targets: dict = field(default_factory=dict)
    max_steps: int = 50
    difficulty: str = "medium"        # "easy", "medium", "hard"

    def to_dict(self) -> dict:
        return {
            "scenario_id": self.scenario_id,
            "scenario_type": self.scenario_type,
            "description": self.description,
            "start_geometry": self.start_geometry,
            "reference_stl": self.reference_stl,
            "constraints": self.constraints,
            "targets": self.targets,
            "max_steps": self.max_steps,
            "difficulty": self.difficulty,
        }


# ============================================================
# Part Generators — create CadQuery geometry programmatically
# ============================================================

def make_box(length=20, width=15, height=10):
    return cq.Workplane("XY").box(length, width, height)

def make_cylinder(radius=8, height=20):
    return cq.Workplane("XY").cylinder(height, radius)

def make_filleted_box(length=20, width=15, height=10, fillet=2):
    return cq.Workplane("XY").box(length, width, height).edges().fillet(fillet)

def make_bracket(length=30, width=20, height=5, hole_d=5):
    wp = cq.Workplane("XY").box(length, width, height)
    wp = wp.faces(">Z").workplane().pushPoints([
        (-length/4, 0), (length/4, 0)
    ]).hole(hole_d)
    return wp

def make_shell_box(length=20, width=15, height=15, wall=2):
    return cq.Workplane("XY").box(length, width, height).faces(">Z").shell(-wall)

def make_l_bracket(length=30, width=10, height=30, thickness=3):
    base = cq.Workplane("XY").box(length, width, thickness)
    wall = cq.Workplane("XY").box(thickness, width, height).translate(
        (-length/2 + thickness/2, 0, height/2 - thickness/2)
    )
    return base.union(wall)

def make_tube(outer_r=10, inner_r=7, height=25):
    outer = cq.Workplane("XY").cylinder(height, outer_r)
    inner = cq.Workplane("XY").cylinder(height + 1, inner_r)
    return outer.cut(inner)

def make_plate_with_holes(length=40, width=30, height=3, n_holes=4, hole_d=4):
    wp = cq.Workplane("XY").box(length, width, height)
    spacing_x = length * 0.3
    spacing_y = width * 0.3
    points = [
        (-spacing_x, -spacing_y), (spacing_x, -spacing_y),
        (-spacing_x, spacing_y), (spacing_x, spacing_y),
    ][:n_holes]
    wp = wp.faces(">Z").workplane().pushPoints(points).hole(hole_d)
    return wp

def make_stepped_cylinder(r1=10, h1=10, r2=6, h2=15):
    bottom = cq.Workplane("XY").cylinder(h1, r1)
    top = cq.Workplane("XY").cylinder(h2, r2).translate((0, 0, h1/2 + h2/2))
    return bottom.union(top)

def make_wedge(length=20, width=15, height=10, taper=0.5):
    pts = [
        (0, 0),
        (length, 0),
        (length * taper, height),
        (0, height),
    ]
    return cq.Workplane("XZ").polyline(pts).close().extrude(width).translate(
        (-length/2, -width/2, -height/2)
    )


# ============================================================
# Part Library — all available base geometries
# ============================================================

PART_LIBRARY = {
    "box": {"fn": make_box, "params": {"length": (10, 40), "width": (8, 30), "height": (5, 20)}},
    "cylinder": {"fn": make_cylinder, "params": {"radius": (4, 15), "height": (10, 40)}},
    "filleted_box": {"fn": make_filleted_box, "params": {"length": (15, 35), "width": (10, 25), "height": (8, 18), "fillet": (1, 4)}},
    "bracket": {"fn": make_bracket, "params": {"length": (20, 50), "width": (10, 25), "height": (3, 8), "hole_d": (3, 8)}},
    "shell_box": {"fn": make_shell_box, "params": {"length": (15, 30), "width": (10, 25), "height": (10, 25), "wall": (1, 3)}},
    "l_bracket": {"fn": make_l_bracket, "params": {"length": (20, 40), "width": (8, 15), "height": (20, 40), "thickness": (2, 5)}},
    "tube": {"fn": make_tube, "params": {"outer_r": (6, 15), "inner_r": (3, 12), "height": (15, 40)}},
    "plate_with_holes": {"fn": make_plate_with_holes, "params": {"length": (25, 50), "width": (20, 40), "height": (2, 5), "n_holes": (2, 4), "hole_d": (3, 6)}},
    "stepped_cylinder": {"fn": make_stepped_cylinder, "params": {"r1": (6, 15), "h1": (5, 15), "r2": (3, 10), "h2": (8, 20)}},
    "wedge": {"fn": make_wedge, "params": {"length": (15, 30), "width": (10, 25), "height": (8, 20), "taper": (0.3, 0.7)}},
}


def random_part(rng: np.random.RandomState) -> tuple:
    """Generate a random part from the library with randomized parameters."""
    name = rng.choice(list(PART_LIBRARY.keys()))
    spec = PART_LIBRARY[name]
    params = {}
    for param_name, (lo, hi) in spec["params"].items():
        if isinstance(lo, int) and isinstance(hi, int):
            params[param_name] = int(rng.randint(lo, hi + 1))
        else:
            params[param_name] = float(rng.uniform(lo, hi))

    # Special case: tube inner_r must be less than outer_r
    if name == "tube" and params.get("inner_r", 0) >= params.get("outer_r", 1):
        params["inner_r"] = params["outer_r"] * 0.7

    try:
        wp = spec["fn"](**params)
        return name, params, wp
    except Exception:
        # Fallback to simple box
        wp = make_box()
        return "box", {"length": 20, "width": 15, "height": 10}, wp


def workplane_to_stl(wp: cq.Workplane, path: str):
    """Export a CadQuery workplane to STL."""
    cq.exporters.export(wp, path)


def workplane_to_trimesh(wp: cq.Workplane) -> trimesh.Trimesh:
    """Convert CadQuery workplane to trimesh."""
    solid = wp.val()
    vertices, triangles = solid.tessellate(0.1)
    verts = np.array([(v.x, v.y, v.z) for v in vertices])
    faces = np.array([(t[0], t[1], t[2]) for t in triangles])
    return trimesh.Trimesh(vertices=verts, faces=faces, process=False)


# ============================================================
# Scenario Generators
# ============================================================

def generate_perturbation_scenarios(
    output_dir: Path, count: int, rng: np.random.RandomState
) -> list:
    """
    Parametric Perturbation: create a good part, damage it, train to fix it.

    The network starts with the damaged version and the original is the reference.
    Reward comes from reducing the Hausdorff distance to the reference.
    """
    ref_dir = output_dir / "references"
    ref_dir.mkdir(parents=True, exist_ok=True)

    scenarios = []
    for i in range(count):
        name, params, wp_good = random_part(rng)

        # Save the good part as reference
        ref_path = str(ref_dir / f"perturbation_{i:04d}_ref.stl")
        try:
            workplane_to_stl(wp_good, ref_path)
        except Exception:
            continue

        # Create a damaged version by applying random perturbations
        damage_type = rng.choice(["scale", "translate", "cut", "add_blob"])
        damage_desc = ""

        try:
            if damage_type == "scale":
                # Non-uniform scale (stretches the part)
                sx = rng.uniform(0.7, 1.3)
                sy = rng.uniform(0.7, 1.3)
                sz = rng.uniform(0.7, 1.3)
                solid = wp_good.val().scale(sx)  # CadQuery only does uniform
                wp_damaged = cq.Workplane("XY").newObject([solid])
                damage_desc = f"Uniformly scaled by {sx:.2f}"

            elif damage_type == "translate":
                dx = rng.uniform(-5, 5)
                dy = rng.uniform(-5, 5)
                dz = rng.uniform(-3, 3)
                wp_damaged = wp_good.translate((dx, dy, dz))
                damage_desc = f"Translated by ({dx:.1f}, {dy:.1f}, {dz:.1f})"

            elif damage_type == "cut":
                bbox = workplane_to_trimesh(wp_good).bounding_box.extents
                cut_size = float(max(bbox)) * rng.uniform(0.2, 0.4)
                cx = rng.uniform(-bbox[0]/3, bbox[0]/3)
                cy = rng.uniform(-bbox[1]/3, bbox[1]/3)
                cz = rng.uniform(-bbox[2]/3, bbox[2]/3)
                cutter = cq.Workplane("XY").box(cut_size, cut_size, cut_size).translate(
                    (cx, cy, cz)
                )
                wp_damaged = wp_good.cut(cutter)
                damage_desc = f"Cut with {cut_size:.1f}mm cube at ({cx:.1f},{cy:.1f},{cz:.1f})"

            else:  # add_blob
                bbox = workplane_to_trimesh(wp_good).bounding_box.extents
                blob_r = float(max(bbox)) * rng.uniform(0.1, 0.25)
                bx = rng.uniform(-bbox[0]/3, bbox[0]/3)
                by = rng.uniform(-bbox[1]/3, bbox[1]/3)
                bz = rng.uniform(-bbox[2]/3, bbox[2]/3)
                blob = cq.Workplane("XY").sphere(blob_r).translate((bx, by, bz))
                wp_damaged = wp_good.union(blob)
                damage_desc = f"Added sphere r={blob_r:.1f} at ({bx:.1f},{by:.1f},{bz:.1f})"

        except Exception:
            continue

        # Save damaged start as STL
        start_path = str(ref_dir / f"perturbation_{i:04d}_start.stl")
        try:
            workplane_to_stl(wp_damaged, start_path)
        except Exception:
            continue

        scenarios.append(TrainingScenario(
            scenario_id=f"perturbation_{i:04d}",
            scenario_type="perturbation",
            description=f"Fix {name} ({damage_desc}). Match reference shape.",
            start_geometry=f"load_stl('{start_path}')",
            reference_stl=ref_path,
            constraints={},
            targets={"hausdorff_target": 1.0},
            max_steps=40 + int(rng.uniform(0, 30)),
            difficulty=rng.choice(["easy", "medium", "hard"]),
        ))

    return scenarios


def generate_constrained_scenarios(
    output_dir: Path, count: int, rng: np.random.RandomState
) -> list:
    """
    Constrained Design: start from a primitive, satisfy engineering specs.

    No reference shape — success is measured by constraint satisfaction.
    """
    scenarios = []
    for i in range(count):
        # Random starting primitive
        start_type = rng.choice(["box", "cylinder"])
        if start_type == "box":
            sx = rng.uniform(15, 35)
            sy = rng.uniform(10, 25)
            sz = rng.uniform(5, 15)
            start_code = f"box({sx:.1f}, {sy:.1f}, {sz:.1f})"
        else:
            sr = rng.uniform(5, 15)
            sh = rng.uniform(10, 30)
            start_code = f"cylinder({sr:.1f}, {sh:.1f})"

        # Generate random engineering constraints
        constraints = {}
        n_constraints = rng.randint(2, 6)

        possible_constraints = [
            lambda: ("min_wall", {
                "type": "printability",
                "min_wall": float(rng.uniform(0.8, 2.0)),
            }),
            lambda: ("target_volume", {
                "type": "tolerance",
                "measurement": "volume",
                "target": float(rng.uniform(500, 5000)),
                "tolerance": float(rng.uniform(100, 500)),
            }),
            lambda: ("max_bbox_x", {
                "type": "dimensional",
                "measurement": "bbox_x",
                "min": 0,
                "max": float(rng.uniform(20, 50)),
            }),
            lambda: ("max_bbox_z", {
                "type": "dimensional",
                "measurement": "bbox_z",
                "min": float(rng.uniform(5, 10)),
                "max": float(rng.uniform(15, 30)),
            }),
            lambda: ("has_hole", {
                "type": "feature",
                "feature_type": "hole",
            }),
        ]

        chosen = rng.choice(len(possible_constraints), size=min(n_constraints, len(possible_constraints)), replace=False)
        for idx in chosen:
            name, spec = possible_constraints[idx]()
            constraints[name] = spec

        scenarios.append(TrainingScenario(
            scenario_id=f"constrained_{i:04d}",
            scenario_type="constrained",
            description=f"Modify {start_type} to satisfy {len(constraints)} engineering constraints.",
            start_geometry=start_code,
            reference_stl=None,
            constraints=constraints,
            targets={},
            max_steps=50 + int(rng.uniform(0, 50)),
            difficulty=rng.choice(["easy", "medium", "hard"]),
        ))

    return scenarios


def generate_matching_scenarios(
    output_dir: Path, count: int, rng: np.random.RandomState
) -> list:
    """
    Shape Matching: start from a simple primitive, modify to match a target.

    The target is a more complex part from the library. The start is always
    a simple box or cylinder. Reward is Hausdorff distance reduction.
    """
    ref_dir = output_dir / "references"
    ref_dir.mkdir(parents=True, exist_ok=True)

    scenarios = []
    for i in range(count):
        name, params, wp_target = random_part(rng)

        ref_path = str(ref_dir / f"matching_{i:04d}_ref.stl")
        try:
            workplane_to_stl(wp_target, ref_path)
        except Exception:
            continue

        # Start from a simple primitive that roughly matches the target's bbox
        mesh = workplane_to_trimesh(wp_target)
        bbox = mesh.bounding_box.extents
        start_code = f"box({bbox[0]:.1f}, {bbox[1]:.1f}, {bbox[2]:.1f})"

        scenarios.append(TrainingScenario(
            scenario_id=f"matching_{i:04d}",
            scenario_type="matching",
            description=f"Transform box into {name} with params {params}.",
            start_geometry=start_code,
            reference_stl=ref_path,
            constraints={},
            targets={"hausdorff_target": 2.0},
            max_steps=60 + int(rng.uniform(0, 40)),
            difficulty=rng.choice(["medium", "hard"]),
        ))

    return scenarios


# ============================================================
# Main Generator
# ============================================================

def generate_all(
    output_dir: str = "./scenarios",
    count: int = 200,
    scenario_type: str = "all",
    seed: int = 42,
):
    output_dir = Path(output_dir)
    output_dir.mkdir(parents=True, exist_ok=True)
    rng = np.random.RandomState(seed)

    all_scenarios = []

    if scenario_type in ("all", "perturbation"):
        n = count if scenario_type == "perturbation" else count // 3
        print(f"Generating {n} perturbation scenarios...")
        scenarios = generate_perturbation_scenarios(output_dir, n, rng)
        all_scenarios.extend(scenarios)
        print(f"  ✓ Generated {len(scenarios)} perturbation scenarios")

    if scenario_type in ("all", "constrained"):
        n = count if scenario_type == "constrained" else count // 3
        print(f"Generating {n} constrained design scenarios...")
        scenarios = generate_constrained_scenarios(output_dir, n, rng)
        all_scenarios.extend(scenarios)
        print(f"  ✓ Generated {len(scenarios)} constrained scenarios")

    if scenario_type in ("all", "matching"):
        n = count if scenario_type == "matching" else count // 3
        print(f"Generating {n} shape matching scenarios...")
        scenarios = generate_matching_scenarios(output_dir, n, rng)
        all_scenarios.extend(scenarios)
        print(f"  ✓ Generated {len(scenarios)} matching scenarios")

    # Save manifest
    manifest = [s.to_dict() for s in all_scenarios]
    manifest_path = output_dir / "manifest.json"
    with open(manifest_path, "w") as f:
        json.dump(manifest, f, indent=2)

    print(f"\n✓ Total: {len(all_scenarios)} scenarios")
    print(f"  Manifest: {manifest_path}")
    print(f"  References: {output_dir / 'references'}")

    return all_scenarios


if __name__ == "__main__":
    parser = argparse.ArgumentParser(
        description="DARCI v4 — Geometry Training Scenario Generator"
    )
    parser.add_argument("--output", default="./scenarios", help="Output directory")
    parser.add_argument("--count", type=int, default=200, help="Number of scenarios")
    parser.add_argument(
        "--type", default="all",
        choices=["all", "perturbation", "constrained", "matching"],
    )
    parser.add_argument("--seed", type=int, default=42)

    args = parser.parse_args()
    generate_all(args.output, args.count, args.type, args.seed)
