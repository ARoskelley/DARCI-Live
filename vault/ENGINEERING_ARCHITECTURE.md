# DARCI v4 — Engineering Intelligence Architecture

## Executive Summary

This document specifies the engineering tool layer for DARCI v4. It replaces the
current "LLM generates CadQuery scripts" approach with a neural, tool-based
architecture where trained networks make geometric and engineering decisions
through measurable, repeatable actions.

The design philosophy: **every engineering tool is a standalone service with a
numerical interface. DARCI's engineering networks learn to use them through
the same state → action → reward loop that drives her behavioral decisions.**

---

## 1. The Tool Service Pattern

Every engineering tool follows one contract. This is non-negotiable — it's what
makes the tools trainable, testable, and independently deployable.

### 1.1 Universal Tool Interface

```csharp
public interface IEngineeringTool
{
    /// <summary>Tool identity and capabilities.</summary>
    string ToolId { get; }
    string DisplayName { get; }
    int StateDimensions { get; }
    int ActionCount { get; }

    /// <summary>
    /// Encode the tool's current situation as a numerical vector.
    /// All values normalized to [0,1] or [-1,1].
    /// </summary>
    float[] GetState();

    /// <summary>
    /// Which actions are valid right now.
    /// bool[ActionCount] — true = action is available.
    /// </summary>
    bool[] GetActionMask();

    /// <summary>
    /// Execute an action with continuous parameters.
    /// Returns the new state vector and a quality metrics bundle.
    /// </summary>
    ToolStepResult Execute(int actionId, float[] parameters);

    /// <summary>
    /// Run full validation suite. Returns decomposed quality scores
    /// that feed directly into the reward calculator.
    /// </summary>
    ToolValidationResult Validate();

    /// <summary>Reset the tool to a clean starting state, optionally with a reference/target.</summary>
    void Reset(ToolResetOptions? options = null);

    /// <summary>Whether the tool has an active session.</summary>
    bool IsActive { get; }
}

public record ToolStepResult
{
    public float[] NewState { get; init; }
    public Dictionary<string, float> Metrics { get; init; }  // named quality scores
    public bool Success { get; init; }
    public string? ErrorMessage { get; init; }
}

public record ToolValidationResult
{
    public bool Passed { get; init; }
    public float OverallScore { get; init; }                   // 0–1 composite
    public Dictionary<string, float> CategoryScores { get; init; }  // decomposed
    public List<ToolViolation> Violations { get; init; }
}

public record ToolViolation
{
    public string Category { get; init; }     // "wall_thickness", "overhang", "clearance", etc.
    public string Severity { get; init; }     // "error", "warning", "info"
    public string Description { get; init; }
    public float? Value { get; init; }        // the measured value that violated
    public float? Threshold { get; init; }    // the threshold it should meet
    public float[]? Location { get; init; }   // optional XYZ location in model space
}

public record ToolResetOptions
{
    public string? ReferencePath { get; init; }      // STEP/STL file to use as target
    public string? ConstraintSpec { get; init; }     // JSON constraint specification
    public Dictionary<string, float>? Targets { get; init; }  // numerical targets
}
```

### 1.2 Engineering Decision Network Interface

Each tool gets its own neural network. The behavioral `IDecisionNetwork` handles
"which tool should I use?" The tool-specific networks handle "what should I do
within this tool?"

```csharp
public interface IEngineeringNetwork
{
    string ToolId { get; }
    int StateDimensions { get; }
    int ActionCount { get; }
    int ParameterDimensions { get; }  // continuous parameters per action

    /// <summary>
    /// Select an action given the tool's current state.
    /// Returns both a discrete action ID and continuous parameters.
    /// </summary>
    (int actionId, float[] parameters) SelectAction(float[] state, bool[] actionMask);

    /// <summary>Get raw action logits (for confidence/logging).</summary>
    float[] PredictLogits(float[] state);

    /// <summary>Get continuous parameters for a given action (actor-critic style).</summary>
    float[] PredictParameters(float[] state, int actionId);

    bool IsAvailable { get; }
    Task LoadModelAsync(string path);
}
```

### 1.3 How the Behavioral Network Integrates

The existing 10-action behavioral network gets a small extension. When it selects
`WorkOnGoal` and the current goal is an engineering task, it delegates to the
engineering orchestrator:

```
Behavioral Network selects: WorkOnGoal
    → Goal is "Design bracket with 5mm wall thickness"
    → Engineering Orchestrator activates
        → Checks goal type → selects Geometry Workbench
        → Runs Geometry Workbench network in a loop:
            get_state() → network.SelectAction() → execute() → validate()
            repeat until validation passes or max iterations reached
        → Reports result back to behavioral layer
        → Behavioral network sees reward from engineering outcome
```

---

## 2. Geometry Workbench (Tool 1 — Build First)

The Geometry Workbench is the core engineering tool. It maintains a live 3D model
in memory and exposes geometric manipulation through a numerical interface.

### 2.1 Technology Stack

**Kernel:** Open CASCADE Technology (OCCT) via CadQuery Python library.
CadQuery stays as the geometry engine but is now wrapped behind a stateful
REST API instead of being called through generated scripts.

