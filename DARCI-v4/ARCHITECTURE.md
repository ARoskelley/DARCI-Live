# DARCI v4 — Neural Decision Network Architecture

## 1. The Problem: Why Hardcoded Decisions Fail

DARCI v3 uses a C# priority ladder in `Decision.cs` to choose actions. This has a hard ceiling: she can never handle a situation her developer didn't explicitly code for, can never improve her judgment through experience, and can never develop intuition about what works.

Routing decisions through English language (LLM prompts) adds latency, ambiguity, and optimizes for "plausible-sounding text" rather than "effective action." A JARVIS-like system needs to operate the way game AIs do: numerical state in, decision out, reward signal back.

### What This Document Covers

This specification defines two interconnected neural systems:

- **Behavioral Decision Network (BDN)** — replaces the hardcoded priority ladder with a neural network that learns which actions to take based on numerical state.
- **Engineering Validation Network (EVN)** — a future extension that applies the same pattern to 3D engineering tasks, evaluating fit, shape accuracy, and assembly correctness through simulation loops.

Both share a common architecture: vector state representation, discrete action spaces, and reward-driven learning. The BDN ships first and generates the training patterns that inform the EVN.

---

## 2. Architecture Overview

### 2.1 Two-Brain Design

The core insight is separating language intelligence from behavioral intelligence. The LLM (Ollama) remains DARCI's "language cortex" — it generates text, understands natural language, and summarizes information. But it no longer makes decisions about what to do. A dedicated neural network becomes DARCI's "executive cortex" — fast, numerical, and trained on outcomes rather than language patterns.

**Current Architecture (v3):**
```
Perception (mixed) → C# Priority Ladder → Action
                          │
                   if needs text → LLM generates words
```

**Proposed Architecture (v4):**
```
Perception → State Encoder → [numerical vector]
                                    │
                            Decision Network (NN)
                                    │
                             Action Selection
                              ┌─────┴─────┐
                    needs language?    behavioral?
                         │                 │
                    LLM generates     execute directly
                       text
                              └─────┬─────┘
                              Outcome → Reward Signal
                                         │
                                 Update Network Weights
```

### 2.2 Component Summary

| Component | Role | Technology | Speed |
|-----------|------|------------|-------|
| State Encoder | Converts raw perception into fixed-size numerical vector | C# (hand-coded initially, learnable later) | < 1ms |
| Decision Network | Maps state vector to action probabilities | ONNX Runtime | < 5ms |
| Action Executor | Validates and carries out chosen action | C# (existing `Darci.cs` Act method) | Varies |
| Reward Calculator | Scores outcome quality | C# rules + LLM for nuanced eval | < 10ms |
| Experience Buffer | Stores (state, action, reward, next_state) tuples | SQLite ring buffer | < 1ms |
| Training Loop | Periodically updates network weights from experience | Background service | Async |
| LLM (Ollama) | Text generation only — replies, summaries, comprehension | Ollama / gemma2:9b | 1–10s |

---

## 3. State Representation

The state vector is DARCI's complete numerical snapshot of "what's happening right now." Every value is normalized to [0, 1] or [-1, 1] so the network treats all inputs equally.

### 3.1 Internal State (8 dimensions)

These represent DARCI's own condition — the equivalent of how she "feels."

| Index | Name | Range | Description |
|-------|------|-------|-------------|
| 0 | energy | 0–1 | Current energy level (0 = exhausted) |
| 1 | focus | 0–1 | How concentrated vs scattered |
| 2 | engagement | 0–1 | How invested in current activity |
| 3 | mood_valence | -1–1 | Negative to positive mood |
| 4 | mood_intensity | 0–1 | How strongly she feels |
| 5 | confidence | 0–1 | Current self-assessed confidence |
| 6 | warmth | 0–1 | Current warmth/empathy level |
| 7 | curiosity | 0–1 | Current intellectual interest |

### 3.2 Situational Awareness (12 dimensions)

