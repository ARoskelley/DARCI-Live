# DARCI v4 — Training Scripts

Neural decision network training pipeline for DARCI's behavioral intelligence.

## Where These Go

Place this entire folder in the DARCI repo:

```
DARCI-Live/
├── DARCI-v4/
│   ├── Darci.Brain/
│   ├── Darci.Core/
│   ├── Darci.Brain.Training/       ← THIS FOLDER
│   │   ├── README.md
│   │   ├── requirements.txt
│   │   ├── train_behavioral_cloning.py
│   │   ├── train_reward_model.py
│   │   ├── train_dqn.py
│   │   ├── simulate.py
│   │   └── models/                  ← created by scripts (gitignored)
│   │       ├── darci_teacher.onnx
│   │       ├── darci_teacher.pt
│   │       ├── darci_policy.onnx
│   │       ├── darci_policy.pt
│   │       └── reward_model.onnx
│   └── ARCHITECTURE.md
└── CLAUDE.md
```

## Setup

```bash
cd DARCI-v4/Darci.Brain.Training
pip install -r requirements.txt
```

## Pipeline — Run In Order

### Step 1: Behavioral Cloning (once you have ~200+ decisions logged)

Trains a network to replicate the v3 priority-ladder decisions. This
becomes the "frozen teacher" that anchors all subsequent training.

```bash
python train_behavioral_cloning.py --db ../../darci.db
```

**Outputs:** `models/darci_teacher.onnx`, `models/darci_teacher.pt`

**When to run:** After DARCI v4 has been running for a few days/weeks
and the decision_log table has 200+ entries. More data = better teacher.

**What to look for:** Validation accuracy >90% means the network has
learned the priority ladder well. Below 80% suggests the ladder has
inconsistent patterns or you need more data.

### Step 2: Reward Model (bootstrap immediately, refine with preferences later)

Trains a reward model from the hand-coded reward table (bootstrap),
then optionally fine-tunes from human preference comparisons (PEBBLE approach).

```bash
# Bootstrap from reward table (no data needed)
python train_reward_model.py --db ../../darci.db --mode bootstrap

# Later, after you've labeled some preference pairs:
python train_reward_model.py --db ../../darci.db --mode both
```

**Outputs:** `models/reward_model.onnx`, `models/reward_model.pt`

**When to run:** Bootstrap mode can run immediately. Preference mode
needs at least 10 labeled pairs in the `preferences` table.

### Step 3: DQN Training (after steps 1 and 2)

Trains the actual decision policy via Deep Q-Learning, using:
- Teacher data as a permanent anchor (RLPD mixed batches)
- KL penalty toward the teacher to prevent catastrophic forgetting
- Teacher-initialized Q-values for a strong head start
- Learned reward model for nuanced reward signal

```bash
# Basic (uses stored rewards from experience buffer)
python train_dqn.py --db ../../darci.db --teacher models/darci_teacher.pt

# With learned reward model (recommended)
python train_dqn.py --db ../../darci.db \
  --teacher models/darci_teacher.pt \
  --reward-model models/reward_model.pt
```

**Outputs:** `models/darci_policy.onnx`, `models/darci_policy.pt`

**When to run:** After steps 1 and 2, and ideally after the experience
buffer has 500+ entries with reward signals.

**What to look for:**
- Teacher agreement >85%: DQN internalized teacher behavior
- Teacher agreement 60-85%: DQN developing its own strategy (good)
- Teacher agreement <60%: May be diverging too fast, check KL coeff

### Step 4: Simulation Training (optional accelerator)

Runs thousands of episodes in a simulated DARCI environment for
accelerated learning. Doesn't need real data.

```bash
# Basic simulation
python simulate.py train --episodes 1000

# With teacher seeding (recommended)
python simulate.py train --teacher models/darci_teacher.pt --episodes 5000

# Generate preference pairs for human labeling
python simulate.py preferences --db ../../darci.db --n-pairs 50
```

**When to run:** Anytime after step 1. More useful as a supplement
to real data, not a replacement for it.

## Deploying the ONNX Model

After training, copy the ONNX file to where the C# runtime expects it:

```bash
cp models/darci_policy.onnx ../Darci.Brain/Models/darci_policy.onnx
```

Claude Code will wire this into the `OnnxDecisionNetwork` implementation
that loads and runs inference via ONNX Runtime. The C# side needs:

1. `OnnxDecisionNetwork` class implementing `IDecisionNetwork`
2. Load `darci_policy.onnx` at startup
3. `Predict()` feeds state vector, gets logits back
4. `SelectAction()` applies mask + softmax + epsilon-greedy
5. `Decision.cs` switches from `RunPriorityLadder()` to network

## Preference Labeling

The reward model improves from human preference comparisons. The workflow:

1. Run `simulate.py preferences --db darci.db` to generate pairs
2. Pairs are stored in the `preferences` table with `preferred = -1` (unlabeled)
3. Review each pair and set `preferred` to 0 (A better), 1 (B better), or 2 (tie)
4. Re-run `train_reward_model.py --mode preferences` to update the model

A future API endpoint (`/brain/preferences`) can make this a nicer UI,
but direct SQLite updates work fine for now.

## Key Research References

These scripts implement techniques from:

- **RLPD** (Ball et al.): Mixed offline/online batches, seeding replay with prior data
- **PEBBLE** (Lee et al.): Preference-based RL with active querying
- **Christiano et al.**: Deep RL from human preferences (Bradley-Terry loss)
- **Warm-STAGGER** (Li & Zhang): Hybrid BC + interactive learning
- **Conservative offline RL**: KL penalty toward teacher, Q-value initialization
