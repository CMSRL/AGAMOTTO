#!/usr/bin/env python3
"""
Plot per-episode reward curves for all ablations in an evaluation suite run.

Reads CSVs produced by evaluation_suite.py via training_eval_summarize.py
(e.g. baseline_sac/baseline_sac_episode_metrics.csv).

Usage:
  cd "AGAMOTTO/AGAMOTTO Server"
  pip install matplotlib
  python plot_eval_rewards.py --suite-dir evaluation/results/agamotto_atd_eval_v1/20250519_120000
  python plot_eval_rewards.py --suite-dir ... --output figures/rewards.png --rolling 20
  python plot_eval_rewards.py --csv path/a.csv --csv path/b.csv --labels "baseline" "no curiosity"
"""

from __future__ import annotations

import argparse
import csv
import sys
from pathlib import Path
from typing import Dict, List, Optional, Sequence, Tuple

import numpy as np

try:
    import matplotlib.pyplot as plt
except ImportError:
    print("Install matplotlib: pip install matplotlib", file=sys.stderr)
    raise SystemExit(1)

SERVER_DIR = Path(__file__).resolve().parent
DEFAULT_MANIFEST = SERVER_DIR / "evaluation" / "manifest.yaml"

# Default experiment order (matches evaluation/manifest.yaml).
DEFAULT_EXPERIMENT_ORDER: List[str] = [
    "baseline_sac",
    "ablation_no_curiosity",
    "ablation_lower_lr",
    "ablation_shorter_horizon",
    "ablation_no_squad_rewards",
    "ablation_no_tak_feedback",
]

SHORT_LABELS: Dict[str, str] = {
    "baseline_sac": "Baseline SAC",
    "ablation_no_curiosity": "No curiosity",
    "ablation_lower_lr": "Lower LR",
    "ablation_shorter_horizon": "Shorter horizon",
    "ablation_no_squad_rewards": "No squad rewards",
    "ablation_no_tak_feedback": "No TAK feedback",
}


def _rolling_mean(arr: np.ndarray, window: int) -> np.ndarray:
    if window < 1:
        window = 1
    if len(arr) < window:
        return np.array([], dtype=float)
    kernel = np.ones(window, dtype=float) / window
    return np.convolve(arr, kernel, mode="valid")


def load_manifest_experiments(manifest_path: Path) -> List[str]:
    try:
        import yaml
    except ImportError:
        return DEFAULT_EXPERIMENT_ORDER
    if not manifest_path.is_file():
        return DEFAULT_EXPERIMENT_ORDER
    data = yaml.safe_load(manifest_path.read_text(encoding="utf-8")) or {}
    exps = data.get("experiments") or []
    ids = [e.get("id") for e in exps if e.get("id")]
    return ids if ids else DEFAULT_EXPERIMENT_ORDER


def load_episode_metrics_csv(csv_path: Path) -> Tuple[np.ndarray, np.ndarray]:
    episodes: List[int] = []
    rewards: List[float] = []
    with csv_path.open(newline="", encoding="utf-8") as f:
        reader = csv.DictReader(f)
        if not reader.fieldnames:
            raise ValueError(f"empty CSV: {csv_path}")
        reward_col = None
        for candidate in ("cumulative_reward", "episode_return", "reward"):
            if candidate in reader.fieldnames:
                reward_col = candidate
                break
        if reward_col is None:
            raise ValueError(
                f"{csv_path}: expected a reward column "
                f"(cumulative_reward / episode_return / reward), got {reader.fieldnames}"
            )
        ep_col = "episode_index" if "episode_index" in reader.fieldnames else None
        for row in reader:
            rewards.append(float(row[reward_col]))
            if ep_col:
                episodes.append(int(row[ep_col]))
            else:
                episodes.append(len(episodes))
    if not rewards:
        raise ValueError(f"no data rows in {csv_path}")
    return np.array(episodes, dtype=int), np.array(rewards, dtype=float)


def discover_suite_csvs(suite_dir: Path, experiment_order: Sequence[str]) -> Dict[str, Path]:
    found: Dict[str, Path] = {}
    for exp_id in experiment_order:
        exp_dir = suite_dir / exp_id
        if not exp_dir.is_dir():
            continue
        matches = sorted(exp_dir.glob("*_episode_metrics.csv"))
        if not matches:
            matches = sorted(exp_dir.glob("*.csv"))
            matches = [p for p in matches if p.name != "suite_summary.csv"]
        if matches:
            found[exp_id] = matches[-1]
    if found:
        return found

    # Flat layout: all *_episode_metrics.csv directly under suite_dir.
    for path in sorted(suite_dir.glob("*_episode_metrics.csv")):
        stem = path.name.replace("_episode_metrics.csv", "")
        if stem in experiment_order:
            found[stem] = path
    return found


def resolve_series(
    suite_dir: Optional[Path],
    csv_paths: List[Path],
    labels: List[str],
    experiment_order: Sequence[str],
) -> List[Tuple[str, Path]]:
    series: List[Tuple[str, Path]] = []
    if csv_paths:
        for i, path in enumerate(csv_paths):
            label = labels[i] if i < len(labels) else path.stem
            series.append((label, path))
        return series

    if suite_dir is None:
        return series

    discovered = discover_suite_csvs(suite_dir, experiment_order)
    for exp_id in experiment_order:
        path = discovered.get(exp_id)
        if path is None:
            continue
        label = SHORT_LABELS.get(exp_id, exp_id)
        series.append((label, path))
    return series