**Mesh Analysis:** trimesh (Python). Fast triangle mesh operations for quality
metrics — wall thickness sampling, overhang detection, surface area, volume,
bounding box, mesh quality statistics.

**Service:** FastAPI Python service (replaces the current Darci.Python CAD service).
Stateful — holds the current workpiece in memory. Exposes the IEngineeringTool
contract as REST endpoints.

**Why Python, not C#:** CadQuery and trimesh are Python-native with deep
NumPy/SciPy integration. Wrapping them in C# via PythonNET adds complexity
for no benefit. The C# side calls the REST API like any other tool.

### 2.2 State Vector (64 dimensions)

The state vector captures everything the network needs to know about the current
part. Grouped by category:

#### Global Geometry (12 dimensions)
```
Index  Name                    Range   Description
─────────────────────────────────────────────────────────────────
 0     bbox_x                  0–1     Bounding box X / max_expected_size
 1     bbox_y                  0–1     Bounding box Y / max_expected_size
 2     bbox_z                  0–1     Bounding box Z / max_expected_size
 3     volume                  0–1     Volume / bbox_volume (fill ratio)
 4     surface_area            0–1     Surface area / max_expected_area
 5     center_of_mass_x        -1–1    CoM X relative to bbox center
 6     center_of_mass_y        -1–1    CoM Y relative to bbox center
 7     center_of_mass_z        -1–1    CoM Z relative to bbox center
 8     n_faces                 0–1     Face count / max_expected_faces
 9     n_edges                 0–1     Edge count / max_expected_edges
10     is_watertight           0 or 1  Mesh is closed manifold
11     symmetry_score          0–1     Bilateral symmetry measure
```

#### Wall Analysis (10 dimensions)
Sampled at multiple points across the part. Uses ray-casting through the mesh
to measure material thickness at strategic locations.
```
Index  Name                    Range   Description
─────────────────────────────────────────────────────────────────
12     min_wall_thickness      0–1     Thinnest wall / target_thickness
13     max_wall_thickness      0–1     Thickest wall / max_expected
14     mean_wall_thickness     0–1     Average wall / target_thickness
15     wall_std_dev            0–1     Thickness variation (uniformity)
16     pct_below_min           0–1     % of samples below minimum threshold
17     pct_above_max           0–1     % of samples above maximum threshold
18     thinnest_region_x       -1–1    Location of thinnest wall (normalized)
19     thinnest_region_y       -1–1    Location of thinnest wall (normalized)
20     thinnest_region_z       -1–1    Location of thinnest wall (normalized)
21     wall_violation_count    0–1     Count of wall violations / max_expected
```

#### Printability Analysis (10 dimensions)
Specific to additive manufacturing. Analyzes overhangs, bridges, support
requirements, and build orientation suitability.
```
Index  Name                    Range   Description
─────────────────────────────────────────────────────────────────
22     max_overhang_angle      0–1     Steepest unsupported overhang / 90°
23     overhang_area_pct       0–1     % of surface area that is overhang
24     support_volume_ratio    0–1     Estimated support volume / part volume
25     min_feature_size        0–1     Smallest feature / min_printable_size
26     bridge_max_span         0–1     Longest unsupported bridge / max_span
27     n_islands               0–1     Disconnected regions / max_expected
28     build_height            0–1     Height in build orientation / max_build
29     first_layer_area        0–1     Base contact area / min_required
30     has_enclosed_voids      0 or 1  Internal cavities that trap material
31     printability_score      0–1     Composite printability (0=unprintable)
```

#### Mesh Quality (8 dimensions)
Quality of the triangle mesh itself — important for downstream simulation.
```
Index  Name                    Range   Description
─────────────────────────────────────────────────────────────────
32     mesh_face_count         0–1     Triangle count / max_expected
33     min_aspect_ratio        0–1     Worst triangle aspect ratio
34     mean_aspect_ratio       0–1     Average triangle quality
35     pct_degenerate          0–1     % of near-degenerate triangles
36     max_edge_length         0–1     Longest edge / bbox_diagonal
37     min_edge_length         0–1     Shortest edge / bbox_diagonal
38     edge_length_ratio       0–1     min/max edge length (uniformity)
39     mesh_is_valid           0 or 1  No self-intersections, correct normals
```

#### Reference Comparison (10 dimensions)
When working toward a target shape or specification, these measure how close
the current part is to the goal.
```
Index  Name                    Range   Description
─────────────────────────────────────────────────────────────────
40     has_reference           0 or 1  Whether a reference shape is loaded
41     hausdorff_distance      0–1     Hausdorff distance / max_expected
42     mean_surface_deviation  0–1     Average surface deviation from reference
43     volume_ratio            0–1     Current volume / reference volume
44     bbox_similarity         0–1     How close bounding boxes match
45     feature_count_diff      0–1     |current_features - ref_features| / max
46     topology_match          0–1     How well face/edge topology matches
47     n_missing_features      0–1     Features in reference but not in current
48     n_extra_features        0–1     Features in current but not in reference
49     alignment_score         0–1     Pose alignment quality (ICP-based)
```

