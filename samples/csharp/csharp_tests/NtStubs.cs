// Stubs minimos para compilar fuera de NinjaTrader los .cs del bot que lo
// referencian. SOLO para el proyecto de tests: en NT8 se usan los tipos reales.

using System;

namespace NinjaTrader.Core
{
    public static class Globals
    {
        // En NT8 es una propiedad de solo lectura; aqui es asignable para
        // poder simular la hora (TimeBlock, ForceClose).
        public static DateTime Now = new DateTime(2026, 1, 2, 12, 0, 0);
    }
}

namespace NinjaTrader.NinjaScript
{
    public enum PrintTo { OutputTab1, OutputTab2 }
}

namespace NinjaTrader.Code
{
    public static class Output
    {
        public static void Process(string message, NinjaTrader.NinjaScript.PrintTo target) { }
    }
}

namespace HFT_BOT_ML
{
    // Los detectores reales dependen de indicadores de NT8. El motor solo lee
    // la lista de nombres activos (diagnostico de detectores sin modelo).
    public static class Tier1StrategyDetectors
    {
        public static string[] ActiveDetectorNames = new string[0];
    }
}