These capture what's happening in DARCI's environment.

| Index | Name | Range | Description |
|-------|------|-------|-------------|
| 8 | messages_waiting | 0–1 | Count / max_expected (e.g. count/10) |
| 9 | has_urgent | 0 or 1 | Binary: any urgent messages? |
| 10 | time_since_user_contact | 0–1 | Hours / 24, clamped |
| 11 | time_since_last_action | 0–1 | Minutes / 60, clamped |
| 12 | active_goals_count | 0–1 | Count / max_expected |
| 13 | goals_with_pending_steps | 0–1 | Count / active_goals |
| 14 | pending_memories | 0–1 | Count / max_expected |
| 15 | completed_tasks_waiting | 0–1 | Count / max_expected |
| 16 | is_quiet_hours | 0 or 1 | Binary |
| 17 | current_goal_active | 0 or 1 | Binary: working on something? |
| 18 | consecutive_rests | 0–1 | Count / 100, clamped |
| 19 | user_trust_level | 0–1 | From personality traits |

### 3.3 Message Context (8 dimensions)

When there's a message to process, these features characterize it without using English.

| Index | Name | Range | Description |
|-------|------|-------|-------------|
| 20 | msg_length_norm | 0–1 | Character count / 1000, clamped |
| 21 | msg_has_question | 0 or 1 | Contains question mark |
| 22 | msg_sentiment | -1–1 | Positive/negative sentiment score |
| 23 | msg_intent_conversation | 0–1 | Classifier confidence: conversation |
| 24 | msg_intent_request | 0–1 | Classifier confidence: action request |
| 25 | msg_intent_research | 0–1 | Classifier confidence: research |
| 26 | msg_intent_feedback | 0–1 | Classifier confidence: feedback |
| 27 | msg_memory_relevance | 0–1 | Best memory match score for this message |
| 28 | research_topic_confidence | 0–1 | Confidence in the currently relevant research topic |

**Total state vector size: 29 dimensions.** Intentionally compact. A game AI playing Atari uses thousands of dimensions (raw pixels). DARCI's world is simpler, and a small state space means faster learning with less data.

---

## 4. Action Space

The network outputs a probability distribution over discrete actions. Each action is a number, not a word.

### 4.1 Primary Actions

