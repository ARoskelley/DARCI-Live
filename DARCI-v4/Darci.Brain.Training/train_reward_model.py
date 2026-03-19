#!/usr/bin/env python3
"""
DARCI v4 — Reward Model Trainer
=================================
Trains a small neural network to predict reward from (state, action) pairs.

Two training modes:
  1. Bootstrap mode:  Learn from the hand-coded reward table in ARCHITECTURE.md §5.1
                      as synthetic preference data. Gets the model started.
  2. Preference mode: Learn from human pairwise comparisons stored in the
                      preferences table. This is the PEBBLE/Christiano approach.

The reward model replaces hard-coded scalars during DQN training, solving
the reward calibration and reward hacking problems identified in the research.

Produces:
  reward_model.onnx — for C# runtime (scores actions during live operation)
  reward_model.pt   — for train_dqn.py (generates reward labels during training)

Usage:
  python train_reward_model.py --db darci.db --mode bootstrap
  python train_reward_model.py --db darci.db --mode preferences
  python train_reward_model.py --db darci.db --mode both

Requirements:
  pip install torch numpy onnx onnxruntime
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
# Reward Network Architecture
# Input: state(29) + action_one_hot(10) = 39 dimensions
# Output: scalar reward prediction
# ============================================================

class DarciRewardModel(nn.Module):
    """
    Predicts scalar reward for a (state, action) pair.
    
    Architecture is deliberately small — this model needs to generalize
    from limited preference data, not memorize it. Overfitting the reward
    model is how reward hacking starts.
    """
    
    STATE_DIM = 29
    ACTION_DIM = 10
    INPUT_DIM = STATE_DIM + ACTION_DIM  # 39
    
    def __init__(self):
        super().__init__()
        self.net = nn.Sequential(
            nn.Linear(self.INPUT_DIM, 64),
            nn.ReLU(),
            nn.LayerNorm(64),  # LayerNorm helps stability (per RLPD research)
            nn.Linear(64, 32),
            nn.ReLU(),
            nn.LayerNorm(32),
            nn.Linear(32, 1),  # scalar reward output
        )
    
    def forward(self, state: torch.Tensor, action: torch.Tensor) -> torch.Tensor:
        """
        Args:
            state:  (batch, 29) float state vectors
            action: (batch,) int action IDs  OR  (batch, 10) one-hot
        Returns:
            (batch, 1) predicted reward
        """
        if action.dim() == 1 or (action.dim() == 2 and action.shape[1] == 1):
            # Convert action IDs to one-hot
            if action.dim() == 2:
                action = action.squeeze(1)
            action_oh = torch.zeros(action.shape[0], self.ACTION_DIM, device=action.device)
            action_oh.scatter_(1, action.long().unsqueeze(1), 1.0)
        else:
            action_oh = action
        
        x = torch.cat([state, action_oh], dim=1)
        return self.net(x)
    
    def predict_reward(self, state: np.ndarray, action: int) -> float:
        """Convenience method for single (state, action) pair."""
        self.eval()
        with torch.no_grad():
            s = torch.from_numpy(state).float().unsqueeze(0)
            a = torch.tensor([action]).long()
            return self.forward(s, a).item()


# ============================================================
# Bootstrap Data — synthetic preferences from ARCHITECTURE.md §5.1
# ============================================================

def generate_bootstrap_data(n_samples: int = 5000, seed: int = 42) -> tuple:
    """
    Generate synthetic (state, action, reward) tuples based on the
    hand-coded reward table. These serve as a warm-start for the reward
    model before real preference data is available.
    
    Returns:
        states:  (N, 29) float32
        actions: (N,)    int64
        rewards: (N,)    float32
    """
    rng = np.random.RandomState(seed)
    
    states = []
    actions = []
    rewards = []
    
    for _ in range(n_samples):
        # Generate a random but plausible state vector
        state = np.zeros(29, dtype=np.float32)
        
        # Internal state (0-7): random personality values
        state[0] = rng.uniform(0.2, 1.0)   # energy
        state[1] = rng.uniform(0.2, 0.9)   # focus
        state[2] = rng.uniform(0.1, 0.8)   # engagement
        state[3] = rng.uniform(-0.5, 0.7)  # mood_valence
        state[4] = rng.uniform(0.2, 0.8)   # mood_intensity
        state[5] = rng.uniform(0.3, 0.9)   # confidence
        state[6] = rng.uniform(0.3, 0.9)   # warmth
        state[7] = rng.uniform(0.2, 0.8)   # curiosity
        
        # Situational awareness (8-19)
        has_messages = rng.random() > 0.4
        state[8]  = rng.uniform(0.1, 0.8) if has_messages else 0.0  # messages_waiting
        state[9]  = float(rng.random() > 0.7 and has_messages)       # has_urgent
        state[10] = rng.uniform(0.0, 0.8)  # time_since_user
        state[11] = rng.uniform(0.0, 0.5)  # time_since_action
        state[12] = rng.uniform(0.0, 0.5)  # active_goals
        state[13] = rng.uniform(0.0, 0.8)  # goals_with_pending
        state[14] = rng.uniform(0.0, 0.3)  # pending_memories
        state[15] = rng.uniform(0.0, 0.3)  # completed_tasks
        state[16] = float(rng.random() > 0.85)  # quiet_hours
        state[17] = float(rng.random() > 0.6)   # goal_active
        state[18] = rng.uniform(0.0, 0.3)  # consecutive_rests
        state[19] = rng.uniform(0.4, 0.9)  # trust_level
        
        # Message context (20-27): zero if no messages
        if has_messages:
            state[20] = rng.uniform(0.05, 0.5)  # msg_length
            state[21] = float(rng.random() > 0.5)  # has_question
            state[22] = rng.uniform(-0.3, 0.8)  # sentiment
            # Intent confidences (softmax-ish, should roughly sum to ~1)
            intents = rng.dirichlet([2, 2, 1, 1])
            state[23] = intents[0]  # conversation
            state[24] = intents[1]  # request
            state[25] = intents[2]  # research
            state[26] = intents[3]  # feedback
            state[27] = rng.uniform(0.0, 0.7)  # memory_relevance
        
        # Pick a random action and compute contextual reward
        action = rng.randint(0, 10)
        reward = _compute_synthetic_reward(state, action, rng)
        
        states.append(state)
        actions.append(action)
        rewards.append(reward)
    
    return (
        np.array(states, dtype=np.float32),
        np.array(actions, dtype=np.int64),
        np.array(rewards, dtype=np.float32),
    )


def _compute_synthetic_reward(state: np.ndarray, action: int, rng) -> float:
    """
    Compute reward based on ARCHITECTURE.md §5.1 reward table,
    conditioned on state context. Adds small noise to prevent
    the reward model from learning exact thresholds.
    """
    has_messages = state[8] > 0.05
    has_urgent = state[9] > 0.5
    has_goals = state[12] > 0.05
    has_pending_steps = state[13] > 0.05
    has_pending_memories = state[14] > 0.05
    low_energy = state[0] < 0.2
    quiet_hours = state[16] > 0.5
    
    noise = rng.normal(0, 0.05)  # small noise for generalization
    
    # Action 0: Rest
    if action == 0:
        if has_messages:
            return -1.0 + noise  # rested when messages waiting
        elif has_pending_steps:
            return -0.2 + noise  # could be working on goals
        else:
            return 0.1 + noise   # appropriate rest
    
    # Action 1: Reply to message
    if action == 1:
        if not has_messages:
            return -0.5 + noise  # impossible action
        elif has_urgent:
            return 1.5 + noise   # replied to urgent
        else:
            return 1.0 + noise   # replied to normal
    
    # Action 2: Research
    if action == 2:
        if low_energy:
            return -0.3 + noise  # too tired for expensive op
        elif has_messages and state[25] > 0.3:  # research intent
            return 0.8 + noise
        else:
            return 0.3 + noise
    
    # Action 3: Create goal
    if action == 3:
        if has_messages and state[24] > 0.3:  # request intent
            return 0.8 + noise
        elif not has_messages:
            return -0.3 + noise  # creating goals from nothing
        else:
            return 0.2 + noise
    
    # Action 4: Work on goal
    if action == 4:
        if not has_goals:
            return -0.5 + noise  # impossible action
        elif has_pending_steps:
            return 0.6 + noise   # advancing a goal
        else:
            return 0.2 + noise
    
    # Action 5: Store memory
    if action == 5:
        return 0.3 + noise
    
    # Action 6: Recall memories
    if action == 6:
        if has_messages and state[27] > 0.3:  # high memory relevance
            return 0.4 + noise
        else:
            return 0.1 + noise
    
    # Action 7: Consolidate memories
    if action == 7:
        if not has_pending_memories:
            return -0.3 + noise  # nothing to consolidate
        else:
            return 0.3 + noise
    
    # Action 8: Notify user
    if action == 8:
        if quiet_hours:
            return -0.8 + noise  # don't disturb
        elif state[15] > 0.1:   # completed tasks waiting
            return 0.8 + noise
        else:
            return 0.1 + noise
    
    # Action 9: Think
    if action == 9:
        if low_energy:
            return -0.3 + noise  # too tired
        elif not has_messages and not has_pending_steps:
            return 0.4 + noise   # good time to reflect
        else:
            return 0.1 + noise
    
    return 0.0 + noise


# ============================================================
# Preference Data — load from preferences table
# ============================================================

def ensure_preferences_table(db_path: str):
    """Create the preferences table if it doesn't exist."""
    conn = sqlite3.connect(db_path)
    conn.execute("""
        CREATE TABLE IF NOT EXISTS preferences (
            id          INTEGER PRIMARY KEY AUTOINCREMENT,
            state_a     TEXT NOT NULL,
            action_a    INTEGER NOT NULL,
            state_b     TEXT NOT NULL,
            action_b    INTEGER NOT NULL,
            preferred   INTEGER NOT NULL,  -- 0 = A preferred, 1 = B preferred, 2 = tie
            timestamp   TEXT NOT NULL,
            notes       TEXT
        )
    """)
    conn.commit()
    conn.close()


