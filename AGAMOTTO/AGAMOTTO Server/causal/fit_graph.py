#!/usr/bin/env python3
"""Fit the v1 terrain SCM from AGAMOTTO_CausalRecorder JSONL.

Expects kind-typed rows: trial_begin, agent, dyad, shot, death, trial_end.

Examples:
  python fit_graph.py path/to/causal_YYYYMMDD_HHMMSS.jsonl
  python fit_graph.py --self-test
"""

from __future__ import annotations

import argparse
import json
import math
import sys
from pathlib import Path
from typing import Any, Dict, List, Optional, Sequence

import numpy as np

try:
    from scm_stats import (
        as_bool01,
        as_float,
        ate_binary,
        column,
        describe,
        fmt,
        load_jsonl,
        mean_finite,
        ols,
        rename_ols,
    )
except ImportError:  # python -m causal.fit_graph
    from causal.scm_stats import (
        as_bool01,
        as_float,
        ate_binary,
        column,
        describe,
        fmt,
        load_jsonl,
        mean_finite,
        ols,
        rename_ols,
    )

SCRIPT_DIR = Path(__file__).resolve().parent
DEFAULT_SAMPLE = SCRIPT_DIR / "sample_causal_graph.jsonl"

DAG: Dict[str, List[str]] = {
    "elev": ["slope_deg", "prom"],
    "height_adv": ["terrain_los"],
    "slope_deg": ["mobility"],
    "terrain_los": ["first_shot", "hit_rate", "enemy_killed"],
}

TRIAL_KEYS = (
    "init_height_adv",
    "elev",
    "slope_deg",
    "prom",
    "terrain_los",
    "mobility",
    "enemy_killed",
    "first_shot",
    "hit_rate",
)


def _kind(row: Dict[str, Any]) -> str:
    return str(row.get("kind") or "")


def _trial_id(row: Dict[str, Any]) -> Optional[int]:
    value = row.get("trial")
    if value is None:
        return None
    try:
        return int(value)
    except (TypeError, ValueError):
        return None


def _first(rows: Sequence[Dict[str, Any]], kind: str) -> Dict[str, Any]:
    for row in rows:
        if _kind(row) == kind:
            return row
    return {}


def _mean_key(rows: Sequence[Dict[str, Any]], key: str) -> Optional[float]:
    return mean_finite([as_float(row.get(key)) for row in rows])


def trials_from_records(records: Sequence[Dict[str, Any]]) -> List[Dict[str, Any]]:
    """Collapse CausalRecorder JSONL to one analysis row per trial."""
    grouped: Dict[int, List[Dict[str, Any]]] = {}
    for row in records:
        tid = _trial_id(row)
        if tid is None:
            continue
        grouped.setdefault(tid, []).append(row)

    trials: List[Dict[str, Any]] = []
    for tid, members in sorted(grouped.items()):
        begin = _first(members, "trial_begin")
        end = _first(members, "trial_end")
        agents = [m for m in members if _kind(m) == "agent"]
        dyads = [m for m in members if _kind(m) == "dyad"]
        blue_agents = [a for a in agents if as_bool01(a.get("blue")) == 1.0]
        blue_dyads = [d for d in dyads if as_bool01(d.get("a_blue")) == 1.0]
        if not blue_dyads:
            blue_dyads = dyads

        red_losses = as_float(end.get("red_losses"))
        outcome = str(end.get("outcome") or "")
        enemy_killed = as_float(end.get("enemy_killed"))
        if enemy_killed is None:
            enemy_killed = 1.0 if (
                (red_losses is not None and red_losses > 0)
                or outcome == "red_destroyed"
            ) else 0.0 if end else None

        blue_shots = as_float(end.get("blue_shots"))
        blue_hits = as_float(end.get("blue_hits"))
        hit_rate = as_float(end.get("blue_hit_rate"))
        if hit_rate is None and blue_shots is not None and blue_shots > 0 and blue_hits is not None:
            hit_rate = blue_hits / blue_shots

        first_shot_t = as_float(end.get("blue_first_shot_t"))
        first_shot = as_float(end.get("first_shot"))
        if first_shot is None:
            first_shot = 1.0 if first_shot_t is not None else (0.0 if end else None)

        elev = as_float(begin.get("blue_elev"))
        if elev is None:
            elev = _mean_key(blue_agents or agents, "elev")
        slope_deg = as_float(begin.get("blue_slope_deg"))
        if slope_deg is None:
            slope_deg = _mean_key(blue_agents or agents, "slope_deg")
        prom = as_float(begin.get("blue_prom"))
        if prom is None:
            prom = _mean_key(blue_agents or agents, "prom")

        height_adv = as_float(begin.get("init_height_adv"))
        if height_adv is None:
            height_adv = _mean_key(blue_dyads, "height_adv")

        terrain_los = _mean_key(blue_dyads, "terrain_los")
        mobility = _mean_key(blue_agents, "disp_rate")
        if mobility is None:
            mobility = _mean_key(blue_agents, "speed")

        trials.append(
            {
                "trial": tid,
                "assignment": begin.get("assignment"),
                "movement": begin.get("movement"),
                "seed": begin.get("seed"),
                "outcome": outcome or None,
                "init_height_adv": height_adv,
                "elev": elev,
                "slope_deg": slope_deg,
                "prom": prom,
                "terrain_los": terrain_los,
                "mobility": mobility,
                "enemy_killed": enemy_killed,
                "first_shot": first_shot,
                "first_shot_t": first_shot_t,
                "hit_rate": hit_rate,
                "blue_shots": blue_shots,
                "blue_hits": blue_hits,
                "red_losses": red_losses,
                "blue_losses": as_float(end.get("blue_losses")),
                "n_agent_rows": len(agents),
                "n_dyad_rows": len(dyads),
            }
        )
    return trials


