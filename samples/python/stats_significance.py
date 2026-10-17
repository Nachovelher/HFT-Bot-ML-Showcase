# -*- coding: utf-8 -*-
"""
═══════════════════════════════════════════════════════════════════════
stats_significance.py — ¿la EV de una pata se distingue de la suerte?
═══════════════════════════════════════════════════════════════════════
Post-proceso puro sobre los `trades_*.csv` que exporta backtest_ensemble
(--out_csv). NO reentrena, NO toca modelos, NO simula barreras: sólo
re-agrega los trades ya simulados.

Qué calcula, por pata y periodo:
  1. Métricas base: n, WR, EV neta en ATR, PF, PnL $, EV $/trade,
     Sharpe diario anualizado, max drawdown en $.
  2. Bootstrap por BLOQUES (semanas) → IC 95% de EV_ATR, EV_$ y Sharpe.
     Bloques semanales para preservar la autocorrelación intra-semana;
     el muestreo por trade individual daría ICs demasiado estrechos.
  3. Barrido de coste → coste de breakeven (cost_atr al que EV=0).
  4. Deflated Sharpe Ratio (Bailey & López de Prado 2014): corrige el
     Sharpe por el nº de configuraciones probadas, la asimetría y las
     colas. DSR < 0.95 = no distinguible de la mejor de N pruebas.
  5. PBO por CSCV (López de Prado 2015), si se pasan ≥2 patas del mismo
     periodo: probabilidad de que la mejor config in-sample quede por
     debajo de la mediana out-of-sample.

IMPORTANTE sobre el Sharpe: se calcula sobre PnL en $ por día con
sizing por contratos (sin capitalización) → es un Sharpe de PnL, no de
retorno sobre capital. Comparable entre patas, no con un índice.

Uso:
  python tools/stats_significance.py \
      --leg "MNQ10_2025:output/models_10m_MNQCUT24/trades_gate.csv:2025-01-01:2026-01-01:0.053:2" \
      --leg "MNQ10_2026:output/val2026_10m_MNQ/trades_gate.csv:2026-01-01:2027-01-01:0.053:2" \
      --n_trials 40 [--pbo] [--n_boot 5000] [--out_csv informe.csv]

Formato de --leg (campos separados por ':'):
  label:ruta_csv:fecha_ini:fecha_fin:cost_atr:point_value[:skipeu]
  El sufijo opcional 'skipeu' aplica el filtro de sesión (sin entradas
  07:00-13:30 UTC) igual que el preset MGC10_Session_VAL2026.
═══════════════════════════════════════════════════════════════════════
"""

import argparse
import itertools
import sys

import numpy as np
import pandas as pd
from scipy import stats

try:
    sys.stdout.reconfigure(errors="replace")
    sys.stderr.reconfigure(errors="replace")
except AttributeError:
    pass

TRADING_DAYS = 252
SESSION_EU_START, SESSION_EU_END = 7.0, 13.5   # UTC, igual que SessionFilter_SkipEU


# ─────────────────────────────────────────────────────────────────────
# CARGA
# ─────────────────────────────────────────────────────────────────────
def load_leg(spec):
    """Parsea 'label:csv:ini:fin:cost:pv[:skipeu]' y devuelve (label, df, cost, pv)."""
    parts = spec.split(":")
    if len(parts) < 6:
        raise ValueError("--leg mal formado (faltan campos): %s" % spec)
    label, path, d0, d1, cost, pv = parts[:6]
    skip_eu = len(parts) > 6 and parts[6].lower() == "skipeu"

    df = pd.read_csv(path, parse_dates=["time"])
    df = df[(df.time >= d0) & (df.time < d1)].copy()
    if skip_eu:
        h = df.time.dt.hour + df.time.dt.minute / 60.0
        df = df[~((h >= SESSION_EU_START) & (h < SESSION_EU_END))].copy()

    cost, pv = float(cost), float(pv)
    # ret_net_atr del CSV lleva el coste con el que se corrió el backtest;
    # lo recalculamos para que el barrido de coste sea coherente.
    df["ret_net"] = df.ret_atr - cost
    df["date"] = df.time.dt.normalize()
    # PnL recalculado desde ret_net para que respete el coste de este run
    # (el pnl_usd del CSV lleva incrustado el coste del backtest original).
    df["pnl"] = df.ret_net * df.atr * pv * df.contracts
    df.attrs["period"] = (pd.Timestamp(d0), pd.Timestamp(d1))
    return label, df, cost, pv


