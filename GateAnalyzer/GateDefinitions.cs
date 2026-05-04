using System.Collections.Generic;

namespace GateAnalyzer;

public enum GateType
{
    TheSliceIsRight,
    AirForceOne,
    Cliffhanger,
    LeapOfFaith,
    AnyWayTheWindBlows,
}

public static class GateDefinitions
{
    public static readonly Dictionary<GateType, string> DisplayNames = new()
    {
        { GateType.TheSliceIsRight, "The Slice Is Right" },
        { GateType.AirForceOne, "Air Force One" },
        { GateType.Cliffhanger, "Cliffhanger" },
        { GateType.LeapOfFaith, "Leap of Faith" },
        { GateType.AnyWayTheWindBlows, "Any Way the Wind Blows" },
    };

    public static readonly Dictionary<GateType, string> ChatSubstrings = new()
    {
        { GateType.TheSliceIsRight, "The Slice Is Right" },
        { GateType.AirForceOne, "Air Force One" },
        { GateType.Cliffhanger, "Cliffhanger" },
        { GateType.LeapOfFaith, "Leap of Faith" },
        { GateType.AnyWayTheWindBlows, "Any Way the Wind Blows" },
    };

    public static readonly Dictionary<int, string[]> SlotPools = new()
    {
        { 0, new[] { "Air Force One", "Cliffhanger", "Leap of Faith" } },
        { 20, new[] { "Any Way the Wind Blows", "The Slice Is Right", "Air Force One" } },
        { 40, new[] { "The Slice Is Right", "Leap of Faith", "Air Force One" } },
    };

    public static readonly Dictionary<GateType, string> Locations = new()
    {
        { GateType.TheSliceIsRight, "Event Square" },
        { GateType.AirForceOne, "Round Square" },
        { GateType.Cliffhanger, "Wonder Square East" },
        { GateType.LeapOfFaith, "Round Square" },
        { GateType.AnyWayTheWindBlows, "Event Square" },
    };

    public static readonly Dictionary<GateType, int> JoinWindowSeconds = new()
    {
        { GateType.TheSliceIsRight, 120 },
        { GateType.AirForceOne, 600 },
        { GateType.Cliffhanger, 600 },
        { GateType.LeapOfFaith, 490 },
        { GateType.AnyWayTheWindBlows, 120 },
    };
}
