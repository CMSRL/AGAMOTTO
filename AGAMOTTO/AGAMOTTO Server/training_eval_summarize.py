"""
Summarize ML-Agents training runs for research reporting (TensorBoard event files).

Dependencies:
  pip install tensorboard numpy

Usage:
  python training_eval_summarize.py path/to/results/<behavior>/<run_id>
  python training_eval_summarize.py path/to/results/AGAMOTTO_RL/run_1 --export-csv episode_metrics.csv

Typical ML-Agents layout after mlagents-learn:
  results/<experiment>/<behavior_name>/<run_timestamp>/events.out.tfevents.*

Per-episode kill scalars are written by AGAMOTTO_ATD_Agent (Unity StatsRecorder):
  - AGAMOTTO/Enemies Killed Per Episode
  - AGAMOTTO/Friendlies Lost Per Episode
"""

from __future__ import annotations

import argparse
import csv
import glob
import json
import os
import sys
from dataclasses import dataclass, asdict, field
from typing import List, Optional, Tuple

import numpy as np

try:
    from tensorboard.backend.event_processing import event_accumulator as ea_module
except ImportError:
    print("Install tensorboard: pip install tensorboard", file=sys.stderr)
    sys.exit(1)


REWARD_TAGS = [
    "Environment/Cumulative Reward",
    "Cumulative Reward",
]
STEP_TAGS = [
    "Environment/Episode Length",
    "Episode Length",
]
ENEMY_KILL_TAGS = [
    "AGAMOTTO/Enemies Killed Per Episode",
    "Environment/AGAMOTTO/Enemies Killed Per Episode",
]
FRIENDLY_LOSS_TAGS = [
    "AGAMOTTO/Friendlies Lost Per Episode",
    "Environment/AGAMOTTO/Friendlies Lost Per Episode",
]


def _first_scalar(
    accumulator: "ea_module.EventAccumulator", candidates: List[str]
) -> Optional[Tuple[str, List[Tuple[float, float, int]]]]:
    tags = set(accumulator.Tags()["scalars"])
    for tag in candidates:
        if tag in tags:
            events = accumulator.Scalars(tag)
            return tag, [(e.wall_time, e.step, e.value) for e in events]
    return None


def _find_scalar_by_substring(
    accumulator: "ea_module.EventAccumulator", substrings: List[str]
) -> Optional[Tuple[str, List[Tuple[float, float, int]]]]:
    tags = sorted(accumulator.Tags().get("scalars", []))
    for sub in substrings:
        for tag in tags:
            if sub.lower() in tag.lower():
                events = accumulator.Scalars(tag)
                return tag, [(e.wall_time, e.step, e.value) for e in events]
    return None


def _values(events: Optional[List[Tuple[float, float, int]]]) -> Optional[np.ndarray]:
    if not events:
        return None
    return np.array([e[2] for e in events], dtype=float)


def _rolling_mean(arr: np.ndarray, window: int) -> np.ndarray:
    if window < 1:
        window = 1
    if len(arr) < window:
        return np.array([], dtype=float)
    kernel = np.ones(window) / window
    return np.convolve(arr, kernel, mode="valid")


@dataclass
class KillProgressionSummary:
    tag: Optional[str]
    n_episodes: int
    mean_per_episode: float
    std_per_episode: float
    mean_last_k: float
    total_kills: float
    cumulative_by_episode: List[float] = field(default_factory=list)


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
    enemies_killed: KillProgressionSummary
    friendlies_lost: KillProgressionSummary
    episode_returns: List[float] = field(default_factory=list)
    episode_enemies_killed: List[float] = field(default_factory=list)
    episode_friendlies_lost: List[float] = field(default_factory=list)


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


def _kill_progression(
    events: Optional[List[Tuple[float, float, int]]],
    tag: Optional[str],
    last_k: int,
) -> KillProgressionSummary:
    vals = _values(events)
    if vals is None or len(vals) == 0:
        return KillProgressionSummary(
            tag=tag,
            n_episodes=0,
            mean_per_episode=0.0,
            std_per_episode=0.0,
            mean_last_k=0.0,
            total_kills=0.0,
            cumulative_by_episode=[],
        )
    k = min(last_k, len(vals))
    cumulative = np.cumsum(vals)
    return KillProgressionSummary(
        tag=tag,
        n_episodes=int(len(vals)),
        mean_per_episode=float(np.mean(vals)),
        std_per_episode=float(np.std(vals)),
        mean_last_k=float(np.mean(vals[-k:])),
        total_kills=float(np.sum(vals)),
        cumulative_by_episode=cumulative.tolist(),
    )


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

    enemy_info = _first_scalar(acc, ENEMY_KILL_TAGS)
    if enemy_info is None:
        enemy_info = _find_scalar_by_substring(acc, ["enemies killed", "enemy killed"])
    friendly_info = _first_scalar(acc, FRIENDLY_LOSS_TAGS)
    if friendly_info is None:
        friendly_info = _find_scalar_by_substring(
            acc, ["friendlies lost", "friendly lost", "friendly killed"]
        )

    if len(returns) == 0:
        raise RuntimeError("Reward series empty")

    mean_all = float(np.mean(returns))
    std_all = float(np.std(returns))
    k = min(last_k, len(returns))
    mean_last = float(np.mean(returns[-k:]))

    wall_hours = float((times[-1] - times[0]) / 3600.0) if len(times) > 1 else 0.0

    if rolling_window < 1:
        rolling_window = 1
    if len(returns) >= rolling_window:
        ma = _rolling_mean(returns, rolling_window)
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

    enemy_events = enemy_info[1] if enemy_info else None
    friendly_events = friendly_info[1] if friendly_info else None
    enemy_tag = enemy_info[0] if enemy_info else None
    friendly_tag = friendly_info[0] if friendly_info else None

    enemies = _kill_progression(enemy_events, enemy_tag, last_k)
    friendlies = _kill_progression(friendly_events, friendly_tag, last_k)

    enemy_vals = _values(enemy_events)
    friendly_vals = _values(friendly_events)
    n_align = int(len(returns))
    if enemy_vals is not None and len(enemy_vals) != n_align:
        n_align = min(n_align, len(enemy_vals))
    if friendly_vals is not None and len(friendly_vals) != n_align:
        n_align = min(n_align, len(friendly_vals))

    ep_returns = returns[:n_align].tolist()
    ep_enemies = (
        enemy_vals[:n_align].tolist() if enemy_vals is not None else [0.0] * n_align
    )
    ep_friendlies = (
        friendly_vals[:n_align].tolist()
        if friendly_vals is not None
        else [0.0] * n_align
    )

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
        enemies_killed=enemies,
        friendlies_lost=friendlies,
        episode_returns=ep_returns,
        episode_enemies_killed=ep_enemies,
        episode_friendlies_lost=ep_friendlies,
    )


