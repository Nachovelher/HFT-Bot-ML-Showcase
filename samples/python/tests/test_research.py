"""Tests del paquete research/ (contabilidad, causalidad y calendario)."""
import os
import sys

import numpy as np
import pandas as pd
import pytest

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

from research.continuous import expiry_key, build_continuous  # noqa: E402
from research.sessions import session_prices  # noqa: E402
from research.backtest import interval_pnl, roundtrip_pnl  # noqa: E402
from research.signals import vol_target_contracts, weekly_hold, tom_mask, ewma_sigma  # noqa: E402
from research.evaluate import sharpe, block_bootstrap_ci  # noqa: E402


# ── continuous ──────────────────────────────────────────────────────────
@pytest.mark.parametrize("sym,date,expected", [
    ("NQZ5", "2015-11-13", (2015, 12)),
    ("NQH6", "2015-11-13", (2016, 3)),
    ("NQH0", "2019-12-20", (2020, 3)),
    ("NQZ5", "2025-10-01", (2025, 12)),
    ("NQZ5-NQH6", "2015-11-13", None),
])
def test_expiry_key_disambiguates_decade(sym, date, expected):
    assert expiry_key(sym, "NQ", pd.Timestamp(date)) == expected


def _bars(symbol, start, n, price, vol, step=0.0):
    ts = pd.date_range(start, periods=n, freq="1min", tz="UTC")
    p = price + step * np.arange(n)
    return pd.DataFrame({"ts_event": ts.strftime("%Y-%m-%dT%H:%M:%S.000000000Z"),
                         "open": p, "high": p + 1, "low": p - 1, "close": p,
                         "volume": vol, "symbol": symbol})


def test_build_continuous_rolls_by_volume_and_ratio_adjusts_to_last_contract():
    d1 = pd.concat([_bars("NQZ5", "2015-12-10 14:00", 60, 100.0, 50),
                    _bars("NQH6", "2015-12-10 14:00", 60, 110.0, 10)])
    d2 = pd.concat([_bars("NQZ5", "2015-12-11 14:00", 60, 100.0, 10),
                    _bars("NQH6", "2015-12-11 14:00", 60, 110.0, 50)])
    out = build_continuous(pd.concat([d1, d2]), "NQ")
    day1 = out[out.ts_event.str.startswith("2015-12-10")]
    day2 = out[out.ts_event.str.startswith("2015-12-11")]
    assert set(day1.contract) == {"NQZ5"} and set(day2.contract) == {"NQH6"}
    # último contrato: ajustado == raw
    assert np.allclose(day2.close, day2.close_raw)
    # contrato viejo escalado por 110/100 → continuidad en el roll
    assert np.allclose(day1.close, day1.close_raw * 1.1)
    assert day2.is_rollover_bar.iloc[0] and not day2.is_rollover_bar.iloc[1:].any()


# ── sessions (hora de Nueva York, con DST) ──────────────────────────────
def _session_frame(et_day, utc_offset_h):
    # barras de 09:30 a 16:10 ET de un día, precio = minuto ET (fácil de verificar)
    et_idx = pd.date_range(f"{et_day} 09:30", f"{et_day} 16:10", freq="1min")
    utc = (et_idx + pd.Timedelta(hours=utc_offset_h)).tz_localize("UTC")
    hm = et_idx.hour * 60 + et_idx.minute
    p = hm.astype(float).values
    return pd.DataFrame({"ts_event": utc.strftime("%Y-%m-%d %H:%M:%S+00:00"),
                         "open": p, "high": p, "low": p, "close": p + 0.5,
                         "open_raw": p, "high_raw": p, "low_raw": p, "close_raw": p + 0.5,
                         "volume": 1})