#### Task Context (6 dimensions)
Meta-information about the current engineering task.
```
Index  Name                    Range   Description
─────────────────────────────────────────────────────────────────
50     iterations_used         0–1     Steps taken / max_allowed_steps
51     time_elapsed            0–1     Time used / time_budget
52     last_action_success     0 or 1  Did the previous action succeed?
53     last_reward             -1–1    Normalized reward from last action
54     consecutive_failures    0–1     Failed actions in a row / max_before_abort
55     improvement_trend       -1–1    Rolling average of reward delta (improving?)
```

#### Constraint Satisfaction (8 dimensions)
How well the part meets specified engineering constraints.
```
Index  Name                    Range   Description
─────────────────────────────────────────────────────────────────
56     n_constraints           0–1     Total constraints / max_expected
57     n_satisfied             0–1     Satisfied / total constraints
58     n_violated              0–1     Violated / total constraints
59     worst_violation_mag     0–1     Magnitude of worst violation
60     total_violation_mag     0–1     Sum of all violation magnitudes
61     constraint_type_dim     0–1     Dimensional constraint satisfaction
62     constraint_type_tol     0–1     Tolerance constraint satisfaction
63     constraint_type_feat    0–1     Feature constraint satisfaction
```

**Total: 64 dimensions.** All normalized. No English anywhere.

### 2.3 Action Space (20 discrete actions + continuous parameters)

Each action takes a parameter vector. The network outputs both a discrete
action choice AND continuous parameters through a hybrid architecture:
discrete head (softmax over actions) + continuous head (parameter values
per action, tanh-activated to bound them).

#### Primitive Creation (actions 0–4)
```
ID  Action              Parameters (continuous, all [-1,1] normalized)
──────────────────────────────────────────────────────────────────────
 0  extrude             [direction_x, direction_y, direction_z, length, taper_angle]
 1  cut                 [direction_x, direction_y, direction_z, depth, taper_angle]
 2  revolve             [axis_x, axis_y, axis_z, angle, offset]
 3  add_cylinder        [center_x, center_y, center_z, radius, height]
 4  add_box             [center_x, center_y, center_z, size_x, size_y, size_z]
```

#### Feature Operations (actions 5–10)
```
ID  Action              Parameters
──────────────────────────────────────────────────────────────────────
 5  fillet_edges        [radius, edge_selector_x, edge_selector_y, edge_selector_z, max_edges]
 6  chamfer_edges       [distance, angle, edge_selector_x, edge_selector_y, edge_selector_z]
 7  shell               [thickness, face_selector_x, face_selector_y, face_selector_z]
 8  add_hole            [center_x, center_y, center_z, diameter, depth, direction_x]
 9  add_boss            [center_x, center_y, center_z, diameter, height, draft_angle]
10  add_rib             [start_x, start_y, direction_x, direction_y, thickness, height]
```

#### Transformation Operations (actions 11–14)
```
ID  Action              Parameters
──────────────────────────────────────────────────────────────────────
11  translate_feature   [feature_selector, delta_x, delta_y, delta_z]
12  scale_feature       [feature_selector, scale_x, scale_y, scale_z]
13  mirror              [plane_normal_x, plane_normal_y, plane_normal_z, plane_offset]
14  pattern_linear      [direction_x, direction_y, direction_z, count, spacing]
```

#### Repair & Refinement (actions 15–17)
```
ID  Action              Parameters
──────────────────────────────────────────────────────────────────────
15  thicken_wall        [face_selector_x, face_selector_y, face_selector_z, amount]
16  smooth_region       [center_x, center_y, center_z, radius, intensity]
17  remove_feature      [feature_selector_x, feature_selector_y, feature_selector_z]
```

#### Control Actions (actions 18–19)
```
ID  Action              Parameters
──────────────────────────────────────────────────────────────────────
18  validate            []  (no parameters — triggers full validation)
19  finalize            []  (declares the part complete — triggers final scoring)
```

**Parameter handling:** The continuous parameters are output by a separate
network head. Each parameter is in [-1, 1] and gets mapped to the actual
physical range by the workbench service. For example, `length` in [-1, 1]
maps to [0.1mm, max_dimension_mm] based on the current part scale.

**Maximum parameter vector: 6 floats per action.** Actions with fewer
parameters ignore the extras.

### 2.4 Action Masking

```
Condition                         Masked Actions
─────────────────────────────────────────────────────────────
No geometry loaded yet            All except 3 (add_cylinder), 4 (add_box)
Part is not watertight            15 (thicken_wall), 7 (shell) disabled
No reference loaded               Nothing masked (but ref comparison dims are 0)
Consecutive failures > 5          Only 15-17 (repair) and 18-19 (control) allowed
Max iterations reached            Only 18 (validate) and 19 (finalize) allowed
Validation already passed         Only 19 (finalize) allowed
```

### 2.5 Reward System

Decomposed rewards — the network gets specific feedback about what improved
or degraded, not just a single number.

