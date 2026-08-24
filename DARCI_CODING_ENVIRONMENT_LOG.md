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

---

## Third-Pass Implementation — Behavioral Verification, Model Swap, Full-File Context, Confidence Escalation

Date: 2026-06-11

### Problem Context

The agent loop ran a task autonomously and produced code that COMPILED but was behaviorally wrong (floating-point multiplication result silently truncated to int). The only success signal was `dotnet build` exit code 0. This pass fixes the blindness.

### What Was Implemented

**1. Behavioral Verification (CodingAgentLoop.cs)**

- After a successful build, the loop now runs the `dotnet test` (or equivalent test command) if the LLM emitted `### VERIFY:` blocks.
- `### VERIFY: path/to/Test.cs` blocks are parsed separately from `### FILE:` blocks. VERIFY files are written to the workspace and the test command is executed.
- A behavioral test failure is treated identically to a build failure: the failure output is injected into `stepResult` and fed back into the retry loop. The step is eligible for roadblock escalation just like a build failure.
- `PickTestCommand` picks the appropriate test command for the workspace language (dotnet test, pytest, cargo test, go test, npm test).
- `SplitVerifyBlocks(response)` splits VERIFY sections out before passing to PatchApplier.
- `StripMetaAnnotations(response)` strips CONFIDENCE/UNSURE_ABOUT lines before patch application.

**2. Model Swap (ModelRouter.cs + .env.local)**

- Default coding model fallback changed from `_generalModel` to `"qwen2.5-coder:7b"` in `ModelRouter.cs`.
- `DARCI-v4/Darci.Api/.env.local` updated: `DARCI_OLLAMA_CODING_MODEL=qwen2.5-coder:7b`.
- **NOTE:** `ollama pull qwen2.5-coder:7b` must be run before the agent loop is used.
- The fallback is only active when `DARCI_OLLAMA_CODING_MODEL` is unset. The env var always takes precedence.

**3. Prompt Quality + Full-File Context (CodingAgentLoop.cs)**

- `BuildStepPromptAsync` is now `async` and reads files directly from disk (full content) instead of using the 1500-char truncated preview from the context package.
- Files ≤ 50 KB are sent in full. Files > 50 KB get a first/last 20K-char window with a gap marker.
- The 1500-char `preview[..1500]` truncation in the step prompt has been removed.
- Explicit numeric type constraints added to INSTRUCTIONS section:
  - Prefer `double` over `float` or `int` for numeric results.
  - Never insert casts that silently truncate precision.
  - One class per file; match existing naming conventions.
  - Write complete file content — no truncation, no TODO placeholders.

**4. Confidence + Proactive Escalation (CodingAgentLoop.cs + RoadblockDetector.cs + ICodingServices.cs)**

- The step prompt now requires the model to end every response with:
  ```
  CONFIDENCE: 0.0–1.0
  UNSURE_ABOUT: <specific uncertainty or 'nothing'>
  ```
- `ParseConfidence(response)` extracts the score and note.
- Confidence + note are stored on `CodingTaskRecord.ConfidenceScore` / `ConfidenceNote` and surfaced via `GET /coding/tasks/{id}/status`.
- **Early roadblock escalation**: `CheckAndResearchAsync` is now called after ANY failed attempt at index >= `EarlyEscalationAttempt` (1), not only on the final retry. Research result is injected into the task's `RoadblockResearch` for the next attempt's prompt.
- **Proactive research**: if build succeeds but `ConfidenceScore < 0.4` and `UNSURE_ABOUT` is not "nothing", `IRoadblockDetector.ResearchTopicAsync` is called BEFORE running behavioral verification. Research is stored in `RoadblockResearch` for the next attempt.
- New interface method `IRoadblockDetector.ResearchTopicAsync(question, ct)` added to `ICodingServices.cs` and implemented in `RoadblockDetector.cs`. Calls deep research directly without failure-count gate.

### Schema Changes

- `coding_tasks` table: added 3 new columns:
  - `verification_result TEXT NOT NULL DEFAULT ''` — last behavioral test run output
  - `confidence_score REAL NOT NULL DEFAULT -1.0` — last model self-assessed confidence (-1 = not assessed)
  - `confidence_note TEXT NOT NULL DEFAULT ''` — what the model was uncertain about
