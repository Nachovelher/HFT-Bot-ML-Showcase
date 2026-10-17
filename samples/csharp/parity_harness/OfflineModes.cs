/*
=============================================================
Parity Harness — modos OFFLINE (sin NinjaTrader)
=============================================================
Ejecutan el código C# REAL del bot sobre un CSV OHLCV del TF
(el mismo que usa el pipeline Python) para comparar contra la
referencia Python sin pasar por un replay de NT8:

  ParityHarness features <tf.csv> <out.csv> [--tz <WindowsTzId>]
      FullFeatureExtractor barra a barra → 173 features por barra.

  ParityHarness signals  <tf.csv> <out.csv> [--tz <WindowsTzId>]
      Tier1StrategyDetectors barra a barra → señal de cada detector.

Formato del CSV de entrada (data/resampled/*_<TF>.csv):
  datetime,open,high,low,close,volume[,close_raw,high_raw,low_raw]
  con datetime en UTC (sufijo +00:00). Barras etiquetadas al CIERRE
  (resample label='right', closed='right'), igual que Time[0] en NT8.

--tz emula lo que pasaba el bot ANTES del fix de zona horaria
(Time[0] en hora local del PC): convierte cada timestamp UTC a esa
zona antes de entregarlo al código C#. Sin --tz se entrega UTC, que
es lo que el bot entrega tras el fix (conversión con la zona de NT8).

Lo invoca tools/parity_offline.py.
=============================================================
*/

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace HFT_BOT_ML
{
    internal sealed class HarnessBar
    {
        public DateTime Utc;
        public double O, H, L, C, V;
        public double HRaw, LRaw, CRaw;
    }

    internal static class HarnessCsv
    {
        public static List<HarnessBar> Load(string path)
        {
            var bars = new List<HarnessBar>();
            using (var sr = new StreamReader(path))
            {
                string header = sr.ReadLine();
                if (header == null) return bars;
                var cols = header.Split(',');
                int iT = Find(cols, "datetime"), iO = Find(cols, "open"), iH = Find(cols, "high");
                int iL = Find(cols, "low"), iC = Find(cols, "close"), iV = Find(cols, "volume");
                int iHr = Find(cols, "high_raw"), iLr = Find(cols, "low_raw"), iCr = Find(cols, "close_raw");
                if (iT < 0 || iO < 0 || iH < 0 || iL < 0 || iC < 0 || iV < 0)
                    throw new InvalidDataException("CSV sin columnas datetime/open/high/low/close/volume: " + path);

                string line;
                while ((line = sr.ReadLine()) != null)
                {
                    if (line.Length == 0) continue;
                    var p = line.Split(',');
                    var b = new HarnessBar
                    {
                        Utc = DateTimeOffset.Parse(p[iT], CultureInfo.InvariantCulture,
                                  DateTimeStyles.AssumeUniversal).UtcDateTime,
                        O = D(p[iO]), H = D(p[iH]), L = D(p[iL]), C = D(p[iC]), V = D(p[iV]),
                    };
                    b.HRaw = iHr >= 0 ? D(p[iHr]) : b.H;
                    b.LRaw = iLr >= 0 ? D(p[iLr]) : b.L;
                    b.CRaw = iCr >= 0 ? D(p[iCr]) : b.C;
                    if (double.IsNaN(b.O) || double.IsNaN(b.H) || double.IsNaN(b.L)
                        || double.IsNaN(b.C) || double.IsNaN(b.V))
                        continue;   // = dropna de load_ohlcv
                    bars.Add(b);
                }
            }
            bars.Sort((a, b) => a.Utc.CompareTo(b.Utc));
            return bars;
        }

        private static int Find(string[] cols, string name)
        {
            for (int i = 0; i < cols.Length; i++)
                if (string.Equals(cols[i].Trim(), name, StringComparison.OrdinalIgnoreCase)) return i;
            return -1;
        }

        private static double D(string s)
        {
            s = s.Trim();
            if (s.Length == 0 || s.Equals("nan", StringComparison.OrdinalIgnoreCase)) return double.NaN;
            return double.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);
        }

        public static string F(double v)
        {
            return double.IsNaN(v) ? "nan" : v.ToString("R", CultureInfo.InvariantCulture);
        }

        public static DateTime ToBotTime(DateTime utc, TimeZoneInfo tz)
        {
            return tz == null ? utc : TimeZoneInfo.ConvertTimeFromUtc(utc, tz);
        }

        public static TimeZoneInfo ParseTz(string[] args)
        {
            for (int i = 0; i < args.Length - 1; i++)
                if (args[i] == "--tz") return TimeZoneInfo.FindSystemTimeZoneById(args[i + 1]);
            return null;
        }
    }

    internal static class OfflineModes
    {
        public static int Features(string[] args)
        {
            if (args.Length < 3)
            {
                Console.Error.WriteLine("uso: ParityHarness features <tf.csv> <out.csv> [--tz <id>]");
                return 2;
            }
            var bars = HarnessCsv.Load(args[1]);
            var tz = HarnessCsv.ParseTz(args);
            var fx = new FullFeatureExtractor(60);
            int written = 0;
            using (var w = new StreamWriter(args[2], false, new UTF8Encoding(false)))
            {
                var sb = new StringBuilder("datetime");
                for (int j = 0; j < FullFeatureExtractor.FEATURE_COUNT; j++) sb.Append(",f").Append(j);
                w.WriteLine(sb.ToString());
                foreach (var b in bars)
                {
                    DateTime t = HarnessCsv.ToBotTime(b.Utc, tz);
                    fx.Update(b.O, b.H, b.L, b.C, b.V, t);
                    if (!fx.IsReady) continue;
                    double[] f = fx.GetFeatures(t);
                    sb.Clear();
                    sb.Append(b.Utc.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
                    for (int j = 0; j < f.Length; j++) sb.Append(',').Append(HarnessCsv.F(f[j]));
                    w.WriteLine(sb.ToString());
                    written++;
                }
            }
            Console.Error.WriteLine(string.Format("[ParityHarness] features: {0} barras leídas, {1} escritas (tz={2})",
                bars.Count, written, tz == null ? "UTC" : tz.Id));
            return 0;
        }

        public static int Signals(string[] args)
        {
            Console.Error.WriteLine("signals: pendiente");
            return 2;
        }
    }
}