#### Immediate Rewards (after every action)
```
Signal                              Reward      Rationale
────────────────────────────────────────────────────────────────
Action succeeded (geometry valid)   +0.1        Encourages valid operations
Action failed (bad geometry)        -0.3        Penalizes invalid operations
Wall thickness improved             +0.2–0.5    Scaled by improvement magnitude
Overhang reduced                    +0.2–0.5    Scaled by reduction magnitude
Volume closer to reference          +0.1–0.3    Scaled by improvement
Feature count closer to reference   +0.1–0.3    Scaled by improvement
Printability score improved         +0.2–0.5    Scaled by improvement
Constraint satisfied (new)          +0.8        Major progress signal
Constraint violated (new)           -0.5        Regression signal
Watertight maintained               +0.05       Ongoing integrity bonus
Watertight broken                   -1.0        Severe — integrity loss
Unnecessary action (no change)      -0.2        Penalizes no-ops
Created self-intersection           -0.8        Severe geometry error
```

#### Completion Rewards (on finalize)
```
Signal                              Reward      Rationale
────────────────────────────────────────────────────────────────
All constraints satisfied           +3.0        Primary objective
Validation fully passed             +2.0        Quality gate
Printability > 0.8                  +1.0        Manufacturable
Efficiency bonus (few iterations)   +0.5–2.0    Scaled inversely with steps used
Reference match > 0.9              +1.5        Close to target
Finalized with violations           -2.0        Premature completion
```

#### Learned Reward Model (Phase 2)
Same PEBBLE/preference approach as behavioral training. You review pairs
of engineering outcomes and pick which is better. The learned model handles
subjective quality that can't be expressed as simple thresholds — things like
"this fillet placement looks more natural" or "this wall distribution is
better for the load case."

### 2.6 Network Architecture

Larger than the behavioral network because the state and action spaces are bigger.
Still CPU-trainable given the user's hardware.

```
                         ┌─────────────────────┐
                         │  State Vector (64)   │
                         └──────────┬──────────┘
                                    │
                         ┌──────────▼──────────┐
                         │   Shared Trunk       │
                         │   64 → 256 (ReLU)    │
                         │   + LayerNorm        │
                         │   256 → 128 (ReLU)   │
                         │   + LayerNorm        │
                         └──────┬───────┬───────┘
                                │       │
                    ┌───────────▼──┐  ┌─▼───────────┐
                    │ Action Head  │  │ Parameter    │
                    │ (Discrete)   │  │ Head         │
                    │ 128 → 64     │  │ (Continuous) │
                    │ 64 → 20      │  │ 128 → 64    │
                    │ (logits)     │  │ 64 → 20×6   │
                    │              │  │ (tanh)       │
                    └──────────────┘  └─────────────┘
                         │                  │
                    action_id          parameters[6]
```

**Parameter count:** ~85,000 weights. Still trains in seconds on CPU.
Much larger than the behavioral network (4,160) because the problem is
harder — geometric reasoning needs more representational capacity.

**Training algorithm:** TD3 (Twin Delayed Deep Deterministic Policy Gradient)
instead of DQN. TD3 handles the hybrid discrete + continuous action space
naturally. It's the standard for robotics control with continuous parameters.
Uses two Q-networks (twin critics) for stability and delayed policy updates.

Alternatively: SAC (Soft Actor-Critic) with a discrete-continuous hybrid.
SAC adds entropy regularization which encourages exploration — useful when
the geometric search space is vast.

**Recommendation:** Start with SAC. It's more robust for exploration-heavy
problems, and the RLPD paper already showed it works well with prior data
seeding.

---

## 3. Board Designer (Tool 2)

### 3.1 Technology Stack

**Kernel:** KiCad 8+ Python API (`pcbnew` module).
KiCad runs as a library, not a GUI. The Python API can load boards, place
components, route traces, and run DRC — all programmatically.

**Service:** FastAPI Python service on its own port (e.g., 8002).

### 3.2 State Vector (48 dimensions)

```
Category                Dims    Description
─────────────────────────────────────────────────────────────────
Board Geometry           6      Board dimensions, area, layer count,
                                edge clearance, aspect ratio

Component Status        10      Total components, placed count,
                                placed ratio, avg component density,
                                largest unplaced, smallest placed,
                                overlap count, alignment score,
                                group proximity, rotation variance

Routing Status          10      Total nets, routed count, routed ratio,
                                total trace length, avg trace length,
                                via count, layer crossings, longest
                                unrouted, routing congestion, signal
                                integrity estimate

DRC Status              10      Total violations, clearance violations,
                                track width violations, via violations,
                                unconnected nets, courtyard overlaps,
                                silkscreen overlap, annular ring violations,
                                thermal relief issues, overall DRC score

Manufacturing           6       Min trace width, min clearance, min
                                drill size, copper balance (per layer),
                                impedance compliance, fab complexity score

Task Context             6      Iterations used, time elapsed,
                                last action success, last reward,
                                consecutive failures, improvement trend
```

### 3.3 Action Space (15 actions + continuous parameters)

