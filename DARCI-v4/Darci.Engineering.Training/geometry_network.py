#!/usr/bin/env python3
"""
DARCI v4 — Geometry Network Architecture
==========================================
Soft Actor-Critic (SAC) with hybrid discrete + continuous action space.

The geometry network has two output heads:
  1. Action Head (discrete): which of 20 operations to perform
  2. Parameter Head (continuous): 6 parameters for that operation

This is larger than the behavioral network (~85K params vs 4K)
because geometric reasoning needs more representational capacity.

Architecture (ENGINEERING_ARCHITECTURE.md §2.6):
  State (64) → Shared Trunk (256→128 with LayerNorm)
    → Action Head (128→64→20 logits)
    → Parameter Head (128→64→120, reshaped to 20×6, tanh)

Training uses SAC which naturally handles entropy-regularized
exploration — critical for the vast geometric search space.
"""

import numpy as np
import torch
import torch.nn as nn
import torch.nn.functional as F
from torch.distributions import Categorical

STATE_DIM = 64
ACTION_DIM = 20
PARAM_DIM = 6      # continuous parameters per action
TOTAL_PARAMS = ACTION_DIM * PARAM_DIM  # 120


# ============================================================
# Shared Trunk — used by Actor and Critic
# ============================================================

class SharedTrunk(nn.Module):
    """Feature extractor shared between actor and critic."""

    def __init__(self, state_dim: int = STATE_DIM, hidden1: int = 256, hidden2: int = 128):
        super().__init__()
        self.net = nn.Sequential(
            nn.Linear(state_dim, hidden1),
            nn.ReLU(),
            nn.LayerNorm(hidden1),
            nn.Linear(hidden1, hidden2),
            nn.ReLU(),
            nn.LayerNorm(hidden2),
        )
        self.output_dim = hidden2

    def forward(self, state: torch.Tensor) -> torch.Tensor:
        return self.net(state)


# ============================================================
# Actor — outputs discrete action + continuous parameters
# ============================================================

class GeometryActor(nn.Module):
    """
    Policy network: state → (action_probs, parameters).

    Action head: softmax over 20 discrete actions.
    Parameter head: tanh-bounded continuous params per action.
    """

    def __init__(self, state_dim: int = STATE_DIM):
        super().__init__()
        self.trunk = SharedTrunk(state_dim)
        h = self.trunk.output_dim  # 128

        # Discrete action head
        self.action_head = nn.Sequential(
            nn.Linear(h, 64),
            nn.ReLU(),
            nn.Linear(64, ACTION_DIM),
        )

        # Continuous parameter head
        # Outputs 20×6 = 120 values (one param set per action)
        self.param_head = nn.Sequential(
            nn.Linear(h, 64),
            nn.ReLU(),
            nn.Linear(64, TOTAL_PARAMS),
        )

    def forward(self, state: torch.Tensor, action_mask: torch.Tensor = None):
        """
        Returns:
            action_logits: (batch, 20) unnormalized log-probs
            params: (batch, 20, 6) tanh-bounded parameters per action
        """
        features = self.trunk(state)

        # Action logits with masking
        logits = self.action_head(features)
        if action_mask is not None:
            logits = logits.masked_fill(~action_mask.bool(), float("-inf"))

        # Parameters: tanh to bound in [-1, 1]
        raw_params = self.param_head(features)
        params = torch.tanh(raw_params).view(-1, ACTION_DIM, PARAM_DIM)

        return logits, params

    def get_action(self, state: torch.Tensor, action_mask: torch.Tensor = None,
                   deterministic: bool = False):
        """
        Sample an action and its parameters.

        Returns:
            action: (batch,) int action IDs
            params: (batch, 6) continuous parameters for chosen action
            log_prob: (batch,) log probability (for SAC entropy)
        """
        logits, all_params = self.forward(state, action_mask)

        # Guard against all-masked (all -inf) logits
        if torch.all(logits == float("-inf")):
            logits = torch.zeros_like(logits)
        probs = F.softmax(logits, dim=-1)
        probs = probs.clamp(min=1e-8)  # prevent zero probabilities
        dist = Categorical(probs)

        if deterministic:
            action = probs.argmax(dim=-1)
        else:
            action = dist.sample()

        log_prob = dist.log_prob(action)

        # Select parameters for the chosen action
        batch_size = state.shape[0]
        params = all_params[torch.arange(batch_size), action]  # (batch, 6)

        return action, params, log_prob

    def evaluate_action(self, state: torch.Tensor, action: torch.Tensor,
                        action_mask: torch.Tensor = None):
        """
        Evaluate log_prob and entropy for given state-action pairs.
        Used during SAC training to compute the policy gradient.
        """
        logits, all_params = self.forward(state, action_mask)
        probs = F.softmax(logits, dim=-1)
        dist = Categorical(probs)

        log_prob = dist.log_prob(action)
        entropy = dist.entropy()

        batch_size = state.shape[0]
        params = all_params[torch.arange(batch_size), action]

        return log_prob, entropy, params


