using System;
using System.IO;

namespace HFT_BOT_ML.Tests
{
    internal static class KellyZoneTests
    {
        private static KellyZoneLookup WithFile(string dir, string json)
        {
            File.WriteAllText(Path.Combine(dir, "kelly_thresholds.json"), json);
            return new KellyZoneLookup(dir);
        }

        public static void Run()
        {
            T.Section("KellyZoneLookup");

            T.Run("lee umbrales por detector y lado; normaliza '@' y mayusculas", delegate
            {
                T.WithTempDir(delegate (string d)
                {
                    var k = WithFile(d, "{\"flat_lookup\":{\"a_long\":[0.3,0.55,0.7],\"htf_x_short\":[0.999,0.999,0.999]}}");
                    T.True(k.IsLoaded, "debe cargar: " + k.LoadError);
                    T.Near(0.3, k.GetMinThreshold("A", "long"), 0, "a_long");
                    T.Near(0.999, k.GetMinThreshold("HTF@X", "short"), 0, "htf_x_short");
                });
            });

            T.Run("clave ausente en una tabla valida -> 0.0 (paridad con Python)", delegate
            {
                T.WithTempDir(delegate (string d)
                {
                    var k = WithFile(d, "{\"flat_lookup\":{\"a_long\":[0.3,0.55,0.7]}}");
                    T.Near(0.0, k.GetMinThreshold("otro", "long"), 0, "clave ausente");
                });
            });

            T.Run("tabla ausente -> bloquea todo (fail-closed)", delegate
            {
                T.WithTempDir(delegate (string d)
                {
                    var k = new KellyZoneLookup(d);
                    T.True(!k.IsLoaded, "no debe figurar como cargada");
                    T.True(k.LoadError.Length > 0, "debe explicar el motivo");
                    T.True(k.GetMinThreshold("a", "long") >= KellyZoneLookup.BlockedThreshold, "umbral bloqueante");
                });
            });

            T.Run("tabla corrupta, vacia o sin flat_lookup -> bloquea todo", delegate
            {
                foreach (string json in new[] { "{not json", "{}", "{\"flat_lookup\":{}}" })
                {
                    string j = json;
                    T.WithTempDir(delegate (string d)
                    {
                        var k = WithFile(d, j);
                        T.True(!k.IsLoaded, "no debe cargar: " + j);
                        T.True(k.GetMinThreshold("a", "long") >= KellyZoneLookup.BlockedThreshold, "bloqueante: " + j);
                    });
                }
            });

            T.Run("entrada ilegible bloquea solo esa clave", delegate
            {
                T.WithTempDir(delegate (string d)
                {
                    var k = WithFile(d, "{\"flat_lookup\":{\"ok_long\":[0.3,0.5,0.7],\"bad_long\":[abc,0.5,0.7],"
                                      + "\"corta_long\":[0.3],\"nan_long\":[NaN,0.5,0.7]}}");
                    T.True(k.IsLoaded, "la tabla en conjunto es valida");
                    T.Near(0.3, k.GetMinThreshold("ok", "long"), 0, "entrada correcta");
                    T.True(k.GetMinThreshold("bad", "long") >= KellyZoneLookup.BlockedThreshold, "no numerica");
                    T.True(k.GetMinThreshold("corta", "long") >= KellyZoneLookup.BlockedThreshold, "menos de 3 valores");
                    T.True(k.GetMinThreshold("nan", "long") >= KellyZoneLookup.BlockedThreshold, "NaN");
                });
            });

            T.Run("contratos genericos por zonas de probabilidad (0 / 1 / 2 / 3)", delegate
            {
                T.WithTempDir(delegate (string d)
                {
                    var k = new KellyZoneLookup(d);   // las zonas genericas no dependen del fichero
                    T.Eq(0, k.GetContractsGeneric(0.10), "0.10");
                    T.Eq(1, k.GetContractsGeneric(0.25), "0.25");
                    T.Eq(1, k.GetContractsGeneric(0.549), "0.549");
                    T.Eq(2, k.GetContractsGeneric(0.55), "0.55");
                    T.Eq(2, k.GetContractsGeneric(0.699), "0.699");
                    T.Eq(3, k.GetContractsGeneric(0.70), "0.70");
                });
            });
        }
    }
}