```
ID  Action                  Parameters
──────────────────────────────────────────────────────────────────
 0  place_component         [component_selector, x, y, rotation, layer]
 1  move_component          [component_selector, delta_x, delta_y, delta_rotation]
 2  swap_components         [component_a, component_b]
 3  route_trace             [net_selector, start_x, start_y, end_x, end_y, width]
 4  route_via               [x, y, drill_size, from_layer, to_layer]
 5  change_trace_width      [trace_selector, new_width]
 6  delete_trace            [trace_selector]
 7  reroute_net             [net_selector, strategy]
 8  add_ground_plane        [layer, clearance]
 9  adjust_clearance        [region_x, region_y, radius, clearance_value]
10  run_drc                 []
11  auto_route_net          [net_selector, max_vias, preferred_layer]
12  group_components        [group_selector, arrangement_style]
13  set_trace_impedance     [trace_selector, target_impedance]
14  finalize                []
```

### 3.4 Reward System

```
Signal                                  Reward
────────────────────────────────────────────────────
DRC violation eliminated                +0.5 per violation
New DRC violation introduced            -0.5 per violation
Net connected                           +1.0
Net disconnected                        -1.0
All nets connected (milestone)          +3.0
DRC fully clean (milestone)             +3.0
Trace length reduced                    +0.1 (scaled)
Component overlap eliminated            +0.3
Failed action                           -0.3
Board finalized with clean DRC          +5.0
Board finalized with violations         -3.0
```

---

## 4. Parts Library & Selection (Tool 3)

### 4.1 Technology Stack

**Sources:** Local SQLite database of standard parts (initially populated from
open catalogs like McMaster parametric data). Future: API connections to
DigiKey, LCSC, McMaster via their REST APIs.

**Service:** C# service (this one stays in .NET since it's mostly database
queries and API calls, no geometric computation).

### 4.2 State Vector (32 dimensions)

```
Category                Dims    Description
─────────────────────────────────────────────────────────────────
Requirement Spec         12     Load rating needed, size constraints (L/W/H),
                                material type encoding, temperature range,
                                quantity needed, cost target, weight target,
                                tolerance class, environment (indoor/outdoor/
                                submersible), lifecycle requirement

Search Status            8      Candidates found, candidates evaluated,
                                best match score, search iterations,
                                query specificity, coverage of spec
                                dimensions, filter strictness, database
                                coverage

Best Candidate Match     12     Match score per spec dimension:
                                load_match, size_match, material_match,
                                temp_match, cost_match, weight_match,
                                tolerance_match, availability_score,
                                lead_time_score, standard_vs_custom,
                                datasheet_available, supplier_reliability
```

### 4.3 Action Space (10 actions)

```
ID  Action                  Parameters
──────────────────────────────────────────────────────────────────
 0  search_broad            [category_encoding, size_range]
 1  search_specific         [part_type, dimension_filters]
 2  filter_by_property      [property_id, min_value, max_value]
 3  relax_constraint        [constraint_id, relaxation_amount]
 4  tighten_constraint      [constraint_id, tightening_amount]
 5  evaluate_candidate      [candidate_id]
 6  compare_top_n           [n_candidates]
 7  select_part             [candidate_id]
 8  substitute_material     [current_material, new_material]
 9  request_custom_quote    [spec_encoding]
```

---

## 5. Simulation Bridge (Tool 4)

### 5.1 Technology Stack

**FEA:** CalculiX (open-source, command-line FEA solver).
**Kinematics:** MuJoCo (open-source, fast physics).
**Print simulation:** PrusaSlicer CLI or CuraEngine CLI for slicing analysis.

**Service:** FastAPI Python service (port 8003) wrapping all three engines.
Selects engine based on simulation type requested.

### 5.2 State Vector (40 dimensions)

```
Category                Dims    Description
─────────────────────────────────────────────────────────────────
Simulation Config       10      Sim type encoding, mesh density,
                                n_elements, n_nodes, boundary condition
                                count, load case count, material
                                properties summary (4 dims)

Results                 16      Max stress, max displacement, safety
                                factor, yield margin, max strain,
                                stress concentration factor, fatigue
                                life estimate, buckling load factor,
                                thermal max, thermal gradient,
                                natural frequency (first 3 modes),
                                convergence quality, runtime, result
                                confidence

Print Sim Results        8      Layer count, estimated time, estimated
                                material, support percentage, predicted
                                failures, warping risk, stringing risk,
                                overall print success probability

Task Context             6      (same pattern as other tools)
```

### 5.3 Action Space (12 actions)

```
ID  Action                  Parameters
──────────────────────────────────────────────────────────────────
 0  set_mesh_density        [region_x, region_y, region_z, density]
 1  refine_mesh_region      [center_x, center_y, center_z, radius, refinement]
 2  add_boundary_condition  [type_encoding, face_selector, magnitude]
 3  remove_boundary_cond    [bc_id]
 4  add_load_case           [type_encoding, magnitude, direction_x, direction_y, direction_z]
 5  set_material            [material_encoding]
 6  run_structural_sim      []
 7  run_thermal_sim         []
 8  run_print_sim           [orientation_x, orientation_y, layer_height, infill]
 9  accept_results          []
10  iterate_geometry        [modification_hint_encoding]  // feeds back to Geometry Workbench
11  finalize                []
```

