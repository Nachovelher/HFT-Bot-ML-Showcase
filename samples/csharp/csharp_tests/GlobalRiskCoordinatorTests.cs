using System;
using NinjaTrader.Core;

namespace HFT_BOT_ML.Tests
{
    internal static class GlobalRiskCoordinatorTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 3, 10, 14, 0, 0);

        private static void FreshSession(
            bool ddEnabled = false, double ddUsd = 200.0,
            bool lockEnabled = false, double lockTrigger = 200.0, double lockGiveback = 100.0,
            bool timeBlockEnabled = false, int tbStart = 21, int tbEnd = 23,
            bool forceCloseEnabled = false, int fcHour = 21, int fcMin = 44,
            DateTime? now = null)
        {
            DateTime t = now ?? T0;
            Globals.Now = t;
            // Limpiar cualquier posicion residual de tests previos
            GlobalRiskCoordinator.RegisterClose("MNQ", "inst1");
            GlobalRiskCoordinator.RegisterClose("MNQ", "inst2");
            GlobalRiskCoordinator.RegisterClose("MGC", "inst3");
            GlobalRiskCoordinator.Configure(
                ddEnabled, ddUsd,
                lockEnabled, lockTrigger, lockGiveback,
                timeBlockEnabled, tbStart, tbEnd,
                forceCloseEnabled, fcHour, fcMin);
            // Avanzar > 12h respecto a cualquier reset previo para forzar el reset
            GlobalRiskCoordinator.ResetSession(t);
        }

        public static void Run()
        {
            T.Section("GlobalRiskCoordinator");

            T.Run("concurrencia por instancia: cerrar una pata de MNQ no libera el bloqueo sobre MGC", delegate
            {
                FreshSession(now: T0.AddDays(1));
                GlobalRiskCoordinator.RegisterOpen("MNQ", "inst1");
                GlobalRiskCoordinator.RegisterOpen("MNQ", "inst2");

                T.True(!GlobalRiskCoordinator.CanOpen("MGC", allowMulti: false),
                    "con 2 patas de MNQ abiertas, MGC no puede abrir si allowMulti=false");
                T.True(GlobalRiskCoordinator.CanOpen("MNQ", allowMulti: false),
                    "el mismo instrumento si puede abrir");
                T.True(GlobalRiskCoordinator.CanOpen("MGC", allowMulti: true),
                    "con allowMulti=true MGC si puede abrir");

                // Cerrar solo inst1: inst2 sigue abierta -> MGC sigue bloqueado
                GlobalRiskCoordinator.RegisterClose("MNQ", "inst1");
                T.True(!GlobalRiskCoordinator.CanOpen("MGC", allowMulti: false),
                    "cerrar inst1 no debe liberar el bloqueo mientras inst2 siga abierta");

                // Cerrar inst2: ya no quedan posiciones de MNQ -> MGC puede abrir
                GlobalRiskCoordinator.RegisterClose("MNQ", "inst2");
                T.True(GlobalRiskCoordinator.CanOpen("MGC", allowMulti: false),
                    "al cerrar ambas instancias de MNQ, MGC ya puede abrir");
            });

            T.Run("halt por drawdown diario bloquea nuevas entradas hasta el reset de sesion", delegate
            {
                FreshSession(ddEnabled: true, ddUsd: 200.0, now: T0.AddDays(2));
                T.True(!GlobalRiskCoordinator.UpdateAccountPnL(150.0), "sube a +150: nuevo pico");
                T.Near(150.0, GlobalRiskCoordinator.AccountPeak, 1e-9, "pico = 150");
                T.True(!GlobalRiskCoordinator.UpdateAccountPnL(-10.0), "cae a -10 (dd = 160 < 200): aun no salta");
                T.True(GlobalRiskCoordinator.CanOpen("MNQ", allowMulti: true), "sigue operativo");

                T.True(GlobalRiskCoordinator.UpdateAccountPnL(-50.0), "cae a -50 (dd = 200 >= 200): dispara halt");
                T.True(GlobalRiskCoordinator.IsHaltActive, "halt activo");
                T.True(!GlobalRiskCoordinator.CanOpen("MNQ", allowMulti: true), "bloquea nuevas entradas");
                T.True(!GlobalRiskCoordinator.UpdateAccountPnL(-80.0), "segunda llamada con halt ya activo devuelve false");
            });

            T.Run("profit lock con trinquete: arma al alcanzar trigger, sube con el pico y nunca baja", delegate
            {
                FreshSession(lockEnabled: true, lockTrigger: 300.0, lockGiveback: 100.0, now: T0.AddDays(3));
                GlobalRiskCoordinator.UpdateAccountPnL(250.0);
                T.Near(0.0, GlobalRiskCoordinator.ProfitLockLevel, 1e-9, "bajo trigger: lock sin armar");

                GlobalRiskCoordinator.UpdateAccountPnL(300.0);
                T.Near(200.0, GlobalRiskCoordinator.ProfitLockLevel, 1e-9, "en trigger 300: lock = 200");

                GlobalRiskCoordinator.UpdateAccountPnL(450.0);
                T.Near(350.0, GlobalRiskCoordinator.ProfitLockLevel, 1e-9, "pico 450: trinquete sube a 350");

                T.True(!GlobalRiskCoordinator.UpdateAccountPnL(360.0), "retrocede a 360 > 350: no salta y el lock no baja");
                T.Near(350.0, GlobalRiskCoordinator.ProfitLockLevel, 1e-9, "el lock se mantiene en 350");

                T.True(GlobalRiskCoordinator.UpdateAccountPnL(350.0), "toca el lock (350 <= 350): dispara halt");
                T.True(GlobalRiskCoordinator.IsHaltActive, "halt por profit lock activo");
                T.True(!GlobalRiskCoordinator.CanOpen("MNQ", allowMulti: true), "entradas bloqueadas");
            });

            T.Run("ResetSession es idempotente dentro de 12h y limpia el estado pasadas 12h", delegate
            {
                DateTime baseTime = T0.AddDays(5);
                FreshSession(ddEnabled: true, ddUsd: 100.0, now: baseTime);
                GlobalRiskCoordinator.UpdateAccountPnL(-120.0);
                T.True(GlobalRiskCoordinator.IsHaltActive, "halt activo");

                // 2 horas despues: otro strategy dispara OnSessionStart -> no debe borrar el halt del dia
                GlobalRiskCoordinator.ResetSession(baseTime.AddHours(2));
                T.True(GlobalRiskCoordinator.IsHaltActive, "a las 2h no debe resetear");

                // 13 horas despues (nueva sesion): limpia halt y contadores
                GlobalRiskCoordinator.ResetSession(baseTime.AddHours(13));
                T.True(!GlobalRiskCoordinator.IsHaltActive, "a las 13h limpia el halt");
                T.Near(0.0, GlobalRiskCoordinator.AccountPeak, 1e-9, "pico a 0");
                T.Near(0.0, GlobalRiskCoordinator.AccountCum, 1e-9, "cum a 0");
            });

            T.Run("TimeBlock: ventana simple (21h-23h) y ventana que cruza medianoche (22h-02h)", delegate
            {
                FreshSession(timeBlockEnabled: true, tbStart: 21, tbEnd: 23, now: T0.AddDays(7));
                Globals.Now = new DateTime(2026, 3, 17, 20, 59, 0);
                T.True(!GlobalRiskCoordinator.IsInTimeBlock(), "20:59 fuera de 21-23");
                T.True(GlobalRiskCoordinator.CanOpen("MNQ", allowMulti: true), "20:59 permite abrir");

                Globals.Now = new DateTime(2026, 3, 17, 21, 30, 0);
                T.True(GlobalRiskCoordinator.IsInTimeBlock(), "21:30 dentro de 21-23");
                T.True(!GlobalRiskCoordinator.CanOpen("MNQ", allowMulti: true), "21:30 bloquea CanOpen");

                Globals.Now = new DateTime(2026, 3, 17, 23, 0, 0);
                T.True(!GlobalRiskCoordinator.IsInTimeBlock(), "23:00 extremo superior abierto");

                // Ventana que cruza medianoche: 22h -> 02h
                FreshSession(timeBlockEnabled: true, tbStart: 22, tbEnd: 2, now: T0.AddDays(8));
                Globals.Now = new DateTime(2026, 3, 18, 23, 15, 0);
                T.True(GlobalRiskCoordinator.IsInTimeBlock(), "23:15 dentro de 22-02");
                Globals.Now = new DateTime(2026, 3, 19, 1, 30, 0);
                T.True(GlobalRiskCoordinator.IsInTimeBlock(), "01:30 dentro de 22-02");
                Globals.Now = new DateTime(2026, 3, 19, 2, 0, 0);
                T.True(!GlobalRiskCoordinator.IsInTimeBlock(), "02:00 fuera de 22-02");
            });

            T.Run("ForceClose: ventana de 15 minutos desde HH:MM", delegate
            {
                FreshSession(forceCloseEnabled: true, fcHour: 21, fcMin: 44, now: T0.AddDays(9));
                Globals.Now = new DateTime(2026, 3, 19, 21, 43, 59);
                T.True(!GlobalRiskCoordinator.IsForceCloseTime(), "21:43:59 antes de la ventana");

                Globals.Now = new DateTime(2026, 3, 19, 21, 44, 0);
                T.True(GlobalRiskCoordinator.IsForceCloseTime(), "21:44:00 inicio exacto");

                Globals.Now = new DateTime(2026, 3, 19, 21, 52, 0);
                T.True(GlobalRiskCoordinator.IsForceCloseTime(), "21:52:00 mitad de la ventana de 15m (barra de 10m/15m)");

                Globals.Now = new DateTime(2026, 3, 19, 21, 59, 0);
                T.True(!GlobalRiskCoordinator.IsForceCloseTime(), "21:59:00 fin de la ventana de 15m");
            });
        }
    }
}
