#!/usr/bin/env python3
"""
Custom AWAC trainer for AGAMOTTO hybrid dispatch.

ML-Agents does not ship AWAC. This script:
  1. Connects to a running Unity Academy (same as mlagents-learn).
  2. Waits for Unity expert-baseline transitions at StreamingAssets/awac/expert_buffer.jsonl.
  3. Runs online SAC + advantage-weighted actor updates on expert actions (AWAC).

Unity hybrid flow (WinTAK or eval CoT):
  - Friendly entity + leader route CoT (mode=Hybrid)
  - Expert baseline follows the route; mission rewards shape the buffer
  - Online phase switches BehaviorType back to Default for policy learning
"""

from __future__ import annotations

import argparse
import json
import random
import sys
import time
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Dict, List, Optional, Tuple

import numpy as np

# mlagents_envs uses removed NumPy 2.x aliases (np.bool). __dict__ patch works on NumPy 1.26+ and 2.x.
for _alias, _dtype in (
    ("bool", np.bool_),
    ("int", np.int_),
    ("float", np.float64),
    ("object", np.object_),
):
    np.__dict__[_alias] = _dtype

try:
    import torch
    import torch.nn as nn
    import torch.nn.functional as F
except ImportError as exc:
    print("Install PyTorch: pip install torch", file=sys.stderr)
    raise SystemExit(1) from exc

try:
    import yaml
except ImportError as exc:
    print("Install PyYAML: pip install pyyaml", file=sys.stderr)
    raise SystemExit(1) from exc

try:
    from mlagents_envs.environment import UnityEnvironment
    from mlagents_envs.base_env import ActionTuple
    from mlagents_envs.side_channel.engine_configuration_channel import EngineConfigurationChannel
except ImportError as exc:
    print("Install ML-Agents Python: pip install mlagents", file=sys.stderr)
    raise SystemExit(1) from exc


SERVER_DIR = Path(__file__).resolve().parent
DEFAULT_STREAMING = SERVER_DIR.parent / "AGAMOTTO Simulation" / "Assets" / "StreamingAssets"
OBS_DIM = 10
ACT_DIM = 2


@dataclass
class Transition:
    obs: np.ndarray
    expert_action: np.ndarray
    reward: float
    next_obs: np.ndarray
    done: bool


def load_trainer_yaml(path: Path) -> Dict[str, Any]:
    with path.open(encoding="utf-8") as f:
        return yaml.safe_load(f) or {}


def behavior_config(cfg: Dict[str, Any], behavior_name: str) -> Dict[str, Any]:
    behaviors = cfg.get("behaviors") or {}
    if behavior_name in behaviors:
        return behaviors[behavior_name] or {}
    if behaviors:
        return next(iter(behaviors.values())) or {}
    return {}


def expert_paths(streaming_root: Path) -> Tuple[Path, Path]:
    awac_dir = streaming_root / "awac"
    return awac_dir / "expert_buffer.jsonl", awac_dir / "expert_manifest.json"


def extract_agent_obs(decision, agent_index: int = 0) -> np.ndarray:
    """Concatenate all vector sensors for one agent (decision.obs is per-sensor batches)."""
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
    """Match expert-buffer layout (10-dim ATD); drop leading RIDE base obs if present."""
    flat = np.asarray(obs, dtype=np.float32).reshape(-1)
    if flat.shape[0] == target_dim:
        return flat
    if flat.shape[0] > target_dim:
        return flat[-target_dim:]
    return np.pad(flat, (0, target_dim - flat.shape[0]))


def load_expert_transitions(buffer_path: Path) -> List[Transition]:
    if not buffer_path.is_file():
        return []
    rows: List[Transition] = []
    malformed = 0
    # Unity File.WriteAllLines uses UTF-8 with BOM; utf-8-sig strips it on read.
    with buffer_path.open(encoding="utf-8-sig") as f:
        for line_no, line in enumerate(f, start=1):
            line = line.strip()
            if not line:
                continue
            # Repair legacy rows missing comma after expert_action array.
            line = line.replace(']"reward":', '],"reward":')
            try:
                row = json.loads(line)
            except json.JSONDecodeError:
                malformed += 1
                continue
            try:
                obs = np.asarray(row["observation"], dtype=np.float32)
                act = np.asarray(row["expert_action"], dtype=np.float32)
            except (KeyError, TypeError, ValueError):
                malformed += 1
                continue
            if obs.shape[0] != OBS_DIM or act.shape[0] != ACT_DIM:
                continue
            rows.append(
                Transition(
                    obs=obs,
                    expert_action=np.clip(act, -1.0, 1.0),
                    reward=float(row.get("reward", 0.0)),
                    next_obs=obs,
                    done=bool(row.get("done", False)),
                )
            )
    if malformed:
        print(
            f"[awac] WARN skipped {malformed} malformed expert_buffer line(s) in {buffer_path.name}",
            flush=True,
        )
    return rows


