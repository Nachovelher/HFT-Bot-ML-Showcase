# Auditoría técnica — octubre 2026

Fecha: 2026-10-06. Alcance: pipeline Python (features, entrenamiento, validación), runtime C# en NinjaTrader 8 y herramientas. No se reentrenó ningún modelo.

## 1. Resumen

1. **La validación de los 4 presets tenía un leak de selección.**
   - **Mecanismo.** El cache de features solo contenía las barras cuyo futuro se resolvía (TP o SL en 5 barras), y el backtest ponía features NaN en el resto. Así, casi todos los trades "validados" caían en barras que se sabía que iban a resolverse.
   - **Resultado honesto.** Con features en todas las barras, como en NT8, la EV fuera de muestra es **≈ 0 o negativa en las 8 mediciones** (4 patas × 2 años); el total de las 8 es ≈ −13.500 $.
   - **El entrenamiento también está afectado.** Los modelos aprendieron el patrón "features NaN = pérdida".
2. **NT8 tampoco ejecutaba lo que se validaba.** Se encontraron, entre otros, estos fallos:
   - El time-decay y la expiración no actuaban nunca.
   - Las features de sesión iban desplazadas ~6 h.
   - El bloqueo de repetición de señales era distinto del de Python.
   - Un detector de los modelos no se emitía.
   - El ForceClose no disparaba nunca.

   Lo corregido está en la §4; lo pendiente, en la §5.
3. **Salvaguarda.** Mientras no haya modelos re-validados sin leak, la estrategia solo envía órdenes en cuentas Sim/Playback (`AllowRealAccount = false` por defecto).
4. **Siguiente paso recomendado** (requiere tu permiso, regla 1 de CLAUDE.md): reentrenar con el cache completo y re-validar con `val_features_full.sh`. El port completo de los detectores a C# solo tiene sentido si ese reentrenamiento muestra un edge en dos años.

## 2. Hallazgo principal: leak de selección en el features cache

### Mecanismo

1. `pipeline/step1c_session_features.py` construía `combined = features.join(labels).dropna(subset=["label_long", "label_short", "regime"])` y guardaba **ese** subconjunto como `features_v2_cache.csv`.
2. Con `include_expired='exclude'`, `label_long`/`label_short` son NaN cuando el trade **expira** sin tocar TP ni SL en 5 barras. El cache quedaba restringido a las barras donde **ambos** lados se resuelven: una condición sobre el futuro. Son ~55 % de las barras en 10m (68.860 de 124.794 en MNQ 10m).
3. `tools/backtest_ensemble.py` hacía `X_all.reindex(df.index)`, así que en el ~45 % restante las 153 features base eran NaN.
4. `pipeline/step8_metalabeling.py` entrenaba con ese mismo patrón. Fuera del cache, si el lado de la señal se resolvió es que el lado contrario expiró. Eso implica que el precio no se movió 1 ATR en su contra ni 1,5 ATR a su favor, y entonces el lado resuelto **solo pudo ser SL**. Por tanto el meta-modelo aprendió "features NaN = pérdida segura".
5. Resultado: los modelos casi nunca superaban el gate en barras NaN y **el 98-100 % de los trades del backtest caían en barras cuyo futuro se resolvía**:

   | Pata | Trades | En barras del cache |
   |---|---|---|
   | MNQ10 2026 | 109 | 100 % |
   | MNQ10 2025 | 256 | 99,2 % |
   | MGC15 2026 | 115 | 98,3 % |

   Por eso solo expiraba el 0-2 % de los trades.

En NT8 el `FullFeatureExtractor` calcula features en **todas** las barras: nada de lo validado aplicaba en vivo.

### Re-validación honesta

`val_features_full.sh` usa los mismos modelos, el mismo motor, los mismos costes y el mismo corte temporal. Las features se calculan para todas las barras con `tools/build_features_full.py`: mismas funciones que step1c y diferencia **0,0** en las filas comunes con el cache antiguo. Estadísticas de `tools/stats_significance.py` (bootstrap semanal, 2.000 réplicas):

| Pata | Año | "Validado" (con leak) | Honesto n | EV ATR/trade | IC95 | PnL $ |
|---|---|---|---|---|---|---|
| MNQ10 | 2025 | +0.150 (n=256) | 460 | −0.034 | [−0.156, +0.081] | −164 |
| MNQ10 | 2026 | +0.275 (n=109) | 201 | +0.002 | [−0.141, +0.126] | +856 |
| MGC10 (sin EU) | 2025 | +0.150 (n=276) | 466 | −0.001 | [−0.098, +0.092] | +592 |
| MGC10 (sin EU) | 2026 | +0.110 (n=126) | 215 | −0.046 | [−0.205, +0.113] | +2.309 |
| MGC15 | 2025 | +0.079 (n=271) | 566 | −0.076 | [−0.173, +0.029] | −7.099 |
| MGC15 | 2026 | +0.245 (n=115) | 239 | −0.068 | [−0.214, +0.081] | −453 |
| MNQ5 | 2025 | +0.046 (n=418) | 808 | **−0.109** | **[−0.173, −0.043]** | −9.935 |
| MNQ5 | 2026 | +0.336 (n=59) | 114 | +0.081 | [−0.123, +0.278] | +424 |

