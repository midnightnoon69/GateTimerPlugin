using System.Collections.Generic;

namespace GateNotifier;

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
}
