using Dalamud.Configuration;
using System;
using System.Collections.Generic;

namespace GateNotifier;

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 0;

    public Dictionary<GateType, bool> EnabledGates { get; set; } = new();
    public List<int> AlertMinutesBefore { get; set; } = new() { 5, 1 };

    public bool NotifyViaChat { get; set; } = true;
    public bool NotifyViaToast { get; set; } = true;

    public bool ShowOverlay { get; set; } = true;

    public bool EnableTimerAlerts { get; set; } = true;
    public bool EnableChatDetection { get; set; } = true;

    public bool SuppressInDuty { get; set; } = true;

    public void Initialize()
    {
        foreach (var gate in Enum.GetValues<GateType>())
        {
            EnabledGates.TryAdd(gate, true);
        }
    }

    public void Save()
    {
        Plugin.PluginInterface.SavePluginConfig(this);
    }
}