Notas:
- Las cifras "validado" son las de CLAUDE.md, con la salida ATROnly del motor; las de MNQ10 2026 y MGC15 llevaban además decay.
- El DSR es ≤ 0,07 en todas las patas.
- In-sample, los mismos modelos dan EV ≈ +0,7 ATR/trade (PF 4-6): memorización, no edge.

### Corrección aplicada (commit 9d5e5e7)
- step1c escribe el cache con **todas** las barras.
- step8 y backtest_ensemble **abortan** si el cache cubre < 99 % de las barras del CSV. `--allow_partial_cache` existe solo para reproducir resultados antiguos.
- `tools/build_features_full.py` regenera un cache completo sin reentrenar.

Los modelos actuales siguen entrenados con el leak: hay que reentrenar para saber si existe algún edge.

## 3. Divergencias NT8 ↔ Python encontradas

| # | Divergencia | Impacto medido | Estado |
|---|---|---|---|
| 1 | `ManageExit` hacía `return` en ATROnly antes de `ApplyTimeTrail`: la expiración a 5 barras y el time-decay **nunca** actuaban (desde ee6f826, 2026-07-07). | Expiración: casi nada, porque el 0-2 % de los trades llegan a 5 barras. Decay perdido, por ejemplo MGC15 2026: +0.241 → +0.163 (simulado sobre los trades con leak). | Corregido |
| 2 | `FullFeatureExtractor` trataba `Time[0]` como hora de **Nueva York**, pero NT8 la entrega en la zona configurada (Madrid en este equipo). | Features de sesión desplazadas ~6 h; corte de día y slots de volumen distintos. Antes del fix las 5 de sesión fallaban en el 25-33 % de las barras; después, **112/173 features idénticas**. | Corregido (UTC con `Globals.GeneralOptions.TimeZoneInfo`) |
| 3 | TP/SL con el `ATR(14)` de NT8 (Wilder) frente al ATR SMA del backtest y las etiquetas. | Difieren > 10 % en ~45 % de las barras; EV −0.04 a +0.01 ATR/trade. | Corregido en TP/SL y RegimeFilter (ATR SMA del extractor). En detectores: pendiente (#9) |
| 4 | Bloqueo de repetición de señales (lock): C# bloqueaba 5 barras a todos los detectores; Python bloquea 4, 9 o 14 según el detector. | +24 % de señales y +40 % de trades. EV de MNQ10 2026 +0.254 → +0.113; MGC 2026 mejoraba. Signo variable. | Corregido (lock por detector = Python) |
| 5 | `HTF_OB_1H` comentado en `DetectAll`, pero con modelo, en el manifest y votando en el backtest. | 1-11 trades/año por pata. | Corregido (reactivado) |
| 6 | ForceClose: exigía el minuto exacto con `DateTime.Now` y solo se comprobaba al cierre de barra. | Con barras de 5/10/15 min **nunca** disparaba. | Corregido (ventana de 15 min, `Globals.Now`, comprobación tick a tick, solo en tiempo real) |
| 7 | `GlobalRiskCoordinator` registraba las posiciones por símbolo. | MNQ5+MNQ10 o MGC10+MGC15: cerrar una pata liberaba el hueco de la otra (con `EnableMultiInstrument=false`). | Corregido (registro por instancia) |
| 8 | `GetMinThreshold`: clave ausente → 0,25 en C# frente a 0,0 en Python. | Latente; hoy todas las claves existen. | Corregido |
| 9 | **Señales de detectores**: no existía ningún test de paridad. Divergencias vistas en el código: barras de timeframe superior (Python re-agrupa barras del chart con `resample()`; en 10m, sus "barras de 15m" son alternas de 20 y 10 min; NT8 usa series reales); `DetectHTFFVG15m` con umbral 0,7×ATR frente a 0,5 en Python y otra invalidación; índices `[1]`/`[3]` que añaden una barra de 15m de retraso; ATR/RSI Wilder frente a SMA; corte de día a medianoche local frente a UTC. | Sensibilidad medida: con la construcción de barras HTF de NT8, las señales FVG/OB HTF coinciden solo en un 13-36 % (Jaccard); las de tendencia/régimen, en un 63-97 %. | **Pendiente** |
| 10 | 61 de 173 features diferían entre C# y Python tras el fix horario: niveles de sesión Asia/EU causales, TPO/Volume Profile (puntos medios de bucket e invalidez < 10 barras), IB (< 30 min), VWAP diario/semanal (`ddof=1`, `diff(3)` por sesión, ISO week), `pa_*` (ATR Wilder 14, VWAP 390 barras, `close_raw`), doji `NaN` en candle patterns, desempate de swing low y clip `of_vwdir_cum_5`. | **173/173 features idénticas** (`tol=1e-6`, 0 divergencias en 17.904 barras). | **Corregido** |

## 4. Otros puntos revisados

- **`backtest_exits.py`.** Cuando el precio salta el SL (o el SL decaído ya está cruzado), Python sale al precio del SL y no al del mercado. El sesgo medido es ≤ 0,007 ATR/trade, y una simulación fiel al C# da lo mismo o mejor: **no era un problema**.
- **Decay en MNQ10 2025.** No se había medido nunca; a 1m empeora un 12-15 % en $. Con el leak, todas estas comparaciones de palancas quedan **en suspenso**.
- **Sizing Kelly por zonas.** La correlación entre `agg_p` y el retorno es de −0,05 a +0,13 en las 8 mediciones, y la EV no crece con los contratos: el sizing por probabilidad es ruido. A re-evaluar con modelos honestos.
- **`stats_significance.py`.** El Sharpe solo contaba los días con trades (inflaba el Sharpe y el DSR) y el IC95 decía "cruza 0" aunque fuera entero negativo. **Corregido**.
- **Resto de corregidos:**
  - KellyZoneLookup.
  - Motor C# frente a réplica Python: equivalentes.
  - Manifest/modelos/kelly coherentes en los 4 directorios.
  - Paridad de predicciones 54/54.

## 5. Pendiente

1. **Reentrenar sin leak (requiere permiso).**
   - Ejecutar `train_symbols.sh` (CUT=1 y completo) por combo con el step1c corregido.
   - Validar con `val_features_full.sh`, adaptado a los nuevos directorios.
   - Criterio: positivo en $ en 2025 **y** 2026, con placebos.
   - Además, jul-ago 2026 sigue siendo ventana virgen.
2. **Solo si (1) da edge: paridad de señales y features.**
   - Portar `Tier1StrategyDetectors` a C# puro, sin tipos NT8, alimentado con OHLCV + UTC y replicando pandas: barras HTF desde el chart, ATR/RSI SMA, día UTC y los 23 detectores.
   - El harness ya tiene el modo `signals` reservado y `tools/parity_offline.py signals` hace la comparación. Objetivo: Jaccard ≥ 0,999 por detector.
   - Lo mismo para las 61 features con `parity_offline.py features`.
3. **Despliegue de este código** (NT8 cerrado):
   - Ejecutar `./deploy.sh 10m MNQ` (y el resto de combos).
   - Los `.cs` retirados se renombran a `.removed_<fecha>` en `bin/Custom/HFT_BOT_ML/`. Si quedaran como `.cs`, `step2b_*.cs` redefiniría `IsotonicCalibrator` y NT8 no compilaría.
   - `AllowRealAccount` es una propiedad nueva: las instancias existentes arrancan en `false`.
   - `BypassChoppy` desaparece; NT8 ignora el valor guardado.
4. **Limpieza de artefactos sin trackear — HECHA el 2026-10-06 con confirmación del owner** (`output` + `data`: 11,5 → 5,3 GB). Se borró:
   - `output/models_3m_MNQ.bak_prefix_*` (3 directorios, ~1,85 GB) y `output/models_3m_MNQCUT24.bak_prefix_*`.
   - Combos abandonados en `output/`: 3m (MNQ/MGC/MES/M2K/M6E/MCL/MYM), 15m (MNQ/MES/M2K/M6E/MCL/MYM/10Y, más CUT23/CUT24/ib_experiment), 10m M2K/MYM, 5m MGC y `output/models` (genérico). Total ≈ 3,5 GB.
   - CSV de datos de instrumentos abandonados en `data/` (M2K, MCL, MYM, M6E, MES, 10Y, MNQCUT23): ≈ 1 GB.
   - Directorios de modelos abandonados **dentro de NT8** (`bin/Custom/HFT_BOT_ML_15m_{10Y,M2K,M6E,MCL,MES,MNQ,MYM}`, `HFT_BOT_ML_3m_MNQ`). Con `RequireModels`, un chart de esos combos cargaría modelos abandonados y operaría.
   - `sanity_check_*.xlsx` en la raíz.

## 5b. Reentrenamiento sin leak (2026-10-06) y despliegue (2026-10-08)

`retrain_honest.sh` reentrenó las 4 patas (producción 2023-2025 + gemelo CUT24) con el step1c corregido. Los modelos con leak quedan en `output/models_*.bak_leak_20261006`. Validación OOS con features en todas las barras (`output/fullfeat_retrain/`):

| Pata | 2025 (CUT24) | 2026 H1 (producción) |
|---|---|---|
| MNQ10 | −0.153 (n=201, −1.730 $) | −0.216 (n=26, −375 $) |
| MGC10 (sin EU) | −0.117 (−2.970 $) | −0.234 (−2.131 $) |
| MGC15 | +0.053 (n=173, +1.995 $) | +0.123 (n=57, +439 $) |
| MNQ5 | −0.026 (n=96, −147 $) | +0.321 (n=18, +761 $) |

- Todos los IC95 cruzan 0; DSR ≤ 0,25.
- In-sample los modelos dan EV +1,0 ATR/trade y PF de 8 a 80: memorización. Fuera de muestra no queda nada.
- **Placebos de MGC15**, la única pata positiva en los dos años (`placebos_leg.sh`):

  | Año | Real | Entrar 1 barra tarde | Lado aleatorio (5 semillas) |
  |---|---|---|---|
  | 2025 | +0.053 | −0.001 | −0.058 … **+0.120** |
  | 2026 | +0.123 | +0.060 | −0.273 … **+0.212** |

  En los dos años la EV real queda dentro de la banda de los sorteos de lado: **no se distingue del azar**. Con 4 patas probadas, que una salga positiva dos años seguidos por azar tiene una probabilidad ≈ 68 %.

**Conclusión: con estos 23 detectores, esta economía (TP 1.5 / SL 1 / 5 barras) y meta-modelos XGBoost por detector, no hay edge demostrable en ninguna de las 4 patas.**

**Despliegue (2026-10-08).** Ejecutado `deploy.sh` con NT8 cerrado para las 4 patas:
- Paridad de predicciones 53-54/54 OK y addon completo compilado sin errores.
- Los 6 `.cs` retirados se renombraron a `.removed_20261008`.
- En NT8 hay ahora el código del repo y los modelos **reentrenados sin leak**, verificados byte a byte.
- `AllowRealAccount = false`: solo Sim/Playback.

## 6. Herramientas nuevas

| Herramienta | Para qué |
|---|---|
| `tools/build_features_full.py` | Cache de features para todas las barras (sin filtro por etiquetas). |
| `val_features_full.sh` | Re-validación honesta de las 4 patas × 2 años, con estadísticas. |
| `tools/parity_offline.py features` | Paridad de las 173 features C# ↔ Python sin NT8; ejecuta el `FullFeatureExtractor` real. |
| `tools/parity_offline.py signals` | Paridad de señales de detectores; requiere el port de la §5.2. |
| `tools/nt8_compile_check.py [--warnings]` | Compila el addon NT8 completo con los `.cs` del repo (sustituye a los antiguos `full_compile_check.*`). |
| `deploy.sh` | Rutas actualizadas, pre-check con `nt8_compile_check.py` y sincronización (renombra los `.cs` retirados). |

## 7. Código eliminado

- **C#:**
  - `NewsGate.cs`.
  - `step2b_XGBoostRegimeEvaluator.cs`; su `IsotonicCalibrator` pasa a `HFT_BOT_ML_Common.cs`.
  - `step2c_SessionFeatureExtractor.cs`, `step2e_NewFeatureExtractor.cs`, `step4_MarketRegimeHMM.cs` y `step8b_MetaLabelEvaluator.cs`.
  - `legacy/`.
  - En la estrategia: modos de salida de trailing/BE/DD-rebound/reversal (el enum se conserva por serialización y se fuerza ATROnly), gate HMM, news gate, `BypassChoppy`, `BarData`, boost `PrevDay_HL`, `PatternDetectorSet`/veto C y 14 detectores retirados.
- **Python/shell:** 28 scripts sin referencias (lista en el commit 238535b) y `docs/ALL_PATCHES.diff` / `docs/FIXES_APPLIED_SUMMARY.md`.
- **Tamaño:** `HFT_ML_Strategy.cs` 3.131 → ~2.620 líneas, `EnsembleDecisionEngine.cs` 1.092 → ~380, `Tier1StrategyDetectors.cs` 2.450 → 1.787.

## 8. Commits

| Commit | Bloque |
|---|---|
| 9d5e5e7 | Leak de selección: cache completo + guardas en backtest/step8 |
| f29f46b | C#: expiración/decay con ATROnly, UTC, ForceClose, coordinador por instancia, solo Sim + poda del motor |
| c2d87a7 | Poda de C# muerto + `deploy.sh` sincronizado |
| 238535b | Poda de scripts + `stats_significance` (Sharpe con días sin trades, etiqueta IC) |
| b6a0faf | Detectores: lock por detector, `HTF_OB_1H`, fuera `PrevDay_HL` y 14 retirados |