def load_preferences(db_path: str) -> list[dict]:
    """Load preference pairs from the preferences table."""
    conn = sqlite3.connect(db_path)
    cursor = conn.execute(
        "SELECT state_a, action_a, state_b, action_b, preferred FROM preferences"
    )
    
    pairs = []
    for row in cursor:
        try:
            pairs.append({
                "state_a": np.array(json.loads(row[0]), dtype=np.float32),
                "action_a": int(row[1]),
                "state_b": np.array(json.loads(row[2]), dtype=np.float32),
                "action_b": int(row[3]),
                "preferred": int(row[4]),
            })
        except (json.JSONDecodeError, TypeError):
            continue
    
    conn.close()
    return pairs


# ============================================================
# Training — Bradley-Terry preference loss (PEBBLE approach)
# ============================================================

def preference_loss(reward_model: DarciRewardModel, batch: dict) -> torch.Tensor:
    """
    Bradley-Terry model: P(A preferred over B) = sigmoid(r(A) - r(B))
    
    This is the standard loss used in RLHF and PEBBLE for learning
    reward models from pairwise comparisons.
    """
    r_a = reward_model(batch["state_a"], batch["action_a"])  # (batch, 1)
    r_b = reward_model(batch["state_b"], batch["action_b"])  # (batch, 1)
    
    # preferred: 0 = A better, 1 = B better
    # We want: if A preferred, r_a > r_b → positive logit
    logits = r_a - r_b  # (batch, 1)
    labels = batch["preferred"].float().unsqueeze(1)  # 0.0 or 1.0
    
    # Binary cross-entropy: label=0 means A preferred (logit should be positive)
    # label=1 means B preferred (logit should be negative)
    return nn.functional.binary_cross_entropy_with_logits(
        -logits,  # negate so label=0 → want positive logit to be high
        labels,
    )


