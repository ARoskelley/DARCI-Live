#!/usr/bin/env python3
"""
DARCI v4 — Environment Simulator
===================================
Phase 3: A lightweight simulation of DARCI's world for accelerated training.

Instead of waiting for real interactions (slow, maybe 50 decisions/day),
the simulator generates synthetic states and approximates outcomes,
letting the DQN train on thousands of episodes in minutes.

The simulator also generates preference pairs for reward model training
(PEBBLE active querying approach).

Key design principle: the simulator doesn't need to be perfect.
Even a rough model of "messages arrive, goals progress, users respond"
provides useful training signal. The real world will refine what the
simulator gets wrong during online Phase 3.

Usage:
  python simulate.py --episodes 1000 --output models/
  python simulate.py --episodes 5000 --teacher models/darci_teacher.pt
  python simulate.py --generate-preferences --db darci.db --n-pairs 50

Requirements:
  pip install torch numpy
"""

import argparse
import json
import sqlite3
from pathlib import Path
from dataclasses import dataclass, field

import numpy as np
import torch

from train_behavioral_cloning import DarciDecisionNetwork
from train_dqn import DarciDQNAgent, DQNConfig, MixedReplayBuffer

# ============================================================
# Simulated DARCI Environment
# ============================================================

@dataclass
class SimState:
    """Mutable state tracking for the simulator."""
    # Internal
    energy: float = 0.7
    focus: float = 0.5
    engagement: float = 0.3
    mood_valence: float = 0.2
    mood_intensity: float = 0.4
    confidence: float = 0.6
    warmth: float = 0.5
    curiosity: float = 0.5
    
    # Situational
    messages_waiting: int = 0
    has_urgent: bool = False
    hours_since_user: float = 0.0
    minutes_since_action: float = 0.0
    active_goals: int = 0
    goals_with_pending: int = 0
    pending_memories: int = 0
    completed_tasks: int = 0
    is_quiet_hours: bool = False
    current_goal_active: bool = False
    consecutive_rests: int = 0
    trust_level: float = 0.6
    
    # Message context (only meaningful when messages_waiting > 0)
    msg_length: float = 0.0
    msg_has_question: bool = False
    msg_sentiment: float = 0.0
    intent_conversation: float = 0.0
    intent_request: float = 0.0
    intent_research: float = 0.0
    intent_feedback: float = 0.0
    memory_relevance: float = 0.0
    research_topic_confidence: float = 0.5
    
    # Tracking
    step: int = 0
    total_reward: float = 0.0
    
    def to_vector(self) -> np.ndarray:
        """Convert to the 29-dim normalized state vector."""
        v = np.zeros(29, dtype=np.float32)
        
        # Internal (0-7)
        v[0] = np.clip(self.energy, 0, 1)
        v[1] = np.clip(self.focus, 0, 1)
        v[2] = np.clip(self.engagement, 0, 1)
        v[3] = np.clip(self.mood_valence, -1, 1)
        v[4] = np.clip(self.mood_intensity, 0, 1)
        v[5] = np.clip(self.confidence, 0, 1)
        v[6] = np.clip(self.warmth, 0, 1)
        v[7] = np.clip(self.curiosity, 0, 1)
        
        # Situational (8-19)
        v[8]  = np.clip(self.messages_waiting / 10.0, 0, 1)
        v[9]  = 1.0 if self.has_urgent else 0.0
        v[10] = np.clip(self.hours_since_user / 24.0, 0, 1)
        v[11] = np.clip(self.minutes_since_action / 60.0, 0, 1)
        v[12] = np.clip(self.active_goals / 10.0, 0, 1)
        v[13] = np.clip(self.goals_with_pending / max(self.active_goals, 1), 0, 1)
        v[14] = np.clip(self.pending_memories / 20.0, 0, 1)
        v[15] = np.clip(self.completed_tasks / 5.0, 0, 1)
        v[16] = 1.0 if self.is_quiet_hours else 0.0
        v[17] = 1.0 if self.current_goal_active else 0.0
        v[18] = np.clip(self.consecutive_rests / 100.0, 0, 1)
        v[19] = np.clip(self.trust_level, 0, 1)
        
        # Message context (20-27) — zero if no messages
        if self.messages_waiting > 0:
            v[20] = np.clip(self.msg_length, 0, 1)
            v[21] = 1.0 if self.msg_has_question else 0.0
            v[22] = np.clip(self.msg_sentiment, -1, 1)
            v[23] = self.intent_conversation
            v[24] = self.intent_request
            v[25] = self.intent_research
            v[26] = self.intent_feedback
            v[27] = np.clip(self.memory_relevance, 0, 1)

        v[28] = np.clip(self.research_topic_confidence, 0, 1)
        
        return v
    
    def get_action_mask(self) -> np.ndarray:
        """Which actions are valid in this state (matches ARCHITECTURE.md §4.3)."""
        mask = np.ones(10, dtype=bool)
        
        if self.messages_waiting == 0:
            mask[1] = False  # can't reply
            mask[8] = False  # can't notify (no context)
        
        if self.active_goals == 0:
            mask[4] = False  # can't work on goal
        
        if self.pending_memories == 0:
            mask[7] = False  # can't consolidate
        
        if self.energy < 0.2:
            mask[2] = False  # research too expensive
            mask[9] = False  # thinking too expensive

        if self.research_topic_confidence < 0.4:
            mask[2] = self.energy > 0.1
        
        if self.is_quiet_hours:
            mask[8] = False  # don't notify during quiet hours
        
        return mask


