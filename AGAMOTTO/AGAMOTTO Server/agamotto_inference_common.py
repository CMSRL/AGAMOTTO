"""Shared post-training inference setup and Unity episode runner (SAC)."""

from __future__ import annotations

import shutil
import sys
from pathlib import Path
from typing import Callable, List, Optional

import numpy as np

for _alias, _dtype in (
    ("bool", np.bool_),
    ("int", np.int_),
    ("float", np.float64),
    ("object", np.object_),
):
    np.__dict__[_alias] = _dtype

try:
    from mlagents_envs.environment import UnityEnvironment
    from mlagents_envs.base_env import ActionTuple
    from mlagents_envs.side_channel.engine_configuration_channel import EngineConfigurationChannel
except ImportError as exc:
    UnityEnvironment = None  # type: ignore
    ActionTuple = None  # type: ignore
    EngineConfigurationChannel = None  # type: ignore
    _MLAGENTS_IMPORT_ERROR = exc
else:
    _MLAGENTS_IMPORT_ERROR = None

try:
    from orchestrator_core import write_active_run_config
    from tak_feedback import send_training_complete
except ImportError:
    write_active_run_config = None  # type: ignore
    send_training_complete = None  # type: ignore

SERVER_DIR = Path(__file__).resolve().parent
DEFAULT_STREAMING = SERVER_DIR.parent / "AGAMOTTO Simulation" / "Assets" / "StreamingAssets"
OBS_DIM = 10
ACT_DIM = 2
POLICY_ONNX_NAME = "AGAMOTTO_RL.onnx"


def find_latest_onnx(results_dir: Path, behavior: str, run_id: str) -> Path | None:
    candidates = [
        results_dir / behavior / run_id / f"{behavior}.onnx",
        results_dir / behavior / run_id / "model.onnx",
    ]
    for path in candidates:
        if path.is_file():
            return path
    run_root = results_dir / behavior / run_id
    if run_root.is_dir():
        onnx_files = sorted(run_root.rglob("*.onnx"), key=lambda p: p.stat().st_mtime, reverse=True)
        if onnx_files:
            return onnx_files[0]
    return None


def copy_policy_to_streaming(onnx_src: Path, streaming_root: Path) -> Path:
    policies_dir = streaming_root / "policies"
    policies_dir.mkdir(parents=True, exist_ok=True)
    onnx_dst = policies_dir / POLICY_ONNX_NAME
    shutil.copy2(onnx_src, onnx_dst)
    return onnx_dst


def write_training_active_run(
    streaming_root: Path,
    *,
    run_id: str,
    trainer_type: str,
    dispatch_mode: str = "Training",
    leader_callsign: str = "leader",
) -> None:
    if write_active_run_config is None:
        return
    write_active_run_config(
        {
            "runId": run_id,
            "phase": "training",
            "trainerType": trainer_type,
            "dispatchMode": dispatch_mode,
            "inferenceEnabled": False,
            "sendTrainingFeedbackToTak": True,
            "leaderCallsign": leader_callsign,
            "awacEnabled": trainer_type.lower() == "awac",
            "trainingFriendlyLimit": 1,
        },
        streaming_root,
    )


def write_inference_active_run(
    streaming_root: Path,
    *,
    run_id: str,
    trainer_type: str,
    leader_callsign: str = "leader",
) -> None:
    if write_active_run_config is None:
        return
    write_active_run_config(
        {
            "runId": run_id,
            "phase": "inference",
            "trainerType": trainer_type,
            "dispatchMode": "Inference",
            "inferenceEnabled": True,
            "pythonDrivesInference": True,
            "sendTrainingFeedbackToTak": True,
            "inferencePolicyPath": f"policies/{POLICY_ONNX_NAME}",
            "leaderCallsign": leader_callsign,
            "awacEnabled": False,
        },
        streaming_root,
    )


def notify_training_complete(
    trainer_type: str,
    run_id: str,
    total_steps: int,
    checkpoint_path: str,
    *,
    tag: str = "",
) -> None:
    if send_training_complete is None:
        return
    try:
        send_training_complete(trainer_type, run_id, total_steps, checkpoint_path=checkpoint_path)
    except OSError as ex:
        prefix = f"[{tag}] " if tag else ""
        print(f"{prefix}WARN could not notify WinTAK: {ex}", flush=True)


def setup_inference_from_results(
    streaming_root: Path,
    results_dir: Path,
    *,
    behavior_name: str,
    run_id: str,
    trainer_type: str,
    total_steps: int = 0,
    leader_callsign: str = "leader",
    onnx_src: Path | None = None,
) -> Path:
    """Copy ONNX to StreamingAssets, write inference active_run.json, notify WinTAK."""
    src = onnx_src or find_latest_onnx(results_dir, behavior_name, run_id)
    if src is None or not src.is_file():
        raise FileNotFoundError(
            f"No ONNX found under {results_dir / behavior_name / run_id}"
        )
    onnx_dst = copy_policy_to_streaming(src, streaming_root)
    write_inference_active_run(
        streaming_root,
        run_id=run_id,
        trainer_type=trainer_type,
        leader_callsign=leader_callsign,
    )
    notify_training_complete(
        trainer_type,
        run_id,
        total_steps,
        str(onnx_dst),
        tag=trainer_type,
    )
    return onnx_dst


