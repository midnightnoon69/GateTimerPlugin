using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Command;
using Dalamud.Game.Chat;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using FFXIVClientStructs.FFXIV.Client.Game.GoldSaucer;
using FFXIVClientStructs.FFXIV.Client.Network;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Dalamud.Game.Gui.Dtr;
using Dalamud.Hooking;
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
    [PluginService] internal static IPlayerState PlayerState { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static ISigScanner SigScanner { get; private set; } = null!;
    [PluginService] internal static IGameInteropProvider GameInteropProvider { get; private set; } = null!;

    private const string CommandName = "/gate";
    private const string Tag = "[GateNotifier]";

    public Configuration Configuration { get; init; }

    public readonly WindowSystem WindowSystem = new("GateNotifier");
    private ConfigWindow ConfigWindow { get; init; }
    private OverlayWindow OverlayWindow { get; init; }

    private readonly IDtrBarEntry dtrEntry;
    public ApiService ApiService { get; init; }

    private TimeSpan previousTimeRemaining = GateScheduler.GetTimeUntilNextGate(DateTime.UtcNow);
    private DateTime lastAlertTime = DateTime.MinValue;
    private bool previousOverlayOpen;
    private byte lastDirectorGateType;
    private int lastDirectorSlot = -1;
    private DateTime lastDirectorChangeTime = DateTime.MinValue;

    // Packet hook for cycle counter (opcode 0x0217)
    private unsafe delegate void OnReceivePacketDelegate(NetworkModulePacketReceiverCallback* self, uint opcode, nint data);
    private Hook<OnReceivePacketDelegate>? packetReceiveHook;
    public byte? LastCycleCounter { get; private set; }
    public DateTime? LastCycleCounterTime { get; private set; }

    public string PluginVersion => PluginInterface.Manifest.AssemblyVersion.ToString();
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

        ApiService = new ApiService(Configuration, Log);

        ConfigWindow = new ConfigWindow(this);
        OverlayWindow = new OverlayWindow(this);

        WindowSystem.AddWindow(ConfigWindow);
        WindowSystem.AddWindow(OverlayWindow);

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Toggle overlay. /gate help for all commands.",
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

        InstallPacketHook();

        var version = PluginInterface.Manifest.AssemblyVersion;
        Log.Information($"{Tag} v{version} loaded.");
    }

    public void Dispose()
    {
        packetReceiveHook?.Dispose();
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

        ApiService.Dispose();
    }

    private void OnCommand(string command, string args)
    {
        var trimmed = args.Trim();
        if (trimmed.Equals("config", StringComparison.OrdinalIgnoreCase))
        {
            ConfigWindow.Toggle();
        }
        else if (trimmed.Equals("clear", StringComparison.OrdinalIgnoreCase))
        {
            Log.Information($"{Tag} /gate clear: resetting all local state");
            CurrentGateName = null;
            CurrentGateType = null;
            CurrentGateDetectedAt = null;
            LastDetectedGateName = null;
            LastDetectedGateType = null;
            ApiService.ClearApiState();
            Configuration.Save();
            ChatGui.Print($"{Tag} Local data cleared.");
        }
        else if (trimmed.Equals("help", StringComparison.OrdinalIgnoreCase))
        {
            ChatGui.Print($"{Tag} Commands:");
            ChatGui.Print($"  /gate         — Toggle overlay");
            ChatGui.Print($"  /gate config  — Open settings");
            ChatGui.Print($"  /gate clear   — Reset all local state");
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

        // Poll Director for early GATE detection (up to 15+ min before broadcast)
        PollDirectorForGateChange();

        var now = DateTime.UtcNow;
        var timeRemaining = GateScheduler.GetTimeUntilNextGate(now);

        // When a new cycle starts (countdown wrapped around), promote detected GATE to current
        if (timeRemaining > previousTimeRemaining)
        {
            // Only promote if current isn't already set (chat "underway" may have set it first)
            if (CurrentGateName == null)
            {
                CurrentGateName = LastDetectedGateName;
                CurrentGateType = LastDetectedGateType;
                CurrentGateDetectedAt = LastDetectedGateName != null ? GateScheduler.GetCurrentGateTime(now) : null;
            }
            LastDetectedGateName = null;
            LastDetectedGateType = null;
            ApiService.ClearApiState();
            Configuration.Save();
            Log.Information($"{Tag} Slot boundary: current={CurrentGateName ?? "none"}, lastDetected cleared");
        }

        // Clear current GATE when its registration window has expired
        if (CurrentGateType != null && CurrentGateDetectedAt != null)
        {
            var windowSeconds = GateDefinitions.JoinWindowSeconds[CurrentGateType.Value];
            if ((now - CurrentGateDetectedAt.Value).TotalSeconds >= windowSeconds)
            {
                Log.Information($"{Tag} State: join window expired for {CurrentGateName}");
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

        // Poll API if no local detection for next GATE
        if (LastDetectedGateName == null)
        {
            if (Configuration.DebugApiSimulation)
            {
                // Simulate API data with a random gate from the next slot's pool
                if (ApiService.ApiNextGateName == null)
                {
                    var possibleNext = GetPossibleGates();
                    if (possibleNext.Length > 0)
                    {
                        var pick = possibleNext[Random.Shared.Next(possibleNext.Length)];
                        ApiService.SetSimulatedNextGate(pick, GateScheduler.GetNextGateTime(now).Minute, now.AddMinutes(20));
                        Log.Information($"{Tag} API sim: {pick}");
                    }
                }
            }
            else
            {
                var world = GetCurrentWorldName();
                ApiService.PollIfNeeded(world);
            }

            // If API next gate data expired, clear it
            if (ApiService.ApiNextGateExpiresAt != null && ApiService.ApiNextGateExpiresAt <= now)
            {
                ApiService.ClearApiState();
            }
        }
        else
        {
            // Local detection takes priority — clear API state
            ApiService.ClearApiState();
        }

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
            else if (ApiService.ApiNextGateName != null)
            {
                dtrEntry.Text = $"{ApiService.ApiNextGateName} T-{timeRemaining.Minutes}:{timeRemaining.Seconds:D2}";
                dtrEntry.Tooltip = $"Next GATE: {ApiService.ApiNextGateName} (reported)";
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

    private void OnChatMessage(IHandleableChatMessage msg)
    {
        if (!Configuration.EnableChatDetection)
            return;

        // GATE announcements use chat type 68 (0x0044), a system announcement type.
        if (((int)msg.LogKind & 0x7F) != 68)
            return;

        var text = msg.Message.TextValue;

        // Detect registration closing (two variants by venue)
        // Round Square: "Entries for the special limited-time event are now closed"
        // Event Square: "Entries for the main stage event are now closed"
        if (text.Contains("Entries for the special limited-time event are now closed", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Entries for the main stage event are now closed", StringComparison.OrdinalIgnoreCase))
        {
            if (CurrentGateName != null && CurrentGateDetectedAt != null)
            {
                var duration = (DateTime.UtcNow - CurrentGateDetectedAt.Value).TotalSeconds;
                Log.Information($"{Tag} GATE registration closed: {CurrentGateName} after {duration:F0}s");

                var world = GetCurrentWorldName();
                if (world != null)
                    ApiService.ReportEvent(world, "gate_registration_closed",
                        $"{CurrentGateName}|{duration:F1}s|slot={GetCurrentSlot()}",
                        PluginVersion, LastCycleCounter);
            }

            Log.Information($"{Tag} State: registration closed for {CurrentGateName}, keeping as active (event still running)");
            // Don't clear CurrentGateName — the GATE is still running, just registration ended.
            // Clear DetectedAt so the join timer disappears from the overlay.
            CurrentGateDetectedAt = null;
            Configuration.Save();
            return;
        }

        // Detect GATE concluded (end of the event itself)
        if (text.Contains("The special limited-time event has concluded", StringComparison.OrdinalIgnoreCase))
        {
            Log.Information($"{Tag} State: GATE concluded, clearing current={CurrentGateName}, lastDetected={LastDetectedGateName}");
            CurrentGateName = null;
            CurrentGateType = null;
            CurrentGateDetectedAt = null;
            LastDetectedGateName = null;
            LastDetectedGateType = null;
            Configuration.Save();
            var world = GetCurrentWorldName();
            if (world != null)
                ApiService.ReportEvent(world, "gate_concluded",
                    $"slot={GetCurrentSlot()}", PluginVersion, LastCycleCounter);
            return;
        }

        // Detect "is now underway" — confirms active GATE with name in quotes
        // e.g. 'The limited-time event "The Slice Is Right" is now underway in Event Square.'
        if (text.Contains("is now underway", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var (gateType, substring) in GateDefinitions.ChatSubstrings)
            {
                if (text.Contains(substring, StringComparison.OrdinalIgnoreCase))
                {
                    var gateName = GateDefinitions.DisplayNames[gateType];
                    Log.Information($"{Tag} GATE now underway: {gateName} (current was {CurrentGateName ?? "none"})");

                    // Always report gate_underway event (PKT 0x022C may have already set state)
                    var world = GetCurrentWorldName();
                    if (world != null)
                    {
                        ApiService.ReportEvent(world, "gate_underway",
                            $"{gateName}|slot={GetCurrentSlot()}",
                            PluginVersion, LastCycleCounter);
                    }

                    if (CurrentGateName == null)
                    {
                        CurrentGateName = gateName;
                        CurrentGateType = gateType;
                        CurrentGateDetectedAt = GateScheduler.GetCurrentGateTime(DateTime.UtcNow);
                        Configuration.Save();
                        Log.Information($"{Tag} State: set current={gateName}");

                        if (Configuration.EnabledGates.TryGetValue(gateType, out var enabled) && enabled)
                        {
                            SendGateDetectedAlert(gateType);
                        }
                    }

                    break;
                }
            }

            return;
        }

        // Detect GATE announcement (pre-start)
        foreach (var (gateType, substring) in GateDefinitions.ChatSubstrings)
        {
            if (text.Contains(substring, StringComparison.OrdinalIgnoreCase))
            {
                var gateName = GateDefinitions.DisplayNames[gateType];
                Log.Information($"{Tag} GATE detected: {gateName}");

                var timeRemaining = GateScheduler.GetTimeUntilNextGate(DateTime.UtcNow);

                // If we just crossed a cycle boundary (>15 min left) and have no
                // current GATE yet, the announcement is for the active slot.
                if (CurrentGateName == null && timeRemaining.TotalMinutes > 15)
                {
                    CurrentGateName = gateName;
                    CurrentGateType = gateType;
                    CurrentGateDetectedAt = GateScheduler.GetCurrentGateTime(DateTime.UtcNow);
                    Configuration.Save();

                    var world = GetCurrentWorldName();
                    if (world != null)
                        ApiService.ReportGate(world, gateName, GetCurrentSlot(), "chat_announce", text,
                            pluginVersion: PluginVersion, cycleCounter: LastCycleCounter);
                }
                else
                {
                    // Only set if no local source has already detected this slot's GATE
                    if (LastDetectedGateName == null)
                    {
                        Log.Information($"{Tag} State: set lastDetected={gateName} (from chat announce)");
                        LastDetectedGateName = gateName;
                        LastDetectedGateType = gateType;
                        Configuration.Save();

                        if (Configuration.EnabledGates.TryGetValue(gateType, out var enabled) && enabled)
                        {
                            SendGateDetectedAlert(gateType);
                        }
                    }
                    else
                    {
                        Log.Information($"{Tag} Chat: skipping — already detected '{LastDetectedGateName}' from earlier source");
                    }

                    var nextSlot = GateScheduler.GetNextGateTime(DateTime.UtcNow);
                    var world = GetCurrentWorldName();
                    if (world != null)
                        ApiService.ReportGate(world, gateName, nextSlot.Minute, "chat_announce", text,
                            pluginVersion: PluginVersion, cycleCounter: LastCycleCounter);
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

        // Debug: log all NPC dialogue in Gold Saucer to discover registration NPC text
        Log.Information($"{Tag} Talk: speaker=\"{speaker}\", text=\"{text}\"");

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
                    Log.Information($"{Tag} GATE Keeper revealed next GATE: {gateName}");

                    // Update if no detection yet, or if NPC now shows a different gate (previous was stale)
                    if (LastDetectedGateName == null || LastDetectedGateName != gateName)
                    {
                        if (LastDetectedGateName != null)
                            Log.Information($"{Tag} GATE Keeper: updating from '{LastDetectedGateName}' to '{gateName}'");
                        Log.Information($"{Tag} State: set lastDetected={gateName} (from NPC)");
                        LastDetectedGateName = gateName;
                        LastDetectedGateType = gateType;
                        Configuration.Save();

                        if (Configuration.EnabledGates.TryGetValue(gateType, out var enabled) && enabled)
                        {
                            SendGateDetectedAlert(gateType);
                        }
                    }
                    else
                    {
                        Log.Information($"{Tag} GATE Keeper: skipping — already detected '{LastDetectedGateName}'");
                    }

                    // Report to API, but skip if NPC is still showing the currently active GATE
                    // (GATE Keeper doesn't update until the current GATE ends)
                    if (CurrentGateName != null && CurrentGateName == gateName)
                    {
                        Log.Information($"{Tag} GATE Keeper: skipping API report — NPC still showing active GATE '{gateName}'");
                    }
                    else
                    {
                        var nextSlot = GateScheduler.GetNextGateTime(DateTime.UtcNow);
                        var world = GetCurrentWorldName();
                        Log.Information($"{Tag} API POST: world={world ?? "null"}, gate={gateName}, slot={nextSlot.Minute}");
                        if (world != null)
                            ApiService.ReportGate(world, gateName, nextSlot.Minute, "npc_gate_keeper", text,
                                pluginVersion: PluginVersion, cycleCounter: LastCycleCounter);
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
                Log.Information($"{Tag} GATE Keeper: next GATE at {time} in {location}");
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

    // ── Packet hook: GATE announcement (0x022C) + cycle counter ──

    private unsafe void InstallPacketHook()
    {
        try
        {
            // Resolve OnReceivePacket from live vtable (survives patches as long as vtable index is stable).
            // NetworkModulePacketReceiverCallback vtable[8] = OnReceivePacket(uint opcode, nint data)
            // Verified via: proxy vtable[8], *proxy+0x10 vtable[4], *proxy+0x10+0x08 vtable[1]
            const int vtableIndex = 8;

            var fw = FFXIVClientStructs.FFXIV.Client.System.Framework.Framework.Instance();
            if (fw == null)
            {
                Log.Warning($"{Tag} Packet hook: Framework instance is null (too early?)");
                return;
            }

            var proxy = fw->NetworkModuleProxy;
            if (proxy == null)
            {
                Log.Warning($"{Tag} Packet hook: NetworkModuleProxy is null (too early?)");
                return;
            }

            // The proxy itself is a NetworkModulePacketReceiverCallback subclass — read vtable directly
            var vtable = *(nint**)proxy;
            var addr = vtable[vtableIndex];

            if (addr == nint.Zero)
            {
                Log.Warning($"{Tag} Packet hook: vtable[{vtableIndex}] is null");
                return;
            }

            // Sanity check: address should be within the game module
            var moduleBase = SigScanner.Module.BaseAddress;
            var moduleEnd = moduleBase + SigScanner.Module.ModuleMemorySize;
            if (addr < moduleBase || addr >= moduleEnd)
            {
                Log.Warning($"{Tag} Packet hook: vtable[{vtableIndex}] = 0x{addr:X} is outside game module — skipping");
                return;
            }

            packetReceiveHook = GameInteropProvider.HookFromAddress<OnReceivePacketDelegate>(addr,
                (NetworkModulePacketReceiverCallback* self, uint opcode, nint data) =>
                {
                    packetReceiveHook!.Original(self, opcode, data);
                    OnPacketReceived(opcode, data);
                });
            packetReceiveHook.Enable();
            var rva = addr - moduleBase;
            Log.Information($"{Tag} Packet hook installed via vtable[{vtableIndex}] (RVA 0x{rva:X})");
        }
        catch (Exception ex)
        {
            Log.Warning($"{Tag} Packet hook failed (non-fatal): {ex.Message}");
        }
    }

    private unsafe void OnPacketReceived(uint hookOpcode, nint data)
    {
        try
        {
            // IPC opcode is at data[2:4] (little-endian uint16)
            var ipcOpcode = (ushort)(*(byte*)(data + 2) | (*(byte*)(data + 3) << 8));

            // 0x022C = GATE announcement (post-patch, was 0x00B4)
            // Fires at :x0:00 with gate_type at data[36] and position_type at data[32]
            if (ipcOpcode == 0x022C)
            {
                OnGateAnnouncementPacket(data);
                return;
            }

            // 0x03C9 = GATE director event stream (post-patch, was 0x0217)
            // Periodic heartbeat every 10s + burst at transitions
            if (ipcOpcode == 0x03C9)
            {
                OnDirectorEventPacket(data);
                return;
            }
        }
        catch (Exception ex)
        {
            Log.Error($"{Tag} Packet handler error: {ex.Message}");
        }
    }

    /// <summary>
    /// Handle 0x022C GATE announcement packet.
    /// Payload: data[32] = position_type, data[36] = gate_type_byte.
    /// Fires once at each :00/:20/:40 transition.
    /// </summary>
    private unsafe void OnGateAnnouncementPacket(nint data)
    {
        // Validate this is a GATE announcement (check for Gold Saucer content ID at data[24])
        // data[24:28] = 10 6B 0F 00 (0x000F6B10) for Gold Saucer GATE announcements
        var contentId = *(uint*)(data + 24);
        if (contentId != 0x000F6B10)
            return;

        var positionType = *(byte*)(data + 32);
        var gateTypeByte = *(byte*)(data + 36);

        var mapped = MapDirectorGateType(gateTypeByte);
        if (mapped == null)
        {
            Log.Warning($"{Tag} PKT 0x022C: unknown gate_type={gateTypeByte}, pos={positionType}");
            return;
        }

        var gateName = GateDefinitions.DisplayNames[mapped.Value];
        Log.Information($"{Tag} PKT 0x022C GATE announcement: {gateName} (type={gateTypeByte}, pos={positionType})");

        // Report to API
        var now = DateTime.UtcNow;
        var currentSlot = GateScheduler.GetCurrentGateTime(now);
        var world = GetCurrentWorldName();
        if (world != null)
        {
            ApiService.ReportGate(world, gateName, currentSlot.Minute, "packet_announce",
                $"type={gateTypeByte},pos={positionType}",
                gateTypeByte: gateTypeByte, positionType: positionType, flags: 0,
                pluginVersion: PluginVersion, cycleCounter: LastCycleCounter);
        }

        // Use as GATE detection — this is the fastest source (direct from server packet)
        if (CurrentGateName != gateName)
        {
            Log.Information($"{Tag} State: set current={gateName} (from packet 0x022C)");
            CurrentGateName = gateName;
            CurrentGateType = mapped.Value;
            CurrentGateDetectedAt = GateScheduler.GetCurrentGateTime(now);
            Configuration.Save();

            if (Configuration.EnabledGates.TryGetValue(mapped.Value, out var enabled) && enabled)
            {
                SendGateDetectedAlert(mapped.Value);
            }
        }
    }

    /// <summary>
    /// Handle 0x03C9 director event stream packet.
    /// Periodic heartbeat: data[16:20] = 18 00 00 00.
    /// Transition burst: data[16:20] = 64/6D 00 00 00 with content ID at data[20:24].
    /// </summary>
    private unsafe void OnDirectorEventPacket(nint data)
    {
        var payload16 = *(uint*)(data + 16);

        // Skip periodic heartbeats (payload16 = 0x18 = 24)
        if (payload16 == 0x18)
            return;

        // Log non-heartbeat director events for analysis
        var hexDump = new System.Text.StringBuilder(128);
        for (var i = 0; i < 40 && i < 128; i++)
            hexDump.Append(((byte*)data)[i].ToString("X2")).Append(' ');
        Log.Debug($"{Tag} PKT 0x03C9 event: payload16=0x{payload16:X} data[0..39]: {hexDump}");
    }

    // RESEARCH METHODS REMOVED — use GateAnalyzer plugin (/ga) for:
    // dump, scan, post, exd, sig, log, ctx

    /// <summary>
    /// Poll GoldSaucerManager's GFateDirector for early GATE detection.
    /// The Director updates when the current GATE concludes — potentially 15+ min
    /// before the broadcast chat announcement at the next :00/:20/:40.
    /// </summary>
    private unsafe void PollDirectorForGateChange()
    {
        var mgr = GoldSaucerManager.Instance();
        if (mgr == null)
            return;

        var director = mgr->CurrentGFateDirector;
        if (director == null)
        {
            lastDirectorGateType = 0;
            return;
        }

        var gateTypeByte = (byte)director->GateType;

        // Reset tracking when slot changes (handles same GATE type in consecutive slots, e.g. AFO→AFO)
        var slotMinute = (DateTime.UtcNow.Minute / 20) * 20;
        if (slotMinute != lastDirectorSlot)
        {
            lastDirectorGateType = 0;
            lastDirectorSlot = slotMinute;
        }

        // Detect change
        if (gateTypeByte == lastDirectorGateType || gateTypeByte == 0)
        {
            lastDirectorGateType = gateTypeByte;
            return;
        }

        // Debounce: ignore changes within 30s of the last one
        var now = DateTime.UtcNow;
        if ((now - lastDirectorChangeTime).TotalSeconds < 30)
        {
            lastDirectorGateType = gateTypeByte;
            return;
        }

        lastDirectorGateType = gateTypeByte;
        lastDirectorChangeTime = now;

        var mapped = MapDirectorGateType(gateTypeByte);
        if (mapped == null)
        {
            Log.Warning($"{Tag} Director: unknown GateType byte {gateTypeByte}");
            return;
        }

        var gateName = GateDefinitions.DisplayNames[mapped.Value];
        var endTs = director->EndTimestamp;
        var posType = (byte)director->GatePositionType;
        var flags = (uint)director->Flags;
        Log.Information($"{Tag} Director: GATE change [{DateTime.UtcNow:O}] -> {gateName} (byte={gateTypeByte}, pos={posType}, endTs={endTs}, flags=0x{flags:X8})");

        // Report to API (structured fields for data collection)
        var currentSlot = GateScheduler.GetCurrentGateTime(now);
        var world = GetCurrentWorldName();
        if (world != null)
        {
            ApiService.ReportGate(world, gateName, currentSlot.Minute, "memory_director",
                $"byte={gateTypeByte},pos={posType},endTs={endTs},flags=0x{flags:X8}",
                gateTypeByte: gateTypeByte, positionType: posType, flags: (int)flags,
                pluginVersion: PluginVersion, cycleCounter: LastCycleCounter);
        }

        // Skip if this is the same GATE we already know about
        // (director briefly goes null during zone transitions, causing spurious re-detection)
        if (CurrentGateName == gateName)
        {
            Log.Debug($"{Tag} Director: skip — already current GATE '{CurrentGateName}'");
            return;
        }

        // Director detects the ACTIVE gate, so set CurrentGateName (not LastDetected).
        // LastDetectedGateName is for the upcoming/next gate (from NPC or pre-announcement).
        Log.Information($"{Tag} State: set current={gateName} (from director)");
        CurrentGateName = gateName;
        CurrentGateType = mapped.Value;
        CurrentGateDetectedAt = GateScheduler.GetCurrentGateTime(now);
        Configuration.Save();

        if (Configuration.EnabledGates.TryGetValue(mapped.Value, out var enabled) && enabled)
        {
            SendGateDetectedAlert(mapped.Value);
        }
    }

    /// <summary>
    /// Map FFXIVClientStructs GFateDirector.GateType byte values to our GateType enum.
    /// Client enum: None=0, Cliffhanger=1, VaseOff=2, Skinchange=3, TimeOfMyLife=4,
    ///              AWTW=5, LoF=6, AFO=7, SIR=8
    /// </summary>
    private static GateType? MapDirectorGateType(byte gateTypeByte)
    {
        return gateTypeByte switch
        {
            1 => GateType.Cliffhanger,
            5 => GateType.AnyWayTheWindBlows,
            6 => GateType.LeapOfFaith,
            7 => GateType.AirForceOne,
            8 => GateType.TheSliceIsRight,
            _ => null, // 0=None, 2-4=retired GATEs
        };
    }

    private string? GetCurrentWorldName()
    {
        if (!PlayerState.IsLoaded)
            return null;
        var world = PlayerState.CurrentWorld.ValueNullable;
        if (world == null)
            return null;
        var name = world.Value.Name.ExtractText();
        return string.IsNullOrEmpty(name) ? null : name;
    }

    private int GetCurrentSlot()
    {
        return GateScheduler.GetCurrentGateTime(DateTime.UtcNow).Minute;
    }

    public void OpenConfigUi() => ConfigWindow.IsOpen = true;
    public void OpenMainUi()
    {
        Configuration.ShowOverlay = true;
        Configuration.Save();
    }

    public void ToggleConfigUi() => ConfigWindow.Toggle();
}
