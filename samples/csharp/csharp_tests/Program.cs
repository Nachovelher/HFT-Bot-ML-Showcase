// Runner minimo de tests (sin NuGet): cada test es una lambda; un fallo es
// una excepcion. El proceso devuelve != 0 si algo falla (lo usa el CI).

using System;
using System.IO;

namespace HFT_BOT_ML.Tests
{
    internal static class T
    {
        private static int _passed;
        private static int _failed;

        public static void Run(string name, Action body)
        {
            try { body(); _passed++; Console.WriteLine("  ok    " + name); }
            catch (Exception ex)
            {
                _failed++;
                Console.WriteLine("  FAIL  " + name);
                Console.WriteLine("        " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        public static void Section(string title) { Console.WriteLine(); Console.WriteLine(title); }

        public static void True(bool cond, string msg)
        {
            if (!cond) throw new Exception(msg);
        }

        public static void Near(double expected, double actual, double tol, string msg)
        {
            if (double.IsNaN(expected) != double.IsNaN(actual) || Math.Abs(expected - actual) > tol)
                throw new Exception(string.Format("{0}: esperado {1:R}, obtenido {2:R}", msg, expected, actual));
        }

        public static void Eq<TV>(TV expected, TV actual, string msg)
        {
            if (!Equals(expected, actual))
                throw new Exception(string.Format("{0}: esperado '{1}', obtenido '{2}'", msg, expected, actual));
        }

        public static void Throws<TEx>(Action body, string msg) where TEx : Exception
        {
            try { body(); }
            catch (TEx) { return; }
            catch (Exception ex)
            {
                throw new Exception(string.Format("{0}: se esperaba {1} y salio {2}", msg, typeof(TEx).Name, ex.GetType().Name));
            }
            throw new Exception(msg + ": se esperaba " + typeof(TEx).Name + " y no salto ninguna excepcion");
        }

        /// <summary>Directorio temporal que se borra al terminar el bloque.</summary>
        public static void WithTempDir(Action<string> body)
        {
            string dir = Path.Combine(Path.GetTempPath(), "hft_tests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try { body(dir); }
            finally { try { Directory.Delete(dir, true); } catch { /* temporal */ } }
        }

        public static int Summary()
        {
            Console.WriteLine();
            Console.WriteLine(string.Format("{0} pasaron, {1} fallaron", _passed, _failed));
            return _failed == 0 ? 0 : 1;
        }
    }

    internal static class Program
    {
        public static int Main(string[] args)
        {
            EngineLog.Sink = delegate (string msg) { };   // silencia el log del motor

            MiniJsonTests.Run();
            XgbEvaluatorTests.Run();
            KellyZoneTests.Run();
            EnsembleEngineTests.Run();
            GlobalRiskCoordinatorTests.Run();

            return T.Summary();
        }
    }
}