# ============================================================
# Critic — evaluates Q(s, a, params)
# ============================================================

class GeometryCritic(nn.Module):
    """
    Q-value network: (state, action_onehot, params) → Q-value.

    Input: state(64) + action_onehot(20) + params(6) = 90 dims.
    """

    def __init__(self, state_dim: int = STATE_DIM):
        super().__init__()
        input_dim = state_dim + ACTION_DIM + PARAM_DIM  # 64 + 20 + 6 = 90

        self.net = nn.Sequential(
            nn.Linear(input_dim, 256),
            nn.ReLU(),
            nn.LayerNorm(256),
            nn.Linear(256, 128),
            nn.ReLU(),
            nn.LayerNorm(128),
            nn.Linear(128, 64),
            nn.ReLU(),
            nn.Linear(64, 1),
        )

    def forward(self, state: torch.Tensor, action: torch.Tensor,
                params: torch.Tensor) -> torch.Tensor:
        """
        Args:
            state: (batch, 64)
            action: (batch,) int action IDs
            params: (batch, 6) continuous parameters
        Returns:
            Q-value: (batch, 1)
        """
        # One-hot encode action
        action_oh = torch.zeros(state.shape[0], ACTION_DIM, device=state.device)
        action_oh.scatter_(1, action.long().unsqueeze(1), 1.0)

        x = torch.cat([state, action_oh, params], dim=-1)
        return self.net(x)


# ============================================================
# Full SAC Agent
# ============================================================

