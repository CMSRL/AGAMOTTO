# AGAMOTTO

**Advanced Guidance and Analytics for Military Operations, Tactical Threats, and Outcomes (AGAMOTTO)** — a research and experimentation stack that combines a **Unity / RIDE** tactical simulation, **ML-Agents** reinforcement learning, optional **Python** services, and a **WinTAK MIL** plugin for C2-style workflows.

---

## Overview

| Area | What it does |
|------|----------------|
| **Unity simulation** (`AGAMOTTO/AGAMOTTO Simulation`) | Primary environment: RIDE-based entities, terrain, scenarios, and RL agents (Attack-to-Destroy / `AGAMOTTO_RL`). |
| **WinTAK plugin** (`AGAMOTTO-WinTAKMIL-Plugin`) | C# plugin for WinTAK MIL — **Evaluation** (spawn) and **SAC** (training) dispatch. |
| **Shared WinTAK / MIL glue** (`AGAMOTTO/WinTAKMIL`) | Supporting C# helpers used with WinTAK-oriented features. |
| **Python server** (`AGAMOTTO/AGAMOTTO Server`) | Optional SAC trainer (`agamotto_sac_train.py`). CoT no longer goes through a Python orchestrator. |

The **RIDE** simulation SDK content (`Assets/Ride`, `Ride_Art`, `Ride_Dependencies`) is **not stored in this repository** (see `.gitignore`). Install it locally per the simulation setup guide.

---

## Prerequisites (summary)

- **Unity Hub** + **Unity 2022.3 LTS** (project pinned to **2022.3.62f3**).
- **Visual Studio 2022** (or compatible MSBuild) for the WinTAK plugin.
- **WinTAK MIL** (optional) for map-based C2 dispatch.
- **Python 3.10+** with **ML-Agents** (`mlagents-learn`) if you run SAC training.

---

## Quick start

### Send tactical data into Unity (no orchestrator)

Unity listens for CoT on **TCP 65432** after you press Play. Do **not** start `AGAMOTTO Orchestrator.py`.

1. Open `AGAMOTTO Simulation` → `Assets/Scenes/BaseScene` → **Play**.
2. Wait until terrain is **Valid** (Unity log: `AGAMOTTO_CotTcpListener: listening on 0.0.0.0:65432`).
3. In WinTAK: IP `127.0.0.1` (or the Unity machine), Port **65432**.
4. Place friendly / hostile / optional route → **Evaluation** or **SAC** → **Send Map Data to Listener**.

CoT with no `mode` (typical ATAK) spawns as Evaluation.

Without WinTAK, after Unity is playing:

```powershell
cd "AGAMOTTO\AGAMOTTO Server"
python "AGAMOTTO Orchestrator.py" --replay-unity-data
```

### SAC training (optional)

Start Unity Play first (CoT listener + Academy), then:

```powershell
cd "AGAMOTTO\AGAMOTTO Server"
python agamotto_sac_train.py --run-id=sac_run --timeout-wait=5000 --time-scale=50 --force
```

Then WinTAK **SAC** → Send Map Data.

Full notes: **[`AGAMOTTO/AGAMOTTO Server/README.md`](AGAMOTTO/AGAMOTTO%20Server/README.md)**

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

The plugin sends length-prefixed CoT XML to Unity's Play-mode listener (default `127.0.0.1:65432`).

---

## Repository layout

```text
AGAMOTTO/
├── AGAMOTTO Simulation/       # Unity + RIDE
├── AGAMOTTO Server/           # SAC trainer, causal fitter, configs
└── WinTAKMIL/

AGAMOTTO-WinTAKMIL-Plugin/     # WinTAK MIL plugin
AGAMOTTO_RL_sac_tuned.yaml     # SAC trainer config (repo root)
docs/paper/                    # Workshop LaTeX (optional)
```

---

## Contributing / branches

Development proceeds on feature branches (for example `wintak-plugin`). Open pull requests against `main` after review.