| Action ID | Name | Requires LLM? | Parameters (from C#) |
|-----------|------|----------------|----------------------|
| 0 | rest | No | Duration calculated from state |
| 1 | reply_to_message | Yes (text gen) | Target message from queue |
| 2 | research | Yes (query formation) | Topic from message or goal |
| 3 | create_goal | Partial (title gen) | Type/priority from context |
| 4 | work_on_goal | Maybe | Highest priority goal with pending steps |
| 5 | store_memory | No | Content from recent interaction |
| 6 | recall_memories | No | Query from context |
| 7 | consolidate_memories | No | None |
| 8 | notify_user | Yes (text gen) | Content from goal/event |
| 9 | think | Yes (reflection) | Topic from idle analysis |

### 4.2 How Action Selection Works

The network outputs 10 values (one per action). These are converted to probabilities using softmax. During early training, DARCI uses epsilon-greedy exploration: she takes the highest-probability action most of the time but occasionally tries a random action to discover new strategies.

```
network_output = [0.1, 3.2, 0.5, 0.8, 0.3, 0.2, 0.1, 0.0, 0.4, 0.1]
probabilities  = softmax(output)
                 [0.02, 0.52, 0.03, 0.05, 0.03, 0.02, 0.02, 0.02, 0.03, 0.02]
                        ^^^^
                  52% confidence: reply_to_message

if random() < epsilon:  // exploration
    action = random_action()
else:                    // exploitation
    action = argmax(probabilities)  // = 1 (reply)
```

### 4.3 Action Masking

Not all actions are valid in every state. The system applies a mask before softmax, setting invalid actions to negative infinity so they get zero probability.

| Condition | Masked Actions |
|-----------|----------------|
| No messages waiting | reply_to_message, notify_user |
| No active goals | work_on_goal |
| No pending memories | consolidate_memories |
| Energy < 0.2 | research, think (expensive operations) |
| Quiet hours | notify_user |
| Recent reply (< 1 min) | reply_to_message (prevents spam) |

---

## 5. Reward System

The reward function defines what "good behavior" means for DARCI. Rewards are numerical signals that tell the network whether an action was beneficial.

### 5.1 Immediate Rewards

Calculated right after an action completes.

| Signal | Reward | Rationale |
|--------|--------|-----------|
| Replied to waiting message | +1.0 | Core purpose: be responsive |
| Replied to urgent message | +1.5 | Urgency should be prioritized |
| User got positive sentiment reply | +0.5 | Good interaction quality (LLM-scored) |
| Created goal from clear request | +0.8 | Correctly identified actionable intent |
| Advanced a goal step | +0.6 | Making progress on commitments |
| Completed a goal | +1.5 | Major achievement |
| Stored relevant memory | +0.3 | Building knowledge base |
| Recalled useful memory | +0.4 | Leveraging past experience |
| Rested when nothing to do | +0.1 | Efficient use of cycles |
| Rested when messages waiting | -1.0 | Failed to notice work |
| Attempted impossible action | -0.5 | Wasted a cycle |
| Spammed user (2+ msgs < 30s) | -2.0 | Annoying behavior |
| Failed action (error) | -0.3 | Something went wrong |
| Created duplicate goal | -0.5 | Redundant work |

### 5.2 Delayed Rewards

Some outcomes take time to evaluate. Scored retroactively and applied to the experience buffer.

| Signal | Reward | Measured When |
|--------|--------|---------------|
| User said "thanks" / positive feedback | +1.0 | Next message from user |
| User repeated request (we missed it) | -1.5 | Pattern detection |
| Goal completed within reasonable time | +1.0 | Goal completion event |
| Goal abandoned by user | -0.5 | Goal status change |
| Research led to follow-up conversation | +0.8 | Next interaction analysis |
| Memory was recalled and useful later | +0.5 | Memory access events |

### 5.3 Reward Shaping

Raw rewards can be sparse. Reward shaping adds small continuous signals that guide learning: small positive rewards for keeping energy above 0.5, small positive rewards for reducing the message queue, and small negative rewards for very long idle periods when goals are pending.

---

## 6. Network Architecture

### 6.1 The Decision Network

Intentionally simple. 29-dimension state space, 10 actions. Complexity can be added later.

```
Input Layer:    29 neurons (state vector)
                    │
Hidden Layer 1: 64 neurons, ReLU activation
                    │
Hidden Layer 2: 32 neurons, ReLU activation
                    │
Output Layer:   10 neurons (action logits)
                    │
Action Mask  → Softmax → Probabilities
```

Total parameters: ~4,330 parameters. Trains in seconds on CPU. No GPU required.

### 6.2 Technology Choice

| Option | Pros | Cons | Recommendation |
|--------|------|------|----------------|
| ML.NET | Pure C#, native integration | Limited RL support | Good for production |
| TensorFlow.NET | Full TF ecosystem, GPU support | Heavy dependency | Overkill for this size |
| ONNX Runtime | Fast inference, train in Python, run in C# | Two-language workflow | Best hybrid approach |

**Recommendation:** Train in PyTorch (Python), export to ONNX, serve via ONNX Runtime in C#.

### 6.3 Training Algorithm: Deep Q-Network (DQN)

DQN is the standard algorithm for discrete actions + numerical state + delayed rewards. It was the algorithm DeepMind used to master Atari games.

Key components:
- **Experience Replay Buffer** — stores (state, action, reward, next_state) tuples. Training samples randomly to break temporal correlations.
- **Target Network** — a slowly-updating copy that provides stable Q-value targets.
- **Epsilon-Greedy Exploration** — starts at ε=0.5 (50% random), decays to ε=0.05 (5% random).
- **Discount Factor (γ=0.95)** — future rewards worth 95% of immediate, encouraging forward thinking.

---

## 7. Training Pipeline

### 7.1 Phase 1: Bootstrap from Existing Behavior

Use the current hardcoded `Decision.cs` as a teacher. Log every (state, action) pair, then train via behavioral cloning.

```
1. Instrument Decision.Decide() to log state vectors + chosen actions
2. Run DARCI normally for days/weeks, collecting experience
3. Also run synthetic scenarios (simulated messages, goals, etc.)
4. Train network to predict the coded action from the state vector
5. Validate: network should match coded decisions ~95%+ of the time
6. Deploy in shadow mode (network predicts, code still decides, log disagreements)
```

### 7.2 Phase 2: Reinforcement Learning

Switch to network-driven decisions. Reward system kicks in. Experience buffer accumulates real outcomes.

```
Every DARCI cycle:
  1. Encode perception → state vector
  2. Network predicts action probabilities
  3. Select action (epsilon-greedy)
  4. Execute action, observe outcome
  5. Calculate reward
  6. Store (state, action, reward, next_state) in experience buffer

Every N cycles (e.g., 100):
  7. Sample batch of 32 experiences from buffer
  8. Compute Q-learning update
  9. Update network weights
 10. Every 1000 cycles: update target network
```

### 7.3 Phase 3: Simulation Acceleration

Build a simplified environment simulator that generates synthetic perception states and simulates outcomes. Lets DARCI practice thousands of decisions in minutes. Doesn't need to be perfect — even a rough model provides useful training signal.

### 7.4 Safety: The Guardrail Layer

The C# action executor remains a safety layer. Even if the network makes a bad decision, the executor can reject it:
- Rate limits (no more than 3 messages per minute)
- Action validation (can't reply without a message)
- Sanity checks (don't create 100 goals)
- Fallback to v3 logic if network unavailable

The network proposes, the guardrails dispose.

---

## 8. Engineering Validation Network (EVN)

The longer-term vision: applying state-action-reward to 3D engineering tasks.

### 8.1 Concept

When DARCI works on engineering tasks — designing a 3D-printable part, assembling a mechanism, checking tolerances — she needs spatial intelligence, not language intelligence. The EVN evaluates geometric relationships, checks fits, compares shapes against references, and iteratively improves designs.

### 8.2 State Representation for Engineering

| Category | Dimensions | Examples |
|----------|------------|----------|
| Part Geometry | Variable (mesh encoding) | Bounding box, volume, surface area, curvature distribution |
| Reference Comparison | ~20 | Hausdorff distance, volume difference ratio, surface deviation |
| Assembly Context | ~15 | Clearance to mating parts, alignment error, interference volume |
| Constraint Satisfaction | ~10 | Tolerance violations, DOF analysis, symmetry deviation |
| Material/Physics | ~8 | Stress concentration, mass, center of gravity, wall thickness |
| Process Constraints | ~6 | Overhang angles, support volume, minimum feature size |

### 8.3 Action Space for Engineering

| Action | Description |
|--------|-------------|
| adjust_dimension(axis, amount) | Scale or shift a feature along an axis |
| add_fillet(edge, radius) | Round an edge |
| add_chamfer(edge, distance) | Bevel an edge |
| thicken_wall(face, amount) | Increase wall thickness |
| modify_clearance(joint, amount) | Adjust fit between mating parts |
| approve_design | Mark current state as acceptable |
| flag_issue(type, location) | Identify a problem for human review |
| request_reference | Ask for reference image or spec |
| run_simulation(type) | Trigger stress test, fit check, or print sim |

### 8.4 Reward Signals for Engineering

| Signal | Reward | Source |
|--------|--------|--------|
| Reduced deviation from reference | +1.0 per 10% reduction | Hausdorff distance |
| All tolerances within spec | +2.0 | Constraint checker |
| Interference eliminated | +1.5 | Collision detection |
| Printability improved | +0.8 | Overhang/support analysis |
| Unnecessary modification | -0.3 | No metric improvement |
| Introduced new violation | -1.5 | Regression detection |
| Design approved by user | +3.0 | Human feedback |
| Design rejected by user | -1.0 | Human feedback |

### 8.5 The Simulation Loop

Engineering tasks are simulatable — unlike conversations, a 3D part can be evaluated computationally. The EVN can run thousands of design iterations in minutes:

```
1. Load part geometry + reference/constraints
2. Encode state vector from geometric analysis
3. EVN selects modification action
4. Apply modification to mesh/model
5. Re-analyze: check fits, tolerances, printability
6. Compute reward from metric changes
7. Store experience, update network
8. Repeat until approved or max iterations
```

After thousands of iterations, DARCI learns patterns like "tight internal corners cause stress concentrations" and "clearance under 0.2mm causes print failures" — not as English rules but as weight patterns that produce good actions.

---

## 9. Integration with Existing DARCI v3

### 9.1 New Projects

| Project | Purpose | Dependencies |
|---------|---------|--------------|
| Darci.Brain | State encoder, network inference, experience buffer, reward calculator | Darci.Shared, ONNX Runtime |
| Darci.Brain.Training | Offline training scripts, simulation environment, model export | Python (PyTorch) |

### 9.2 Modified Components

| Existing File | Change |
|---------------|--------|
| Decision.cs | Replace Decide() body: encode state, query network, decode action. Keep as fallback. |
| Awareness.cs | Add StateEncoder call in Perceive() to produce numerical vector. |
| State.cs | Add ToVector() method. Add FromReward() for reward processing. |
| Darci.cs (Live loop) | Add experience logging after Act(). Trigger training loop periodically. |
| Toolkit.cs | No changes — actions still execute through existing tools. |

### 9.3 Core Interface: IDecisionNetwork

```csharp
public interface IDecisionNetwork
{
    /// <summary>
    /// Get action logits for a state
    /// </summary>
    float[] Predict(float[] stateVector);

    /// <summary>
    /// Select an action with exploration and masking
    /// </summary>
    int SelectAction(float[] stateVector, bool[] actionMask);

    /// <summary>
    /// Record an experience for training
    /// </summary>
    void RecordExperience(float[] state, int action, float reward, float[] nextState);

    /// <summary>
    /// Train on accumulated experience
    /// </summary>
    Task Train(int batchSize = 32);

    /// <summary>
    /// Save/load model weights
    /// </summary>
    Task SaveModel(string path);
    Task LoadModel(string path);
}
```

---

## 10. Implementation Roadmap

### Phase 1: Instrumentation and Data Collection (1–2 weeks)
- Add StateEncoder to convert existing Perception + State into float[29]
- Instrument Decision.Decide() to log (state_vector, chosen_action, timestamp) to SQLite
- Add reward calculation to Darci.cs after each action outcome
- Run DARCI normally, accumulating training data

### Phase 2: Network Training and Shadow Mode (2–3 weeks)
- Build the DQN in PyTorch, train on collected data (behavioral cloning)
- Export to ONNX format, integrate with Darci.Brain via ONNX Runtime
- Run in shadow mode: network predicts, code still decides, log disagreements
- Tune until network agrees with code >90% of the time

### Phase 3: Live Decision Making (2–4 weeks)
- Switch to network-driven decisions with guardrail layer
- Enable online reinforcement learning from real outcomes
- Monitor reward trends, action distributions, and failure rates
- Build simulation environment for accelerated training

### Phase 4: Engineering Extension (4–8 weeks)
- Define geometric state encoder for 3D parts
- Integrate with 3D simulation environment (mesh analysis, collision detection)
- Train EVN on synthetic parts with known-good references
- Connect EVN output to DARCI's goal system for engineering tasks
