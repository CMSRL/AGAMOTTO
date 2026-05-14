"""
Summarize ML-Agents training runs for research reporting (TensorBoard event files).

Dependencies:
  pip install tensorboard numpy

Usage:
  python training_eval_summarize.py path/to/results/<behavior>/<run_id>

Typical ML-Agents layout after mlagents-learn:
  results/<experiment>/<behavior_name>/<run_timestamp>/events.out.tfevents.*

Metrics computed (see paper text in repo discussion / your methods section):
  - Episode return: Cumulative Reward per completed episode (tag name varies by version).
  - Steps: environment or policy steps if present in logs.
  - Convergence: operational definition via rolling mean + patience (CLI args).

This script is best-effort on scalar tag names across ML-Agents versions; extend
TAG_CANDIDATES if your run uses different tags (inspect with tensorboard --logdir).
"""

from __future__ import annotations

import argparse
import glob
import json
import os
import sys
from dataclasses import dataclass, asdict
from typing import List, Optional, Tuple

import numpy as np

try:
    from tensorboard.backend.event_processing import event_accumulator as ea_module
except ImportError:
    print("Install tensorboard: pip install tensorboard", file=sys.stderr)
    sys.exit(1)


# ML-Agents / PyTorch often logs these (first match wins per category)
REWARD_TAGS = [
    "Environment/Cumulative Reward",
    "Cumulative Reward",
]
STEP_TAGS = [
    "Environment/Episode Length",
    "Episode Length",
]


def _first_scalar(
    accumulator: "ea_module.EventAccumulator", candidates: List[str]
) -> Optional[Tuple[str, List[Tuple[float, float, int]]]]:
    """Returns (tag, [(wall_time, step, value), ...]) for first tag that exists."""
    tags = set(accumulator.Tags()["scalars"])
    for tag in candidates:
        if tag in tags:
            events = accumulator.Scalars(tag)
            return tag, [(e.wall_time, e.step, e.value) for e in events]
    return None


@dataclass
class RunSummary:
    run_path: str
    reward_tag: Optional[str]
    episode_length_tag: Optional[str]
    n_episode_points: int
    mean_return_all: float
    std_return_all: float
    mean_return_last_k: float
    last_k_episodes: int
    wall_time_hours: float
    convergence_step: Optional[float]
    convergence_episode_index: Optional[int]
    convergence_definition: str


def load_accumulator(run_dir: str) -> "ea_module.EventAccumulator":
    paths = sorted(glob.glob(os.path.join(run_dir, "events.out.tfevents.*")))
    if not paths:
        raise FileNotFoundError(f"No events.out.tfevents.* under {run_dir}")
    acc = ea_module.EventAccumulator(
        run_dir,
        size_guidance={
            ea_module.COMPRESSED_HISTOGRAMS: 0,
            ea_module.IMAGES: 0,
            ea_module.AUDIO: 0,
            ea_module.SCALARS: 0,
            ea_module.HISTOGRAMS: 0,
        },
    )
    acc.Reload()
    return acc


def summarize(
    run_dir: str,
    rolling_window: int,
    success_threshold: float,
    patience: int,
    last_k: int,
) -> RunSummary:
    acc = load_accumulator(run_dir)
    reward_info = _first_scalar(acc, REWARD_TAGS)
    if reward_info is None:
        available = sorted(acc.Tags().get("scalars", []))
        raise RuntimeError(
            "No cumulative reward tag found. Available scalars:\n"
            + "\n".join(available[:80])
            + ("\n..." if len(available) > 80 else "")
        )
    reward_tag, reward_events = reward_info
    returns = np.array([e[2] for e in reward_events], dtype=float)
    steps = np.array([e[1] for e in reward_events], dtype=float)
    times = np.array([e[0] for e in reward_events], dtype=float)

    len_info = _first_scalar(acc, STEP_TAGS)
    ep_len_tag = len_info[0] if len_info else None

    if len(returns) == 0:
        raise RuntimeError("Reward series empty")

    mean_all = float(np.mean(returns))
    std_all = float(np.std(returns))
    k = min(last_k, len(returns))
    mean_last = float(np.mean(returns[-k:]))

    wall_hours = float((times[-1] - times[0]) / 3600.0) if len(times) > 1 else 0.0

    # Rolling mean on episode index (assumes one scalar per completed episode)
    if rolling_window < 1:
        rolling_window = 1
    if len(returns) >= rolling_window:
        kernel = np.ones(rolling_window) / rolling_window
        ma = np.convolve(returns, kernel, mode="valid")
        # ma[i] aligns with episode index rolling_window - 1 + i
        conv_ep = None
        conv_step = None
        run = 0
        for i, v in enumerate(ma):
            if v >= success_threshold:
                run += 1
                if run >= patience:
                    ep_idx = rolling_window - 1 + i
                    conv_ep = int(ep_idx)
                    conv_step = float(steps[ep_idx]) if ep_idx < len(steps) else None
                    break
            else:
                run = 0
        conv_def = (
            f"First episode index where {rolling_window}-episode mean return "
            f">= {success_threshold} for {patience} consecutive rolling windows"
        )
    else:
        conv_ep, conv_step, conv_def = None, None, "Not enough episodes for rolling window"

    return RunSummary(
        run_path=os.path.abspath(run_dir),
        reward_tag=reward_tag,
        episode_length_tag=ep_len_tag,
        n_episode_points=int(len(returns)),
        mean_return_all=mean_all,
        std_return_all=std_all,
        mean_return_last_k=mean_last,
        last_k_episodes=k,
        wall_time_hours=wall_hours,
        convergence_step=conv_step,
        convergence_episode_index=conv_ep,
        convergence_definition=conv_def,
    )


def main() -> None:
    p = argparse.ArgumentParser(description="Summarize ML-Agents TensorBoard run for papers")
    p.add_argument("run_dir", help="Directory containing events.out.tfevents.*")
    p.add_argument("--rolling", type=int, default=20, help="Rolling mean window (episodes)")
    p.add_argument(
        "--threshold",
        type=float,
        default=0.0,
        help="Return threshold for convergence (tune to your reward scale / success proxy)",
    )
    p.add_argument(
        "--patience",
        type=int,
        default=5,
        help="Consecutive rolling windows above threshold",
    )
    p.add_argument(
        "--last-k",
        type=int,
        default=50,
        help="Episodes for 'final performance' mean return",
    )
    p.add_argument("--json", action="store_true", help="Print JSON only")
    args = p.parse_args()

    s = summarize(
        args.run_dir,
        rolling_window=args.rolling,
        success_threshold=args.threshold,
        patience=args.patience,
        last_k=args.last_k,
    )
    if args.json:
        print(json.dumps(asdict(s), indent=2))
        return

    print("=== AGAMOTTO / ML-Agents run summary ===\n")
    for k, v in asdict(s).items():
        print(f"{k}: {v}")
    print(
        "\nTip: set --threshold from a pilot run (e.g. 80% of best rolling mean) "
        "so convergence is meaningful for your reward scale."
    )


if __name__ == "__main__":
    main()
