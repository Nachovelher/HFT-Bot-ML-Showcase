using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace HFT_BOT_ML.Tests
{
    /// <summary>
    /// Motor de decision con meta-modelos de probabilidad CONSTANTE (un arbol de una
    /// sola hoja = 0.0, asi que prob = base_score). Detectores ficticios DetA..DetD:
    ///   DetA 0.6   DetB 0.8   DetC 0.6   DetD 0.8   (mismo valor en largo y corto)
    /// </summary>
    internal static class EnsembleEngineTests
    {
        private static double Sigmoid(double x) { return 1.0 / (1.0 + Math.Exp(-x)); }
        private static double Logit(double p) { return Math.Log(p / (1.0 - p)); }

        private static string ConstModel(double p)
        {
            return "{\"learner\":{\"objective\":{\"name\":\"binary:logistic\"},"
                 + "\"learner_model_param\":{\"base_score\":\"" + p.ToString("R", CultureInfo.InvariantCulture)
                 + "\",\"num_feature\":\"2\"},"
                 + "\"gradient_booster\":{\"model\":{\"trees\":[{\"split_indices\":[0],\"split_conditions\":[0.0],"
                 + "\"left_children\":[-1],\"right_children\":[-1],\"default_left\":[0]}]}}}}";
        }

        private static void WriteModels(string dir, string kellyJson)
        {
            var probs = new Dictionary<string, double> { { "deta", 0.6 }, { "detb", 0.8 }, { "detc", 0.6 }, { "detd", 0.8 } };
            foreach (var kv in probs)
            {
                File.WriteAllText(Path.Combine(dir, "meta_long_" + kv.Key + ".json"), ConstModel(kv.Value));
                File.WriteAllText(Path.Combine(dir, "meta_short_" + kv.Key + ".json"), ConstModel(kv.Value));
            }
            if (kellyJson != null) File.WriteAllText(Path.Combine(dir, "kelly_thresholds.json"), kellyJson);
            Tier1StrategyDetectors.ActiveDetectorNames = new[] { "DetA", "DetB", "DetC", "DetD" };
        }

        private static Dictionary<string, int> Signals(params object[] pairs)
        {
            var d = new Dictionary<string, int>();
            for (int i = 0; i < pairs.Length; i += 2) d[(string)pairs[i]] = (int)pairs[i + 1];
            return d;
        }

        private static readonly double[] Feat = new double[2];

        public static void Run()
        {
            T.Section("EnsembleDecisionEngine");

            T.Run("un solo votante no alcanza minVoters=2", delegate
            {
                T.WithTempDir(delegate (string d)
                {
                    WriteModels(d, null);
                    var e = new EnsembleDecisionEngine(d);
                    T.True(e.HasModels, "debe cargar los meta-modelos");
                    T.Eq(0, e.Evaluate(Feat, Signals("DetA", 1), 1).Direction, "direccion");
                });
            });

            T.Run("dos votantes largos -> compra; prob = media de log-odds; contratos por zona", delegate
            {
                T.WithTempDir(delegate (string d)
                {
                    WriteModels(d, null);
                    var e = new EnsembleDecisionEngine(d);
                    var r = e.Evaluate(Feat, Signals("DetA", 1, "DetB", 1), 1);
                    double expected = Sigmoid((Logit(0.6) + Logit(0.8)) / 2.0);   // ~0.7101
                    T.Eq(1, r.Direction, "direccion");
                    T.Eq(2, r.NAgree, "votantes");
                    T.Near(expected, r.Probability, 1e-9, "probabilidad agregada");
                    T.Eq(3, r.Contracts, "0.71 >= 0.70 -> 3 contratos");
                });
            });

            T.Run("empate entre largos y cortos -> abstencion", delegate
            {
                T.WithTempDir(delegate (string d)
                {
                    WriteModels(d, null);
                    var e = new EnsembleDecisionEngine(d);
                    var r = e.Evaluate(Feat, Signals("DetA", 1, "DetB", 1, "DetC", -1, "DetD", -1), 1);
                    T.Eq(0, r.Direction, "direccion");
                    T.Eq(0, r.Contracts, "contratos");
                });
            });

            T.Run("cooldown: 5 barras bloqueadas tras operar y la sexta vuelve a operar", delegate
            {
                T.WithTempDir(delegate (string d)
                {
                    WriteModels(d, null);
                    var e = new EnsembleDecisionEngine(d);
                    var sig = Signals("DetA", 1, "DetB", 1);
                    T.Eq(1, e.Evaluate(Feat, sig, 1).Direction, "barra 1 opera");
                    for (int bar = 2; bar <= 6; bar++)
                    {
                        var r = e.Evaluate(Feat, sig, bar);
                        T.Eq(0, r.Direction, "barra " + bar + " en cooldown");
                        T.True(r.Source.StartsWith("COOLDOWN"), "origen de la barra " + bar + ": " + r.Source);
                    }
                    T.Eq(1, e.Evaluate(Feat, sig, 7).Direction, "barra 7 opera otra vez");
                });
            });

            T.Run("una evaluacion por barra: repetir currentBar devuelve la decision cacheada", delegate
            {
                T.WithTempDir(delegate (string d)
                {
                    WriteModels(d, null);
                    var e = new EnsembleDecisionEngine(d);
                    var sig = Signals("DetA", 1, "DetB", 1);
                    var first = e.Evaluate(Feat, sig, 5);
                    var again = e.Evaluate(Feat, sig, 5);
                    T.True(ReferenceEquals(first, again), "misma decision, sin reevaluar ni envejecer el buffer");
                });
            });

            T.Run("strategy gate: descarta votos por debajo del umbral kelly", delegate
            {
                T.WithTempDir(delegate (string d)
                {
                    // deta_long exige 0.7 (su prob es 0.6 -> descartado); detb_long exige 0.5 (0.8 -> pasa)
                    WriteModels(d, "{\"flat_lookup\":{\"deta_long\":[0.7,0.8,0.9],\"detb_long\":[0.5,0.6,0.7]}}");
                    var sig = Signals("DetA", 1, "DetB", 1);

                    var gated = new EnsembleDecisionEngine(d);
                    gated.SetStrategyGate(true);
                    T.Eq(0, gated.Evaluate(Feat, sig, 1).Direction, "con gate queda 1 votante < minVoters");

                    var open = new EnsembleDecisionEngine(d);
                    open.SetStrategyGate(false);
                    T.Eq(1, open.Evaluate(Feat, sig, 1).Direction, "sin gate votan los dos");
                });
            });

            T.Run("strategy gate con kelly_thresholds.json ausente -> no opera (fail-closed)", delegate
            {
                T.WithTempDir(delegate (string d)
                {
                    WriteModels(d, null);   // sin tabla de Kelly
                    var e = new EnsembleDecisionEngine(d);
                    e.SetStrategyGate(true);
                    T.Eq(0, e.Evaluate(Feat, Signals("DetA", 1, "DetB", 1), 1).Direction, "gate sin tabla");
                });
            });
        }
    }
}