@pytest.mark.parametrize("day,offset", [("2024-01-10", 5), ("2024-07-10", 4)])
def test_session_prices_pick_new_york_times(day, offset):
    sp = session_prices(_session_frame(day, offset)).iloc[0]
    assert sp.open_0930 == 570            # open de la barra 09:30
    assert sp.close_1000 == 599 + 0.5     # close de la barra 09:59
    assert sp.close_1530 == 929 + 0.5     # close de la barra 15:29
    assert sp.close_1600 == 959 + 0.5     # close de la barra 15:59
    assert sp.exec_1605 == 965 + 0.5      # close de la primera barra >= 16:05


# ── backtest ────────────────────────────────────────────────────────────
def test_interval_pnl_charges_half_roundtrip_per_contract_changed():
    pos = pd.Series([0.0, 2.0, 2.0, -1.0])
    ret = pd.Series([0.01, 0.01, -0.02, 0.01])
    raw = pd.Series([100.0, 100.0, 100.0, 100.0])
    pnl = interval_pnl(pos, ret, raw, point_value=2.0, cost_pts=1.0)
    # coste = |Δpos| × 0.5 pts × 2 $
    expected = [0.0, 2 * 1.0 * 2 - 2 * 0.5 * 2, 2 * -2.0 * 2, -1 * 1.0 * 2 - 3 * 0.5 * 2]
    assert np.allclose(pnl.values, expected)


def test_roundtrip_pnl_charges_full_cost_only_when_trading():
    side = pd.Series([1, -1, 0])
    ret = pd.Series([0.01, 0.01, 0.05])
    raw = pd.Series([100.0, 100.0, 100.0])
    pnl = roundtrip_pnl(side, ret, raw, point_value=2.0, cost_pts=1.0)
    assert np.allclose(pnl.values, [2.0 - 2.0, -2.0 - 2.0, 0.0])


# ── signals ─────────────────────────────────────────────────────────────
def test_vol_target_uses_only_past_information():
    rng = np.random.default_rng(0)
    chg = pd.Series(rng.normal(0, 10, 300))
    base = vol_target_contracts(chg, target=100.0)
    shocked = chg.copy()
    shocked.iloc[200] = 10_000.0          # shock en t=200
    pos = vol_target_contracts(shocked, target=100.0)
    # la posición decidida en t usa σ conocida en t: nada cambia antes del shock
    assert np.allclose(pos.iloc[:200], base.iloc[:200])


def test_ewma_sigma_is_positive_and_causal():
    chg = pd.Series(np.ones(50))
    s = ewma_sigma(chg, com=60)
    assert (s.dropna() > 0).all()


def test_weekly_hold_only_changes_on_last_day_of_week():
    idx = pd.bdate_range("2024-01-01", "2024-01-19")
    target = pd.Series(np.arange(len(idx), dtype=float), index=idx)
    held = weekly_hold(target)
    # viernes 5 y 12 → cambia; el resto mantiene el valor del viernes anterior
    assert held.loc["2024-01-05"] == target.loc["2024-01-05"]
    assert held.loc["2024-01-08"] == target.loc["2024-01-05"]
    assert held.loc["2024-01-12"] == target.loc["2024-01-12"]


def test_tom_mask_holds_days_minus1_to_plus3():
    idx = pd.bdate_range("2024-01-22", "2024-02-09")
    m = tom_mask(idx, before=1, after=3)
    held = list(m[m].index.strftime("%m-%d"))
    assert held == ["01-31", "02-01", "02-02", "02-05"]


# ── evaluate ────────────────────────────────────────────────────────────
def test_sharpe_counts_zero_days():
    pnl = pd.Series([1.0, 0.0, 1.0, 0.0])
    assert sharpe(pnl) == pytest.approx(pnl.mean() / pnl.std(ddof=1) * np.sqrt(252))


def test_block_bootstrap_ci_contains_mean():
    idx = pd.bdate_range("2020-01-01", periods=500)
    pnl = pd.Series(np.random.default_rng(1).normal(1.0, 1.0, 500), index=idx)
    lo, hi = block_bootstrap_ci(pnl, n_boot=300, seed=0)
    assert lo < pnl.mean() < hi
