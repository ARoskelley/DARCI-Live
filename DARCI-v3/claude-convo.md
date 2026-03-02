Hey Claude. Not planning to necessarily do programming for this right now, but I want to toss a couple questions aroundIt would be a significant jump from the current STL generation capabilities, but what would it look like to make an engineering sandbox so she can design full projects like prosthetics, hydraulics, etc?1:54 PMContemplated architectural expansion from STL generation to comprehensive engineering sandboxThe user wants to discuss architectural ideas for extending DARCI with an engineering/CAD sandbox capability. This is a conceptual discussion, not a coding task. Let me think about what this would look like given DARCI's existing architecture.DoneThis is a really fun question to think through. Looking at DARCI's architecture, she's already got the right bones for this — the goal system with multi-step execution, the toolkit abstraction, and the autonomous loop that can work on things over time. The jump is in giving her a spatial/physical reasoning layer she doesn't have today.
Here's how I'd think about it in layers:
The core problem: LLMs don't think in geometry. DARCI can reason about what a prosthetic hand needs (degrees of freedom, grip strength, user comfort), but she can't natively work in 3D space. So you need a bridge between her semantic reasoning and actual parametric geometry. That bridge is basically a constraint-based CAD kernel she can talk to through her toolkit.
What the toolkit extension might look like:
Rather than DARCI generating raw mesh data, she'd be composing engineering intent — something like "create a socket that fits this residual limb scan, with 2mm wall thickness, a distal pressure relief zone here, and mounting points for a wrist rotator." That intent gets translated into operations against a parametric engine (OpenCascade, CadQuery, or Build123d are the open-source options that could sit behind an API).
So you'd add something like an IEngineeringWorkbench to her tools with operations at the right abstraction level — not "place vertex at x,y,z" but things like "create tube with inner diameter D, add fillet at junction, boolean-subtract clearance channel." She reasons about the design; the kernel handles the geometry.
The goal system already supports this pattern. A prosthetic design goal would decompose into steps naturally:

Research — gather requirements (user measurements, use case, standards)
Generate — create a design specification (semantic, not geometric yet)
A new step type: Engineer — iteratively build the CAD model through toolkit calls
Validate — run checks (wall thickness, stress concentration, printability)
Notify — present results with STL export

Steps 3 and 4 are where it gets interesting, because they'd likely loop. She designs, validates, finds a problem, revises — which maps well to her existing cycle-based architecture.
The hard parts:
The first is physical reasoning. For a hydraulic system, she needs to understand fluid dynamics constraints, pressure ratings, seal tolerances. You'd probably want domain-specific knowledge modules — essentially structured reference data she can query (material properties, standard fittings, ISO specs) rather than relying on LLM training data for precise engineering values.
The second is spatial feedback. When DARCI makes a design choice, she needs to "see" the result to iterate. Since she can't look at a 3D viewport, you'd want the CAD kernel to report back in terms she can reason about: interference detected between parts A and B, minimum wall thickness is 0.8mm (below 1.5mm threshold), center of mass is offset 3cm from grip axis. That textual/numerical feedback closes the loop.
The third is parametric vs. generative. For well-understood components (standard hydraulic fittings, common joint mechanisms), parametric templates with DARCI choosing parameters works great. For organic shapes (prosthetic sockets conforming to anatomy), you might need a generative approach — maybe a specialized geometry model she can invoke, similar to how she currently invokes Ollama for text.
A realistic incremental path:
Rather than jumping to "design a full hydraulic system," you could grow it from her existing STL work:

Parametric primitives — she can compose standard shapes with boolean operations
Component library — pre-built parametric components (hinges, sockets, channels) she can customize and assemble
Constraint solver — she specifies relationships ("these holes must align," "this must withstand 50N") and the system resolves geometry
Simulation hooks — basic FEA or flow analysis she can invoke to validate designs
Full assembly — multi-part designs with tolerances and fit specifications

Each step is a meaningful capability on its own, and the toolkit interface stays clean — DARCI's decision-making doesn't change much, she just gets richer tools to call.
What's your instinct on where the value is first? Prosthetics feel like they'd benefit most from the organic/scan-fitting side, while hydraulics are more about component selection and system layout. Pretty different engineering domains under the hood.I’d be interested to see if I could teach her 3d reasoning. Is there anything that you’ve worked with or that you could think of to implement that reasoning into her?2:03 PMExamined spatial reasoning approaches and practical implementation strategiesThe user is asking about approaches to teaching an AI system 3D spatial reasoning. This is a fascinating research question. Let me think about what I know regarding this topic and what could practically be integrated into DARCI's architecture.
Let me think about several angles:

How LLMs currently handle spatial reasoning
Research/approaches for giving AI 3D understanding
What could practically be built into DARCI's toolkit

Let me think carefully and honestly about this.Show moreDoneThis is one of the genuinely hard open problems in AI, so I want to be honest about what's known versus what's speculative — but there are some really interesting angles.
Why LLMs struggle with 3D by default
Language models build world representations from text, which is fundamentally sequential and symbolic. They can learn that "a bolt goes through a hole" but they don't maintain an internal spatial model the way you do when you mentally rotate an object. They approximate spatial reasoning through linguistic patterns, which works surprisingly well for simple cases and falls apart on anything requiring precise geometric relationships.
So the question is really: can you give DARCI a spatial reasoning prosthetic (fitting, given the domain) that lets her think in 3D without actually having a native spatial cortex?
Approaches I'd consider, from most practical to most experimental:
1. Structured spatial representation as working memory
Rather than hoping the LLM reasons about geometry internally, you externalize the spatial state. DARCI maintains a scene graph — a structured data representation of what exists in her design space, where things are, and how they relate. Something like:
Part: Socket
  Origin: (0, 0, 0)
  BoundingBox: 120mm x 85mm x 95mm
  Features:
    - Cavity (conforms to scan profile A)
    - MountingFlange (distal, 4x M4 holes, PCD 60mm)
  Constraints:
    - Wall thickness >= 1.5mm everywhere
    - MountingFlange.axis aligned with Cavity.central_axis
  Neighbors:
    - WristRotator (attached at MountingFlange)
She reasons about this representation — which is text and therefore in her native language — and then a geometry kernel realizes it. When she wants to "think about" the design, she reads the scene graph, not a mesh. This is probably the most immediately implementable approach given her current architecture. You'd add a SpatialWorkingMemory alongside her existing memory system.
2. Tool-augmented spatial queries
Give her the ability to ask questions about the geometry she's building, rather than reasoning about it internally. The CAD kernel becomes an oracle:

"What's the minimum distance between the hydraulic channel and the outer wall?"
"If I move this mounting point 5mm distally, does anything interfere?"
"What's the cross-sectional area at plane Z=40?"

