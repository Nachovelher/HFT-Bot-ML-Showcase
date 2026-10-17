"""
Continuo front-month (multidécada) desde un CSV *parent* de Databento.

- Front por volumen diario (UTC): se pasa a un vencimiento posterior cuando
  supera en volumen al front actual (o si el front deja de cotizar).
- Ajuste por RATIO anclado en el ÚLTIMO contrato (ajustado == raw al final):
  en cada roll k_nuevo = k_viejo × close_viejo / close_nuevo en el último
  minuto común del día anterior; al final se normaliza k_último = 1.
- Los símbolos repiten dígito de año (NQZ5 = dic-2015 y dic-2025): el año de
  vencimiento se resuelve con la fecha de la barra (expiry_key).

Salida con el formato de los continuos existentes (data/*_continuous_*.csv).

Uso:
  python -m research.continuous --databento_csv <parent.csv> --root NQ \
      --out data/NQ_continuous_ohlcv1m_2015-2026.csv
"""
from __future__ import annotations

import argparse
import re

import numpy as np
import pandas as pd

MONTHS = {m: i for i, m in enumerate("FGHJKMNQUVXZ", 1)}


def expiry_key(symbol: str, root: str, date: pd.Timestamp) -> tuple[int, int] | None:
    """(año, mes) de vencimiento de un outright; None para spreads u otros roots."""
    m = re.fullmatch(rf"{re.escape(root)}([FGHJKMNQUVXZ])(\d)", symbol)
    if not m:
        return None
    digit = int(m.group(2))
    year = date.year + (digit - date.year % 10) % 10
    return year, MONTHS[m.group(1)]


def _select_fronts(df: pd.DataFrame, root: str) -> dict[pd.Timestamp, str]:
    vol = df.groupby(["date", "symbol"]).volume.sum()
    fronts: dict[pd.Timestamp, str] = {}
    cur_sym, cur_key = None, None
    for date, day in vol.groupby(level="date"):
        day = day.droplevel("date")
        best = day.idxmax()
        best_key = expiry_key(best, root, date)
        if cur_sym is None or cur_sym not in day.index:
            cur_sym, cur_key = best, best_key
        elif best_key > cur_key and day[best] > day[cur_sym]:
            cur_sym, cur_key = best, best_key
        fronts[date] = cur_sym
    return fronts


def _roll_ratio(df: pd.DataFrame, prev_date: pd.Timestamp, old: str, new: str,
                new_date: pd.Timestamp) -> float:
    """close_old / close_new en el último minuto común de prev_date."""
    both = df[(df.date == prev_date) & df.symbol.isin([old, new])]
    piv = both.pivot_table(index="ts", columns="symbol", values="close").dropna()
    if len(piv):
        last = piv.iloc[-1]
        return float(last[old] / last[new])
    old_last = df[(df.date == prev_date) & (df.symbol == old)].close.iloc[-1]
    new_first = df[(df.date == new_date) & (df.symbol == new)].close.iloc[0]
    return float(old_last / new_first)


def build_continuous(raw: pd.DataFrame, root: str) -> pd.DataFrame:
    pat = rf"^{re.escape(root)}[FGHJKMNQUVXZ]\d$"
    df = raw[raw.symbol.str.match(pat)].copy()
    df["ts"] = pd.to_datetime(df.ts_event, utc=True)
    df["date"] = df.ts.dt.tz_localize(None).dt.normalize()
    df = df.sort_values(["ts", "symbol"])

    fronts = _select_fronts(df, root)
    dates = sorted(fronts)
    k = {dates[0]: 1.0}
    rolled_dates = set()
    for prev, cur in zip(dates[:-1], dates[1:]):
        if fronts[cur] != fronts[prev]:
            k[cur] = k[prev] * _roll_ratio(df, prev, fronts[prev], fronts[cur], cur)
            rolled_dates.add(cur)
        else:
            k[cur] = k[prev]
    k_last = k[dates[-1]]

    front = df.date.map(fronts)
    out = df[df.symbol == front].copy()
    kk = out.date.map(k).astype(float) / k_last
    res = pd.DataFrame({
        "ts_event": out.ts.dt.strftime("%Y-%m-%d %H:%M:%S+00:00"),
        "contract": out.symbol,
        "open_raw": out.open, "high_raw": out.high, "low_raw": out.low, "close_raw": out.close,
        "volume": out.volume.astype(np.int64),
        "open": out.open * kk, "high": out.high * kk, "low": out.low * kk, "close": out.close * kk,
    })
    first_of_day = ~out.date.duplicated()
    res["is_rollover_bar"] = (first_of_day & out.date.isin(rolled_dates)).values
    return res.reset_index(drop=True)


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--databento_csv", required=True)
    ap.add_argument("--root", required=True)
    ap.add_argument("--out", required=True)
    a = ap.parse_args()
    raw = pd.read_csv(a.databento_csv,
                      usecols=["ts_event", "open", "high", "low", "close", "volume", "symbol"])
    res = build_continuous(raw, a.root)
    res.to_csv(a.out, index=False)
    rolls = res[res.is_rollover_bar]
    print(f"[OK] {len(res):,} barras, {len(rolls)} rolls, "
          f"{res.ts_event.iloc[0]} -> {res.ts_event.iloc[-1]} -> {a.out}")


if __name__ == "__main__":
    main()