def _nan_array(trials: Sequence[Dict[str, Any]], key: str) -> np.ndarray:
    return np.array(
        [as_float(t.get(key)) if as_float(t.get(key)) is not None else math.nan for t in trials],
        dtype=np.float64,
    )


def fit_causal_graph(records: Sequence[Dict[str, Any]]) -> Dict[str, Any]:
    """Estimate terrain DAG from CausalRecorder kind-schema logs."""
    trials = trials_from_records(records)
    agents = [r for r in records if _kind(r) == "agent"]
    dyads = [r for r in records if _kind(r) == "dyad"]
    blue_dyads = [d for d in dyads if as_bool01(d.get("a_blue")) == 1.0] or dyads

    elev_a = column(agents, "elev")
    slope_a = column(agents, "slope_deg")
    prom_a = column(agents, "prom")
    mobility_a = column(agents, "disp_rate")
    if not np.isfinite(mobility_a).any():
        mobility_a = column(agents, "speed")

    height_d = column(blue_dyads, "height_adv")
    los_d = np.array(
        [as_bool01(d.get("terrain_los")) if as_bool01(d.get("terrain_los")) is not None else math.nan
         for d in blue_dyads],
        dtype=np.float64,
    )

    elev_t = _nan_array(trials, "elev")
    slope_t = _nan_array(trials, "slope_deg")
    prom_t = _nan_array(trials, "prom")
    height_t = _nan_array(trials, "init_height_adv")
    los_t = _nan_array(trials, "terrain_los")
    mob_t = _nan_array(trials, "mobility")
    killed_t = _nan_array(trials, "enemy_killed")
    first_t = _nan_array(trials, "first_shot")
    hit_t = _nan_array(trials, "hit_rate")
    los_bin = np.array(
        [1.0 if (v is not None and v >= 0.5) else (0.0 if v is not None else math.nan)
         for v in (as_float(t.get("terrain_los")) for t in trials)],
        dtype=np.float64,
    )

    report: Dict[str, Any] = {
        "n_rows": len(records),
        "n_trials": len(trials),
        "n_agent_rows": len(agents),
        "n_dyad_rows": len(dyads),
        "dag": DAG,
        "descriptives": {
            "trials": describe(trials, TRIAL_KEYS),
            "agent": describe(agents, ("elev", "slope_deg", "prom", "disp_rate", "speed")),
            "dyad": describe(blue_dyads, ("height_adv", "dist", "terrain_los")),
        },
        "equations": {
            "slope_from_elev": rename_ols(ols(slope_a, elev_a), ["elev"]),
            "prom_from_elev": rename_ols(ols(prom_a, elev_a), ["elev"]),
            "mobility_from_slope": rename_ols(ols(mobility_a, slope_a), ["slope_deg"]),
            "terrain_los_from_height_adv": rename_ols(
                ols(los_d, height_d),
                ["height_adv"],
            ),
            "trial_slope_from_elev": rename_ols(ols(slope_t, elev_t), ["elev"]),
            "trial_prom_from_elev": rename_ols(ols(prom_t, elev_t), ["elev"]),
            "trial_terrain_los_from_height_adv": rename_ols(
                ols(los_t, height_t),
                ["init_height_adv"],
            ),
            "trial_enemy_killed_from_terrain_los": rename_ols(
                ols(killed_t, los_t),
                ["terrain_los"],
            ),
            "trial_first_shot_from_terrain_los": rename_ols(
                ols(first_t, los_t),
                ["terrain_los"],
            ),
            "trial_hit_rate_from_terrain_los": rename_ols(
                ols(hit_t, los_t),
                ["terrain_los"],
            ),
            "trial_mobility_from_slope": rename_ols(
                ols(mob_t, slope_t),
                ["slope_deg"],
            ),
        },
        "ate": {
            "terrain_los_on_enemy_killed": ate_binary(los_bin, killed_t),
            "terrain_los_on_first_shot": ate_binary(los_bin, first_t),
            "terrain_los_on_hit_rate": ate_binary(los_bin, hit_t),
        },
        "trials": trials,
    }
    return report


