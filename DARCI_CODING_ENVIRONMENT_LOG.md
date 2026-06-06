# DARCI Coding Environment Log

Date: 2026-06-05

## First-Pass Objective

Build the first vertical slice of DARCI's coding environment: a project workspace DARCI can import, index, package into context, run safe commands against, and associate with coding tasks. This is the infrastructure layer for a later autonomous edit-test-debug loop.

## Implemented In This Pass

- Added `DARCI-v4/Darci.Coding`.
- Added SQLite-backed coding workspace storage.
- Added coding workspace import/scanning.
- Added deterministic file manifesting with ignore rules.
- Added language and build/test command detection.
- Added first-pass context package builder.
- Added safe command runner scoped to workspace roots.
- Added coding task records with initial deterministic planning.
- Wired coding services into `Darci.Api`.
- Added coding API endpoints.
- Added README section for coding workspace usage.
- Ignored `DARCI-v4/Workspaces/` as a local imported-project sandbox folder.

## Verification

- `dotnet build Darci.Api/Darci.Api.csproj --no-restore -v:minimal`
  - Result: passed with 0 warnings and 0 errors.

- `dotnet sln DARCI.sln add Darci.Coding/Darci.Coding.csproj`
  - Result: `Darci.Coding` added as a first-class solution project.

- `dotnet build DARCI.sln --no-restore -v:minimal`
  - Result: passed with 0 warnings and 0 errors.

- Temporary API smoke test on `http://localhost:5083`
  - Imported `DARCI-v4` as a coding workspace.
  - Indexed 226 files.
  - Built a context package with 3 files.
  - Ran safe command: `dotnet build Darci.Coding/Darci.Coding.csproj --no-restore -v:minimal`.
  - Result: command exit code 0.

## New API Surface

- `POST /coding/workspaces/import`
  - Body: `rootPath`, optional `name`, `createdBy`, `tags`.
  - Scans a folder and stores a manifest.

- `GET /coding/workspaces`
  - Lists imported workspaces.

- `GET /coding/workspaces/{id}`
  - Returns one workspace record.

- `GET /coding/workspaces/{id}/files`
  - Returns indexed files for a workspace.

- `GET /coding/workspaces/{id}/context?query=...&limit=8`
  - Builds a deterministic context package with relevant file previews and suggested commands.

- `POST /coding/workspaces/{id}/commands`
  - Runs an allowlisted command inside the workspace.
  - Body: `command`, `arguments`, optional `workingSubdirectory`, optional `timeoutSeconds`.

- `GET /coding/workspaces/{id}/commands`
  - Lists recent command runs.

- `POST /coding/tasks`
  - Creates a task record and first-pass plan from a context package.

- `GET /coding/tasks`
  - Lists coding tasks.

- `GET /coding/tasks/{id}`
  - Returns one coding task.

## Safe Command Runner

The command runner avoids shell execution and uses `ProcessStartInfo.ArgumentList`.

Allowed first-pass commands:

- `dotnet restore`
- `dotnet build`
- `dotnet test`
- `npm test`
- `npm run build`
- `npm run test`
- `npm run lint`
- `npm run check`
- `python -m pytest`
- `python -m unittest`
- `python -m py_compile`
- `py -m pytest`
- `py -m unittest`
- `py -m py_compile`
- `pytest`
- `cargo build`
- `cargo check`
- `cargo test`
- `go build`
- `go test`
- `git status`
- `git diff`
- `git log`

The runner confines the working directory to the imported workspace root and records stdout/stderr tails, exit code, timeout status, and errors.

## Workspace Scanner Rules

Ignored directories:

- `.git`, `.hg`, `.svn`
- `.vs`, `.idea`, `.vscode`
- `bin`, `obj`
- `node_modules`
- `.venv`, `venv`
- `__pycache__`, `.pytest_cache`
- `dist`, `build`, `coverage`
- `Data`, `tmp`

Ignored heavy/binary extensions include `.dll`, `.exe`, `.pdb`, `.onnx`, `.pt`, images, archives, PDFs, and CAD mesh/export formats.

Current scan cap:

- 10,000 files
- 2 MB per indexed file

## Current Limitations