- All 3 columns added via idempotent `TryMigrateAsync` in `InitializeAsync`.
- `BindTask` and `MapTask` updated to include the new columns.
- `MapTask` uses try/catch ordinal lookup for the new columns to survive reads from pre-migration rows.

### API Surface Changes

- `GET /coding/tasks/{id}/status` now returns `verificationResult`, `confidenceScore`, `confidenceNote` in the response body.

### Design Decisions

- `### VERIFY:` blocks are written persistently to the workspace (not cleaned up between retries). On a retry, the LLM can emit new VERIFY blocks that overwrite the old ones. Old verify files may cause build failures on retries if they reference APIs that no longer exist — this is intentional: the build failure feeds back correctly.
- `dotnet run` was NOT added to the SafeCommandRunner allowlist. `dotnet test` (already allowed) covers behavioral verification for .NET workspaces. The `### VERIFY:` mechanism relies on LLMs generating xUnit/NUnit test files.
- The `PickBuildCommand` fallback chain no longer includes `dotnet test` — test commands are intentionally separated to a `PickTestCommand` method so build and verification are distinct phases.
- Full-file reading cap: 50 KB per file (sends full content); above 50 KB, first+last 20K chars are sent with a gap marker. This keeps prompt size manageable while ensuring small-to-medium source files arrive complete.

### Verification

- `dotnet build DARCI.sln --no-restore -v:minimal` — 0 errors, 0 warnings.

### Remaining / Future Work

- End-to-end testing with qwen2.5-coder:7b against a real workspace (run `ollama pull qwen2.5-coder:7b` first).
- `dotnet run` could be added to the SafeCommandRunner allowlist to support "run and check output" verification for workspaces without a test project infrastructure.
- The behavioral verification relies on the LLM generating valid test projects when none exist. For workspaces that are console apps (not libraries), the LLM would need to refactor to extract a library first — plan steps should ideally account for this.
- Streaming loop progress via SignalR `DarciHub` is still outstanding.
- `PatchApplier` fuzzy hunk applicator still lacks strict context-line verification.

---

## Fourth-Pass — First Live Run, False-Success Bug Found + Guardrails

Date: 2026-06-11

### Test Setup

- Created an isolated sandbox `C:\Users\aiden\DarciSandbox` (full copy of DARCI-v4 incl. `Darci.Coding.Tests`, clean git baseline `cbef061`) so the autonomous agent could not touch the live working tree.
- Pulled `qwen2.5-coder:7b` (the new default coding model).
- Imported sandbox as workspace `a27c1000…`; detected commands included `dotnet build "DARCI.sln"` and `dotnet test "DARCI.sln"`.
- Task: add `WorkspaceHealthReport.Compute(IReadOnlyList<CodingCommandRun>)` returning a record with `double SuccessRate` / `double MeanDurationSeconds` — a deliberate int-vs-double behavioral trap (2 successes / 3 runs = 0 in int math, 0.667 correct) plus xUnit tests.
- qwen produced a real LLM plan (`planGeneratedBy: llm`) of 7 granular steps.

### What Happened (the bug)

The loop ran all 7 steps over ~38 min and reported **`status: completed`** — but:
- **Zero files were written** (`git status` clean, no `WorkspaceHealthReport.cs`, no tests).
- **`dotnet test` never ran** (0 test runs in command history; 7 `dotnet build` runs, all exit 0).
- `confidenceScore` stayed `-1`, `verificationResult` and `roadblockResearch` empty.

Root cause, confirmed by replaying a single generation against qwen: the granular plan ("create the file", "define the record", "add usings" as separate steps) + the step-prompt instruction "produce ONLY the edits needed for THIS step" + the `NO_EDITS_NEEDED` escape hatch led qwen to answer **`NO_EDITS_NEEDED` with CONFIDENCE 1.0** for atomic steps. That cascaded across all steps. Because the success signal was still `dotnet build` exit 0, and an **unchanged tree always builds**, the no-op run was rubber-stamped as success. This is the same blindness the third pass targeted, in a more extreme form: not "compiles but wrong" but "wrote nothing, reported success." Third-pass behavioral verification did not catch it because verification only fired when the model volunteered `### VERIFY:` blocks — and a model doing nothing volunteers nothing.

### Fix — Three Guardrails (CodingAgentLoop.cs)

