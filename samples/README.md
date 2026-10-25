# 🧩 Muestras de código (`samples/`)

Ficheros reales extraídos del repositorio privado. Se publican para que se pueda evaluar cómo diseño y programo; los detectores de señal, los modelos entrenados y los datos no se incluyen.

> ✅ **Suites de tests autocontenidas y ejecutables directamente en este repositorio:**
> - **C# (.NET, sin NuGet ni NinjaTrader):** `dotnet run --project samples/csharp/csharp_tests -c Release` (25 tests sobre `MiniJson`, `XGBoostEvaluator`, `KellyZoneLookup` y `EnsembleDecisionEngine`).
> - **Python (`pytest`):** `pytest samples/python/tests` (16 tests sobre construcción de continuo, sesiones, costes, causalidad y bootstrap).

## C# / .NET (`samples/csharp/`)

| Archivo | Qué demuestra |
| :--- | :--- |
| [`HFT_BOT_ML_Common.cs`](csharp/HFT_BOT_ML_Common.cs) | Evaluador **XGBoost en C# puro**: `base_score` a *logit*, rama por defecto de los `NaN` por nodo, comparación en `float32`, validación estricta del número de features y **calibración isotónica** (`np.interp`). Incluye un buffer circular con estadísticos (`Std` poblacional y `StdSample` con `ddof=1`). |
| [`MiniJson.cs`](csharp/MiniJson.cs) | **Parser JSON recursivo propio** sin dependencias (solo `System.*`), compatible con .NET Framework 4.8, con soporte de `NaN`/`Infinity`, comparación ordinal in-situ y validación estricta (`FormatException`). |
| [`EnsembleDecisionEngine.cs`](csharp/EnsembleDecisionEngine.cs) | **Motor de decisión** réplica del backtest de Python: votos por detector con persistencia, empate como abstención, media de *log-odds*, *strategy gate* *fail-closed* (`KellyZoneLookup`), *cooldown* y una evaluación por barra. Log desacoplado (`EngineLog`) para compilar fuera de NinjaTrader. |
| [`GlobalRiskCoordinator.cs`](csharp/GlobalRiskCoordinator.cs) | **Estado compartido y concurrente** (`lock`) entre instancias de estrategia: posiciones por instancia, *halt* por drawdown, *profit lock* con trinquete, bloqueo horario y cierre forzado. |
| [`csharp_tests/`](csharp/csharp_tests/) | **Runner de tests unitarios en C#** sin dependencias externas que compila y verifica las fuentes reales de `samples/csharp/`. |
| [`parity_harness/`](csharp/parity_harness/) | **Harness .NET** que compila el código real del bot sin NinjaTrader y exporta features y predicciones para compararlas con Python. |

## Python (`samples/python/`)

| Archivo | Qué demuestra |
| :--- | :--- |
| [`stats_significance.py`](python/stats_significance.py) | Significancia y robustez sobre los trades: bootstrap por bloques semanales, *Deflated Sharpe Ratio*, PBO e IC95. |
| [`validate_prediction_parity.py`](python/validate_prediction_parity.py) | Comprobación de paridad de probabilidades entre el modelo en Python y el evaluador en C#. |
| [`research/`](python/research/) | Paquete de investigación ([`docs/HIPOTESIS.md`](../docs/HIPOTESIS.md)): serie continua con *rolls* por volumen y ajuste por ratio, precios por sesión (Nueva York con horario de verano), contabilidad de PnL, señales causales y evaluación. |
| [`tests/test_research.py`](python/tests/test_research.py) | Tests con `pytest`: desambiguación de contratos por década, *roll* por volumen, costes por contrato, **ausencia de información futura** en el *vol-targeting* y calendario. |
