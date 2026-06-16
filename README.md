# AGAMOTTO

**Advanced Guidance and Analytics for Military Operations, Tactical Threats, and Outcomes (AGAMOTTO)** — a research and experimentation stack that combines a **Unity / RIDE** tactical simulation, **ML-Agents** reinforcement learning, optional **Python** services, and a **WinTAK MIL** plugin for C2-style workflows.

---

## Overview

| Area | What it does |
|------|----------------|
| **Unity simulation** (`AGAMOTTO/AGAMOTTO Simulation`) | Primary environment: RIDE-based entities, terrain, scenarios, and RL agents (Attack-to-Destroy / `AGAMOTTO_RL`). |
| **WinTAK plugin** (`AGAMOTTO-WinTAKMIL-Plugin`) | C# plugin for WinTAK MIL — Training, Evaluation, and **Hybrid** (AWAC) dispatch modes. |
| **Shared WinTAK / MIL glue** (`AGAMOTTO/WinTAKMIL`) | Supporting C# helpers used with WinTAK-oriented features. |
| **Python server** (`AGAMOTTO/AGAMOTTO Server`) | CoT orchestration, Unity_Data replay, SAC via `mlagents-learn`, and custom **hybrid AWAC** trainer. |

The **RIDE** simulation SDK content (`Assets/Ride`, `Ride_Art`, `Ride_Dependencies`) is **not stored in this repository** (see `.gitignore`). Install it locally per the simulation setup guide.

---

## Prerequisites (summary)

- **Unity Hub** + **Unity 2022.3 LTS** (project pinned to **2022.3.62f3**).
- **Visual Studio 2022** (or compatible MSBuild) for the WinTAK plugin.
- **WinTAK MIL** (optional) for map-based C2 dispatch.
- **Python 3.10+** with **ML-Agents** (`mlagents-learn`) and **PyTorch** for hybrid AWAC.

---

## Quick start

### CoT orchestrator (Training / Evaluation)

```powershell
cd "AGAMOTTO\AGAMOTTO Server"
python "AGAMOTTO Orchestrator.py"
```

WinTAK: match CoT port (`65432` default). Without WinTAK:

```powershell
python "AGAMOTTO Orchestrator.py" --replay-unity-data
```

### Hybrid AWAC (route as expert demonstration)

1. WinTAK **Hybrid** mode → friendly unit + leader route → Send.
2. Start the AWAC trainer, then press **Play** in Unity.

```powershell
cd "AGAMOTTO\AGAMOTTO Server"
pip install -r requirements-awac.txt

python agamotto_awac_train.py configs/hybrid_awac.yaml `
  --run-id=hybrid_awac --results-dir=results --behavior-name=AGAMOTTO_RL `
  --base-port=5004 --timeout-wait=5000 --time-scale=50 `
  --expert-wait-s=900 --expert-min-transitions=32
```

Unity records the expert trajectory to `StreamingAssets/awac/expert_buffer.jsonl` during the baseline phase; Python uses it for offline AWAC updates.

Full workflow, `active_run.json`, and SAC commands: **[`AGAMOTTO/AGAMOTTO Server/README.md`](AGAMOTTO/AGAMOTTO%20Server/README.md)**

---

## Install: Unity simulation

1. **Clone** this repository.
2. **Add RIDE** — [`AGAMOTTO/AGAMOTTO Simulation/Setup.md`](AGAMOTTO/AGAMOTTO%20Simulation/Setup.md).
3. **Open** `AGAMOTTO/AGAMOTTO Simulation` → scene `Assets/Scenes/BaseScene`.
4. **ML-Agents** — behavior name **`AGAMOTTO_RL`** on the Attack-to-Destroy prefab.

---

## Install: WinTAK MIL plugin

1. [`AGAMOTTO-WinTAKMIL-Plugin/README.md`](AGAMOTTO-WinTAKMIL-Plugin/README.md)
2. Build in Visual Studio 2022 and deploy to WinTAK MIL plugins.

Hybrid mode sends `mode=Hybrid` on CoT and links route callsigns to friendly entities.

---

## Repository layout

```text
AGAMOTTO/
├── AGAMOTTO Simulation/       # Unity + RIDE
├── AGAMOTTO Server/           # Orchestrator, AWAC trainer, configs
└── WinTAKMIL/

AGAMOTTO-WinTAKMIL-Plugin/     # WinTAK MIL plugin
AGAMOTTO_RL_sac_tuned.yaml     # SAC trainer config (repo root)
docs/paper/                    # Workshop LaTeX (optional)
```

---

## Contributing / branches

Development proceeds on feature branches (for example `wintak-plugin`). Open pull requests against `main` after review.