1. **False-success guard.** Track `totalFilesWritten` across the whole task (sum of applied code + verify patches). If a task finishes "completed" with `totalFilesWritten == 0`, force status to **`no-op`** with an explanatory `LastStepResult`. A no-op can never read as success again.
2. **Mandatory end-of-task verification gate.** If the workspace has a detected test command and the task wrote files, run that test command as a final gate **regardless of whether the model emitted `### VERIFY:` blocks**. Non-zero exit ⇒ status `verification-failed`. Behavioral verification is no longer opt-in by the model.
3. **Tamed `NO_EDITS_NEEDED`.** Step prompt now states that if the overall TASK implies creating/modifying a code file, the model MUST emit the complete file even on preparatory-sounding steps, and `NO_EDITS_NEEDED` is only for purely analytical steps.

### New Task Statuses

- `no-op` — task completed all steps but wrote zero files (guard 1).
- `verification-failed` — already existed for caller-supplied verification; now also set by the mandatory end-of-task test gate (guard 2).

### Verification

- `dotnet build Darci.Coding/Darci.Coding.csproj --no-restore -v:minimal` — 0 errors, 0 warnings.
  (Full-solution build was blocked only by the live `Darci.Api` process locking output DLLs; not a compile issue.)

### Still To Validate (next live run)

