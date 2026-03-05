using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Command;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Dalamud.Game.Gui.Dtr;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using Dalamud.Utility;
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
    [PluginService] internal static IAddonLifecycle AddonLifecycle { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;

    private const string CommandName = "/gate";

    public Configuration Configuration { get; init; }

    public readonly WindowSystem WindowSystem = new("GateNotifier");
    private ConfigWindow ConfigWindow { get; init; }
    private OverlayWindow OverlayWindow { get; init; }

    private readonly IDtrBarEntry dtrEntry;

    private TimeSpan previousTimeRemaining = GateScheduler.GetTimeUntilNextGate(DateTime.UtcNow);
    private DateTime lastAlertTime = DateTime.MinValue;
    private bool previousOverlayOpen;
    public string? LastDetectedGateName
    {
        get => Configuration.NextGateName;
        private set => Configuration.NextGateName = value;
    }

    public GateType? LastDetectedGateType
    {
        get => Configuration.NextGateType;
        private set => Configuration.NextGateType = value;
    }

    public string? CurrentGateName
    {
        get => Configuration.ActiveGateName;
        private set => Configuration.ActiveGateName = value;
    }

    public GateType? CurrentGateType
    {
        get => Configuration.ActiveGateType;
        private set => Configuration.ActiveGateType = value;
    }

    public DateTime? CurrentGateDetectedAt
    {
        get => Configuration.ActiveGateDetectedAt;
        private set => Configuration.ActiveGateDetectedAt = value;
    }

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
        PluginInterface.UiBuilder.OpenConfigUi += OpenConfigUi;
        PluginInterface.UiBuilder.OpenMainUi += OpenMainUi;

        dtrEntry = DtrBar.Get("GateNotifier");
        dtrEntry.OnClick = _ => ConfigWindow.Toggle();

        Framework.Update += OnFrameworkUpdate;
        ChatGui.ChatMessage += OnChatMessage;

        AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "Talk", OnTalkPostSetup);
        AddonLifecycle.RegisterListener(AddonEvent.PostRefresh, "Talk", OnTalkPostRefresh);


        Log.Information("GATE Notifier loaded.");
    }

    public void Dispose()
    {
        dtrEntry.Remove();

        Framework.Update -= OnFrameworkUpdate;
        ChatGui.ChatMessage -= OnChatMessage;

        AddonLifecycle.UnregisterListener(OnTalkPostSetup);
        AddonLifecycle.UnregisterListener(OnTalkPostRefresh);

        PluginInterface.UiBuilder.Draw -= WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= OpenConfigUi;
        PluginInterface.UiBuilder.OpenMainUi -= OpenMainUi;

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
        // Detect X button close: window was open last frame but now closed without config change
        if (previousOverlayOpen && !OverlayWindow.IsOpen && Configuration.ShowOverlay)
        {
            Configuration.ShowOverlay = false;
            Configuration.Save();
        }
        // Sync window to config (command, settings toggle)
        else if (OverlayWindow.IsOpen != Configuration.ShowOverlay)
        {
            OverlayWindow.IsOpen = Configuration.ShowOverlay;
        }
        previousOverlayOpen = OverlayWindow.IsOpen;

        var now = DateTime.UtcNow;
        var timeRemaining = GateScheduler.GetTimeUntilNextGate(now);

        // When a new cycle starts (countdown wrapped around), promote detected GATE to current
        if (timeRemaining > previousTimeRemaining)
        {
            CurrentGateName = LastDetectedGateName;
            CurrentGateType = LastDetectedGateType;
            CurrentGateDetectedAt = LastDetectedGateName != null ? now : null;
            LastDetectedGateName = null;
            LastDetectedGateType = null;
            Configuration.Save();
        }

        // Clear current GATE when its registration window has expired
        if (CurrentGateType != null && CurrentGateDetectedAt != null)
        {
            var windowSeconds = GateDefinitions.JoinWindowSeconds[CurrentGateType.Value];
            if ((now - CurrentGateDetectedAt.Value).TotalSeconds >= windowSeconds)
            {
                CurrentGateName = null;
                CurrentGateType = null;
                CurrentGateDetectedAt = null;
                Configuration.Save();
            }
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

            var joinRemaining = GetJoinTimeRemaining();
            if (CurrentGateName != null && joinRemaining != null)
            {
                var joinMin = (int)joinRemaining.Value.TotalMinutes;
                var joinSec = joinRemaining.Value.Seconds;
                dtrEntry.Text = $"{CurrentGateName} Join {joinMin}:{joinSec:D2}";

                dtrEntry.Tooltip = $"Registration open for {CurrentGateName}";
            }
            else if (LastDetectedGateName != null)
            {
                dtrEntry.Text = $"{LastDetectedGateName} T-{timeRemaining.Minutes}:{timeRemaining.Seconds:D2}";
                dtrEntry.Tooltip = $"Next GATE: {LastDetectedGateName}";
            }
            else
            {
                dtrEntry.Text = $"GATE T-{timeRemaining.Minutes}:{timeRemaining.Seconds:D2}";
                dtrEntry.Tooltip = $"Possible: {string.Join(" / ", GetPossibleGates())}";
            }
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

        // GATE announcements use chat type 68 (0x0044), a system announcement type.
        if (((int)type & 0x7F) != 68)
            return;

        var text = message.TextValue;

        // Detect registration closing
        if (text.Contains("Entries for the special limited-time event are now closed", StringComparison.OrdinalIgnoreCase))
        {
            if (CurrentGateName != null && CurrentGateDetectedAt != null)
            {
                var duration = (DateTime.UtcNow - CurrentGateDetectedAt.Value).TotalSeconds;
                Log.Information($"GATE registration closed: {CurrentGateName} after {duration:F0}s");
                LogGateTiming(CurrentGateName, CurrentGateDetectedAt.Value, DateTime.UtcNow, duration);
            }

            CurrentGateName = null;
            CurrentGateType = null;
            CurrentGateDetectedAt = null;
            Configuration.Save();
            return;
        }

        foreach (var (gateType, substring) in GateDefinitions.ChatSubstrings)
        {
            if (text.Contains(substring, StringComparison.OrdinalIgnoreCase))
            {
                var gateName = GateDefinitions.DisplayNames[gateType];
                Log.Information($"GATE detected: {gateName}");

                var timeRemaining = GateScheduler.GetTimeUntilNextGate(DateTime.UtcNow);

                // If we just crossed a cycle boundary (>15 min left) and have no
                // current GATE yet, the announcement is for the active slot.
                if (CurrentGateName == null && timeRemaining.TotalMinutes > 15)
                {
                    CurrentGateName = gateName;
                    CurrentGateType = gateType;
                    CurrentGateDetectedAt = DateTime.UtcNow;
                    Configuration.Save();
                }
                else
                {
                    LastDetectedGateName = gateName;
                    LastDetectedGateType = gateType;
                    Configuration.Save();
                }

                if (Configuration.EnabledGates.TryGetValue(gateType, out var enabled) && enabled)
                {
                    SendGateDetectedAlert(gateType);
                }

                break;
            }
        }
    }

    private unsafe void OnTalkPostSetup(AddonEvent type, AddonArgs args)
    {
        ReadTalkAddon((AtkUnitBase*)(nint)args.Addon);
    }

    private unsafe void OnTalkPostRefresh(AddonEvent type, AddonArgs args)
    {
        ReadTalkAddon((AtkUnitBase*)(nint)args.Addon);
    }

    private bool inGateKeeperDialogue;

    private unsafe void ReadTalkAddon(AtkUnitBase* addon)
    {
        if (addon == null)
            return;

        var talkAddon = (AddonTalk*)addon;
        var speakerNode = talkAddon->AtkTextNode220;
        var textNode = talkAddon->AtkTextNode228;

        if (speakerNode == null || textNode == null)
            return;

        var speaker = speakerNode->NodeText.StringPtr.AsDalamudSeString().TextValue.Trim();
        var text = textNode->NodeText.StringPtr.AsDalamudSeString().TextValue.Trim();

        if (speaker.Contains("GATE Keeper", StringComparison.OrdinalIgnoreCase))
        {
            inGateKeeperDialogue = true;

            // Only detect from "next scheduled event" dialogue, not current/active GATE dialogue
            if (!text.Contains("next scheduled event", StringComparison.OrdinalIgnoreCase))
                return;

            foreach (var (gateType, substring) in GateDefinitions.ChatSubstrings)
            {
                if (text.Contains(substring, StringComparison.OrdinalIgnoreCase))
                {
                    var gateName = GateDefinitions.DisplayNames[gateType];
                    Log.Information($"GATE Keeper revealed next GATE: {gateName}");

                    LastDetectedGateName = gateName;
                    LastDetectedGateType = gateType;
                    Configuration.Save();

                    if (Configuration.EnabledGates.TryGetValue(gateType, out var enabled) && enabled)
                    {
                        SendGateDetectedAlert(gateType);
                    }

                    break;
                }
            }
        }
        else if (inGateKeeperDialogue && string.IsNullOrEmpty(speaker))
        {
            // Second dialogue line: "The next GATE will be held at 10:40 p.m. in Round Square."
            var match = Regex.Match(text, @"at (\d{1,2}:\d{2} [ap]\.m\.) in (.+)\.", RegexOptions.IgnoreCase);
            if (match.Success)
            {
                var time = match.Groups[1].Value;
                var location = match.Groups[2].Value;
                Log.Information($"GATE Keeper: next GATE at {time} in {location}");
            }

            inGateKeeperDialogue = false;
        }
        else
        {
            inGateKeeperDialogue = false;
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
                ? $"[Gold Saucer] {LastDetectedGateName} starts in {minutes} minute!"
                : $"[Gold Saucer] {LastDetectedGateName} starts in {minutes} minutes!";
        }
        else
        {
            var possible = string.Join(" / ", GetPossibleGates());
            msg = minutes == 1
                ? $"[Gold Saucer] {minutes} minute! Possible: {possible}"
                : $"[Gold Saucer] {minutes} minutes! Possible: {possible}";
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
        var msg = $"[Gold Saucer] Upcoming GATE: {name}!";

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

    public TimeSpan? GetJoinTimeRemaining()
    {
        if (CurrentGateType == null || CurrentGateDetectedAt == null)
            return null;

        var windowSeconds = GateDefinitions.JoinWindowSeconds[CurrentGateType.Value];
        var elapsed = (DateTime.UtcNow - CurrentGateDetectedAt.Value).TotalSeconds;
        var remaining = windowSeconds - elapsed;
        return remaining > 0 ? TimeSpan.FromSeconds(remaining) : null;
    }

    private void LogGateTiming(string gateName, DateTime openedUtc, DateTime closedUtc, double durationSeconds)
    {
        try
        {
            var dir = PluginInterface.GetPluginConfigDirectory();
            var path = Path.Combine(dir, "gate_timings.csv");
            var exists = File.Exists(path);
            using var writer = new StreamWriter(path, append: true);
            if (!exists)
                writer.WriteLine("gate,opened_utc,closed_utc,duration_seconds");
            writer.WriteLine($"{gateName},{openedUtc:O},{closedUtc:O},{durationSeconds:F1}");
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to log gate timing: {ex.Message}");
        }
    }

    public void OpenConfigUi() => ConfigWindow.IsOpen = true;
    public void OpenMainUi()
    {
        Configuration.ShowOverlay = true;
        Configuration.Save();
    }

    public void ToggleConfigUi() => ConfigWindow.Toggle();
}
