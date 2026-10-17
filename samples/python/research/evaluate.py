"""
Métricas y criterios de aprobado (docs/HIPOTESIS.md, sección de criterios).
Todas trabajan sobre PnL diario en $ indexado por fecha, incluidos los días a 0.
"""
from __future__ import annotations

import os
import sys
from typing import Callable

import numpy as np
import pandas as pd

sys.path.insert(0, os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "tools"))
from stats_significance import deflated_sharpe  # noqa: E402

TRADING_DAYS = 252


def sharpe(pnl: pd.Series) -> float:
    sd = pnl.std(ddof=1)
    return float(pnl.mean() / sd * np.sqrt(TRADING_DAYS)) if sd > 0 else float("nan")


def block_bootstrap_ci(pnl: pd.Series, n_boot: int = 2000, seed: int = 42,
                       level: float = 0.95) -> tuple[float, float]:
    """IC del PnL medio diario remuestreando SEMANAS completas."""
    weeks = [g.values for _, g in pnl.groupby(pnl.index.to_period("W"))]
    rng = np.random.default_rng(seed)
    means = np.empty(n_boot)
    for b in range(n_boot):
        pick = rng.integers(0, len(weeks), len(weeks))
        means[b] = np.concatenate([weeks[i] for i in pick]).mean()
    a = (1.0 - level) / 2.0
    return float(np.quantile(means, a)), float(np.quantile(means, 1.0 - a))


def halves(pnl: pd.Series, split: str) -> tuple[float, float]:
    cut = pd.Timestamp(split)
    return float(pnl[pnl.index < cut].sum()), float(pnl[pnl.index >= cut].sum())


def placebo_p95(make_pnl: Callable[[np.random.Generator], pd.Series], n: int = 200,
                seed: int = 7) -> float:
    rng = np.random.default_rng(seed)
    return float(np.quantile([sharpe(make_pnl(rng)) for _ in range(n)], 0.95))


def dsr(pnl: pd.Series, n_trials: int, sr_std: float) -> float:
    value, _ = deflated_sharpe(pnl, n_trials, sr_std)
    return float(value)


def assess(pnl: pd.Series, pnl_cost2x: pd.Series, split: str, min_sharpe: float = 0.4,
           placebo: float | None = None, benchmark_sharpe: float | None = None,
           min_diff: float = 0.15) -> dict:
    """Aplica los criterios pre-registrados y devuelve métricas + veredicto."""
    sr = sharpe(pnl)
    lo, hi = block_bootstrap_ci(pnl)
    h1, h2 = halves(pnl, split)
    sr2 = sharpe(pnl_cost2x)
    checks = {
        "c1_sharpe": sr >= min_sharpe,
        "c2_ci": lo > 0,
        "c3_halves": h1 > 0 and h2 > 0,
        "c4_cost2x": sr2 > 0,
    }
    if placebo is not None:
        checks["c5_placebo"] = sr > placebo
    if benchmark_sharpe is not None:
        checks["c6_vs_bh"] = sr - benchmark_sharpe >= min_diff
    return {"sharpe": sr, "ci_lo": lo, "ci_hi": hi, "half1": h1, "half2": h2,
            "sharpe_cost2x": sr2, "placebo_p95": placebo, "active_days": int((pnl != 0).sum()),
            "pnl_total": float(pnl.sum()), **checks, "aprobado": all(checks.values())}
