#!/usr/bin/env python3
"""Draw the RIDE combat SCM and the fitted edges from a CausalRecorder report.

Reads <jsonl>.report.json (or a JSONL, which looks for the sibling report).
Saves a PNG next to that file.

Examples:
  python causal/visualize_graph.py path/to/causal_YYYYMMDD_HHMMSS.jsonl.report.json
  python causal/visualize_graph.py path/to/causal_YYYYMMDD_HHMMSS.jsonl
"""

from __future__ import annotations

import argparse
import json
import math
import sys
from pathlib import Path
from typing import Any, Dict, List, Optional, Tuple

NODE_LABEL = {
    "elev": "elev\nmap height",
    "slope_deg": "slope_deg\nlocal slope",
    "dist": "dist\nshot range",
    "b_exposure": "exposure\nposture",
    "suppression": "suppression",
    "p_hit_sim": "p_hit_sim\nRIDE accuracy",
    "hit": "hit",
    "force_ratio": "force_ratio\nBlue / Red",
    "enemy_killed": "enemy_killed",
    "depth": "depth\nfile length",
    "blue_losses": "blue_losses",
}

NODE_XY = {
    "elev": (0.08, 0.78),
    "slope_deg": (0.08, 0.28),
    "dist": (0.28, 0.78),
    "b_exposure": (0.46, 0.78),
    "suppression": (0.64, 0.78),
    "p_hit_sim": (0.46, 0.48),
    "hit": (0.46, 0.16),
    "force_ratio": (0.84, 0.78),
    "enemy_killed": (0.84, 0.48),
    "depth": (0.93, 0.48),
    "blue_losses": (0.93, 0.16),
}

EDGES: List[Tuple[str, str, str, str]] = [
    ("elev", "slope_deg", "trial_slope_from_elev", "elev"),
    ("dist", "p_hit_sim", "p_hit_from_ride", "dist"),
    ("b_exposure", "p_hit_sim", "p_hit_from_ride", "b_exposure"),
    ("suppression", "p_hit_sim", "p_hit_from_ride", "suppression"),
    ("p_hit_sim", "hit", "hit_from_p_hit", "p_hit_sim"),
    ("force_ratio", "enemy_killed", "trial_killed_from_force", "force_ratio"),
    ("depth", "blue_losses", "trial_losses_from_depth", "depth"),
]


def _fmt(value: Any, digits: int = 3) -> str:
    if value is None:
        return "na"
    try:
        number = float(value)
    except (TypeError, ValueError):
        return str(value)
    if math.isnan(number) or math.isinf(number):
        return "na"
    if abs(number) < 1e-12:
        return "0"
    return f"{number:.{digits}g}"


def _load_report(path: Path) -> Dict[str, Any]:
    text = path.read_text(encoding="utf-8")
    if path.suffix == ".jsonl" or path.name.endswith(".jsonl"):
        sibling = path.with_suffix(path.suffix + ".report.json")
        if not sibling.is_file():
            raise FileNotFoundError(
                f"No report at {sibling}. Run fit_graph.py on the JSONL first."
            )
        path = sibling
        text = path.read_text(encoding="utf-8")
    report = json.loads(text)
    if not isinstance(report, dict) or "equations" not in report:
        raise ValueError(f"{path} is not a fit_graph report (missing 'equations').")
    report["_source_path"] = str(path)
    return report


def _eq(report: Dict[str, Any], key: str) -> Dict[str, Any]:
    return report.get("equations", {}).get(key) or {}


def _edge_caption(report: Dict[str, Any], eq_key: str, coef_key: str) -> str:
    if eq_key.startswith("ate:"):
        ate = (report.get("ate") or {}).get(eq_key.split(":", 1)[1]) or {}
        return f"ATE {_fmt(ate.get('ate'))}"
    eq = _eq(report, eq_key)
    coef = (eq.get("coef") or {}).get(coef_key)
    return f"{_fmt(coef)}  R²={_fmt(eq.get('r2'))}"


def _finite_pairs(trials: List[Dict[str, Any]], x_key: str, y_key: str):
    xs: List[float] = []
    ys: List[float] = []
    for row in trials:
        try:
            x = float(row[x_key])
            y = float(row[y_key])
        except (KeyError, TypeError, ValueError):
            continue
        if math.isnan(x) or math.isnan(y) or math.isinf(x) or math.isinf(y):
            continue
        xs.append(x)
        ys.append(y)
    return xs, ys


