using System.Collections.Generic;

namespace GateNotifier;

public static class WorldDefinitions
{
    public static readonly Dictionary<string, string[]> NaDataCenters = new()
    {
        { "Aether", new[] { "Adamantoise", "Cactuar", "Faerie", "Gilgamesh", "Jenova", "Midgardsormr", "Sargatanas", "Siren" } },
        { "Primal", new[] { "Behemoth", "Excalibur", "Exodus", "Famfrit", "Hyperion", "Lamia", "Leviathan", "Ultros" } },
        { "Crystal", new[] { "Balmung", "Brynhildr", "Coeurl", "Diabolos", "Goblin", "Malboro", "Mateus", "Zalera" } },
        { "Dynamis", new[] { "Cuchulainn", "Golem", "Halicarnassus", "Kraken", "Maduin", "Marilith", "Rafflesia", "Seraph" } },
    };

    private static Dictionary<string, string>? worldToDataCenter;

    /// <summary>
    /// Returns the data center name for a given world, or null if unknown.
    /// </summary>
    public static string? GetDataCenter(string world)
    {
        if (worldToDataCenter == null)
        {
            worldToDataCenter = new Dictionary<string, string>();
            foreach (var (dc, worlds) in NaDataCenters)
            {
                foreach (var w in worlds)
                    worldToDataCenter[w] = dc;
            }
        }

        return worldToDataCenter.TryGetValue(world, out var result) ? result : null;
    }
}
