# Pre-registro de hipótesis — NQ (planes A y B)

Fecha de registro: **2026-10-13**, ANTES de calcular ningún retorno con estos datos. Cualquier cambio posterior de reglas, parámetros o criterios se añade como enmienda fechada al final, sin borrar lo anterior, y cuenta como prueba adicional.

## Datos y convenios

- **Fuente**: Databento GLBX.MDP3 `ohlcv-1m`, parent `NQ.FUT` (carpeta `GLBX-20261013-MBHWNSGGU8`), 2015-11-13 → 2026-10-12. Solo contratos outright (sin spreads).
- **Continuo**: front por volumen diario (roll cuando un vencimiento posterior supera en volumen al front); ajuste por **ratio** anclado en el último contrato. Retornos con precios ajustados; conversión a $ con el precio raw.
- **Instrumento de ejecución**: MNQ (2 $/punto). Resultados por **1 contrato** o en contratos fraccionarios con objetivo de volatilidad (el Sharpe no depende de la escala).
- **Horas**: en hora de Nueva York (America/New_York). `ts_event` de Databento = inicio de la barra de 1m.
  - Precio a las HH:MM = cierre de la barra que empieza en HH:MM−1. Ejemplo: 16:00 → cierre de la barra 15:59.
  - Apertura de la sesión de Nueva York = `open` de la barra 09:30.
- **Coste**: 1,25 puntos por ida y vuelta y contrato (0,625 por cada cambio de posición de 1 contrato). Escenario de estrés: ×2.
- **Periodos**:
  - **Evaluación**: 2016-01-01 → 2024-12-31. 2015-11/12 solo sirve de calentamiento.
  - **Examen final**: 2025-01-01 → 2026-10-12. Se ejecuta **una sola vez** y solo con las hipótesis que aprueben.

## Hipótesis (la especificación primaria decide; las variantes se informan, no se eligen)

### H0 — referencias
- **H0a** Buy & hold de 1 contrato. Solo es referencia, no candidata.
- **H0b** Largo con objetivo de volatilidad:
  - contratos = objetivo / σ;
  - σ = desviación EWMA (centro de masa 60 días) del cambio diario en $ de 1 contrato, conocida al cierre del día;
  - rebalanceo semanal (último día hábil de la semana).
  - Es candidata: "prima de la renta variable con gestión de volatilidad".

### H1 — Tendencia (time-series momentum)
- **Señal** al cierre diario (16:00) = media de sign(r21), sign(r63), sign(r252), retornos de 21/63/252 días hábiles. Rango [−1, 1].
- **Posición**: señal × objetivo / σ (σ como en H0b). Rebalanceo semanal.
- **Ejecución** de las estrategias con señal diaria (H0b, H1, H2): primer precio con barra que empieza ≥ 16:05 (cierre de esa barra). La señal solo usa información hasta las 16:00.
- **Variantes**: (a) solo sign(r252); (b) versión largo/plano: max(señal, 0).

### H2 — Exposición gestionada por volatilidad (Moreira y Muir 2017)
- **Posición** larga ∝ 1 / varianza realizada de los últimos 21 retornos diarios, revisada a fin de mes.
- Escala normalizada a la media; tope en 2× la exposición media.
- **Variante**: ∝ 1/σ (no 1/σ²) con rebalanceo semanal.

### H3 — Deriva overnight (Lou, Polk y Skouras 2019)
- Largo 1 contrato del cierre de la sesión de Nueva York (16:00, cierre de la barra 15:59) a la apertura siguiente (09:30, open de la barra 09:30). Todas las sesiones con ambos precios.
- **Contraste** (no es prueba): la pata intradía, larga de 09:30 a 16:00.

### H4 — Momentum intradía (Gao, Han, Li y Zhou 2018)
- **Señal** r1 = retorno del cierre de la sesión anterior (16:00) a las 10:00 (cierre de la barra 09:59).
- **Operación**: sign(r1) × 1 contrato de las 15:30 (cierre de la barra 15:29) a las 16:00 (cierre de la barra 15:59). Solo días con todas las barras.
- **Variante**: r1 medido de la apertura (09:30) a las 10:00, sin el overnight.

### H5 — Cambio de mes (Lakonishok y Smidt 1988)
- Largo 1 contrato del cierre del día hábil −2 al cierre del día hábil +3. El día −1 es el último hábil del mes, así que se mantienen los días −1, +1, +2 y +3. Días hábiles = días con cierre de sesión en los datos.
- **Variante**: del −2 al +4.

## Criterios de aprobado (sobre 2016-2024, especificación primaria, coste base)