def train_bootstrap(
    output_dir: Path,
    n_samples: int = 5000,
    epochs: int = 100,
    lr: float = 1e-3,
    batch_size: int = 128,
    seed: int = 42,
):
    """Train reward model from synthetic data based on hand-coded reward table."""
    torch.manual_seed(seed)
    
    print("Generating bootstrap data from reward table...")
    states, actions, rewards = generate_bootstrap_data(n_samples, seed)
    print(f"  ✓ Generated {n_samples} synthetic (state, action, reward) samples")
    print(f"  Reward range: [{rewards.min():.2f}, {rewards.max():.2f}]")
    
    # Normalize rewards to [-1, 1] for stable training
    r_mean = rewards.mean()
    r_std = max(rewards.std(), 1e-6)
    rewards_norm = (rewards - r_mean) / r_std
    rewards_norm = np.clip(rewards_norm, -3.0, 3.0)
    
    dataset = TensorDataset(
        torch.from_numpy(states),
        torch.from_numpy(actions),
        torch.from_numpy(rewards_norm),
    )
    
    val_size = max(1, int(n_samples * 0.15))
    train_size = n_samples - val_size
    train_ds, val_ds = random_split(dataset, [train_size, val_size])
    
    train_loader = DataLoader(train_ds, batch_size=batch_size, shuffle=True)
    val_loader = DataLoader(val_ds, batch_size=batch_size, shuffle=False)
    
    model = DarciRewardModel()
    optimizer = optim.Adam(model.parameters(), lr=lr, weight_decay=1e-4)
    loss_fn = nn.MSELoss()
    
    print(f"\nTraining reward model (bootstrap mode, {epochs} epochs)...")
    print("-" * 50)
    
    best_val_loss = float("inf")
    
    for epoch in range(1, epochs + 1):
        model.train()
        train_loss = 0.0
        
        for batch_s, batch_a, batch_r in train_loader:
            optimizer.zero_grad()
            pred = model(batch_s, batch_a).squeeze(1)
            loss = loss_fn(pred, batch_r)
            loss.backward()
            optimizer.step()
            train_loss += loss.item() * len(batch_s)
        
        # Validate
        model.eval()
        val_loss = 0.0
        with torch.no_grad():
            for batch_s, batch_a, batch_r in val_loader:
                pred = model(batch_s, batch_a).squeeze(1)
                val_loss += loss_fn(pred, batch_r).item() * len(batch_s)
        
        avg_train = train_loss / train_size
        avg_val = val_loss / val_size
        
        if epoch % 20 == 0:
            print(f"  Epoch {epoch:4d} | Train MSE: {avg_train:.4f} | Val MSE: {avg_val:.4f}")
        
        if avg_val < best_val_loss:
            best_val_loss = avg_val
            torch.save({
                "model_state_dict": model.state_dict(),
                "reward_mean": r_mean,
                "reward_std": r_std,
                "mode": "bootstrap",
            }, output_dir / "reward_model.pt")
    
    print("-" * 50)
    print(f"  Best val MSE: {best_val_loss:.4f}")
    
    return model, r_mean, r_std