- A re-run requires rebuilding the live API (loop logic runs from the API's compiled `Darci.Coding.dll`, not the sandbox copy) — i.e. stop the running API, rebuild solution, restart, reset sandbox to `cbef061`, re-create + re-run the task.
- Expected on re-run: guard 3 makes qwen emit the full file on step 1; guards 1–2 ensure the result is an honest `completed` (with green tests) / `verification-failed` / `no-op` rather than a false `completed`.
- Plan granularity is still suboptimal (7 atomic steps for a one-file task). Consider having `CodingTaskService` produce coarser steps, or collapsing to a single implement-then-verify step for small tasks.

---

## Fifth-Pass — Coarse Planning + Loop Robustness (timeout escape, orphaned status)

Date: 2026-06-11

### Coarse Planning (CodingTaskService.cs)

The 7-step granular plan from pass 4 (split a single file into "create file" / "define class" / "add usings") was a primary cause of the `NO_EDITS_NEEDED` cascade. Fixed:
- LLM planning prompt now requests 2-4 COARSE steps where each step produces COMPLETE files, with the explicit shape: (1) implement feature file(s); (2) write test file(s); (3) build + run tests. Never split one file across steps.
- Template fallback plan rewritten from 5 vague steps to 3 file-complete steps.
- Result verified live: the same task that produced 7 granular steps now produces 3 coarse steps (implement → test → verify).

### Loop Robustness — Two Bugs Found on the Second Live Run

On the coarse-plan run, the loop died mid-step-1 after ~7.5 min and left the task **orphaned at `in_progress` with `isRunning=false`** (never reached a terminal status). Root cause from the API log stack trace (`ModelRouter.GenerateAsync` line 79 → `RunLoopAsync` → `StartLoop`):

1. **Ollama contention + timeout escape.** DARCI's own autonomous core (`Darci.Tools.Ollama.OllamaClient`, gemma4) runs in the background and competes with the coding loop's qwen calls on a single local Ollama. qwen generation exceeded ModelRouter's 8-min `HttpClient.Timeout`, which throws `TaskCanceledException`. `GenerateAsync`'s catch filter was `when (ex is not OperationCanceledException)` — and `TaskCanceledException : OperationCanceledException`, so the timeout slipped past the catch and propagated as an unhandled exception.
   - **Fix:** `GenerateAsync` and `GetEmbeddingAsync` now `catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }` (genuine caller cancellation propagates) followed by a broad `catch` that returns empty (HttpClient timeout = caller token NOT cancelled = soft failure → loop retries). Timeout bumped 8 → 12 min.

2. **No terminal-status guarantee.** When the loop threw, `StartLoop`'s catch logged but never wrote a terminal status, orphaning the task forever.
   - **Fix:** added `MarkTaskAbortedAsync(taskId, ex)` called from `StartLoop`'s catch. It sets status `failed` with `LastStepResult = "Loop aborted: <type>: <message>"`, but only overwrites non-terminal statuses (`in_progress`/`planned`/`planning`). A task can no longer be orphaned.

### Operational Notes

- The live API runs from the compiled `Darci.Api.exe`. Starting the exe directly does NOT read `launchSettings.json`, so it binds the ASP.NET default port 5000. To match the project's 5081, start with `ASPNETCORE_URLS=http://localhost:5081`.
- The coding DB is keyed to the API process's path resolution; restarting can surface a different `darci.db` than a prior ad-hoc instance — re-import the workspace if `GET /coding/workspaces` returns empty after a restart.
- Ollama contention between DARCI's autonomous core and the coding loop is the main performance risk on modest hardware. The robustness fixes make it survivable (graceful retry + guaranteed terminal status); a future improvement is to serialize or pause core autonomy while a coding loop runs, and/or trim the full-file context budget (currently up to 5 files × 50 KB) to speed generation.

### Verification

- `dotnet build DARCI.sln --no-restore -v:minimal` — 0 errors, 0 warnings.

---

## Sixth-Pass — Full-File Context Corruption (third live run)

Date: 2026-06-11

### What the Third Live Run Revealed

With coarse planning + robustness fixes in place, the run no longer orphaned and **confidence capture worked** (`conf=0.95` then `0.9` recorded live — item 4b validated). Step 1 wrote files and the build passed once. But the run went off the rails on a NEW axis, and was also impractically slow (~1.5h+ and never finished) due to Ollama contention with DARCI's autonomous core.

Inspecting the sandbox mid-run showed qwen2.5-coder:7b had:
- Created `WorkspaceService.cs` / `WorkspaceServiceTests.cs` — **wrong names** (task asked for `WorkspaceHealthReport`), referencing nonexistent `IWorkspaceService`/`Workspace` types → build broke.
- **Rewritten an unrelated file**, `WorkspaceEmbeddingService.cs` (166 lines churned).
- **Corrupted `Darci.Memory.Confidence/Models/SynthesisResult.cs`** in a different project — injected a UTF-8 BOM and stripped the trailing newline.

### Root Cause

The pass-3 "full-file context" change sent up to 5 complete files under the header `### {relativePath}` — which is nearly identical to the `### FILE: {path}` edit format the model is instructed to emit. The 7B model could not distinguish "reference" from "edit target": it mimicked the header and regenerated the reference files, corrupting files it was only meant to read. The change intended to *help* (full files so code isn't dropped) actively *harmed* with a small local model.

Note the guards held: because the build was broken, the loop did NOT report success — it kept retrying (no false `completed`). The mandatory test gate would have forced `verification-failed` had it settled.

### Fix (CodingAgentLoop.BuildStepPromptAsync)

- Reference files are now clearly labeled **READ-ONLY** with a visually distinct delimiter (`----- REFERENCE (DO NOT EDIT): path -----`) that cannot be confused with the `### FILE:` edit format, plus an explicit instruction not to emit FILE blocks for them.
- Added instruction: "Create or modify ONLY the file path(s) explicitly named in the TASK; use the exact class names and signatures the task specifies."
- Reduced `MaxStepPromptContextFiles` 5 → 3 (less corruption surface, faster generation).

### Open Issues / Recommendations

- **Ollama contention is the dominant practical blocker.** DARCI's autonomous core (gemma4) and the coding loop (qwen) thrash a single local Ollama; each generation balloons to many minutes and full runs exceed ~1.5h. Recommend pausing/yielding DARCI core autonomy while a coding loop runs (e.g., a mutex or a "focus mode" that suspends the core's periodic generation), and/or a dedicated Ollama instance for coding.
- **Model adherence:** even with coarse plans, qwen2.5-coder:7b drifted from the spec (wrong class name/design). Consider re-grounding each step prompt in the literal target filenames/signatures from the task, and a hard post-patch guard that rejects writes to files neither newly created nor named in the task.
- The int-vs-double behavioral trap was never reached because the model never produced the requested class. Behavioral verification (guards 1–2) remains validated by construction but not yet by a green end-to-end run.

### State Left For Review

- Live API is currently STOPPED (killed to halt the runaway loop). Restart with `ASPNETCORE_URLS=http://localhost:5081` from `DARCI-v4/`.
- Sandbox `C:\Users\aiden\DarciSandbox` reset to clean baseline `cbef061`.
- All code changes compile; full-solution `dotnet build DARCI.sln` passes when the API is not holding DLL locks.

---

## Seventh-Pass — Backfill (Nodes layer) + Model Focus Gate

Date: 2026-08-23

### Why this entry exists

This log stopped at the sixth pass (2026-06-11) while commits ran through 2026-06-25. Two weeks
of work went undocumented, and a cold session reading only this log built a materially wrong
picture of the system. That is the failure this entry closes.

### Backfill — what shipped 2026-06-12 → 2026-06-25 and was never logged

New first-class project **`Darci.Nodes`** (in `DARCI.sln`), ~840 lines:

- `NodePacket.cs` — `NodePacket` (:10), `NodePacketStatus` (:147), `InvalidNodeTransitionException` (:163)
- `NodePrimitives.cs` — `NodeId` (:11), `Capability` (:26), `NodeState` (:42) — all C# enums;
  `NodeLogEntry` (:112), `PacketPayload` (:135)
- `INodePacketStore.cs` / `SqliteNodePacketStore.cs` (362 lines)
- `NodeWatchdog.cs` — generalises the fifth-pass orphaned-task fix into a reusable watchdog

Supporting work:

- **`Darci.Nodes.Tests`** — 4 test files (`ConfidenceTests`, `NodeStateMachineTests`,
  `NodeWatchdogTests`, `SqliteNodePacketStoreTests`), in the solution
- **`Darci.Coding/CodingNodeTracker.cs`** (157 lines) — mirrors every coding run as a node packet;
  optional store, all calls best-effort
- **`Darci.Api/NodeWatchdogService.cs`** + `Program.cs` wiring (registration, init, REST endpoints)
- **`Darci.Coding.Tests/CodingModelsTests.cs`** — expanded by 530 lines
- **`docs/NODE_PACKET_PROTOCOL.md`** (269 lines) — architecture audit + design

**Not shipped:** `INodeRouter`. Zero references in the codebase. Packet, store, state machine and
watchdog exist; routing does not. That remains the next build step.

### Sixth-pass blockers — status check

Re-verified 2026-08-23 against `8ba29f3`:

- **Ollama contention** — was still unfixed. No mutex, semaphore, or focus-mode logic existed in
  `Darci.Coding/*.cs` or `Program.cs`. **Addressed in this pass (below).**
- **Model adherence / writes to files not named in the task** — still unfixed. No post-patch path
  guard in `PatchApplier.cs` or `CodingAgentLoop.cs`. **Still open.**

### Model Focus Gate (this pass)

**Problem.** `Darci.Tools.Ollama.OllamaClient` (living loop, `gemma4:e4b`) and
`Darci.Coding.ModelRouter` (coding loop, `qwen2.5-coder:7b`) both default to the same Ollama at
`http://localhost:11434` and request *different* models. When VRAM cannot hold both resident,
every alternation forces an unload/reload of a multi-GB model — the direct cause of the >90 minute
runs recorded in the sixth pass.

**Why not a second Ollama instance.** On single-GPU hardware two servers still compete for the
same VRAM and compute, each trying to keep its own model resident. It helps only with a second GPU
or VRAM sized for both models at once — neither assumable for AI Society team leads. It also
doubles the bootstrapper's install and config surface.

**Implementation.**

- `Darci.Shared/ModelFocus.cs` — `IModelFocus` / `ModelFocus`, a `SemaphoreSlim(1,1)` gate with
  holder name, timestamps, and acquisition/soft-skip counters. `Darci.Shared` is a leaf project
  (no project references), so both callers can depend on it without a cycle.
- `AcquireAsync(holder, ct)` — waits indefinitely. Used by `CodingAgentLoop.StartLoop`, which now
  holds an exclusive lease for the entire run and releases it in `finally` before deregistering.
- `TryAcquireAsync(holder, maxWait, ct)` — returns null on timeout. Used by `OllamaClient.Generate`
  and `.GetEmbedding`, which **soft-skip** rather than block: `Generate` returns the sentinel
  `OllamaClient.SkippedForFocus`, `GetEmbedding` returns an empty list. Both mirror the existing
  error-path conventions, so callers that already tolerate a sentinel keep working.
- Perception, messaging, and non-model actions continue during a coding run — only model calls
  yield.
- `Darci.Coding.csproj` gained a `Darci.Shared` ProjectReference.
- Registered in `Program.cs` as a singleton; status at **`GET /model-focus/status`** (holder,
  held-for seconds, total acquisitions, total soft-skips).

**Narrow gate (revised 2026-08-23 — supersedes the first cut).**

The first implementation gated `IOllamaClient` wholesale. That was too broad: a direct user message
would block behind a coding run, so DARCI went silent for up to an hour. For the reference build
that is actively harmful — a team lead who pokes at a core and gets nothing concludes it is broken.

The contention recorded in the sixth pass came from long-running *background* work competing with
the coding loop, not from short foreground replies. So the gate now distinguishes them:

- `ModelCallKind.Background` (default) — Think cycles, memory consolidation, LLM-backed research,
  goal decomposition, CAD planning. **Yields** during a coding run.
- `ModelCallKind.Foreground` — `Toolkit.GenerateReply`, the path behind `ActionType.Reply`.
  **Passes through** under the default policy.

Policy lives in one place, `IModelFocus.TryAcquireForAsync(holder, kind, maxWait, ct)`, so callers
do not each re-derive it.

**Configuration — `DARCI_FOCUS_MODE`:**

| Mode | Background | Foreground reply | For |
|---|---|---|---|
| `narrow` *(default)* | yields | passes through | most hosts |
| `broad` | yields | yields | constrained VRAM — a mid-run reply forces a model swap costing tens of seconds, and repeated messages thrash |
| `off` | — | — | VRAM for both models resident, or a second GPU |

- `DARCI_FOCUS_CORE_WAIT_SECONDS` — background patience before soft-skipping. Default 5.
- `DARCI_FOCUS_MODE_ENABLED=false` still honoured as an alias for `off`.

`GET /model-focus/status` reports the active mode plus `totalForegroundBypasses` — replies that
passed through mid-run. Non-zero on a low-VRAM host is the signal to switch to `broad`.

> **Dependency for the bootstrapper (work order item 2):** the model tier recommendation must carry
> a default focus mode with it. A host sized for one model at a time wants `broad`; a host with
> headroom for both resident wants `narrow` or `off`. Tiering that does not also set this produces
> cores that either feel broken (silent under `broad`) or thrash (swapping under `narrow`). This
> note is duplicated at `ModelFocus.CurrentMode` so the tiering work encounters it in code.

### FINDING: every persisted enum in Darci.Nodes round-trips as a raw integer

Surfaced while scoping the `NodeId` open-set change. **Not caused by that change — a latent bug in
committed Phase 0 code.**

`SqliteNodePacketStore` stores five enum-valued columns as `INTEGER`:

| Table | Column | Enum |
|---|---|---|
| `node_packets` | `address` | `NodeId` |
| `node_packets` | `requested_capability` | `Capability` |
| `node_packets` | `state` | `NodeState` |
| `node_log` | `node` | `NodeId` |
| `node_log` | `state_after` | `NodeState` |

Write path casts to int (`(int)p.State` :144, `(int)e.Node` :168, `(int)e.StateAfter` :170).
Read path casts back unchecked (`(NodeId)reader.GetInt32(...)` :303/:331,
`(Capability)…` :332, `(NodeState)…` :305/:333).

**Mitigating:** the enums carry explicit values (`Orchestrator = 0` … `Cad = 5`), so merely
reordering declarations does not shift them. This is better than implicit ordinals.

**Still broken in two ways:**

1. *Nothing enforces value stability.* No test pins the numbers. A contributor inserting
   `Biomed = 3` and renumbering `Knowledge`/`Cad` silently reinterprets every stored row — a
   `Knowledge` packet reads back as `Cad`. No exception, no failed parse, just wrong data.
2. *The read cast is unchecked.* A C# cast from an out-of-range int produces an invalid enum
   instance rather than throwing. A row written by newer code and read by older code yields a
   nonsense `NodeState` that `IsTerminal()` reports as non-terminal — so the watchdog either
   sweeps a finished packet or fails to sweep a stuck one. That is a correctness hole in the exact
   subsystem built to make orphaning "structurally impossible."

Indexes `ix_node_packets_state`, `ix_node_packets_lease`, and `ix_node_log_node_success` are built
on these integer columns, so any type change touches indexes too.

**Consequence for the open-`NodeId` work:** string identifiers of the form `author/node-name`
cannot live in an `INTEGER` column. `node_packets.address` and `node_log.node` must migrate to
`TEXT` — two columns, two tables, two indexes. That migration is a prerequisite, not a detail, and
its strategy is Tinman's call. **Not implemented; reported per the standing instruction.**

Recommended regardless of the `NodeId` decision: pin `NodeState` and `Capability` numeric values
with a test, and make the read path a checked parse that fails loudly on an unknown value.

### Verification — PASSED (2026-08-23)

`dotnet restore DARCI.sln` followed by `dotnet build DARCI.sln -v:minimal`:
**0 errors, 18/18 projects built**, including `Darci.Shared`, `Darci.Tools`, `Darci.Coding`,
`Darci.Core`, and `Darci.Api`.

Sole warning is **pre-existing and unrelated** to this pass:
`Darci.Coding.Tests/CodingModelsTests.cs(257,9): warning xUnit2002 — Do not use Assert.NotNull()
on value type '(string Name, string Kind, string Signature)'`. It arrived with the 530-line test
expansion of 2026-06-25. Worth fixing on its own merits: `Assert.NotNull()` on a value type can
never fail, so that assertion is dead weight in a suite whose job is catching regressions — the
same shape of problem as the fourth-pass false-success bug, one level down.

**What the green build does and does not prove.** It proves the code compiles and the new
`Darci.Coding` → `Darci.Shared` reference introduces no cycle. It does **not** prove focus-mode
behaviour. There is no test covering `ModelFocus`: narrow-mode foreground bypass, broad-mode
yielding, off-mode transparency, and lease release on the abort path are all unverified. Given
`Darci.Nodes.Tests` and `Darci.Coding.Tests` already exist, a `ModelFocusTests` is cheap and
should land before this is trusted under a real run.

*(Original note retained: the SDK was unavailable in the environment this pass was authored from —
the `dot.net` install script is blocked (HTTP 403) by network policy — so the build was run by
Tinman on the host.)*

**Outstanding, must run on the host with `Darci.Api` stopped:**

```powershell
cd DARCI-v4
dotnet restore DARCI.sln
dotnet build DARCI.sln -v:minimal
```

Nothing in this pass should be considered landed until that returns 0 errors.

> **Do NOT use `--no-restore` after a `git reset --hard` or a fresh clone.** First attempt on
> 2026-08-23 used it and produced 15 errors — all `NETSDK1064` / `NETSDK1004`, none of them
> compile errors. Cause below.

### FINDING: committed `obj/` artifacts break builds on any machine but the one that restored them

`.gitignore` carries `**/bin/` and `**/obj/` (lines 8–9), but **287 `obj/` files remain tracked** —
they were committed before those rules were added, and `.gitignore` does not untrack existing
files.

Consequence: `obj/project.assets.json` and `obj/project.nuget.cache` are version-controlled. They
encode absolute NuGet paths from whichever machine last restored. Pulling them onto a different
machine yields `NETSDK1064: Package <X> was not found` for essentially every package, because the
assets file points at a package cache that does not exist there. `Darci.Nodes` and
`Darci.Nodes.Tests` fail differently — `NETSDK1004`, no assets file at all — because their `obj/`
was never committed.

**Why this matters well beyond one bad build:** every AI Society team lead who clones this repo
inherits Tinman's `project.assets.json` files and hits a wall of 15 errors on first build, before
DARCI does anything. That is an onboarding blocker for the bootstrapper (work order item 2) and it
directly violates the node-contract bar — "if writing a node takes a week to figure out, nobody
writes one."

**Recommended fix (repo-wide, Tinman's call — not applied):**

```powershell
git rm -r --cached DARCI-v4/*/obj DARCI-v4/*/bin
git commit -m "Untrack bin/obj; .gitignore rules already present but never took effect"
```

287 tracked files disappear from the index while staying on disk. After this, a fresh clone plus
`dotnet restore` works on any machine. Worth doing before contributors arrive, not after.

### Documentation trued up alongside

- `docs/NODE_PACKET_PROTOCOL.md` — the "DRAFT / not implemented / do not commit" header was false
  once Phase 0 shipped; replaced with a partial-implementation status and a reading note. §6
  now records §6.1 resolved (out-of-process transport) and §6.2 resolved (living-loop integration
  deferred past the semester) per the Dispatch Brief of 2026-08-23.
- `DARCI_SOCIETY_PLAN_RECONCILED.md` — reconciliation of the AI Society plan against `8ba29f3`.

### Next

1. Run the build above.
2. Post-patch path guard — reject writes to files neither newly created nor named in the task
   (the remaining sixth-pass blocker).
3. Open the `Capability` set, per the §6.1 cascade.
4. `INodeRouter`.