def extract_agent_obs(decision, agent_index: int = 0) -> np.ndarray:
    chunks: List[np.ndarray] = []
    for sensor_obs in decision.obs:
        arr = np.asarray(sensor_obs, dtype=np.float32)
        if arr.ndim >= 2:
            chunks.append(arr[agent_index].reshape(-1))
        else:
            chunks.append(arr.reshape(-1))
    if not chunks:
        return np.zeros(OBS_DIM, dtype=np.float32)
    return np.concatenate(chunks)


def align_obs_to_atd(obs: np.ndarray, target_dim: int = OBS_DIM) -> np.ndarray:
    flat = np.asarray(obs, dtype=np.float32).reshape(-1)
    if flat.shape[0] == target_dim:
        return flat
    if flat.shape[0] > target_dim:
        return flat[-target_dim:]
    return np.pad(flat, (0, target_dim - flat.shape[0]))


class OnnxPolicy:
    """Deterministic continuous policy from an exported ML-Agents ONNX file."""

    def __init__(self, onnx_path: Path):
        try:
            import onnxruntime as ort
        except ImportError as exc:
            raise ImportError(
                "Install onnxruntime for SAC post-training inference: pip install onnxruntime"
            ) from exc
        self._session = ort.InferenceSession(str(onnx_path), providers=["CPUExecutionProvider"])
        self._input = self._session.get_inputs()[0].name
        self._output = self._session.get_outputs()[0].name

    def act(self, obs: np.ndarray) -> np.ndarray:
        batch = np.asarray(obs, dtype=np.float32).reshape(1, -1)
        out = self._session.run([self._output], {self._input: batch})[0]
        action = np.asarray(out, dtype=np.float32).reshape(-1)
        if action.shape[0] != ACT_DIM:
            action = action[:ACT_DIM] if action.shape[0] > ACT_DIM else np.pad(action, (0, ACT_DIM - action.shape[0]))
        return np.clip(action, -1.0, 1.0)


PolicyFn = Callable[[np.ndarray], np.ndarray]


def run_unity_inference_episodes(
    env: UnityEnvironment,
    behavior_name: str,
    policy_fn: PolicyFn,
    *,
    episodes: int = 1,
    tag: str = "inference",
) -> int:
    print(f"[{tag}] Running {episodes} inference episode(s)...", flush=True)
    completed = 0

    for ep in range(episodes):
        env.reset()
        steps = 0
        while steps < 50_000:
            decision, terminal = env.get_steps(behavior_name)
            if len(terminal) > 0:
                steps += 1
                completed += 1
                print(
                    f"[{tag}] inference episode {ep + 1}/{episodes} finished after {steps} steps",
                    flush=True,
                )
                env.step()
                break

            if len(decision) == 0:
                env.step()
                continue

            raw_obs = extract_agent_obs(decision, agent_index=0)
            obs = align_obs_to_atd(raw_obs)
            action = policy_fn(obs)
            n_agents = len(decision)
            if n_agents == 1:
                action_batch = np.asarray([action], dtype=np.float32)
            else:
                action_batch = np.zeros((n_agents, ACT_DIM), dtype=np.float32)
                action_batch[0] = action
            env.set_actions(behavior_name, ActionTuple(continuous=action_batch))
            env.step()
            steps += 1

    return completed


def connect_unity_env(
    *,
    base_port: int = 5004,
    timeout_wait: int = 5000,
    time_scale: float = 50.0,
    no_graphics: bool = False,
) -> UnityEnvironment:
    if UnityEnvironment is None:
        raise ImportError(
            f"mlagents_envs is required: {_MLAGENTS_IMPORT_ERROR}"
        )
    engine_channel = EngineConfigurationChannel()
    engine_channel.set_configuration_parameters(time_scale=float(time_scale))
    env = UnityEnvironment(
        file_name=None,
        base_port=int(base_port),
        timeout_wait=int(timeout_wait),
        no_graphics=bool(no_graphics),
        side_channels=[engine_channel],
    )
    env.reset()
    return env


def run_onnx_inference_episodes(
    onnx_path: Path,
    behavior_name: str,
    *,
    episodes: int = 1,
    base_port: int = 5004,
    timeout_wait: int = 5000,
    time_scale: float = 50.0,
    no_graphics: bool = False,
    tag: str = "sac",
) -> int:
    policy = OnnxPolicy(onnx_path)
    env = connect_unity_env(
        base_port=base_port,
        timeout_wait=timeout_wait,
        time_scale=time_scale,
        no_graphics=no_graphics,
    )
    try:
        behavior_names = list(env.behavior_specs.keys())
        if behavior_name not in behavior_names and behavior_names:
            behavior_name = behavior_names[0]
        return run_unity_inference_episodes(
            env,
            behavior_name,
            policy.act,
            episodes=episodes,
            tag=tag,
        )
    finally:
        env.close()