- No autonomous file editing loop yet.
- No model router wired into coding tasks yet.
- No symbol graph extraction yet.
- No embeddings or semantic ranking in the context package yet.
- No automatic KG enrichment of imported workspaces yet.
- No automatic low-confidence escalation from coding task to KG/deep research yet.
- No git checkpoint/rollback API yet.
- File import is deterministic manifest/context only; it is not yet a full code intelligence index.

## Intended Next Architecture

1. Model router:
   - general model
   - coding model
   - fast coding/debug model
   - embedding model
   - research model

2. KG/Admin context package:
   - relevant files
   - relevant KG claims
   - confidence scores
   - provenance/evidence
   - project conventions
   - known command/test history

3. Coding agent loop:
   - inspect context
   - create plan
   - read target files
   - apply patch
   - run safe command
   - parse failure
   - iterate
   - ask DARCI for KG/research help when blocked

4. Roadblock detection:
   - same error repeated
   - missing dependency/API uncertainty
   - low confidence context package
   - failing command count threshold
   - no relevant local/KG memory

5. Success detection:
   - command exit code success
   - expected artifact exists
   - health endpoint responds
   - tests pass
   - user-provided success criteria satisfied

## Useful Example Requests

Import a workspace:

```json
{
  "rootPath": "C:\\Users\\aiden\\OneDrive\\Documents\\GitHub\\ProgDS\\DARCI-Live\\DARCI-v4",
  "name": "DARCI v4",
  "createdBy": "Tinman",
  "tags": ["darci", "coding-environment"]
}
```

Run a build:

```json
{
  "command": "dotnet",
  "arguments": ["build", "DARCI.sln", "--no-restore", "-v:minimal"],
  "timeoutSeconds": 180
}
```

Create a coding task:

```json
{
  "workspaceId": "<workspace-id>",
  "prompt": "Add a failing-test-driven fix for the workspace import scanner.",
  "successCriteria": "dotnet build succeeds and relevant tests pass.",
  "createdBy": "Tinman"
}
```

## Notes For Future Context Windows

The current first pass is intentionally conservative. It gives DARCI a durable coding workspace substrate, not full autonomy. The next useful implementation step is to connect this module to a model router and a patch/apply mechanism, then let the task loop run safe commands until success or a documented blocker.

---

## Second-Pass Implementation — Next Architecture Connections

Date: 2026-06-05

### What Was Implemented

**1. Model Router (`IModelRouter` / `ModelRouter`)**
- Dispatches text generation and embedding requests to the appropriate Ollama model based on `ModelTaskType` enum (`General`, `Coding`, `FastCoding`, `Embedding`).
- Model names read from: `DARCI_OLLAMA_MODEL`, `DARCI_OLLAMA_CODING_MODEL`, `DARCI_OLLAMA_FAST_CODING_MODEL`, `DARCI_OLLAMA_EMBED_MODEL`.
- Registered as `AddHttpClient<ModelRouter>` typed client in DI.

**2. Embedding-Based Context Ranking**
- `WorkspaceEmbeddingService` computes embeddings (path + 500-char preview) for all text files after import and stores them in a new `coding_file_embeddings` SQLite table.
- `CodingContextBuilder` now loads stored embeddings and re-ranks files by `0.4 * cosine_sim + 0.6 * heuristic_score` when a query is provided.
- Falls back gracefully to pure heuristic if Ollama is unavailable.

**3. KG Enrichment (`KgEnrichmentService`)**
- Extracts type/class/function/export symbols from `.cs`, `.py`, `.ts`, `.js` files via regex.
- Creates KG nodes (`code-file`, `type`, `method`, `class`, `function`, `export`) with `defines` edges.
- Triggered in background after workspace import alongside embedding pass.
- `CodingContextBuilder` includes top-5 KG symbol hits in the context package.

**4. Git Checkpointing (`GitCheckpointService`)**
- `CreateCheckpointAsync`: stages all files (`git add -A`), commits, records SHA in `coding_checkpoints` table.
- `RollbackToCheckpointAsync`: `git reset --hard <sha>` to the checkpoint commit.
- Expanded git allowlist in `SafeCommandRunner`: `stash`, `stash pop`, `commit`, `checkout`, `reset --hard <sha/HEAD>`, `add`, `rev-parse`.
- New endpoints: `POST /coding/workspaces/{id}/checkpoint`, `POST /coding/workspaces/{id}/rollback`.

