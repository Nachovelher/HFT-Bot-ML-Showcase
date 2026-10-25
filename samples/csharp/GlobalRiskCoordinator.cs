using System;
using System.Collections.Generic;
using NinjaTrader.Code;
using NinjaTrader.NinjaScript;

namespace HFT_BOT_ML
{
    /// <summary>
    /// Coordinador de risk a nivel de cuenta. Singleton static compartido
    /// entre todas las instancias de HFT_ML_Strategy en el mismo AppDomain
    /// (proceso NT8). Implementa 3 mecanismos de risk a nivel cuenta:
    ///
    ///   1. Control de concurrencia: 1 instrumento o múltiples a la vez
    ///      (allowMulti se pasa por strategy en CanOpen).
    ///
    ///   2. Halt por DD simple: si peak_global − cum_global ≥ threshold,
    ///      bloquea entradas hasta reset de sesión.
    ///
    ///   3. Halt por trail DD (Profit Lock): paridad con el ProfitLock
    ///      per-bot existente, pero a nivel cuenta. Cuando peak_global ≥
    ///      LockTriggerUSD se arma el lock = peak − Giveback (ratchet up).
    ///      Halt si cum_global ≤ lock.
    ///
    /// Las configs (DDEnabled, LockEnabled, thresholds) son public static
    /// para que cualquier HFT_ML_Strategy las setee desde sus properties UI
    /// vía Configure(). Si dos strategies tienen configs distintas, el
    /// último Configure() gana (con warning en log).
    ///
    /// Source de PnL global: Account.Get(AccountItem.RealizedProfitLoss,
    /// Currency.UsDollar) — la cuenta NT8 ya agrega todos los instrumentos.
    /// La strategy invoca UpdateAccountPnL(realizedTotal) tras cada cierre.
    /// </summary>
    public static class GlobalRiskCoordinator
    {
        // ── State ──
        private static readonly object _lock = new object();
        private static double _accountPeak = 0.0;
        private static double _accountCum = 0.0;
        private static double _profitLock = 0.0;
        private static bool _haltActive = false;
        private static string _haltReason = null;
        private static DateTime _lastResetTime = DateTime.MinValue;
        // [2026-10-06] Posiciones abiertas por INSTANCIA de estrategia. Antes era
        // un HashSet de símbolos: dos patas del mismo instrumento (MNQ5 + MNQ10,
        // MGC10 + MGC15) compartían la clave y al cerrar una se liberaba el hueco
        // de la otra con EnableMultiInstrument=false.
        private static readonly Dictionary<string, HashSet<string>> _openByInstrument =
            new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        // ── Configs (set by any HFT_ML_Strategy via Configure()) ──
        public static bool DDEnabled = false;
        public static double DDThresholdUSD = 200.0;
        public static bool LockEnabled = false;
        public static double LockTriggerUSD = 200.0;
        public static double LockGivebackUSD = 100.0;
        // 2026-05-04: bloqueo horario para evitar entradas durante el cierre
        // del bróker (típicamente 21h-23h Madrid). Compara contra Globals.Now
        // (hora local del PC), no contra Time[0] del bar — porque la
        // restricción es REAL del bróker, no del chart-timezone.
        public static bool TimeBlockEnabled = false;
        public static int TimeBlockStartHour = 21;
        public static int TimeBlockEndHour = 23;

        // 2026-05-04: cierre forzado a hora puntual (ej. 21:44 Madrid antes
        // del cierre 21:45 del bróker). Evita quedarse atrapado con posiciones
        // cuando el bróker corta. Cada strategy llama IsForceCloseTime() en
        // OnBarUpdate; si true y tiene posición → cierre a mercado idempotente.
        public static bool ForceCloseEnabled = false;
        public static int ForceCloseHour = 21;
        public static int ForceCloseMinute = 44;

        // ── Public read-only state ──
        public static bool IsHaltActive { get { lock (_lock) { return _haltActive; } } }
        public static string HaltReason { get { lock (_lock) { return _haltReason; } } }
        public static double AccountPeak { get { lock (_lock) { return _accountPeak; } } }
        public static double AccountCum { get { lock (_lock) { return _accountCum; } } }
        public static double ProfitLockLevel { get { lock (_lock) { return _profitLock; } } }
        public static string ActiveInstrumentsStr {
            get {
                lock (_lock) {
                    var act = new List<string>();
                    foreach (var kv in _openByInstrument)
                        if (kv.Value.Count > 0) act.Add(kv.Key + "x" + kv.Value.Count);
                    return string.Join(",", act);
                }
            }
        }

