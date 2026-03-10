#!/usr/bin/env python3
"""
DARCI v4 — Geometry Behavioral Cloning
========================================
Creates a teacher policy from replayed CadQuery operations.

Cold-start approach: take known-good operations (fillet a box, shell a part,
drill holes in a bracket) and record the state→action→params transitions.
Train the network to imitate these "expert demonstrations."

This teacher is then used as:
  1. The starting weights for SAC training (warm start)
  2. A KL anchor to prevent the SAC policy from diverging too wildly

Usage:
  # Generate demonstrations first
  python train_geometry_bc.py generate --output demos/

  # Train from demonstrations
  python train_geometry_bc.py train --demos demos/ --output models/

Requirements:
  pip install torch numpy cadquery trimesh
"""

import argparse
import json
import os
import sys
from pathlib import Path

import numpy as np
import torch
import torch.nn as nn
import torch.optim as optim
from torch.utils.data import DataLoader, TensorDataset, random_split

# Add workbench to path
WORKBENCH_DIR = os.path.join(os.path.dirname(__file__), "..", "Darci.Engineering.Workbench")
sys.path.insert(0, WORKBENCH_DIR)

from workbench.engine import GeometryEngine
from geometry_network import (
    GeometryActor, STATE_DIM, ACTION_DIM, PARAM_DIM,
)

import cadquery as cq


# ============================================================
# Demonstration Generation
# ============================================================

# Expert demonstrations: sequences of (description, action_id, raw_params)
# that produce known-good results. The state vector is captured from the
# engine before each action.

EXPERT_SEQUENCES = [
    # --- Filleted Box ---
    {
        "name": "filleted_box_basic",
        "steps": [
            ("Create box", 4, [0.0, 0.0, 0.0, 0.5, 0.3, 0.0]),
            ("Fillet all edges", 5, [0.3, 0.0, 0.0, 0.0, 0.0, 0.0]),
            ("Validate", 18, [0.0]*6),
            ("Finalize", 19, [0.0]*6),
        ],
    },
    # --- Bracket with holes ---
    {
        "name": "bracket_with_holes",
        "steps": [
            ("Create plate", 4, [0.0, 0.0, 0.0, 0.8, 0.5, -0.6]),
            ("Add hole left", 8, [-0.4, 0.0, 0.0, 0.3, 0.5, 0.0]),
            ("Add hole right", 8, [0.4, 0.0, 0.0, 0.3, 0.5, 0.0]),
            ("Fillet edges", 5, [0.1, 0.0, 0.0, 0.0, 0.0, 0.0]),
            ("Validate", 18, [0.0]*6),
            ("Finalize", 19, [0.0]*6),
        ],
    },
    # --- Shelled box ---
    {
        "name": "shelled_container",
        "steps": [
            ("Create box", 4, [0.0, 0.0, 0.0, 0.5, 0.5, 0.5]),
            ("Shell it", 7, [0.2, 0.0, 0.0, 0.0, 0.0, 0.0]),
            ("Validate", 18, [0.0]*6),
            ("Finalize", 19, [0.0]*6),
        ],
    },
    # --- Cylinder with boss ---
    {
        "name": "cylinder_with_boss",
        "steps": [
            ("Create cylinder", 3, [0.0, 0.0, 0.0, 0.5, 0.6, 0.0]),
            ("Add boss on top", 9, [0.0, 0.0, 0.0, 0.2, 0.3, 0.0]),
            ("Validate", 18, [0.0]*6),
            ("Finalize", 19, [0.0]*6),
        ],
    },
    # --- Box with cut ---
    {
        "name": "box_with_notch",
        "steps": [
            ("Create box", 4, [0.0, 0.0, 0.0, 0.6, 0.4, 0.2]),
            ("Cut notch", 1, [0.3, 0.0, 0.0, 0.3, 0.2, 0.4]),
            ("Fillet edges", 5, [0.1, 0.0, 0.0, 0.0, 0.0, 0.0]),
            ("Validate", 18, [0.0]*6),
            ("Finalize", 19, [0.0]*6),
        ],
    },
    # --- Plate with rib ---
    {
        "name": "reinforced_plate",
        "steps": [
            ("Create plate", 4, [0.0, 0.0, 0.0, 0.8, 0.6, -0.7]),
            ("Add rib", 10, [0.0, 0.0, 0.0, 0.0, 0.1, 0.3]),
            ("Chamfer edges", 6, [0.05, 0.0, 0.0, 0.0, 0.0, 0.0]),
            ("Validate", 18, [0.0]*6),
            ("Finalize", 19, [0.0]*6),
        ],
    },
    # --- Mirrored box ---
    {
        "name": "symmetric_part",
        "steps": [
            ("Create half", 4, [0.3, 0.0, 0.0, 0.3, 0.4, 0.3]),
            ("Mirror across YZ", 13, [1.0, 0.0, 0.0, 0.0, 0.0, 0.0]),
            ("Fillet", 5, [0.15, 0.0, 0.0, 0.0, 0.0, 0.0]),
            ("Validate", 18, [0.0]*6),
            ("Finalize", 19, [0.0]*6),
        ],
    },
    # --- Stepped cylinder ---
    {
        "name": "stepped_shaft",
        "steps": [
            ("Create base cylinder", 3, [0.0, 0.0, 0.0, 0.5, 0.4, 0.0]),
            ("Add smaller top", 3, [0.0, 0.0, 0.5, 0.3, 0.3, 0.0]),
            ("Chamfer transition", 6, [0.1, 0.0, 0.0, 0.0, 0.0, 0.0]),
            ("Validate", 18, [0.0]*6),
            ("Finalize", 19, [0.0]*6),
        ],
    },
    # --- Rest when nothing to do (idle behavior) ---
    # The network should learn that validate/finalize are appropriate endpoints
]


