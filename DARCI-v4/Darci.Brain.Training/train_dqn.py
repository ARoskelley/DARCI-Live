#!/usr/bin/env python3
"""
DARCI v4 — DQN Trainer (Offline-to-Online RL)
================================================
Phase 2b/3: Train the decision network via Deep Q-Learning using the
experience buffer, with research-backed techniques from RLPD, PEBBLE,
and conservative offline RL.

Key techniques (from Perplexity research):
  1. RLPD mixed batches:   Always mix teacher (BC) data with self-generated
                            data. Teacher data never leaves the buffer.
  2. KL penalty:           Regularize toward the frozen teacher policy to
                            prevent catastrophic forgetting during transition.
  3. Teacher-init Q:       Initialize Q-values high for teacher actions,
                            neutral for others. Gives DQN a strong head start.
  4. Reward normalization:  Running mean/std normalization on rewards,
                            clipped to [-1, 1] for stable gradient updates.
  5. Learned reward model:  Optionally use reward_model.pt instead of
                            raw stored rewards for richer signal.

Produces:
  darci_policy.onnx  — the trained DQN policy for C# ONNX Runtime
  darci_policy.pt    — PyTorch checkpoint

Usage:
  python train_dqn.py --db darci.db --teacher models/darci_teacher.pt
  python train_dqn.py --db darci.db --teacher models/darci_teacher.pt --reward-model models/reward_model.pt
  python train_dqn.py --db darci.db --teacher models/darci_teacher.pt --episodes 500

Requirements:
  pip install torch numpy onnx onnxruntime
"""

import argparse
import json
import sqlite3
import copy
from pathlib import Path
from collections import deque

import numpy as np
import torch
import torch.nn as nn
import torch.nn.functional as F
import torch.optim as optim

# Import shared network architecture
from train_behavioral_cloning import DarciDecisionNetwork
from train_reward_model import DarciRewardModel

# ============================================================
# Hyperparameters - grounded in research recommendations
# ============================================================

STATE_DIM = 29

class DQNConfig:
    # Network
    state_dim: int = STATE_DIM
    action_dim: int = 10
    
    # DQN core
    gamma: float = 0.95           # discount factor (ARCHITECTURE.md §6.3)
    tau: float = 0.005            # soft target network update rate
    
    # Exploration (epsilon-greedy)
    epsilon_start: float = 0.3    # lower than typical because we have teacher init
    epsilon_end: float = 0.05
    epsilon_decay_steps: int = 10000
    
    # Training
    batch_size: int = 64
    lr: float = 1e-4              # conservative LR for fine-tuning
    weight_decay: float = 1e-5
    max_grad_norm: float = 1.0    # gradient clipping
    
    # RLPD: mixed batch ratio (fraction of teacher data in each batch)
    teacher_ratio_start: float = 0.5   # start with 50% teacher data
    teacher_ratio_end: float = 0.1     # anneal to 10%
    teacher_ratio_decay_steps: int = 20000
    
    # KL penalty toward teacher (conservative update)
    kl_coeff_start: float = 1.0        # strong anchor initially
    kl_coeff_end: float = 0.05         # relax as agent builds track record
    kl_decay_steps: int = 15000
    
    # Reward normalization
    reward_clip: float = 3.0
    reward_norm_window: int = 1000
    
    # Target network
    target_update_every: int = 100  # soft update every N training steps
    
    # Training loop
    train_every: int = 4           # train after every N experiences
    min_buffer_size: int = 200     # don't train until buffer has this many
    updates_per_step: int = 2      # high UTD ratio (per RLPD)


# ============================================================
# Reward Normalizer — running mean/std with clipping
# ============================================================