**5. LLM-Driven Planning**
- `CodingTaskService.CreateTaskAsync` now calls `IModelRouter.GenerateAsync` (ModelTaskType.Coding) to produce a numbered plan.
- Parses model response into structured `CodingPlanStep[]` (JSON in `plan` column).
- Falls back to the 5-step template if the LLM is unavailable or returns an unparseable response.
- New `CodingTaskRecord` fields: `PlanGeneratedBy` ("llm" | "template"), `PlanModel`, `CurrentStepIndex`, `LastStepResult`, `RoadblockResearch`.

**6. Roadblock Detection (`RoadblockDetector`)**
- Triggers when 3+ consecutive non-zero exit codes OR repeated identical stderr pattern for a task.
- Calls `IDeepResearchOrchestrator.RunDeepResearchAsync` with a structured question from the failing command and stderr.
- Research result stored in `CodingTaskRecord.RoadblockResearch` and surfaced via the status endpoint.

**7. Coding Agent Loop (`CodingAgentLoop`)**
- `StartLoop(taskId)` starts an async background `Task.Run` loop; returns false if already running.
- Per step: build context package → generate LLM patch prompt → parse file edits (`PatchApplier`) → run build command → retry up to 3× feeding stderr back → trigger roadblock detection on 3rd failure.
- Marks steps `pending → in_progress → completed | failed | roadblocked`.
- After all steps: marks task `completed | blocked | failed`, optionally runs a success verification command.
- New endpoints: `POST /coding/tasks/{id}/run` (202 Accepted), `GET /coding/tasks/{id}/status`.

**8. Patch Applier (`PatchApplier`)**
- Parses `### FILE: relative/path` + fenced code block as full file replacement.
- Parses ` ```diff ` blocks as unified diffs (hunk-based application with offset tracking).
- All writes are path-contained to the workspace root (mirrors `SafeCommandRunner`'s check).

### Schema Changes
- `coding_command_runs`: added `task_id TEXT NOT NULL DEFAULT ''` column.
- `coding_tasks`: added `plan_generated_by`, `plan_model`, `current_step_index`, `last_step_result`, `roadblock_research` columns.
- New tables: `coding_file_embeddings (file_id, workspace_id, embedding_json, computed_at)`.
- New table: `coding_checkpoints (id, workspace_id, task_id, commit_sha, message, created_at)`.
- All migrations are idempotent via try/catch on `ALTER TABLE ADD COLUMN`.

### Design Decisions
- `IModelRouter` is a self-contained HTTP client (not a wrapper around `IOllamaClient`) to avoid Darci.Coding taking on Darci.Tools as a direct dependency.
- Embedding and KG enrichment run in background `Task.Run` after import — import response is not blocked.
- The agent loop uses full file replacement prompts rather than diffs by default (more reliable with LLMs); unified diff parsing is available as a fallback.
- `ISafeCommandRunner.RunForTaskAsync` threads the `task_id` through to command run records so roadblock detection can query task-specific history.
- `IKnowledgeGraph` and `IDeepResearchOrchestrator` are required DI dependencies (not nullable), since they are always registered in Program.cs.

### Verification
- `dotnet build Darci.Coding/Darci.Coding.csproj --no-restore -v:minimal` — 0 errors, 0 warnings.
- `dotnet build DARCI.sln --no-restore -v:minimal` — 0 errors, 0 warnings.

### New API Surface (additions to first pass)
- `POST /coding/tasks/{id}/run` — starts the agent loop in background, returns 202.
- `GET /coding/tasks/{id}/status` — returns step index, step description, last result, roadblock notes, running flag.
- `POST /coding/workspaces/{id}/checkpoint` — creates a git checkpoint commit.
- `POST /coding/workspaces/{id}/rollback` — resets workspace to latest checkpoint.

### Remaining / Future Work
- End-to-end testing against a real workspace with Ollama running.
- Tuning the step prompt format for better LLM compliance (full file blocks vs diffs).
- Streaming the agent loop progress via SignalR `DarciHub`.
- Confidence-gated escalation: if KG or embedding relevance is below threshold, skip LLM edit and go straight to research.
- `git stash` / `stash pop` as a lighter-weight checkpoint alternative for workspaces with uncommitted changes.
- `PatchApplier` unified diff hunk applicator is functional but fuzzy — a stricter context-line verification mode would catch mismatches earlier.