def wait_for_expert_buffer(
    streaming_root: Path,
    *,
    poll_s: float = 2.0,
    timeout_s: float = 600.0,
    min_transitions: int = 32,
) -> List[Transition]:
    buffer_path, manifest_path = expert_paths(streaming_root)
    deadline = time.time() + timeout_s
    print(f"[awac] Waiting for expert buffer at {buffer_path} (min={min_transitions})...", flush=True)
    while time.time() < deadline:
        try:
            transitions = load_expert_transitions(buffer_path)
        except OSError as ex:
            print(f"[awac] WARN could not read expert buffer yet: {ex}", flush=True)
            time.sleep(poll_s)
            continue
        if len(transitions) >= min_transitions:
            print(f"[awac] Loaded {len(transitions)} expert transitions.", flush=True)
            return transitions
        if manifest_path.is_file():
            try:
                manifest = json.loads(manifest_path.read_text(encoding="utf-8-sig"))
                count = int(manifest.get("transitions", 0))
                if count >= min_transitions and transitions:
                    print(f"[awac] Loaded {len(transitions)} expert transitions (manifest).", flush=True)
                    return transitions
            except (json.JSONDecodeError, OSError, TypeError, ValueError):
                pass
        time.sleep(poll_s)
    print("[awac] Expert buffer timeout — continuing with online-only AWAC/SAC.", flush=True)
    return load_expert_transitions(buffer_path)


class Mlp(nn.Module):
    def __init__(self, in_dim: int, out_dim: int, hidden: int = 128, layers: int = 2):
        super().__init__()
        dims = [in_dim] + [hidden] * layers
        blocks: List[nn.Module] = []
        for i in range(len(dims) - 1):
            blocks.append(nn.Linear(dims[i], dims[i + 1]))
            blocks.append(nn.ReLU())
        blocks.append(nn.Linear(dims[-1], out_dim))
        self.net = nn.Sequential(*blocks)

    def forward(self, x: torch.Tensor) -> torch.Tensor:
        return self.net(x)


class GaussianPolicy(nn.Module):
    def __init__(self, obs_dim: int, act_dim: int, hidden: int = 128, layers: int = 2):
        super().__init__()
        self.body = Mlp(obs_dim, hidden, hidden, layers)
        self.mean = nn.Linear(hidden, act_dim)
        self.log_std = nn.Parameter(torch.zeros(act_dim))

    def forward(self, obs: torch.Tensor) -> Tuple[torch.Tensor, torch.Tensor]:
        h = self.body(obs)
        mean = torch.tanh(self.mean(h))
        log_std = self.log_std.clamp(-5.0, 2.0)
        return mean, log_std

    def sample(self, obs: torch.Tensor) -> Tuple[torch.Tensor, torch.Tensor]:
        mean, log_std = self(obs)
        std = log_std.exp()
        normal = torch.distributions.Normal(mean, std)
        raw = normal.rsample()
        action = torch.tanh(raw)
        log_prob = normal.log_prob(raw) - torch.log(1.0 - action.pow(2) + 1e-6)
        return action, log_prob.sum(dim=-1)