class DarciSimulator:
    """
    Simulates DARCI's world at an abstract level.
    
    The simulator models:
    - Messages arriving stochastically
    - Goals being created and progressing
    - Energy/mood dynamics
    - User responses to DARCI's actions
    - Time passing between steps
    """
    
    def __init__(self, seed: int = None):
        self.rng = np.random.RandomState(seed)
        self.state = SimState()
    
    def reset(self) -> np.ndarray:
        """Reset to a random plausible starting state."""
        s = SimState()
        
        # Randomize starting conditions
        s.energy = self.rng.uniform(0.4, 0.9)
        s.focus = self.rng.uniform(0.3, 0.7)
        s.mood_valence = self.rng.uniform(-0.2, 0.5)
        s.mood_intensity = self.rng.uniform(0.2, 0.6)
        s.confidence = self.rng.uniform(0.4, 0.8)
        s.warmth = self.rng.uniform(0.4, 0.8)
        s.curiosity = self.rng.uniform(0.3, 0.7)
        s.trust_level = self.rng.uniform(0.5, 0.9)
        
        # Maybe start with some messages/goals
        if self.rng.random() > 0.3:
            s.messages_waiting = self.rng.randint(1, 4)
            s.has_urgent = self.rng.random() > 0.7
            self._generate_message_context(s)
        
        s.active_goals = self.rng.randint(0, 4)
        s.goals_with_pending = min(s.active_goals, self.rng.randint(0, 3))
        s.pending_memories = self.rng.randint(0, 5)
        
        s.is_quiet_hours = self.rng.random() > 0.85
        s.hours_since_user = self.rng.uniform(0, 8)
        
        self.state = s
        return s.to_vector()
    
    def step(self, action: int) -> tuple[np.ndarray, float, bool]:
        """
        Execute an action and return (next_state, reward, done).
        
        Models the approximate consequences of each action on DARCI's
        internal state and environment.
        """
        s = self.state
        s.step += 1
        reward = 0.0
        done = s.step >= 200  # episode length
        
        # Time passes (1-5 simulated minutes per step)
        time_delta = self.rng.uniform(1, 5)
        s.minutes_since_action = time_delta
        s.hours_since_user += time_delta / 60.0
        
        # === Action effects ===
        
        if action == 0:  # Rest
            s.consecutive_rests += 1
            s.energy = min(1.0, s.energy + 0.05)
            s.focus = max(0.0, s.focus - 0.02)
            
            if s.messages_waiting > 0:
                reward = -1.0  # rested when messages waiting
            elif s.goals_with_pending > 0:
                reward = -0.2
            else:
                reward = 0.1
        
        elif action == 1:  # Reply
            s.consecutive_rests = 0
            if s.messages_waiting > 0:
                s.messages_waiting -= 1
                s.energy = max(0.0, s.energy - 0.08)
                s.engagement = min(1.0, s.engagement + 0.15)
                s.hours_since_user = 0.0
                
                if s.has_urgent:
                    reward = 1.5
                    s.has_urgent = False
                    s.confidence = min(1.0, s.confidence + 0.1)
                else:
                    reward = 1.0
                
                # Positive sentiment bonus
                if s.msg_sentiment > 0.3:
                    reward += 0.3
                
                # User might respond (creating new message later)
                if self.rng.random() > 0.6:
                    self._schedule_response()
                
                # Refresh message context for next message
                if s.messages_waiting > 0:
                    self._generate_message_context(s)
                else:
                    s.has_urgent = False
            else:
                reward = -0.5  # impossible action
        
        elif action == 2:  # Research
            s.consecutive_rests = 0
            s.energy = max(0.0, s.energy - 0.15)
            s.curiosity = min(1.0, s.curiosity + 0.1)
            s.focus = min(1.0, s.focus + 0.1)
            
            if s.energy < 0.2:
                reward = -0.3
            elif s.messages_waiting > 0 and s.intent_research > 0.3:
                reward = 0.8
                # Research might complete a goal step
                if s.goals_with_pending > 0 and self.rng.random() > 0.5:
                    s.goals_with_pending -= 1
                    reward += 0.3
            else:
                reward = 0.3
        
        elif action == 3:  # Create goal
            s.consecutive_rests = 0
            s.energy = max(0.0, s.energy - 0.05)
            
            if s.messages_waiting > 0 and s.intent_request > 0.3:
                s.active_goals += 1
                s.goals_with_pending += 1
                reward = 0.8
            elif s.active_goals > 8:
                reward = -0.5  # too many goals
            else:
                s.active_goals += 1
                s.goals_with_pending += 1
                reward = 0.2
        
        elif action == 4:  # Work on goal
            s.consecutive_rests = 0
            s.current_goal_active = True
            s.energy = max(0.0, s.energy - 0.1)
            s.focus = min(1.0, s.focus + 0.1)
            
            if s.active_goals > 0 and s.goals_with_pending > 0:
                # Progress on goal
                if self.rng.random() > 0.3:
                    s.goals_with_pending -= 1
                    reward = 0.6
                    
                    # Goal completed?
                    if s.goals_with_pending == 0 and self.rng.random() > 0.5:
                        s.active_goals = max(0, s.active_goals - 1)
                        s.completed_tasks += 1
                        reward = 1.5
                        s.confidence = min(1.0, s.confidence + 0.15)
                else:
                    reward = 0.2  # worked but no visible progress
            elif s.active_goals == 0:
                reward = -0.5  # impossible
            else:
                reward = 0.1
        
        elif action == 5:  # Store memory
            s.consecutive_rests = 0
            s.pending_memories += 1
            reward = 0.3
        
        elif action == 6:  # Recall memories
            s.consecutive_rests = 0
            if s.messages_waiting > 0 and s.memory_relevance > 0.3:
                reward = 0.4
                s.confidence = min(1.0, s.confidence + 0.05)
            else:
                reward = 0.1
        
        elif action == 7:  # Consolidate memories
            s.consecutive_rests = 0
            if s.pending_memories > 0:
                consolidated = min(s.pending_memories, self.rng.randint(1, 4))
                s.pending_memories -= consolidated
                reward = 0.3
            else:
                reward = -0.3  # nothing to consolidate
        
        elif action == 8:  # Notify user
            s.consecutive_rests = 0
            if s.is_quiet_hours:
                reward = -0.8
            elif s.completed_tasks > 0:
                s.completed_tasks -= 1
                reward = 0.8
                s.hours_since_user = 0.0
            else:
                reward = 0.1
        
        elif action == 9:  # Think
            s.consecutive_rests = 0
            s.energy = max(0.0, s.energy - 0.05)
            s.curiosity = min(1.0, s.curiosity + 0.05)
            
            if s.energy < 0.2:
                reward = -0.3
            elif s.messages_waiting == 0 and s.goals_with_pending == 0:
                reward = 0.4  # good time to reflect
            else:
                reward = 0.1
        
        # === Environmental dynamics ===
        
        # Energy naturally decays
        s.energy = max(0.0, s.energy - 0.01)
        
        # Mood drifts toward neutral
        s.mood_valence *= 0.98
        s.mood_intensity *= 0.97
        
        # Reward affects mood
        if reward > 0.5:
            s.mood_valence = min(1.0, s.mood_valence + 0.1)
        elif reward < -0.5:
            s.mood_valence = max(-1.0, s.mood_valence - 0.1)
        
        # New messages arrive stochastically
        if self.rng.random() > 0.92:
            s.messages_waiting += 1
            if self.rng.random() > 0.8:
                s.has_urgent = True
            self._generate_message_context(s)
        
        # Quiet hours change
        if s.step % 50 == 0:
            s.is_quiet_hours = self.rng.random() > 0.85
        
        s.total_reward += reward
        
        return s.to_vector(), reward, done
    
    def _generate_message_context(self, s: SimState):
        """Generate random but plausible message context."""
        s.msg_length = self.rng.uniform(0.05, 0.5)
        s.msg_has_question = self.rng.random() > 0.5
        s.msg_sentiment = self.rng.uniform(-0.3, 0.8)
        
        intents = self.rng.dirichlet([2, 2, 1, 1])
        s.intent_conversation = intents[0]
        s.intent_request = intents[1]
        s.intent_research = intents[2]
        s.intent_feedback = intents[3]
        
        s.memory_relevance = self.rng.uniform(0, 0.7)
        s.research_topic_confidence = self.rng.uniform(0.15, 0.85) if s.intent_research > 0.2 else 0.5
    
    def _schedule_response(self):
        """Simulate user sending a follow-up message soon."""
        # In a real simulator this would be time-delayed;
        # here we just increment the counter with some probability
        if self.rng.random() > 0.5:
            self.state.messages_waiting += 1
            self._generate_message_context(self.state)