class RewardNormalizer:
    """
    Online reward normalization using running statistics.
    Per research: normalize rewards to stabilize gradient updates,
    especially when mixing hand-coded and learned reward signals.
    """
    
    def __init__(self, window_size: int = 1000, clip: float = 3.0):
        self.rewards = deque(maxlen=window_size)
        self.clip = clip
    
    def normalize(self, reward: float) -> float:
        self.rewards.append(reward)
        if len(self.rewards) < 10:
            return np.clip(reward, -self.clip, self.clip)
        
        mean = np.mean(self.rewards)
        std = max(np.std(self.rewards), 1e-6)
        normalized = (reward - mean) / std
        return float(np.clip(normalized, -self.clip, self.clip))
    
    def normalize_batch(self, rewards: np.ndarray) -> np.ndarray:
        return np.array([self.normalize(r) for r in rewards])


# ============================================================
# Experience Replay with Teacher/Online Separation
# ============================================================

class MixedReplayBuffer:
    """
    RLPD-style replay buffer that maintains separate pools for
    teacher (BC) data and self-generated (online) data, then
    samples mixed batches according to a configurable ratio.
    
    Teacher data is NEVER removed — it's a permanent anchor.
    Online data uses standard ring-buffer eviction.
    """
    
    def __init__(self, online_capacity: int = 50000):
        self.teacher_states = []
        self.teacher_actions = []
        self.teacher_rewards = []
        self.teacher_next_states = []
        self.teacher_terminals = []
        
        self.online_capacity = online_capacity
        self.online_states = deque(maxlen=online_capacity)
        self.online_actions = deque(maxlen=online_capacity)
        self.online_rewards = deque(maxlen=online_capacity)
        self.online_next_states = deque(maxlen=online_capacity)
        self.online_terminals = deque(maxlen=online_capacity)
    
    def add_teacher(self, state, action, reward, next_state, terminal=False):
        self.teacher_states.append(state)
        self.teacher_actions.append(action)
        self.teacher_rewards.append(reward)
        self.teacher_next_states.append(next_state)
        self.teacher_terminals.append(terminal)
    
    def add_online(self, state, action, reward, next_state, terminal=False):
        self.online_states.append(state)
        self.online_actions.append(action)
        self.online_rewards.append(reward)
        self.online_next_states.append(next_state)
        self.online_terminals.append(terminal)
    
    @property
    def teacher_size(self):
        return len(self.teacher_states)
    
    @property
    def online_size(self):
        return len(self.online_states)
    
    @property
    def total_size(self):
        return self.teacher_size + self.online_size
    
    def sample(self, batch_size: int, teacher_ratio: float) -> dict:
        """
        Sample a mixed batch with `teacher_ratio` fraction from teacher pool.
        If either pool is too small, sample entirely from the other.
        """
        n_teacher = int(batch_size * teacher_ratio)
        n_online = batch_size - n_teacher
        
        # Adjust if pools are too small
        if self.teacher_size < n_teacher:
            n_teacher = self.teacher_size
            n_online = batch_size - n_teacher
        if self.online_size < n_online:
            n_online = self.online_size
            n_teacher = batch_size - n_online
        
        # Sample indices
        t_idx = np.random.choice(self.teacher_size, size=n_teacher, replace=False) if n_teacher > 0 else []
        o_idx = np.random.choice(self.online_size, size=n_online, replace=False) if n_online > 0 else []
        
        # Gather
        states = []
        actions = []
        rewards = []
        next_states = []
        terminals = []
        
        for i in t_idx:
            states.append(self.teacher_states[i])
            actions.append(self.teacher_actions[i])
            rewards.append(self.teacher_rewards[i])
            next_states.append(self.teacher_next_states[i])
            terminals.append(self.teacher_terminals[i])
        
        for i in o_idx:
            states.append(self.online_states[i])
            actions.append(self.online_actions[i])
            rewards.append(self.online_rewards[i])
            next_states.append(self.online_next_states[i])
            terminals.append(self.online_terminals[i])
        
        return {
            "states": torch.FloatTensor(np.array(states)),
            "actions": torch.LongTensor(np.array(actions)),
            "rewards": torch.FloatTensor(np.array(rewards)),
            "next_states": torch.FloatTensor(np.array(next_states)),
            "terminals": torch.FloatTensor(np.array(terminals, dtype=np.float32)),
            "n_teacher": n_teacher,
            "n_online": n_online,
        }