class AwacLearner:
    def __init__(
        self,
        *,
        awac_lambda: float = 1.0,
        gamma: float = 0.99,
        tau: float = 0.005,
        lr: float = 3e-4,
        batch_size: int = 256,
        hidden_units: int = 128,
        num_layers: int = 2,
        device: str = "cpu",
    ):
        self.device = torch.device(device)
        self.gamma = gamma
        self.tau = tau
        self.batch_size = batch_size
        self.awac_lambda = max(awac_lambda, 1e-3)

        self.actor = GaussianPolicy(OBS_DIM, ACT_DIM, hidden_units, num_layers).to(self.device)
        self.critic1 = Mlp(OBS_DIM + ACT_DIM, 1, hidden_units, num_layers).to(self.device)
        self.critic2 = Mlp(OBS_DIM + ACT_DIM, 1, hidden_units, num_layers).to(self.device)
        self.critic1_t = Mlp(OBS_DIM + ACT_DIM, 1, hidden_units, num_layers).to(self.device)
        self.critic2_t = Mlp(OBS_DIM + ACT_DIM, 1, hidden_units, num_layers).to(self.device)
        self.critic1_t.load_state_dict(self.critic1.state_dict())
        self.critic2_t.load_state_dict(self.critic2.state_dict())

        self.actor_opt = torch.optim.Adam(self.actor.parameters(), lr=lr)
        self.critic_opt = torch.optim.Adam(
            list(self.critic1.parameters()) + list(self.critic2.parameters()), lr=lr
        )
        self.log_alpha = torch.zeros(1, requires_grad=True, device=self.device)
        self.alpha_opt = torch.optim.Adam([self.log_alpha], lr=lr)
        self.target_entropy = -float(ACT_DIM)

        self.online: List[Transition] = []
        self.expert: List[Transition] = []

    def set_expert_buffer(self, transitions: List[Transition]) -> None:
        self.expert = list(transitions)

    def remember(self, tr: Transition) -> None:
        self.online.append(tr)
        if len(self.online) > 200_000:
            self.online.pop(0)

    def select_action(self, obs: np.ndarray, deterministic: bool = False) -> np.ndarray:
        obs_t = torch.as_tensor(obs, dtype=torch.float32, device=self.device).unsqueeze(0)
        with torch.no_grad():
            mean, log_std = self.actor(obs_t)
            if deterministic:
                action = mean
            else:
                std = log_std.exp()
                action = torch.tanh(torch.distributions.Normal(mean, std).sample())
        return action.cpu().numpy()[0]

    def _sample_batch(self, pool: List[Transition]) -> Optional[Dict[str, torch.Tensor]]:
        if len(pool) < self.batch_size:
            return None
        batch = random.sample(pool, self.batch_size)
        obs = torch.as_tensor(np.stack([b.obs for b in batch]), dtype=torch.float32, device=self.device)
        act = torch.as_tensor(np.stack([b.expert_action for b in batch]), dtype=torch.float32, device=self.device)
        rew = torch.as_tensor([b.reward for b in batch], dtype=torch.float32, device=self.device).unsqueeze(1)
        nobs = torch.as_tensor(np.stack([b.next_obs for b in batch]), dtype=torch.float32, device=self.device)
        done = torch.as_tensor([float(b.done) for b in batch], dtype=torch.float32, device=self.device).unsqueeze(1)
        return {"obs": obs, "act": act, "rew": rew, "nobs": nobs, "done": done}

    def _soft_update(self) -> None:
        for tgt, src in ((self.critic1_t, self.critic1), (self.critic2_t, self.critic2)):
            for tp, sp in zip(tgt.parameters(), src.parameters()):
                tp.data.copy_(self.tau * sp.data + (1.0 - self.tau) * tp.data)

    def update(self) -> Dict[str, float]:
        metrics: Dict[str, float] = {}
        batch = self._sample_batch(self.online)
        if batch is None:
            return metrics

        obs, act, rew, nobs, done = batch["obs"], batch["act"], batch["rew"], batch["nobs"], batch["done"]
        with torch.no_grad():
            next_action, next_logp = self.actor.sample(nobs)
            q1_t = self.critic1_t(torch.cat([nobs, next_action], dim=-1))
            q2_t = self.critic2_t(torch.cat([nobs, next_action], dim=-1))
            alpha = self.log_alpha.exp()
            target_q = rew + (1.0 - done) * self.gamma * (torch.min(q1_t, q2_t) - alpha * next_logp.unsqueeze(1))

        q1 = self.critic1(torch.cat([obs, act], dim=-1))
        q2 = self.critic2(torch.cat([obs, act], dim=-1))
        critic_loss = F.mse_loss(q1, target_q) + F.mse_loss(q2, target_q)
        self.critic_opt.zero_grad()
        critic_loss.backward()
        self.critic_opt.step()
        metrics["critic_loss"] = float(critic_loss.item())

        new_action, logp = self.actor.sample(obs)
        q1_pi = self.critic1(torch.cat([obs, new_action], dim=-1))
        q2_pi = self.critic2(torch.cat([obs, new_action], dim=-1))
        alpha = self.log_alpha.exp()
        actor_loss = (alpha * logp.unsqueeze(1) - torch.min(q1_pi, q2_pi)).mean()

        expert_batch = self._sample_batch(self.expert)
        if expert_batch is not None:
            e_obs = expert_batch["obs"]
            e_act = expert_batch["act"]
            with torch.no_grad():
                q1_e = self.critic1(torch.cat([e_obs, e_act], dim=-1))
                q2_e = self.critic2(torch.cat([e_obs, e_act], dim=-1))
                q_e = torch.min(q1_e, q2_e)
                pi_act, _ = self.actor.sample(e_obs)
                q1_pi = self.critic1(torch.cat([e_obs, pi_act], dim=-1))
                q2_pi = self.critic2(torch.cat([e_obs, pi_act], dim=-1))
                v = torch.min(q1_pi, q2_pi)
                advantage = (q_e - v).clamp(max=20.0)
                weights = torch.exp(advantage / self.awac_lambda).clamp(max=100.0)
            mean, log_std = self.actor(e_obs)
            std = log_std.exp()
            dist = torch.distributions.Normal(mean, std)
            raw = torch.atanh(e_act.clamp(-0.999, 0.999))
            log_prob = dist.log_prob(raw) - torch.log(1.0 - e_act.pow(2) + 1e-6)
            awac_loss = -(weights * log_prob.sum(dim=-1, keepdim=True)).mean()
            actor_loss = actor_loss + awac_loss
            metrics["awac_loss"] = float(awac_loss.item())

        self.actor_opt.zero_grad()
        actor_loss.backward()
        self.actor_opt.step()
        metrics["actor_loss"] = float(actor_loss.item())

        alpha_loss = -(self.log_alpha * (logp + self.target_entropy).detach()).mean()
        self.alpha_opt.zero_grad()
        alpha_loss.backward()
        self.alpha_opt.step()
        metrics["alpha"] = float(self.log_alpha.exp().item())

        self._soft_update()
        return metrics

    def save(self, path: Path) -> None:
        path.parent.mkdir(parents=True, exist_ok=True)
        torch.save(
            {
                "actor": self.actor.state_dict(),
                "critic1": self.critic1.state_dict(),
                "critic2": self.critic2.state_dict(),
                "log_alpha": self.log_alpha.detach().cpu(),
            },
            path,
        )


