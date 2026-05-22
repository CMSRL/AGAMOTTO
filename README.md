# AGAMOTTO

**Advanced Guidance and Analytics for Military Operations, Tactical Threats, and Outcomes (AGAMOTTO)** — a research and experimentation stack that combines a **Unity / RIDE** tactical simulation, **ML-Agents** reinforcement learning, optional **Python** services, and a **WinTAK MIL** plugin for C2-style workflows.

---

## Overview

| Area | What it does |
|------|----------------|
| **Unity simulation** (`AGAMOTTO/AGAMOTTO Simulation`) | Primary environment: RIDE-based entities, terrain, scenarios, and RL agents (Attack-to-Destroy / `AGAMOTTO_RL`). |
| **WinTAK plugin** (`AGAMOTTO-WinTAKMIL-Plugin`) | C# plugin for WinTAK MIL to interoperate with AGAMOTTO workflows. |
| **Shared WinTAK / MIL glue** (`AGAMOTTO/WinTAKMIL`) | Supporting C# helpers used with WinTAK-oriented features. |
| **Python server** (`AGAMOTTO/AGAMOTTO Server`) | Evaluation suite, CoT orchestration, TensorBoard summarization, and legacy RL scripts. |

The **RIDE** simulation SDK content (`Assets/Ride`, `Ride_Art`, `Ride_Dependencies`) is **not stored in this repository** (see `.gitignore`). Install it locally per the simulation setup guide.

---

## Prerequisites (summary)

- **Unity Hub** + **Unity 2022.3 LTS** (project pinned to **2022.3.62f3** in `AGAMOTTO/AGAMOTTO Simulation/ProjectSettings/ProjectVersion.txt`).
- **Visual Studio 2022** (or compatible MSBuild) for the WinTAK plugin.
- **WinTAK MIL** (optional for manual C2 workflows; **not** required for the automated evaluation suite).
- **Python 3.10+** with **ML-Agents** (`mlagents-learn`) for scripted training and ablations.

---

## Quick start: evaluation suite

Automated ATD training runs **without WinTAK**: Unity is launched from Python, terrain is waited on, two CoT entity files are replayed from StreamingAssets, and SAC trains across six ablations.

```powershell
cd "AGAMOTTO\AGAMOTTO Server"
pip install -r requirements-eval.txt
copy evaluation\paths.example.yaml evaluation\paths.local.yaml
# Edit paths.local.yaml (unity_exe, mlagents_learn, cot_port, ...)

python evaluation_suite.py --list
python evaluation_suite.py --only baseline_sac          # one ablation
python evaluation_suite.py --paths evaluation/paths.local.yaml   # full matrix
```

**Outputs:** `evaluation/results/agamotto_atd_eval_v1/<timestamp>/suite_summary.csv` plus per-run TensorBoard and `*_episode_metrics.csv`.

**Plot rewards:**

```powershell
python plot_eval_rewards.py --suite-dir evaluation/results/agamotto_atd_eval_v1/<timestamp> -o rewards.png --no-show
```

Full documentation: **[`AGAMOTTO/AGAMOTTO Server/README.md`](AGAMOTTO/AGAMOTTO%20Server/README.md)** (step budgets, ablation table, smoke tests, troubleshooting).

| Setting | Value |
|---------|--------|
| Training steps per ablation | **500,000** (`evaluation/configs/*.yaml`) |
| Unity time scale (training) | **50** (`mlagents_time_scale` in `paths.local.yaml`) |
| Unity steps per episode | **5,000** (`AttackToDestroy` prefab `MaxStep`) |
| Default scenario | Two CoT files in `Simulation/Assets/StreamingAssets/entities` |
| RL training team | Friendly (`a-f-*`) only; hostiles spawn for combat |

---

## Install: Unity simulation

1. **Clone** this repository.

2. **Add RIDE** — follow [`AGAMOTTO/AGAMOTTO Simulation/Setup.md`](AGAMOTTO/AGAMOTTO%20Simulation/Setup.md) (copy `Ride`, `Ride_Art`, `Ride_Dependencies`, UPM packages including ML-Agents, terrain key, etc.).

3. **Open the Unity project** — folder `AGAMOTTO/AGAMOTTO Simulation`; main scene `Assets/Scenes/BaseScene`.

4. **ML-Agents** — Behavior name **`AGAMOTTO_RL`** on the Attack-to-Destroy prefab. Trainer YAMLs for the suite are under `AGAMOTTO Server/evaluation/configs/` (SAC + optional curiosity).

5. **Headless / batch** (optional) — `runUnity.bat`, `runUnity.sh`, `runUnity.cs` under the simulation folder.

---

## Install: WinTAK MIL plugin

1. [`AGAMOTTO-WinTAKMIL-Plugin/README.md`](AGAMOTTO-WinTAKMIL-Plugin/README.md)

2. Build the plugin in Visual Studio 2022 and deploy to your WinTAK MIL plugins directory.

3. Template reference: https://github.com/Hellikandra/WinTAK-plugintemplate

---

## Install: Python server

See **[`AGAMOTTO/AGAMOTTO Server/README.md`](AGAMOTTO/AGAMOTTO%20Server/README.md)** for:

- `evaluation_suite.py` — full ablation matrix
- `run_unity_data_spawn.py` — spawn-only smoke test
- `training_eval_summarize.py` — TensorBoard / paper metrics
- `plot_eval_rewards.py` — ablation reward figures
- `orchestrator_core.py` — Unity + CoT + terrain coordination

Evaluation dependencies: `AGAMOTTO Server/requirements-eval.txt` (`pyyaml`, `tensorboard`, `numpy`, `matplotlib`). Install **ML-Agents** in the same environment as `mlagents-learn`.

Legacy scripts (`AGAMOTTO PyTAK Server.py`, `EYE_OF_AGAMOTTO_RL/`) may need additional packages; use a virtual environment.

---

## Repository layout

```text
AGAMOTTO/
├── AGAMOTTO Simulation/       # Unity + RIDE (open in Unity Hub)
├── AGAMOTTO Server/           # Python evaluation suite & orchestration  ← README.md
└── WinTAKMIL/                 # C# helpers

AGAMOTTO-WinTAKMIL-Plugin/     # WinTAK MIL plugin
docs/paper/                    # Workshop LaTeX (optional)
```

---

## Contributing / branches

Development often proceeds on feature branches (for example `wintak-plugin`). Open pull requests against your team’s default integration branch (commonly `main`) after review.
