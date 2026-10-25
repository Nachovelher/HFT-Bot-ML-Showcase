using System;
using System.Globalization;
using System.IO;

namespace HFT_BOT_ML.Tests
{
    /// <summary>
    /// Modelos XGBoost minimos (un stump) cuyo resultado se calcula a mano:
    ///   nodo 0: si f0 &lt; SPLIT -> nodo 1 (hoja -1.0), si no -> nodo 2 (hoja +1.0)
    /// </summary>
    internal static class XgbEvaluatorTests
    {
        private static string Inv(double d) { return d.ToString("R", CultureInfo.InvariantCulture); }
        private static double Sigmoid(double x) { return 1.0 / (1.0 + Math.Exp(-x)); }
        private static double Logit(double p) { return Math.Log(p / (1.0 - p)); }

        private static string ModelJson(string baseScore, int defaultLeft, double split, string treeExtra)
        {
            return "{\"learner\":{\"objective\":{\"name\":\"binary:logistic\"},"
                 + "\"learner_model_param\":{\"base_score\":\"" + baseScore + "\",\"num_feature\":\"2\"},"
                 + "\"gradient_booster\":{\"model\":{\"trees\":[{" + treeExtra
                 + "\"split_indices\":[0,0,0],\"split_conditions\":[" + Inv(split) + ",-1.0,1.0],"
                 + "\"left_children\":[1,-1,-1],\"right_children\":[2,-1,-1],"
                 + "\"default_left\":[" + defaultLeft + ",0,0]}]}}}}";
        }

        private static XGBoostEvaluator Load(string dir, string baseScore, int defaultLeft, double split,
                                             string treeExtra = "", string calJson = null)
        {
            string path = Path.Combine(dir, "m.json");
            File.WriteAllText(path, ModelJson(baseScore, defaultLeft, split, treeExtra));
            if (calJson != null) File.WriteAllText(Path.Combine(dir, "m_cal.json"), calJson);
            var ev = new XGBoostEvaluator(path, autoLoadCalibration: calJson != null);
            T.True(ev.IsLoaded, "el modelo debe cargar");
            return ev;
        }

        public static void Run()
        {
            T.Section("XGBoostEvaluator");

            T.Run("izquierda si x < umbral, derecha si x >= umbral", delegate
            {
                T.WithTempDir(delegate (string d)
                {
                    var ev = Load(d, "5E-1", 1, 0.5);
                    T.Near(Sigmoid(-1), ev.PredictProba(new[] { 0.3, 0.0 }), 1e-12, "x < umbral");
                    T.Near(Sigmoid(1), ev.PredictProba(new[] { 0.7, 0.0 }), 1e-12, "x > umbral");
                    T.Near(Sigmoid(1), ev.PredictProba(new[] { 0.5, 0.0 }), 1e-12, "x == umbral va a la derecha");
                });
            });

            T.Run("base_score (formato vector '[3.7E-1]') se aplica como logit", delegate
            {
                T.WithTempDir(delegate (string d)
                {
                    var ev = Load(d, "[3.7E-1]", 1, 0.5);
                    T.Near(Sigmoid(Logit(0.37) + 1.0), ev.PredictProba(new[] { 0.7, 0.0 }), 1e-12, "hoja +1");
                    T.Near(Sigmoid(Logit(0.37) - 1.0), ev.PredictProba(new[] { 0.3, 0.0 }), 1e-12, "hoja -1");
                });
            });

            T.Run("NaN sigue la direccion default_left de cada nodo", delegate
            {
                T.WithTempDir(delegate (string d)
                {
                    var left = Load(d, "5E-1", 1, 0.5);
                    T.Near(Sigmoid(-1), left.PredictProba(new[] { double.NaN, 0.0 }), 1e-12, "default_left=1");
                });
                T.WithTempDir(delegate (string d)
                {
                    var right = Load(d, "5E-1", 0, 0.5);
                    T.Near(Sigmoid(1), right.PredictProba(new[] { double.NaN, 0.0 }), 1e-12, "default_left=0");
                });
            });

            T.Run("compara en float32 como XGBoost (no en double)", delegate
            {
                T.WithTempDir(delegate (string d)
                {
                    // umbral = 0.1f exacto en double. En double 0.1 < 0.10000000149 iria a la
                    // izquierda; en float32 ambos valen 0.1f y NO es menor: va a la derecha.
                    var ev = Load(d, "5E-1", 1, (double)0.1f);
                    T.Near(Sigmoid(1), ev.PredictProba(new[] { 0.1, 0.0 }), 1e-12, "frontera float32");
                });
            });

            T.Run("calibracion isotonica interpola linealmente (np.interp)", delegate
            {
                T.WithTempDir(delegate (string d)
                {
                    const string cal = "{\"calibrators\":[{\"X_thresholds\":[0,0.5,1],\"y_thresholds\":[0,0.25,1]}]}";
                    var ev = Load(d, "5E-1", 1, 0.5, "", cal);
                    T.True(ev.HasCalibration, "debe cargar el sidecar _cal.json");
                    double raw = Sigmoid(1);                       // 0.7310...
                    double expected = 0.25 + (raw - 0.5) / 0.5 * 0.75;
                    T.Near(expected, ev.PredictProba(new[] { 0.7, 0.0 }), 1e-12, "interpolado");
                });
            });

            T.Run("valida el numero de features con error explicito", delegate
            {
                T.WithTempDir(delegate (string d)
                {
                    var ev = Load(d, "5E-1", 1, 0.5);
                    T.Throws<InvalidOperationException>(delegate { ev.Predict(new[] { 0.3 }); }, "pocas features");
                    T.Throws<InvalidOperationException>(delegate { ev.ValidateFeatureCount(3); }, "recuento distinto");
                    ev.ValidateFeatureCount(2);
                });
            });

            T.Run("un modelo con NaN/Infinity en campos auxiliares carga y predice igual", delegate
            {
                T.WithTempDir(delegate (string d)
                {
                    // XGBoost escribe NaN/Infinity sin comillas en arrays como sum_hessian.
                    // Antes el parser truncaba el JSON aqui y el arbol quedaba vacio.
                    var ev = Load(d, "5E-1", 1, 0.5, "\"sum_hessian\":[1.0,Infinity,NaN],");
                    T.Near(Sigmoid(1), ev.PredictProba(new[] { 0.7, 0.0 }), 1e-12, "hoja +1");
                    T.Near(Sigmoid(-1), ev.PredictProba(new[] { 0.3, 0.0 }), 1e-12, "hoja -1");
                });
            });
        }
    }
}
