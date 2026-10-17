"""
Precios a horas fijas de Nueva York por sesión (convenios de docs/HIPOTESIS.md).

`ts_event` = INICIO de la barra de 1m (Databento). Precio "a las HH:MM" =
cierre de la barra que empieza en HH:MM−1; la apertura de la sesión de
Nueva York (09:30) = open de la barra 09:30.
"""
from __future__ import annotations

import pandas as pd

NY = "America/New_York"
M_0930, M_0959, M_1529, M_1559, M_1605, M_1700 = 570, 599, 929, 959, 965, 1020


def _first(frame: pd.DataFrame, col: str) -> pd.Series:
    return frame.groupby("date")[col].first()


def _last(frame: pd.DataFrame, col: str) -> pd.Series:
    return frame.groupby("date")[col].last()


def session_prices(cont: pd.DataFrame) -> pd.DataFrame:
    """Una fila por fecha (Nueva York) con los precios de las hipótesis.
    Columnas sin sufijo = ajustadas (para retornos); `_raw` = contrato real ($)."""
    ts = pd.to_datetime(cont.ts_event, utc=True).dt.tz_convert(NY)
    base = pd.DataFrame({
        "date": ts.dt.tz_localize(None).dt.normalize().values,
        "hm": (ts.dt.hour * 60 + ts.dt.minute).values,
        "open": cont.open.values, "close": cont.close.values,
        "open_raw": cont.open_raw.values, "close_raw": cont.close_raw.values,
    })
    out = {}
    for name, minute, col in (("open_0930", M_0930, "open"), ("close_1000", M_0959, "close"),
                              ("close_1530", M_1529, "close"), ("close_1600", M_1559, "close")):
        at = base[base.hm == minute]
        out[name] = _last(at, col)
        out[name + "_raw"] = _last(at, col + "_raw")
    after = base[(base.hm >= M_1605) & (base.hm < M_1700)]
    out["exec_1605"] = _first(after, "close")
    out["exec_1605_raw"] = _first(after, "close_raw")
    # cierre diario = última barra de la sesión de NY (cubre cierres anticipados)
    rth = base[(base.hm >= M_0930) & (base.hm <= M_1559)]
    out["daily_close"] = _last(rth, "close")
    out["daily_close_raw"] = _last(rth, "close_raw")
    return pd.DataFrame(out).sort_index()
