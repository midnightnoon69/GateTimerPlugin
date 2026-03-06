using Dalamud.Configuration;
using System;
using System.Collections.Generic;

namespace GateNotifier;

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 0;

    public Dictionary<GateType, bool> EnabledGates { get; set; } = new();
    public List<int> AlertMinutesBefore { get; set; } = new();

    public bool NotifyViaChat { get; set; } = true;
    public bool NotifyViaToast { get; set; } = true;
    public bool NotifyViaSound { get; set; } = true;
    public int SoundEffectNumber { get; set; } = 3;

    public bool ShowOverlay { get; set; } = true;

    public bool EnableTimerAlerts { get; set; } = true;
    public bool EnableChatDetection { get; set; } = true;

    public bool SuppressInDuty { get; set; } = true;

    public bool ShowDtrBar { get; set; } = true;

    // API sharing settings
    public bool EnableApiSharing { get; set; } = true;
    public string ApiUrl { get; set; } = "http://localhost:5000";

    // Persisted active GATE state (survives reloads)
    public string? ActiveGateName { get; set; }
    public GateType? ActiveGateType { get; set; }
    public DateTime? ActiveGateDetectedAt { get; set; }

    // Persisted next GATE detection (survives reloads)
    public string? NextGateName { get; set; }
    public GateType? NextGateType { get; set; }

    public void Initialize()
    {
        foreach (var gate in Enum.GetValues<GateType>())
        {
            EnabledGates.TryAdd(gate, true);
        }

        // Deduplicate alert list (fixes bug where deserializer appended onto field defaults)
        if (AlertMinutesBefore.Count > 0)
        {
            var deduped = new HashSet<int>(AlertMinutesBefore);
            AlertMinutesBefore.Clear();
            AlertMinutesBefore.AddRange(deduped);
            AlertMinutesBefore.Sort((a, b) => b.CompareTo(a));
        }
        else
        {
            AlertMinutesBefore.AddRange(new[] { 5, 1 });
        }
    }

    public void Save()
    {
        Plugin.PluginInterface.SavePluginConfig(this);
    }
}