# ─────────────────────────────────────────────────────────────────────
# MÉTRICAS
# ─────────────────────────────────────────────────────────────────────
def daily_pnl(df):
    """PnL $ por día INCLUYENDO los días hábiles sin trades (PnL 0).

    [FIX 2026-10-06] Antes solo se contaban los días con trades: con
    ~0.5-1 trade/día eso excluye muchos ceros, sobreestima el PnL medio
    diario y el Sharpe anualizado con sqrt(252) (y por tanto el DSR).
    Rango: del inicio del periodo al último dato (no hasta el fin nominal,
    que en 2026 H1 añadiría meses sin datos)."""
    d = df.groupby("date").pnl.sum()
    period = df.attrs.get("period")
    if period is None or d.empty:
        return d
    start = period[0].normalize()
    end = min(period[1] - pd.Timedelta(days=1), d.index.max())
    idx = pd.bdate_range(start, end).union(d.index)
    return d.reindex(idx, fill_value=0.0)


def sharpe(daily):
    if len(daily) < 2 or daily.std(ddof=1) == 0:
        return np.nan
    return daily.mean() / daily.std(ddof=1) * np.sqrt(TRADING_DAYS)


def max_drawdown(daily):
    eq = daily.cumsum()
    return float((eq - eq.cummax()).min()) if len(eq) else 0.0


def profit_factor(x):
    pos, neg = x[x > 0].sum(), -x[x < 0].sum()
    return float(pos / neg) if neg > 0 else np.inf


def base_metrics(df):
    d = daily_pnl(df)
    return {
        "n": len(df),
        "wr": float((df.ret_atr > 0).mean()) if len(df) else np.nan,
        "ev_atr": float(df.ret_net.mean()) if len(df) else np.nan,
        "pf": profit_factor(df.ret_net.values) if len(df) else np.nan,
        "pnl_usd": float(df.pnl.sum()),
        "ev_usd": float(df.pnl.mean()) if len(df) else np.nan,
        "sharpe": sharpe(d),
        "maxdd_usd": max_drawdown(d),
        "dias": int(d.shape[0]),
        "trades_dia": len(df) / max(d.shape[0], 1),
    }


# ─────────────────────────────────────────────────────────────────────
# BOOTSTRAP POR BLOQUES (semanas)
# ─────────────────────────────────────────────────────────────────────
def block_bootstrap(df, n_boot, seed=42, daily=None):
    """Remuestrea SEMANAS completas con reemplazo (bloques = semanas ISO).

    Cada semana sorteada aporta sus trades y sus días como observaciones
    NUEVAS (no se colapsan días repetidos), que es lo que hace del
    bootstrap por bloques un estimador honesto con autocorrelación.
    """
    rng = np.random.default_rng(seed)
    df = df.copy()
    df["week"] = df.time.dt.to_period("W")

    # Pre-agregado por semana en arrays numpy: el bucle de 5.000 reps
    # con concat de DataFrames sería inviable.
    if daily is None:
        daily = daily_pnl(df)
    daily_by_week = {w: g.values for w, g in daily.groupby(daily.index.to_period("W"))}
    weeks, w_ret, w_pnl, w_daily = [], [], [], []
    for w, g in df.groupby("week"):
        weeks.append(w)
        w_ret.append(g.ret_net.values)
        w_pnl.append(g.pnl.values)
        w_daily.append(daily_by_week.get(w, g.groupby("date").pnl.sum().values))
    if len(weeks) < 2:
        return None
    idx = np.arange(len(weeks))

    out = {"ev_atr": [], "ev_usd": [], "sharpe": []}
    for _ in range(n_boot):
        pick = rng.choice(idx, size=len(idx), replace=True)
        ret = np.concatenate([w_ret[i] for i in pick])
        pnl = np.concatenate([w_pnl[i] for i in pick])
        day = np.concatenate([w_daily[i] for i in pick])
        out["ev_atr"].append(ret.mean())
        out["ev_usd"].append(pnl.mean())
        sd = day.std(ddof=1) if day.size > 1 else 0.0
        out["sharpe"].append(day.mean() / sd * np.sqrt(TRADING_DAYS) if sd > 0 else np.nan)
    return {k: np.array(v, dtype=float) for k, v in out.items()}


