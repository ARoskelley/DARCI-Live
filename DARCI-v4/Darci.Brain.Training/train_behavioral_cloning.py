#!/usr/bin/env python3
"""
DARCI v4 — Behavioral Cloning Trainer
======================================
Phase 2, Step 1: Train a neural network to replicate the v3 priority-ladder
decisions by learning from the decision_log table in SQLite.

Produces two artifacts:
  1. darci_teacher.onnx   — the frozen teacher policy (used by C# ONNX Runtime
                             and as the KL anchor during DQN fine-tuning)
  2. darci_teacher.pt     — PyTorch checkpoint (used by train_dqn.py)

Usage:
  python train_behavioral_cloning.py --db path/to/darci.db
  python train_behavioral_cloning.py --db darci.db --epochs 200 --lr 1e-3

Requirements:
  pip install torch numpy onnx onnxruntime
  (sqlite3 is in Python stdlib)
"""

import argparse
import json
import sqlite3
from pathlib import Path

import numpy as np
import torch
import torch.nn as nn
import torch.optim as optim
from torch.utils.data import DataLoader, TensorDataset, random_split

# ============================================================
# Network Architecture — matches ARCHITECTURE.md §6.1 exactly
# 28 → 64 (ReLU) → 32 (ReLU) → 10 (logits)
# ~4,160 parameters
# ============================================================

class DarciDecisionNetwork(nn.Module):
    """
    DARCI's executive cortex.
    
    Input:  float[28] state vector (normalized, from StateEncoder)
    Output: float[10] action logits (one per BrainAction)
    """
    
    STATE_DIM = 28
    ACTION_DIM = 10
    
    def __init__(self):
        super().__init__()
        self.net = nn.Sequential(
            nn.Linear(self.STATE_DIM, 64),
            nn.ReLU(),
            nn.Linear(64, 32),
            nn.ReLU(),
            nn.Linear(32, self.ACTION_DIM),
        )
    
    def forward(self, x: torch.Tensor) -> torch.Tensor:
        return self.net(x)


# ============================================================
# Data Loading — reads from the decision_log SQLite table
# ============================================================

def load_decision_log(db_path: str) -> tuple[np.ndarray, np.ndarray]:
    """
    Load (state_vector, action_chosen) pairs from the decision_log table.
    
    Returns:
        states:  np.ndarray of shape (N, 28), dtype float32
        actions: np.ndarray of shape (N,),    dtype int64
    """
    conn = sqlite3.connect(db_path)
    cursor = conn.execute(
        "SELECT state_vector, action_chosen FROM decision_log ORDER BY id"
    )
    
    states = []
    actions = []
    skipped = 0
    
    for row in cursor:
        try:
            sv = json.loads(row[0])
            if len(sv) != DarciDecisionNetwork.STATE_DIM:
                skipped += 1
                continue
            states.append(sv)
            actions.append(row[1])
        except (json.JSONDecodeError, TypeError):
            skipped += 1
    
    conn.close()
    
    if skipped > 0:
        print(f"  ⚠ Skipped {skipped} malformed rows")
    
    return (
        np.array(states, dtype=np.float32),
        np.array(actions, dtype=np.int64),
    )


# ============================================================
# Training Loop
# ============================================================

