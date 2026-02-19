using System;
using System.Collections.Generic;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Command;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using FFXIVClientStructs.FFXIV.Client.UI;
using Dalamud.Game.Gui.Dtr;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using GateNotifier.Windows;

namespace GateNotifier;

public sealed class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IToastGui ToastGui { get; private set; } = null!;
    [PluginService] internal static ICondition Condition { get; private set; } = null!;
    [PluginService] internal static IDtrBar DtrBar { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;

    private const string CommandName = "/gate";

    public Configuration Configuration { get; init; }

    public readonly WindowSystem WindowSystem = new("GateNotifier");
    private ConfigWindow ConfigWindow { get; init; }
    private OverlayWindow OverlayWindow { get; init; }

    private readonly IDtrBarEntry dtrEntry;

    private TimeSpan previousTimeRemaining = GateScheduler.GetTimeUntilNextGate(DateTime.UtcNow);
    private DateTime lastAlertTime = DateTime.MinValue;
    public string? LastDetectedGateName { get; private set; }
    public GateType? LastDetectedGateType { get; private set; }
    public string? CurrentGateName { get; private set; }
    public GateType? CurrentGateType { get; private set; }

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        Configuration.Initialize();

        ConfigWindow = new ConfigWindow(this);
        OverlayWindow = new OverlayWindow(this);

        WindowSystem.AddWindow(ConfigWindow);
        WindowSystem.AddWindow(OverlayWindow);

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Toggle GATE overlay. Use '/gate config' to open settings.",
        });

        PluginInterface.UiBuilder.Draw += WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi += ToggleMainUi;

        dtrEntry = DtrBar.Get("GateNotifier");
        dtrEntry.OnClick = _ => ConfigWindow.Toggle();

        Framework.Update += OnFrameworkUpdate;
        ChatGui.ChatMessage += OnChatMessage;

        Log.Information("GATE Notifier loaded.");
    }

    public void Dispose()
    {
        dtrEntry.Remove();

        Framework.Update -= OnFrameworkUpdate;
        ChatGui.ChatMessage -= OnChatMessage;

        PluginInterface.UiBuilder.Draw -= WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleMainUi;

        WindowSystem.RemoveAllWindows();

        ConfigWindow.Dispose();
        OverlayWindow.Dispose();

        CommandManager.RemoveHandler(CommandName);
    }

    private void OnCommand(string command, string args)
    {
        if (args.Trim().Equals("config", StringComparison.OrdinalIgnoreCase))
        {
            ConfigWindow.Toggle();
        }
        else
        {
            Configuration.ShowOverlay = !Configuration.ShowOverlay;
            Configuration.Save();
        }
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        OverlayWindow.IsOpen = Configuration.ShowOverlay;

        var now = DateTime.UtcNow;
        var timeRemaining = GateScheduler.GetTimeUntilNextGate(now);

        // When a new cycle starts (countdown wrapped around), promote detected GATE to current
        if (timeRemaining > previousTimeRemaining)
        {
            CurrentGateName = LastDetectedGateName;
            CurrentGateType = LastDetectedGateType;
            LastDetectedGateName = null;
            LastDetectedGateType = null;
        }

        if (Configuration.EnableTimerAlerts)
        {
            var crossed = GateScheduler.GetCrossedThresholds(previousTimeRemaining, timeRemaining, Configuration.AlertMinutesBefore);
            foreach (var minutes in crossed)
            {
                SendTimerAlert(minutes);
            }
        }

        previousTimeRemaining = timeRemaining;

        // Update DTR bar entry
        if (Configuration.ShowDtrBar)
        {
            dtrEntry.Shown = true;
            dtrEntry.Text = $"GATE {timeRemaining.Minutes:D2}:{timeRemaining.Seconds:D2}";

            // Tooltip: show detected or possible GATEs
            if (CurrentGateName != null)
                dtrEntry.Tooltip = $"Current: {CurrentGateName}";
            else if (LastDetectedGateName != null)
                dtrEntry.Tooltip = $"Next: {LastDetectedGateName}";
            else
                dtrEntry.Tooltip = $"Possible: {string.Join(" / ", GetPossibleGates())}";
        }
        else
        {
            dtrEntry.Shown = false;
        }
    }

    private void OnChatMessage(XivChatType type, int timestamp, ref SeString sender, ref SeString message, ref bool isHandled)
    {
        if (!Configuration.EnableChatDetection)
            return;

        // GATE announcements use SystemMessage base type (57) but the raw value
        // may include flag bits (e.g. 2105 = 0x0839). Check the base type only.
        if (((int)type & 0x7F) != (int)XivChatType.SystemMessage)
            return;

        var text = message.TextValue;
        foreach (var (gateType, substring) in GateDefinitions.ChatSubstrings)
        {
            if (text.Contains(substring, StringComparison.OrdinalIgnoreCase))
            {
                var gateName = GateDefinitions.DisplayNames[gateType];
                Log.Information($"GATE detected: {gateName} (chat type {(int)type})");

                var timeRemaining = GateScheduler.GetTimeUntilNextGate(DateTime.UtcNow);

                // If we just crossed a cycle boundary (>15 min left) and have no
                // current GATE yet, the announcement is for the active slot.
                if (CurrentGateName == null && timeRemaining.TotalMinutes > 15)
                {
                    CurrentGateName = gateName;
                    CurrentGateType = gateType;
                }
                else
                {
                    LastDetectedGateName = gateName;
                    LastDetectedGateType = gateType;
                }

                if (Configuration.EnabledGates.TryGetValue(gateType, out var enabled) && enabled)
                {
                    SendGateDetectedAlert(gateType);
                }

                break;
            }
        }
    }

    private bool IsInDuty()
    {
        return Condition[ConditionFlag.BoundByDuty] || Condition[ConditionFlag.BoundByDuty56] || Condition[ConditionFlag.BoundByDuty95];
    }

    private void SendTimerAlert(int minutes)
    {
        if (Configuration.SuppressInDuty && IsInDuty())
            return;

        // Skip if no tracked GATEs in this slot
        if (!HasEnabledGateInSlot(GetPossibleGates()))
            return;

        // Debounce: don't fire multiple alerts within 30 seconds
        var now = DateTime.UtcNow;
        if ((now - lastAlertTime).TotalSeconds < 30)
            return;
        lastAlertTime = now;

        string msg;
        if (LastDetectedGateName != null)
        {
            msg = minutes == 1
                ? $"[GATE] {LastDetectedGateName} starts in {minutes} minute!"
                : $"[GATE] {LastDetectedGateName} starts in {minutes} minutes!";
        }
        else
        {
            var possible = string.Join(" / ", GetPossibleGates());
            msg = minutes == 1
                ? $"[GATE] {minutes} minute! Possible: {possible}"
                : $"[GATE] {minutes} minutes! Possible: {possible}";
        }

        if (Configuration.NotifyViaChat)
        {
            ChatGui.Print(msg);
        }

        if (Configuration.NotifyViaToast)
        {
            ToastGui.ShowQuest(msg);
        }

        if (Configuration.NotifyViaSound)
        {
            unsafe { UIGlobals.PlayChatSoundEffect((uint)Configuration.SoundEffectNumber); }
        }
    }

    private void SendGateDetectedAlert(GateType gateType)
    {
        if (Configuration.SuppressInDuty && IsInDuty())
            return;

        var name = GateDefinitions.DisplayNames[gateType];
        var msg = $"[GATE] Upcoming GATE: {name}!";

        if (Configuration.NotifyViaChat)
        {
            ChatGui.Print(msg);
        }

        if (Configuration.NotifyViaToast)
        {
            ToastGui.ShowQuest(msg);
        }

        if (Configuration.NotifyViaSound)
        {
            unsafe { UIGlobals.PlayChatSoundEffect((uint)Configuration.SoundEffectNumber); }
        }
    }

    public bool IsGateNameEnabled(string gateName)
    {
        foreach (var (gateType, name) in GateDefinitions.DisplayNames)
        {
            if (name == gateName && Configuration.EnabledGates.TryGetValue(gateType, out var enabled))
                return enabled;
        }

        return false;
    }

    public TimeSpan GetTimeUntilNextGate()
    {
        return GateScheduler.GetTimeUntilNextGate(DateTime.UtcNow);
    }

    public bool HasEnabledGateInSlot(string[] possibleGates)
    {
        foreach (var gate in possibleGates)
        {
            if (IsGateNameEnabled(gate))
                return true;
        }

        return false;
    }

    public string[] GetPossibleGates()
    {
        return GateScheduler.GetPossibleGates(DateTime.UtcNow);
    }

    public string[] GetCurrentPossibleGates()
    {
        return GateScheduler.GetCurrentPossibleGates(DateTime.UtcNow);
    }

    public List<(DateTime Time, string[] Gates)> GetUpcomingSlots(int count)
    {
        return GateScheduler.GetUpcomingSlots(DateTime.UtcNow, count);
    }

    public void ToggleConfigUi() => ConfigWindow.Toggle();
    public void ToggleMainUi() => OverlayWindow.Toggle();
}
