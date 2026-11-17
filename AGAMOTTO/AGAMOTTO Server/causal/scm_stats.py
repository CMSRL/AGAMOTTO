"""Shared OLS / ATE helpers for AGAMOTTO causal fitters."""

from __future__ import annotations

import json
import math
from pathlib import Path
from typing import Any, Dict, List, Optional, Sequence

import numpy as np


def load_jsonl(path: Path | str) -> List[Dict[str, Any]]:
    """Load JSONL records, skipping blank / comment / malformed-non-object lines."""
    records: List[Dict[str, Any]] = []
    with Path(path).open("r", encoding="utf-8") as handle:
        for line_no, line in enumerate(handle, start=1):
            text = line.strip()
            if not text or text.startswith("#"):
                continue
            try:
                row = json.loads(text)
            except json.JSONDecodeError as exc:
                raise ValueError(f"{path}:{line_no}: invalid JSON ({exc})") from exc
            if isinstance(row, dict):
                records.append(row)
    return records


def as_float(value: Any) -> Optional[float]:
    if value is None or value is False:
        if value is False:
            return 0.0
        return None
    if value is True:
        return 1.0
    try:
        number = float(value)
    except (TypeError, ValueError):
        return None
    if math.isnan(number) or math.isinf(number):
        return None
    return number


def as_bool01(value: Any) -> Optional[float]:
    if isinstance(value, bool):
        return 1.0 if value else 0.0
    number = as_float(value)
    if number is None:
        return None
    return 1.0 if number >= 0.5 else 0.0


def column(rows: Sequence[Dict[str, Any]], key: str) -> np.ndarray:
    return np.array([as_float(row.get(key)) for row in rows], dtype=np.float64)


def finite_mask(*arrays: np.ndarray) -> np.ndarray:
    mask = np.ones(arrays[0].shape[0], dtype=bool)
    for array in arrays:
        mask &= np.isfinite(array)
    return mask


def ols(y: np.ndarray, *predictors: np.ndarray) -> Dict[str, Any]:
    """OLS with intercept. Returns coefficients, n, r2, and residual std."""
    names = [f"x{i}" for i in range(len(predictors))]
    empty = {
        "n": 0,
        "intercept": None,
        "coef": {name: None for name in names},
        "r2": None,
        "resid_std": None,
    }
    if y.size == 0:
        return empty
    mask = finite_mask(y, *predictors)
    y_fit = y[mask]
    n = int(y_fit.shape[0])
    if n < 3:
        empty["n"] = n
        return empty

    columns = [np.ones(n, dtype=np.float64)]
    for predictor in predictors:
        columns.append(predictor[mask])
    design = np.column_stack(columns)
    beta, *_ = np.linalg.lstsq(design, y_fit, rcond=None)
    fitted = design @ beta
    residual = y_fit - fitted
    ss_res = float(np.dot(residual, residual))
    ss_tot = float(np.dot(y_fit - y_fit.mean(), y_fit - y_fit.mean()))
    r2 = 1.0 - ss_res / ss_tot if ss_tot > 1e-12 else 0.0
    dof = max(n - design.shape[1], 1)
    return {
        "n": n,
        "intercept": float(beta[0]),
        "coef": {name: float(beta[i + 1]) for i, name in enumerate(names)},
        "r2": float(r2),
        "resid_std": float(math.sqrt(ss_res / dof)),
    }


def ate_binary(treatment: np.ndarray, outcome: np.ndarray) -> Dict[str, Any]:
    """Unadjusted ATE for a 0/1 treatment: E[Y|T=1] - E[Y|T=0]."""
    if treatment.size == 0:
        return {
            "n_treated": 0,
            "n_control": 0,
            "ate": None,
            "mean_treated": None,
            "mean_control": None,
        }
    mask = finite_mask(treatment, outcome)
    t = treatment[mask]
    y = outcome[mask]
    treated = t >= 0.5
    n1 = int(treated.sum())
    n0 = int((~treated).sum())
    if n1 == 0 or n0 == 0:
        return {
            "n_treated": n1,
            "n_control": n0,
            "ate": None,
            "mean_treated": None,
            "mean_control": None,
        }
    mean_t = float(y[treated].mean())
    mean_c = float(y[~treated].mean())
    return {
        "n_treated": n1,
        "n_control": n0,
        "mean_treated": mean_t,
        "mean_control": mean_c,
        "ate": mean_t - mean_c,
    }


def rename_ols(result: Dict[str, Any], names: Sequence[str]) -> Dict[str, Any]:
    renamed = dict(result)
    coef = result.get("coef") or {}
    renamed["coef"] = {names[i]: coef.get(f"x{i}") for i in range(len(names))}
    return renamed


def describe(rows: Sequence[Dict[str, Any]], keys: Sequence[str]) -> Dict[str, Any]:
    summary: Dict[str, Any] = {}
    for key in keys:
        values = np.array(
            [v for v in (as_float(row.get(key)) for row in rows) if v is not None],
            dtype=np.float64,
        )
        if values.size == 0:
            summary[key] = {"n": 0}
            continue
        summary[key] = {
            "n": int(values.size),
            "mean": float(values.mean()),
            "std": float(values.std(ddof=1)) if values.size > 1 else 0.0,
            "min": float(values.min()),
            "max": float(values.max()),
            "nan_count": int(len(rows) - values.size),
        }
    return summary


def mean_finite(values: Sequence[Optional[float]]) -> Optional[float]:
    finite = [v for v in values if v is not None and math.isfinite(v)]
    if not finite:
        return None
    return float(sum(finite) / len(finite))


def fmt(value: Any) -> str:
    if value is None:
        return "NA"
    try:
        return f"{float(value):.4f}"
    except (TypeError, ValueError):
        return str(value)
