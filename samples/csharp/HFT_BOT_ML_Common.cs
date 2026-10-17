/*
=============================================================
HFT_BOT_ML — Common Types
=============================================================
Shared classes used across all ML pipeline C# files.
Must be in: NinjaTrader 8/bin/Custom/HFT_BOT_ML/
=============================================================
*/

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace HFT_BOT_ML
{
    // ─────────────────────────────────────────────────────────
    // ROLLING BUFFER — circular buffer with stats
    // ─────────────────────────────────────────────────────────

    public class RollingBuffer
    {
        private readonly double[] _data;
        private readonly int _capacity;
        private int _head;
        private int _count;

        public int Count => _count;
        public int Capacity => _capacity;

        public RollingBuffer(int capacity)
        {
            _capacity = capacity;
            _data = new double[capacity];
        }

        public void Push(double value)
        {
            _data[_head] = value;
            _head = (_head + 1) % _capacity;
            if (_count < _capacity) _count++;
        }

        /// <summary>Get value at index (0 = most recent)</summary>
        public double GetAt(int barsAgo)
        {
            if (barsAgo >= _count) return 0;
            int idx = (_head - 1 - barsAgo + _capacity) % _capacity;
            return _data[idx];
        }

        public double Mean(int n)
        {
            int use = Math.Min(n, _count);
            if (use == 0) return 0;
            double sum = 0;
            for (int i = 0; i < use; i++) sum += GetAt(i);
            return sum / use;
        }

        public double Std(int n)
        {
            int use = Math.Min(n, _count);
            if (use < 2) return 0;
            double mean = Mean(use);
            double sumSq = 0;
            for (int i = 0; i < use; i++)
            {
                double d = GetAt(i) - mean;
                sumSq += d * d;
            }
            return Math.Sqrt(sumSq / use);
        }

        public double RollingMax(int n)
        {
            int use = Math.Min(n, _count);
            if (use == 0) return double.MinValue;
            double max = double.MinValue;
            for (int i = 0; i < use; i++)
                max = Math.Max(max, GetAt(i));
            return max;
        }

        public double RollingMin(int n)
        {
            int use = Math.Min(n, _count);
            if (use == 0) return double.MaxValue;
            double min = double.MaxValue;
            for (int i = 0; i < use; i++)
                min = Math.Min(min, GetAt(i));
            return min;
        }
    }


    // ─────────────────────────────────────────────────────────
    // FEATURE STAT — for feature clipping/normalization
    // ─────────────────────────────────────────────────────────

    public class FeatureStat
    {
        public double Mean { get; set; }
        public double Std  { get; set; }
        public double P1   { get; set; }
        public double P99  { get; set; }
    }


    // ─────────────────────────────────────────────────────────
    // ISOTONIC CALIBRATOR — sidecar <model>_cal.json
    // [2026-10-06] Movido aquí desde step2b_XGBoostRegimeEvaluator.cs (fichero
    // retirado: el evaluador por régimen no lo usaba nadie). Equivale a
    // np.interp(raw, X_thresholds, y_thresholds) de backtest_ensemble.
    // ─────────────────────────────────────────────────────────

    internal class IsotonicCalibrator
    {
        private double[] _xThresholds;
        private double[] _yThresholds;
        public bool IsLoaded => _xThresholds != null && _xThresholds.Length > 0;

        public void Load(JObject calData)
        {
            try
            {
                var cals = calData["calibrators"].AsJArray();
                if (cals == null || cals.Count == 0) return;
                // Primer calibrador (fold único en cv="prefit")
                var first = cals[0].AsJObject();
                _xThresholds = first["X_thresholds"].ToObject<double[]>();
                _yThresholds = first["y_thresholds"].ToObject<double[]>();
            }
            catch { /* sin calibración → usar raw */ }
        }

        /// <summary>Interpolación lineal sobre la curva isotónica.</summary>
        public double Calibrate(double rawProb)
        {
            if (!IsLoaded) return rawProb;
            if (rawProb <= _xThresholds[0])
                return _yThresholds[0];
            if (rawProb >= _xThresholds[_xThresholds.Length - 1])
                return _yThresholds[_yThresholds.Length - 1];

            int lo = 0, hi = _xThresholds.Length - 1;
            while (lo < hi - 1)
            {
                int mid = (lo + hi) / 2;
                if (_xThresholds[mid] <= rawProb) lo = mid;
                else hi = mid;
            }
            double t = (_xThresholds[hi] - _xThresholds[lo]) > 1e-10
                ? (rawProb - _xThresholds[lo]) / (_xThresholds[hi] - _xThresholds[lo])
                : 0;
            return _yThresholds[lo] + t * (_yThresholds[hi] - _yThresholds[lo]);
        }
    }


    // ─────────────────────────────────────────────────────────
    // XGBOOST PREDICTION RESULT
    // ─────────────────────────────────────────────────────────

    public class XGBPrediction
    {
        public double RawScore   { get; set; }
        public double Probability { get; set; }       // calibrada si hay _cal.json cargado
        public double RawProbability { get; set; }    // sigmoid(margin), sin calibrar
        public int    Label       { get; set; }  // 0 or 1
    }


    // ─────────────────────────────────────────────────────────
    // XGBOOST EVALUATOR — loads JSON model, predicts
    // ─────────────────────────────────────────────────────────

    public class XGBoostEvaluator
    {
        private string _name;
        private List<XGBTree> _trees;
        private double _baseScore = 0.5;
        private double _baseMargin = 0.0;   // logit(base_score) para binary:logistic — se SUMA a las hojas
        private string _objective = "binary:logistic";
        private string[] _featureNames;
        private Dictionary<string, FeatureStat> _featureStats;
        private bool _loaded;
        private int _modelFeatureCount;  // Sprint 3.2: nº mínimo de features que este modelo necesita
        private IsotonicCalibrator _calibrator;  // sidecar <model>_cal.json (opcional)

        public bool IsLoaded => _loaded;
        public string Name => _name;
        /// <summary>Nº de features que el modelo requiere (tomado de num_feature en JSON, o del max split index + 1).</summary>
        public int ModelFeatureCount => _modelFeatureCount;
        /// <summary>True si hay calibración isotónica cargada (Predict.Probability ya sale calibrada).</summary>
        public bool HasCalibration => _calibrator != null && _calibrator.IsLoaded;

        // ── Constructors ─────────────────────────────────────

        public XGBoostEvaluator(string nameOrPath, bool autoLoadCalibration = false)
        {
            _name = nameOrPath;
            _trees = new List<XGBTree>();

            // If path ends with .json, auto-load
            if (nameOrPath.EndsWith(".json") && File.Exists(nameOrPath))
            {
                LoadModel(nameOrPath);
                if (autoLoadCalibration)
                    LoadCalibration(nameOrPath.Substring(0, nameOrPath.Length - ".json".Length) + "_cal.json");
            }
        }

        /// <summary>
        /// Carga la calibración isotónica exportada por el pipeline (_cal.json).
        /// Los thresholds de step8/kelly_thresholds se calcularon sobre
        /// probabilidades CALIBRADAS — sin esto, el runtime compara probs crudas
        /// contra umbrales de escala calibrada. Idempotente y tolerante a fallos:
        /// si el archivo no existe o es inválido, se sigue sin calibrar (con log).
        /// </summary>
        public void LoadCalibration(string calPath)
        {
            if (!File.Exists(calPath)) return;
            try
            {
                var cal = new IsotonicCalibrator();
                cal.Load(JObject.Parse(File.ReadAllText(calPath)));
                if (cal.IsLoaded)
                {
                    _calibrator = cal;
                    Console.WriteLine($"[XGB-{_name}] Calibración isotónica cargada: {Path.GetFileName(calPath)}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[XGB-{_name}] Cal load error ({Path.GetFileName(calPath)}): {ex.Message}");
            }
        }

        // ── Load model + meta ────────────────────────────────

        public void Load(string modelPath, string metaPath)
        {
            LoadModel(modelPath);

            if (File.Exists(metaPath))
            {
                try
                {
                    var meta = JObject.Parse(File.ReadAllText(metaPath));
                    _featureNames = meta["feature_names"]?.ToObject<string[]>();
                    _featureStats = new Dictionary<string, FeatureStat>();

                    var stats = meta["feature_stats"].AsJObject();
                    if (stats != null)
                    {
                        foreach (var kv in stats)
                        {
                            var s = kv.Value;
                            _featureStats[kv.Key] = new FeatureStat
                            {
                                Mean = s["mean"]?.Value<double>() ?? 0,
                                Std  = s["std"]?.Value<double>()  ?? 1,
                                P1   = s["p1"]?.Value<double>()   ?? -10,
                                P99  = s["p99"]?.Value<double>()  ?? 10
                            };
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[XGB-{_name}] Meta load error: {ex.Message}");
                }
            }
        }

        private void LoadModel(string modelPath)
        {
            if (!File.Exists(modelPath))
            {
                Console.WriteLine($"[XGB-{_name}] Model not found: {modelPath}");
                return;
            }

            try
            {
                var root = JObject.Parse(File.ReadAllText(modelPath));

                // XGBoost JSON format: learner.gradient_booster.model.trees[]
                var learner = root["learner"];
                if (learner == null)
                {
                    Console.WriteLine($"[XGB-{_name}] Invalid model format (no learner)");
                    return;
                }

                // Objective (necesario para interpretar base_score)
                var objectiveNode = learner["objective"];
                if (objectiveNode?["name"] != null)
                    _objective = objectiveNode["name"].Value<string>();

                // Base score → base margin.
                // XGBoost lo guarda en learner_model_param como string, y desde
                // la v2.x en formato vector: "[3.703302E-1]". Para binary:logistic
                // el valor está en espacio de PROBABILIDAD y el margen inicial es
                // logit(base_score). El bug histórico era ignorarlo por completo
                // ("base_score already accounted in trees" — falso): con
                // base_score=0.37 faltaba un offset de −0.53 en logit y TODAS las
                // probabilidades salían infladas.
                var objNode = learner["learner_model_param"];
                if (objNode?["base_score"] != null)
                {
                    double parsed;
                    if (TryParseBaseScore(objNode["base_score"], out parsed))
                        _baseScore = parsed;
                }
                if (_objective == "binary:logistic" || _objective == "reg:logistic")
                {
                    double p = Math.Max(1e-15, Math.Min(1.0 - 1e-15, _baseScore));
                    _baseMargin = Math.Log(p / (1.0 - p));
                }
                else
                {
                    // Para objetivos de regresión el base_score ya es el margen.
                    _baseMargin = _baseScore;
                }

                // Trees
                var gbtree = learner["gradient_booster"]?["model"];
                if (gbtree == null)
                {
                    Console.WriteLine($"[XGB-{_name}] No gradient_booster model found");
                    return;
                }

                var treesArray = gbtree["trees"].AsJArray();
                if (treesArray == null || treesArray.Count == 0)
                {
                    Console.WriteLine($"[XGB-{_name}] No trees found");
                    return;
                }

                _trees.Clear();
                foreach (JObject treeJson in treesArray)
                {
                    var tree = new XGBTree();
                    tree.Load(treeJson);
                    _trees.Add(tree);
                }

                // Sprint 3.2: determinar feature count del modelo.
                // XGBoost guarda `num_feature` dentro de learner_model_param; si no,
                // lo calculamos como max(split_indices)+1 sobre todos los árboles.
                int numFeatureDeclared = 0;
                if (objNode?["num_feature"] != null)
                {
                    try { numFeatureDeclared = objNode["num_feature"].Value<int>(); }
                    catch
                    {
                        // XGBoost a veces lo guarda como string.
                        int.TryParse(objNode["num_feature"].Value<string>(), out numFeatureDeclared);
                    }
                }
                int maxSplitIdx = -1;
                foreach (var t in _trees)
                    if (t.MaxFeatureIndex > maxSplitIdx) maxSplitIdx = t.MaxFeatureIndex;
                int minRequired = maxSplitIdx + 1;

                if (numFeatureDeclared > 0 && minRequired > numFeatureDeclared)
                {
                    Console.WriteLine(
                        $"[XGB-{_name}] WARN: num_feature declared={numFeatureDeclared} but trees split up to index {maxSplitIdx} → using {minRequired}");
                    _modelFeatureCount = minRequired;
                }
                else
                {
                    _modelFeatureCount = numFeatureDeclared > 0 ? numFeatureDeclared : minRequired;
                }

                _loaded = true;
                Console.WriteLine(
                    $"[XGB-{_name}] Loaded: {_trees.Count} trees, " +
                    $"base_score={_baseScore:F4} (margin={_baseMargin:F4}), " +
                    $"objective={_objective}, feature_count={_modelFeatureCount}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[XGB-{_name}] Load error: {ex.Message}");
            }
        }

        /// <summary>
        /// Parsea base_score de learner_model_param. Formatos posibles según
        /// versión de XGBoost: número JSON, string "0.5", o string vector
        /// "[3.703302E-1]" (v2.x+, num_target=1 → un solo valor).
        /// </summary>
        private static bool TryParseBaseScore(JToken token, out double value)
        {
            value = 0.5;
            if (token == null) return false;
            string s = token.ToString();
            if (string.IsNullOrEmpty(s)) return false;
            s = s.Trim().TrimStart('[').TrimEnd(']');
            int comma = s.IndexOf(',');
            if (comma >= 0) s = s.Substring(0, comma);
            return double.TryParse(s.Trim(),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out value);
        }


        // ── Prediction ───────────────────────────────────────

        /// <summary>
        /// Sprint 3.2: strict validation. Lanza si el array de features
        /// recibido no cubre lo que el modelo espera. Previene la clase de
        /// bug de la Fase 0 (C# enviaba 93 features cuando el modelo esperaba
        /// 173 → splits fuera de rango silenciosamente devolvían 0).
        /// </summary>
        public void ValidateFeatureCount(int expected)
        {
            if (!_loaded) return;   // sin modelo cargado no hay nada que validar
            if (expected != _modelFeatureCount)
            {
                throw new InvalidOperationException(string.Format(
                    "[XGB-{0}] Feature count mismatch: extractor provides {1} features, model requires {2}. " +
                    "Check FullFeatureExtractor.FEATURE_COUNT vs meta_meta.feature_count.",
                    _name, expected, _modelFeatureCount));
            }
        }

        public XGBPrediction Predict(double[] features)
        {
            if (!_loaded || _trees.Count == 0)
                return new XGBPrediction { RawScore = 0, Probability = 0.5, RawProbability = 0.5, Label = 0 };

            if (features == null)
                throw new ArgumentNullException(nameof(features));
            if (features.Length < _modelFeatureCount)
            {
                throw new InvalidOperationException(string.Format(
                    "[XGB-{0}] Predict: got {1} features but model needs at least {2}. " +
                    "Check FullFeatureExtractor.FEATURE_COUNT.",
                    _name, features.Length, _modelFeatureCount));
            }

            // margin = Σ hojas + logit(base_score). XGBoost NO incluye el
            // base_score en las hojas — es el sesgo global inicial del booster.
            double sum = _baseMargin;
            foreach (var tree in _trees)
                sum += tree.Predict(features);

            double rawScore = sum;
            double rawProb = Sigmoid(rawScore);

            // Calibración isotónica (si hay sidecar cargado). Replica el
            // CalibratedModel de Python: clip a [0.001, 0.999].
            double prob = rawProb;
            if (_calibrator != null && _calibrator.IsLoaded)
            {
                prob = _calibrator.Calibrate(rawProb);
                prob = Math.Max(0.001, Math.Min(0.999, prob));
            }

            return new XGBPrediction
            {
                RawScore       = rawScore,
                Probability    = prob,
                RawProbability = rawProb,
                Label          = prob >= 0.5 ? 1 : 0
            };
        }

        /// <summary>Convenience: return probability directly</summary>
        public double PredictProba(double[] features)
        {
            return Predict(features).Probability;
        }

        private static double Sigmoid(double x)
        {
            if (x > 30) return 1.0;
            if (x < -30) return 0.0;
            return 1.0 / (1.0 + Math.Exp(-x));
        }
    }


    // ─────────────────────────────────────────────────────────
    // XGBOOST TREE — single decision tree
    // ─────────────────────────────────────────────────────────

    internal class XGBTree
    {
        private int[] _splitFeatureId;
        private double[] _splitValue;
        private int[] _leftChild;
        private int[] _rightChild;
        private bool[] _defaultLeft;
        private bool[] _isLeaf;
        private double[] _leafValue;
        private int _nNodes;

        public void Load(JObject treeJson)
        {
            // XGBoost JSON tree format uses parallel arrays
            var splitIndices   = treeJson["split_indices"]?.ToObject<int[]>();
            var splitConditions = treeJson["split_conditions"]?.ToObject<double[]>();
            var leftChildren   = treeJson["left_children"]?.ToObject<int[]>();
            var rightChildren  = treeJson["right_children"]?.ToObject<int[]>();
            var defaultLeft    = treeJson["default_left"]?.ToObject<bool[]>();

            if (splitIndices == null || splitConditions == null ||
                leftChildren == null || rightChildren == null)
            {
                _nNodes = 0;
                return;
            }

            _nNodes = splitIndices.Length;
            _splitFeatureId = splitIndices;
            _splitValue     = splitConditions;
            _leftChild      = leftChildren;
            _rightChild     = rightChildren;
            _defaultLeft    = defaultLeft;   // dirección aprendida para missing/NaN por nodo

            _isLeaf    = new bool[_nNodes];
            _leafValue = new double[_nNodes];

            for (int i = 0; i < _nNodes; i++)
            {
                // In XGBoost, leaf nodes have left_child == -1
                _isLeaf[i] = _leftChild[i] == -1;
                if (_isLeaf[i])
                    _leafValue[i] = _splitValue[i]; // leaf value stored in split_conditions
            }
        }

        /// <summary>
        /// Sprint 3.2: Max split feature index del árbol (−1 si todos son leaves
        /// o el árbol está vacío). Lo usa XGBoostEvaluator.LoadModel para
        /// determinar el feature_count mínimo que necesita el modelo.
        /// </summary>
        public int MaxFeatureIndex
        {
            get
            {
                int max = -1;
                for (int i = 0; i < _nNodes; i++)
                {
                    if (_isLeaf[i]) continue;
                    if (_splitFeatureId[i] > max) max = _splitFeatureId[i];
                }
                return max;
            }
        }

        public double Predict(double[] features)
        {
            if (_nNodes == 0) return 0;

            int nodeIdx = 0;
            while (!_isLeaf[nodeIdx])
            {
                // Sprint 3.2: el guard defensivo que devolvía 0 cuando
                // fIdx >= features.Length ha sido eliminado. La validación
                // estricta ahora ocurre en XGBoostEvaluator.Predict antes
                // de llamar a este método. Si hay mismatch, el acceso al
                // array lanza IndexOutOfRangeException — fallo ruidoso en
                // lugar del fallo silencioso anterior.
                int fIdx = _splitFeatureId[nodeIdx];
                double val = features[fIdx];

                if (double.IsNaN(val))
                {
                    // NaN sigue la dirección default APRENDIDA por nodo
                    // (default_left del JSON). El bug histórico mandaba todo
                    // NaN a la izquierda, divergiendo de Python en cada nodo
                    // con default_left=false.
                    nodeIdx = (_defaultLeft == null || _defaultLeft[nodeIdx])
                        ? _leftChild[nodeIdx]
                        : _rightChild[nodeIdx];
                }
                else
                {
                    // Comparación en float32 — XGBoost castea entrada y
                    // thresholds a float internamente; comparar en double
                    // puede voltear el split en valores de frontera.
                    if ((float)val < (float)_splitValue[nodeIdx])
                        nodeIdx = _leftChild[nodeIdx];
                    else
                        nodeIdx = _rightChild[nodeIdx];
                }

                if (nodeIdx < 0 || nodeIdx >= _nNodes) return 0;
            }

            return _leafValue[nodeIdx];
        }
    }
}
