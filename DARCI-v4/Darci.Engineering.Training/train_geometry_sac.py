#!/usr/bin/env python3
"""
DARCI v4 — Geometry SAC Training
==================================
Trains the geometry decision network via Soft Actor-Critic using the
workbench engine directly (no HTTP, for speed during training).

Runs training scenarios from the scenario generator, collects experience,
and trains the SAC agent. Exports ONNX model for C# runtime.

Usage:
  # Generate scenarios first
  python generate_training_parts.py --output scenarios/ --count 200

  # Train from scenarios
  python train_geometry_sac.py --scenarios scenarios/ --episodes 2000

  # Train with teacher (BC model) for KL penalty
  python train_geometry_sac.py --scenarios scenarios/ --episodes 2000 --teacher models/geometry_teacher.pt

  # Quick test run
  python train_geometry_sac.py --scenarios scenarios/ --episodes 50 --eval-every 10

Requirements:
  pip install torch numpy cadquery trimesh
"""

import argparse
import json
import os
import sys
import time
from pathlib import Path
from collections import deque

import numpy as np
import torch

# Add workbench to path
WORKBENCH_DIR = os.path.join(os.path.dirname(__file__), "..", "Darci.Engineering.Workbench")
sys.path.insert(0, WORKBENCH_DIR)

from workbench.engine import GeometryEngine
from geometry_network import (
    GeometrySACAgent, GeometryReplayBuffer,
    STATE_DIM, ACTION_DIM, PARAM_DIM,
)

import cadquery as cq


# ============================================================
# Training Configuration
# ============================================================

class TrainConfig:
    # SAC hyperparameters
    lr_actor: float = 3e-4
    lr_critic: float = 3e-4
    gamma: float = 0.95
    tau: float = 0.005
    initial_alpha: float = 0.2

    # Training loop
    batch_size: int = 128
    min_buffer_size: int = 500
    updates_per_step: int = 2
    max_steps_per_episode: int = 30

    # Reward scaling
    reward_scale: float = 1.0
    completion_bonus: float = 3.0
    failure_penalty: float = -2.0

    # Teacher KL (for BC→RL transition)
    teacher_kl_coeff: float = 0.0   # set >0 if teacher loaded

    # Logging
    eval_every: int = 50
    save_every: int = 100
    log_every: int = 10


# ============================================================
# Scenario Loader
# ============================================================

def load_scenarios(scenarios_dir: str) -> list:
    """Load training scenarios from manifest."""
    manifest_path = os.path.join(scenarios_dir, "manifest.json")
    if not os.path.exists(manifest_path):
        print(f"✗ No manifest.json found in {scenarios_dir}")
        print(f"  Run: python generate_training_parts.py --output {scenarios_dir}")
        return []

    with open(manifest_path) as f:
        scenarios = json.load(f)

    print(f"  ✓ Loaded {len(scenarios)} scenarios from {manifest_path}")
    return scenarios


def setup_scenario(engine: GeometryEngine, scenario: dict):
    """
    Initialize the engine for a training scenario.

    Parses the start_geometry code and loads reference if specified.
    """
    engine.reset(
        reference_path=scenario.get("reference_stl"),
        constraints=scenario.get("constraints", {}),
        targets=scenario.get("targets", {}),
    )

    start_code = scenario.get("start_geometry", "")
    if not start_code:
        return

    try:
        # Parse simple geometry commands
        if start_code.startswith("box("):
            dims = start_code[4:-1].split(",")
            dims = [float(d.strip()) for d in dims]
            wp = cq.Workplane("XY").box(*dims[:3])
            engine.current_workplane = wp
            engine._update_mesh()

        elif start_code.startswith("cylinder("):
            dims = start_code[9:-1].split(",")
            dims = [float(d.strip()) for d in dims]
            wp = cq.Workplane("XY").cylinder(dims[1], dims[0])
            engine.current_workplane = wp
            engine._update_mesh()

        elif start_code.startswith("load_stl("):
            path = start_code[10:-2]  # strip load_stl(' ... ')
            if os.path.exists(path):
                import trimesh
                mesh = trimesh.load(path)
                engine._mesh = mesh
                engine.mesh_analyzer = engine.mesh_analyzer.__class__(mesh) if engine.mesh_analyzer else None
                # Note: no CadQuery workplane for loaded STLs
                # The network can still analyze and get state, but
                # CadQuery operations won't work. For STL scenarios,
                # we start with a box approximation instead.
                bbox = mesh.bounding_box.extents
                wp = cq.Workplane("XY").box(float(bbox[0]), float(bbox[1]), float(bbox[2]))
                engine.current_workplane = wp
                engine._update_mesh()

    except Exception as e:
        # If setup fails, start with a default box
        wp = cq.Workplane("XY").box(20, 15, 10)
        engine.current_workplane = wp
        engine._update_mesh()


# ============================================================
# Episode Runner
# ============================================================