def train(
    db_path: str,
    output_dir: str = ".",
    epochs: int = 150,
    lr: float = 3e-4,
    batch_size: int = 64,
    val_split: float = 0.15,
    seed: int = 42,
):
    torch.manual_seed(seed)
    np.random.seed(seed)
    
    output_dir = Path(output_dir)
    output_dir.mkdir(parents=True, exist_ok=True)
    
    # --- Load data ---
    print(f"Loading decision log from {db_path}...")
    states, actions = load_decision_log(db_path)
    n_samples = len(states)
    
    if n_samples < 50:
        print(f"✗ Only {n_samples} samples found. Need at least 50 for meaningful training.")
        print("  Run DARCI v4 longer to collect more decision data.")
        return
    
    print(f"  ✓ Loaded {n_samples} decision samples")
    
    # Action distribution (sanity check)
    unique, counts = np.unique(actions, return_counts=True)
    print(f"  Action distribution:")
    action_names = [
        "Rest", "Reply", "Research", "CreateGoal", "WorkOnGoal",
        "StoreMemory", "RecallMemories", "Consolidate", "Notify", "Think"
    ]
    for a, c in zip(unique, counts):
        pct = c / n_samples * 100
        name = action_names[a] if a < len(action_names) else f"Unknown({a})"
        print(f"    {a} ({name}): {c} ({pct:.1f}%)")
    
    # --- Build dataset ---
    dataset = TensorDataset(
        torch.from_numpy(states),
        torch.from_numpy(actions),
    )
    
    val_size = max(1, int(n_samples * val_split))
    train_size = n_samples - val_size
    train_ds, val_ds = random_split(dataset, [train_size, val_size])
    
    train_loader = DataLoader(train_ds, batch_size=batch_size, shuffle=True, drop_last=False)
    val_loader = DataLoader(val_ds, batch_size=batch_size, shuffle=False)
    
    print(f"  Train: {train_size}, Val: {val_size}")
    
    # --- Model, optimizer, loss ---
    model = DarciDecisionNetwork()
    optimizer = optim.Adam(model.parameters(), lr=lr, weight_decay=1e-5)
    
    # Class weights to handle imbalanced action distribution
    # (DARCI probably rests a lot more than she researches)
    class_counts = np.bincount(actions, minlength=DarciDecisionNetwork.ACTION_DIM).astype(np.float32)
    class_counts = np.maximum(class_counts, 1.0)  # avoid div by zero
    class_weights = 1.0 / class_counts
    class_weights = class_weights / class_weights.sum() * DarciDecisionNetwork.ACTION_DIM
    loss_fn = nn.CrossEntropyLoss(weight=torch.from_numpy(class_weights))
    
    # Learning rate scheduler — reduce on plateau
    scheduler = optim.lr_scheduler.ReduceLROnPlateau(
        optimizer, mode="max", factor=0.5, patience=15
    )
    
    print(f"\nTraining for {epochs} epochs (lr={lr}, batch={batch_size})...")
    print("-" * 60)
    
    best_val_acc = 0.0
    best_epoch = 0
    patience_counter = 0
    early_stop_patience = 30
    
    for epoch in range(1, epochs + 1):
        # --- Train ---
        model.train()
        train_loss = 0.0
        train_correct = 0
        train_total = 0
        
        for batch_states, batch_actions in train_loader:
            optimizer.zero_grad()
            logits = model(batch_states)
            loss = loss_fn(logits, batch_actions)
            loss.backward()
            optimizer.step()
            
            train_loss += loss.item() * len(batch_states)
            train_correct += (logits.argmax(dim=1) == batch_actions).sum().item()
            train_total += len(batch_states)
        
        # --- Validate ---
        model.eval()
        val_correct = 0
        val_total = 0
        
        with torch.no_grad():
            for batch_states, batch_actions in val_loader:
                logits = model(batch_states)
                val_correct += (logits.argmax(dim=1) == batch_actions).sum().item()
                val_total += len(batch_states)
        
        train_acc = train_correct / train_total * 100
        val_acc = val_correct / max(val_total, 1) * 100
        avg_loss = train_loss / train_total
        
        scheduler.step(val_acc)
        
        # Print progress every 10 epochs or on improvement
        if epoch % 10 == 0 or val_acc > best_val_acc:
            marker = " ★" if val_acc > best_val_acc else ""
            print(f"  Epoch {epoch:4d} | Loss: {avg_loss:.4f} | "
                  f"Train: {train_acc:.1f}% | Val: {val_acc:.1f}%{marker}")
        
        # Track best
        if val_acc > best_val_acc:
            best_val_acc = val_acc
            best_epoch = epoch
            patience_counter = 0
            # Save best checkpoint
            torch.save({
                "epoch": epoch,
                "model_state_dict": model.state_dict(),
                "optimizer_state_dict": optimizer.state_dict(),
                "val_acc": val_acc,
                "n_samples": n_samples,
                "class_weights": class_weights,
            }, output_dir / "darci_teacher.pt")
        else:
            patience_counter += 1
        
        if patience_counter >= early_stop_patience:
            print(f"\n  Early stopping at epoch {epoch} (no improvement for {early_stop_patience} epochs)")
            break
    
    print("-" * 60)
    print(f"Best validation accuracy: {best_val_acc:.1f}% at epoch {best_epoch}")
    
    if best_val_acc < 80:
        print(f"⚠ Accuracy below 80%. The network may need more training data or")
        print(f"  the v3 priority ladder may have inconsistent patterns.")
        print(f"  Consider collecting more diverse decision samples.")
    elif best_val_acc >= 95:
        print(f"✓ Excellent — network closely replicates the v3 teacher.")
    else:
        print(f"✓ Good — network captures the primary decision patterns.")
    
    # --- Export to ONNX ---
    print(f"\nExporting to ONNX...")
    
    # Reload best checkpoint
    checkpoint = torch.load(output_dir / "darci_teacher.pt", weights_only=False)
    model.load_state_dict(checkpoint["model_state_dict"])
    model.eval()
    
    dummy_input = torch.randn(1, DarciDecisionNetwork.STATE_DIM)
    onnx_path = output_dir / "darci_teacher.onnx"
    
    torch.onnx.export(
        model,
        dummy_input,
        str(onnx_path),
        input_names=["state_vector"],
        output_names=["action_logits"],
        dynamic_axes={
            "state_vector": {0: "batch_size"},
            "action_logits": {0: "batch_size"},
        },
        opset_version=17,
    )
    
    # Validate ONNX
    import onnx
    onnx_model = onnx.load(str(onnx_path))
    onnx.checker.check_model(onnx_model)
    
    # Quick inference test with onnxruntime
    import onnxruntime as ort
    session = ort.InferenceSession(str(onnx_path))
    test_input = np.random.randn(1, DarciDecisionNetwork.STATE_DIM).astype(np.float32)
    result = session.run(None, {"state_vector": test_input})
    
    print(f"  ✓ ONNX export validated")
    print(f"  ✓ Test inference shape: {result[0].shape}")
    print(f"\nOutputs:")
    print(f"  {onnx_path}              — for C# ONNX Runtime")
    print(f"  {output_dir / 'darci_teacher.pt'}  — for DQN training")


# ============================================================
# Entry point
# ============================================================

if __name__ == "__main__":
    parser = argparse.ArgumentParser(
        description="DARCI v4 — Behavioral Cloning Trainer"
    )
    parser.add_argument(
        "--db", required=True,
        help="Path to DARCI's SQLite database (contains decision_log table)"
    )
    parser.add_argument(
        "--output", default="./models",
        help="Directory for output files (default: ./models)"
    )
    parser.add_argument("--epochs", type=int, default=150)
    parser.add_argument("--lr", type=float, default=3e-4)
    parser.add_argument("--batch-size", type=int, default=64)
    parser.add_argument("--seed", type=int, default=42)
    
    args = parser.parse_args()
    
    train(
        db_path=args.db,
        output_dir=args.output,
        epochs=args.epochs,
        lr=args.lr,
        batch_size=args.batch_size,
        seed=args.seed,
    )
