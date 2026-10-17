/*
=============================================================
HFT_BOT_ML — EnsembleDecisionEngine (Ensemble B)
=============================================================
Réplica exacta del motor de tools/backtest_ensemble.py (run_backtest):

  1. BuildVotes   — cada detector Tier1 con señal ≠ 0 y meta-modelo de su
                    lado aporta su prob calibrada; strategy gate opcional
                    (prob ≥ zone1_threshold del kelly_thresholds.json);
                    la señal persiste 3 barras (buffer con edad).
  2. Aggregate    — minVoters (default 2), empate → abstención, media de
                    log-odds de los votantes de la dirección ganadora,
                    probThreshold 0.25, Kelly genérico {0.25,0.55,0.70}.
  3. Cooldown     — 5 barras tras cada decisión con contratos > 0.

Evaluate() se llama UNA vez por barra (cache por currentBar).

[2026-10-06] Poda: HMM gate (régimen dummy, nunca bloqueaba), detector set
PA_* de Ensemble C, BarData, boost PrevDay_HL (nunca se aplicaba) y
parsers JSON sin uso. El log va por EngineLog para poder compilar el motor
fuera de NT8 (tools/parity_harness).
=============================================================
*/

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace HFT_BOT_ML
{
    /// <summary>
    /// Sumidero de log del motor. HFT_ML_Strategy lo apunta al Output de NT8;
    /// sin sumidero (harness offline) escribe en consola.
    /// </summary>
    public static class EngineLog
    {
        public static Action<string> Sink;

        public static void Write(string msg)
        {
            try
            {
                if (Sink != null) Sink(msg);
                else Console.WriteLine(msg);
            }
            catch { /* el log nunca debe tumbar el motor */ }
        }
    }

    public class EnsembleDecision
    {
        public int      Direction;
        public double   Probability;
        public int      Contracts;
        public int      NAgree;
        public string   Source;
        public string[] ActiveStrats;
    }

    public class EnsembleDecisionEngine
    {
        private readonly string _modelsDir;

        private Dictionary<string, XGBoostEvaluator> _strategyModelsLong;
        private Dictionary<string, XGBoostEvaluator> _strategyModelsShort;
        private Dictionary<string, string> _modelKeyMap;   // detector → model key (case-insensitive)
        private string[] _activeStrategies;
        private readonly HashSet<string> _diagReportedKeys = new HashSet<string>();

        private KellyZoneLookup _kellyZones;
        private int    _minVoters = 2;
        private double _probThreshold = 0.25;
        private bool   _strategyGateEnabled = false;
        private bool   _verboseLogging = true;

        // Persistencia de señales (Step B/C de BuildVotes)
        private const int SIGNAL_PERSIST_BARS = 3;
        private readonly Dictionary<string, (int dir, double prob, int barAge)> _signalBuffer
            = new Dictionary<string, (int, double, int)>();

        // Cooldown tras decisión con contratos
        private const int TRADE_COOLDOWN_BARS = 5;
        private int _cooldownRemaining = 0;

        // Una evaluación por barra
        private int _lastEvaluatedBar = -1;
        private EnsembleDecision _lastDecisionCached;

        public EnsembleDecisionEngine(string modelsDir)
        {
            _modelsDir = modelsDir;
            LoadEnsembleB();
            _kellyZones = new KellyZoneLookup(_modelsDir);
        }

        private void LoadEnsembleB()
        {
            _strategyModelsLong  = new Dictionary<string, XGBoostEvaluator>();
            _strategyModelsShort = new Dictionary<string, XGBoostEvaluator>();

            // Escanear meta_long_*.json y meta_short_*.json (un detector puede
            // tener solo un lado tras retiros). Se excluyen los sidecars de
            // calibración, el global "cal" y el modelo unificado ensemble_c.
            var stratSet = new HashSet<string>();
            if (Directory.Exists(_modelsDir))
            {
                foreach (var prefix in new[] { "meta_long_", "meta_short_" })
                {
                    foreach (var f in Directory.GetFiles(_modelsDir, prefix + "*.json"))
                    {
                        string raw = Path.GetFileNameWithoutExtension(f).Substring(prefix.Length);
                        if (string.IsNullOrEmpty(raw)) continue;
                        if (raw == "cal" || raw.EndsWith("_cal")) continue;
                        if (raw.StartsWith("ensemble")) continue;
                        stratSet.Add(raw);
                    }
                }
            }
            _activeStrategies = stratSet.ToArray();

            foreach (var raw in _activeStrategies)
            {
                // autoLoadCalibration: los thresholds de kelly_thresholds.json
                // están en escala de probabilidad CALIBRADA (sidecar _cal.json).
                string pL = Path.Combine(_modelsDir, "meta_long_" + raw + ".json");
                string pS = Path.Combine(_modelsDir, "meta_short_" + raw + ".json");
                if (File.Exists(pL)) _strategyModelsLong[raw]  = new XGBoostEvaluator(pL, autoLoadCalibration: true);
                if (File.Exists(pS)) _strategyModelsShort[raw] = new XGBoostEvaluator(pS, autoLoadCalibration: true);
            }

            _modelKeyMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var key in _strategyModelsLong.Keys)  _modelKeyMap[key] = key;
            foreach (var key in _strategyModelsShort.Keys) if (!_modelKeyMap.ContainsKey(key)) _modelKeyMap[key] = key;

            // Diagnóstico: detectores activos sin meta-modelo (se descartan en BuildVotes).
            var orphans = Tier1StrategyDetectors.ActiveDetectorNames
                .Where(d => !_modelKeyMap.ContainsKey(ModelKey(d))).ToList();
            if (orphans.Count > 0 && HasModels)
                EngineLog.Write(string.Format(
                    "[ML-DIAG] Detectores activos SIN meta-modelo ({0}): {1} — se descartan en BuildVotes.",
                    orphans.Count, string.Join(", ", orphans)));
        }

        /// <summary>Clave de modelo de un detector: igual que Python (name.replace('@','_').lower()).</summary>
        private static string ModelKey(string detector)
        {
            return detector.Replace("@", "_").ToLowerInvariant();
        }

        public EnsembleDecision Evaluate(double[] features, Dictionary<string, int> tier1Signals,
                                         int currentBar = -1)
        {
            if (currentBar >= 0 && currentBar == _lastEvaluatedBar)
                return _lastDecisionCached ?? new EnsembleDecision { Direction = 0, Source = "Already evaluated this bar" };

            EnsembleDecision result = (tier1Signals == null || tier1Signals.Count == 0)
                ? new EnsembleDecision { Direction = 0, Source = "No Tier1" }
                : AggregateVotes(BuildVotes(tier1Signals, features));

            // Cooldown: decrementa UNA vez por barra; bloquea la decisión
            if (_cooldownRemaining > 0)
            {
                _cooldownRemaining--;
                if (result.Direction != 0)
                {
                    result.Direction = 0;
                    result.Contracts = 0;
                    result.Source = string.Format("COOLDOWN ({0} bars left)", _cooldownRemaining + 1);
                }
            }

            // Decisión con contratos → reset del buffer + cooldown
            if (result.Direction != 0 && result.Contracts > 0)
            {
                _signalBuffer.Clear();
                _cooldownRemaining = TRADE_COOLDOWN_BARS;
            }

            if (currentBar >= 0)
            {
                _lastEvaluatedBar = currentBar;
                _lastDecisionCached = result;
            }
            return result;
        }

        private Dictionary<string, (int dir, double prob)> BuildVotes(
            Dictionary<string, int> tier1Signals, double[] features)
        {
            // Step A: señales frescas → buffer (solo detectores con modelo del lado)
            foreach (var kv in tier1Signals)
            {
                if (kv.Value == 0) continue;
                string modelKey;
                if (!_modelKeyMap.TryGetValue(ModelKey(kv.Key), out modelKey))
                {
                    WarnOnce("nomap:" + kv.Key, string.Format(
                        "[ML-VOTE-DEBUG] '{0}' disparó dir={1} pero no tiene meta-modelo.", kv.Key, kv.Value));
                    continue;
                }

                XGBoostEvaluator model = null;
                if (kv.Value > 0) _strategyModelsLong.TryGetValue(modelKey, out model);
                else              _strategyModelsShort.TryGetValue(modelKey, out model);
                if (model == null) continue;   // sin modelo de ese lado: voto descartado (= NaN en Python)

                double prob = model.PredictProba(features);

                if (_strategyGateEnabled)
                {
                    string side = kv.Value > 0 ? "long" : "short";
                    double minThr = _kellyZones.GetMinThreshold(modelKey, side);
                    if (prob < minThr)
                    {
                        if (_verboseLogging)
                            WarnOnce("gate:" + kv.Key + ":" + side, string.Format(
                                "[ML-GATE] '{0}' {1} prob={2:F3} < threshold={3:F3} — voto descartado (primer hit).",
                                kv.Key, side, prob, minThr));
                        continue;
                    }
                }
                _signalBuffer[kv.Key] = (kv.Value, prob, 0);
            }

            // Step B: votos vigentes (fresh + persistidos)
            var votes = new Dictionary<string, (int dir, double prob)>();
            var expired = new List<string>();
            foreach (var kv in _signalBuffer)
            {
                if (kv.Value.barAge <= SIGNAL_PERSIST_BARS) votes[kv.Key] = (kv.Value.dir, kv.Value.prob);
                else expired.Add(kv.Key);
            }

            // Step C: purgar caducados y envejecer
            foreach (var k in expired) _signalBuffer.Remove(k);
            foreach (var k in _signalBuffer.Keys.ToList())
            {
                var v = _signalBuffer[k];
                _signalBuffer[k] = (v.dir, v.prob, v.barAge + 1);
            }
            return votes;
        }

        private EnsembleDecision AggregateVotes(Dictionary<string, (int dir, double prob)> votes)
        {
            const string src = "Ensemble_B";
            if (votes.Count == 0)
                return new EnsembleDecision { Direction = 0, Source = "No votes" };

            var longs  = votes.Where(v => v.Value.dir > 0).ToList();
            var shorts = votes.Where(v => v.Value.dir < 0).ToList();

            // Empate → abstención (sin sesgo pro-long)
            if (longs.Count == shorts.Count && longs.Count >= _minVoters)
                return new EnsembleDecision
                {
                    Direction = 0,
                    Source = string.Format("{0} tie ({1}L={2}S)", src, longs.Count, shorts.Count)
                };

            int dir = 0; double aggP = 0;
            List<KeyValuePair<string, (int dir, double prob)>> winners = null;
            if (longs.Count >= _minVoters && longs.Count > shorts.Count) { dir = 1; winners = longs; }
            else if (shorts.Count >= _minVoters && shorts.Count > longs.Count) { dir = -1; winners = shorts; }

            if (winners != null) aggP = LogOddsMean(winners.Select(v => v.Value.prob));
            if (aggP < _probThreshold) dir = 0;

            if (dir != 0 && _verboseLogging)
            {
                Func<IEnumerable<KeyValuePair<string, (int dir, double prob)>>, string> fmt = kvs =>
                    string.Join(", ", kvs.OrderByDescending(v => v.Value.prob)
                                         .Select(v => string.Format("{0}:{1:F2}", v.Key, v.Value.prob)));
                EngineLog.Write(string.Format("[ML-VOTERS] {0} d={1} aggP={2:F3} | longs({3})=[{4}] | shorts({5})=[{6}]",
                    src, dir, aggP, longs.Count, fmt(longs), shorts.Count, fmt(shorts)));
            }

            string[] names = (dir != 0 && winners != null) ? winners.Select(v => v.Key).ToArray() : new string[0];
            return new EnsembleDecision
            {
                Direction    = dir,
                Probability  = aggP,
                Contracts    = dir != 0 ? _kellyZones.GetContractsGeneric(aggP) : 0,
                NAgree       = names.Length,
                Source       = string.Format("{0} ({1} agree)", src, names.Length),
                ActiveStrats = names
            };
        }

        /// <summary>Media de log-odds (Naive Bayes bajo independencia).</summary>
        private static double LogOddsMean(IEnumerable<double> probs)
        {
            double sum = 0; int n = 0;
            foreach (var p in probs)
            {
                double pc = Math.Max(1e-6, Math.Min(1.0 - 1e-6, p));
                sum += Math.Log(pc / (1.0 - pc));
                n++;
            }
            if (n == 0) return 0.5;
            return 1.0 / (1.0 + Math.Exp(-sum / n));
        }

        private void WarnOnce(string key, string msg)
        {
            if (_diagReportedKeys.Add(key)) EngineLog.Write(msg);
        }

        // ═══ Configuración / diagnóstico ═══
        public void SetMinVoters(int minVoters) { _minVoters = Math.Max(1, Math.Min(8, minVoters)); }
        public int  MinVoters => _minVoters;
        public void SetStrategyGate(bool enabled) { _strategyGateEnabled = enabled; }
        public bool StrategyGateEnabled => _strategyGateEnabled;
        public void SetVerbose(bool verbose) { _verboseLogging = verbose; }

        /// <summary>True si cargó al menos un meta-modelo (fail-closed de RequireModels).</summary>
        public bool HasModels =>
            (_strategyModelsLong != null && _strategyModelsLong.Count > 0)
            || (_strategyModelsShort != null && _strategyModelsShort.Count > 0);

        public string GetDiagString()
        {
            return string.Format("Mode=Ensemble_B | Strats={0} ModelsL={1} ModelsS={2} | Strats=[{3}]",
                _activeStrategies.Length, _strategyModelsLong.Count, _strategyModelsShort.Count,
                string.Join(", ", _activeStrategies));
        }
    }

    // ═══════════════════════════════════════════════════════════
    // KELLY ZONE LOOKUP — kelly_thresholds.json → flat_lookup
    // ═══════════════════════════════════════════════════════════
    public class KellyZoneLookup
    {
        private readonly Dictionary<string, double[]> _zones;
        private static readonly double[] GenericZones = { 0.25, 0.55, 0.70 };

        public KellyZoneLookup(string modelsDir)
        {
            _zones = new Dictionary<string, double[]>(StringComparer.OrdinalIgnoreCase);
            string path = Path.Combine(modelsDir, "kelly_thresholds.json");
            if (!File.Exists(path)) return;
            try { _zones = ParseFlatLookup(File.ReadAllText(path)); }
            catch { /* sin tabla: gate permisivo */ }
        }

        /// <summary>
        /// zone1_threshold (= effective_used de step8) de strategy/side. Las
        /// strategy/side sin edge llevan el centinela 0.999 (bloqueadas).
        /// [2026-10-06] Clave ausente → 0.0, igual que zone1_threshold() de
        /// backtest_ensemble (antes 0.25: divergencia latente).
        /// </summary>
        public double GetMinThreshold(string strat, string side)
        {
            double[] z;
            string key = strat.Replace("@", "_").ToLowerInvariant() + "_" + side;
            return _zones.TryGetValue(key, out z) ? z[0] : 0.0;
        }

        public int GetContractsGeneric(double prob)
        {
            if (prob < GenericZones[0]) return 0;
            if (prob < GenericZones[1]) return 1;
            if (prob < GenericZones[2]) return 2;
            return 3;
        }

        /// <summary>Parsea "flat_lookup": {"key": [z1, z2, z3], ...}.</summary>
        private static Dictionary<string, double[]> ParseFlatLookup(string json)
        {
            var result = new Dictionary<string, double[]>(StringComparer.OrdinalIgnoreCase);
            const string section = "\"flat_lookup\"";
            int secIdx = json.IndexOf(section, StringComparison.Ordinal);
            if (secIdx < 0) return result;
            int braceStart = json.IndexOf('{', secIdx + section.Length);
            if (braceStart < 0) return result;
            int depth = 1, braceEnd = braceStart + 1;
            while (braceEnd < json.Length && depth > 0)
            {
                if (json[braceEnd] == '{') depth++;
                else if (json[braceEnd] == '}') depth--;
                braceEnd++;
            }
            string inner = json.Substring(braceStart + 1, braceEnd - braceStart - 2);
            int pos = 0;
            while (pos < inner.Length)
            {
                int q1 = inner.IndexOf('"', pos);
                if (q1 < 0) break;
                int q2 = inner.IndexOf('"', q1 + 1);
                if (q2 < 0) break;
                string key = inner.Substring(q1 + 1, q2 - q1 - 1);
                int arrStart = inner.IndexOf('[', q2);
                int arrEnd = arrStart < 0 ? -1 : inner.IndexOf(']', arrStart);
                if (arrEnd < 0) break;
                string[] parts = inner.Substring(arrStart + 1, arrEnd - arrStart - 1).Split(',');
                if (parts.Length >= 3)
                {
                    var arr = new double[3];
                    for (int i = 0; i < 3; i++)
                        double.TryParse(parts[i].Trim(), System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out arr[i]);
                    result[key] = arr;
                }
                pos = arrEnd + 1;
            }
            return result;
        }
    }
}
