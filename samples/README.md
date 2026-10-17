# 🧩 Muestras de código (`samples/`)

Ficheros reales extraídos **sin modificar** del repositorio privado. Se publican para que se pueda evaluar cómo diseño y programo; los detectores de señal, los modelos entrenados y los datos no se incluyen.

> Algunos ficheros de C# hacen referencia a módulos privados (por ejemplo `EnsembleDecisionEngine.cs` usa `Tier1StrategyDetectors`) o a `NinjaTrader.*` (`GlobalRiskCoordinator.cs`), así que no compilan de forma aislada. Los tests de Python sí son autocontenidos: `pytest samples/python/tests`.

## C# / .NET (`samples/csharp/`)

| Archivo | Qué demuestra |
| :--- | :--- |
| [`HFT_BOT_ML_Common.cs`](csharp/HFT_BOT_ML_Common.cs) | Evaluador **XGBoost en C# puro**: `base_score` a *logit*, rama por defecto de los `NaN` por nodo, comparación en `float32`, validación estricta del número de features y **calibración isotónica** (`np.interp`). Incluye un buffer circular con estadísticos. |
| [`MiniJson.cs`](csharp/MiniJson.cs) | **Parser JSON recursivo propio** sin dependencias (solo `System.*`), compatible con .NET Framework 4.8. |
| [`EnsembleDecisionEngine.cs`](csharp/EnsembleDecisionEngine.cs) | **Motor de decisión** réplica del backtest de Python: votos por detector con persistencia, empate como abstención, media de *log-odds*, *cooldown* y una evaluación por barra. Log desacoplado (`EngineLog`) para poder compilar el motor fuera de NinjaTrader. |
| [`GlobalRiskCoordinator.cs`](csharp/GlobalRiskCoordinator.cs) | **Estado compartido y concurrente** (`lock`) entre instancias de estrategia: posiciones por instancia, *halt* por drawdown, *profit lock* con trinquete, bloqueo horario y cierre forzado. |
| [`parity_harness/`](csharp/parity_harness/) | **Harness .NET** que compila el código real del bot sin NinjaTrader y exporta features y predicciones para compararlas con Python. |

## Python (`samples/python/`)

| Archivo | Qué demuestra |
| :--- | :--- |
| [`stats_significance.py`](python/stats_significance.py) | Significancia y robustez sobre los trades: bootstrap por bloques semanales, *Deflated Sharpe Ratio*, PBO e IC95. |
| [`validate_prediction_parity.py`](python/validate_prediction_parity.py) | Comprobación de paridad de probabilidades entre el modelo en Python y el evaluador en C#. |
| [`research/`](python/research/) | Paquete de investigación: serie continua con *rolls* por volumen y ajuste por ratio, precios por sesión (Nueva York con horario de verano), contabilidad de PnL, señales causales y evaluación. |
| [`tests/test_research.py`](python/tests/test_research.py) | Tests con `pytest`: desambiguación de contratos por década, *roll* por volumen, costes por contrato, **ausencia de información futura** en el *vol-targeting* y calendario. |