def train_preferences(
    db_path: str,
    output_dir: Path,
    pretrained_path: Path = None,
    epochs: int = 200,
    lr: float = 5e-4,
    batch_size: int = 32,
):
    """Train/fine-tune reward model from human preference comparisons."""
    
    pairs = load_preferences(db_path)
    
    # Filter out ties (preference == 2) — they're uninformative for Bradley-Terry
    pairs = [p for p in pairs if p["preferred"] in (0, 1)]
    
    if len(pairs) < 10:
        print(f"⚠ Only {len(pairs)} preference pairs found. Need at least 10.")
        print(f"  Use the preference labeling tool to add more comparisons.")
        print(f"  Falling back to bootstrap mode.")
        return train_bootstrap(output_dir)
    
    print(f"  ✓ Loaded {len(pairs)} preference pairs")
    
    model = DarciRewardModel()
    
    # Load pretrained if available (warm-start from bootstrap)
    if pretrained_path and pretrained_path.exists():
        checkpoint = torch.load(pretrained_path, weights_only=False)
        model.load_state_dict(checkpoint["model_state_dict"])
        print(f"  ✓ Loaded pretrained weights from {pretrained_path}")
    
    optimizer = optim.Adam(model.parameters(), lr=lr, weight_decay=1e-4)
    
    # Build tensors
    states_a = torch.from_numpy(np.array([p["state_a"] for p in pairs]))
    actions_a = torch.tensor([p["action_a"] for p in pairs]).long()
    states_b = torch.from_numpy(np.array([p["state_b"] for p in pairs]))
    actions_b = torch.tensor([p["action_b"] for p in pairs]).long()
    preferred = torch.tensor([p["preferred"] for p in pairs]).long()
    
    n = len(pairs)
    
    print(f"\nTraining reward model (preference mode, {epochs} epochs)...")
    print("-" * 50)
    
    best_loss = float("inf")
    
    for epoch in range(1, epochs + 1):
        model.train()
        
        # Shuffle
        perm = torch.randperm(n)
        epoch_loss = 0.0
        n_batches = 0
        
        for i in range(0, n, batch_size):
            idx = perm[i:i+batch_size]
            batch = {
                "state_a": states_a[idx],
                "action_a": actions_a[idx],
                "state_b": states_b[idx],
                "action_b": actions_b[idx],
                "preferred": preferred[idx],
            }
            
            optimizer.zero_grad()
            loss = preference_loss(model, batch)
            loss.backward()
            optimizer.step()
            
            epoch_loss += loss.item()
            n_batches += 1
        
        avg_loss = epoch_loss / max(n_batches, 1)
        
        if epoch % 20 == 0:
            print(f"  Epoch {epoch:4d} | Preference Loss: {avg_loss:.4f}")
        
        if avg_loss < best_loss:
            best_loss = avg_loss
            torch.save({
                "model_state_dict": model.state_dict(),
                "mode": "preferences",
                "n_pairs": n,
            }, output_dir / "reward_model.pt")
    
    print("-" * 50)
    return model, 0.0, 1.0


