# DARCI v4 — Geometry Engineering Training

Training pipeline for DARCI's Geometry Workbench neural network.
Teaches DARCI to manipulate 3D geometry through numerical actions.

## Where These Go

```
DARCI-Live/
├── DARCI-v4/
│   ├── Darci.Engineering.Workbench/   ← The Python service (already built)
│   ├── Darci.Engineering.Training/    ← THIS FOLDER
│   │   ├── README.md
│   │   ├── requirements.txt
│   │   ├── geometry_network.py        ← SAC network architecture
│   │   ├── generate_training_parts.py ← Scenario generator
│   │   ├── train_geometry_bc.py       ← Behavioral cloning (teacher)
│   │   ├── train_geometry_sac.py      ← SAC training (main)
│   │   ├── scenarios/                 ← Generated training scenarios
│   │   ├── demos/                     ← Behavioral cloning demonstrations
│   │   └── models/                    ← Trained ONNX models
│   └── Darci.Brain.Training/          ← Behavioral decision training (already built)
```

## Setup

```bash
cd DARCI-v4/Darci.Engineering.Training
pip install -r requirements.txt
```

CadQuery must be installed. Verify: `python -c "import cadquery; print('OK')"`

## Pipeline — Run In Order

### Step 1: Generate Training Scenarios

Creates diverse geometric challenges: perturbation repair, constrained
design, and shape matching. Produces STL references and a manifest.

```bash
python generate_training_parts.py --output scenarios/ --count 200
```

This takes 1-2 minutes. Generates ~200 scenarios across three types.

### Step 2: Generate Expert Demonstrations (Behavioral Cloning)

Replays known-good CadQuery operation sequences with random variations.
Records (state, action, params) at each step.

```bash
python train_geometry_bc.py generate --output demos/ --variations 30
```

### Step 3: Train Teacher Network (Behavioral Cloning)

Trains the actor network to imitate the expert demonstrations.
Produces a teacher model used as KL anchor during SAC training.

```bash
python train_geometry_bc.py train --demos demos/ --output models/
```

**Outputs:** `models/geometry_teacher.onnx`, `models/geometry_teacher.pt`

**What to look for:** Validation accuracy >70% means the network learned
the basic operation patterns. Don't expect 95%+ like behavioral — geometry
has more variation and the demonstrations are intentionally noisy.

### Step 4: SAC Training (Main Training)

Trains the full policy via Soft Actor-Critic. Runs episodes against
the workbench engine directly (no HTTP, for speed).

```bash
# Without teacher (pure RL)
python train_geometry_sac.py --scenarios scenarios/ --episodes 2000

# With teacher anchor (recommended)
python train_geometry_sac.py --scenarios scenarios/ --episodes 2000 \
  --teacher models/geometry_teacher.pt

# Quick test (50 episodes)
python train_geometry_sac.py --scenarios scenarios/ --episodes 50 --eval-every 10
```

**Outputs:** `models/geometry_policy.onnx`, `models/geometry_policy_best.pt`

**What to look for:**
- Avg reward trending upward over episodes
- Validation score improving at eval checkpoints
- Pass rate increasing (parts passing validation)
- Alpha (entropy) should decrease over time as the network gets more confident

**Expected training time on beefy PC:**
- 500 episodes: ~10-15 minutes
- 2000 episodes: ~45-60 minutes
- 5000 episodes: ~2-3 hours

### Step 5: Deploy

Copy the ONNX model to where the C# runtime expects it:

```bash
copy models\geometry_policy.onnx ..\Darci.Engineering\Models\geometry_policy.onnx
```

The C# `EngineeringOrchestrator` will load this via an `IEngineeringNetwork`
implementation (future Claude Code task).

## Architecture Summary

**Network:** Soft Actor-Critic (SAC) with hybrid discrete-continuous actions
- State: 64 dimensions (geometry + printability + mesh quality + constraints)
- Actions: 20 discrete (add_box, fillet, cut, shell, hole, etc.)
- Parameters: 6 continuous per action (position, radius, angle, etc.)
- Architecture: 64→256→128 trunk, action head (→20), param head (→120)
- ~85,000 parameters total

**Why SAC instead of DQN:**
DQN only handles discrete actions. SAC naturally handles the hybrid
discrete + continuous action space (which action AND what parameters).
It also has entropy regularization which encourages exploration —
critical in the vast geometric search space.

**Three scenario types:**
1. Perturbation: Fix a damaged part to match its original (Hausdorff distance)
2. Constrained: Modify a primitive to satisfy engineering specs
3. Matching: Transform a box into a target shape

## Key Differences from Behavioral Training

| Aspect | Behavioral (Brain) | Engineering (Geometry) |
|--------|-------------------|----------------------|
| State dim | 28 | 64 |
| Actions | 10 discrete | 20 discrete + 6 continuous params |
| Algorithm | DQN | SAC |
| Network size | ~4K params | ~85K params |
| Training data | Real DARCI decisions | Simulated workbench episodes |
| Reward source | User interaction outcomes | Geometric quality metrics |
