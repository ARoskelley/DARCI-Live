# Claude Code Task: Build OnnxDecisionNetwork and Wire Into Decision Pipeline

## Context

DARCI v4 has completed Phase 1 (data collection) and Phase 2 (behavioral cloning + DQN training).
We now have trained ONNX models ready to deploy. This task builds the C# inference
layer and wires it into the living loop so DARCI can use the neural network for decisions.

Read ARCHITECTURE.md and CLAUDE.md before starting. The interface is already defined
in `Darci.Brain/IDecisionNetwork.cs`.

## What Needs to Be Built

### 1. OnnxDecisionNetwork (new file: `Darci.Brain/OnnxDecisionNetwork.cs`)

Implements `IDecisionNetwork` using ONNX Runtime for inference.

**NuGet dependency:** Add `Microsoft.ML.OnnxRuntime` (version 1.16+ or latest stable) to `Darci.Brain.csproj`.

**Constructor:**
- Takes `ILogger<OnnxDecisionNetwork>`, `ExperienceBuffer`, and a `string modelPath`
- Loads the ONNX model via `InferenceSession`
- If the model file doesn't exist at `modelPath`, set `IsAvailable = false` and log a warning
  (DARCI falls back to the priority ladder gracefully)

**Key implementation details:**

```csharp
// Predict(): feed state vector, get logits back
public float[] Predict(float[] stateVector)
{
    // Create OrtValue from float[28]
    // Input name is "state_vector" (matches ONNX export)
    // Output name is "action_logits" (matches ONNX export)
    // Returns float[10]
}

// SelectAction(): the main decision method
public int SelectAction(float[] stateVector, bool[] actionMask)
{
    // 1. Get logits from Predict()
    // 2. Apply action mask: set logits[i] = float.NegativeInfinity where mask[i] == false
    // 3. Epsilon-greedy: with probability Epsilon, pick random valid action
    //    Otherwise pick argmax of masked logits
    // 4. Return action ID (0-9)
}
```

