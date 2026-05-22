# AGAMOTTO Server

Python orchestration for Unity / RIDE training, CoT replay, and the **automated evaluation suite** (no WinTAK GUI required for scripted runs).

---

## Prerequisites

| Requirement | Notes |
|-------------|--------|
| **Unity 2022.3.62f3** | Same version as `AGAMOTTO Simulation` (`ProjectSettings/ProjectVersion.txt`). |
| **ML-Agents** | `mlagents-learn` on `PATH` or set `mlagents_learn` in `evaluation/paths.local.yaml`. |
| **Python 3.10+** | Conda/venv recommended. |
| **RIDE + BaseScene** | Simulation project built per [`../AGAMOTTO Simulation/Setup.md`](../AGAMOTTO%20Simulation/Setup.md). |

### Python dependencies (evaluation)

```powershell
cd "AGAMOTTO\AGAMOTTO Server"
pip install -r requirements-eval.txt
```

Install **ML-Agents** separately in the same environment (for example `pip install mlagents` from your ML-Agents release tree).

### Machine config

```powershell
copy evaluation\paths.example.yaml evaluation\paths.local.yaml
```

Edit `paths.local.yaml`: `unity_exe`, `unity_project`, `mlagents_learn`, `cot_port`, and optional `trainer_config_base`.  
`paths.local.yaml` is gitignored (`*.local.yaml`).

---

## Evaluation suite (recommended entry point)

The suite runs six SAC ablations for **Attack-to-Destroy** (`AGAMOTTO_RL`), one experiment at a time (fresh Unity + `mlagents-learn` per ablation).

### What each run does

1. Writes `StreamingAssets/evaluation/active_run.json` (Unity overrides for that ablation).
2. Starts **`mlagents-learn`** (trainer waits for Academy).
3. Clears stale `Assets/status.json`, launches **Unity** play mode on `BaseScene`.
4. Waits for terrain **Valid** (fresh `status.json` after launch).
5. Spawns **two CoT entities** from `AGAMOTTO Simulation/Assets/StreamingAssets/entities` (one friendly `a-f-*`, one hostile `a-h-*`; no synthetic extras).
6. Trains until **`max_steps: 500000`** (per-ablation budget in `evaluation/configs/*.yaml`).
7. Summarizes TensorBoard → `{run_id}_episode_metrics.csv`.
8. Shuts down Unity and writes `run_record.json`.

**Episode cap (Unity):** `MaxStep: 5000` on `AttackToDestroy` prefab — each RL episode resets after 5k decisions or terminal outcome (primary enemy killed, leader killed, etc.).

**Training policy:** Only **friendly (Blue)** squads receive `AGAMOTTO_RL`; hostiles are evaluation-only.

### Commands

```powershell
cd "AGAMOTTO\AGAMOTTO Server"

# List experiments
python evaluation_suite.py --list

# Dry-run (print plan, no Unity)
python evaluation_suite.py --dry-run

# Full matrix (hours; subprocess per ablation)
python evaluation_suite.py --paths evaluation/paths.local.yaml

# Single ablation
python evaluation_suite.py --only baseline_sac

# Unity + CoT only (no mlagents-learn)
python evaluation_suite.py --only baseline_sac --skip-train
```

### Ablation matrix (`evaluation/manifest.yaml`)

| Experiment ID | Trainer change | Unity / other |
|---------------|----------------|---------------|
| `baseline_sac` | SAC + extrinsic + curiosity | Full squad kill/loss shaping |
| `ablation_no_curiosity` | Extrinsic only | Same as baseline |
| `ablation_lower_lr` | LR `1e-4` (vs `3e-4`) | Same |
| `ablation_shorter_horizon` | `time_horizon: 256` (vs `512`) | Same |
| `ablation_no_squad_rewards` | Same as baseline | Squad kill/loss rewards = 0 |
| `ablation_no_tak_feedback` | Same as baseline | WinTAK TCP feedback off |

Trainer configs live under `evaluation/configs/`. Scenario defaults: `unity_data_atd` in `manifest.yaml`.

### Results layout

```text
evaluation/results/agamotto_atd_eval_v1/<UTC_timestamp>/
  suite_summary.json
  suite_summary.csv
  baseline_sac/
    run_record.json
    baseline_sac_episode_metrics.csv
    AGAMOTTO_RL/<run_id>/events.out.tfevents.*
  ablation_no_curiosity/
    ...
```

### Plot reward curves

```powershell
python plot_eval_rewards.py --suite-dir evaluation/results/agamotto_atd_eval_v1/20260101_120000 -o figures/rewards.png --no-show
```

Options: `--rolling 20`, `--show-raw`, `--facets`, `--only` via per-run CSVs with `--csv`.

### Summarize one TensorBoard run

```powershell
python training_eval_summarize.py evaluation/results/.../baseline_sac/AGAMOTTO_RL/<run_id> --export-csv out.csv
```

Logs **Environment/Cumulative Reward** and per-episode kill scalars (`AGAMOTTO/Enemies Killed Per Episode`, `AGAMOTTO/Friendlies Lost Per Episode`).

---

## Smoke tests (no full training)

### Spawn-only (Unity + CoT, no `mlagents-learn`)

```powershell
python run_unity_data_spawn.py
python run_unity_data_spawn.py --no-launch-unity   # Unity already in play
```

Uses the same terrain wait + `StreamingAssets/entities` replay as the suite.

---

## Unity reward summary (ATD)

Implemented in `AGAMOTTO_ATD_Agent.cs` (defaults on `AttackToDestroy` prefab):

| Signal | Value |
|--------|-------|
| Primary enemy killed (terminal) | +5 |
| RL leader killed (terminal) | −5 |
| Non-primary enemy killed | +0.45 |
| Friendly wingman killed | −0.50 |
| Closer / farther from primary enemy | +0.002 / −0.0008 per step |
| Obstacle collision | −0.002 |

Episodes reset all squads to locked spawn anchors (revive + teleport). Mission success requires the primary enemy to have been **alive after reset** and killed during that episode.

---

## Other scripts

| Script | Purpose |
|--------|---------|
| `orchestrator_core.py` | Unity launch, terrain `status.json`, CoT TCP + StreamingAssets replay |
| `AGAMOTTO Orchestrator.py` | Legacy / manual orchestration entry |
| `EYE_OF_AGAMOTTO_RL/` | Older RL experiments (DQN, etc.) |

---

## Troubleshooting

| Symptom | Check |
|---------|--------|
| CoT spawns before terrain ready | Suite clears `status.json` and uses `min_mtime` on Valid; ensure `AGAMOTTOScenarioCreator` writes Valid on terrain load. |
| Instant episode success loop | Rebuild Unity after ATD agent reset fixes; primary must be revived in `OnEpisodeBegin`. |
| `terrain not Valid` timeout | Open `BaseScene` manually once; confirm terrain export / `Assets/status.json`. |
| Port bind error | Change `cot_port` in `paths.local.yaml` or set `AGAMOTTO_COT_PORT`. |
| Wrong team training | CoT types must be `a-f-*` (friendly) and `a-h-*` (hostile); `training_friendly_limit: 1` in manifest. |

---

## See also

- Root overview: [`../../README.md`](../../README.md)
- Unity setup: [`../AGAMOTTO Simulation/Setup.md`](../AGAMOTTO%20Simulation/Setup.md)
- WinTAK plugin: [`../../AGAMOTTO-WinTAKMIL-Plugin/README.md`](../../AGAMOTTO-WinTAKMIL-Plugin/README.md)