# ============================================================
# ONNX Export
# ============================================================

def export_onnx(model: DarciRewardModel, output_dir: Path):
    """Export reward model to ONNX for C# runtime."""
    model.eval()
    
    # ONNX export needs the concatenated input directly
    # We'll export a wrapper that takes the pre-concatenated 38-dim vector
    class RewardModelONNX(nn.Module):
        def __init__(self, reward_model):
            super().__init__()
            self.net = reward_model.net
        
        def forward(self, state_action: torch.Tensor) -> torch.Tensor:
            return self.net(state_action)
    
    wrapper = RewardModelONNX(model)
    dummy = torch.randn(1, DarciRewardModel.INPUT_DIM)
    onnx_path = output_dir / "reward_model.onnx"
    
    torch.onnx.export(
        wrapper,
        dummy,
        str(onnx_path),
        input_names=["state_action"],
        output_names=["reward"],
        dynamic_axes={
            "state_action": {0: "batch_size"},
            "reward": {0: "batch_size"},
        },
        opset_version=17,
    )
    
    import onnx
    onnx.checker.check_model(onnx.load(str(onnx_path)))
    print(f"  ✓ ONNX export: {onnx_path}")


# ============================================================
# Entry point
# ============================================================

if __name__ == "__main__":
    parser = argparse.ArgumentParser(
        description="DARCI v4 — Reward Model Trainer"
    )
    parser.add_argument("--db", required=True, help="Path to DARCI's SQLite database")
    parser.add_argument("--output", default="./models", help="Output directory")
    parser.add_argument(
        "--mode", choices=["bootstrap", "preferences", "both"], default="both",
        help="Training mode: bootstrap from reward table, learn from preferences, or both"
    )
    parser.add_argument("--epochs", type=int, default=100)
    parser.add_argument("--lr", type=float, default=1e-3)
    
    args = parser.parse_args()
    output_dir = Path(args.output)
    output_dir.mkdir(parents=True, exist_ok=True)
    
    ensure_preferences_table(args.db)
    
    if args.mode == "bootstrap":
        model, _, _ = train_bootstrap(output_dir, epochs=args.epochs, lr=args.lr)
    elif args.mode == "preferences":
        model, _, _ = train_preferences(args.db, output_dir, epochs=args.epochs, lr=args.lr)
    else:  # both
        print("=" * 50)
        print("Phase 1: Bootstrap from reward table")
        print("=" * 50)
        model, _, _ = train_bootstrap(output_dir, epochs=args.epochs, lr=args.lr)
        
        print(f"\n{'=' * 50}")
        print("Phase 2: Fine-tune from preferences")
        print("=" * 50)
        model, _, _ = train_preferences(
            args.db, output_dir,
            pretrained_path=output_dir / "reward_model.pt",
            epochs=args.epochs, lr=args.lr * 0.5,
        )
    
    export_onnx(model, output_dir)
    print(f"\nDone. Reward model ready for DQN training.")
