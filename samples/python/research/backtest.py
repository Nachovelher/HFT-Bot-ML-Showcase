"""
PnL en $ con costes (convenios de docs/HIPOTESIS.md).

- interval_pnl: posición mantenida durante intervalos consecutivos (estrategias
  con posición continua). pos[i] = contratos durante el intervalo i, decididos
  al inicio del intervalo; cada contrato que cambia paga medio ida-y-vuelta.
- roundtrip_pnl: operaciones que abren y cierran dentro del intervalo
  (overnight, última media hora): paga el ida-y-vuelta completo si opera.

`ret` es el retorno del intervalo con precios AJUSTADOS y `raw_*` el precio
real del contrato al inicio, para pasar a $ sin el sesgo del ajuste por ratio.
"""
from __future__ import annotations

import pandas as pd


def interval_pnl(pos: pd.Series, ret: pd.Series, raw_start: pd.Series,
                 point_value: float, cost_pts: float) -> pd.Series:
    gross = pos * ret * raw_start * point_value
    traded = pos.diff().abs()
    traded.iloc[0] = abs(pos.iloc[0])
    return gross - traded * (cost_pts / 2.0) * point_value


def roundtrip_pnl(side: pd.Series, ret: pd.Series, raw_entry: pd.Series,
                  point_value: float, cost_pts: float) -> pd.Series:
    gross = side * ret * raw_entry * point_value
    return gross - side.abs() * cost_pts * point_value
