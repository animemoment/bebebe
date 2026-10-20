# AI Architecture & CLI Reference Guide (`AI_README.md`)

> **Instruction for AI Agents (Claude, GPT, Cursor, Copilot, Antigravity, Aider):**
> You have access to a local static architecture analyzer for Godot, C#, GDScript, and `.tscn` scenes.
> Use this tool before refactoring, writing new features, or deleting code to **eliminate hallucinations, preserve loose event coupling, and save 90%+ token costs**.

---

## 1. Autonomous CLI Mode (Direct Terminal Access)

The analyzer executable `DependencyGraphVisualizer.exe` (or `python app.py`) supports autonomous CLI querying without opening a browser:

```bash
# General Syntax:
DependencyGraphVisualizer.exe --cli --project <PROJECT_PATH> [OPTIONS]
```

### Quick Commands for AI Agents

| Task | Command | Description |
| :--- | :--- | :--- |
| **Check Dependencies & Blast Radius** | `--deps "path/to/File.cs"` | Shows afferent/efferent connections, Robert C. Martin instability $I$, and total blast radius before you edit. |
| **Get Compact Code Skeleton (Save 90% tokens)** | `--skeletons "path/to/File.gd"` | Returns public classes, method signatures, parameters, and types without method bodies. |
| **360° Function Rengen (Deep-Dive)** | `--method "take_damage"` | Full 360° audit: callers, callees, state mutations, CPU cycles, Hz, O(N) complexity, emitted signals, and waterfall. |
| **Granular AI Field Projection (-98% tokens)** | `--query --fields="name,mutations,hz"` | Pull **only** specific fields across functions to fit strict LLM token budgets. |
| **Token Budget Limiter** | `--budget 500` | Hard cap on token consumption with clean truncation. |
| **Trace Godot Signals & C# Events** | `--signals` | Complete registry: who declares a signal, who emits it, and who connects (`.connect`) to it. |
| **Get Godot Scene Node Hierarchy** | `--scenes` | Exact DOM paths for `$Node/Path` and `%UniqueNode` to prevent `null` node reference crashes. |
| **Check for Dead / Orphan Code** | `--orphans` | Lists files with 0 references, separating true dead code from scene entry points. |
| **Check 60-FPS Hot-Paths** | `--hotpaths` | Lists methods called every frame (`_process`, `_physics_process`). Never allocate heavy memory here! |
| **Call Frequency & CPU Cycles** | `--chronology` | Computes execution frequency (Hz: 0.1, 1, 60, 600 Hz), CPU clock cycles (~3.5 GHz), ms per call, and Waterfall execution order with data flow. |
| **Find Cross-File Function Callers** | `--func "method_name"` | Lists all files calling this method and who provides its data. |
| **Full Project Dependency Map** | `--all-deps` | Complete map of all files and their outgoing/incoming links. |
| **Output Formats** | `--format json\|markdown\|compact` | Structured JSON, clean Markdown, or 1-line ultra-dense compact format. |

---

## 2. Granular Field Projection & Token Budgeting (Selective Slicing)

AI agents can selectively request **only** the exact dimensions of function metadata required for their task:

### Available Projection Fields:
- `name` — method name
- `params` — parameter signatures
- `return_type` — return type
- `mutations` — list of state variables mutated by this method
- `calls` — methods invoked by this method
- `callers` — all methods in the codebase calling this method
- `hz` — execution frequency in seconds
- `cycles` — estimated CPU clock cycles per invocation (~3.5 GHz)
- `emits` — signals emitted
- `spawns` — new object instantiations (`new`)
- `doc` — docstring / inline documentation
- `summary` — synthesized behavioral explanation in Russian
- `alloc` — GC heap allocation in KB
- `complexity` — algorithmic complexity $O(1), O(N), O(N^2)$
- `flow` — linked reactive event-flow pipelines
- `waterfall` — sequential execution steps

### Examples for AI:

1. **Check only state mutations for player functions (compact 1-line format, <100 tokens):**
   ```bash
   DependencyGraphVisualizer.exe --cli --project . --query --file "player" --fields="name,mutations" --format=compact
   ```
   *Output:*
   ```text
   [Player.gd::take_damage(amount: int)] | mutations: [current_hp, is_dead]
   [Player.gd::heal(amount: int)] | mutations: [current_hp]
   [Player.gd::add_xp(xp: int)] | mutations: [current_xp, level]
   ```