def run_episode(
    agent: GeometrySACAgent,
    engine: GeometryEngine,
    scenario: dict,
    buffer: GeometryReplayBuffer,
    config: TrainConfig,
    training: bool = True,
) -> dict:
    """
    Run one training episode.

    Returns episode metrics dict.
    """
    setup_scenario(engine, scenario)
    max_steps = min(scenario.get("max_steps", 50), config.max_steps_per_episode)

    episode_reward = 0.0
    episode_steps = 0
    successes = 0
    failures = 0
    finalized = False

    state = engine.get_state()
    action_mask = engine.get_action_mask()

    for step in range(max_steps):
        # Select action
        action_id, params = agent.select_action(
            state, action_mask, deterministic=not training
        )

        # Execute on workbench
        result = engine.execute_action(action_id, params)

        next_state = np.array(result["state"], dtype=np.float32)
        success = result["success"]
        reward_components = result.get("reward_components", {})

        # Compute total reward from components
        reward = sum(reward_components.values()) * config.reward_scale

        # Check for finalize action
        if action_id == 19:  # finalize
            validation = engine.validate()
            if validation["passed"]:
                reward += config.completion_bonus
                finalized = True
            else:
                reward += config.failure_penalty

        terminal = finalized or step == max_steps - 1

        # Track metrics
        episode_reward += reward
        episode_steps += 1
        if success:
            successes += 1
        else:
            failures += 1

        # Get next action mask
        next_mask = engine.get_action_mask()

        # Store in buffer
        if training and buffer is not None:
            buffer.add(
                state, action_id, params, reward,
                next_state, terminal, action_mask, next_mask
            )

        state = next_state
        action_mask = next_mask

        if terminal:
            break

    # Final validation for metrics
    final_validation = engine.validate()

    return {
        "reward": episode_reward,
        "steps": episode_steps,
        "successes": successes,
        "failures": failures,
        "finalized": finalized,
        "validation_passed": final_validation["passed"],
        "validation_score": final_validation["overall_score"],
        "scenario_type": scenario.get("scenario_type", "unknown"),
    }


# ============================================================
# Training Loop
# ============================================================

def train(
    scenarios_dir: str,
    output_dir: str = "./models",
    n_episodes: int = 2000,
    teacher_path: str = None,
    seed: int = 42,
    eval_every: int = None,
    save_every: int = None,
):
    torch.manual_seed(seed)
    np.random.seed(seed)

    output_dir = Path(output_dir)
    output_dir.mkdir(parents=True, exist_ok=True)

    config = TrainConfig()
    if eval_every:
        config.eval_every = eval_every
    if save_every:
        config.save_every = save_every

    # Load scenarios
    print("Loading training scenarios...")
    scenarios = load_scenarios(scenarios_dir)
    if not scenarios:
        return

    # Initialize agent
    print("\nInitializing SAC agent...")
    agent_kwargs = {
        "lr_actor": config.lr_actor,
        "lr_critic": config.lr_critic,
        "gamma": config.gamma,
        "tau": config.tau,
        "initial_alpha": config.initial_alpha,
    }

    if teacher_path and Path(teacher_path).exists():
        agent_kwargs["teacher_kl_coeff"] = 0.5
        config.teacher_kl_coeff = 0.5

    agent = GeometrySACAgent(**agent_kwargs)

    if teacher_path and Path(teacher_path).exists():
        agent.load_teacher(teacher_path)

    # Count parameters
    total_params = sum(p.numel() for p in agent.actor.parameters())
    total_params += sum(p.numel() for p in agent.critic1.parameters())
    total_params += sum(p.numel() for p in agent.critic2.parameters())
    print(f"  Total parameters: {total_params:,}")

    # Replay buffer
    buffer = GeometryReplayBuffer(capacity=100000)

    # Engine (reused across episodes)
    engine = GeometryEngine()

    # Metrics tracking
    episode_rewards = deque(maxlen=100)
    episode_scores = deque(maxlen=100)
    best_avg_reward = -float("inf")
    best_avg_score = -float("inf")

    print(f"\nTraining for {n_episodes} episodes...")
    print(f"  Scenarios: {len(scenarios)}")
    print(f"  Batch size: {config.batch_size}")
    print(f"  Updates per step: {config.updates_per_step}")
    print(f"  Max steps per episode: {config.max_steps_per_episode}")
    print("-" * 75)

    start_time = time.time()
    train_metrics_accum = []

    for episode in range(1, n_episodes + 1):
        # Pick a random scenario
        scenario = scenarios[np.random.randint(len(scenarios))]

        # Run episode
        ep_metrics = run_episode(agent, engine, scenario, buffer, config, training=True)
        episode_rewards.append(ep_metrics["reward"])
        episode_scores.append(ep_metrics["validation_score"])

        # Train if buffer is large enough
        if buffer.size >= config.min_buffer_size:
            n_updates = ep_metrics["steps"] * config.updates_per_step
            print(f"  [upd] ep={episode} buf={buffer.size} n_upd={max(n_updates, 1)}", flush=True)
            train_metrics_accum = []
            for update_i in range(max(n_updates, 1)):
                batch = buffer.sample(config.batch_size)
                metrics = agent.update(batch)
                train_metrics_accum.append(metrics)
            print("  [upd] done", flush=True)

        # Logging
        if episode % config.log_every == 0:
            avg_reward = np.mean(episode_rewards)
            avg_score = np.mean(episode_scores)
            elapsed = time.time() - start_time
            eps_per_sec = episode / elapsed

            is_best = avg_reward > best_avg_reward
            marker = " ★" if is_best else ""

            if is_best:
                best_avg_reward = avg_reward

            # Get latest training metrics
            if train_metrics_accum:
                last_train = train_metrics_accum[-1]
                alpha_str = f"α: {last_train.get('alpha', 0):.3f}"
                q_str = f"Q̄: {last_train.get('mean_q', 0):+.2f}"
            else:
                alpha_str = "α: ---"
                q_str = "Q̄: ---"

            print(
                f"  Ep {episode:5d} | "
                f"R: {ep_metrics['reward']:+6.1f} | "
                f"Avg: {avg_reward:+6.1f} | "
                f"Score: {avg_score:.2f} | "
                f"{alpha_str} | {q_str} | "
                f"Buf: {buffer.size:,} | "
                f"{eps_per_sec:.1f} ep/s"
                f"{marker}"
            )

        # Periodic evaluation
        if episode % config.eval_every == 0:
            eval_results = evaluate(agent, engine, scenarios, config, n_eval=20)
            print(
                f"        EVAL | "
                f"Avg R: {eval_results['avg_reward']:+.1f} | "
                f"Score: {eval_results['avg_score']:.2f} | "
                f"Pass: {eval_results['pass_rate']*100:.0f}% | "
                f"Steps: {eval_results['avg_steps']:.0f}"
            )

            if eval_results["avg_score"] > best_avg_score:
                best_avg_score = eval_results["avg_score"]
                save_path = str(output_dir / "geometry_policy_best.pt")
                agent.save(save_path)
                agent.export_actor_onnx(str(output_dir / "geometry_policy.onnx"))
                print(f"        → Saved best model (score: {best_avg_score:.3f})")

        # Periodic save
        if episode % config.save_every == 0:
            agent.save(str(output_dir / "geometry_policy_latest.pt"))

    # Final save
    print("-" * 75)
    agent.save(str(output_dir / "geometry_policy_final.pt"))
    agent.export_actor_onnx(str(output_dir / "geometry_policy.onnx"))

    elapsed = time.time() - start_time
    print(f"\nTraining complete in {elapsed:.0f}s ({elapsed/60:.1f} min)")
    print(f"  Final avg reward: {np.mean(episode_rewards):+.1f}")
    print(f"  Final avg score: {np.mean(episode_scores):.3f}")
    print(f"  Best avg score: {best_avg_score:.3f}")
    print(f"\nOutputs:")
    print(f"  {output_dir / 'geometry_policy.onnx'}  — for C# ONNX Runtime")
    print(f"  {output_dir / 'geometry_policy_best.pt'} — best checkpoint")
    print(f"  {output_dir / 'geometry_policy_final.pt'} — final checkpoint")