# ============================================================
# Simulated Training Loop
# ============================================================

def run_simulation_training(
    teacher_path: str = None,
    output_dir: str = "./models",
    n_episodes: int = 1000,
    seed: int = 42,
):
    """Run accelerated training via simulation."""
    torch.manual_seed(seed)
    np.random.seed(seed)
    
    output_dir = Path(output_dir)
    config = DQNConfig()
    
    # More aggressive exploration in simulation
    config.epsilon_start = 0.4
    config.epsilon_decay_steps = n_episodes * 100
    
    agent = DarciDQNAgent(config, teacher_path)
    buffer = MixedReplayBuffer()
    env = DarciSimulator(seed=seed)
    
    # If we have a teacher, seed buffer with simulated teacher episodes
    if agent.teacher_loaded:
        print("Generating teacher episodes for buffer seeding...")
        agent.teacher_net.eval()
        for ep in range(min(100, n_episodes // 5)):
            state = env.reset()
            for _ in range(200):
                with torch.no_grad():
                    state_t = torch.FloatTensor(state).unsqueeze(0)
                    action = agent.teacher_net(state_t).argmax(dim=1).item()
                
                mask = env.state.get_action_mask()
                if not mask[action]:
                    valid = np.where(mask)[0]
                    action = int(np.random.choice(valid)) if len(valid) > 0 else 0
                
                next_state, reward, done = env.step(action)
                buffer.add_teacher(state, action, reward, next_state, done)
                state = next_state
                if done:
                    break
        
        print(f"  ✓ Seeded {buffer.teacher_size} teacher experiences")
    
    # --- Main simulation loop ---
    print(f"\nRunning {n_episodes} simulation episodes...")
    print("-" * 70)
    
    episode_rewards = []
    best_avg_reward = -float("inf")
    
    for episode in range(1, n_episodes + 1):
        state = env.reset()
        episode_reward = 0.0
        
        for step in range(200):
            mask = env.state.get_action_mask()
            action = agent.select_action(state, mask)
            
            next_state, reward, done = env.step(action)
            norm_reward = agent.reward_normalizer.normalize(reward)
            
            buffer.add_online(state, action, norm_reward, next_state, done)
            episode_reward += reward
            
            # Train periodically
            if buffer.total_size >= config.min_buffer_size and step % config.train_every == 0:
                for _ in range(config.updates_per_step):
                    agent.train_step_fn(buffer)
            
            state = next_state
            if done:
                break
        
        episode_rewards.append(episode_reward)
        
        # Log
        if episode % (n_episodes // 20) == 0 or episode == 1:
            recent = episode_rewards[-50:]
            avg = np.mean(recent)
            marker = " ★" if avg > best_avg_reward else ""
            
            if avg > best_avg_reward:
                best_avg_reward = avg
                agent.save(output_dir)
            
            print(
                f"  Episode {episode:5d} | "
                f"Reward: {episode_reward:+6.1f} | "
                f"Avg(50): {avg:+6.1f} | "
                f"ε: {agent.get_epsilon():.3f} | "
                f"Buffer: {buffer.total_size:,}"
                f"{marker}"
            )
    
    print("-" * 70)
    
    agent.save(output_dir)
    
    # Summary
    final_avg = np.mean(episode_rewards[-100:])
    initial_avg = np.mean(episode_rewards[:100])
    print(f"\nTraining complete.")
    print(f"  Initial avg reward (first 100): {initial_avg:+.1f}")
    print(f"  Final avg reward (last 100):    {final_avg:+.1f}")
    print(f"  Improvement: {final_avg - initial_avg:+.1f}")
    print(f"\nOutputs: {output_dir / 'darci_policy.onnx'}")


# ============================================================
# Preference Pair Generator (PEBBLE active querying)
# ============================================================

def generate_preference_pairs(
    db_path: str,
    n_pairs: int = 50,
    teacher_path: str = None,
    seed: int = 42,
):
    """
    Generate preference pairs by simulating two different action choices
    in the same state and rolling out short trajectories. Saves to the
    preferences table for human labeling.
    
    This is the PEBBLE active-querying approach: we generate the pairs
    where the reward model is most uncertain, then you label them.
    """
    from train_reward_model import ensure_preferences_table, DarciRewardModel
    
    ensure_preferences_table(db_path)
    env = DarciSimulator(seed=seed)
    rng = np.random.RandomState(seed)
    
    # Load reward model if available (for uncertainty-based selection)
    reward_model = None
    reward_model_path = Path("models/reward_model.pt")
    if reward_model_path.exists():
        checkpoint = torch.load(reward_model_path, weights_only=False)
        reward_model = DarciRewardModel()
        reward_model.load_state_dict(checkpoint["model_state_dict"])
        reward_model.eval()
        print(f"  ✓ Loaded reward model for uncertainty-based pair selection")
    
    pairs = []
    candidates = []
    
    # Generate many candidate pairs
    n_candidates = n_pairs * 10
    print(f"Generating {n_candidates} candidate pairs...")
    
    for _ in range(n_candidates):
        state = env.reset()
        mask = env.state.get_action_mask()
        valid_actions = np.where(mask)[0]
        
        if len(valid_actions) < 2:
            continue
        
        # Pick two different valid actions
        a, b = rng.choice(valid_actions, size=2, replace=False)
        
        # Roll out short trajectories (5 steps) for each
        reward_a = 0.0
        env_copy_state = env.state  # simplified — approximate
        for _ in range(5):
            _, r, done = env.step(int(a))
            reward_a += r
            if done:
                break
            a = int(rng.choice(np.where(env.state.get_action_mask())[0]))
        
        env.state = SimState()  # reset for second rollout
        # Approximate: use same starting state
        reward_b = 0.0
        env.reset()
        for _ in range(5):
            _, r, done = env.step(int(b))
            reward_b += r
            if done:
                break
            b_next = np.where(env.state.get_action_mask())[0]
            b = int(rng.choice(b_next)) if len(b_next) > 0 else 0
        
        # Compute uncertainty if reward model available
        uncertainty = 0.5  # default
        if reward_model is not None:
            with torch.no_grad():
                s_t = torch.FloatTensor(state).unsqueeze(0)
                r_pred_a = reward_model(s_t, torch.tensor([int(valid_actions[0])])).item()
                r_pred_b = reward_model(s_t, torch.tensor([int(valid_actions[1])])).item()
                # High uncertainty = predictions are close (model unsure which is better)
                uncertainty = 1.0 / (1.0 + abs(r_pred_a - r_pred_b))
        
        candidates.append({
            "state": state,
            "action_a": int(valid_actions[0]),
            "action_b": int(valid_actions[1]),
            "rollout_reward_a": reward_a,
            "rollout_reward_b": reward_b,
            "uncertainty": uncertainty,
        })
    
    # Select pairs with highest uncertainty (PEBBLE active querying)
    candidates.sort(key=lambda x: x["uncertainty"], reverse=True)
    selected = candidates[:n_pairs]
    
    # Save to database (without labels — human fills those in)
    conn = sqlite3.connect(db_path)
    for pair in selected:
        conn.execute("""
            INSERT INTO preferences (state_a, action_a, state_b, action_b, preferred, timestamp, notes)
            VALUES (?, ?, ?, ?, -1, datetime('now'), ?)
        """, (
            json.dumps(pair["state"].tolist()),
            pair["action_a"],
            json.dumps(pair["state"].tolist()),  # same state, different actions
            pair["action_b"],
            f"Simulated rollout rewards: A={pair['rollout_reward_a']:.2f}, B={pair['rollout_reward_b']:.2f}",
        ))
    conn.commit()
    conn.close()
    
    print(f"\n✓ Generated {len(selected)} preference pairs in the database.")
    print(f"  These need human labels (preferred = 0 for A, 1 for B, 2 for tie)")
    print(f"  Use the review endpoint or update the preferences table directly.")


# ============================================================
# Entry point
# ============================================================

if __name__ == "__main__":
    parser = argparse.ArgumentParser(
        description="DARCI v4 — Environment Simulator"
    )
    
    sub = parser.add_subparsers(dest="command", help="Command to run")
    
    # Simulation training
    train_parser = sub.add_parser("train", help="Run simulation-accelerated training")
    train_parser.add_argument("--teacher", default=None, help="Path to darci_teacher.pt")
    train_parser.add_argument("--output", default="./models", help="Output directory")
    train_parser.add_argument("--episodes", type=int, default=1000)
    train_parser.add_argument("--seed", type=int, default=42)
    
    # Preference generation
    pref_parser = sub.add_parser("preferences", help="Generate preference pairs for labeling")
    pref_parser.add_argument("--db", required=True, help="Path to DARCI's SQLite database")
    pref_parser.add_argument("--teacher", default=None, help="Path to darci_teacher.pt")
    pref_parser.add_argument("--n-pairs", type=int, default=50)
    pref_parser.add_argument("--seed", type=int, default=42)
    
    args = parser.parse_args()
    
    if args.command == "train":
        run_simulation_training(
            teacher_path=args.teacher,
            output_dir=args.output,
            n_episodes=args.episodes,
            seed=args.seed,
        )
    elif args.command == "preferences":
        generate_preference_pairs(
            db_path=args.db,
            n_pairs=args.n_pairs,
            teacher_path=args.teacher,
            seed=args.seed,
        )
    else:
        parser.print_help()
