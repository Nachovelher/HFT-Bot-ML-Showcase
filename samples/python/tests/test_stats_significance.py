"""
Tests unitarios para samples/python/stats_significance.py:
  - Serie diaria (daily_pnl) incluyendo días hábiles sin trades a 0
  - Deflated Sharpe Ratio (DSR, Bailey & López de Prado 2014)
  - Bootstrap por bloques semanales (Politis-Romano)
  - PBO CSCV (Bailey et al. 2017)
"""

from __future__ import annotations

import numpy as np
import pandas as pd

from stats_significance import (
    block_bootstrap,
    ci,
    daily_pnl,
    deflated_sharpe,
    pbo_cscv,
    sharpe,
)


def _sample_trades_df() -> pd.DataFrame:
    """Construye un DataFrame de trades dispersos en un periodo de 4 semanas."""
    times = pd.to_datetime([
        "2026-01-05 14:30:00",
        "2026-01-07 15:00:00",
        "2026-01-13 14:40:00",
        "2026-01-20 16:00:00",
        "2026-01-27 15:10:00",
    ])
    df = pd.DataFrame({
        "time": times,
        "ret_atr": [1.5, -1.0, 1.5, 1.5, -1.0],
        "atr": [20.0, 20.0, 20.0, 20.0, 20.0],
        "contracts": [1, 1, 1, 1, 1],
    })
    cost = 0.05
    pv = 2.0
    df["ret_net"] = df["ret_atr"] - cost
    df["date"] = df["time"].dt.normalize()
    df["pnl"] = df["ret_net"] * df["atr"] * pv * df["contracts"]
    df.attrs["period"] = (pd.Timestamp("2026-01-02"), pd.Timestamp("2026-01-31"))
    return df


def test_daily_pnl_includes_zero_trade_business_days():
    df = _sample_trades_df()
    d = daily_pnl(df)
    # Del 2 de enero al 27 de enero de 2026 hay 18 días hábiles, aunque solo hubo 5 días con trades
    assert len(d) == 18
    assert (d == 0.0).sum() == 13
    # El Sharpe incluyendo los días en cero es estrictamente menor que si solo contásemos los 5 días activos
    active_only = df.groupby("date").pnl.sum()
    assert sharpe(d) < sharpe(active_only)


def test_deflated_sharpe_penalizes_multiple_testing():
    rng = np.random.default_rng(123)
    daily = pd.Series(rng.normal(loc=5.0, scale=40.0, size=252))
    dsr_single, sr0_single = deflated_sharpe(daily, n_trials=2, sr_std=0.5)
    dsr_many, sr0_many = deflated_sharpe(daily, n_trials=100, sr_std=0.5)
    assert sr0_many > sr0_single > 0.0
    assert dsr_many < dsr_single


def test_block_bootstrap_preserves_negative_ci_when_all_weeks_lose():
    times = pd.date_range("2026-01-05", periods=20, freq="B")
    df = pd.DataFrame({
        "time": times,
        "ret_atr": np.full(20, -1.0),
        "atr": np.full(20, 15.0),
        "contracts": np.ones(20, dtype=int),
    })
    df["ret_net"] = df["ret_atr"] - 0.05
    df["date"] = df["time"].dt.normalize()
    df["pnl"] = df["ret_net"] * df["atr"] * 2.0
    df.attrs["period"] = (pd.Timestamp("2026-01-05"), pd.Timestamp("2026-02-02"))

    boot = block_bootstrap(df, n_boot=200, seed=7)
    assert boot is not None
    lo, hi = ci(boot["ev_atr"])
    assert lo < 0 and hi < 0


def test_pbo_cscv_detects_overfit_noise_matrix():
    rng = np.random.default_rng(99)
    # 160 días x 6 configuraciones donde solo una tiene deriva positiva consistente en todos los bloques
    m = pd.DataFrame(rng.normal(0.0, 1.0, size=(160, 5)), columns=[f"noise_{i}" for i in range(5)])
    m["real_edge"] = rng.normal(0.8, 1.0, size=160)
    pbo, n_comb = pbo_cscv(m, n_splits=8)
    assert n_comb == 70
    assert pbo < 0.15