def export_episode_csv(summary: RunSummary, csv_path: str) -> None:
    n = len(summary.episode_returns)
    cumulative_enemies = np.cumsum(summary.episode_enemies_killed).tolist()
    cumulative_friendlies = np.cumsum(summary.episode_friendlies_lost).tolist()
    with open(csv_path, "w", newline="", encoding="utf-8") as f:
        w = csv.writer(f)
        w.writerow(
            [
                "episode_index",
                "cumulative_reward",
                "enemies_killed_this_episode",
                "friendlies_lost_this_episode",
                "cumulative_enemies_killed",
                "cumulative_friendlies_lost",
            ]
        )
        for i in range(n):
            w.writerow(
                [
                    i,
                    summary.episode_returns[i],
                    summary.episode_enemies_killed[i],
                    summary.episode_friendlies_lost[i],
                    cumulative_enemies[i],
                    cumulative_friendlies[i],
                ]
            )


def _print_kill_section(title: str, kp: KillProgressionSummary, rolling: int) -> None:
    print(f"\n--- {title} ---")
    if kp.n_episodes == 0:
        print("  (not logged — retrain with updated AGAMOTTO_ATD_Agent)")
        return
    print(f"  TensorBoard tag: {kp.tag}")
    print(f"  Episodes logged: {kp.n_episodes}")
    print(f"  Per episode: mean={kp.mean_per_episode:.3f} std={kp.std_per_episode:.3f}")
    print(f"  Last-window mean: {kp.mean_last_k:.3f}")
    print(f"  Total over run: {kp.total_kills:.0f}")
    if kp.cumulative_by_episode:
        cum = np.array(kp.cumulative_by_episode)
        rm = _rolling_mean(cum / np.maximum(np.arange(1, len(cum) + 1), 1), rolling)
        if len(rm) > 0:
            print(f"  Rolling mean kills/episode (window={rolling}): final={rm[-1]:.3f}")
        milestones = [0, len(cum) // 4, len(cum) // 2, 3 * len(cum) // 4, len(cum) - 1]
        seen = set()
        print("  Cumulative kills at milestones (episode index → cumulative total, kills that episode):")
        for idx in milestones:
            if 0 <= idx < len(cum) and idx not in seen:
                seen.add(idx)
                per_ep = float(cum[idx] - (cum[idx - 1] if idx > 0 else 0.0))
                print(f"    ep {idx}: cumulative={cum[idx]:.0f}, this_ep={per_ep:.0f}")


def main() -> None:
    p = argparse.ArgumentParser(description="Summarize ML-Agents TensorBoard run for papers")
    p.add_argument("run_dir", help="Directory containing events.out.tfevents.*")
    p.add_argument("--rolling", type=int, default=20, help="Rolling mean window (episodes)")
    p.add_argument(
        "--threshold",
        type=float,
        default=0.0,
        help="Return threshold for convergence",
    )
    p.add_argument("--patience", type=int, default=5)
    p.add_argument("--last-k", type=int, default=50, help="Episodes for final-window means")
    p.add_argument(
        "--export-csv",
        metavar="PATH",
        help="Write per-episode reward and kill counts to CSV",
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

    if args.export_csv:
        export_episode_csv(s, args.export_csv)

    if args.json:
        print(json.dumps(asdict(s), indent=2))
        return

    print("=== AGAMOTTO / ML-Agents run summary ===\n")
    for key in (
        "run_path",
        "reward_tag",
        "episode_length_tag",
        "n_episode_points",
        "mean_return_all",
        "std_return_all",
        "mean_return_last_k",
        "last_k_episodes",
        "wall_time_hours",
        "convergence_step",
        "convergence_episode_index",
        "convergence_definition",
    ):
        print(f"{key}: {getattr(s, key)}")

    _print_kill_section("Enemies killed (per episode)", s.enemies_killed, args.rolling)
    _print_kill_section("Friendlies lost (per episode)", s.friendlies_lost, args.rolling)

    if args.export_csv:
        print(f"\nPer-episode table written to: {os.path.abspath(args.export_csv)}")
    elif s.enemies_killed.n_episodes == 0:
        print(
            "\nTip: use --export-csv out.csv after a new training run that logs "
            "AGAMOTTO/Enemies Killed Per Episode from Unity."
        )


if __name__ == "__main__":
    main()
