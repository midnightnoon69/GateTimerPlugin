using System;
using System.Collections.Generic;

namespace GateNotifier;

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

        // Next GATE is at :00 of the next hour
        return new DateTime(utcNow.Year, utcNow.Month, utcNow.Day, utcNow.Hour, 0, 0, DateTimeKind.Utc).AddHours(1);
    }

    public static TimeSpan GetTimeUntilNextGate(DateTime utcNow)
    {
        return GetNextGateTime(utcNow) - utcNow;
    }

    public static DateTime GetCurrentGateTime(DateTime utcNow)
    {
        var minute = utcNow.Minute;

        // Find the most recent :00/:20/:40 that has passed
        for (var i = GateMinutes.Length - 1; i >= 0; i--)
        {
            if (minute >= GateMinutes[i])
            {
                return new DateTime(utcNow.Year, utcNow.Month, utcNow.Day, utcNow.Hour, GateMinutes[i], 0, DateTimeKind.Utc);
            }
        }

        // Before :00, so current slot is :40 of the previous hour
        return new DateTime(utcNow.Year, utcNow.Month, utcNow.Day, utcNow.Hour, 0, 0, DateTimeKind.Utc).AddMinutes(-20);
    }

    public static string[] GetCurrentPossibleGates(DateTime utcNow)
    {
        var currentGate = GetCurrentGateTime(utcNow);
        return GateDefinitions.SlotPools[currentGate.Minute];
    }

    public static string[] GetPossibleGates(DateTime utcNow)
    {
        var nextGate = GetNextGateTime(utcNow);
        return GateDefinitions.SlotPools[nextGate.Minute];
    }

    public static List<(DateTime Time, string[] Gates)> GetUpcomingSlots(DateTime utcNow, int count)
    {
        var slots = new List<(DateTime, string[])>();
        var next = GetNextGateTime(utcNow);
        for (var i = 0; i < count; i++)
        {
            var slotTime = next.AddMinutes(i * 20);
            slots.Add((slotTime, GateDefinitions.SlotPools[slotTime.Minute]));
        }

        return slots;
    }

    public static List<int> GetCrossedThresholds(TimeSpan previous, TimeSpan current, List<int> alertMinutes)
    {
        var crossed = new List<int>();
        foreach (var minutes in alertMinutes)
        {
            var threshold = TimeSpan.FromMinutes(minutes);
            if (previous > threshold && current <= threshold)
            {
                crossed.Add(minutes);
            }
        }

        return crossed;
    }
}
