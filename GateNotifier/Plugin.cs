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
using FFXIVClientStructs.FFXIV.Client.Game.GoldSaucer;
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
    [PluginService] internal static IPlayerState PlayerState { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;

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
    private bool continuousLogging;
    private DateTime lastContinuousLogTime = DateTime.MinValue;
    private int lastLoggedEndTimestamp;
    private string? npcPredictedNextGate;
    private DateTime npcPredictionTime = DateTime.MinValue;
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
            HelpMessage = "Toggle overlay. Subcommands: config, clear, dump, log, scan",
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


        var version = PluginInterface.Manifest.AssemblyVersion;
        Log.Information($"{Tag} v{version} loaded.");
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
        else if (trimmed.Equals("dump", StringComparison.OrdinalIgnoreCase))
        {
            DumpDirectorState();
        }
        else if (trimmed.Equals("log", StringComparison.OrdinalIgnoreCase))
        {
            continuousLogging = !continuousLogging;
            ChatGui.Print($"{Tag} Continuous Director logging: {(continuousLogging ? "ON" : "OFF")}");
        }
        else if (trimmed.Equals("scan", StringComparison.OrdinalIgnoreCase))
        {
            ScanGoldSaucerMemory();
        }
        else if (trimmed.Equals("post", StringComparison.OrdinalIgnoreCase))
        {
            ForceDirectorPost();
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

        // Continuous logging mode: log Director state every 10s when fields change
        if (continuousLogging)
            ContinuousDirectorLog();

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
            ApiService.ClearApiState();
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

    private void OnChatMessage(XivChatType type, int timestamp, ref SeString sender, ref SeString message, ref bool isHandled)
    {
        if (!Configuration.EnableChatDetection)
            return;

        // GATE announcements use chat type 68 (0x0044), a system announcement type.
        if (((int)type & 0x7F) != 68)
            return;

        var text = message.TextValue;

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
                LogGateTiming(CurrentGateName, CurrentGateDetectedAt.Value, DateTime.UtcNow, duration);

                var world = GetCurrentWorldName();
                if (world != null)
                    ApiService.ReportEvent(world, "gate_registration_closed",
                        $"{CurrentGateName}|{duration:F1}s|slot={GetCurrentSlot()}");
            }

            CurrentGateName = null;
            CurrentGateType = null;
            CurrentGateDetectedAt = null;
            Configuration.Save();
            return;
        }

        // Detect GATE concluded (end of the event itself)
        if (text.Contains("The special limited-time event has concluded", StringComparison.OrdinalIgnoreCase))
        {
            Log.Information($"{Tag} GATE concluded.");
            CurrentGateName = null;
            CurrentGateType = null;
            CurrentGateDetectedAt = null;
            LastDetectedGateName = null;
            LastDetectedGateType = null;
            Configuration.Save();
            var world = GetCurrentWorldName();
            if (world != null)
                ApiService.ReportEvent(world, "gate_concluded",
                    $"slot={GetCurrentSlot()}");
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
                    Log.Information($"{Tag} GATE now underway: {gateName}");

                    if (CurrentGateName == null)
                    {
                        CurrentGateName = gateName;
                        CurrentGateType = gateType;
                        CurrentGateDetectedAt = DateTime.UtcNow;
                        Configuration.Save();

                        // API POST is handled by PollDirectorForGateChange (has structured fields)
                        var world = GetCurrentWorldName();
                        if (world != null)
                        {
                            ApiService.ReportEvent(world, "gate_underway",
                                $"{gateName}|slot={GetCurrentSlot()}");
                        }

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
        var matched = false;
        foreach (var (gateType, substring) in GateDefinitions.ChatSubstrings)
        {
            if (text.Contains(substring, StringComparison.OrdinalIgnoreCase))
            {
                matched = true;
                var gateName = GateDefinitions.DisplayNames[gateType];
                Log.Information($"{Tag} GATE detected: {gateName}");

                // Check for NPC prediction mismatch (sequence break detection)
                CheckNpcPredictionMismatch(gateName);

                var timeRemaining = GateScheduler.GetTimeUntilNextGate(DateTime.UtcNow);

                // If we just crossed a cycle boundary (>15 min left) and have no
                // current GATE yet, the announcement is for the active slot.
                if (CurrentGateName == null && timeRemaining.TotalMinutes > 15)
                {
                    CurrentGateName = gateName;
                    CurrentGateType = gateType;
                    CurrentGateDetectedAt = DateTime.UtcNow;
                    Configuration.Save();

                    var world = GetCurrentWorldName();
                    if (world != null)
                        ApiService.ReportGate(world, gateName, GetCurrentSlot(), "chat_announce", text);
                }
                else
                {
                    // Only set if no local source has already detected this slot's GATE
                    if (LastDetectedGateName == null)
                    {
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
                        ApiService.ReportGate(world, gateName, nextSlot.Minute, "chat_announce", text);
                }

                break;
            }
        }

        if (!matched)
        {
            var eventType = ClassifyGoldSaucerEvent(text);
            if (eventType == "gate_in_progress")
                return;

            Log.Information($"{Tag} Event {eventType}: {text}");

            var world = GetCurrentWorldName();
            if (world != null)
                ApiService.ReportEvent(world, eventType, text);
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

                    // Track NPC prediction for mismatch detection
                    npcPredictedNextGate = gateName;
                    npcPredictionTime = DateTime.UtcNow;

                    // Update if no detection yet, or if NPC now shows a different gate (previous was stale)
                    if (LastDetectedGateName == null || LastDetectedGateName != gateName)
                    {
                        if (LastDetectedGateName != null)
                            Log.Information($"{Tag} GATE Keeper: updating from '{LastDetectedGateName}' to '{gateName}'");
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
                            ApiService.ReportGate(world, gateName, nextSlot.Minute, "npc_gate_keeper", text);
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
            Log.Error($"{Tag} Failed to log gate timing: {ex.Message}");
        }
    }

    /// <summary>
    /// Dump all readable GFateDirector fields to chat and log for analysis.
    /// </summary>
    private unsafe void DumpDirectorState()
    {
        var mgr = GoldSaucerManager.Instance();
        if (mgr == null)
        {
            ChatGui.Print($"{Tag} GoldSaucerManager not available (not in Gold Saucer?)");
            return;
        }

        var director = mgr->CurrentGFateDirector;
        if (director == null)
        {
            ChatGui.Print($"{Tag} No GFateDirector active.");
            return;
        }

        var gateType = (byte)director->GateType;
        var posType = (byte)director->GatePositionType;
        var endTs = director->EndTimestamp;
        var flags = (uint)director->Flags;

        var mapped = MapDirectorGateType(gateType);
        var gateName = mapped != null ? GateDefinitions.DisplayNames[mapped.Value] : $"Unknown({gateType})";

        var now = DateTime.UtcNow;
        var endDt = endTs > 0 ? DateTimeOffset.FromUnixTimeSeconds(endTs).UtcDateTime : (DateTime?)null;
        var remaining = endDt.HasValue ? (endDt.Value - now).TotalSeconds : -1;

        var msg = $"{Tag} Director dump @ {now:HH:mm:ss} UTC\n" +
                  $"  GateType: {gateType} ({gateName})\n" +
                  $"  PositionType: {posType}\n" +
                  $"  EndTimestamp: {endTs} ({endDt:HH:mm:ss} UTC, {remaining:F0}s remaining)\n" +
                  $"  Flags: 0x{flags:X8}";

        ChatGui.Print(msg);
        Log.Information(msg);

        // Also dump raw bytes around the Director struct for analysis
        var dirPtr = (byte*)director;
        // Dump key regions
        var regions = new (int offset, int size, string label)[]
        {
            (0x788, 32, "EndTs/BGM/Screen/GateType/Flags"),
        };

        foreach (var (offset, size, label) in regions)
        {
            var hex = new System.Text.StringBuilder();
            for (var i = 0; i < size; i++)
            {
                hex.Append($"{dirPtr[offset + i]:X2} ");
            }
            var hexMsg = $"  [{label}] +0x{offset:X}: {hex.ToString().Trim()}";
            ChatGui.Print(hexMsg);
            Log.Information(hexMsg);
        }

        // Log to CSV for analysis
        LogDirectorDump(now, gateType, gateName, posType, endTs, flags);
    }

    /// <summary>
    /// Dump GoldSaucerManager and GFateDirector memory to hex files for diffing.
    /// Run before and after a GATE announcement, then diff to find where "next GATE" lives.
    /// </summary>
    private unsafe void ScanGoldSaucerMemory()
    {
        var mgr = GoldSaucerManager.Instance();
        if (mgr == null)
        {
            ChatGui.Print($"{Tag} GoldSaucerManager not available.");
            return;
        }

        var dir = PluginInterface.GetPluginConfigDirectory();
        var timestamp = DateTime.UtcNow.ToString("HHmmss");

        // Dump GoldSaucerManager — expanded to catch pool config and schedule data
        var mgrPtr = (byte*)mgr;
        var mgrSize = 0x840; // 14 blocks × 0x90 = 0x7E0 + header at 0x78 = 0x858, need at least 0x838 for block 13 floats
        var mgrPath = Path.Combine(dir, $"gsm_{timestamp}.hex");
        DumpMemoryToFile(mgrPtr, mgrSize, mgrPath, "GoldSaucerManager");

        // Dump GFateDirector if active — full struct (0x7B0) plus extra (0x100 beyond)
        var director = mgr->CurrentGFateDirector;
        if (director != null)
        {
            var dirPtr = (byte*)director;
            var dirSize = 0x8B0; // 0x7B0 struct + 0x100 overflow scan
            var dirPath = Path.Combine(dir, $"gfd_{timestamp}.hex");
            DumpMemoryToFile(dirPtr, dirSize, dirPath, "GFateDirector");
            ChatGui.Print($"{Tag} Scan saved: {mgrPath} + {dirPath}");
        }
        else
        {
            ChatGui.Print($"{Tag} Scan saved: {mgrPath} (no Director active)");
        }

        var scanContext = $"{Tag} Scan context [{DateTime.UtcNow:O}]: current={CurrentGateName ?? "none"}, " +
                         $"lastDetected={LastDetectedGateName ?? "none"}, " +
                         $"npcPredicted={npcPredictedNextGate ?? "none"}, " +
                         $"director={(director != null ? "active" : "null")}";
        ChatGui.Print(scanContext);
        Log.Information(scanContext);
        Log.Information($"{Tag} Memory scan saved @ {timestamp}");
    }

    private unsafe void DumpMemoryToFile(byte* ptr, int size, string path, string label)
    {
        try
        {
            using var writer = new StreamWriter(path);
            writer.WriteLine($"# {label} @ {DateTime.UtcNow:O}");
            writer.WriteLine($"# Base address: 0x{(nint)ptr:X}");
            writer.WriteLine($"# Size: 0x{size:X} ({size} bytes)");
            writer.WriteLine();

            for (var offset = 0; offset < size; offset += 16)
            {
                var hex = new System.Text.StringBuilder();
                var ascii = new System.Text.StringBuilder();
                hex.Append($"{offset:X4}: ");

                for (var i = 0; i < 16 && offset + i < size; i++)
                {
                    var b = ptr[offset + i];
                    hex.Append($"{b:X2} ");
                    ascii.Append(b >= 0x20 && b < 0x7F ? (char)b : '.');
                }

                writer.WriteLine($"{hex,-56} {ascii}");
            }
        }
        catch (Exception ex)
        {
            Log.Error($"{Tag} Failed to dump {label}: {ex.Message}");
        }
    }

    /// <summary>
    /// Continuous logging: log Director state every 10s when any field changes.
    /// Critical for capturing the full sequence after server maintenance.
    /// </summary>
    private unsafe void ContinuousDirectorLog()
    {
        var now = DateTime.UtcNow;
        if ((now - lastContinuousLogTime).TotalSeconds < 10)
            return;
        lastContinuousLogTime = now;

        var mgr = GoldSaucerManager.Instance();
        if (mgr == null)
            return;

        var director = mgr->CurrentGFateDirector;
        if (director == null)
            return;

        var gateType = (byte)director->GateType;
        var endTs = director->EndTimestamp;
        var posType = (byte)director->GatePositionType;
        var flags = (uint)director->Flags;

        // Only log when something changed
        if (gateType == lastDirectorGateType && endTs == lastLoggedEndTimestamp)
            return;

        lastLoggedEndTimestamp = endTs;

        var mapped = MapDirectorGateType(gateType);
        var gateName = mapped != null ? GateDefinitions.DisplayNames[mapped.Value] : $"Unknown({gateType})";

        Log.Information($"{Tag} CLog: {gateName} endTs={endTs} pos={posType} flags=0x{flags:X8}");
        LogDirectorDump(now, gateType, gateName, posType, endTs, flags);
    }

    private void LogDirectorDump(DateTime utcNow, byte gateType, string gateName, byte posType, int endTs, uint flags)
    {
        try
        {
            var dir = PluginInterface.GetPluginConfigDirectory();
            var path = Path.Combine(dir, "director_dumps.csv");
            var exists = File.Exists(path);
            using var writer = new StreamWriter(path, append: true);
            if (!exists)
                writer.WriteLine("utc_time,gate_type_byte,gate_name,position_type,end_timestamp,flags");
            writer.WriteLine($"{utcNow:O},{gateType},{gateName},{posType},{endTs},0x{flags:X8}");
        }
        catch (Exception ex)
        {
            Log.Error($"{Tag} Failed to log director dump: {ex.Message}");
        }
    }

    /// <summary>
    /// Compare the announced GATE against what the NPC predicted.
    /// A mismatch indicates a server re-seed (e.g., maintenance boundary).
    /// </summary>
    private void CheckNpcPredictionMismatch(string announcedGate)
    {
        if (npcPredictedNextGate == null)
            return;

        // Only consider predictions made within the current cycle (20min + small buffer)
        var age = (DateTime.UtcNow - npcPredictionTime).TotalMinutes;
        if (age > 21)
        {
            npcPredictedNextGate = null;
            return;
        }

        if (string.Equals(npcPredictedNextGate, announcedGate, StringComparison.OrdinalIgnoreCase))
        {
            Log.Information($"{Tag} NPC prediction MATCHED: {announcedGate}");
        }
        else
        {
            var msg = $"{Tag} SEQUENCE BREAK: NPC predicted '{npcPredictedNextGate}' " +
                      $"but server announced '{announcedGate}' " +
                      $"(prediction was {age:F1}min ago)";
            Log.Warning(msg);
            ChatGui.Print(msg);

            // Log to file
            LogSequenceBreak(npcPredictedNextGate, announcedGate, npcPredictionTime, DateTime.UtcNow);

            // Report to API
            var world = GetCurrentWorldName();
            if (world != null)
                ApiService.ReportEvent(world, "sequence_break",
                    $"predicted={npcPredictedNextGate}|actual={announcedGate}|" +
                    $"prediction_utc={npcPredictionTime:O}|slot={GetCurrentSlot()}");
        }

        npcPredictedNextGate = null;
    }

    private void LogSequenceBreak(string predicted, string actual, DateTime predictionUtc, DateTime announceUtc)
    {
        try
        {
            var dir = PluginInterface.GetPluginConfigDirectory();
            var path = Path.Combine(dir, "sequence_breaks.csv");
            var exists = File.Exists(path);
            using var writer = new StreamWriter(path, append: true);
            if (!exists)
                writer.WriteLine("prediction_utc,announce_utc,predicted_gate,actual_gate,slot");
            writer.WriteLine($"{predictionUtc:O},{announceUtc:O},{predicted},{actual},{GetCurrentSlot()}");
        }
        catch (Exception ex)
        {
            Log.Error($"{Tag} Failed to log sequence break: {ex.Message}");
        }
    }

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

        // Log every Director change for algorithm analysis
        LogDirectorDump(now, gateTypeByte, gateName, posType, endTs, flags);

        // Always send director data to API (structured fields for data collection)
        // even if chat_underway already detected this GATE
        // Director fires DURING the active GATE, so use current slot, not next
        var currentSlot = GateScheduler.GetCurrentGateTime(now);
        var world = GetCurrentWorldName();
        if (world != null)
            ApiService.ReportGate(world, gateName, currentSlot.Minute, "memory_director",
                $"byte={gateTypeByte},pos={posType},endTs={endTs},flags=0x{flags:X8}",
                gateTypeByte: gateTypeByte, positionType: posType, flags: (int)flags);

        // Skip state changes/alerts if this is the same GATE we already detected OR the currently active GATE
        // (director briefly goes null during zone transitions, causing spurious re-detection)
        if (LastDetectedGateName == gateName || CurrentGateName == gateName)
            return;

        LastDetectedGateName = gateName;
        LastDetectedGateType = mapped.Value;
        Configuration.Save();

        if (Configuration.EnabledGates.TryGetValue(mapped.Value, out var enabled) && enabled)
        {
            SendGateDetectedAlert(mapped.Value);
        }
    }

    private unsafe void ForceDirectorPost()
    {
        var mgr = GoldSaucerManager.Instance();
        if (mgr == null)
        {
            ChatGui.Print($"{Tag} GoldSaucerManager is null");
            return;
        }

        var director = mgr->CurrentGFateDirector;
        if (director == null)
        {
            ChatGui.Print($"{Tag} Director is null");
            return;
        }

        var gateTypeByte = (byte)director->GateType;
        var posType = (byte)director->GatePositionType;
        var endTs = director->EndTimestamp;
        var flags = (uint)director->Flags;
        var mapped = MapDirectorGateType(gateTypeByte);
        var gateName = mapped != null ? GateDefinitions.DisplayNames[mapped.Value] : $"unknown({gateTypeByte})";

        var now = DateTime.UtcNow;
        var currentSlot = GateScheduler.GetCurrentGateTime(now);
        var world = GetCurrentWorldName();

        ChatGui.Print($"{Tag} Force POST: {gateName} byte={gateTypeByte} pos={posType} endTs={endTs} flags=0x{flags:X8} slot={currentSlot.Minute} world={world ?? "null"}");
        Log.Information($"{Tag} Force POST [{now:O}]: {gateName} byte={gateTypeByte} pos={posType} endTs={endTs} flags=0x{flags:X8} slot={currentSlot.Minute}");

        if (world != null)
            ApiService.ReportGate(world, gateName, currentSlot.Minute, "memory_director",
                $"byte={gateTypeByte},pos={posType},endTs={endTs},flags=0x{flags:X8}",
                gateTypeByte: gateTypeByte, positionType: posType, flags: (int)flags);
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

    private static string ClassifyGoldSaucerEvent(string text)
    {
        if (text.Contains("chocobo registrar", StringComparison.OrdinalIgnoreCase))
            return "chocobo_racing";
        if (text.Contains("Jumbo Cactpot", StringComparison.OrdinalIgnoreCase))
            return "jumbo_cactpot";
        if (text.Contains("Mini Cactpot", StringComparison.OrdinalIgnoreCase))
        {
            if (text.Contains("close momentarily", StringComparison.OrdinalIgnoreCase))
                return "mini_cactpot_closing";
            if (text.Contains("now being accepted", StringComparison.OrdinalIgnoreCase))
                return "mini_cactpot_new_drawing";
            return "mini_cactpot_sales";
        }
        if (text.Contains("Triple Triad", StringComparison.OrdinalIgnoreCase))
        {
            if (text.Contains("currently underway", StringComparison.OrdinalIgnoreCase))
                return "triple_triad_underway";
            return "triple_triad_open";
        }
        if (text.Contains("Lord of Verminion", StringComparison.OrdinalIgnoreCase))
            return "lord_of_verminion";
        if (text.Contains("trigger finger", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("bonus phase", StringComparison.OrdinalIgnoreCase))
            return "gate_in_progress";
        return "unknown";
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
        return (DateTime.UtcNow.Minute / 20) * 20;
    }

    public void OpenConfigUi() => ConfigWindow.IsOpen = true;
    public void OpenMainUi()
    {
        Configuration.ShowOverlay = true;
        Configuration.Save();
    }

    public void ToggleConfigUi() => ConfigWindow.Toggle();
}
