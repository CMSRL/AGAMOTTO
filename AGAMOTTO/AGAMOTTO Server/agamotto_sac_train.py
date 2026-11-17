#!/usr/bin/env python3
"""
SAC training + automatic post-training inference.

Workflow (WinTAK):
  1. Place friendly, route, and hostile on the map.
  2. Mode SAC -> Send Map Data.
  3. Unity Play after this script prints "Connecting to Unity Academy...".
  4. Training runs; ONNX is copied; inference episode runs; WinTAK receives
     training_complete and inference_complete (blue route + outcome).

Usage:
  python agamotto_sac_train.py --run-id=sac_run
"""

from __future__ import annotations

import argparse
import shutil
import subprocess
import sys
from pathlib import Path

try:
    import yaml
except ImportError as exc:
    print("Install PyYAML: pip install pyyaml", file=sys.stderr)
    raise SystemExit(1) from exc

from agamotto_inference_common import (
    DEFAULT_STREAMING,
    run_onnx_inference_episodes,
    setup_inference_from_results,
    write_training_active_run,
)

SERVER_DIR = Path(__file__).resolve().parent
DEFAULT_SAC_CONFIG = SERVER_DIR.parent.parent / "AGAMOTTO_RL_sac_tuned.yaml"


def load_trainer_yaml(path: Path) -> dict:
    with path.open(encoding="utf-8") as f:
        return yaml.safe_load(f) or {}


def behavior_config(cfg: dict, behavior_name: str) -> dict:
    behaviors = cfg.get("behaviors") or {}
    if behavior_name in behaviors:
        return behaviors[behavior_name] or {}
    if behaviors:
        return next(iter(behaviors.values())) or {}
    return {}


def find_mlagents_learn() -> str:
    exe = shutil.which("mlagents-learn")
    if exe:
        return exe
    return "mlagents-learn"


def run_mlagents_train(args: argparse.Namespace, config_path: Path) -> int:
    cmd = [
        find_mlagents_learn(),
        str(config_path),
        "--run-id",
        args.run_id,
        "--results-dir",
        args.results_dir,
        "--timeout-wait",
        str(args.timeout_wait),
        "--time-scale",
        str(args.time_scale),
    ]
    if args.force:
        cmd.append("--force")
    if args.no_graphics:
        cmd.append("--no-graphics")
    if args.resume:
        cmd.append("--resume")
    cmd.extend(args.mlagents_extra)

    print(f"[sac] Running: {' '.join(cmd)}", flush=True)
    print(
        "[sac] Start Unity Play when mlagents-learn is waiting for the Academy.",
        flush=True,
    )
    result = subprocess.run(cmd, cwd=str(SERVER_DIR))
    return int(result.returncode)


def parse_args() -> argparse.Namespace:
    p = argparse.ArgumentParser(
        description="AGAMOTTO SAC train + automatic post-training inference"
    )
    p.add_argument(
        "trainer_config",
        nargs="?",
        default=str(DEFAULT_SAC_CONFIG),
        help="ML-Agents YAML config (default: repo AGAMOTTO_RL_sac_tuned.yaml)",
    )
    p.add_argument("--run-id", default="sac_run")
    p.add_argument("--results-dir", default="results")
    p.add_argument("--behavior-name", default="AGAMOTTO_RL")
    p.add_argument("--timeout-wait", type=int, default=5000)
    p.add_argument("--time-scale", type=float, default=50.0)
    p.add_argument("--no-graphics", action="store_true")
    p.add_argument("--force", action="store_true", help="Passed to mlagents-learn")
    p.add_argument("--resume", action="store_true", help="Resume SAC training")
    p.add_argument("--streaming-assets", default="")
    p.add_argument("--leader-callsign", default="leader")
    p.add_argument("--base-port", type=int, default=5004)
    p.add_argument(
        "--inference-episodes",
        type=int,
        default=1,
        help="Deterministic inference episodes after training (Unity -> WinTAK)",
    )
    p.add_argument("--skip-inference", action="store_true")
    p.add_argument(
        "--mlagents-extra",
        nargs=argparse.REMAINDER,
        default=[],
        help="Extra args forwarded to mlagents-learn",
    )
    return p.parse_args()


def main() -> int:
    args = parse_args()
    config_path = Path(args.trainer_config)
    if not config_path.is_file():
        print(f"[sac] Config not found: {config_path}", file=sys.stderr)
        return 1

    cfg = load_trainer_yaml(config_path)
    beh = behavior_config(cfg, args.behavior_name)
    max_steps = int(beh.get("max_steps", 0))

    streaming_root = Path(args.streaming_assets) if args.streaming_assets else DEFAULT_STREAMING
    results_dir = Path(args.results_dir)

    write_training_active_run(
        streaming_root,
        run_id=args.run_id,
        trainer_type="sac",
        dispatch_mode="Training",
        leader_callsign=args.leader_callsign,
    )
    print(f"[sac] Wrote training active_run.json (runId={args.run_id})", flush=True)

    train_rc = run_mlagents_train(args, config_path)
    if train_rc != 0:
        print(f"[sac] mlagents-learn exited with code {train_rc}", file=sys.stderr)
        return train_rc

    print("[sac] Training complete — setting up post-training inference...", flush=True)
    try:
        onnx_dst = setup_inference_from_results(
            streaming_root,
            results_dir,
            behavior_name=args.behavior_name,
            run_id=args.run_id,
            trainer_type="sac",
            total_steps=max_steps,
            leader_callsign=args.leader_callsign,
        )
    except FileNotFoundError as ex:
        print(f"[sac] ERROR: {ex}", file=sys.stderr)
        return 1

    print(f"[sac] Policy ready at {onnx_dst}", flush=True)

    if args.skip_inference or args.inference_episodes <= 0:
        print("[sac] Skipping inference episodes (--skip-inference).", flush=True)
        return 0

    print(
        "[sac] Reconnecting to Unity for inference (keep Unity in Play)...",
        flush=True,
    )
    try:
        completed = run_onnx_inference_episodes(
            onnx_dst,
            args.behavior_name,
            episodes=int(args.inference_episodes),
            base_port=int(args.base_port),
            timeout_wait=int(args.timeout_wait),
            time_scale=float(args.time_scale),
            no_graphics=bool(args.no_graphics),
            tag="sac",
        )
    except Exception as ex:
        print(f"[sac] ERROR during inference: {ex}", file=sys.stderr)
        return 1

    print(f"[sac] Finished {completed} inference episode(s). Check WinTAK Assessment.", flush=True)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