def ci(arr, lo=2.5, hi=97.5):
    arr = arr[~np.isnan(arr)]
    if arr.size == 0:
        return (np.nan, np.nan)
    return float(np.percentile(arr, lo)), float(np.percentile(arr, hi))


# ─────────────────────────────────────────────────────────────────────
# DEFLATED SHARPE RATIO (Bailey & López de Prado 2014)
# ─────────────────────────────────────────────────────────────────────
def expected_max_sharpe(n_trials, sr_std):
    """E[max SR] de n_trials pruebas independientes con SR~N(0, sr_std²)."""
    if n_trials < 2 or sr_std <= 0:
        return 0.0
    gamma = 0.5772156649
    e = (1 - gamma) * stats.norm.ppf(1 - 1.0 / n_trials) + \
        gamma * stats.norm.ppf(1 - 1.0 / (n_trials * np.e))
    return sr_std * e


def deflated_sharpe(daily, n_trials, sr_std):
    """DSR: probabilidad de que el Sharpe observado supere al esperado
    por azar tras n_trials pruebas, corrigiendo asimetría y curtosis."""
    r = daily.values
    n = len(r)
    if n < 10 or r.std(ddof=1) == 0:
        return np.nan, np.nan
    sr = r.mean() / r.std(ddof=1)                     # Sharpe por periodo (día)
    sk = float(stats.skew(r))
    ku = float(stats.kurtosis(r, fisher=False))
    sr0 = expected_max_sharpe(n_trials, sr_std) / np.sqrt(TRADING_DAYS)
    denom = np.sqrt(1 - sk * sr + (ku - 1) / 4.0 * sr ** 2)
    if not np.isfinite(denom) or denom <= 0:
        return np.nan, sr0 * np.sqrt(TRADING_DAYS)
    z = (sr - sr0) * np.sqrt(n - 1) / denom
    return float(stats.norm.cdf(z)), float(sr0 * np.sqrt(TRADING_DAYS))


# ─────────────────────────────────────────────────────────────────────
# PBO por CSCV (López de Prado 2015)
# ─────────────────────────────────────────────────────────────────────
def pbo_cscv(matrix, n_splits=8):
    """matrix: DataFrame días × configs con PnL $ diario.
    Devuelve (PBO, nº de combinaciones evaluadas)."""
    m = matrix.dropna(how="all").fillna(0.0)
    if m.shape[1] < 2:
        return np.nan, 0
    blocks = np.array_split(np.arange(len(m)), n_splits)
    half = n_splits // 2
    logits = []
    for combo in itertools.combinations(range(n_splits), half):
        is_idx = np.concatenate([blocks[b] for b in combo])
        oos_idx = np.concatenate([blocks[b] for b in range(n_splits) if b not in combo])
        is_sr = m.iloc[is_idx].apply(lambda c: sharpe(c) if (c != 0).sum() > 2 else np.nan)
        oos_sr = m.iloc[oos_idx].apply(lambda c: sharpe(c) if (c != 0).sum() > 2 else np.nan)
        if is_sr.isna().all() or oos_sr.isna().all():
            continue
        best = is_sr.idxmax()
        ranks = oos_sr.rank(pct=True)          # 1.0 = mejor
        w = ranks.get(best, np.nan)
        if np.isnan(w):
            continue
        w = min(max(w, 1e-6), 1 - 1e-6)
        logits.append(np.log(w / (1 - w)))
    if not logits:
        return np.nan, 0
    logits = np.array(logits)
    return float((logits <= 0).mean()), len(logits)


