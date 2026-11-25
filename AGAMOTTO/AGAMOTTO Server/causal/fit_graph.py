#!/usr/bin/env python3
"""Fit the RIDE combat SCM from AGAMOTTO_CausalRecorder JSONL.

Expects kind-typed rows: trial_begin, agent, group, dyad, shot, death, trial_end.

Combat edges follow BasicAttackCalculator (range, occlusion, skill, suppression,
posture, relative speed). Elevation → slope is kept only as a map check.

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
    "elev": ["slope_deg"],
    "dist": ["in_range", "p_hit_sim"],
    "visible": ["first_shot"],
    "in_range": ["first_shot"],
    "skill": ["p_hit_sim"],
    "suppression": ["p_hit_sim"],
    "b_exposure": ["p_hit_sim"],
    "rel_speed": ["p_hit_sim"],
    "p_hit_sim": ["hit"],
    "hit": ["enemy_killed"],
    "force_ratio": ["enemy_killed", "blue_losses"],
    "depth": ["n_in_contact", "blue_losses"],
}

TRIAL_KEYS = (
    "elev",
    "slope_deg",
    "force_ratio",
    "depth",
    "spacing_mean",
    "min_eng_dist",
    "in_range",
    "visible",
    "n_in_contact",
    "first_shot",
    "enemy_killed",
    "blue_losses",
    "hit_rate",
    "p_hit_sim",
    "shot_dist",
    "time_to_kill",
)

SHOT_SAMPLE_LIMIT = 1500


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


def _shot_kept(row: Dict[str, Any]) -> bool:
    if _kind(row) != "shot":
        return False
    if as_bool01(row.get("a_blue")) == 0.0:
        return False
    if as_bool01(row.get("role_ok")) == 0.0:
        return False
    return True


def _min_key(rows: Sequence[Dict[str, Any]], key: str) -> Optional[float]:
    finite = [v for v in (as_float(row.get(key)) for row in rows) if v is not None]
    if not finite:
        return None
    return float(min(finite))


def _peak_in_contact(dyads: Sequence[Dict[str, Any]]) -> Optional[float]:
    """Most Blue shooters in range on any one sample."""
    by_sample: Dict[int, set] = {}
    for row in dyads:
        if as_bool01(row.get("in_range")) != 1.0:
            continue
        sample = row.get("s")
        try:
            sample_id = int(sample)
        except (TypeError, ValueError):
            sample_id = -1
        by_sample.setdefault(sample_id, set()).add(row.get("a"))
    if not by_sample:
        return 0.0
    return float(max(len(agents) for agents in by_sample.values()))


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
        groups = [m for m in members if _kind(m) == "group"]
        dyads = [m for m in members if _kind(m) == "dyad"]
        blue_agents = [a for a in agents if as_bool01(a.get("blue")) == 1.0]
        blue_groups = [g for g in groups if as_bool01(g.get("blue")) == 1.0]
        blue_dyads = [d for d in dyads if as_bool01(d.get("a_blue")) == 1.0]
        if not blue_dyads:
            blue_dyads = dyads
        shots = [m for m in members if _shot_kept(m)]
        red_deaths = [
            m for m in members
            if _kind(m) == "death" and as_bool01(m.get("blue")) == 0.0
        ]

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
            mobility = _mean_key(blue_groups, "disp_rate")
        if mobility is None:
            mobility = _mean_key(blue_agents, "speed")

        blue_n = as_float(begin.get("blue_n"))
        red_n = as_float(begin.get("red_n"))
        force_ratio = blue_n / red_n if blue_n is not None and red_n not in (None, 0.0) else None
        time_to_kill = _min_key(red_deaths, "t") if enemy_killed == 1.0 else None

        trials.append(
            {
                "trial": tid,
                "assignment": begin.get("assignment"),
                "movement": begin.get("movement"),
                "formation": begin.get("formation"),
                "seed": begin.get("seed"),
                "outcome": outcome or None,
                "init_height_adv": height_adv,
                "elev": elev,
                "slope_deg": slope_deg,
                "prom": prom,
                "terrain_los": terrain_los,
                "mobility": mobility,
                "force_ratio": force_ratio,
                "spacing_mean": _mean_key(blue_groups, "spacing_mean"),
                "frontage": _mean_key(blue_groups, "frontage"),
                "depth": _mean_key(blue_groups, "depth"),
                "heading_deg": _mean_key(blue_groups, "heading_deg"),
                "min_eng_dist": _min_key(blue_dyads, "dist"),
                "in_range": _mean_key(blue_dyads, "in_range"),
                "visible": _mean_key(blue_dyads, "visible"),
                "n_in_contact": _peak_in_contact(blue_dyads),
                "enemy_killed": enemy_killed,
                "first_shot": first_shot,
                "first_shot_t": first_shot_t,
                "first_terrain_los_t": as_float(end.get("first_terrain_los_t")),
                "first_in_range_t": as_float(end.get("first_in_range_t")),
                "min_centroid_dist": as_float(end.get("min_centroid_dist")),
                "time_to_kill": time_to_kill,
                "hit_rate": hit_rate,
                "p_hit_sim": _mean_key(shots, "p_hit_sim"),
                "shot_dist": _mean_key(shots, "dist"),
                "shot_height_adv": _mean_key(shots, "height_adv"),
                "skill": _mean_key(shots, "skill"),
                "suppression": _mean_key(shots, "suppression"),
                "b_exposure": _mean_key(shots, "b_exposure"),
                "rel_speed": _mean_key(shots, "rel_speed"),
                "blue_shots": blue_shots,
                "blue_hits": blue_hits,
                "red_losses": red_losses,
                "blue_losses": as_float(end.get("blue_losses")),
                "n_agent_rows": len(agents),
                "n_group_rows": len(groups),
                "n_dyad_rows": len(dyads),
                "n_shot_rows": len(shots),
            }
        )
    return trials


def _nan_array(trials: Sequence[Dict[str, Any]], key: str) -> np.ndarray:
    return np.array(
        [as_float(t.get(key)) if as_float(t.get(key)) is not None else math.nan for t in trials],
        dtype=np.float64,
    )


def _varies(array: np.ndarray) -> bool:
    finite = array[np.isfinite(array)]
    return int(finite.size) >= 2 and float(np.std(finite)) > 1e-8


def _ols_varying(y: np.ndarray, named: Sequence[tuple]) -> Dict[str, Any]:
    """OLS that omits predictors with no spread, so a constant is not aliased onto the intercept."""
    used = [(name, array) for name, array in named if _varies(array)]
    dropped = [name for name, array in named if not _varies(array)]
    if used:
        fitted = rename_ols(ols(y, *[array for _, array in used]), [name for name, _ in used])
    else:
        fitted = rename_ols(ols(y), [])
        fitted["coef"] = {}
    for name in dropped:
        fitted["coef"][name] = None
    fitted["dropped_constant"] = dropped
    return fitted


def _bool_column(rows: Sequence[Dict[str, Any]], key: str) -> np.ndarray:
    return np.array(
        [as_bool01(row.get(key)) if as_bool01(row.get(key)) is not None else math.nan for row in rows],
        dtype=np.float64,
    )


def _shot_sample(shots: Sequence[Dict[str, Any]]) -> List[Dict[str, Any]]:
    keys = (
        "trial", "dist", "height_adv", "visible", "in_range", "hit", "p_hit_sim",
        "skill", "suppression", "b_exposure", "rel_speed", "damage",
    )
    if len(shots) <= SHOT_SAMPLE_LIMIT:
        chosen = shots
    else:
        rng = np.random.default_rng(0)
        index = rng.choice(len(shots), size=SHOT_SAMPLE_LIMIT, replace=False)
        chosen = [shots[int(i)] for i in index]
    return [{key: row.get(key) for key in keys} for row in chosen]


def fit_causal_graph(records: Sequence[Dict[str, Any]]) -> Dict[str, Any]:
    """Estimate the RIDE combat DAG from CausalRecorder kind-schema logs."""
    trials = trials_from_records(records)
    agents = [r for r in records if _kind(r) == "agent"]
    groups = [r for r in records if _kind(r) == "group"]
    dyads = [r for r in records if _kind(r) == "dyad"]
    shots = [r for r in records if _shot_kept(r)]
    blue_groups = [g for g in groups if as_bool01(g.get("blue")) == 1.0] or groups
    blue_dyads = [d for d in dyads if as_bool01(d.get("a_blue")) == 1.0] or dyads

    elev_a = column(agents, "elev")
    slope_a = column(agents, "slope_deg")
    dist_s = column(shots, "dist")
    skill_s = column(shots, "skill")
    supp_s = column(shots, "suppression")
    exp_s = column(shots, "b_exposure")
    rel_s = column(shots, "rel_speed")
    phit_s = column(shots, "p_hit_sim")
    hadv_s = column(shots, "height_adv")
    hit_s = _bool_column(shots, "hit")
    visible_s = _bool_column(shots, "visible")

    elev_t = _nan_array(trials, "elev")
    slope_t = _nan_array(trials, "slope_deg")
    killed_t = _nan_array(trials, "enemy_killed")
    first_t = _nan_array(trials, "first_shot")
    losses_t = _nan_array(trials, "blue_losses")
    depth_t = _nan_array(trials, "depth")
    force_t = _nan_array(trials, "force_ratio")
    contact_t = _nan_array(trials, "n_in_contact")
    range_t = _nan_array(trials, "in_range")
    visible_t = _nan_array(trials, "visible")
    kill_t = _nan_array(trials, "time_to_kill")
    range_bin = np.array(
        [1.0 if (v is not None and v > 0.0) else (0.0 if v is not None else math.nan)
         for v in (as_float(t.get("in_range")) for t in trials)],
        dtype=np.float64,
    )
    visible_bin = np.array(
        [1.0 if (v is not None and v >= 0.5) else (0.0 if v is not None else math.nan)
         for v in (as_float(t.get("visible")) for t in trials)],
        dtype=np.float64,
    )

    report: Dict[str, Any] = {
        "n_rows": len(records),
        "n_trials": len(trials),
        "n_agent_rows": len(agents),
        "n_group_rows": len(groups),
        "n_dyad_rows": len(dyads),
        "n_shot_rows": len(shots),
        "dag": DAG,
        "descriptives": {
            "trials": describe(trials, TRIAL_KEYS),
            "agent": describe(agents, ("elev", "slope_deg", "prom", "disp_rate", "speed")),
            "group": describe(blue_groups, ("spacing_mean", "frontage", "depth", "disp_rate")),
            "dyad": describe(blue_dyads, ("dist", "height_adv", "terrain_los", "visible", "in_range")),
            "shot": describe(
                shots,
                ("dist", "height_adv", "p_hit_sim", "skill", "suppression", "b_exposure", "rel_speed", "damage"),
            ),
        },
        "equations": {
            "slope_from_elev": rename_ols(ols(slope_a, elev_a), ["elev"]),
            "trial_slope_from_elev": rename_ols(ols(slope_t, elev_t), ["elev"]),
            "p_hit_from_ride": _ols_varying(
                phit_s,
                (
                    ("dist", dist_s),
                    ("skill", skill_s),
                    ("suppression", supp_s),
                    ("b_exposure", exp_s),
                    ("rel_speed", rel_s),
                ),
            ),
            "p_hit_from_dist": rename_ols(ols(phit_s, dist_s), ["dist"]),
            "p_hit_from_height": rename_ols(ols(phit_s, hadv_s), ["height_adv"]),
            "hit_from_p_hit": rename_ols(ols(hit_s, phit_s), ["p_hit_sim"]),
            "hit_from_visible": rename_ols(ols(hit_s, visible_s), ["visible"]),
            "trial_first_shot_from_in_range": rename_ols(ols(first_t, range_t), ["in_range"]),
            "trial_killed_from_force": rename_ols(ols(killed_t, force_t), ["force_ratio"]),
            "trial_losses_from_depth": rename_ols(ols(losses_t, depth_t), ["depth"]),
            "trial_losses_from_force": rename_ols(ols(losses_t, force_t), ["force_ratio"]),
            "trial_contact_from_depth": rename_ols(ols(contact_t, depth_t), ["depth"]),
            "trial_time_to_kill_from_depth": rename_ols(ols(kill_t, depth_t), ["depth"]),
            "trial_first_shot_from_visible": rename_ols(ols(first_t, visible_t), ["visible"]),
        },
        "ate": {
            "in_range_on_first_shot": ate_binary(range_bin, first_t),
            "visible_on_first_shot": ate_binary(visible_bin, first_t),
        },
        "shot_sample": _shot_sample(shots),
        "trials": trials,
    }
    return report


def _coef(equation: Dict[str, Any], name: str) -> Any:
    return (equation.get("coef") or {}).get(name)


def effects_summary(report: Dict[str, Any]) -> List[str]:
    equations = report["equations"]
    lines: List[str] = []
    slope_eq = equations["trial_slope_from_elev"]
    lines.append(
        f"map elev -> slope_deg: coef={fmt(_coef(slope_eq, 'elev'))} "
        f"n={slope_eq.get('n')} r2={fmt(slope_eq.get('r2'))}"
    )
    ride = equations["p_hit_from_ride"]
    lines.append(
        f"shot dist -> p_hit_sim (with skill, suppression, exposure, rel_speed): "
        f"coef={fmt(_coef(ride, 'dist'))} n={ride.get('n')} r2={fmt(ride.get('r2'))}"
    )
    dropped = ", ".join(ride.get("dropped_constant") or []) or "none"
    lines.append(
        f"shot b_exposure coef={fmt(_coef(ride, 'b_exposure'))} "
        f"suppression coef={fmt(_coef(ride, 'suppression'))} "
        f"rel_speed coef={fmt(_coef(ride, 'rel_speed'))} "
        f"skill coef={fmt(_coef(ride, 'skill'))} "
        f"(no variation: {dropped})"
    )
    height = equations["p_hit_from_height"]
    lines.append(
        f"shot height_adv -> p_hit_sim (alone): coef={fmt(_coef(height, 'height_adv'))} "
        f"n={height.get('n')} r2={fmt(height.get('r2'))}"
    )
    hit_eq = equations["hit_from_p_hit"]
    lines.append(
        f"p_hit_sim -> hit: coef={fmt(_coef(hit_eq, 'p_hit_sim'))} "
        f"n={hit_eq.get('n')} r2={fmt(hit_eq.get('r2'))}"
    )
    force_eq = equations["trial_killed_from_force"]
    lines.append(
        f"force_ratio -> enemy_killed: coef={fmt(_coef(force_eq, 'force_ratio'))} "
        f"n={force_eq.get('n')} r2={fmt(force_eq.get('r2'))}"
    )
    depth_eq = equations["trial_losses_from_depth"]
    lines.append(
        f"depth -> blue_losses: coef={fmt(_coef(depth_eq, 'depth'))} "
        f"n={depth_eq.get('n')} r2={fmt(depth_eq.get('r2'))}"
    )
    contact_eq = equations["trial_contact_from_depth"]
    lines.append(
        f"depth -> n_in_contact: coef={fmt(_coef(contact_eq, 'depth'))} "
        f"n={contact_eq.get('n')} r2={fmt(contact_eq.get('r2'))}"
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
        elev = float(rng.normal(160.0, 20.0))
        slope_deg = abs(0.28 * (elev - 140.0) + float(rng.normal(0.0, 2.0)))
        prom = 0.02 * elev + float(rng.normal(0.0, 0.8))
        height_adv = float(rng.normal(0.0, 8.0))
        terrain_los = 1.0 if rng.random() < 0.3 else 0.0
        mobility = max(0.0, 1.6 - 0.01 * slope_deg + float(rng.normal(0.0, 0.1)))
        blue_n = int(rng.integers(6, 16))
        red_n = int(rng.integers(4, 8))
        force_ratio = blue_n / red_n
        depth = float(abs(rng.normal(120.0, 60.0)))
        enemy_killed = 1.0 if rng.random() < min(0.95, 0.15 + 0.28 * force_ratio) else 0.0
        first_shot = 1.0 if rng.random() < 0.9 else 0.0
        blue_losses = int(np.clip(round(0.04 * depth + rng.normal(0.0, 1.0)), 0, blue_n))
        in_contact = int(np.clip(round(8.0 - 0.03 * depth + rng.normal(0.0, 0.6)), 0, blue_n))
        shots_this = 3
        hit_flags = []
        for _shot in range(shots_this):
            dist = float(rng.uniform(25.0, 70.0))
            skill = float(rng.uniform(0.2, 1.0))
            suppression = float(rng.uniform(0.0, 1.0))
            exposure = float(rng.choice([1.0, 0.25, 0.1]))
            rel_speed = float(rng.uniform(0.0, 1.0))
            p_hit = float(np.clip(
                0.85 - 0.008 * dist + 0.25 * skill - 0.35 * suppression
                + 0.45 * exposure - 0.15 * rel_speed + rng.normal(0.0, 0.04),
                0.02,
                0.98,
            ))
            hit = 1.0 if rng.random() < p_hit else 0.0
            hit_flags.append(hit)
            rows.append(
                {
                    "kind": "shot",
                    "trial": trial,
                    "t": 10.0 + _shot,
                    "a": 1000 + trial,
                    "b": 2000 + trial,
                    "a_blue": True,
                    "role_ok": True,
                    "dist": dist,
                    "height_adv": height_adv,
                    "visible": True,
                    "in_range": dist < 50.0,
                    "hit": bool(hit),
                    "damage": 0.4 if hit else 0.0,
                    "p_hit_sim": p_hit,
                    "skill": skill,
                    "suppression": suppression,
                    "b_exposure": exposure,
                    "rel_speed": rel_speed,
                }
            )
        blue_hits = int(sum(hit_flags))
        hit_rate = blue_hits / shots_this
        blue_shots = shots_this
        red_losses = int(enemy_killed)

        rows.append(
            {
                "kind": "trial_begin",
                "trial": trial,
                "assignment": "randomized",
                "movement": "Hold",
                "formation": "None",
                "seed": seed + trial,
                "blue_n": blue_n,
                "red_n": red_n,
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
                "kind": "group",
                "trial": trial,
                "t": 0.0,
                "s": 1,
                "unit": "blue",
                "blue": True,
                "n": 4,
                "formation": "None",
                "cx": 0.0,
                "cy": elev,
                "cz": 0.0,
                "elev": elev,
                "slope_deg": slope_deg,
                "spacing_mean": 8.0,
                "spacing_std": 1.0,
                "frontage": 12.0,
                "depth": depth,
                "heading_deg": 0.0,
                "disp_rate": mobility,
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
                "visible": True,
                "in_range": in_contact > 0,
            }
        )
        rows.append(
            {
                "kind": "trial_end",
                "trial": trial,
                "t": 60.0,
                "outcome": "red_destroyed" if enemy_killed else "stopped",
                "blue_alive": blue_n - blue_losses,
                "red_alive": red_n - red_losses,
                "blue_losses": blue_losses,
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
                "first_terrain_los_t": 2.0 if terrain_los >= 0.5 else None,
                "first_in_range_t": 3.0,
                "min_centroid_dist": 180.0,
            }
        )
    return rows


def run_self_test() -> int:
    rows = generate_synthetic_records()
    report = fit_causal_graph(rows)
    print("self-test synthetic n_rows=", report["n_rows"], "trials=", report["n_trials"])
    for line in effects_summary(report):
        print(line)

    ride = report["equations"]["p_hit_from_ride"]
    height = report["equations"]["p_hit_from_height"]
    hit_eq = report["equations"]["hit_from_p_hit"]
    slope_coef = report["equations"]["slope_from_elev"]["coef"]["elev"]
    dist_coef = _coef(ride, "dist")
    exposure_coef = _coef(ride, "b_exposure")
    hit_coef = _coef(hit_eq, "p_hit_sim")
    failures = []
    if slope_coef is None or slope_coef <= 0:
        failures.append(f"expected elev -> slope_deg > 0, got {slope_coef}")
    if dist_coef is None or dist_coef >= 0:
        failures.append(f"expected dist -> p_hit_sim < 0, got {dist_coef}")
    if exposure_coef is None or exposure_coef <= 0:
        failures.append(f"expected b_exposure -> p_hit_sim > 0, got {exposure_coef}")
    if hit_coef is None or hit_coef <= 0:
        failures.append(f"expected p_hit_sim -> hit > 0, got {hit_coef}")
    ride_r2 = ride.get("r2") or 0.0
    height_r2 = height.get("r2") or 0.0
    if ride_r2 <= height_r2:
        failures.append(
            f"expected RIDE hit model r2 ({ride_r2}) above height-only r2 ({height_r2})"
        )
    if failures:
        print("SELF-TEST FAILED:", file=sys.stderr)
        for item in failures:
            print("  ", item, file=sys.stderr)
        return 1
    print("SELF-TEST PASSED")
    return 0


def main(argv: List[str] | None = None) -> int:
    parser = argparse.ArgumentParser(
        description="Fit the RIDE combat SCM from AGAMOTTO_CausalRecorder JSONL."
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
            "(trial_begin / agent / group / dyad / shot / death / trial_end).",
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