# ============================================================
# DQN Agent with RLPD + KL Penalty + Teacher Init
# ============================================================

class DarciDQNAgent:
    """
    Deep Q-Network agent for DARCI, incorporating:
    - Double DQN (use online net to select, target net to evaluate)
    - Soft target network updates
    - KL penalty toward frozen teacher policy
    - Teacher-initialized Q-values
    - RLPD mixed-batch training
    """
    
    def __init__(self, config: DQNConfig, teacher_path: str = None):
        self.config = config
        
        # Online network (the one being trained)
        self.online_net = DarciDecisionNetwork()
        
        # Target network (slowly updated copy for stable Q-targets)
        self.target_net = DarciDecisionNetwork()
        
        # Frozen teacher (for KL penalty — never updated)
        self.teacher_net = DarciDecisionNetwork()
        self.teacher_loaded = False
        
        # Load teacher weights if available
        if teacher_path and Path(teacher_path).exists():
            checkpoint = torch.load(teacher_path, weights_only=False)
            self.teacher_net.load_state_dict(checkpoint["model_state_dict"])
            
            # Teacher-initialized Q-values: start online net from teacher
            # This gives DQN a massive head start (per research recommendations)
            self.online_net.load_state_dict(checkpoint["model_state_dict"])
            self.teacher_loaded = True
            print(f"  ✓ Loaded teacher weights — Q-values initialized from BC policy")
        
        # Target net starts as copy of online
        self.target_net.load_state_dict(self.online_net.state_dict())
        
        # Freeze teacher and target
        for p in self.teacher_net.parameters():
            p.requires_grad = False
        for p in self.target_net.parameters():
            p.requires_grad = False
        
        # Optimizer (with LayerNorm-friendly settings per RLPD)
        self.optimizer = optim.Adam(
            self.online_net.parameters(),
            lr=config.lr,
            weight_decay=config.weight_decay,
        )
        
        # Reward model (optional, loaded separately)
        self.reward_model = None
        
        # Training state
        self.train_step = 0
        self.reward_normalizer = RewardNormalizer(
            window_size=config.reward_norm_window,
            clip=config.reward_clip,
        )
    
    def load_reward_model(self, path: str):
        """Load the learned reward model for richer reward signal."""
        checkpoint = torch.load(path, weights_only=False)
        self.reward_model = DarciRewardModel()
        self.reward_model.load_state_dict(checkpoint["model_state_dict"])
        self.reward_model.eval()
        for p in self.reward_model.parameters():
            p.requires_grad = False
        print(f"  ✓ Loaded reward model — using learned rewards")
    
    def get_epsilon(self) -> float:
        """Current exploration rate with linear decay."""
        cfg = self.config
        progress = min(self.train_step / max(cfg.epsilon_decay_steps, 1), 1.0)
        return cfg.epsilon_start + (cfg.epsilon_end - cfg.epsilon_start) * progress
    
    def get_teacher_ratio(self) -> float:
        """Current teacher data fraction with linear decay."""
        cfg = self.config
        progress = min(self.train_step / max(cfg.teacher_ratio_decay_steps, 1), 1.0)
        return cfg.teacher_ratio_start + (cfg.teacher_ratio_end - cfg.teacher_ratio_start) * progress
    
    def get_kl_coeff(self) -> float:
        """Current KL penalty coefficient with linear decay."""
        cfg = self.config
        progress = min(self.train_step / max(cfg.kl_decay_steps, 1), 1.0)
        return cfg.kl_coeff_start + (cfg.kl_coeff_end - cfg.kl_coeff_start) * progress
    
    def select_action(self, state: np.ndarray, action_mask: np.ndarray = None) -> int:
        """Epsilon-greedy action selection with optional masking."""
        if np.random.random() < self.get_epsilon():
            # Random valid action
            if action_mask is not None:
                valid = np.where(action_mask)[0]
                return int(np.random.choice(valid)) if len(valid) > 0 else 0
            return int(np.random.randint(0, self.config.action_dim))
        
        # Greedy from online network
        self.online_net.eval()
        with torch.no_grad():
            state_t = torch.FloatTensor(state).unsqueeze(0)
            q_values = self.online_net(state_t).squeeze(0).numpy()
        
        if action_mask is not None:
            q_values[~action_mask] = -np.inf
        
        return int(np.argmax(q_values))
    
    def train_step_fn(self, buffer: MixedReplayBuffer) -> dict:
        """
        One DQN training step with:
          - Double DQN Q-target computation
          - KL penalty toward teacher
          - Mixed teacher/online batches
        
        Returns dict of training metrics.
        """
        if buffer.total_size < self.config.min_buffer_size:
            return {}
        
        self.online_net.train()
        cfg = self.config
        
        # --- Sample mixed batch ---
        teacher_ratio = self.get_teacher_ratio()
        batch = buffer.sample(cfg.batch_size, teacher_ratio)
        
        states = batch["states"]
        actions = batch["actions"]
        rewards = batch["rewards"]
        next_states = batch["next_states"]
        terminals = batch["terminals"]
        
        # --- Compute Q-targets (Double DQN) ---
        with torch.no_grad():
            # Online net selects best action
            next_q_online = self.online_net(next_states)
            best_actions = next_q_online.argmax(dim=1, keepdim=True)
            
            # Target net evaluates that action
            next_q_target = self.target_net(next_states)
            next_q_value = next_q_target.gather(1, best_actions).squeeze(1)
            
            # Bellman target
            target = rewards + cfg.gamma * next_q_value * (1.0 - terminals)
        
        # --- Current Q-values ---
        current_q = self.online_net(states)
        q_values = current_q.gather(1, actions.unsqueeze(1)).squeeze(1)
        
        # --- TD Loss (Huber for stability) ---
        td_loss = F.smooth_l1_loss(q_values, target)
        
        # --- KL Penalty toward teacher ---
        kl_loss = torch.tensor(0.0)
        kl_coeff = self.get_kl_coeff()
        
        if self.teacher_loaded and kl_coeff > 0.01:
            with torch.no_grad():
                teacher_logits = self.teacher_net(states)
                teacher_probs = F.softmax(teacher_logits, dim=1)
            
            online_log_probs = F.log_softmax(current_q, dim=1)
            
            # KL(teacher || online) — penalize divergence from teacher
            kl_loss = F.kl_div(online_log_probs, teacher_probs, reduction="batchmean")
        
        # --- Total loss ---
        total_loss = td_loss + kl_coeff * kl_loss
        
        # --- Optimize ---
        self.optimizer.zero_grad()
        total_loss.backward()
        torch.nn.utils.clip_grad_norm_(self.online_net.parameters(), cfg.max_grad_norm)
        self.optimizer.step()
        
        # --- Soft target update ---
        self.train_step += 1
        if self.train_step % cfg.target_update_every == 0:
            with torch.no_grad():
                for p_target, p_online in zip(
                    self.target_net.parameters(), self.online_net.parameters()
                ):
                    p_target.data.mul_(1.0 - cfg.tau).add_(p_online.data * cfg.tau)
        
        return {
            "td_loss": td_loss.item(),
            "kl_loss": kl_loss.item(),
            "kl_coeff": kl_coeff,
            "total_loss": total_loss.item(),
            "epsilon": self.get_epsilon(),
            "teacher_ratio": teacher_ratio,
            "mean_q": q_values.mean().item(),
            "n_teacher": batch["n_teacher"],
            "n_online": batch["n_online"],
        }
    
    def save(self, output_dir: Path):
        """Save policy checkpoint and ONNX export."""
        output_dir.mkdir(parents=True, exist_ok=True)
        
        # PyTorch checkpoint
        torch.save({
            "model_state_dict": self.online_net.state_dict(),
            "target_state_dict": self.target_net.state_dict(),
            "optimizer_state_dict": self.optimizer.state_dict(),
            "train_step": self.train_step,
        }, output_dir / "darci_policy.pt")
        
        # ONNX export
        self.online_net.eval()
        dummy = torch.randn(1, self.config.state_dim)
        onnx_path = output_dir / "darci_policy.onnx"
        
        torch.onnx.export(
            self.online_net,
            dummy,
            str(onnx_path),
            input_names=["state_vector"],
            output_names=["action_logits"],
            dynamic_axes={
                "state_vector": {0: "batch_size"},
                "action_logits": {0: "batch_size"},
            },
            opset_version=17,
        )
        
        import onnx
        onnx.checker.check_model(onnx.load(str(onnx_path)))
        print(f"  ✓ Saved: {onnx_path}")