def generate_demonstrations(output_dir: str, n_variations: int = 20, seed: int = 42):
    """
    Generate demonstration data by replaying expert sequences with variations.

    For each expert sequence, create N variations by slightly randomizing
    the parameters. Record (state, action, params) at each step.
    """
    output_dir = Path(output_dir)
    output_dir.mkdir(parents=True, exist_ok=True)
    rng = np.random.RandomState(seed)

    engine = GeometryEngine()

    all_states = []
    all_actions = []
    all_params = []

    total_demos = 0
    total_steps = 0

    for seq in EXPERT_SEQUENCES:
        for var in range(n_variations):
            engine.reset()

            for desc, action_id, base_params in seq["steps"]:
                # Add small random variation to parameters
                noise = rng.normal(0, 0.05, size=6).astype(np.float32)
                varied_params = np.clip(
                    np.array(base_params, dtype=np.float32) + noise, -1.0, 1.0
                )

                # Record the state BEFORE the action
                state = engine.get_state()

                # Execute
                result = engine.execute_action(action_id, varied_params)

                # Only record if the action was valid
                if result["success"] or action_id in (18, 19):
                    all_states.append(state)
                    all_actions.append(action_id)
                    all_params.append(varied_params)
                    total_steps += 1

            total_demos += 1

    # Save as numpy arrays
    states = np.array(all_states, dtype=np.float32)
    actions = np.array(all_actions, dtype=np.int64)
    params = np.array(all_params, dtype=np.float32)

    np.save(str(output_dir / "demo_states.npy"), states)
    np.save(str(output_dir / "demo_actions.npy"), actions)
    np.save(str(output_dir / "demo_params.npy"), params)

    print(f"  ✓ Generated {total_demos} demonstrations ({total_steps} steps)")
    print(f"  States: {states.shape}")
    print(f"  Actions: {actions.shape}")
    print(f"  Params: {params.shape}")

    # Action distribution
    unique, counts = np.unique(actions, return_counts=True)
    action_names = [
        "extrude", "cut", "revolve", "add_cyl", "add_box",
        "fillet", "chamfer", "shell", "hole", "boss",
        "rib", "translate", "scale", "mirror", "pattern",
        "thicken", "smooth", "remove", "validate", "finalize",
    ]
    print(f"  Action distribution:")
    for a, c in zip(unique, counts):
        name = action_names[a] if a < len(action_names) else f"?{a}"
        print(f"    {a:2d} ({name:10s}): {c:4d} ({c/len(actions)*100:.1f}%)")

    return states, actions, params