**Epsilon schedule:**
- Start at 0.3 (less exploration since we're initialized from teacher)
- Decay linearly over training steps toward 0.05
- Expose via `Epsilon` property
- Decay happens in `TrainAsync()` or can be stepped externally

**Thread safety:**
- `InferenceSession` is thread-safe for concurrent `Run()` calls
- `RecordExperience` should be fire-and-forget (don't block the living loop)
- Use the existing `ExperienceBuffer` for storage

**RecordExperience:**
```csharp
public void RecordExperience(float[] state, int action, float reward, float[] nextState, bool isTerminal = false)
{
    // Fire-and-forget to ExperienceBuffer
    _ = _buffer.StoreAsync(new Experience
    {
        State = state,
        Action = action,
        Reward = reward,
        NextState = nextState,
        IsTerminal = isTerminal,
        Timestamp = DateTime.UtcNow
    });
}
```

**TrainAsync:** For now, this can be a no-op or log a message. Real online training
happens in Python. The C# side just does inference + experience collection.

**SaveModelAsync / LoadModelAsync:** SaveModel can be no-op (Python handles this).
LoadModel should reload the InferenceSession from a new ONNX file path, allowing
hot-swapping of models without restarting DARCI.

### 2. Modify Decision.cs

The current `Decide()` method encodes the state, runs `RunPriorityLadder()`, and logs.
Add neural network decision-making with fallback to priority ladder.

**Add `IDecisionNetwork` as a constructor dependency:**

```csharp
public Decision(
    ILogger<Decision> logger,
    IToolkit tools,
    IGoalManager goals,
    IStateEncoder encoder,
    ExperienceBuffer buffer,
    IDecisionNetwork network)  // NEW
```

**Modify Decide():**

```csharp
public async Task<DarciAction> Decide(State state, Perception perception)
{
    var stateVector = _encoder.Encode(state.ToEncoderInput(perception));
    
    DarciAction action;
    bool networkDecided = false;
    float? confidence = null;
    int networkChoice = -1;
    
    if (_network.IsAvailable)
    {
        // Build action mask from current state
        var actionMask = BuildActionMask(state, perception);
        
        // Get network's decision
        networkChoice = _network.SelectAction(stateVector, actionMask);
        
        // Calculate confidence (softmax probability of chosen action)
        var logits = _network.Predict(stateVector);
        confidence = Softmax(logits, actionMask)[networkChoice];
        
        // USE the network's decision
        action = BrainActionToDarciAction(networkChoice, state, perception);
        networkDecided = true;
        
        _logger.LogDebug(
            "Neural decision: {Action} (confidence: {Conf:P1})",
            (BrainAction)networkChoice, confidence);
    }
    else
    {
        // Fallback to v3 priority ladder
        action = await RunPriorityLadder(state, perception);
    }
    
    // Always log the decision
    _ = LogDecision(stateVector, action, networkDecided, confidence, networkChoice);
    
    return action;
}
```

**Helper methods needed:**

```csharp
// Build the action validity mask (ARCHITECTURE.md section 4.3)
private bool[] BuildActionMask(State state, Perception perception)
{
    var mask = new bool[10];
    Array.Fill(mask, true);
    
    bool hasMessages = perception.NewMessages.Any();
    bool hasGoals = /* check goal manager for active goals */;
    bool hasPendingMemories = /* check state or memory store */;
    bool lowEnergy = state.Energy < 0.2f;
    bool quietHours = /* check time or state flag */;
    
    if (!hasMessages)
    {
        mask[(int)BrainAction.ReplyToMessage] = false;
        mask[(int)BrainAction.NotifyUser] = false;
    }
    if (!hasGoals) mask[(int)BrainAction.WorkOnGoal] = false;
    if (!hasPendingMemories) mask[(int)BrainAction.ConsolidateMemories] = false;
    if (lowEnergy)
    {
        mask[(int)BrainAction.Research] = false;
        mask[(int)BrainAction.Think] = false;
    }
    if (quietHours) mask[(int)BrainAction.NotifyUser] = false;
    
    return mask;
}

// Convert BrainAction ID back to a DarciAction that Act() can execute.
// Look at RunPriorityLadder() to see how each action type is currently
// constructed — the network picks WHICH action but the construction
// logic (choosing which message to reply to, which goal to work on, etc.)
// stays the same.
private DarciAction BrainActionToDarciAction(int brainAction, State state, Perception perception)
{
    return (BrainAction)brainAction switch
    {
        BrainAction.Rest => new DarciAction { Type = ActionType.Rest, RestDuration = TimeSpan.FromSeconds(5) },
        BrainAction.ReplyToMessage => /* build reply action from top message in perception */,
        BrainAction.Research => /* build research action */,
        BrainAction.CreateGoal => /* build create goal action */,
        BrainAction.WorkOnGoal => /* build work on goal action */,
        BrainAction.StoreMemory => /* build store memory action */,
        BrainAction.RecallMemories => /* build recall action */,
        BrainAction.ConsolidateMemories => /* build consolidate action */,
        BrainAction.NotifyUser => /* build notify action */,
        BrainAction.Think => /* build think action */,
        _ => new DarciAction { Type = ActionType.Rest }
    };
}

// Softmax with masking (for confidence calculation)
private static float[] Softmax(float[] logits, bool[] mask)
{
    var masked = new float[logits.Length];
    for (int i = 0; i < logits.Length; i++)
        masked[i] = mask[i] ? logits[i] : float.NegativeInfinity;
    
    float max = masked.Max();
    var exp = masked.Select(x => x == float.NegativeInfinity ? 0f : MathF.Exp(x - max)).ToArray();
    float sum = exp.Sum();
    return exp.Select(x => x / sum).ToArray();
}
```

### 3. Update Program.cs DI Registration

Add OnnxDecisionNetwork to the service container:

```csharp
// After the ExperienceBuffer registration:

// Decision Network: loads ONNX model for neural inference
var modelPath = Path.Combine(AppContext.BaseDirectory, "Models", "darci_policy.onnx");
builder.Services.AddSingleton<IDecisionNetwork>(sp =>
    new OnnxDecisionNetwork(
        sp.GetRequiredService<ILogger<OnnxDecisionNetwork>>(),
        sp.GetRequiredService<ExperienceBuffer>(),
        modelPath));

// Update Decision registration to include IDecisionNetwork:
builder.Services.AddSingleton<Decision>(sp =>
    new Decision(
        sp.GetRequiredService<ILogger<Decision>>(),
        sp.GetRequiredService<IToolkit>(),
        sp.GetRequiredService<IGoalManager>(),
        sp.GetRequiredService<IStateEncoder>(),
        sp.GetRequiredService<ExperienceBuffer>(),
        sp.GetRequiredService<IDecisionNetwork>()));  // ADD THIS PARAM
```

**Model file location:** The ONNX file should end up at
`Darci.Api/bin/Debug/net8.0/Models/darci_policy.onnx`.
Create a `Models/` directory in the Api project and configure the .csproj to copy it:

```xml
<ItemGroup>
  <Content Include="Models\*.onnx" CopyToOutputDirectory="PreserveNewest" />
</ItemGroup>
```

Or just create the directory manually in the bin output — either works.

### 4. Add API Endpoint for Model Hot-Swap

Add to Program.cs minimal API section:

```csharp
// Hot-swap the neural model without restarting DARCI
app.MapPost("/brain/load-model", async (IDecisionNetwork network) =>
{
    var modelPath = Path.Combine(AppContext.BaseDirectory, "Models", "darci_policy.onnx");
    if (!File.Exists(modelPath))
        return Results.NotFound(new { error = "No model file found at expected path" });
    
    await network.LoadModelAsync(modelPath);
    return Results.Ok(new { 
        message = "Model loaded successfully",
        isAvailable = network.IsAvailable 
    });
});
```

### 5. Update /brain/status Endpoint

Modify the existing brain status endpoint to include network info:

```csharp
app.MapGet("/brain/status", async (ExperienceBuffer buffer, IStateEncoder encoder, IDecisionNetwork network) =>
{
    var experienceCount = await buffer.CountAsync();
    return Results.Ok(new
    {
        version = "v4.0",
        phase = network.IsAvailable ? "Phase 3 — Neural Decision Making" : "Phase 1 — Data Collection",
        stateVectorDimensions = encoder.Dimensions,
        experienceBufferCount = experienceCount,
        networkAvailable = network.IsAvailable,
        networkEpsilon = network.Epsilon,
        networkTrainingSteps = network.TrainingSteps,
        networkMode = network.IsAvailable ? "live" : "fallback"
    });
});
```

## File Summary

| File | Action |
|------|--------|
| `Darci.Brain/Darci.Brain.csproj` | Add `Microsoft.ML.OnnxRuntime` NuGet package |
| `Darci.Brain/OnnxDecisionNetwork.cs` | NEW — implements IDecisionNetwork with ONNX Runtime |
| `Darci.Core/Decision.cs` | MODIFY — add IDecisionNetwork dependency, neural decision path |
| `Darci.Api/Program.cs` | MODIFY — register IDecisionNetwork, update Decision DI, add endpoints |

## Critical Notes

- If the ONNX model file doesn't exist, everything still works via fallback. Graceful degradation is essential.
- `darci_teacher.onnx` (behavioral cloning) can be used first. Swap to `darci_policy.onnx` (DQN trained) later via hot-load endpoint.
- Do NOT remove the priority ladder code from Decision.cs — it remains as fallback.
- ONNX Runtime's InferenceSession is thread-safe for Run() calls.
- The ONNX model input is named "state_vector" (float[28]), output is "action_logits" (float[10]).
