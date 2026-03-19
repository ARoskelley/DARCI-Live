# DARCI v4 — Project Context for Claude Code

## What Is DARCI?

DARCI (Dynamic Adaptive Reasoning & Contextual Intelligence) is an autonomous AI agent built in C# / .NET 8. She runs as a continuous background service with a living loop: Perceive → Feel → Decide → Act → Reflect. She is not a chatbot — messages from users are events in her life, not the reason she exists.

## v3 → v4: What's Changing

The v3 codebase (in `DARCI-v3/`) uses a **hardcoded C# priority ladder** in `Decision.cs` to choose actions. This is being replaced with a **neural decision network** that learns from experience. The LLM (Ollama) remains for language tasks only — it no longer participates in action selection.

### The Core Principle

**v3:** Perception (mixed English + data) → C# if/else chain → Action → LLM generates text  
**v4:** Perception → State Encoder (numerical vector) → Neural Network → Action selection → LLM only if action needs language

The architecture spec is in `DARCI-v4/ARCHITECTURE.md`. Read it before making changes.

## Repository Structure

```
DARCI-Live/
├── DARCI-v3/              # Previous version (reference, do not modify)
│   ├── Darci.Core/        # Living loop, decision logic, awareness
│   ├── Darci.Memory/      # SQLite + embeddings memory system
│   ├── Darci.Goals/       # Goal management
│   ├── Darci.Personality/ # Trait and mood system
│   ├── Darci.Tools/       # Toolkit + Ollama client
│   ├── Darci.Shared/      # Shared models and types
│   └── Darci.Api/         # REST API
│
├── DARCI-v4/              # New version with neural decision-making
│   ├── Darci.Brain/       # NEW: Neural network, state encoder, reward, experience buffer
│   ├── Darci.Core/        # Modified: uses Brain for decisions instead of hardcoded logic
│   ├── Darci.Memory/      # Carried forward (may add vector DB later)
│   ├── Darci.Goals/       # Carried forward
│   ├── Darci.Personality/ # Carried forward (feeds into state vector)
│   ├── Darci.Tools/       # Carried forward (execution layer)
│   ├── Darci.Shared/      # Extended with neural types
│   ├── Darci.Api/         # Extended with training/monitoring endpoints
│   └── ARCHITECTURE.md    # Full architecture specification
│
└── CLAUDE.md              # This file
```

## Key Design Decisions

### State Vector (29 dimensions)
All perception is encoded as a float[29] normalized to [0,1] or [-1,1]. No English passes through the decision network. See ARCHITECTURE.md §3 for the full vector definition.

### Action Space (10 discrete actions)
Actions are integers 0–9, not strings. The network outputs logits over these actions. Invalid actions are masked before softmax. See ARCHITECTURE.md §4.

### Reward System
Immediate rewards (replied to message: +1.0, rested when messages waiting: -1.0) plus delayed rewards (user said thanks: +1.0). See ARCHITECTURE.md §5.

### Cognitive Memory Systems
DARCI v4 now layers a knowledge graph, a confidence tracker, and deep research agents on top of the existing SQLite-backed memory store. The graph captures explicit entities and relations, the confidence layer tracks corroboration and contradictions for claims, and deep research coordinates parallel specialist agents before feeding synthesized findings back into memory.

### Two-Brain Design
- **Decision Network**: Small NN (~4,330 params). 29 → 64 → 32 → 10. Handles all "what should I do" logic.
- **LLM (Ollama)**: Text generation only. Replies, summaries, intent comprehension. Never decides actions.

### Training Pipeline
1. **Bootstrap**: Log v3 decisions as training data, train network via behavioral cloning
2. **Online RL**: Network makes real decisions, learns from reward signals (DQN)
3. **Simulation**: Synthetic environment for accelerated training

### Technology
- Runtime: .NET 8, C#
- Neural network inference: ONNX Runtime (preferred) or ML.NET
- Training: PyTorch (Python script), export to ONNX
- Database: SQLite (existing) for experience buffer
- LLM: Ollama (local, gemma2:9b for text, nomic-embed-text for embeddings)

## Coding Conventions

- Follow existing v3 patterns for C# style (nullable enabled, implicit usings, async/await)
- Interfaces for all major components (testability)
- Singletons for core services (one consciousness)
- SQLite for all persistence (Dapper for queries)
- XML doc comments on public interfaces
- Logging via ILogger<T>

## What To Reference in v3

When building v4 components, look at these v3 files:
- `Darci.Core/State.cs` — fields that become state vector dimensions
- `Darci.Core/Decision.cs` — logic being replaced (understand what it does)
- `Darci.Core/Awareness.cs` — perception gathering (carries forward)
- `Darci.Core/Darci.cs` — the living loop and Act() method (carries forward with modifications)
- `Darci.Shared/Models.cs` — all type definitions (extended in v4)
- `Darci.Tools/Toolkit.cs` — action execution (carries forward as-is)
- `Darci.Tools/IToolkit.cs` — tool interface (unchanged)

## Safety / Guardrails

The C# action executor remains a safety layer even after the network takes over decisions:
- Rate limits: no more than 3 messages per minute
- Action validation: can't reply without a message in queue
- Sanity bounds: max 20 active goals, max 100 memories per hour
- Fallback: if network unavailable, revert to v3 hardcoded logic
- All network decisions are logged for audit