def plot_rewards(
    series: List[Tuple[str, Path]],
    *,
    rolling: int,
    show_raw: bool,
    facets: bool,
    title: str,
    output: Optional[Path],
    show: bool,
) -> None:
    if not series:
        print("No episode_metrics CSVs found.", file=sys.stderr)
        raise SystemExit(2)

    if facets:
        n = len(series)
        ncols = 3
        nrows = (n + ncols - 1) // ncols
        fig, axes = plt.subplots(nrows, ncols, figsize=(4.5 * ncols, 3.2 * nrows), squeeze=False)
        axes_flat = axes.flatten()
    else:
        fig, ax = plt.subplots(figsize=(10, 5.5))
        ax = ax  # noqa: PLW0127 — single axis

    colors = plt.cm.tab10(np.linspace(0, 0.9, max(len(series), 1)))

    for idx, (label, csv_path) in enumerate(series):
        episodes, rewards = load_episode_metrics_csv(csv_path)
        smooth = _rolling_mean(rewards, rolling)
        ep_smooth = episodes[rolling - 1 :] if len(smooth) else episodes[:0]

        target_ax = axes_flat[idx] if facets else ax
        color = colors[idx % len(colors)]

        if show_raw:
            target_ax.plot(
                episodes,
                rewards,
                color=color,
                alpha=0.25,
                linewidth=0.8,
                label="_nolegend_",
            )
        if len(smooth):
            target_ax.plot(
                ep_smooth,
                smooth,
                color=color,
                linewidth=1.8,
                label=label if not facets else f"rolling mean (w={rolling})",
            )
        else:
            target_ax.plot(episodes, rewards, color=color, linewidth=1.8, label=label)

        if facets:
            target_ax.set_title(label, fontsize=10)
            target_ax.set_xlabel("Episode")
            target_ax.set_ylabel("Episode return")
            target_ax.grid(True, alpha=0.3)
            target_ax.legend(loc="best", fontsize=8)
        else:
            target_ax.set_xlabel("Episode")
            target_ax.set_ylabel("Episode return")
            target_ax.set_title(title)
            target_ax.grid(True, alpha=0.3)
            target_ax.legend(loc="best", fontsize=9)

    if facets:
        for j in range(len(series), len(axes_flat)):
            axes_flat[j].set_visible(False)
        fig.suptitle(title, fontsize=12)
    elif rolling > 1:
        ax.text(
            0.99,
            0.01,
            f"rolling mean, window={rolling}",
            transform=ax.transAxes,
            ha="right",
            va="bottom",
            fontsize=8,
            alpha=0.7,
        )

    fig.tight_layout()
    if output:
        output.parent.mkdir(parents=True, exist_ok=True)
        fig.savefig(output, dpi=150, bbox_inches="tight")
        print(f"Wrote {output.resolve()}")
    if show or not output:
        plt.show()
    plt.close(fig)


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Plot episode reward over training for eval-suite ablations"
    )
    parser.add_argument(
        "--suite-dir",
        type=Path,
        help="Eval suite output folder (subdirs per experiment_id with *_episode_metrics.csv)",
    )
    parser.add_argument(
        "--csv",
        action="append",
        type=Path,
        default=[],
        help="Episode metrics CSV (repeat for multiple runs; use with --labels)",
    )
    parser.add_argument(
        "--labels",
        action="append",
        default=[],
        help="Legend label per --csv (same order)",
    )
    parser.add_argument(
        "--manifest",
        type=Path,
        default=DEFAULT_MANIFEST,
        help="manifest.yaml for experiment order (default: evaluation/manifest.yaml)",
    )
    parser.add_argument(
        "--rolling",
        type=int,
        default=20,
        help="Rolling mean window in episodes (default: 20)",
    )
    parser.add_argument(
        "--show-raw",
        action="store_true",
        help="Overlay faint per-episode returns behind the rolling mean",
    )
    parser.add_argument(
        "--facets",
        action="store_true",
        help="One subplot per ablation instead of overlaid lines",
    )
    parser.add_argument(
        "--title",
        default="AGAMOTTO eval — reward over episodes (ablations)",
        help="Figure title",
    )
    parser.add_argument(
        "--output",
        "-o",
        type=Path,
        help="Save figure to this path (e.g. rewards.png); omit to only show interactively",
    )
    parser.add_argument(
        "--no-show",
        action="store_true",
        help="Do not open an interactive window (use with --output)",
    )
    args = parser.parse_args()

    if not args.suite_dir and not args.csv:
        parser.error("Provide --suite-dir or at least one --csv")

    experiment_order = load_manifest_experiments(args.manifest)
    series = resolve_series(
        args.suite_dir,
        args.csv,
        args.labels,
        experiment_order,
    )

    if args.suite_dir:
        discovered_ids = set(discover_suite_csvs(args.suite_dir, experiment_order).keys())
        missing_ids = [e for e in experiment_order if e not in discovered_ids]
        if missing_ids:
            print(
                f"[plot] Warning: no episode_metrics CSV for: {', '.join(missing_ids)}",
                file=sys.stderr,
            )

    print(f"[plot] Plotting {len(series)} run(s):")
    for label, path in series:
        print(f"  - {label}: {path}")

    plot_rewards(
        series,
        rolling=args.rolling,
        show_raw=args.show_raw,
        facets=args.facets,
        title=args.title,
        output=args.output,
        show=not args.no_show,
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