# ============================================================
# Data Loading — seed buffer from SQLite
# ============================================================

def load_experiences_from_db(db_path: str) -> tuple[list[dict], dict]:
    """Load current-format experiences from SQLite, skipping legacy rows."""
    conn = sqlite3.connect(db_path)
    cursor = conn.execute(
        "SELECT state, action, reward, next_state, is_terminal FROM experiences ORDER BY id"
    )
    
    experiences = []
    stats = {
        "accepted": 0,
        "legacy_28": 0,
        "wrong_dim": 0,
        "malformed": 0,
    }

    for row in cursor:
        try:
            state = np.array(json.loads(row[0]), dtype=np.float32)
            next_state = np.array(json.loads(row[3]), dtype=np.float32)

            state_len = len(state)
            next_state_len = len(next_state)
            if state_len != STATE_DIM or next_state_len != STATE_DIM:
                dims = {state_len, next_state_len}
                if dims.issubset({28, STATE_DIM}) and 28 in dims:
                    stats["legacy_28"] += 1
                else:
                    stats["wrong_dim"] += 1
                continue

            experiences.append({
                "state": state,
                "action": int(row[1]),
                "reward": float(row[2]),
                "next_state": next_state,
                "terminal": bool(row[4]),
            })
            stats["accepted"] += 1
        except (json.JSONDecodeError, TypeError):
            stats["malformed"] += 1
    
    conn.close()
    return experiences, stats