# ============================================================
# Training
# ============================================================

def train_bc(
    demos_dir: str,
    output_dir: str = "./models",
    epochs: int = 200,
    lr: float = 3e-4,
    batch_size: int = 64,
    seed: int = 42,
):
    """Train the actor network via behavioral cloning."""
    torch.manual_seed(seed)
    np.random.seed(seed)

    output_dir = Path(output_dir)
    output_dir.mkdir(parents=True, exist_ok=True)
    demos_dir = Path(demos_dir)

    # Load demonstration data
    print("Loading demonstrations...")
    states = np.load(str(demos_dir / "demo_states.npy"))
    actions = np.load(str(demos_dir / "demo_actions.npy"))
    params = np.load(str(demos_dir / "demo_params.npy"))

    n_samples = len(states)
    print(f"  ✓ Loaded {n_samples} demonstration steps")

    if n_samples < 50:
        print("✗ Too few demonstrations. Generate more with 'generate' command.")
        return

    # Build dataset
    states_t = torch.FloatTensor(states)
    actions_t = torch.LongTensor(actions)
    params_t = torch.FloatTensor(params)

    dataset = TensorDataset(states_t, actions_t, params_t)
    val_size = max(1, int(n_samples * 0.15))
    train_size = n_samples - val_size
    train_ds, val_ds = random_split(dataset, [train_size, val_size])

    train_loader = DataLoader(train_ds, batch_size=batch_size, shuffle=True)
    val_loader = DataLoader(val_ds, batch_size=batch_size, shuffle=False)

    # Model
    actor = GeometryActor(STATE_DIM)
    optimizer = optim.Adam(actor.parameters(), lr=lr, weight_decay=1e-5)

    # Losses
    action_loss_fn = nn.CrossEntropyLoss()
    param_loss_fn = nn.MSELoss()

    # Class weights for imbalanced actions
    class_counts = np.bincount(actions, minlength=ACTION_DIM).astype(np.float32)
    class_counts = np.maximum(class_counts, 1.0)
    class_weights = 1.0 / class_counts
    class_weights = class_weights / class_weights.sum() * ACTION_DIM
    action_loss_fn = nn.CrossEntropyLoss(weight=torch.FloatTensor(class_weights))

    scheduler = optim.lr_scheduler.ReduceLROnPlateau(
        optimizer, mode="min", factor=0.5, patience=20
    )

    print(f"\nTraining for {epochs} epochs...")
    print(f"  Parameters: {sum(p.numel() for p in actor.parameters()):,}")
    print("-" * 60)

    best_val_loss = float("inf")
    best_epoch = 0

    for epoch in range(1, epochs + 1):
        actor.train()
        train_action_loss = 0.0
        train_param_loss = 0.0
        train_correct = 0
        train_total = 0

        for batch_s, batch_a, batch_p in train_loader:
            optimizer.zero_grad()

            logits, all_params = actor(batch_s)

            # Action classification loss
            a_loss = action_loss_fn(logits, batch_a)

            # Parameter regression loss (only for the chosen action)
            batch_size_actual = batch_s.shape[0]
            predicted_params = all_params[torch.arange(batch_size_actual), batch_a]
            p_loss = param_loss_fn(predicted_params, batch_p)

            # Combined loss (action classification is primary)
            loss = a_loss + 0.5 * p_loss
            loss.backward()
            torch.nn.utils.clip_grad_norm_(actor.parameters(), 1.0)
            optimizer.step()

            train_action_loss += a_loss.item() * batch_size_actual
            train_param_loss += p_loss.item() * batch_size_actual
            train_correct += (logits.argmax(dim=1) == batch_a).sum().item()
            train_total += batch_size_actual

        # Validate
        actor.eval()
        val_action_loss = 0.0
        val_param_loss = 0.0
        val_correct = 0
        val_total = 0

        with torch.no_grad():
            for batch_s, batch_a, batch_p in val_loader:
                logits, all_params = actor(batch_s)
                a_loss = action_loss_fn(logits, batch_a)

                batch_size_actual = batch_s.shape[0]
                predicted_params = all_params[torch.arange(batch_size_actual), batch_a]
                p_loss = param_loss_fn(predicted_params, batch_p)

                val_action_loss += a_loss.item() * batch_size_actual
                val_param_loss += p_loss.item() * batch_size_actual
                val_correct += (logits.argmax(dim=1) == batch_a).sum().item()
                val_total += batch_size_actual

        train_acc = train_correct / train_total * 100
        val_acc = val_correct / max(val_total, 1) * 100
        avg_val_loss = (val_action_loss + val_param_loss) / max(val_total, 1)

        scheduler.step(avg_val_loss)

        if epoch % 10 == 0 or avg_val_loss < best_val_loss:
            marker = " ★" if avg_val_loss < best_val_loss else ""
            print(
                f"  Epoch {epoch:4d} | "
                f"Act Loss: {train_action_loss/train_total:.4f} | "
                f"Param Loss: {train_param_loss/train_total:.4f} | "
                f"Train Acc: {train_acc:.1f}% | "
                f"Val Acc: {val_acc:.1f}%"
                f"{marker}"
            )

        if avg_val_loss < best_val_loss:
            best_val_loss = avg_val_loss
            best_epoch = epoch
            torch.save({
                "actor_state_dict": actor.state_dict(),
                "epoch": epoch,
                "val_acc": val_acc,
                "val_loss": avg_val_loss,
            }, output_dir / "geometry_teacher.pt")

    print("-" * 60)
    print(f"Best validation loss: {best_val_loss:.4f} at epoch {best_epoch}")

    # Export ONNX
    print("\nExporting ONNX...")
    checkpoint = torch.load(output_dir / "geometry_teacher.pt", weights_only=False)
    actor.load_state_dict(checkpoint["actor_state_dict"])

    # Use the SAC agent's export method pattern
    from geometry_network import GeometrySACAgent
    dummy_agent = GeometrySACAgent()
    dummy_agent.actor.load_state_dict(actor.state_dict())
    dummy_agent.export_actor_onnx(str(output_dir / "geometry_teacher.onnx"))

    print(f"\nOutputs:")
    print(f"  {output_dir / 'geometry_teacher.onnx'}  — for C# ONNX Runtime")
    print(f"  {output_dir / 'geometry_teacher.pt'}    — for SAC training (KL anchor)")


# ============================================================
# Entry Point
# ============================================================

if __name__ == "__main__":
    parser = argparse.ArgumentParser(
        description="DARCI v4 — Geometry Behavioral Cloning"
    )
    sub = parser.add_subparsers(dest="command")

    gen_parser = sub.add_parser("generate", help="Generate expert demonstrations")
    gen_parser.add_argument("--output", default="./demos", help="Output directory")
    gen_parser.add_argument("--variations", type=int, default=20, help="Variations per sequence")
    gen_parser.add_argument("--seed", type=int, default=42)

    train_parser = sub.add_parser("train", help="Train from demonstrations")
    train_parser.add_argument("--demos", required=True, help="Demonstrations directory")
    train_parser.add_argument("--output", default="./models", help="Output directory")
    train_parser.add_argument("--epochs", type=int, default=200)
    train_parser.add_argument("--lr", type=float, default=3e-4)
    train_parser.add_argument("--seed", type=int, default=42)

    args = parser.parse_args()

    if args.command == "generate":
        generate_demonstrations(args.output, args.variations, args.seed)
    elif args.command == "train":
        train_bc(args.demos, args.output, args.epochs, args.lr, args.seed)
    else:
        parser.print_help()