def run_training(args: argparse.Namespace) -> int:
    cfg = load_trainer_yaml(Path(args.trainer_config))
    behavior_name = args.behavior_name
    beh = behavior_config(cfg, behavior_name)
    hyper = beh.get("hyperparameters") or {}
    max_steps = int(beh.get("max_steps", 500_000))
    awac_lambda = float(hyper.get("awac_lambda", beh.get("awac_lambda", 1.0)))
    gamma = float((beh.get("reward_signals") or {}).get("extrinsic", {}).get("gamma", 0.99))
    tau = float(hyper.get("tau", 0.005))
    lr = float(hyper.get("learning_rate", 3e-4))
    batch_size = int(hyper.get("batch_size", 256))
    hidden = int((beh.get("network_settings") or {}).get("hidden_units", 128))
    layers = int((beh.get("network_settings") or {}).get("num_layers", 2))
    buffer_init = int(hyper.get("buffer_init_steps", 1000))

    streaming_root = Path(args.streaming_assets) if args.streaming_assets else DEFAULT_STREAMING
    results_dir = Path(args.results_dir)
    run_dir = results_dir / behavior_name / args.run_id
    run_dir.mkdir(parents=True, exist_ok=True)

    learner = AwacLearner(
        awac_lambda=awac_lambda,
        gamma=gamma,
        tau=tau,
        lr=lr,
        batch_size=batch_size,
        hidden_units=hidden,
        num_layers=layers,
        device="cuda" if torch.cuda.is_available() and not args.cpu else "cpu",
    )

    expert = wait_for_expert_buffer(
        streaming_root,
        timeout_s=float(args.expert_wait_s),
        min_transitions=int(args.expert_min_transitions),
    )
    learner.set_expert_buffer(expert)
    if expert:
        print(f"[awac] Expert reward mean={np.mean([t.reward for t in expert]):.4f}", flush=True)

    print("[awac] Connecting to Unity Academy...", flush=True)
    engine_channel = EngineConfigurationChannel()
    engine_channel.set_configuration_parameters(time_scale=float(args.time_scale))
    env = UnityEnvironment(
        file_name=None,
        base_port=int(args.base_port),
        timeout_wait=int(args.timeout_wait),
        no_graphics=bool(args.no_graphics),
        side_channels=[engine_channel],
    )
    try:
        env.reset()
        behavior_names = list(env.behavior_specs.keys())
        if behavior_name not in behavior_names:
            if behavior_names:
                behavior_name = behavior_names[0]
            else:
                print("[awac] No behaviors registered in Unity.", file=sys.stderr)
                return 1

        spec = env.behavior_specs[behavior_name]
        if spec.action_spec.continuous_size != ACT_DIM:
            print(
                f"[awac] Expected {ACT_DIM} continuous actions, got {spec.action_spec.continuous_size}",
                file=sys.stderr,
            )
            return 1

        step = 0
        prev_obs: Optional[np.ndarray] = None
        prev_action: Optional[np.ndarray] = None
        episode_reward = 0.0
        expert_reload_deadline = time.time() + float(args.expert_wait_s)

        while step < max_steps:
            decision, terminal = env.get_steps(behavior_name)
            if len(decision) == 0:
                env.step()
                continue

            n_agents = len(decision)
            raw_obs = extract_agent_obs(decision, agent_index=0)
            obs = align_obs_to_atd(raw_obs)
            if step == 0:
                print(
                    f"[awac] {n_agents} deciding agent(s); raw obs={raw_obs.shape[0]} "
                    f"aligned to ATD {obs.shape[0]}",
                    flush=True,
                )

            action = learner.select_action(obs, deterministic=False)
            if n_agents == 1:
                action_batch = np.asarray([action], dtype=np.float32)
            else:
                action_batch = np.zeros((n_agents, ACT_DIM), dtype=np.float32)
                action_batch[0] = action
            env.set_actions(behavior_name, ActionTuple(continuous=action_batch))
            env.step()
            step += 1

            reward = float(decision.reward[0]) if len(decision.reward) else 0.0
            episode_reward += reward
            leader_id = decision.agent_id[0] if len(decision) > 0 else None
            # DecisionSteps has no interrupted flag in this mlagents_envs version; use TerminalSteps.
            done = leader_id is not None and len(terminal) > 0 and leader_id in terminal

            if prev_obs is not None and prev_action is not None:
                learner.remember(
                    Transition(
                        obs=prev_obs,
                        expert_action=prev_action,
                        reward=reward,
                        next_obs=obs,
                        done=done,
                    )
                )

            if step > buffer_init and step % int(hyper.get("steps_per_update", 1)) == 0:
                metrics = learner.update()
                if metrics and step % 1200 == 0:
                    summary = " ".join(f"{k}={v:.4f}" for k, v in metrics.items())
                    print(f"[awac] step={step} episode_reward={episode_reward:.3f} {summary}", flush=True)

            if done:
                episode_reward = 0.0

            prev_obs = obs
            prev_action = action

            if not expert and time.time() < expert_reload_deadline and step % 500 == 0:
                refreshed = load_expert_transitions(expert_paths(streaming_root)[0])
                if len(refreshed) > len(learner.expert):
                    learner.set_expert_buffer(refreshed)
                    print(f"[awac] Reloaded expert buffer ({len(refreshed)} transitions).", flush=True)

            if step % int(beh.get("checkpoint_interval", 5000)) == 0:
                learner.save(run_dir / f"awac_{step}.pt")

        learner.save(run_dir / "awac_final.pt")
        print(f"[awac] Training complete at step={step}. Checkpoints in {run_dir}", flush=True)
        return 0
    finally:
        env.close()


def parse_args() -> argparse.Namespace:
    p = argparse.ArgumentParser(description="AGAMOTTO hybrid AWAC trainer")
    p.add_argument("trainer_config", help="YAML trainer config (trainer_type: awac)")
    p.add_argument("--run-id", default="hybrid_awac")
    p.add_argument("--results-dir", default="results")
    p.add_argument("--behavior-name", default="AGAMOTTO_RL")
    p.add_argument("--base-port", type=int, default=5004)
    p.add_argument("--timeout-wait", type=int, default=5000)
    p.add_argument("--time-scale", type=float, default=50.0)
    p.add_argument("--no-graphics", action="store_true", help="Headless build only; leave off for Unity Editor")
    p.add_argument("--streaming-assets", default="")
    p.add_argument("--expert-wait-s", type=float, default=900.0)
    p.add_argument("--expert-min-transitions", type=int, default=32)
    p.add_argument("--cpu", action="store_true")
    return p.parse_args()


def main() -> int:
    return run_training(parse_args())


if __name__ == "__main__":
    raise SystemExit(main())