def load_teacher_decisions(db_path: str) -> tuple[list[dict], dict]:
    """
    Load teacher decisions and convert to pseudo-experiences.
    Since decision_log doesn't have next_state or reward,
    we use adjacent entries and assign neutral rewards.
    """
    conn = sqlite3.connect(db_path)
    cursor = conn.execute(
        "SELECT state_vector, action_chosen FROM decision_log ORDER BY id"
    )
    
    rows = []
    stats = {
        "accepted": 0,
        "legacy_28": 0,
        "wrong_dim": 0,
        "malformed": 0,
    }

    for row in cursor:
        try:
            sv = np.array(json.loads(row[0]), dtype=np.float32)
            if len(sv) == STATE_DIM:
                rows.append({"state": sv, "action": int(row[1])})
                stats["accepted"] += 1
            elif len(sv) == 28:
                stats["legacy_28"] += 1
            else:
                stats["wrong_dim"] += 1
        except (json.JSONDecodeError, TypeError):
            stats["malformed"] += 1
    
    conn.close()
    
    # Create pseudo-experiences from sequential decisions
    experiences = []
    for i in range(len(rows) - 1):
        experiences.append({
            "state": rows[i]["state"],
            "action": rows[i]["action"],
            "reward": 0.0,  # neutral — teacher data is for policy shape, not reward
            "next_state": rows[i + 1]["state"],
            "terminal": False,
        })
    
    return experiences, stats


