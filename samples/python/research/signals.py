"""
Señales y dimensionamiento de las hipótesis (docs/HIPOTESIS.md).

Todas las funciones son causales: el valor en t usa solo información hasta t
(el cierre de t). La posición decidida en t se aplica al intervalo siguiente.
"""
from __future__ import annotations

import numpy as np
import pandas as pd


def ewma_sigma(dollar_chg: pd.Series, com: float = 60.0, min_periods: int = 20) -> pd.Series:
    """Desviación EWMA (centro de masa `com`) de los cambios diarios en $."""
    return np.sqrt((dollar_chg ** 2).ewm(com=com, adjust=False, min_periods=min_periods).mean())


def vol_target_contracts(dollar_chg: pd.Series, target: float, com: float = 60.0) -> pd.Series:
    """Contratos para un riesgo diario objetivo en $ (0 hasta tener σ)."""
    contracts = target / ewma_sigma(dollar_chg, com)
    return contracts.replace([np.inf, -np.inf], np.nan).fillna(0.0)


def _week_key(index: pd.DatetimeIndex) -> pd.Series:
    iso = index.isocalendar()
    return pd.Series((iso.year * 100 + iso.week).values, index=index)


def week_end_mask(index: pd.DatetimeIndex) -> pd.Series:
    """True en el último día hábil de cada semana ISO presente en el índice."""
    key = _week_key(index)
    # isocalendar devuelve enteros nullable: la última comparación es NA → fin de periodo
    return (key != key.shift(-1)).fillna(True).astype(bool)


def month_end_mask(index: pd.DatetimeIndex) -> pd.Series:
    """True en el último día hábil de cada mes presente en el índice."""
    month = pd.Series(index.year * 100 + index.month, index=index)
    return month != month.shift(-1)


def weekly_hold(target: pd.Series) -> pd.Series:
    """Rebalanceo semanal: toma el objetivo el último día hábil de la semana
    y lo mantiene hasta el siguiente (0 antes del primer rebalanceo)."""
    return target.where(week_end_mask(target.index)).ffill().fillna(0.0)


def tsmom_signal(close: pd.Series, lookbacks: tuple[int, ...] = (21, 63, 252)) -> pd.Series:
    """Media de sign(retorno a L días) para cada L; 0 hasta tener el mayor L."""
    signs = [np.sign(close / close.shift(lb) - 1.0) for lb in lookbacks]
    return pd.concat(signs, axis=1).mean(axis=1, skipna=False).fillna(0.0)


def inverse_vol_position(ret: pd.Series, window: int, power: float, rebalance: pd.Series,
                         cap: float = 2.0) -> pd.Series:
    """Exposición larga ∝ 1/σ^power (σ de `window` retornos), revisada en
    `rebalance`; normalizada por la media EXPANDIDA de los pesos pasados
    (causal) y con tope `cap`."""
    raw_w = 1.0 / ret.rolling(window).std() ** power
    w_reb = raw_w.where(rebalance)
    norm = w_reb.expanding().mean()
    return (w_reb / norm).clip(upper=cap).ffill().fillna(0.0)


def tom_mask(index: pd.DatetimeIndex, before: int, after: int) -> pd.Series:
    """True en los últimos `before` días hábiles del mes y los primeros
    `after` del siguiente. Solo cuenta bordes de mes visibles en el índice
    (un mes cortado al principio/final de la muestra no se marca)."""
    month = pd.Series(index.year * 100 + index.month, index=index)
    starts = month != month.shift(1)
    ends = month != month.shift(-1)
    starts.iloc[0] = False
    ends.iloc[-1] = False
    seg = starts.cumsum()
    seg_start_ok = starts.groupby(seg).transform("first")
    seg_end_ok = ends.groupby(seg).transform("last")
    pos_from_start = month.groupby(seg).cumcount() + 1
    pos_from_end = month.groupby(seg).cumcount(ascending=False) + 1
    held = (seg_start_ok & (pos_from_start <= after)) | (seg_end_ok & (pos_from_end <= before))
    return held