---

## 6. Assembly Manager (Tool 5)

### 6.1 Technology Stack

**Kernel:** CadQuery assembly module + trimesh for interference detection.
**Service:** Part of the Geometry Workbench service (port 8001), extended endpoint.

### 6.2 State Vector (36 dimensions)

```
Category                Dims    Description
─────────────────────────────────────────────────────────────────
Assembly Structure       8      Part count, connection count, joint count,
                                total DOF, constrained DOF, is fully
                                constrained, has kinematic chain, depth

Interference             8      Pairs checked, interference count, min
                                clearance, mean clearance, max penetration,
                                interference volume, worst pair encoding (2)

Motion Analysis          8      Joints with motion defined, motion range
                                coverage, collision-free range, peak
                                reaction force, peak torque, center of
                                mass shift, stability margin, motion
                                smoothness

Assembly Quality         6      Fastener count, unfastened joints,
                                symmetry score, accessibility score,
                                assembly sequence validity, disassembly
                                possible

Task Context             6      (same pattern)
```

### 6.3 Action Space (12 actions)

```
ID  Action                  Parameters
──────────────────────────────────────────────────────────────────
 0  add_part                [part_id, position_x, position_y, position_z, rotation]
 1  move_part               [part_id, delta_x, delta_y, delta_z, delta_rotation]
 2  add_joint               [part_a, part_b, joint_type, axis]
 3  modify_joint            [joint_id, parameter_id, new_value]
 4  add_fastener            [position_x, position_y, position_z, fastener_type]
 5  check_interference      [part_a, part_b]
 6  check_all_interference  []
 7  run_motion_sweep        [joint_id, range_start, range_end, steps]
 8  adjust_clearance        [part_a, part_b, target_clearance]
 9  auto_align              [part_id, reference_face, target_face]
10  validate_assembly       []
11  finalize                []
```

---

## 7. Geometry Workbench Service — Detailed Implementation

This is the first service to build. The others follow the same pattern.

### 7.1 Project Structure

```
DARCI-Live/
├── DARCI-v4/
│   ├── Darci.Engineering/              ← NEW C# project
│   │   ├── IEngineeringTool.cs         ← Universal interface (§1.1)
│   │   ├── IEngineeringNetwork.cs      ← Network interface (§1.2)
│   │   ├── EngineeringOrchestrator.cs  ← Coordinates tool usage
│   │   ├── GeometryWorkbenchClient.cs  ← C# HTTP client for Python service
│   │   ├── BoardDesignerClient.cs      ← C# HTTP client (future)
│   │   ├── PartsLibraryService.cs      ← C# native service (future)
│   │   ├── SimulationBridgeClient.cs   ← C# HTTP client (future)
│   │   └── AssemblyManagerClient.cs    ← C# HTTP client (future)
│   │
│   ├── Darci.Engineering.Workbench/    ← NEW Python service
│   │   ├── requirements.txt
│   │   ├── main.py                     ← FastAPI app
│   │   ├── workbench/
│   │   │   ├── __init__.py
│   │   │   ├── engine.py               ← CadQuery geometry operations
│   │   │   ├── state_encoder.py        ← Generates 64-dim state vector
│   │   │   ├── action_executor.py      ← Maps action IDs to geometry ops
│   │   │   ├── mesh_analyzer.py        ← trimesh-based quality analysis
│   │   │   ├── validator.py            ← Full validation suite
│   │   │   ├── constraint_checker.py   ← Engineering constraint evaluation
│   │   │   └── reference_comparator.py ← Hausdorff distance, shape matching
│   │   ├── models/                     ← Stores reference parts for training
│   │   │   ├── primitives/             ← Simple training shapes
│   │   │   ├── mechanical/             ← Brackets, housings, mounts
│   │   │   └── prosthetic/             ← Domain-specific references
│   │   └── tests/
│   │       └── test_workbench.py
│   │
│   ├── Darci.Engineering.Training/     ← NEW Python training scripts
│   │   ├── requirements.txt
│   │   ├── train_geometry_bc.py        ← Behavioral cloning for geometry
│   │   ├── train_geometry_sac.py       ← SAC training for geometry
│   │   ├── train_geometry_sim.py       ← Simulated training episodes
│   │   ├── generate_training_parts.py  ← Creates training scenarios
│   │   └── models/                     ← Trained ONNX models
```

### 7.2 Python Service API Endpoints

```
POST   /workbench/reset             Reset with optional reference/constraints
GET    /workbench/state             Get current 64-dim state vector
GET    /workbench/action-mask       Get valid actions (bool[20])
POST   /workbench/execute           Execute action {action_id, parameters[6]}
POST   /workbench/validate          Run full validation, return decomposed scores
GET    /workbench/metrics           Get current quality metrics
GET    /workbench/thumbnail         Render current part as PNG (for UI/debugging)
POST   /workbench/export            Export current part as STEP/STL
GET    /workbench/health            Service health check

POST   /workbench/batch-execute     Execute multiple actions (for fast simulation)
POST   /workbench/load-reference    Load a reference STEP/STL for comparison
GET    /workbench/history           Get action history for current session
POST   /workbench/undo              Undo last action (keeps history for training)
```