        /// <summary>
        /// Comprueba si un instrumento puede abrir una nueva posición.
        /// Bloquea si:
        ///   - El halt global está activo (DD o ProfitLock).
        ///   - allowMulti=false y otro instrumento ya tiene posición abierta
        ///     (registrada vía RegisterOpen).
        /// </summary>
        public static bool CanOpen(string instrument, bool allowMulti)
        {
            lock (_lock)
            {
                if (_haltActive) return false;
                if (!allowMulti)
                    foreach (var kv in _openByInstrument)
                        if (kv.Value.Count > 0 && !string.Equals(kv.Key, instrument,
                                StringComparison.OrdinalIgnoreCase))
                            return false;
                if (TimeBlockEnabled && IsInTimeBlock()) return false;
                return true;
            }
        }

        /// <summary>
        /// True si la hora local actual cae dentro de la ventana
        /// [TimeBlockStartHour, TimeBlockEndHour). Soporta ventanas que
        /// cruzan medianoche (ej. start=22, end=2 = 22h-02h).
        /// </summary>
        public static bool IsInTimeBlock()
        {
            lock (_lock)
            {
                // Globals.Now = reloj de NT8 (hora de Playback en Market Replay).
                int h = NinjaTrader.Core.Globals.Now.Hour;
                int s = TimeBlockStartHour;
                int e = TimeBlockEndHour;
                if (s == e) return false;       // ventana vacía
                if (s < e)  return h >= s && h < e;
                // s > e → cruza medianoche
                return h >= s || h < e;
            }
        }

        /// <summary>
        /// True si el reloj de NT8 está dentro de la ventana de ForceClose. Cada strategy
        /// debe consultar esto en OnBarUpdate y, si true Y tiene posición,
        /// cerrar a mercado. La idempotencia (cerrar solo una vez por día)
        /// es responsabilidad de la strategy llamadora — el coordinator solo
        /// expone "es la hora?".
        /// </summary>
        public static bool IsForceCloseTime()
        {
            lock (_lock)
            {
                if (!ForceCloseEnabled) return false;
                // [2026-10-06] Ventana de 15 min desde HH:MM (antes: minuto EXACTO,
                // que con barras de 5/10/15 min nunca coincidía con el cierre de
                // barra → el ForceClose no disparaba jamás). La idempotencia por
                // día la gestiona la estrategia (_forceClosedToday).
                TimeSpan now = NinjaTrader.Core.Globals.Now.TimeOfDay;
                TimeSpan start = new TimeSpan(ForceCloseHour, ForceCloseMinute, 0);
                return now >= start && now < start + FORCE_CLOSE_WINDOW;
            }
        }

        private static readonly TimeSpan FORCE_CLOSE_WINDOW = TimeSpan.FromMinutes(15);

        /// <summary>
        /// Marca un instrumento como activo (posición abierta).
        /// Idempotente — múltiples llamadas para el mismo símbolo no
        /// añaden duplicados (HashSet).
        /// </summary>
        public static void RegisterOpen(string instrument, string instanceId)
        {
            if (string.IsNullOrEmpty(instrument)) return;
            lock (_lock)
            {
                HashSet<string> set;
                if (!_openByInstrument.TryGetValue(instrument, out set))
                    _openByInstrument[instrument] = set = new HashSet<string>();
                set.Add(instanceId ?? "");
            }
        }

        /// <summary>
        /// Marca un instrumento como cerrado (sin posición abierta).
        /// Idempotente — si no estaba activo no hace nada.
        /// </summary>
        public static void RegisterClose(string instrument, string instanceId)
        {
            if (string.IsNullOrEmpty(instrument)) return;
            lock (_lock)
            {
                HashSet<string> set;
                if (_openByInstrument.TryGetValue(instrument, out set))
                    set.Remove(instanceId ?? "");
            }
        }

        /// <summary>
        /// Actualiza el PnL realizado a nivel de cuenta y dispara halts si
        /// aplica. Llamar tras cada cierre de posición con
        /// Account.Get(AccountItem.RealizedProfitLoss, Currency.UsDollar).
        /// </summary>
        /// <returns>true si el halt acaba de activarse en esta llamada</returns>
        public static bool UpdateAccountPnL(double realizedTotal)
        {
            lock (_lock)
            {
                _accountCum = realizedTotal;
                if (_accountCum > _accountPeak) _accountPeak = _accountCum;

                // Profit Lock ratchet: solo sube, nunca baja.
                if (LockEnabled && _accountPeak >= LockTriggerUSD)
                {
                    double newLock = _accountPeak - LockGivebackUSD;
                    if (newLock > _profitLock) _profitLock = newLock;
                }

                if (_haltActive) return false;

                bool justHalted = false;

                if (DDEnabled && (_accountPeak - _accountCum) >= DDThresholdUSD)
                {
                    _haltActive = true;
                    _haltReason = string.Format(
                        "DD: peak=${0:F2} cum=${1:F2} dd=${2:F2} >= ${3:F2}",
                        _accountPeak, _accountCum,
                        _accountPeak - _accountCum, DDThresholdUSD);
                    justHalted = true;
                }
                else if (LockEnabled && _profitLock > 0 && _accountCum <= _profitLock)
                {
                    _haltActive = true;
                    _haltReason = string.Format(
                        "ProfitLock: cum=${0:F2} <= lock=${1:F2} (peak=${2:F2}, giveback=${3:F2})",
                        _accountCum, _profitLock, _accountPeak, LockGivebackUSD);
                    justHalted = true;
                }

                if (justHalted)
                {
                    try {
                        Output.Process(
                            string.Format("[GLOBAL-RISK-HALT] {0}", _haltReason),
                            PrintTo.OutputTab1);
                    } catch { /* no crash por log */ }
                }
                return justHalted;
            }
        }