class GeometrySACAgent:
    """
    Soft Actor-Critic for geometry workbench.

    Twin critics (Q1, Q2) for stability.
    Automatic entropy tuning (alpha).
    Teacher KL penalty (optional, for BC→RL transition).
    """

    def __init__(
        self,
        state_dim: int = STATE_DIM,
        lr_actor: float = 3e-4,
        lr_critic: float = 3e-4,
        lr_alpha: float = 3e-4,
        gamma: float = 0.95,
        tau: float = 0.005,
        initial_alpha: float = 0.2,
        target_entropy_ratio: float = 0.5,
        teacher_kl_coeff: float = 0.0,
    ):
        self.gamma = gamma
        self.tau = tau
        self.teacher_kl_coeff = teacher_kl_coeff

        # Actor
        self.actor = GeometryActor(state_dim)

        # Twin critics
        self.critic1 = GeometryCritic(state_dim)
        self.critic2 = GeometryCritic(state_dim)
        self.target_critic1 = GeometryCritic(state_dim)
        self.target_critic2 = GeometryCritic(state_dim)

        # Initialize targets as copies
        self.target_critic1.load_state_dict(self.critic1.state_dict())
        self.target_critic2.load_state_dict(self.critic2.state_dict())

        # Freeze target parameters
        for p in self.target_critic1.parameters():
            p.requires_grad = False
        for p in self.target_critic2.parameters():
            p.requires_grad = False

        # Optimizers
        self.actor_optimizer = torch.optim.Adam(self.actor.parameters(), lr=lr_actor)
        self.critic1_optimizer = torch.optim.Adam(self.critic1.parameters(), lr=lr_critic)
        self.critic2_optimizer = torch.optim.Adam(self.critic2.parameters(), lr=lr_critic)

        # Automatic entropy tuning
        self.log_alpha = torch.tensor(np.log(initial_alpha), requires_grad=True)
        self.alpha_optimizer = torch.optim.Adam([self.log_alpha], lr=lr_alpha)
        self.target_entropy = -target_entropy_ratio * np.log(1.0 / ACTION_DIM)

        # Teacher (optional, for KL penalty during BC→RL transition)
        self.teacher_actor = None

        # Training step counter
        self.train_step = 0

    @property
    def alpha(self):
        return self.log_alpha.exp().item()

    def load_teacher(self, path: str):
        """Load a frozen teacher policy for KL regularization."""
        checkpoint = torch.load(path, weights_only=False)
        self.teacher_actor = GeometryActor()
        self.teacher_actor.load_state_dict(checkpoint["actor_state_dict"])
        self.teacher_actor.eval()
        for p in self.teacher_actor.parameters():
            p.requires_grad = False
        print(f"  ✓ Loaded teacher actor for KL penalty")

    def select_action(self, state: np.ndarray, action_mask: np.ndarray = None,
                      deterministic: bool = False):
        """Select action for environment interaction."""
        self.actor.eval()
        with torch.no_grad():
            state_t = torch.FloatTensor(state).unsqueeze(0)
            mask_t = None
            if action_mask is not None:
                mask_t = torch.BoolTensor(action_mask).unsqueeze(0)

            action, params, _ = self.actor.get_action(state_t, mask_t, deterministic)

        return int(action.item()), params.squeeze(0).numpy()

    def update(self, batch: dict) -> dict:
        """
        One SAC training step.

        batch keys: states, actions, params, rewards, next_states,
                    terminals, action_masks, next_action_masks
        """
        states = batch["states"]
        actions = batch["actions"]
        params = batch["params"]
        rewards = batch["rewards"]
        next_states = batch["next_states"]
        terminals = batch["terminals"]
        action_masks = batch.get("action_masks")
        next_masks = batch.get("next_action_masks")

        alpha = self.log_alpha.exp().detach()

        # --- Update Critics ---
        with torch.no_grad():
            next_action, next_params, next_log_prob = self.actor.get_action(
                next_states, next_masks
            )
            next_q1 = self.target_critic1(next_states, next_action, next_params).squeeze(-1)
            next_q2 = self.target_critic2(next_states, next_action, next_params).squeeze(-1)
            next_q = torch.min(next_q1, next_q2) - alpha * next_log_prob
            target_q = rewards + self.gamma * (1.0 - terminals) * next_q

        current_q1 = self.critic1(states, actions, params).squeeze(-1)
        current_q2 = self.critic2(states, actions, params).squeeze(-1)

        critic1_loss = F.mse_loss(current_q1, target_q)
        critic2_loss = F.mse_loss(current_q2, target_q)

        self.critic1_optimizer.zero_grad()
        critic1_loss.backward()
        torch.nn.utils.clip_grad_norm_(self.critic1.parameters(), 1.0)
        self.critic1_optimizer.step()

        self.critic2_optimizer.zero_grad()
        critic2_loss.backward()
        torch.nn.utils.clip_grad_norm_(self.critic2.parameters(), 1.0)
        self.critic2_optimizer.step()

        # --- Update Actor (delayed, every 2 steps) ---
        actor_loss_val = 0.0
        kl_loss_val = 0.0

        if self.train_step % 2 == 0:
            new_action, new_params, new_log_prob = self.actor.get_action(
                states, action_masks
            )
            q1 = self.critic1(states, new_action, new_params).squeeze(-1)
            q2 = self.critic2(states, new_action, new_params).squeeze(-1)
            min_q = torch.min(q1, q2)

            actor_loss = (alpha * new_log_prob - min_q).mean()

            # Teacher KL penalty (optional)
            if self.teacher_actor is not None and self.teacher_kl_coeff > 0:
                with torch.no_grad():
                    teacher_logits, _ = self.teacher_actor(states, action_masks)
                    teacher_probs = F.softmax(teacher_logits, dim=-1)

                student_logits, _ = self.actor(states, action_masks)
                student_log_probs = F.log_softmax(student_logits, dim=-1)

                kl = F.kl_div(student_log_probs, teacher_probs, reduction="batchmean")
                actor_loss = actor_loss + self.teacher_kl_coeff * kl
                kl_loss_val = kl.item()

            self.actor_optimizer.zero_grad()
            actor_loss.backward()
            torch.nn.utils.clip_grad_norm_(self.actor.parameters(), 1.0)
            self.actor_optimizer.step()
            actor_loss_val = actor_loss.item()

        # --- Update Alpha (entropy temperature) ---
        _, _, log_prob = self.actor.get_action(states.detach(), action_masks)
        alpha_loss = -(self.log_alpha * (log_prob.detach() + self.target_entropy)).mean()

        self.alpha_optimizer.zero_grad()
        alpha_loss.backward()
        self.alpha_optimizer.step()

        # --- Soft update target critics ---
        self.train_step += 1
        with torch.no_grad():
            for p_targ, p in zip(self.target_critic1.parameters(), self.critic1.parameters()):
                p_targ.data.mul_(1 - self.tau).add_(p.data * self.tau)
            for p_targ, p in zip(self.target_critic2.parameters(), self.critic2.parameters()):
                p_targ.data.mul_(1 - self.tau).add_(p.data * self.tau)

        return {
            "critic1_loss": critic1_loss.item(),
            "critic2_loss": critic2_loss.item(),
            "actor_loss": actor_loss_val,
            "alpha": alpha.item(),
            "alpha_loss": alpha_loss.item(),
            "mean_q": current_q1.mean().item(),
            "kl_loss": kl_loss_val,
        }

    def save(self, path: str):
        """Save all model weights."""
        torch.save({
            "actor_state_dict": self.actor.state_dict(),
            "critic1_state_dict": self.critic1.state_dict(),
            "critic2_state_dict": self.critic2.state_dict(),
            "target_critic1_state_dict": self.target_critic1.state_dict(),
            "target_critic2_state_dict": self.target_critic2.state_dict(),
            "log_alpha": self.log_alpha.data,
            "train_step": self.train_step,
        }, path)

    def load(self, path: str):
        """Load model weights."""
        checkpoint = torch.load(path, weights_only=False)
        self.actor.load_state_dict(checkpoint["actor_state_dict"])
        self.critic1.load_state_dict(checkpoint["critic1_state_dict"])
        self.critic2.load_state_dict(checkpoint["critic2_state_dict"])
        self.target_critic1.load_state_dict(checkpoint["target_critic1_state_dict"])
        self.target_critic2.load_state_dict(checkpoint["target_critic2_state_dict"])
        self.log_alpha.data = checkpoint["log_alpha"]
        self.train_step = checkpoint.get("train_step", 0)

    def export_actor_onnx(self, path: str):
        """Export just the actor to ONNX for C# inference."""
        self.actor.eval()

        # Wrapper that returns action logits and params in a flat format
        class ActorONNX(nn.Module):
            def __init__(self, actor):
                super().__init__()
                self.trunk = actor.trunk
                self.action_head = actor.action_head
                self.param_head = actor.param_head

            def forward(self, state):
                features = self.trunk(state)
                logits = self.action_head(features)
                raw_params = self.param_head(features)
                params = torch.tanh(raw_params)  # (batch, 120)
                return logits, params

        wrapper = ActorONNX(self.actor)
        dummy = torch.randn(1, STATE_DIM)

        torch.onnx.export(
            wrapper, dummy, path,
            input_names=["state_vector"],
            output_names=["action_logits", "action_params"],
            dynamic_axes={
                "state_vector": {0: "batch"},
                "action_logits": {0: "batch"},
                "action_params": {0: "batch"},
            },
            opset_version=17,
        )

        import onnx
        onnx.checker.check_model(onnx.load(path))
        print(f"  ✓ Exported actor ONNX: {path}")