1. Sharpe anual neto ≥ 0,4. Se calcula sobre el PnL diario incluyendo los días sin posición, ×√252.
2. IC95 del PnL medio diario (bootstrap por bloques semanales) que **excluye el 0**.
3. PnL positivo en las dos mitades: 2016-2020 y 2021-2024.
4. Con coste ×2, Sharpe > 0.
5. **Placebo** (H1 y H4): la estrategia real supera el percentil 95 de 200 versiones con el lado al azar.
6. **H2, en particular**: Sharpe(H2) − Sharpe(H0a) ≥ 0,15 y positivo en las dos mitades.
7. **H0b** se evalúa con los criterios 1-4. Si aprueba, es la referencia a batir por H1 y H2 en la cartera.

Se informa el DSR con N = 11 pruebas registradas: H0b, H1 ×3, H2 ×2, H3, H4 ×2 y H5 ×2. Este registro es una familia nueva: horizonte diario o de sesión, no las ~4.000 configuraciones intradía anteriores.

**Examen final** (2025-01 → 2026-10, una sola vez): aprueba la hipótesis que mantenga Sharpe neto > 0 y el mismo signo.

## Enmiendas
(ninguna)

## Resultados (2026-10-13)

Implementación: `research/` (tests en `tests/test_research.py`), `python -m research.run_nq`. Revisión independiente del código (agente revisor): sin look-ahead ni errores que cambien veredictos. Corregidos tras la revisión: llave del placebo de H1 (periodo de tenencia, no semana ISO) y caché de sesiones; ningún veredicto cambió.

**Evaluación 2016-2024** (coste 1,25 pts; PnL en $ de MNQ):

| Hipótesis | Sharpe | IC95 media diaria $ | 2016-20 / 2021-24 $ | Sharpe coste ×2 | Veredicto |
|---|---|---|---|---|---|
| H0a buy & hold (referencia) | 0,63 | [0,9, 24,4] | +16.225 / +12.788 | 0,63 | — |
| **H0b** largo con objetivo de vol. | **0,83** | [13,6, 99,6] | +88.888 / +40.790 | 0,83 | **APRUEBA** |
| H1 tendencia | 0,43 | [−11,7, 56,7] | + / + | 0,43 | no (IC cruza 0; placebo p95 0,51) |
| H2 1/varianza mensual | 0,65 | [0,4, 14,7] | + / + | 0,65 | no (+0,02 sobre B&H; se exigía +0,15) |
| **H3** overnight | **0,57** | [0,7, 12,7] | +8.598 / +6.488 | 0,36 | **APRUEBA** (pata intradía: 0,12) |
| H4 última media hora | −0,77 | [−6,7, −0,6] | − / − | −1,27 | no |
| H5 cambio de mes | 0,13 | [−4,3, 6,6] | + / − | 0,12 | no |

- DSR (N=11): H0b 0,38 y H3 0,14 → no superan el listón estricto de "mejor de 11". El soporte es económico (prima de riesgo documentada) más su persistencia fuera de muestra.
- Sensibilidad del IC (informativa, no cambia el criterio): H0b sigue excluyendo 0 con bloques de 4 y 8 semanas. H3 queda en el límite (bloques de 4 semanas: [−0,3, 12,9]).

**Examen final 2025-01 → 2026-10** (una sola vez; criterio: Sharpe > 0):

| | Sharpe | PnL $ | Veredicto |
|---|---|---|---|
| H0a (referencia) | 0,85 | +15.880 | — |
| H0b | 0,68 | +21.129 | **aprueba** |
| H3 | 0,94 (0,85 con coste ×2) | +11.533 | **aprueba** |

El examen se re-ejecutó una vez más solo para corregir la etiqueta de la columna `aprobado`; los números son idénticos (cálculo determinista).

**Lectura honesta.**
- H0b y H3 son **exposición larga a la prima de la renta variable** gestionada (beta), no alfa direccional.
- Pierden en mercados bajistas (2022: H0b −25k $ a su escala, H3 −6,6k $ por contrato).
- Su correlación diaria es 0,47.
- La muestra es una década alcista excepcional del Nasdaq: los retornos futuros esperables son menores.
- H3 con 1 MNQ: ≈ +2,6k $/año de media (2016-2026), volatilidad ≈ 3,8k $/año, drawdown máximo −8,8k $.

**Observaciones NO pre-registradas** (no son operables; serían hipótesis nuevas que necesitan datos nuevos): H4 es significativamente negativa en 2016-2024, es decir, una posible reversión de la última media hora.
