# AGAMOTTO Server

Python orchestration for Unity / RIDE training and CoT replay (WinTAK optional).

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

## Interactive orchestrator

Launches Unity, waits for terrain **Valid**, then listens for CoT on TCP (WinTAK plugin or scripted replay).

```powershell
cd "AGAMOTTO\AGAMOTTO Server"
python "AGAMOTTO Orchestrator.py"
```

**Replay CoT from disk** (no WinTAK GUI):

```powershell
python "AGAMOTTO Orchestrator.py" --replay-unity-data --unity-data-dir Unity_Data
```

Place entity XML under `Unity_Data/entities/` and optional routes under `Unity_Data/routes/`.

**Environment overrides**

| Variable | Default | Purpose |
|----------|---------|---------|
| `AGAMOTTO_UNITY_PROJECT` | (auto) | Path to simulation project |
| `AGAMOTTO_COT_PORT` | `65432` | TCP port — match WinTAK plugin |
| `AGAMOTTO_SCENE_PATH` | `Assets\Scenes\BaseScene.unity` | Scene to open |

---

## Unity run configuration

Optional per-run overrides: `AGAMOTTO Simulation/Assets/StreamingAssets/evaluation/active_run.json`

Example for hybrid AWAC:

```json
{
  "version": 1,
  "runId": "hybrid_awac",
  "dispatchMode": "Hybrid",
  "awacEnabled": true,
  "awacLambda": 1.0,
  "expertPhaseMaxSteps": 2000,
  "expertBlendDuringOnline": 0.0,
  "trainingFriendlyLimit": 1,
  "sendTrainingFeedbackToTak": false,
  "useSquadNavMeshPaths": false
}
```

Unity reads this at RL agent init via `AGAMOTTO_EvaluationRunConfig.cs`.

---

## Hybrid AWAC training

ML-Agents has **no native AWAC**. AGAMOTTO implements:

1. **Expert baseline (Unity)** — leader follows the WinTAK/orchestrator route; mission rewards and transitions are recorded.
2. **Online AWAC (Python)** — `agamotto_awac_train.py` connects to the Academy and runs SAC + advantage-weighted updates using the expert buffer.

### Expert buffer (created in Unity)

During **Hybrid** dispatch, Unity writes:

```text
Assets/StreamingAssets/awac/
  expert_buffer.jsonl   # one JSON transition per line
  expert_manifest.json
```

Each line contains `observation` (10-dim ATD), `expert_action`, `policy_action`, step `reward`, and metadata. The buffer is flushed when the expert baseline phase ends (route complete or `expertPhaseMaxSteps`).

**Requires:** friendly entity CoT (`a-f-*`, `mode=Hybrid`) **and** leader route CoT (`b-m-p-s-r`, `mode=Hybrid`) with matching callsign.

### Full AWAC workflow

**1. WinTAK (or orchestrator):** Hybrid mode → place friendly squad + draw leader route → Send.

**2. Unity:** Open `BaseScene`, wait for terrain **Valid**, ensure squads spawned.

**3. Terminal 1 — AWAC trainer** (start **before** Play):

```powershell
cd "C:\Users\matthew.corbett\AGAMOTTO\AGAMOTTO\AGAMOTTO Server"

python agamotto_awac_train.py configs/hybrid_awac.yaml `
  --run-id=hybrid_awac `
  --results-dir=results `
  --behavior-name=AGAMOTTO_RL `
  --base-port=5004 `
  --timeout-wait=5000 `
  --time-scale=50 `
  --expert-wait-s=900 `
  --expert-min-transitions=32
```

The trainer waits for `expert_buffer.jsonl` (≥32 transitions), then connects to the Academy.

**4. Unity:** Press **Play** when you see `Connecting to Unity Academy...` (online AWAC phase — after expert baseline has written the buffer).

**5. Outputs:** checkpoints under `results/AGAMOTTO_RL/hybrid_awac/`; training logs every ~1200 steps.

### Order summary

| Step | Action |
|------|--------|
| 1 | Hybrid CoT: friendly + route |
| 2 | Unity terrain Valid; expert baseline runs (first session) |
| 3 | `agamotto_awac_train.py` listening |
| 4 | Unity **Play** for online AWAC |
| 5 | Check `results/` for `.pt` checkpoints |

---

## SAC training (manual)

Standard ML-Agents SAC (no AWAC expert buffer):

```powershell
# Terminal 1
mlagents-learn ..\AGAMOTTO_RL_sac_tuned.yaml `
  --run-id=atd_train `
  --results-dir=results `
  --timeout-wait=5000 `
  --time-scale=50 `
  --force

# Terminal 2 — orchestrator or WinTAK Training-mode CoT, then Unity Play
python "AGAMOTTO Orchestrator.py" --replay-unity-data
```

Behavior name: **`AGAMOTTO_RL`**. Only friendly (`a-f-*`) squads receive the policy.

---

## Scripts

| Script | Purpose |
|--------|---------|
| `orchestrator_core.py` | Unity launch, terrain `status.json`, CoT TCP + StreamingAssets |
| `AGAMOTTO Orchestrator.py` | Interactive entry point |
| `unity_data_loader.py` | Load `Unity_Data` CoT for `--replay-unity-data` |
| `agamotto_awac_train.py` | Custom AWAC trainer (PyTorch + mlagents_envs) |
| `configs/hybrid_awac.yaml` | AWAC hyperparameters |
| `requirements-awac.txt` | Python deps for AWAC trainer |
| `EYE_OF_AGAMOTTO_RL/` | Legacy RL experiments |

---

## Troubleshooting

| Symptom | Check |
|---------|--------|
| Academy never connects | Start `agamotto_awac_train.py` **before** Unity Play; stay within `--timeout-wait`. |
| Expert buffer empty | Hybrid CoT must include **route**; watch Unity log for `expert baseline complete`. |
| `np.bool` / NumPy error | `pip install "numpy<2.0"` or use `requirements-awac.txt`. |
| Wrong obs size | Rebuild Unity after ATD obs changes; trainer expects 10-dim ATD vector. |
| Multiple deciding agents | Set `trainingFriendlyLimit: 1` in `active_run.json`. |
| CoT spawns before terrain ready | Wait for `status.json` **Valid** before sending entities. |
| Port bind error | Set `AGAMOTTO_COT_PORT` and match WinTAK plugin port. |
| Units dead after episode reset | Rebuild with `RestoreAgentAtSpawn` (`SetAgentHealthFull` + Idle). |

---

## See also

- Root overview: [`../../README.md`](../../README.md)
- Unity setup: [`../AGAMOTTO Simulation/Setup.md`](../AGAMOTTO%20Simulation/Setup.md)
- WinTAK plugin: [`../../AGAMOTTO-WinTAKMIL-Plugin/README.md`](../../AGAMOTTO-WinTAKMIL-Plugin/README.md)