# ─────────────────────────────────────────────────────────────────────
# INFORME
# ─────────────────────────────────────────────────────────────────────
def cost_sweep(df, cost_base, grid_mult=(0, 0.5, 1.0, 1.5, 2.0, 3.0)):
    gross = df.ret_atr.mean()
    rows = []
    for k in grid_mult:
        c = cost_base * k
        rows.append((c, gross - c))
    return gross, rows


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--leg", action="append", required=True,
                    help="label:csv:ini:fin:cost_atr:point_value[:skipeu]")
    ap.add_argument("--n_boot", type=int, default=5000)
    ap.add_argument("--n_trials", type=int, default=40,
                    help="nº de configuraciones probadas históricamente (para el DSR)")
    ap.add_argument("--sr_std", type=float, default=None,
                    help="desviación de los Sharpe probados; por defecto se estima de las patas dadas")
    ap.add_argument("--pbo", action="store_true", help="calcula PBO/CSCV entre las patas dadas")
    ap.add_argument("--pbo_splits", type=int, default=8)
    ap.add_argument("--out_csv", default=None)
    ap.add_argument("--seed", type=int, default=42)
    args = ap.parse_args()

    legs = [load_leg(s) for s in args.leg]
    results, daily_map = [], {}

    for label, df, cost, pv in legs:
        if df.empty:
            print("[WARN] %s: 0 trades en el periodo" % label)
            continue
        m = base_metrics(df)
        m["label"] = label
        m["cost_atr"] = cost
        daily_map[label] = daily_pnl(df)
        results.append((label, df, cost, m))

    sr_std = args.sr_std
    if sr_std is None:
        srs = [m["sharpe"] for _, _, _, m in results if np.isfinite(m["sharpe"])]
        sr_std = float(np.std(srs, ddof=1)) if len(srs) > 1 else 0.5

    print("=" * 78)
    print("  SIGNIFICANCIA — %d patas | bootstrap %d reps (bloques semanales)"
          % (len(results), args.n_boot))
    print("  DSR con n_trials=%d, sr_std=%.3f" % (args.n_trials, sr_std))
    print("=" * 78)

    rows_out = []
    for label, df, cost, m in results:
        d = daily_map[label]
        boot = block_bootstrap(df, args.n_boot, seed=args.seed, daily=d)
        dsr, sr0 = deflated_sharpe(d, args.n_trials, sr_std)
        gross, sweep = cost_sweep(df, cost)

        print(f"\n── {label} ──")
        print(f"   n={m['n']} en {m['dias']} días ({m['trades_dia']:.2f} trades/día)"
              f"  WR={100 * m['wr']:.1f}%")
        print(f"   EV neta = {m['ev_atr']:+.4f} ATR/trade   PF={m['pf']:.2f}")
        print(f"   PnL     = {m['pnl_usd']:+,.0f} $   EV = {m['ev_usd']:+.2f} $/trade"
              f"   maxDD = {m['maxdd_usd']:,.0f} $")
        print(f"   Sharpe diario anualizado = {m['sharpe']:.2f}")
        if boot:
            for k, dec in (("ev_atr", 4), ("ev_usd", 2), ("sharpe", 2)):
                lo, hi = ci(boot[k])
                flag = ("   (excluye 0: positivo)" if lo > 0
                        else "   (excluye 0: NEGATIVO)" if hi < 0
                        else "   <-- IC cruza 0")
                print(f"   IC95 {k:<7} [{lo:+.{dec}f}, {hi:+.{dec}f}]{flag}")
                m[f"{k}_lo"], m[f"{k}_hi"] = lo, hi
        margen = gross / cost if cost else float("inf")
        print(f"   coste breakeven = {gross:.4f} ATR  (nominal {cost:.4f} -> margen x{margen:.2f})")
        print("   barrido: " + "  ".join(f"c={c:.3f}:{e:+.3f}" for c, e in sweep))
        print(f"   DSR = {dsr:.3f}  (Sharpe esperado por azar tras {args.n_trials} pruebas: {sr0:.2f})")
        m["dsr"], m["sr_azar"], m["breakeven_atr"] = dsr, sr0, gross
        rows_out.append(m)

    if args.pbo and len(daily_map) >= 2:
        mat = pd.DataFrame(daily_map).sort_index()
        p, ncomb = pbo_cscv(mat, args.pbo_splits)
        print("\n" + "=" * 78)
        print("  PBO (CSCV, %d splits, %d combinaciones) = %.3f" % (args.pbo_splits, ncomb, p))
        print("  Lectura: probabilidad de que la config mejor in-sample")
        print("  quede por debajo de la mediana out-of-sample. >0.5 = selección sin valor.")
        print("=" * 78)

    if args.out_csv and rows_out:
        pd.DataFrame(rows_out).to_csv(args.out_csv, index=False)
        print("\n[OK] Informe -> %s" % args.out_csv)


if __name__ == "__main__":
    main()