### 7.3 Engine Implementation Notes

**State persistence:** The workbench holds a CadQuery `Workplane` object in memory.
Every action mutates it. The previous state is kept for undo. Thread-safe via a
lock on the workplane (one action at a time, but state reads can be concurrent).

**Parameter mapping:** The network outputs parameters in [-1, 1]. The engine maps
them to physical dimensions based on the current part's scale:

```python
def map_parameter(raw: float, part_bbox: BoundingBox, param_type: str) -> float:
    """Map [-1, 1] network output to physical dimension."""
    scale = max(part_bbox.x_size, part_bbox.y_size, part_bbox.z_size)

    if param_type == "length":
        return 0.1 + (raw + 1) / 2 * scale * 0.5    # 0.1mm to half the part size
    elif param_type == "position":
        return raw * scale * 0.6                       # within 60% of part extent
    elif param_type == "radius":
        return 0.05 + (raw + 1) / 2 * scale * 0.2    # 0.05mm to 20% of part size
    elif param_type == "angle":
        return raw * 180.0                             # -180 to +180 degrees
    # ... etc
```

**Edge/face selection:** The network can't pick specific edges by ID (the topology
changes every operation). Instead, it outputs a 3D position, and the engine selects
the nearest edge/face to that point. This is how robotic grasping networks handle
the same problem — position-based selection rather than enumeration.

**Failure handling:** If a CadQuery operation fails (invalid geometry, self-intersection),
the engine reverts to the previous state, returns `success: false`, and the network
gets a negative reward. This is expected and part of learning.

### 7.4 Mesh Analysis Implementation

Uses trimesh for all mesh quality metrics. The CadQuery B-rep is tessellated to
a triangle mesh for analysis, but the B-rep is kept as the source of truth.

```python
import trimesh
import numpy as np

class MeshAnalyzer:
    def __init__(self, mesh: trimesh.Trimesh):
        self.mesh = mesh

    def wall_thickness_samples(self, n_samples: int = 1000) -> dict:
        """
        Ray-cast from random surface points inward to measure wall thickness.
        Returns statistics and locations of thin spots.
        """
        points, face_idx = trimesh.sample.sample_surface(self.mesh, n_samples)
        normals = self.mesh.face_normals[face_idx]

        # Cast rays inward (negative normal direction)
        locations, index_ray, _ = self.mesh.ray.intersects_location(
            ray_origins=points + normals * 0.001,  # slight offset to avoid self-hit
            ray_directions=-normals
        )

        # Compute distances for rays that hit
        thicknesses = np.linalg.norm(locations - points[index_ray], axis=1)
        # ... return statistics

    def overhang_analysis(self, build_direction: np.ndarray = np.array([0, 0, 1]),
                          threshold_angle: float = 45.0) -> dict:
        """
        Identify faces with overhang angles exceeding the threshold.
        Returns overhang area, max angle, and affected face indices.
        """
        normals = self.mesh.face_normals
        cos_angles = np.dot(normals, -build_direction)
        angles = np.degrees(np.arccos(np.clip(cos_angles, -1, 1)))
        overhang_mask = angles > (90 + threshold_angle)
        # ... return analysis

    def printability_score(self) -> float:
        """Composite score from wall thickness, overhangs, bridges, islands."""
        # ... weighted combination of sub-scores
```

---

## 8. Training Pipeline for Geometry

### 8.1 Training Scenario Generator

Creates diverse training situations. The network needs to see a wide variety
of parts and modification challenges.

```python
# generate_training_parts.py

SCENARIOS = [
    # Basic shape matching: "make this box have rounded edges"
    {
        "type": "modify_to_match",
        "start": "box(10, 10, 10)",
        "reference": "box(10, 10, 10).edges().fillet(2)",
        "constraints": {"min_wall": 1.0},
        "max_steps": 20,
    },
    # Printability repair: "fix this part so it can be printed"
    {
        "type": "repair_printability",
        "start": "load_stl('part_with_overhangs.stl')",
        "constraints": {"max_overhang": 45, "min_wall": 0.8, "min_feature": 0.4},
        "max_steps": 50,
    },
    # Constraint satisfaction: "make a bracket meeting these specs"
    {
        "type": "constrained_design",
        "start": "box(20, 15, 5)",
        "constraints": {
            "target_volume_range": [800, 1200],
            "min_wall": 1.5,
            "holes": [{"diameter": 5, "position": "corners"}],
            "max_mass_grams": 15,
            "material": "PLA"
        },
        "max_steps": 100,
    },
    # Assembly interface: "this face needs to mate with a cylinder"
    {
        "type": "interface_design",
        "start": "box(30, 20, 10)",
        "mating_geometry": "cylinder(r=5, h=15)",
        "interface_type": "press_fit",
        "constraints": {"clearance": 0.1, "engagement_depth": 8},
        "max_steps": 60,
    },
]
```