# ============================================================
# Replay Buffer for SAC (stores continuous params too)
# ============================================================

class GeometryReplayBuffer:
    """Experience replay storing (state, action, params, reward, next_state, terminal, masks)."""

    def __init__(self, capacity: int = 100000):
        self.capacity = capacity
        self.buffer = []
        self.pos = 0

    def add(self, state, action, params, reward, next_state, terminal,
            action_mask=None, next_mask=None):
        experience = {
            "state": np.array(state, dtype=np.float32),
            "action": int(action),
            "params": np.array(params, dtype=np.float32),
            "reward": float(reward),
            "next_state": np.array(next_state, dtype=np.float32),
            "terminal": float(terminal),
            "action_mask": np.array(action_mask, dtype=bool) if action_mask is not None else np.ones(ACTION_DIM, dtype=bool),
            "next_mask": np.array(next_mask, dtype=bool) if next_mask is not None else np.ones(ACTION_DIM, dtype=bool),
        }

        if len(self.buffer) < self.capacity:
            self.buffer.append(experience)
        else:
            self.buffer[self.pos] = experience
        self.pos = (self.pos + 1) % self.capacity

    @property
    def size(self):
        return len(self.buffer)

    def sample(self, batch_size: int) -> dict:
        indices = np.random.choice(len(self.buffer), size=batch_size, replace=False)
        batch = [self.buffer[i] for i in indices]

        return {
            "states": torch.FloatTensor(np.array([b["state"] for b in batch])),
            "actions": torch.LongTensor(np.array([b["action"] for b in batch])),
            "params": torch.FloatTensor(np.array([b["params"] for b in batch])),
            "rewards": torch.FloatTensor(np.array([b["reward"] for b in batch])),
            "next_states": torch.FloatTensor(np.array([b["next_state"] for b in batch])),
            "terminals": torch.FloatTensor(np.array([b["terminal"] for b in batch])),
            "action_masks": torch.BoolTensor(np.array([b["action_mask"] for b in batch])),
            "next_action_masks": torch.BoolTensor(np.array([b["next_mask"] for b in batch])),
        }