def _ols_line(ax, xs: List[float], intercept: Optional[float], slope: Optional[float]) -> None:
    if intercept is None or slope is None or not xs:
        return
    lo, hi = min(xs), max(xs)
    if lo == hi:
        hi = lo + 1.0
    pad = 0.05 * (hi - lo)
    grid = [lo - pad, hi + pad]
    ax.plot(grid, [intercept + slope * v for v in grid], color="#c45c26", linewidth=1.6, zorder=2)


def _scatter(
    ax,
    trials: List[Dict[str, Any]],
    x_key: str,
    y_key: str,
    xlabel: str,
    ylabel: str,
    title: str,
    intercept: Optional[float],
    slope: Optional[float],
    r2: Any,
    n: Any,
) -> None:
    xs, ys = _finite_pairs(trials, x_key, y_key)
    ax.scatter(xs, ys, s=36, c="#1f4e79", alpha=0.85, zorder=3, edgecolors="none")
    _ols_line(ax, xs, intercept, slope)
    ax.set_xlabel(xlabel)
    ax.set_ylabel(ylabel)
    ax.set_title(f"{title}\nn={n}  R²={_fmt(r2)}", loc="left", fontsize=10)
    ax.grid(True, alpha=0.25)


def _draw_dag(ax, report: Dict[str, Any]) -> None:
    ax.set_xlim(0, 1)
    ax.set_ylim(0, 1)
    ax.axis("off")
    ax.set_title("Assumed causes (arrows) with this run's coefficients", loc="left", fontsize=11)

    box = dict(boxstyle="round,pad=0.35", facecolor="#f4f1ea", edgecolor="#333333", linewidth=1.0)
    for name, (x, y) in NODE_XY.items():
        ax.text(x, y, NODE_LABEL[name], ha="center", va="center", fontsize=7.5, bbox=box, zorder=3)

    for parent, child, eq_key, coef_key in EDGES:
        x0, y0 = NODE_XY[parent]
        x1, y1 = NODE_XY[child]
        ax.annotate(
            "",
            xy=(x1, y1 + 0.07),
            xytext=(x0, y0 - 0.07),
            arrowprops=dict(arrowstyle="-|>", color="#333333", lw=1.1),
        )
        mx, my = (x0 + x1) / 2.0, (y0 + y1) / 2.0 + 0.02
        dx = x1 - x0
        if abs(dx) < 0.02:
            ha, tx = "left", mx + 0.03
        elif dx > 0:
            ha, tx = "left", mx + 0.02
        else:
            ha, tx = "right", mx - 0.02
        ax.text(
            tx,
            my,
            _edge_caption(report, eq_key, coef_key),
            fontsize=7,
            color="#c45c26",
            ha=ha,
            va="center",
            zorder=4,
        )

    ax.text(
        0.01,
        0.02,
        "Left: map check. Center: RIDE hit calculator. Right: force and file depth. "
        "Numbers are OLS — not ground truth.",
        fontsize=8,
        color="#444444",
        va="bottom",
    )


def _draw_r2(ax, report: Dict[str, Any]) -> None:
    ride = _eq(report, "p_hit_from_ride")
    height = _eq(report, "p_hit_from_height")
    labels = ["RIDE factors\n→ p_hit", "height alone\n→ p_hit"]
    values = [float(ride.get("r2") or 0.0), float(height.get("r2") or 0.0)]
    colors = ["#1f4e79", "#a33b20"]
    ax.barh([1, 0], values, color=colors, height=0.55)
    ax.set_yticks([1, 0])
    ax.set_yticklabels(labels)
    ax.set_xlim(0, max(0.05, max(values) * 1.25))
    for y, value in zip([1, 0], values):
        ax.text(value, y, f"  {_fmt(value)}", va="center", fontsize=8)
    ax.set_xlabel("R² of p_hit_sim")
    ax.set_title("Is height in the hit model?", loc="left", fontsize=10)
    ax.text(
        0.0,
        -0.7,
        f"shot n={ride.get('n')}   height n={height.get('n')}",
        fontsize=8,
        color="#444444",
        clip_on=False,
    )


