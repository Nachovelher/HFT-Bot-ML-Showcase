using System;
using System.Linq;
using System.Text;

namespace HFT_BOT_ML.Tests
{
    internal static class MiniJsonTests
    {
        public static void Run()
        {
            T.Section("MiniJson");

            T.Run("parsea objetos, arrays, strings con escapes, bool, null y numeros", delegate
            {
                var o = JObject.Parse("{\"a\":1,\"b\":[1,2,3],\"c\":{\"d\":\"x\\u0041\\n\"},"
                                    + "\"e\":true,\"f\":null,\"g\":-1.5e2}");
                T.Near(1, o["a"].Value<double>(), 0, "a");
                T.Eq(3, o["b"].AsJArray().Count, "longitud de b");
                T.Eq("xA\n", o["c"]["d"].Value<string>(), "string con escapes");
                T.True(o["e"].Value<bool>(), "bool true");
                T.True(o["f"].IsNull, "null");
                T.Near(-150, o["g"].Value<double>(), 1e-12, "exponente");
            });

            T.Run("bool[] desde 0/1 (default_left de XGBoost) y desde true/false", delegate
            {
                bool[] a = JObject.Parse("{\"x\":[1,0,1]}")["x"].ToObject<bool[]>();
                T.True(a[0] && !a[1] && a[2], "0/1");
                bool[] b = JObject.Parse("{\"x\":[true,false,true]}")["x"].ToObject<bool[]>();
                T.True(b[0] && !b[1] && b[2], "true/false");
            });

            T.Run("array largo de booleanos se lee entero y en orden", delegate
            {
                var sb = new StringBuilder("{\"x\":[");
                for (int i = 0; i < 5000; i++) sb.Append(i == 0 ? "" : ",").Append(i % 2 == 0 ? "true" : "false");
                sb.Append("]}");
                bool[] a = JObject.Parse(sb.ToString())["x"].ToObject<bool[]>();
                T.Eq(5000, a.Length, "longitud");
                T.True(Enumerable.Range(0, 5000).All(i => a[i] == (i % 2 == 0)), "orden y valores");
            });

            T.Run("acepta NaN, Infinity y -Infinity (formato de XGBoost/Python)", delegate
            {
                double[] a = JObject.Parse("{\"a\":[1,NaN,Infinity,-Infinity,2]}")["a"].ToObject<double[]>();
                T.Eq(5, a.Length, "no debe truncar el array");
                T.True(double.IsNaN(a[1]), "NaN");
                T.True(double.IsPositiveInfinity(a[2]), "Infinity");
                T.True(double.IsNegativeInfinity(a[3]), "-Infinity");
                T.Near(2, a[4], 0, "el valor posterior al literal se conserva");
            });

            T.Run("JSON malformado falla con FormatException en vez de truncar en silencio", delegate
            {
                T.Throws<FormatException>(delegate { JObject.Parse("{\"a\":[1,@,2]}"); }, "caracter invalido");
                T.Throws<FormatException>(delegate { JObject.Parse("{\"a\":tru}"); }, "true truncado");
                T.Throws<FormatException>(delegate { JObject.Parse("{\"a\":nul}"); }, "null truncado");
            });
        }
    }
}
