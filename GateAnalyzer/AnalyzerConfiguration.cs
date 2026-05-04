using Dalamud.Configuration;
using System;

namespace GateAnalyzer;

[Serializable]
public class AnalyzerConfiguration : IPluginConfiguration
{
    public int Version { get; set; } = 0;

    public bool EnableApiSharing { get; set; } = true;
    public string ApiUrl { get; set; } = "https://saucyxiv.duckdns.org";

    public void Save()
    {
        AnalyzerPlugin.PluginInterface.SavePluginConfig(this);
    }
}