def effects_summary(report: Dict[str, Any]) -> List[str]:
    lines: List[str] = []
    slope_eq = report["equations"]["slope_from_elev"]
    lines.append(
        f"elev -> slope_deg: coef={fmt((slope_eq.get('coef') or {}).get('elev'))} "
        f"n={slope_eq.get('n')} r2={fmt(slope_eq.get('r2'))}"
    )
    prom_eq = report["equations"]["prom_from_elev"]
    lines.append(
        f"elev -> prom: coef={fmt((prom_eq.get('coef') or {}).get('elev'))} "
        f"n={prom_eq.get('n')} r2={fmt(prom_eq.get('r2'))}"
    )
    los_eq = report["equations"]["terrain_los_from_height_adv"]
    lines.append(
        f"height_adv -> terrain_los: coef={fmt((los_eq.get('coef') or {}).get('height_adv'))} "
        f"n={los_eq.get('n')} r2={fmt(los_eq.get('r2'))}"
    )
    mob_eq = report["equations"]["mobility_from_slope"]
    lines.append(
        f"slope_deg -> mobility: coef={fmt((mob_eq.get('coef') or {}).get('slope_deg'))} "
        f"n={mob_eq.get('n')} r2={fmt(mob_eq.get('r2'))}"
    )
    ate_k = report["ate"]["terrain_los_on_enemy_killed"]
    lines.append(
        f"terrain_los -> enemy_killed (unadjusted ATE): ate={fmt(ate_k.get('ate'))} "
        f"n_treated={ate_k.get('n_treated')} n_control={ate_k.get('n_control')}"
    )
    ate_s = report["ate"]["terrain_los_on_first_shot"]
    lines.append(
        f"terrain_los -> first_shot (unadjusted ATE): ate={fmt(ate_s.get('ate'))} "
        f"n_treated={ate_s.get('n_treated')} n_control={ate_s.get('n_control')}"
    )
    hit_eq = report["equations"]["trial_hit_rate_from_terrain_los"]
    lines.append(
        f"terrain_los -> hit_rate: coef={fmt((hit_eq.get('coef') or {}).get('terrain_los'))} "
        f"n={hit_eq.get('n')} r2={fmt(hit_eq.get('r2'))}"
    )
    return lines


def _json_ready(value: Any) -> Any:
    if isinstance(value, dict):
        return {str(k): _json_ready(v) for k, v in value.items()}
    if isinstance(value, list):
        return [_json_ready(v) for v in value]
    if isinstance(value, np.generic):
        return value.item()
    if value is None or isinstance(value, (str, int, float, bool)):
        return value
    return str(value)


def write_report(report: Dict[str, Any], out_path: Path) -> None:
    out_path.parent.mkdir(parents=True, exist_ok=True)
    out_path.write_text(json.dumps(_json_ready(report), indent=2) + "\n", encoding="utf-8")


