# AGAMOTTO

**Advanced Guidance and Analytics for Military Operations, Tactical Threats, and Outcomes (AGAMOTTO)** — a research and experimentation stack that combines a **Unity / RIDE** tactical simulation, **ML-Agents** reinforcement learning, optional **Python** services, and a **WinTAK MIL** plugin for C2-style workflows.

---

## Overview

| Area | What it does |
|------|----------------|
| **Unity simulation** (`AGAMOTTO/AGAMOTTO Simulation`) | Primary environment: RIDE-based entities, terrain, scenarios, and RL agents (for example Attack-to-Destroy style missions). |
| **WinTAK plugin** (`AGAMOTTO-WinTAKMIL-Plugin`) | C# plugin for WinTAK MIL to interoperate with AGAMOTTO workflows (see plugin README for template alignment). |
| **Shared WinTAK / MIL glue** (`AGAMOTTO/WinTAKMIL`) | Supporting C# helpers used with WinTAK-oriented features. |
| **Python server / RL utilities** (`AGAMOTTO/AGAMOTTO Server`) | Orchestration, TAK messaging, and RL-related scripts (for example `EYE_OF_AGAMOTTO_RL`). |

The **RIDE** simulation SDK content (`Assets/Ride`, `Ride_Art`, `Ride_Dependencies`) is **not stored in this repository** (see `.gitignore`). You install it locally per the simulation setup guide.

---

## Prerequisites (summary)

- **Unity Hub** + **Unity 2022.3 LTS** (project is pinned to **2022.3.62f3** in `AGAMOTTO/AGAMOTTO Simulation/ProjectSettings/ProjectVersion.txt`; any nearby 2022.3 LTS patch is usually fine).
- **Visual Studio 2022** (or compatible MSBuild) for the WinTAK plugin and typical C# tooling.
- **WinTAK MIL** (if you are building or deploying the plugin).
- **Python 3** (optional, if you run `AGAMOTTO Server` scripts).

---

## Install: Unity simulation

1. **Clone** this repository.

2. **Add RIDE** — RIDE assets are required and are documented in the project setup guide:
   - Open and follow: [`AGAMOTTO/AGAMOTTO Simulation/Setup.md`](AGAMOTTO/AGAMOTTO%20Simulation/Setup.md)  
   That file covers copying `Ride`, `Ride_Art`, and `Ride_Dependencies`, UPM packages (ML-Agents, Input System, etc.), Photon PUN 2, `UnityMainThreadDispatcher`, terrain key, and optional Photon demo cleanup.

3. **Open the Unity project**  
   - Folder: `AGAMOTTO/AGAMOTTO Simulation`  
   - Main scene (per `Setup.md`): `Assets/Scenes/BaseScene`

4. **ML-Agents** — Install `com.unity.ml-agents` as described in `Setup.md`. Training configs may live outside this repo (for example under your local ML-Agents `config` tree); align behavior names with your agents (for example `AGAMOTTO_RL` where used).

5. **Headless / batch** (optional) — See `runUnity.bat`, `runUnity.sh`, and `runUnity.cs` under the simulation folder for automation patterns.

---

## Install: WinTAK MIL plugin

1. Read the plugin-specific notes: [`AGAMOTTO-WinTAKMIL-Plugin/README.md`](AGAMOTTO-WinTAKMIL-Plugin/README.md).

2. **Visual Studio 2022** — Open the plugin project (for example `AGAMOTTO WinTAK Plugin.csproj` in `AGAMOTTO-WinTAKMIL-Plugin`). Restore NuGet packages and build **Release** (or your target configuration).

3. **Deploy** — Copy the built plugin assembly (and any required dependencies per your WinTAK version) into the WinTAK MIL plugins directory for your installation. Exact paths depend on WinTAK MIL layout on your machine.

4. **Template reference** — Plugin structure is aligned with community WinTAK plugin templates, for example:  
   https://github.com/Hellikandra/WinTAK-plugintemplate  

---

## Install: Python server (optional)

The `AGAMOTTO/AGAMOTTO Server` directory contains orchestration and messaging scripts. There is no single packaged `requirements.txt` at the repo root; open the scripts you need and install any imports they declare (for example PyTAK-related usage in `AGAMOTTO PyTAK Server.py`). Use a virtual environment for isolation.

---

## Repository layout

```text
AGAMOTTO/
├── AGAMOTTO Simulation/     # Unity project (open this folder in Unity)
├── AGAMOTTO Server/         # Python / server-side utilities
└── WinTAKMIL/               # C# helpers for WinTAK-oriented code

AGAMOTTO-WinTAKMIL-Plugin/   # WinTAK MIL C# plugin project
```

---

## Contributing / branches

Development often proceeds on feature branches (for example `wintak-plugin`). Open pull requests against your team’s default integration branch (commonly `main`) after review.
