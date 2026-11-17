# AGAMOTTO Server

Optional Python helpers. **CoT ingest lives in Unity** — press Play, then send TAK data to port **65432**. Do not run a CoT TCP middleman.

---

## Prerequisites

| Requirement | Notes |
|-------------|--------|
| **Unity 2022.3.62f3** | Same version as `AGAMOTTO Simulation` (`ProjectSettings/ProjectVersion.txt`). |
| **Python 3.10+** | Conda/venv recommended. |
| **RIDE + BaseScene** | Simulation project built per [`../AGAMOTTO Simulation/Setup.md`](../AGAMOTTO%20Simulation/Setup.md). |

**Python packages**

```powershell
cd "AGAMOTTO\AGAMOTTO Server"
pip install -r requirements-awac.txt
pip install mlagents
```

`requirements-awac.txt` pins `numpy<2.0` for compatibility with current `mlagents_envs`. Use a dedicated conda env if `(base)` has NumPy 2.x.

For standard SAC training, `mlagents-learn` must be on `PATH`.

---

## Direct TAK → Unity

1. Unity **Play** on `BaseScene`.
2. Wait for `AGAMOTTO_CotTcpListener: listening on 0.0.0.0:65432`.
3. WinTAK plugin: IP of the Unity machine, port **65432**, **Send Map Data**.

`AGAMOTTO Orchestrator.py` no longer binds that port (it would steal the socket from Unity). Replay-only:

```powershell
python "AGAMOTTO Orchestrator.py" --replay-unity-data --unity-data-dir Unity_Data
```

Place entity XML under `Unity_Data/entities/` and optional routes under `Unity_Data/routes/`. Unity must already be playing.

**Environment overrides**

| Variable | Default | Purpose |
|----------|---------|---------|
| `AGAMOTTO_COT_PORT` | `65432` | Must match Unity `cotListenPort` and the WinTAK plugin |

---

## Unity run configuration

Optional per-run overrides: `AGAMOTTO Simulation/Assets/StreamingAssets/evaluation/active_run.json`

Example for SAC:

```json
{
  "version": 1,
  "runId": "sac_run",
  "dispatchMode": "Training",
  "trainingFriendlyLimit": 1,
  "sendTrainingFeedbackToTak": false,
  "useSquadNavMeshPaths": false
}
```

Unity reads this at RL agent init via `AGAMOTTO_EvaluationRunConfig.cs`.

---

## SAC training (automatic inference)

WinTAK **SAC** mode (`mode=Training` on CoT) spawns the friendly squad and attaches the RL policy. **Play Unity first** so the CoT listener and Academy are up, then send map data.

```powershell
cd "AGAMOTTO\AGAMOTTO Server"
pip install -r requirements-awac.txt

python agamotto_sac_train.py `
  --run-id=sac_run `
  --results-dir=results `
  --timeout-wait=5000 `
  --time-scale=50 `
  --force
```

Default config: repo-root `AGAMOTTO_RL_sac_tuned.yaml`. Override with a positional YAML path.

| Step | Action |
|------|--------|
| 1 | Unity **Play** (CoT listener 65432 + Academy) |
| 2 | `python agamotto_sac_train.py ...` |
| 3 | WinTAK **SAC**: friendly + route + hostile → Send Map Data |
| 4 | Training completes → ONNX copied → inference episode(s) → WinTAK `training_complete` + `inference_complete` |

Flags: `--inference-episodes` (default 1), `--skip-inference`, `--leader-callsign`, `--resume`.

Legacy: `agamotto_post_train.py` only re-runs setup/inference from an existing `results/` folder.

---

## Scripts

| Script | Purpose |
|--------|---------|
| `orchestrator_core.py` | Shared CoT framing helpers (replay only) |
| `AGAMOTTO Orchestrator.py` | Prints the Unity/TAK workflow; optional `--replay-unity-data` |
| `unity_data_loader.py` | Load `Unity_Data` CoT for `--replay-unity-data` |
| `agamotto_sac_train.py` | SAC via `mlagents-learn` + post-training inference |
| `agamotto_inference_common.py` | Shared ONNX copy, `active_run.json`, Unity inference runner |
| `agamotto_post_train.py` | Re-run SAC inference setup from existing results (optional) |
| `tak_feedback.py` | JSON messages to WinTAK plugin (port 65433) |
| `causal/fit_graph.py` | Fit terrain SCM from CausalRecorder JSONL |
| `requirements-awac.txt` | Python deps for SAC trainer |
| `EYE_OF_AGAMOTTO_RL/` | Legacy RL experiments |

---

## Post-training inference (SAC)

After training, one **inference episode** runs automatically. Unity samples the leader path and sends results to WinTAK (Assessment text + **blue route** on the map). No separate Inference mode or manual post-train script is required.

`agamotto_sac_train.py` runs `mlagents-learn`, copies `StreamingAssets/policies/AGAMOTTO_RL.onnx`, writes `evaluation/active_run.json` (`phase: inference`), notifies WinTAK (`training_complete`), then runs `--inference-episodes` (default 1) via ONNX + `mlagents_envs`. Unity reports `inference_complete` with lat/lon route points.

### WinTAK results (port 65433)

| `kind` | Meaning |
|--------|---------|
| `training_complete` | Trainer finished; checkpoint path in payload |
| `inference_complete` | Inference episode outcome + `routePoints` (blue `b-m-p-s-r` on map) |
| `rl_training_episode` | Per-episode training feedback (unchanged) |

---

## Troubleshooting

| Symptom | Check |
|---------|--------|
| WinTAK Send fails | Unity must be in Play with `AGAMOTTO_CotTcpListener` on 65432. Do not run the old Python CoT server. |
| `np.bool` / NumPy error | `pip install "numpy<2.0"` or use `requirements-awac.txt`. |
| Wrong obs size | Rebuild Unity after ATD obs changes; trainer expects 10-dim ATD vector. |
| Multiple deciding agents | Set `trainingFriendlyLimit: 1` in `active_run.json`. |
| CoT spawns before terrain ready | Wait for terrain **Valid** before sending entities. |
| Port bind error | Another process (old orchestrator) is holding 65432. |
| Units dead after episode reset | Rebuild with `RestoreAgentAtSpawn` (`SetAgentHealthFull` + Idle). |

---

## Terrain causal OE (v1)

Evaluation + F8 `CausalRecorder` writes `AGAMOTTO Simulation/CausalLogs/causal_*.jsonl`. Fit:

```powershell
python causal\fit_graph.py --self-test
python causal\fit_graph.py "..\AGAMOTTO Simulation\CausalLogs\causal_YYYYMMDD_HHMMSS.jsonl"
```

See [`causal/README.md`](causal/README.md).

---

## See also

- Root overview: [`../../README.md`](../../README.md)
- Unity setup: [`../AGAMOTTO Simulation/Setup.md`](../AGAMOTTO%20Simulation/Setup.md)
- WinTAK plugin: [`../../AGAMOTTO-WinTAKMIL-Plugin/README.md`](../../AGAMOTTO-WinTAKMIL-Plugin/README.md)