This is powerful because it offloads the precise computation entirely. She forms hypotheses in natural language, tests them through tool calls, and iterates. It mirrors how a human engineer works — you don't mentally compute stress tensors, you sketch something, run analysis, and adjust. You'd implement this as a set of query tools on the workbench: MeasureDistance, CheckInterference, SectionAnalysis, FindMinThickness, etc.
3. Multi-view projection feedback
This one's interesting. Even though DARCI can't "see" a 3D model, you could render orthographic projections (front, side, top, section views) and feed them back as images if you're using a multimodal model. Some recent research suggests vision-language models can do reasonable spatial reasoning from multiple 2D views — it's essentially how engineering drawings worked for centuries before CAD.
This wouldn't work with her current Ollama/Gemma setup since that's text-only, but if you ever moved to a multimodal model, she could literally look at her designs from multiple angles and reason about what she sees. It's a surprisingly natural feedback loop.
4. Learned spatial primitives through experience
This is more experimental but aligns with DARCI's personality-evolution philosophy. Right now her traits drift based on interaction. You could do something similar with spatial intuition — she attempts designs, gets feedback (simulation results, print failures you report back, your corrections), and over time builds up heuristic knowledge:

"Thin walls near stress concentrations usually fail"
"Hydraulic channels need gradual bends, not sharp angles, for flow"
"Socket pressure points need relief within 15mm radius"