# ============================================================
# Evaluation
# ============================================================

def evaluate(
    agent: GeometrySACAgent,
    engine: GeometryEngine,
    scenarios: list,
    config: TrainConfig,
    n_eval: int = 20,
) -> dict:
    """Run deterministic evaluation on random scenarios."""
    rewards = []
    scores = []
    pass_count = 0
    steps_list = []

    indices = np.random.choice(len(scenarios), size=min(n_eval, len(scenarios)), replace=False)

    for idx in indices:
        scenario = scenarios[idx]
        ep = run_episode(agent, engine, scenario, None, config, training=False)

        # run_episode with training=False doesn't add to buffer
        # but we passed None for buffer, need to handle that
        rewards.append(ep["reward"])
        scores.append(ep["validation_score"])
        steps_list.append(ep["steps"])
        if ep["validation_passed"]:
            pass_count += 1

    return {
        "avg_reward": float(np.mean(rewards)),
        "avg_score": float(np.mean(scores)),
        "pass_rate": pass_count / max(len(indices), 1),
        "avg_steps": float(np.mean(steps_list)),
    }


# ============================================================
# Entry Point
# ============================================================

if __name__ == "__main__":
    parser = argparse.ArgumentParser(
        description="DARCI v4 — Geometry SAC Training"
    )
    parser.add_argument("--scenarios", required=True, help="Path to scenarios directory")
    parser.add_argument("--output", default="./models", help="Output directory")
    parser.add_argument("--episodes", type=int, default=2000)
    parser.add_argument("--teacher", default=None, help="Path to teacher .pt (optional)")
    parser.add_argument("--seed", type=int, default=42)
    parser.add_argument("--eval-every", type=int, default=None)
    parser.add_argument("--save-every", type=int, default=None)

    args = parser.parse_args()

    train(
        scenarios_dir=args.scenarios,
        output_dir=args.output,
        n_episodes=args.episodes,
        teacher_path=args.teacher,
        seed=args.seed,
        eval_every=args.eval_every,
        save_every=args.save_every,
    )