        /// <summary>
        /// Reset de sesión (peak, cum, lock, halt). Idempotente — solo
        /// resetea si han pasado más de 12 horas desde el último reset
        /// (evita conflictos cuando varios strategies disparan
        /// OnSessionStart en horas distintas).
        /// </summary>
        public static void ResetSession(DateTime now)
        {
            lock (_lock)
            {
                if (now - _lastResetTime < TimeSpan.FromHours(12)) return;
                _accountPeak = 0.0;
                _accountCum = 0.0;
                _profitLock = 0.0;
                _haltActive = false;
                _haltReason = null;
                _lastResetTime = now;
                try {
                    Output.Process(
                        string.Format(
                            "[GLOBAL-RISK-RESET] Session reset at {0:yyyy-MM-dd HH:mm}", now),
                        PrintTo.OutputTab1);
                } catch { /* no crash por log */ }
            }
        }

        /// <summary>
        /// Setea las configs globales. Llamar desde cada HFT_ML_Strategy
        /// en SetDefaults o State.DataLoaded. Si dos strategies tienen
        /// configs distintas, el último Configure() gana (con warning).
        /// </summary>
        public static void Configure(
            bool ddEnabled, double ddUSD,
            bool lockEnabled, double lockTrigger, double lockGiveback,
            bool timeBlockEnabled, int timeBlockStart, int timeBlockEnd,
            bool forceCloseEnabled, int forceCloseHour, int forceCloseMinute)
        {
            lock (_lock)
            {
                bool changed = (DDEnabled != ddEnabled
                    || Math.Abs(DDThresholdUSD - ddUSD) > 0.01
                    || LockEnabled != lockEnabled
                    || Math.Abs(LockTriggerUSD - lockTrigger) > 0.01
                    || Math.Abs(LockGivebackUSD - lockGiveback) > 0.01
                    || TimeBlockEnabled != timeBlockEnabled
                    || TimeBlockStartHour != timeBlockStart
                    || TimeBlockEndHour != timeBlockEnd
                    || ForceCloseEnabled != forceCloseEnabled
                    || ForceCloseHour != forceCloseHour
                    || ForceCloseMinute != forceCloseMinute);

                if (changed && _lastResetTime != DateTime.MinValue)
                {
                    try {
                        Output.Process(
                            string.Format(
                                "[GLOBAL-RISK-CONFIG] Configs cambiadas: " +
                                "DD={0}->{1} (${2:F2}->${3:F2}), " +
                                "Lock={4}->{5} (trig ${6:F2}->${7:F2}, give ${8:F2}->${9:F2}), " +
                                "TimeBlock={10}->{11} ({12}h-{13}h -> {14}h-{15}h), " +
                                "ForceClose={16}->{17} ({18:D2}:{19:D2} -> {20:D2}:{21:D2})",
                                DDEnabled, ddEnabled, DDThresholdUSD, ddUSD,
                                LockEnabled, lockEnabled,
                                LockTriggerUSD, lockTrigger,
                                LockGivebackUSD, lockGiveback,
                                TimeBlockEnabled, timeBlockEnabled,
                                TimeBlockStartHour, TimeBlockEndHour,
                                timeBlockStart, timeBlockEnd,
                                ForceCloseEnabled, forceCloseEnabled,
                                ForceCloseHour, ForceCloseMinute,
                                forceCloseHour, forceCloseMinute),
                            PrintTo.OutputTab1);
                    } catch { /* no crash */ }
                }

                DDEnabled = ddEnabled;
                DDThresholdUSD = ddUSD;
                LockEnabled = lockEnabled;
                LockTriggerUSD = lockTrigger;
                LockGivebackUSD = lockGiveback;
                TimeBlockEnabled = timeBlockEnabled;
                TimeBlockStartHour = timeBlockStart;
                TimeBlockEndHour = timeBlockEnd;
                ForceCloseEnabled = forceCloseEnabled;
                ForceCloseHour = forceCloseHour;
                ForceCloseMinute = forceCloseMinute;
            }
        }
    }
}