These aren't geometric computations — they're engineering judgment, which is exactly what LLMs can learn. You'd store these as a specialized memory type, maybe tagged as spatial_heuristic or engineering_intuition, and surface them during design tasks. Over time she develops something like an engineer's instinct, even without true spatial processing.
5. The research frontier: 3D foundation models
There's active work on models that natively understand 3D — things like Point-E, Shape-E, and more recent work on 3D-aware transformers. These are still early and mostly focused on generation from text prompts rather than engineering-grade parametric design. But the trajectory is heading toward models that maintain internal 3D representations.
The practical integration would be similar to how DARCI uses Ollama now — a specialized spatial model she can call for geometry-specific reasoning, while her main LLM handles the high-level engineering logic. Think of it as a right-brain module she can consult.
What I'd actually build first
If I were adding this to DARCI tomorrow, I'd combine approaches 1 and 2. The scene graph gives her a way to think about spatial relationships in her native medium (structured text), and the query tools let her verify her reasoning against actual geometry. That gets you surprisingly far without any exotic models or research-grade components.
The scene graph also slots naturally into her existing goal step system — each design iteration updates the graph, queries validate it, and she can describe what she's doing and why in terms you can follow and correct. That human-in-the-loop correction is probably the fastest path to her actually getting good at it, honestly.
What's your sense of how hands-on you'd want to be in the loop versus how autonomous you'd want her design process to be?I’d want it to be quite autonomous. I don’t mind it taking time, it makes sense for it to possibly take entire days to design complex pieces like that. I’d like to be able to create full renders and models of additive prosthetics/hydraulic exoskeleton systems that I can use myself. So the spacial reasoning and experimentation in these environments would be usefulCan you look on the web for other models that people have made with 3D and engineering use for something like that?2:10 PMScoured web for 3D engineering models and CAD toolsThe user wants me to search for AI models and tools that handle 3D reasoning, CAD generation, and engineering design that could be integrated into DARCI's architecture. Let me search for this.DoneLet me dig into what's out there right now.Synthesized resources for autonomous 3D engineering design systemsAI 3D CAD generation engineering design model10 resultsNew AI agent learns to use CAD to create 3D objects from sketches | MIT News | Massachusetts Institute of Technologynews.mit.eduDraftAid | AI CAD Drawing Automation | Generate 2D Drawings from 3D Models Fastdraftaid.ioML CAD Model Generator | Create CAD Files With Text | Zoozoo.devAI 3D Model Generators & the Future of CAD: What You Need to Knowblog.printpal.ioGenCADgencad.github.ioAI copies human CAD moves to turn sketches into fast 3D modelsinterestingengineering.comGenerative AI meets CAD: enhancing engineering design to manufacturing processes with large language models | The International Journal of Advanced Manufacturing Technology | Springer Nature Linklink.springer.comAI CAD Design Solutions | Autodesk Fusion AI CAD Tools for Smart Engineering| Autodeskwww.autodesk.comAI 3D Model Generator - Create 3D models with Text or imageswww.sloyd.aiUpcoming 3D generative AI foundation models for Autodesk Fusion and Formaadsknews.autodesk.comGood results. Let me search for more specific things - open source CAD generation, CadQuery/Build123d with AI, and specifically prosthetic/engineering design AI.CadQuery Build123d AI LLM parametric CAD generation10 resultsBuild123d vs CadQuery: Navigating the Future of Python CAD Modeling - Oreate AI Blogwww.oreateai.comGitHub - khaledelhady44/Build123d-Cookbook: A community-driven collection of build123d examples and scripts for parametric 3D modeling. Contributions welcome! 🚀github.comGitHub - CadQuery/cadquery: A python parametric CAD scripting framework based on OCCTgithub.comGitHub - gumyr/build123d: A python CAD programming librarygithub.comGitHub - katalyst-labs-os/katalyst-core: Multimodal generative AI for CADgithub.com. . Latest updates: hps://dl.acm.org/doi/10.1145/3787499 . . SURVEYdl.acm.orgText-to-CadQuery: A New Paradigm for CAD Generation with Scalable Large Model Capabilitiesarxiv.orgCAD-CODER: AN OPEN-SOURCE VISION-LANGUAGE MODEL FOR COMPUTER-AIDED DESIGN CODEdecode.mit.eduExternal Tools and Libraries — build123d 0.10.1.dev190+g562b3e82d documentationbuild123d.readthedocs.ioLanguage models, parametric design spaces, L-systems and formal grammars in CAD — a marriage made in heaven? | by Pavel Golubev | Mediummedium.comGreat, let me search for more specific things about FEA simulation, prosthetic design AI, and Zoo's text-to-CAD which seems very relevant.Zoo text-to-CAD open source KittyCAD API engineering10 resultsGitHub - KittyCAD/text-to-cad-ui: A lightweight UI for interacting with the Zoo Text-to-CAD API.github.comOpen-source AI Text-to-CAD Software by Zoo Unlocks Accessible 3D Design - 3D Printing Industry3dprintingindustry.comIntroducing Text-to-CAD | Zoozoo.devtext-to-cad-ui/README.md at main · KittyCAD/text-to-cad-uigithub.comText-to-CAD Tutorial | Zoozoo.devKittyCAD - Zoogithub.comML for CAD Design | Develop Using Our ML API | Zoozoo.devEngineering CAD Software | Build With Our Design API | Zoozoo.devKITTYCAD ANNOUNCES ZOO, LAUNCHES NEW MACHINE LEARNING APIwww.prnewswire.comFrequently Asked Questions | Zoozoo.devLet me search for one more thing - AI for FEA simulation and prosthetic design specifically.AI FEA simulation open source structural analysis CAD10 resultsTop Free Options for Finite Element Analysis (FEA) - FEAssistantfeassistant.comAI-Powered Finite Element Modeling Software for Large Assemblies and Visualizations | Altair HyperMeshaltair.comSimulation Software | Engineering AI in the Cloud | SimScalewww.simscale.comOpen Source FEA Software as an Accessible Alternative to ...www.linkedin.comFree FEA Program: Best Open-Source Finite Element Softwarecaeflow.comFree Online 3D Structural Finite Element Analysis Softwarecalcforge.comThe best free FEA software packages - FEA4freefea4free.comBest Open-Source Finite Element Analysis Softwarewww.epsilonforge.comList of finite element software packages - Wikipediaen.wikipedia.org10 of the Best Free Software Alternatives for Engineers — Omnia MFGwww.omniamfg.comLet me do one more search specifically about AI for prosthetic design and exoskeleton design.KATALYST open source AI CAD generation CadQuery LLM10 resultsGitHub - katalyst-labs-os/katalyst-core: Multimodal generative AI for CADgithub.comGitHub - OpenOrion/CQAsk: the open source llm cad generation toolgithub.comText-to-CadQuery: A New Paradigm for CAD Generation with Scalable Large Model Capabilitiesarxiv.orgCAD-Coder: An Open-Source Vision-Language Model for Computer-Aided Design Code Generationarxiv.orgCAD-CODER: AN OPEN-SOURCE VISION-LANGUAGE MODEL FOR COMPUTER-AIDED DESIGN CODEdecode.mit.edu[2505.06507] Text-to-CadQuery: A New Paradigm for CAD Generation with Scalable Large Model Capabilitiesarxiv.orgText-to-CadQuery: A New Paradigm for CAD Generation ...arxiv.orgLanguage models, parametric design spaces, L-systems and formal grammars in CAD — a marriage made in heaven? | by Pavel Golubev | Mediummedium.comCAD-Coder: An Open-Source Vision-Language Model for Computer-Aided Design Code Generation | alphaXivwww.alphaxiv.orgGitHub - CadQuery/cadquery: A python parametric CAD scripting framework based on OCCTgithub.comThis is a great set of results. Let me now put together a comprehensive response about what's out there that could help DARCI with 3D reasoning and engineering design.DoneOkay, there's actually a lot happening in this space right now — more than I expected. Here's what's out there that maps to what you're trying to build:
The most directly useful: CadQuery as DARCI's geometry language
This is the clearest winner for your architecture. CadQuery is an open-source, script-based, parametric model generator built on Python and the Open CASCADE Technology (OCCT) kernel. GitHub The key thing is that it's pure Python with no GUI required — it was built to be used as a Python library without any GUI, making it great for integration into servers or creating scientific and engineering scripts. GitHub It can output high-quality, lossless CAD formats like STEP and DXF in addition to STL, VRML, AMF and 3MF. GitHub
The reason this matters for DARCI specifically: she could generate CadQuery Python scripts through her LLM, execute them, get the resulting geometry back as feedback, and iterate. It fits perfectly into her goal-step architecture — each design iteration is a step that produces executable code.
Build123d is the newer alternative, derived from portions of CadQuery but extensively refactored into an independent framework over Open Cascade. GitHub It has a cleaner Pythonic interface with builder patterns. Either would work, but CadQuery currently has more AI training data available.
AI models specifically for CAD generation:
Several projects are directly tackling the "LLM generates CAD code" problem:
CAD-Coder (MIT) — This is probably the most relevant for you. It's an open-source Vision-Language Model fine-tuned to generate editable CadQuery Python code directly from visual input, trained on a dataset of over 163k CAD-model image and code pairs. arXiv It outperforms baselines like GPT-4.5 and Qwen2.5-VL-72B, achieving a 100% valid syntax rate. arXiv The exciting part is it showed some signs of generalizability, successfully generating CAD code from real-world images and executing CAD operations unseen during fine-tuning. arXiv This could be DARCI's "spatial cortex" — a specialized model she calls when she needs to generate or reason about geometry.
Text-to-CadQuery — An academic project that augmented the Text2CAD dataset with 170,000 CadQuery annotations and fine-tuned six open-source LLMs of varying sizes. arXiv Their finding is important for your case: since LLMs already excel at Python generation and spatial reasoning, fine-tuning them on Text-to-CadQuery data proves highly effective. arXiv
KATALYST — An open-source core that comprises all the algorithms needed to generate parametric CAD models, execute them, iterate on them, evaluate them and manage a dataset of designs, powered by the CadQuery library and LLM API endpoints with RAG. GitHub This is essentially someone building a pipeline very similar to what you'd want for DARCI, just not as an autonomous agent.
Zoo/KittyCAD — A commercial but API-accessible option. Their Zookeeper agent has research and reasoning capabilities and engine-level tools that let it inspect, snapshot, and debug geometry while generating production-ready CAD. Zoo They have a Text-to-CAD tool that generates STEP files from prompts Zoo, and importantly their approach outputs their own parametric language (KCL) so designs remain editable. The API has Python client libraries.
VideoCAD (MIT) — A different angle entirely. MIT created a dataset of over 41,000 examples of how 3D models are built in CAD software, recording every click, drag, and selection. MIT News The trained model could take a 2D sketch as input and directly control the CAD software to construct the full 3D shape. MIT News This is more of a "CAD co-pilot" approach but shows AI can learn the sequential reasoning of building complex parts step by step.
The feedback loop problem — and how others are solving it:
This is critical for autonomous design, and researchers are converging on a pattern that maps well to DARCI's architecture. A robust architecture can incorporate visual or analytical feedback — the LLM generates a CAD model, the system renders it to an image, and then either a human or an AI vision module checks if it meets the requirements. Medium One team executed generated CadQuery code in a subprocess, and if it failed, fed the error message back into the LLM for self-correction — raising success rate from 53% to 85%. arXiv
One researcher noted that a study found many open-source LLMs didn't recognize CadQuery at all, returning incorrect answers until fine-tuned or given examples. Medium This is worth knowing — if DARCI stays on Gemma2, she'd probably need either fine-tuning or heavy RAG with CadQuery examples. A dedicated CAD model like CAD-Coder as a separate tool would sidestep that.
For the simulation/validation side:
For FEA (stress analysis, structural validation), the open-source options are solid. Code_Aster, developed by EDF for France's nuclear industry, rivals any commercial offering in depth and validation CAEFlow — it handles the nonlinear analysis you'd need for prosthetic load cases. FreeCAD's FEM Workbench integrates CAD with analysis directly CAEFlow, and PrePoMax provides a complete, user-friendly free FEA solution using the CalculiX solver that rivals commercial packages. FEA4free All of these can be driven programmatically, which means DARCI could set up and run simulations autonomously.
How I'd wire this into DARCI's architecture:
Given everything above, the most realistic path looks like:

CadQuery as the geometry language — DARCI generates Python/CadQuery scripts through her existing LLM pipeline, executes them, gets STEP/STL output
CAD-Coder as a specialized tool — A second model (like she currently uses Ollama for text) specifically for geometry reasoning. She calls it when she needs to translate design intent into spatial operations
The Build123d-Cookbook dataset — There's a community-driven repository collecting build123d scripts specifically for building a dataset for training an LLM for generating CadQuery code. GitHub You could contribute to this and use it for RAG to boost DARCI's CAD knowledge
CalculiX/FreeCAD FEM for validation — Programmatic FEA that DARCI runs after generating geometry, reads results, iterates if stress concentrations are too high
Render-and-check loop — Render orthographic views, feed back to a multimodal model to verify the design matches intent

The fact that this would take days for complex designs actually works in your favor — DARCI's autonomous loop is designed for exactly that kind of long-running work. She'd cycle through design→execute→validate→revise across hundreds of iterations while you're doing other things, only notifying you when she has something worth reviewing or when she's stuck on a design decision that requires your input.
The biggest gap I see in the ecosystem right now is that nobody has really tackled the assembly-level reasoning you'd need for a full exoskeleton — individual components yes, but understanding how a hydraulic actuator connects to a joint mount that interfaces with a socket that conforms to anatomy, that whole system-level spatial reasoning is still very much an open problem. DARCI would probably need to decompose that into subsystem designs and then validate interfaces between them.That could work. In some CAD programs, you can test dynamic interactions between parts to see things like fit and range of motion. Do you think there’s a way we could take these subsystems that DARCI creates and add them to a 3d sandbox environment to test things like that?2:22 PMInvestigated open-source solutions for dynamic assembly testing and simulationThe user is asking about dynamic simulation/testing of assembled subsystems - things like fit checking, range of motion, collision detection, and kinematic simulation. Let me search for open-source options for this kind of testing.DoneGreat question — that's really where this goes from "generating parts" to "engineering a working system." Let me look at what's available for that kind of dynamic testing.Synthesized concepts into coherent framework for useropen source physics simulation kinematic assembly testing robotics10 resultsProject Chrono - An Open-Source Physics Engineprojectchrono.orgDART: Dynamic Animation and Robotics Toolkitdartsim.github.ioMuJoCo — Advanced Physics Simulationmujoco.orgAnnouncing Newton, an Open-Source Physics Engine for Robotics Simulation | NVIDIA Technical Blogdeveloper.nvidia.com(PDF) An Evaluation of Open Source Physics Engines for Use in Virtual Reality Assembly Simulationswww.researchgate.netA Review of Nine Physics Engines for Reinforcement Learning Researcharxiv.orgCyberbotics: Robotics simulation with Webotscyberbotics.comGitHub - newton-physics/newton: An open-source, GPU-accelerated physics simulation engine built upon NVIDIA Warp, specifically targeting roboticists and simulation researchers.github.comGenesisgenesis-embodied-ai.github.ioOpening up a physics simulator for robotics - Google DeepMinddeepmind.googleGreat results. Let me also search for URDF generation from CAD and prosthetic/exoskeleton simulation specifically.URDF generation from STEP CAD model exoskeleton prosthetic simulation10 resultsGitHub - MariaLazzaroni/HumanExoskeleton-model: SDF and URDF models for simulation of the human and the exoskeleton in Gazebogithub.comGitHub - ReconCycle/urdf_from_step: ROS package for creating URDF format from specialy prepared STEP/STP format.github.comGitHub - Rok-Pahic/urdf_creator: Program for creating URDF from STEP defined robotgithub.comStep file creation - ReconCycle documentationreconcycle.github.ioCreating a URDF file using a CAD assembly – Duke MEMS: Experiment Design and Research Methodssites.duke.eduFrom CAD to URDF: Preparing a 4-Legged Robot for Training | by Surabhi Gade | Jan, 2026 | Mediummedium.comConverting SolidWorks CAD Models to URDF for ROS & Gazebo - Complete Guide (2025)robocademy.comROS Sample Project – LEGACY SITE: Experiment Design and Research Methodssites.duke.eduGitHub - robotology/simmechanics-to-urdf: Script for converting simmechanics XML files to URDFgithub.comURDF Primer - MATLAB & Simulinkwww.mathworks.comThis is coming together really well. Let me synthesize all of this into a clear picture for the user.DoneYes, absolutely — and the pipeline for getting from CAD parts into a dynamic simulation sandbox is actually more mature than I expected. Here's how the pieces connect:
The bridge: URDF (Unified Robot Description Format)
This is the key format that connects the CAD world to the simulation world. URDF describes your system as a tree of links (rigid bodies) and joints (connections with motion constraints) — exactly what you need for an exoskeleton. There's already a ROS package called urdf_from_step that does automated conversion of STEP models to URDF format, using Open Cascade Technology to analyze the STEP file and extract joint and link definitions. GitHub
The workflow they've established is clever: you add parts representing joints and links to your CAD model with specific naming conventions, and since the joint and link definitions are connected in the original CAD model, any change to the shape data automatically changes the positions in the kinematic chain. GitHub DARCI could do this programmatically — when she generates CadQuery parts, she'd simultaneously generate the URDF joint/link annotations.
There's even an existing project with SDF and URDF models specifically for human-exoskeleton simulation in Gazebo, where the exoskeleton model was extracted from original CAD files with kinematics parameters, inertial parameters and meshes. GitHub That's almost exactly your use case.
The simulation engines — three strong candidates:
MuJoCo is probably the best fit for your prosthetic/exoskeleton work. It's a free, open-source physics engine designed for robotics, biomechanics, graphics and animation, offering a unique combination of speed, accuracy and modeling power. MuJoCo What makes it particularly relevant is that it includes spatial tendon routing, including wrapping around bones, meaning that applied forces can be distributed correctly to the joints, describing complicated effects like the variable moment-arm in the knee. Google DeepMind That's directly applicable to prosthetic joints and hydraulic actuator routing. It has full Python bindings, and its MJCF scene-description format contains elements for real-world robotic components like equality constraints, tendons, actuators, and sensors. Google DeepMind
Project Chrono is worth considering for the hydraulics side. It can simulate mechatronic systems, compliant mechanisms, and fluid-solid interaction phenomena, with systems made of rigid and flexible parts with constraints, motors and contacts. Projectchrono It also has a Python version called PyChrono Projectchrono and can perform co-simulation with other CFD and FEA software packages Projectchrono — so DARCI could run structural analysis and dynamic simulation together.
Genesis is the most cutting-edge option. It's a comprehensive physics simulation platform designed for general-purpose Robotics and Embodied AI, integrating various physics solvers into a unified framework, enhanced by a generative agent framework for automated data generation. Genesis-embodied-ai The speed is remarkable — when simulating a manipulation scene, Genesis runs at 43 million FPS, 430,000 times faster than real time. Genesis-embodied-ai That speed matters for DARCI because she could run thousands of motion simulations overnight to test range of motion across different configurations.
And then Newton, just announced — an open-source physics engine being developed by NVIDIA, Google DeepMind, and Disney Research, built on NVIDIA Warp for GPU acceleration. NVIDIA Developer It's designed to be a unified, scalable, and customizable solution to model real-world physics NVIDIA Developer and is explicitly targeting the sim-to-real gap that matters when you actually want to print and wear what DARCI designs.
How DARCI's sandbox pipeline would work:
The autonomous loop would look like this:

Design phase — DARCI generates CadQuery scripts for each subsystem (socket, joint, actuator mount, hydraulic channel), producing STEP files
Assembly phase — She programmatically annotates the STEP files with joint definitions and exports URDF, defining the kinematic chain (which parts rotate, which slide, what the limits are)
Sandbox import — The URDF loads into MuJoCo (or Genesis), creating a dynamic model with collision meshes, inertial properties, and actuator definitions
Testing — She runs simulations autonomously: range of motion sweeps, load testing (what happens when you grip something and the wrist joint takes the torque), interference checks (do parts collide at full extension), gait analysis if it's a leg exoskeleton
Feedback extraction — She reads the simulation results: maximum joint torques, collision events, range of motion achieved vs. target, energy consumption of actuators
Iteration — Back to step 1 with modifications based on what the simulation revealed

Each of those steps maps to DARCI's existing goal-step architecture. You'd add new GoalStepType values like Simulate, Assemble, ValidateMotion alongside the existing Research, Generate, Notify.
The part that's genuinely hard:
The biggest challenge isn't any individual tool — it's the semantic gap between design intent and simulation setup. When DARCI designs a hydraulic elbow joint, she needs to know that the actuator should be modeled as a force element with specific pressure-displacement characteristics, that the joint needs limits matching human elbow range (roughly 0–145 degrees), that the bearing surfaces need friction coefficients, and so on. That's engineering knowledge that has to come from somewhere — either fine-tuned into her model, built as reference tables she can query, or learned from your feedback over time.
But the toolchain exists. CadQuery → STEP → URDF → MuJoCo is a complete pipeline, all open source, all scriptable from Python, all runnable headless (no GUI needed). DARCI could run that entire loop at 3 AM while you're sleeping and have a tested, validated design waiting for you in the morning with a summary of what she tried, what worked, and what tradeoffs she made.That sounds fantastic. Can you run through a hypothetical solution for this framework? I’m interested to see how it could work to have a system mounted on my back and supported by my shoulders that can act as a third arm. It can monitor electrical/nerve signals from anywhere on my body and I want it to be able to support weight and reach over my shoulders or around my ribs2:28 PMArchitected autonomous engineering pipeline for wearable exoskeleton designThe user wants me to walk through a hypothetical design process for a third-arm exoskeleton system using DARCI's proposed engineering framework. This is a conceptual/architectural walkthrough, not actual code implementation. Let me think through how DARCI would approach this as an autonomous engineering project, from requirements gathering through design, simulation, and iteration.
This is a really cool problem - a back-mounted third arm with EMG/nerve signal control, shoulder support structure, and the ability to reach over shoulders or around the torso. Let me think through the engineering challenges and how DARCI's pipeline would handle them.
I should structure this as a narrative of how DARCI would autonomously work through this over days, using the tools we discussed.DoneThis is a fantastic test case because it hits almost every hard problem at once — biomechanical interface, structural load paths, kinematic reach envelopes, and neural signal interpretation. Let me walk through how DARCI would approach this as a multi-day autonomous project.

Day 1: DARCI receives the goal and begins decomposition
You send her: "Design a back-mounted third arm supported by the shoulders. EMG/nerve signal control from anywhere on my body. Needs to support weight, reach over shoulders and around ribs."
Her intent classifier flags this as a Task with GoalType.Project. She creates a top-level goal and immediately recognizes she needs to decompose it before she can design anything. Her first action is a Think step where she uses her LLM to break the problem into subsystems:
She identifies five subsystems:

Harness/frame — the load-bearing structure that sits on your back and distributes forces to your shoulders and torso
Shoulder mount/turret — the base joint that gives the arm its primary orientation (which shoulder to reach over, or to sweep around the ribs)
Arm kinematic chain — the actual articulated arm with enough degrees of freedom to be useful
End effector — the gripper or tool interface at the end
Control system — EMG sensor placement, signal processing, and mapping to arm movements

She creates sub-goals for each, then starts with research steps because she knows she needs reference data before generating geometry.
Day 1, continued: Research phase
DARCI runs multiple research steps autonomously. She's looking for:

Human shoulder and torso anthropometrics (she'll need your measurements eventually, but starts with standard ranges)
Existing third-arm research (there's actually quite a bit — MIT's Supernumerary Robotic Arms project, the Japanese Jizai Arms project, and various surgical assistant arms)
EMG signal characteristics — which muscle groups produce the cleanest signals for voluntary control, typical electrode placement strategies
Load-bearing harness designs from existing exoskeleton literature (she'd pull from the RoboMate exoskeleton URDF models we found)
Hydraulic actuator specifications for the force requirements she's estimating

She stores all of this in her memory system tagged with the project, so she can recall it during design iterations. She also creates a preliminary engineering specification document using her file tools:
Project: Third Arm System (TAS-001)
User: Tinman

Requirements:
- Mount: Back-mounted, shoulder-supported
- Reach envelope: Over either shoulder, around ribs to front of torso
- Payload: TBD (assuming 5kg functional target)
- Control: EMG-based, electrodes at multiple body sites
- Actuation: Hydraulic (for power density)
- Fabrication: Additive manufacturing (FDM/SLA)

Constraints:
- Total system weight budget: <8kg (distributed across shoulders/back)
- Must not restrict normal arm movement
- Must not compress spine (loads transfer to shoulders/hips)
- Printable in sections, assembled post-print

Open Questions:
- User body measurements needed
- Payload requirement confirmation
- Preferred mounting (over clothing? integrated vest?)
She sends you a notification asking for your measurements and a few clarifying questions, then continues working on what she can without waiting.
Day 2: Harness frame design begins
DARCI starts with the harness because everything else attaches to it. This is where the CadQuery pipeline kicks in.
She generates her first CadQuery script for a parametric backplate. Her approach is to define the spine as a reference line, then build a frame that distributes load to two shoulder pads and a waist belt. She's thinking about this in her scene graph representation:
Assembly: TAS-001-Harness
├── BackPlate
│   ├── SpineChannel (central void for ventilation/comfort)
│   ├── ShoulderPadMount_L (connection point)
│   ├── ShoulderPadMount_R (connection point)
│   ├── WaistBeltMount (lower load transfer)
│   └── TurretMount (where the arm base attaches)
│       Constraints:
│         - Located between scapulae, offset 40mm posterior
│         - Must handle moment loads from arm at full extension
│         - Reinforced with internal lattice structure
├── ShoulderPad_L
│   ├── ConformingSurface (curved to deltoid/trapezius)
│   ├── PaddingChannel (for comfort foam)
│   └── LoadPath → BackPlate.ShoulderPadMount_L
├── ShoulderPad_R (mirror of L)
└── WaistBelt
    ├── HipPad_L
    ├── HipPad_R
    └── TensionSystem (adjustable fit)
She generates the backplate in CadQuery — a curved shell that follows the thoracic spine curvature, with mounting bosses and internal ribbing for stiffness. She runs the script, gets a STEP file, then immediately runs it through her FEA validation step.
For FEA, she sets up a CalculiX simulation: fixed constraints at the shoulder pad interfaces and waist belt, and applies a 50N downward force plus a 25Nm moment at the turret mount (simulating the arm holding 5kg at half extension). She reads the results — peak stress is in the turret mounting area. She notes this and flags it for reinforcement in the next iteration.
She also checks printability: wall thicknesses, overhang angles, whether the part fits within a standard print bed (she might split it into sections with alignment pins and bolt holes).
Day 2-3: Arm kinematic chain design
This is where the range-of-motion requirement gets interesting. She needs the arm to reach over either shoulder AND sweep around the ribs to the front. That's a massive reach envelope from a single mount point.
She designs a 6-DOF arm:

Joint 1 (Turret yaw) — rotates the entire arm around the vertical axis of the mount. This is what lets it sweep from left shoulder to right shoulder and around the ribs. Full 360° rotation on a slewing ring bearing.
Joint 2 (Shoulder pitch) — the arm lifts up over the shoulder or drops down along the ribs. ~180° range.
Joint 3 (Upper arm roll) — axial rotation of the upper arm segment. ~270°.
Joint 4 (Elbow pitch) — classic elbow flexion. ~150°.
Joint 5 (Wrist pitch) — ~120°.
Joint 6 (Wrist roll) — continuous rotation for tool orientation.

Each joint needs a hydraulic actuator. DARCI designs compact rotary-vane hydraulic actuators for joints 1-4, and smaller linear-to-rotary mechanisms for the wrist joints where space is tighter.
She generates each link as a separate CadQuery part, with mating features (bolt patterns, bearing seats, seal grooves for the hydraulic passages). She's routing the hydraulic lines through the arm segments themselves — channels bored into the printed structure, which is one of the huge advantages of additive manufacturing.
Day 3: First sandbox simulation
Now she has enough parts to assemble and test. She:

Exports each part as STEP
Programmatically generates the URDF file, defining the kinematic chain:

xml<robot name="third_arm_system">
  <link name="backplate">
    <visual><mesh filename="backplate.stl"/></visual>
    <collision><mesh filename="backplate_collision.stl"/></collision>
    <inertial>
      <mass value="1.2"/>
      <inertia ixx="0.01" iyy="0.015" izz="0.008" .../>
    </inertial>
  </link>
  
  <joint name="turret_yaw" type="revolute">
    <parent link="backplate"/>
    <child link="upper_arm"/>
    <axis xyz="0 0 1"/>
    <limit lower="-3.14" upper="3.14" effort="30" velocity="2.0"/>
  </joint>
  
  <!-- ... remaining joints and links ... -->
</robot>

Loads into MuJoCo with a humanoid body model — she attaches the harness to a standard biomechanical torso model so she can test the arm reaching around a realistic body shape
Runs reach envelope analysis: she commands each joint through its range and records the end-effector position in 3D space, generating a point cloud of everywhere the arm tip can reach

She finds problems on the first pass. The arm collides with the user's head when reaching over the right shoulder at certain elbow angles. Joint 2 doesn't have enough range to get the arm low enough to reach around the ribs at waist level. The turret creates a bump that interferes with normal arm motion when the third arm is stowed.
She logs all of this, adjusts the design, regenerates, and re-simulates. This iterate-test-fix cycle might run 20-30 times overnight.
Day 4: EMG control system design
In parallel with the mechanical iteration, she's been working on the control architecture. She researches EMG electrode placement strategies and identifies a tiered control scheme:

Primary control — EMG electrodes on the trapezius and latissimus dorsi (back muscles you don't normally use independently from your arms). Shoulder shrug-like gestures mapped to turret rotation and arm lift.
Fine control — Electrodes on the forearm or hand of your dominant arm. Specific finger tap patterns or wrist flexion gestures mapped to elbow, wrist, and gripper.
Auxiliary inputs — Foot pressure sensors or toe gestures for mode switching (so you can switch between controlling the arm and having it hold position).

She designs the electrode mounting system as part of the harness — flexible PCB strips that conform to the body surface, with snap-on dry electrodes that route through channels in the harness to a central processing unit mounted on the backplate.
For the signal processing board housing, she generates a sealed enclosure in CadQuery that mounts to the backplate's internal face, with cable routing channels and a ventilation grill.
Day 5: Hydraulic system design
She designs the hydraulic power unit (HPU) — this would mount at the small of the back or on the waist belt to keep the center of mass low. It includes:

A compact electric pump (she's sized this based on the flow rate and pressure requirements from her actuator calculations)
A small reservoir
Proportional control valves for each joint (6 channels)
Pressure relief and check valves for safety
The hydraulic line routing from the HPU up through the backplate and into the arm

She generates the valve manifold as a single printed block with internal channels — no external hose connections between the manifold and the backplate, just a sealed interface. This is another additive manufacturing advantage; the manifold is a solid part with 3D-printed internal passages that would be impossible to machine conventionally.
She runs a simplified CFD check on the hydraulic channels to verify flow rates and pressure drops at the critical bend points.
Day 5-6: Full system integration simulation
Now she has all the subsystems. She assembles the complete URDF with all links, joints, actuators, and collision meshes, loads it into MuJoCo with the humanoid torso model, and runs a comprehensive test sequence:

Reach test — Can the arm reach a point 300mm in front of the user's chest, approaching from over the right shoulder? From the left? From around the right ribs?
Load test — With 5kg at the end effector in the "over shoulder" configuration, what are the reaction forces at each joint? Do any actuators exceed their torque limits? What's the force distribution on the shoulder pads?
Collision test — Through the full range of motion, does the arm ever collide with the user's head, natural arms, or torso?
Stow test — Can the arm fold into a compact configuration along the spine that doesn't impede normal movement?
Dynamic test — If the user bends forward (to pick something up), how does the arm's hanging weight shift the center of mass? Is it destabilizing?

She reads back every result, identifies the failures and near-misses, and runs another design iteration. She might find that the arm needs a 7th joint (a "shoulder offset" linear actuator that can extend the base mounting point away from the spine) to avoid head collisions during overhead reaches. She adds it, regenerates, re-simulates.
Day 7: Deliverables
DARCI sends you a notification:
"I've completed the initial design for the third arm system. It's a 7-DOF hydraulic arm on a back-mounted harness with EMG control. I ran 47 simulation iterations. The final design clears your body at all tested configurations, supports 5.2kg at full extension over the shoulder, and stows flat along the spine when not in use. I have some tradeoffs I'd like your input on — the waist-mounted hydraulic unit adds 2.1kg and I'm not sure if you'd prefer it integrated into the backplate at the cost of a higher center of mass. I've attached the full STEP assembly, individual STL files for printing, and a summary of the simulation results."
She has waiting for you:

Complete STEP assembly (importable into any CAD program for review)
Individual STL files sized for your printer, with internal lattice structures for weight savings
URDF file so you can run the simulation yourself and see the arm move
A design specification document with materials, tolerances, fastener specifications
Her simulation result summary showing reach envelope, load limits, and safety margins
A list of off-the-shelf components you'd need to source (bearings, seals, hydraulic pump, valves, EMG electrodes, microcontroller)


What's real vs. aspirational in this scenario:
The CadQuery-to-STEP-to-URDF-to-MuJoCo pipeline — that's all real, working technology that could be integrated into DARCI's toolkit today. The parametric design, FEA validation, and kinematic simulation are all achievable with open-source tools.
The parts that would take serious development work are the autonomous engineering judgment — knowing that a turret joint needs a slewing ring bearing, that hydraulic channels need minimum bend radii, that EMG signals from the trapezius are more reliable than from the deltoid for this application. That knowledge either comes from fine-tuning, from building a rich reference database she can query, or (most likely) from iterating with you over many projects until her engineering heuristic memory is deep enough to make good first-pass decisions.
The other gap is the CFD for hydraulics — that's currently the least scriptable part of the pipeline. The structural FEA is very doable with CalculiX, and the kinematics/dynamics simulation is well-handled by MuJoCo, but serious fluid simulation is still expensive and hard to automate. For early iterations she'd probably use analytical approximations (Bernoulli equation for channel sizing) rather than full CFD.
But the bones are all there. You'd be building on established tools, not inventing new ones. The innovation is having DARCI orchestrate the entire pipeline autonomously — and that's an integration problem, not a fundamental research problem.Could it hypothetically handle electronics and electrical design/robotics programming?3:09 PMInvestigated open-source tools for automating electronics and robotics designThe user is asking whether DARCI could be extended to handle electronics design (PCB layout, circuit design), electrical engineering, and robotics programming as part of this third arm project. Let me search for what's available in open-source EDA tools and robotics programming frameworks that could be automated by an AI agent.DoneAbsolutely — let me look at what exists for automating those domains.Synthesized multidomain knowledge for comprehensive responseAI automated PCB design electronics EDA open source KiCad10 resultsKiCad - Schematic Capture & PCB Design Softwarewww.kicad.orgKiCAD MCP Server: A Deep Dive into AI-Powered PCB Design for Engineersskywork.aiDeepPCB | Pure AI-Powered, Cloud-Native PCB Routingdeeppcb.aiExploring KiCad: The Open Source PCB Design Suite for Everyone - Oreate AI Blogwww.oreateai.comAbout KiCad | KiCadwww.kicad.orgQuilter - Physics-Driven AI for Electronics Designwww.quilter.aiKiCad - Wikipediaen.wikipedia.orgFlux - Design PCBs with AIwww.flux.aiKiCad Open Source EDA Software | Free PCB Design Toolengineeringsoftwaretrials.comKicad AI Integration (Artificial Intelligence) - Schematic - KiCad.info Forumsforum.kicad.infoGood results. Let me search for more on the robotics programming/firmware side and SPICE simulation.LLM generate embedded firmware robotics ROS2 control code automated10 resultsGitHub - Auromix/ROS-LLM: ROS-LLM is a framework designed for embodied intelligence applications in ROS. It allows natural language interactions and leverages Large Language Models (LLMs) for decision-making and robot control. With an easy configuration process, this framework allows for swift integration, enabling your robot to operate with it in as little as ten minutes.github.comGitHub - ksDreamer/LLM-Robot: Use LLM to understand user input and control the robot under ROSgithub.comGitHub - MengyangGao/LLM-Robot: Use LLM to understand user input and control the robot under ROSgithub.comLooking inside ROScribe and the idea of LLM-based robotic platform - ROS General - Open Robotics Discoursediscourse.openrobotics.orgROS-LLM: A ROS framework for embodied AI with task feedback and structured reasoningarxiv.orgLooking inside ROScribe and the idea of LLM-based robotic platform | by Saeed Shamshiri | Mediummedium.comLarge language model-based code generation for the control of construction assembly robots: A hierarchical generation approach - ScienceDirectwww.sciencedirect.comros2_control on the real robot | Articulated Roboticsarticulatedrobotics.xyzUtilizing LLMs as a Task Planning Agent for Robotics - hlfshellhlfshell.aiSecuring LLM-Generated Embedded Firmwarearxiv.orgGood results across all three domains. Let me search for one more thing - EMG signal processing and open source tools for that.open source EMG signal processing prosthetic control BCI python10 resultsGumpy: a Python toolbox suitable for hybrid brain-computer interfaces - PubMedpubmed.ncbi.nlm.nih.govLibEMG: An open-source Python toolbox for myoelectric control - Delsysdelsyseurope.comPython and Biosignals: Use Cases and Best Practices | Proxetwww.proxet.comhow to output EMG signal to python — OpenBCI Forumopenbci.comA Novel OpenBCI Framework for EEG-Based Neurophysiological Experimentswww.mdpi.com🧠 BCI Toolkit: 25+ Tools, Datasets & Libraries for Building Brain-AI Systems | by Sharika Zareen | Mediumsharikazareen.medium.comOpen Source Tools for Neuroscienceopenbci.comGitHub - NeuroTechX/awesome-bci: Curated Collection of BCI resourcesgithub.comemg · GitHub Topics · GitHubgithub.comOpenBCI - Wikipediaen.wikipedia.orgGreat, I now have comprehensive information across all three domains. Let me put together a thorough answer.DoneYes — and honestly this is where it gets really exciting, because every layer of the stack has open, scriptable, LLM-compatible tooling now. Let me break it down by domain.

Electronics / PCB Design
KiCad is the clear foundation here. It's an open-source EDA suite handling schematic capture, PCB layout with Gerber output, running on all platforms and licensed under GPL v3. KiCad Critically for DARCI, KiCad exposes a Python API and since V6, schematics, libraries and project files use a human-readable S-expression format Wikipedia — meaning DARCI can read and write KiCad files programmatically without a GUI.
The AI integration is already happening. There's a KiCad MCP Server that allows AI assistants to directly control KiCad software, translating natural language requests into concrete design actions Skypage — schematic generation, component placement, routing, the full workflow. That's essentially the exact integration pattern DARCI would use, just through her toolkit interface instead of MCP.
For the third arm project specifically, DARCI would need to design several boards:

Main controller board — microcontroller, hydraulic valve drivers, power management, comms
EMG acquisition boards — small, flexible boards that sit at each electrode site with analog front-end amplification
Joint encoder boards — position/force sensing at each joint

She'd generate the schematics in KiCad by writing the netlist programmatically (which components connect to which), then use the Python API to place components and route traces. KiCad's 3D PCB viewing function is based on STEP and VRML models, and the board model can be exported for CAD integration Wikipedia — so the PCB enclosures she designs in CadQuery and the boards she designs in KiCad can be checked for mechanical fit in the same pipeline.
There's also Quilter, which uses physics-driven AI to do PCB layout automatically — you define the board outline, pre-place connectors and constraints, and it evaluates each layout against the full list of physical constraints. Quilter DARCI could use something like that for the actual routing step, which is the hardest part of PCB design to automate well.
And for circuit simulation before committing to a board, KiCad has an integrated SPICE simulator which allows users to verify designs before moving to production. Oreate AI DARCI could run SPICE simulations on her motor driver circuits, verify EMG amplifier frequency response, and check power supply stability — all programmatically.

EMG / Biosignal Processing
This domain has remarkably mature open-source tooling. LibEMG is an open-source Python library that aims to facilitate the development of myoelectric control systems, providing instructions for EMG data processing, hardware interfacing, feature extraction, classification and analysis. Delsys It directly supports prosthetic control applications.
For the hardware side, OpenBCI is an open-source hardware and software platform with boards that can work with EEG, EMG, and ECG signals, featuring an ADS1299 analog-to-digital converter with eight input channels expandable to sixteen. MDPI The ADS1299 is the same chip used in medical-grade equipment, and the whole platform is designed for exactly the kind of custom biosensing DARCI would be designing for.
For signal processing and classification, Gumpy is a free, open-source Python toolbox that provides state-of-the-art algorithms for processing and decoding EEG and EMG signals, including deep learning models, and has been demonstrated for real-time prosthetic hand control using surface EMG. PubMed
What DARCI would do for the third arm's control system:

Design the electrode hardware — She'd design custom flexible PCBs in KiCad using the ADS1299 or similar AFE, shaped to conform to specific muscle groups. The board outlines would be generated in CadQuery to match the harness mounting points.
Develop the signal processing pipeline — Using LibEMG, she'd write Python code for filtering, feature extraction (things like mean absolute value, waveform length, zero crossings from the raw EMG), and classification. She'd generate training data requirements and classifier architectures.
Create the gesture-to-command mapping — This is where it gets particularly interesting for autonomous development. She could set up a simulation where different EMG signal patterns (which she models mathematically based on published datasets like NinaPro) map to different arm movements, and then optimize the mapping in her MuJoCo sandbox. She'd test whether a particular muscle activation pattern produces a smooth, intuitive arm movement or a jerky, unintuitive one.


Robotics Programming / Firmware
This is actually where DARCI's architecture is the most natural fit, because the robotics community has been building exactly the LLM-to-robot pipeline she'd need.
ROS-LLM is a framework that integrates ROS with an AI agent connected to open-source and commercial LLMs, with automatic extraction of behavior from LLM output and execution of ROS actions and services, supporting behavior modes including sequences, behavior trees, and state machines. arXiv That's essentially DARCI's decision-action loop mapped to robot control.
ROScribe is an open-source tool that uses LLMs for software generation in robotics within the ROS framework, supporting both ROS 1 and ROS 2 with Python implementations. Openrobotics Its approach is relevant — it captures a high-level description of the robotic task from the user, asks a series of design questions, generates the ROS computational graph, and then for every node, captures the spec and implements it. Medium DARCI could do this same decomposition autonomously.
For the third arm specifically, the software stack would be layered:
Low-level firmware (runs on the microcontroller in real-time):

Hydraulic valve PWM control
Joint encoder reading
PID control loops for each joint
Safety limits and emergency stop
EMG signal acquisition and preprocessing

DARCI would generate this as C/C++ for an STM32 or similar real-time microcontroller. Research has shown LLMs can generate firmware modules tailored for specific embedded tasks using structured prompt engineering arXiv, though the research also notes the importance of validation — while models can produce syntactically correct code, they often need verification for security and timing constraints in embedded systems. arXiv DARCI's iterative loop handles this naturally: generate, compile, test in emulation (QEMU), fix, repeat.
Mid-level control (runs on a companion computer, ROS2):

Inverse kinematics solver (given a desired end-effector position, calculate all joint angles)
Trajectory planning (smooth paths that avoid collisions with the user's body)
EMG signal classification and intent decoding
Sensor fusion (combining EMG intent with joint feedback for stable control)

High-level behavior (DARCI herself, or a local LLM):

Task understanding ("hold this flashlight pointed where I'm looking")
Adaptive behavior (learning your movement patterns over time)
Safety monitoring (if sensor readings seem wrong, freeze the arm)

The ROS2 integration is key because ros2_control provides a framework where you describe your hardware interface in the URDF, define controllers for different joints, and the controller manager links everything together Articulated Robotics — the same URDF that DARCI generates for simulation can drive the real hardware with minimal changes.

How it all connects for the third arm:
The beautiful thing is these aren't separate projects — they're all part of one unified design loop:
DARCI designs a joint actuator in CadQuery → she needs electronics to drive it, so she designs the motor driver in KiCad → the board has a physical shape, so she exports it back to CadQuery to design the enclosure that fits inside the arm link → she writes the firmware for the driver board → she writes the ROS2 node that controls that joint → she connects the EMG pipeline using LibEMG → she loads everything into MuJoCo to test the complete system (mechanical + control) → she finds that the EMG response is too slow for smooth motion, so she goes back to the signal processing code and optimizes the classifier → she re-runs the simulation → it works, so she generates the Gerber files for PCB fabrication, the STL files for printing, the firmware hex for flashing, and the ROS2 package for deployment.
Every one of those steps uses tools that exist today, are open source, are scriptable from Python, and can run headless. The gap isn't the tools — it's building the orchestration layer in DARCI's toolkit that connects them all and giving her enough engineering knowledge (through fine-tuning, RAG, or accumulated experience with you) to make good design decisions at each step.
The end state you're describing is essentially a personal engineering AI that can take "I want a hydraulic third arm controlled by my muscles" and deliver a complete, fabrication-ready package: printed parts, circuit boards, firmware, and control software. That's ambitious, but every individual piece of it has working precedent. The innovation is having one autonomous system that orchestrates the full stack — and DARCI's architecture, with its goal decomposition, multi-step execution, and continuous living loop, is actually well-suited for exactly that kind of long-running, multi-domain engineering project.
What would you want to tackle first in terms of actually building toward this?