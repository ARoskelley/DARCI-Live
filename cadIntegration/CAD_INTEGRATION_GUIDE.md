# DARCI v3 — CAD Generation Integration Guide

## What this adds

A CadQuery-based 3D model generation pipeline with a self-correcting feedback loop.
Users can say "Generate a 100mm mounting plate with 4 bolt holes" and DARCI will:

1. Generate a CadQuery script via Ollama
2. Send it to a Python service for execution + validation
3. Self-critique the result (dimensions, watertight, degenerate triangles)
4. Fix and retry up to N iterations
5. Deliver the STL path to the user via the existing `/responses` channel

## Project structure after integration

```
DARCI-v3/
├── (existing C# projects unchanged)
├── Darci.Python/              ← NEW — Python service
│   ├── main.py                ← FastAPI entry point
│   ├── requirements.txt
│   └── cad/
│       ├── __init__.py
│       ├── cad_engine.py      ← Sandboxed execution, validation, rendering
│       └── cad_routes.py      ← /cad/generate, /cad/feedback-prompt
├── Darci.Tools/
│   ├── Cad/                   ← NEW — C# clients for the Python service
│   │   ├── CadBridge.cs       ← HTTP client (like OllamaClient)
│   │   └── CadModels.cs       ← DTOs matching Python API
│   ├── Toolkit.cs             ← MODIFIED — adds CAD orchestration
│   └── IToolkit.cs            ← MODIFIED — adds 3 CAD methods
├── Darci.Shared/
│   └── Models.cs              ← MODIFIED — adds GenerateCAD action, CAD enums
├── Darci.Core/
│   ├── Darci.cs               ← MODIFIED — adds DoCADWork handler
│   ├── Decision.cs            ← MODIFIED — adds IntentType.CAD routing
│   └── Awareness.cs           ← MODIFIED — adds CAD quick-classification
├── Darci.Goals/
│   └── GoalManager.cs         ← PATCHED — adds GoalType.CAD steps
└── Darci.Api/
    └── Program.cs             ← MODIFIED — adds CadBridge DI + CAD endpoints
```

## Step-by-step integration

### 1. Add the Python service (entirely new)

Copy the `Darci.Python/` folder into your `DARCI-v3/` directory.
It sits alongside `Darci.Api/`, `Darci.Core/`, etc.

```bash
cd DARCI-v3/Darci.Python

# CadQuery is best installed via conda:
conda install -c cadquery -c conda-forge cadquery

# Then install the rest:
pip install -r requirements.txt

# Run:
uvicorn main:app --host 0.0.0.0 --port 8000
```

Test it: `curl http://localhost:8000/` → `{"status":"alive","service":"darci-python"}`

### 2. Add new C# files

Copy into your existing `Darci.Tools` project:
- `Darci.Tools/Cad/CadBridge.cs`
- `Darci.Tools/Cad/CadModels.cs`

No new NuGet packages needed — these use `HttpClient` and `System.Text.Json`
which are already available.

### 3. Replace modified C# files

These are full file replacements (not patches). Back up your originals first.

| File to replace | Location |
|---|---|
| `Models.cs` | `Darci.Shared/Models.cs` |
| `IToolkit.cs` | `Darci.Tools/IToolkit.cs` |
| `Toolkit.cs` | `Darci.Tools/Toolkit.cs` |
| `Decision.cs` | `Darci.Core/Decision.cs` |
| `Darci.cs` | `Darci.Core/Darci.cs` |
| `Awareness.cs` | `Darci.Core/Awareness.cs` |
| `Program.cs` | `Darci.Api/Program.cs` |

### 4. Patch GoalManager.cs

This is a small edit. In `Darci.Goals/GoalManager.cs`, find the
`CreateInitialSteps` method and add the `GoalType.CAD` case to the
switch statement. See `patch/GoalManager_patch.txt` for exact instructions.

### 5. Build and run

```bash
# Terminal 1: Python CAD service
cd DARCI-v3/Darci.Python
uvicorn main:app --port 8000

# Terminal 2: Ollama
ollama serve

# Terminal 3: DARCI
cd DARCI-v3/Darci.Api
dotnet build
dotnet run
```

## API usage

### Through DARCI's message pipeline (recommended)
```bash
# This flows through Perceive → Decide → Act like any other message
curl -X POST http://localhost:5080/cad/generate \
  -H "Content-Type: application/json" \
  -d '{"description": "A 100mm x 50mm mounting plate with 4 M5 bolt holes", "urgent": true}'

# Or just send a regular message — Awareness will detect CAD intent:
curl -X POST http://localhost:5080/message \
  -H "Content-Type: application/json" \
  -d '{"message": "Generate an STL for a 30mm diameter gear with 20 teeth", "urgent": true}'

# Then poll for results:
curl http://localhost:5080/responses/wait
```

### Direct execution (testing/debugging)
```bash
curl -X POST http://localhost:5080/cad/execute \
  -H "Content-Type: application/json" \
  -d '{
    "description": "A simple 50mm cube with a 20mm hole through the center",
    "lengthMm": 50, "widthMm": 50, "heightMm": 50,
    "maxIterations": 3
  }'
```

### Health check
```bash
curl http://localhost:5080/cad/health
```

## How it flows through the system

```
User: "Generate a 100mm × 50mm mounting plate"
  │
  ├─ /message or /cad/generate
  │
  ▼
Awareness.ClassifyIntent()
  │  IsClearlyCADRequest() matches "generate" + "plate" → IntentType.CAD
  │
  ▼
Decision.DecideResponseTo()
  │  IntentType.CAD → HandleCADRequest()
  │  Creates GoalType.CAD goal
  │  Returns DarciAction.GenerateCad(...)
  │
  ▼
Darci.Act() → DoCADWork()
  │
  ▼
Toolkit.GenerateCAD()
  ├─ Ollama: generate initial CadQuery script
  ├─ CadBridge → Python /cad/generate (execute + validate + render)
  ├─ If passed: CadBridge → /cad/feedback-prompt → Ollama evaluates → "APPROVED"?
  ├─ If failed: CadBridge → /cad/feedback-prompt → Ollama fixes script → retry
  └─ Loop until approved or max iterations
  │
  ▼
Toolkit.SendMessage() → outgoing channel → /responses endpoint
```

## Security mitigations

| ID | Threat | Mitigation |
|---|---|---|
| V1 | Code injection via LLM-generated scripts | AST filtering: only cadquery + math imports, blocked dangerous calls |
| V2 | Resource exhaustion | SIGALRM timeout (30s), restricted builtins namespace |
| V3 | Non-manifold/degenerate mesh | trimesh validation: watertight check, degenerate triangle detection |
| V4 | Feedback loop oscillation | Script delta check: stop if LLM produces identical script twice |
| V5 | Floating point tolerance drift | Configurable DIMENSION_EPSILON (0.05mm default) |
| V6 | Path traversal in file exports | Locked output directory, filename sanitization |
| V7 | Prompt injection via user description | Sanitization: strip code fences, APPROVED keyword, length cap (2000 chars) |
| V8 | Script extraction failure | Handles markdown fences, raw Python, and graceful fallback |