### 8.2 Training Script (SAC with Hybrid Actions)

Same architecture as the behavioral training pipeline but adapted for
continuous parameters:

```
train_geometry_bc.py:
    Load solved examples → train network to imitate → export ONNX teacher

train_geometry_sac.py:
    Load teacher → seed replay buffer
    → Run training loop:
        1. Get state from workbench
        2. Network selects (action, parameters)
        3. Execute on workbench
        4. Compute reward from metrics delta
        5. Store experience
        6. SAC update (twin critics + delayed actor + entropy)
    → Export ONNX policy

train_geometry_sim.py:
    Same as above but runs against the workbench service
    with synthetic scenarios from the generator.
    Thousands of episodes per hour.
```

### 8.3 Behavioral Cloning Data for Geometry

The cold-start problem: how do you get initial training data when the network
starts from scratch?

**Approach 1 — Script replay.** Take existing CadQuery scripts (from DARCI v3's
CAD generation or from open CadQuery examples) and replay them step by step,
recording the state vector at each step and the action taken. This is the
geometric equivalent of logging the priority ladder's decisions.

**Approach 2 — Parametric perturbation.** Take a finished part, randomly perturb
it (remove fillets, change dimensions, add violations), and then the training
data is the sequence of operations to restore it. Like giving the network
a puzzle where the answer is known.

**Approach 3 — Self-play.** The network practices against the simulator with
random starts and high exploration. Early episodes are garbage but the reward
signal gradually shapes good behavior. This is how AlphaZero worked — no
human data at all, just self-play with reward.

**Recommendation:** Start with Approach 2 (cheapest, generates the most diverse
data), supplement with Approach 1 (leverages existing CadQuery scripts),
then let Approach 3 refine through real practice.

---

## 9. Implementation Roadmap

### Phase 1: Geometry Workbench Service (2-3 weeks)
- Build the Python FastAPI service with CadQuery engine
- Implement state encoder (64 dimensions)
- Implement all 20 actions with parameter mapping
- Implement mesh analysis (trimesh) for quality metrics
- Implement validation suite
- REST endpoints matching IEngineeringTool contract
- Unit tests for each action and state encoding

### Phase 2: C# Integration Layer (1-2 weeks)
- Build Darci.Engineering project
- IEngineeringTool and IEngineeringNetwork interfaces
- GeometryWorkbenchClient (HTTP client for Python service)
- EngineeringOrchestrator (coordinates tool selection and execution loops)
- Wire into Darci.Core so WorkOnGoal can delegate to engineering tools
- API monitoring endpoints (/engineering/workbench/status, etc.)

### Phase 3: Training Infrastructure (2-3 weeks)
- Training scenario generator
- Behavioral cloning from parametric perturbation
- SAC training script with hybrid discrete-continuous actions
- Simulation training loop (1000+ episodes)
- ONNX export and C# loading via IEngineeringNetwork

### Phase 4: Board Designer (3-4 weeks)
- KiCad Python service with pcbnew API
- State encoder, action executor, DRC-based reward
- Training scenarios from simple circuits
- Integration with C# layer

### Phase 5: Simulation & Assembly (3-4 weeks)
- CalculiX and slicer integration
- Assembly manager extension to workbench service
- Cross-tool training (geometry → simulate → fix → repeat)

### Phase 6: Parts Library (2 weeks)
- SQLite standard parts database
- Search and selection actions
- Specification matching reward
- API connections to external suppliers (future)

---

## 10. Hardware Considerations

The user has indicated a beefy PC will be the dedicated DARCI machine.
The architecture takes advantage of this:

**During inference (DARCI running):**
- Behavioral network: ~4K params, < 1ms inference
- Geometry network: ~85K params, < 5ms inference
- Board designer network: ~50K params, < 3ms inference
- All ONNX Runtime on CPU. No GPU needed for inference.
- Python services: ~200MB each. 3-4 services = ~800MB.
- Ollama (llama3.2:3b): ~2GB VRAM.
- **Total runtime: ~4GB RAM, ~2GB VRAM. Comfortable on any modern PC.**

**During training (batch, offline):**
- SAC training with 85K-param networks: CPU is fine. ~10 minutes for 1000 episodes.
- If GPU available (CUDA): training drops to ~2 minutes for 1000 episodes.
- Simulation training can max out CPU cores (run multiple workbench instances
  in parallel for data generation).
- **Recommendation:** If the PC has an NVIDIA GPU, install torch with CUDA.
  Training speed scales roughly linearly with cores/CUDA threads.

**Parallel training optimization:**
Since the user wants to leverage the hardware, the simulation training can
run N parallel workbench instances (one per CPU core) to generate experience
data simultaneously. The training script collects from all of them.

```python
# train_geometry_sim.py — parallel data generation
from multiprocessing import Pool

def run_episode(worker_id):
    """Single training episode on one workbench instance."""
    workbench = WorkbenchClient(port=8001 + worker_id)
    # ... run episode, return experiences

with Pool(processes=os.cpu_count()) as pool:
    all_experiences = pool.map(run_episode, range(os.cpu_count()))
```
