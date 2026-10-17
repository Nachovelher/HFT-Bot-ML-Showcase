/*
=============================================================
Parity Harness — evalúa un modelo XGBoost con el evaluador
C# REAL del bot (XGBoostEvaluator de HFT_BOT_ML_Common.cs).
=============================================================
Uso:
  ParityHarness <model.json> <features.csv> [out.csv] [--no-cal]

  features.csv : sin header, una fila por muestra, columnas
                 separadas por coma, "nan" para missing.
  out.csv      : "probability,raw_probability,margin" por fila
                 (si se omite, escribe a stdout).
  --no-cal     : no cargar el sidecar <model>_cal.json.

Lo invoca tools/validate_prediction_parity.py — no está pensado
para uso manual, pero funciona standalone.
=============================================================
*/

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace HFT_BOT_ML
{
    public static class ParityHarnessProgram
    {
        public static int Main(string[] args)
        {
            // Modos offline (OfflineModes.cs): features / signals
            if (args.Length > 0 && args[0] == "features") return OfflineModes.Features(args);
            if (args.Length > 0 && args[0] == "signals")  return OfflineModes.Signals(args);

            if (args.Length < 2)
            {
                Console.Error.WriteLine(
                    "uso: ParityHarness <model.json> <features.csv> [out.csv] [--no-cal]");
                return 2;
            }

            string modelPath = args[0];
            string csvPath   = args[1];
            string outPath   = null;
            bool   noCal     = false;

            for (int i = 2; i < args.Length; i++)
            {
                if (args[i] == "--no-cal") noCal = true;
                else outPath = args[i];
            }

            if (!File.Exists(modelPath))
            {
                Console.Error.WriteLine("ERROR: modelo no encontrado: " + modelPath);
                return 3;
            }
            if (!File.Exists(csvPath))
            {
                Console.Error.WriteLine("ERROR: csv no encontrado: " + csvPath);
                return 3;
            }

            var ev = new XGBoostEvaluator(modelPath, autoLoadCalibration: !noCal);
            if (!ev.IsLoaded)
            {
                Console.Error.WriteLine("ERROR: el modelo no cargó: " + modelPath);
                return 4;
            }

            var lines = File.ReadAllLines(csvPath);
            var results = new List<string>(lines.Length);

            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var parts = line.Split(',');
                var feats = new double[parts.Length];
                for (int j = 0; j < parts.Length; j++)
                {
                    string p = parts[j].Trim();
                    if (p.Length == 0 || p == "nan" || p == "NaN" || p == "NAN")
                        feats[j] = double.NaN;
                    else
                        feats[j] = double.Parse(p, NumberStyles.Float,
                                                CultureInfo.InvariantCulture);
                }

                var pred = ev.Predict(feats);
                results.Add(string.Format(CultureInfo.InvariantCulture,
                    "{0:R},{1:R},{2:R}",
                    pred.Probability, pred.RawProbability, pred.RawScore));
            }

            if (outPath != null)
                File.WriteAllLines(outPath, results);
            else
                foreach (var r in results) Console.WriteLine(r);

            Console.Error.WriteLine(string.Format(
                "[ParityHarness] {0} filas evaluadas | cal={1} | feature_count={2}",
                results.Count, ev.HasCalibration, ev.ModelFeatureCount));
            return 0;
        }
    }
}