def generate_synthetic_records(n_trials: int = 40, seed: int = 11) -> List[Dict[str, Any]]:
    """Kind-schema rows with known signs for --self-test (not Unity logs)."""
    rng = np.random.default_rng(seed)
    rows: List[Dict[str, Any]] = []
    for trial in range(1, n_trials + 1):
        elev = float(rng.normal(14.0, 5.0))
        slope_deg = abs(0.35 * elev + float(rng.normal(0.0, 2.0)))
        prom = 0.12 * elev + float(rng.normal(0.0, 0.8))
        height_adv = float(rng.normal(0.0, 8.0))
        los_p = 1.0 / (1.0 + math.exp(-(0.28 * height_adv)))
        terrain_los = 1.0 if rng.random() < los_p else 0.0
        mobility = max(0.0, 1.6 - 0.04 * slope_deg + float(rng.normal(0.0, 0.15)))
        kill_p = 0.18 + 0.55 * terrain_los
        enemy_killed = 1.0 if rng.random() < kill_p else 0.0
        first_shot = 1.0 if (terrain_los > 0.5 or rng.random() < 0.25) else 0.0
        hit_rate = float(np.clip(0.15 + 0.45 * terrain_los + rng.normal(0.0, 0.08), 0.0, 1.0))
        blue_shots = int(rng.integers(2, 12))
        blue_hits = int(round(hit_rate * blue_shots))
        red_losses = int(enemy_killed)

        rows.append(
            {
                "kind": "trial_begin",
                "trial": trial,
                "assignment": "randomized",
                "movement": "Hold",
                "seed": seed + trial,
                "blue_n": 4,
                "red_n": 6,
                "init_height_adv": height_adv,
                "blue_elev": elev,
                "red_elev": elev - height_adv,
                "blue_prom": prom,
                "red_prom": prom - 0.5,
                "blue_slope_deg": slope_deg,
                "red_slope_deg": slope_deg + 1.0,
            }
        )
        rows.append(
            {
                "kind": "agent",
                "trial": trial,
                "t": 0.0,
                "s": 1,
                "id": 1000 + trial,
                "blue": True,
                "elev": elev,
                "slope_deg": slope_deg,
                "prom": prom,
                "disp_rate": mobility,
                "speed": mobility,
            }
        )
        rows.append(
            {
                "kind": "dyad",
                "trial": trial,
                "t": 0.0,
                "s": 1,
                "a": 1000 + trial,
                "b": 2000 + trial,
                "a_blue": True,
                "dist": 180.0,
                "height_adv": height_adv,
                "a_elev": elev,
                "b_elev": elev - height_adv,
                "a_prom": prom,
                "b_prom": prom - 0.5,
                "terrain_los": bool(terrain_los >= 0.5),
            }
        )
        rows.append(
            {
                "kind": "trial_end",
                "trial": trial,
                "t": 60.0,
                "outcome": "red_destroyed" if enemy_killed else "stopped",
                "blue_alive": 4,
                "red_alive": 6 - red_losses,
                "blue_losses": 0,
                "red_losses": red_losses,
                "blue_shots": blue_shots,
                "red_shots": 3,
                "blue_hits": blue_hits,
                "red_hits": 1,
                "blue_first_shot_t": 4.0 if first_shot else None,
                "red_first_shot_t": None,
                "enemy_killed": int(enemy_killed),
                "blue_hit_rate": hit_rate,
                "first_shot": int(first_shot),
            }
        )
    return rows


def run_self_test() -> int:
    rows = generate_synthetic_records()
    report = fit_causal_graph(rows)
    print("self-test synthetic n_rows=", report["n_rows"], "trials=", report["n_trials"])
    for line in effects_summary(report):
        print(line)

    hadv_coef = report["equations"]["terrain_los_from_height_adv"]["coef"]["height_adv"]
    los_ate = report["ate"]["terrain_los_on_enemy_killed"]["ate"]
    slope_coef = report["equations"]["slope_from_elev"]["coef"]["elev"]
    failures = []
    if hadv_coef is None or hadv_coef <= 0:
        failures.append(f"expected height_adv -> terrain_los > 0, got {hadv_coef}")
    if los_ate is None or los_ate <= 0:
        failures.append(f"expected terrain_los ATE on enemy_killed > 0, got {los_ate}")
    if slope_coef is None:
        failures.append("expected elev -> slope_deg coefficient")
    if failures:
        print("SELF-TEST FAILED:", file=sys.stderr)
        for item in failures:
            print("  ", item, file=sys.stderr)
        return 1
    print("SELF-TEST PASSED")
    return 0


def main(argv: List[str] | None = None) -> int:
    parser = argparse.ArgumentParser(
        description="Fit v1 terrain SCM from AGAMOTTO_CausalRecorder JSONL."
    )
    parser.add_argument(
        "jsonl",
        nargs="?",
        default=str(DEFAULT_SAMPLE),
        help="Path to causal_*.jsonl (default: bundled sample).",
    )
    parser.add_argument(
        "--out",
        default="",
        help="Write full report JSON (default: <jsonl>.report.json).",
    )
    parser.add_argument(
        "--self-test",
        action="store_true",
        help="Fit synthetic data with known effect signs and exit.",
    )
    args = parser.parse_args(argv)

    if args.self_test:
        return run_self_test()

    path = Path(args.jsonl)
    if not path.is_file():
        print(f"No log file at {path}", file=sys.stderr)
        print(
            "In Unity: Evaluation dispatch, Send Map Data from WinTAK, then F8 "
            "Run randomized trials. Logs land in AGAMOTTO Simulation/CausalLogs/.",
            file=sys.stderr,
        )
        return 2

    records = load_jsonl(path)
    if not records:
        print(f"{path} is empty", file=sys.stderr)
        return 2
    if not any(_kind(r) for r in records):
        print(
            f"{path} has no 'kind' field — this fitter is for CausalRecorder logs "
            "(trial_begin / agent / dyad / shot / death / trial_end).",
            file=sys.stderr,
        )
        return 2

    report = fit_causal_graph(records)
    print(
        f"loaded {report['n_rows']} rows / {report['n_trials']} trials from {path}"
    )
    for line in effects_summary(report):
        print(line)

    out_path = Path(args.out) if args.out else path.with_suffix(path.suffix + ".report.json")
    write_report(report, out_path)
    print(f"wrote {out_path}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
