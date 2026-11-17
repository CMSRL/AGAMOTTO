# Terrain causal OE (v1)

RIDE is the testbed. WinTAK places a real tactical layout; Unity records what RIDE’s combat model does on that terrain. Python fits a small SCM. **Evaluation mode only — no ML-Agents.**

## DAG

```
elev -> slope_deg
elev -> prom
height_adv -> terrain_los
slope_deg -> mobility
terrain_los -> first_shot
terrain_los -> hit_rate
terrain_los -> enemy_killed
```

Covariates come from RIDE `ITerrainSystem` (height, local slope, prominence, `RaycastTerrain` LOS). Outcomes are trial-level combat results (`trial_end`).

Hold vs BlueAdvances: **Hold** is the cleanest identification (position is the intervention; mobility stays near zero). **BlueAdvances** adds `slope_deg -> mobility` as a mediator.

## Collect logs

1. Start Unity Play and wait until terrain is loaded.
2. In WinTAK, choose **Evaluation** (not SAC). Place Blue (`a-f-*`) and Red (`a-h-*`) on Razish, then **Send Map Data**.
3. In Unity, press **F8**. Set trials, **Cover area** (or Near pins), pad/radius, seed, and **Rate** (RIDE `ITimeSimulationSystem` speed; default 4×). Click **Run randomized trials**. **Cover area** slides the whole TAK layout across a grid over the pin bounding box plus pad, so position covers terrain. **Near pins** only jitters each unit in a disk around its TAK point. Rate > 1 shortens wall-clock time; targeting is still per Unity frame, so keep Rate in 2–8× unless you are only scouting coverage.
4. JSONL is written outside Assets:

```
AGAMOTTO Simulation/CausalLogs/causal_YYYYMMDD_HHMMSS.jsonl
```

Each line has `"kind"`: `trial_begin`, `agent`, `dyad`, `shot`, `death`, `trial_end`. Randomized trials respawn TAK units at a seeded offset so position is set by the experiment. **Record current units** is observational (no respawn).

## Fit the SCM

From `AGAMOTTO/AGAMOTTO Server` (numpy; conda env `agamotto-ml` is fine):

```powershell
cd "AGAMOTTO\AGAMOTTO Server"
python causal\fit_graph.py --self-test
python causal\fit_graph.py
python causal\fit_graph.py "..\AGAMOTTO Simulation\CausalLogs\causal_YYYYMMDD_HHMMSS.jsonl"
```

The script prints:

- `elev -> slope_deg` / `elev -> prom`
- `height_adv -> terrain_los`
- `slope_deg -> mobility`
- `terrain_los -> enemy_killed` (unadjusted ATE) and `terrain_los -> first_shot` / `hit_rate`

and writes `<jsonl>.report.json`.

## Out of scope (v1)

- Vegetation, buildings, weather
- Multi-agent graphs beyond Blue/Red dyads
- Changing ML-Agents observations