def render(report: Dict[str, Any], out_path: Path, show: bool) -> None:
    try:
        import matplotlib.pyplot as plt
    except ImportError as exc:
        raise SystemExit(
            "matplotlib is required: conda activate agamotto-ml && pip install matplotlib"
        ) from exc

    trials = report.get("trials") or []
    shots = report.get("shot_sample") or trials
    fig = plt.figure(figsize=(13.5, 10.5), facecolor="white")
    fig.suptitle(
        f"RIDE combat SCM  ·  {report.get('n_trials')} trials  ·  "
        f"{report.get('n_shot_rows', 0)} shots  ·  "
        f"{Path(report.get('_source_path', '')).name}",
        fontsize=13,
        x=0.01,
        ha="left",
    )

    gs = fig.add_gridspec(3, 3, height_ratios=[1.15, 1.0, 1.0], hspace=0.42, wspace=0.32)
    ax_dag = fig.add_subplot(gs[0, :2])
    ax_r2 = fig.add_subplot(gs[0, 2])
    _draw_dag(ax_dag, report)
    _draw_r2(ax_r2, report)

    slope_eq = _eq(report, "trial_slope_from_elev")
    _scatter(
        fig.add_subplot(gs[1, 0]),
        trials,
        "elev",
        "slope_deg",
        "Blue elevation (m)",
        "slope (°)",
        "elev → slope (map)",
        slope_eq.get("intercept"),
        (slope_eq.get("coef") or {}).get("elev"),
        slope_eq.get("r2"),
        slope_eq.get("n"),
    )

    dist_eq = _eq(report, "p_hit_from_dist")
    _scatter(
        fig.add_subplot(gs[1, 1]),
        shots,
        "dist",
        "p_hit_sim",
        "shot range (m)",
        "p_hit_sim",
        "dist → p_hit",
        dist_eq.get("intercept"),
        (dist_eq.get("coef") or {}).get("dist"),
        dist_eq.get("r2"),
        dist_eq.get("n"),
    )

    hit_eq = _eq(report, "hit_from_p_hit")
    _scatter(
        fig.add_subplot(gs[1, 2]),
        shots,
        "p_hit_sim",
        "hit",
        "p_hit_sim",
        "hit (0/1)",
        "p_hit → hit",
        hit_eq.get("intercept"),
        (hit_eq.get("coef") or {}).get("p_hit_sim"),
        hit_eq.get("r2"),
        hit_eq.get("n"),
    )

    height_eq = _eq(report, "p_hit_from_height")
    _scatter(
        fig.add_subplot(gs[2, 0]),
        shots,
        "height_adv",
        "p_hit_sim",
        "height advantage (m)",
        "p_hit_sim",
        "height → p_hit (alone)",
        height_eq.get("intercept"),
        (height_eq.get("coef") or {}).get("height_adv"),
        height_eq.get("r2"),
        height_eq.get("n"),
    )

    force_eq = _eq(report, "trial_killed_from_force")
    _scatter(
        fig.add_subplot(gs[2, 1]),
        trials,
        "force_ratio",
        "enemy_killed",
        "Blue / Red",
        "enemy killed",
        "force → kill",
        force_eq.get("intercept"),
        (force_eq.get("coef") or {}).get("force_ratio"),
        force_eq.get("r2"),
        force_eq.get("n"),
    )

    loss_eq = _eq(report, "trial_losses_from_depth")
    _scatter(
        fig.add_subplot(gs[2, 2]),
        trials,
        "depth",
        "blue_losses",
        "file depth (m)",
        "Blue losses",
        "depth → blue losses",
        loss_eq.get("intercept"),
        (loss_eq.get("coef") or {}).get("depth"),
        loss_eq.get("r2"),
        loss_eq.get("n"),
    )

    fig.subplots_adjust(left=0.07, right=0.98, top=0.90, bottom=0.07)
    out_path.parent.mkdir(parents=True, exist_ok=True)
    fig.savefig(out_path, dpi=140)
    print(f"wrote {out_path}")
    if show:
        plt.show()
    plt.close(fig)


def main(argv: List[str] | None = None) -> int:
    parser = argparse.ArgumentParser(
        description="Visualize the fitted RIDE combat SCM from a fit_graph report."
    )
    parser.add_argument(
        "path",
        nargs="?",
        default="",
        help="causal_*.jsonl.report.json or the JSONL (uses sibling report).",
    )
    parser.add_argument("--out", default="", help="PNG path (default: <report>.png).")
    parser.add_argument("--no-show", action="store_true", help="Save PNG only; do not open a window.")
    args = parser.parse_args(argv)

    path = Path(args.path) if args.path else None
    if path is None:
        logs = Path(__file__).resolve().parents[2] / "AGAMOTTO Simulation" / "CausalLogs"
        reports = sorted(logs.glob("causal_*.jsonl.report.json")) if logs.is_dir() else []
        if not reports:
            print("Pass a path to the .report.json from fit_graph.py.", file=sys.stderr)
            return 2
        path = reports[-1]
        print(f"using newest report {path}")

    try:
        report = _load_report(path)
    except (OSError, ValueError, json.JSONDecodeError) as exc:
        print(exc, file=sys.stderr)
        return 2

    source = Path(report["_source_path"])
    out_path = Path(args.out) if args.out else source.with_suffix(".png")
    render(report, out_path, show=not args.no_show)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