# ============================================================
# Offline Training Loop
# ============================================================

def train_offline(
    db_path: str,
    teacher_path: str,
    reward_model_path: str = None,
    output_dir: str = "./models",
    n_epochs: int = 200,
    seed: int = 42,
):
    """
    Offline DQN training from accumulated experience data.
    This is the Phase 2b training loop — learns from stored experiences
    without running DARCI live.
    """
    torch.manual_seed(seed)
    np.random.seed(seed)
    
    output_dir = Path(output_dir)
    config = DQNConfig()
    
    # --- Initialize agent ---
    print("Initializing DQN agent...")
    agent = DarciDQNAgent(config, teacher_path)
    
    if reward_model_path and Path(reward_model_path).exists():
        agent.load_reward_model(reward_model_path)
    
    # --- Load data into replay buffer ---
    print(f"\nLoading data from {db_path}...")
    
    buffer = MixedReplayBuffer(online_capacity=50000)
    
    # Teacher data (from decision_log — permanent)
    teacher_data, teacher_stats = load_teacher_decisions(db_path)
    for exp in teacher_data:
        buffer.add_teacher(
            exp["state"], exp["action"], exp["reward"],
            exp["next_state"], exp["terminal"]
        )
    print(f"  ✓ Teacher pool: {buffer.teacher_size} experiences")
    if teacher_stats["legacy_28"] or teacher_stats["wrong_dim"] or teacher_stats["malformed"]:
        print(
            "    skipped teacher rows:"
            f" legacy28={teacher_stats['legacy_28']},"
            f" wrong_dim={teacher_stats['wrong_dim']},"
            f" malformed={teacher_stats['malformed']}"
        )
    
    # Online data (from experience buffer — treated as self-generated)
    online_data, online_stats = load_experiences_from_db(db_path)
    for exp in online_data:
        # Optionally re-score with learned reward model
        reward = exp["reward"]
        if agent.reward_model is not None:
            learned_reward = agent.reward_model.predict_reward(
                exp["state"], exp["action"]
            )
            # Blend: keep hard-coded penalties, use learned model for the rest
            # Hard penalties are large negative values
            if reward < -0.8:
                pass  # keep the hard penalty (spam, impossible action)
            else:
                reward = 0.3 * reward + 0.7 * learned_reward  # weighted blend
        
        buffer.add_online(
            exp["state"], exp["action"],
            agent.reward_normalizer.normalize(reward),
            exp["next_state"], exp["terminal"]
        )
    print(f"  ✓ Online pool: {buffer.online_size} experiences")
    if online_stats["legacy_28"] or online_stats["wrong_dim"] or online_stats["malformed"]:
        print(
            "    skipped online rows:"
            f" legacy28={online_stats['legacy_28']},"
            f" wrong_dim={online_stats['wrong_dim']},"
            f" malformed={online_stats['malformed']}"
        )
    print(f"  ✓ Total: {buffer.total_size} experiences")
    
    if buffer.total_size < config.min_buffer_size:
        print(f"\n✗ Need at least {config.min_buffer_size} total experiences.")
        print(f"  Run DARCI v4 longer to collect more data.")
        return
    
    # --- Training loop ---
    total_steps = n_epochs * (buffer.total_size // config.batch_size)
    print(f"\nTraining for {n_epochs} epochs (~{total_steps} gradient steps)...")
    print(f"  Config: gamma={config.gamma}, lr={config.lr}, batch={config.batch_size}")
    print(f"  Teacher ratio: {config.teacher_ratio_start} → {config.teacher_ratio_end}")
    print(f"  KL coeff: {config.kl_coeff_start} → {config.kl_coeff_end}")
    print(f"  Epsilon: {config.epsilon_start} → {config.epsilon_end}")
    print("-" * 70)
    
    steps_per_epoch = max(buffer.total_size // config.batch_size, 1)
    best_mean_q = -float("inf")
    
    for epoch in range(1, n_epochs + 1):
        epoch_metrics = {
            "td_loss": [], "kl_loss": [], "total_loss": [],
            "mean_q": [], "kl_coeff": [],
        }
        
        for _ in range(steps_per_epoch):
            for _ in range(config.updates_per_step):
                metrics = agent.train_step_fn(buffer)
                if metrics:
                    for k in epoch_metrics:
                        if k in metrics:
                            epoch_metrics[k].append(metrics[k])
        
        # Log
        if epoch % 10 == 0 or epoch == 1:
            avg = {k: np.mean(v) if v else 0 for k, v in epoch_metrics.items()}
            mean_q = avg["mean_q"]
            marker = " ★" if mean_q > best_mean_q else ""
            
            print(
                f"  Epoch {epoch:4d} | "
                f"TD: {avg['td_loss']:.4f} | "
                f"KL: {avg['kl_loss']:.4f} (×{avg['kl_coeff']:.2f}) | "
                f"Q̄: {mean_q:+.3f} | "
                f"ε: {agent.get_epsilon():.3f} | "
                f"τ_ratio: {agent.get_teacher_ratio():.2f}"
                f"{marker}"
            )
            
            if mean_q > best_mean_q:
                best_mean_q = mean_q
                agent.save(output_dir)
    
    print("-" * 70)
    
    # Final save
    agent.save(output_dir)
    
    # --- Validation: compare against teacher ---
    if agent.teacher_loaded:
        print("\nTeacher agreement analysis...")
        agent.online_net.eval()
        agent.teacher_net.eval()
        
        n_agree = 0
        n_total = min(buffer.teacher_size, 1000)
        
        with torch.no_grad():
            for i in range(n_total):
                state = torch.FloatTensor(buffer.teacher_states[i]).unsqueeze(0)
                teacher_action = buffer.teacher_actions[i]
                
                dqn_action = agent.online_net(state).argmax(dim=1).item()
                if dqn_action == teacher_action:
                    n_agree += 1
        
        agreement = n_agree / n_total * 100
        print(f"  DQN agrees with teacher on {agreement:.1f}% of teacher states")
        
        if agreement > 85:
            print(f"  ✓ Strong agreement — DQN has internalized teacher behavior")
            print(f"    and may now be diverging intentionally where reward signal is stronger.")
        elif agreement > 60:
            print(f"  ✓ Moderate agreement — DQN is learning its own policy")
            print(f"    while retaining core teacher patterns.")
        else:
            print(f"  ⚠ Low agreement — check reward signal and KL coefficient.")
            print(f"    The DQN may be diverging too aggressively.")
    
    print(f"\nOutputs:")
    print(f"  {output_dir / 'darci_policy.onnx'}  — for C# ONNX Runtime")
    print(f"  {output_dir / 'darci_policy.pt'}    — checkpoint")


# ============================================================
# Entry point
# ============================================================

if __name__ == "__main__":
    parser = argparse.ArgumentParser(
        description="DARCI v4 — DQN Trainer (Offline-to-Online RL)"
    )
    parser.add_argument("--db", required=True, help="Path to DARCI's SQLite database")
    parser.add_argument("--teacher", required=True, help="Path to darci_teacher.pt")
    parser.add_argument("--reward-model", default=None, help="Path to reward_model.pt (optional)")
    parser.add_argument("--output", default="./models", help="Output directory")
    parser.add_argument("--epochs", type=int, default=200)
    parser.add_argument("--seed", type=int, default=42)
    
    args = parser.parse_args()
    
    train_offline(
        db_path=args.db,
        teacher_path=args.teacher,
        reward_model_path=args.reward_model,
        output_dir=args.output,
        n_epochs=args.epochs,
        seed=args.seed,
    )