2. **Check only 60+ FPS Hot-Paths and their CPU clock cycles:**
   ```bash
   DependencyGraphVisualizer.exe --cli --project . --query --min-hz=60 --fields="name,hz,cycles,complexity" --format=json
   ```

3. **Strict 500-token budget query:**
   ```bash
   DependencyGraphVisualizer.exe --cli --project . --query --fields="name,params,calls,callers" --budget=500
   ```

---

## 3. Token-Saving Refactoring Workflow for AI

When asked to refactor or modify a file in this codebase, follow this **3-step workflow**:

```
Step 1: Check Blast Radius & Dependents
        [--cli --deps "path/to/TargetFile.cs"]
        ↓
Step 2: Pull Signatures or Selective Fields of Connected Files (90-98% Token Reduction)
        [--cli --skeletons "connected_file.cs"] OR [--cli --query --file "target" --fields="name,mutations,callers"]
        ↓
Step 3: If Godot, Verify Signals & Node Paths
        [--cli --signals] & [--cli --scenes]
```

### Example: Before Refactoring `World.gd`
1. Run:
   ```bash
   DependencyGraphVisualizer.exe --cli --project . --deps "scripts/world/World.gd"
   ```
2. You discover:
   - `Blast Radius: 4 files` (Changes to `World.gd` will impact `AgentSystem.gd`, `Main.gd`, `Region.cs`).
   - `Instability I: 0.50` (Balanced component).
   - In a circular cycle with `AgentSystem.gd`.
3. To break the cycle without reading 2,000 lines of implementation:
   ```bash
   DependencyGraphVisualizer.exe --cli --project . --skeletons "scripts/agents/AgentSystem.gd"
   ```
4. You receive 35 lines of pure signatures and interfaces instead of 600 lines of implementation, saving your context window.

---

## 4. Metric Interpretation Cheat Sheet

| Metric | Formula / Range | How AI Must Interpret It |
| :--- | :---: | :--- |
| **Instability ($I$)** | $I = \frac{C_e}{C_a + C_e} \in [0.0, 1.0]$ | **$I < 0.25$ (Stable Core):** High fan-in. Core foundation depended upon by many. **Do not break public signatures!**<br>**$I > 0.75$ (Unstable Coordinator):** High fan-out. Safe to refactor internally. |
| **Blast Radius** | Total transitive dependents (count) | The exact number of files that can potentially break if this file's contracts change. Write tests for these dependents. |
| **God-Object** | Degree $\ge 12$ & LoC $\ge 400$ | Architectural smell. Recommend breaking down into smaller Single Responsibility classes. |
| **Cycle (SCC)** | Directed loop $A \to B \to A$ | Break using Dependency Inversion (C# `interface` / GDScript Duck Typing) or Event Aggregator. |
| **Truly Dead Code** | 0 incoming + 0 outgoing + 0 scene links | Safe to archive or delete. |
| **Scene Entry Point** | 0 code references, but in `.tscn` | Attached to a Godot node (e.g. Level root or UI button). Do not delete! |

---

## 5. Local REST API (Alternative to CLI)

The visualizer also runs a background HTTP daemon:
- **Default URL**: `http://127.0.0.1:8765`

### Endpoints:
- **`POST /api/analyze`**:
  ```json
  { "path": "D:/Games/MyProject", "ignore": ["addons/*", "tests/*"] }
  ```
  Returns full project graph, health score, cycles, signals, chronology, profiling.

- **`POST /api/query`** (Granular Field Projection & Token Budgeting):
  ```json
  {
    "path": "D:/Games/MyProject",
    "file": "player",
    "func": "take_damage",
    "fields": ["name", "params", "mutations", "hz", "cycles", "callers"],
    "min_hz": 10,
    "has_mutations": true,
    "budget": 500,
    "format": "json"
  }
  ```

- **`POST /api/function_detail`** (360° Deep-Dive Rengen):
  ```json
  {
    "path": "D:/Games/MyProject",
    "method": "take_damage",
    "file": "Player.gd"
  }
  ```
