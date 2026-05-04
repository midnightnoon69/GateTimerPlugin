using System;
using System.Collections.Generic;

namespace GateAnalyzer;

public static class GateScheduler
{
    private static readonly int[] GateMinutes = { 0, 20, 40 };

    public static DateTime GetNextGateTime(DateTime utcNow)
    {
        var minute = utcNow.Minute;
        var second = utcNow.Second;

        foreach (var gateMinute in GateMinutes)
        {
            if (minute < gateMinute || (minute == gateMinute && second == 0))
            {
                return new DateTime(utcNow.Year, utcNow.Month, utcNow.Day, utcNow.Hour, gateMinute, 0, DateTimeKind.Utc);
            }
        }

        return new DateTime(utcNow.Year, utcNow.Month, utcNow.Day, utcNow.Hour, 0, 0, DateTimeKind.Utc).AddHours(1);
    }

    public static TimeSpan GetTimeUntilNextGate(DateTime utcNow)
    {
        return GetNextGateTime(utcNow) - utcNow;
    }

    public static DateTime GetCurrentGateTime(DateTime utcNow)
    {
        var snapped = SnapToBoundary(utcNow);
        var minute = snapped.Minute;

        for (var i = GateMinutes.Length - 1; i >= 0; i--)
        {
            if (minute >= GateMinutes[i])
            {
                return new DateTime(snapped.Year, snapped.Month, snapped.Day, snapped.Hour, GateMinutes[i], 0, DateTimeKind.Utc);
            }
        }

        return new DateTime(snapped.Year, snapped.Month, snapped.Day, snapped.Hour, 0, 0, DateTimeKind.Utc).AddMinutes(-20);
    }

    public static DateTime SnapToBoundary(DateTime utcNow)
    {
        var minute = utcNow.Minute;
        var second = utcNow.Second;
        var nextBoundaryMinute = ((minute / 20) + 1) * 20;
        var secondsUntilNext = (nextBoundaryMinute - minute) * 60 - second;
        if (secondsUntilNext <= 10)
        {
            return new DateTime(utcNow.Year, utcNow.Month, utcNow.Day, utcNow.Hour, 0, 0, DateTimeKind.Utc)
                .AddMinutes(nextBoundaryMinute);
        }
        return utcNow;
    }

    public static int GetCurrentSlot(DateTime utcNow)
    {
        return GetCurrentGateTime(utcNow).Minute;
    }
}
