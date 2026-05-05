using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.Command;
using Dalamud.Game.Chat;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using FFXIVClientStructs.FFXIV.Client.Game.GoldSaucer;
using FFXIVClientStructs.FFXIV.Client.Network;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Dalamud.Hooking;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using Lumina.Excel;

namespace GateAnalyzer;

public sealed class AnalyzerPlugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IAddonLifecycle AddonLifecycle { get; private set; } = null!;
    [PluginService] internal static IPlayerState PlayerState { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static ISigScanner SigScanner { get; private set; } = null!;
    [PluginService] internal static IGameInteropProvider GameInteropProvider { get; private set; } = null!;
    [PluginService] internal static IObjectTable ObjectTable { get; private set; } = null!;

    private const string CommandName = "/ga";
    private const string Tag = "[GateAnalyzer]";

    public AnalyzerConfiguration Configuration { get; init; }
    public AnalyzerApiService ApiService { get; init; }

    // Director polling state
    private byte lastDirectorGateType;
    private int lastDirectorSlot = -1;
    private DateTime lastDirectorChangeTime = DateTime.MinValue;

    // Prediction scan state
    private bool predictionScanDone;
    private bool predictionScan2Done;
    private TimeSpan previousTimeRemaining = GateScheduler.GetTimeUntilNextGate(DateTime.UtcNow);

    // Continuous logging
    private bool continuousLogging;
    private DateTime lastContinuousLogTime = DateTime.MinValue;
    private int lastLoggedEndTimestamp;

    // NPC prediction tracking
    private string? npcPredictedNextGate;
    private DateTime npcPredictionTime = DateTime.MinValue;
    private bool inGateKeeperDialogue;

    // Current GATE tracking (for course tagging and context)
    private string? currentGateName;
    private byte currentGateTypeByte;

    // Hook state
    private unsafe delegate nint GateDataReceiverDelegate(nint handlerContext, nint packetData);
    private unsafe delegate nint GatePoolWriterDelegate(nint handlerContext, nint packetData);
    private unsafe delegate nint GatePoolReaderDelegate(nint handlerContext);
    private Hook<GateDataReceiverDelegate>? gateDataReceiverHook;
    private Hook<GatePoolWriterDelegate>? gatePoolWriterHook;
    private Hook<GatePoolReaderDelegate>? gatePoolReaderHook;
    private nint capturedHandlerContext;
    private DateTime lastHandlerContextCapture = DateTime.MinValue;
    private string? capturedBy;

    // Packet capture state
    private unsafe delegate void OnReceivePacketDelegate(NetworkModulePacketReceiverCallback* self, uint opcode, nint data);
    private Hook<OnReceivePacketDelegate>? packetReceiveHook;
    private bool packetCaptureEnabled;
    private int packetsCaptured;
    private string? packetLogPath;

    public AnalyzerPlugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as AnalyzerConfiguration ?? new AnalyzerConfiguration();
        ApiService = new AnalyzerApiService(Configuration, Log);

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "GATE research commands. /ga help for details.",
        });

        PluginInterface.UiBuilder.Draw += () => { };
        PluginInterface.UiBuilder.OpenConfigUi += () => { };
        PluginInterface.UiBuilder.OpenMainUi += () => { };

        Framework.Update += OnFrameworkUpdate;
        ChatGui.ChatMessage += OnChatMessage;

        AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "Talk", OnTalkPostSetup);
        AddonLifecycle.RegisterListener(AddonEvent.PostRefresh, "Talk", OnTalkPostRefresh);

        // Install hooks for handler context capture
        try
        {
            var moduleBase = SigScanner.Module.BaseAddress;
            Log.Information($"{Tag} Module base: 0x{moduleBase:X}");

            InstallHook("DataReceiver",
                "8B 81 DC 1F 00 00 45 33 C0 83 F8 06",
                (nint ctx, nint pkt) => { CaptureContext(ctx, "DataReceiver"); return gateDataReceiverHook!.Original(ctx, pkt); },
                out gateDataReceiverHook);

            InstallHook("PoolWriter",
                "40 53 48 83 EC 40 0F B7 42 12 48 8B DA 44 0F B7",
                (nint ctx, nint pkt) => { CaptureContext(ctx, "PoolWriter"); return gatePoolWriterHook!.Original(ctx, pkt); },
                out gatePoolWriterHook);

            InstallHook("PoolReader",
                "41 57 48 83 EC 50 4C 8B F9 48 8B 0D",
                (nint ctx) => { CaptureContext(ctx, "PoolReader"); return gatePoolReaderHook!.Original(ctx); },
                out gatePoolReaderHook);

            // Read the global pointer used by PoolReader
            if (gatePoolReaderHook != null)
            {
                try
                {
                    unsafe
                    {
                        var readerAddr = (byte*)gatePoolReaderHook.Address;
                        var rel32 = *(int*)(readerAddr + 9 + 3);
                        var instrEnd = (nint)(readerAddr + 9 + 7);
                        var globalAddr = instrEnd + rel32;
                        var globalRva = (long)globalAddr - (long)moduleBase;
                        Log.Information($"{Tag} PoolReader global @ RVA 0x{globalRva:X} (0x{globalAddr:X})");

                        var globalPtr = *(nint*)globalAddr;
                        Log.Information($"{Tag} PoolReader global -> 0x{globalPtr:X}");

                        if (globalPtr != 0)
                        {
                            try
                            {
                                var globalData = new byte[64];
                                System.Runtime.InteropServices.Marshal.Copy(globalPtr, globalData, 0, 64);
                                for (var row = 0; row < 64; row += 16)
                                {
                                    var hex = string.Join(" ", globalData.Skip(row).Take(16).Select(b => b.ToString("X2")));
                                    Log.Information($"{Tag}   global[+0x{row:X2}]: {hex}");
                                }
                            }
                            catch (Exception ex)
                            {
                                Log.Warning($"{Tag} Cannot read global target: {ex.Message}");
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Warning($"{Tag} Global pointer read failed: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error($"{Tag} Failed to install hooks: {ex.Message}");
        }

        // Packet receive hook is installed lazily via /ga net command
        // (requires in-game network module to be active)

        var version = PluginInterface.Manifest.AssemblyVersion;
        Log.Information($"{Tag} v{version} loaded.");
    }

    public void Dispose()
    {
        gateDataReceiverHook?.Dispose();
        gatePoolWriterHook?.Dispose();
        gatePoolReaderHook?.Dispose();
        packetReceiveHook?.Dispose();

        Framework.Update -= OnFrameworkUpdate;
        ChatGui.ChatMessage -= OnChatMessage;

        AddonLifecycle.UnregisterListener(OnTalkPostSetup);
        AddonLifecycle.UnregisterListener(OnTalkPostRefresh);

        CommandManager.RemoveHandler(CommandName);
        ApiService.Dispose();
    }

    private void OnCommand(string command, string args)
    {
        var trimmed = args.Trim();
        if (trimmed.Equals("dump", StringComparison.OrdinalIgnoreCase))
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
        else if (trimmed.StartsWith("post", StringComparison.OrdinalIgnoreCase))
        {
            var courseInput = trimmed.Length > 4 ? trimmed[4..].Trim() : null;
            var courseName = ResolveCourseTag(courseInput);
            ForceDirectorPost(courseName);
        }
        else if (trimmed.Equals("exd", StringComparison.OrdinalIgnoreCase))
        {
            DumpExdSheets();
        }
        else if (trimmed.Equals("sig", StringComparison.OrdinalIgnoreCase))
        {
            DumpFunctionSignatures();
        }
        else if (trimmed.StartsWith("net", StringComparison.OrdinalIgnoreCase))
        {
            var netArgs = trimmed.Length > 3 ? trimmed[3..].Trim() : "";
            HandleNetCommand(netArgs);
        }
        else if (trimmed.Equals("ctx", StringComparison.OrdinalIgnoreCase))
        {
            if (capturedHandlerContext != 0)
            {
                Log.Information($"{Tag} Re-dumping handler context 0x{capturedHandlerContext:X} (captured by {capturedBy} at {lastHandlerContextCapture:O})");
                DumpHandlerContext();
            }
            else
            {
                Log.Information($"{Tag} DataReceiver: {(gateDataReceiverHook != null ? (gateDataReceiverHook.IsEnabled ? "enabled" : "disabled") : "not installed")}");
                Log.Information($"{Tag} PoolWriter: {(gatePoolWriterHook != null ? (gatePoolWriterHook.IsEnabled ? "enabled" : "disabled") : "not installed")}");
                Log.Information($"{Tag} PoolReader: {(gatePoolReaderHook != null ? (gatePoolReaderHook.IsEnabled ? "enabled" : "disabled") : "not installed")}");
                Log.Information($"{Tag} No handler context captured yet.");
            }
        }
        else if (trimmed.Equals("event", StringComparison.OrdinalIgnoreCase))
        {
            DumpEventFrameworkState();
        }
        else if (trimmed.Equals("npc", StringComparison.OrdinalIgnoreCase))
        {
            DumpGateKeeperGameData();
        }
        else if (trimmed.Equals("help", StringComparison.OrdinalIgnoreCase))
        {
            ChatGui.Print($"{Tag} Commands:");
            ChatGui.Print($"  /ga dump    — Print Director state to chat");
            ChatGui.Print($"  /ga scan    — Dump memory to hex files");
            ChatGui.Print($"  /ga post    — Force send current GATE to API");
            ChatGui.Print($"  /ga post <course> — Send with course tag (name or index):");
            ChatGui.Print($"    AFO: 0=gold_saucer, 1=cieldalaes_cliff, 2=cieldalaes_cave, 3=cieldalaes_ship");
            ChatGui.Print($"    LoF: 0=belahdia, 1=nym, 2=sylphstep");
            ChatGui.Print($"  /ga exd     — Dump GATE game data sheets to log");
            ChatGui.Print($"  /ga sig     — Dump function signatures for handler hook");
            ChatGui.Print($"  /ga log     — Toggle continuous Director logging");
            ChatGui.Print($"  /ga ctx     — Dump captured handler context");
            ChatGui.Print($"  /ga net     — Toggle network packet capture to CSV");
            ChatGui.Print($"  /ga net vtable — Dump PacketDispatcher vtable for analysis");
            ChatGui.Print($"  /ga event   — Raw EventFramework memory dump to file");
            ChatGui.Print($"  /ga npc     — Dump GATE Keeper ENpcBase data, CustomTalk refs, Lua scripts");
        }
        else
        {
            ChatGui.Print($"{Tag} Unknown command. Use /ga help for a list.");
        }
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        // Poll Director for GATE changes (data collection)
        PollDirectorForGateChange();

        // Continuous logging mode
        if (continuousLogging)
            ContinuousDirectorLog();

        var now = DateTime.UtcNow;
        var timeRemaining = GateScheduler.GetTimeUntilNextGate(now);

        // Prediction-phase memory scans: fire at 5 min and 3 min before next GATE
        if (!predictionScanDone && timeRemaining.TotalMinutes <= 5)
        {
            predictionScanDone = true;
            var world = GetCurrentWorldName();
            if (world != null)
            {
                var slot = GateScheduler.GetCurrentSlot(now);
                Log.Information($"{Tag} Prediction scan 1: slot={slot}, {timeRemaining.TotalSeconds:F0}s before next GATE");
                CaptureAndUploadScan(world, null, 0, slot, "prediction");
            }
        }
        if (!predictionScan2Done && timeRemaining.TotalMinutes <= 3)
        {
            predictionScan2Done = true;
            var world = GetCurrentWorldName();
            if (world != null)
            {
                var slot = GateScheduler.GetCurrentSlot(now);
                Log.Information($"{Tag} Prediction scan 2: slot={slot}, {timeRemaining.TotalSeconds:F0}s before next GATE");
                CaptureAndUploadScan(world, null, 0, slot, "prediction2");
            }
        }

        // Slot boundary reset
        if (timeRemaining > previousTimeRemaining)
        {
            predictionScanDone = false;
            predictionScan2Done = false;
            npcPredictedNextGate = null;
            currentGateName = null;
            currentGateTypeByte = 0;
            Log.Information($"{Tag} Slot boundary: state reset");
        }

        previousTimeRemaining = timeRemaining;
    }

    private void OnChatMessage(IHandleableChatMessage msg)
    {
        if (((int)msg.LogKind & 0x7F) != 68)
            return;

        var text = msg.Message.TextValue;

        // Track "is now underway" — need currentGateName for /ga post course tagging
        if (text.Contains("is now underway", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var (gateType, substring) in GateDefinitions.ChatSubstrings)
            {
                if (text.Contains(substring, StringComparison.OrdinalIgnoreCase))
                {
                    var gateName = GateDefinitions.DisplayNames[gateType];
                    Log.Information($"{Tag} GATE now underway: {gateName}");
                    currentGateName = gateName;
                    break;
                }
            }
            return;
        }

        // Detect GATE announcement — check NPC prediction mismatch (research data)
        foreach (var (gateType, substring) in GateDefinitions.ChatSubstrings)
        {
            if (text.Contains(substring, StringComparison.OrdinalIgnoreCase))
            {
                var gateName = GateDefinitions.DisplayNames[gateType];
                Log.Information($"{Tag} GATE detected: {gateName}");
                CheckNpcPredictionMismatch(gateName);
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

    private unsafe void ReadTalkAddon(AtkUnitBase* addon)
    {
        if (addon == null)
            return;

        var talkAddon = (FFXIVClientStructs.FFXIV.Client.UI.AddonTalk*)addon;
        var speakerNode = talkAddon->AtkTextNode220;
        var textNode = talkAddon->AtkTextNode228;

        if (speakerNode == null || textNode == null)
            return;

        var speaker = speakerNode->NodeText.StringPtr.AsDalamudSeString().TextValue.Trim();
        var text = textNode->NodeText.StringPtr.AsDalamudSeString().TextValue.Trim();

        Log.Information($"{Tag} Talk: speaker=\"{speaker}\", text=\"{text}\"");

        if (speaker.Contains("GATE Keeper", StringComparison.OrdinalIgnoreCase))
        {
            inGateKeeperDialogue = true;

            // AUTO-CAPTURE disabled — crashed twice, need to verify layout first
            // CaptureGateKeeperHandler();

            if (!text.Contains("next scheduled event", StringComparison.OrdinalIgnoreCase))
                return;

            foreach (var (gateType, substring) in GateDefinitions.ChatSubstrings)
            {
                if (text.Contains(substring, StringComparison.OrdinalIgnoreCase))
                {
                    var gateName = GateDefinitions.DisplayNames[gateType];
                    Log.Information($"{Tag} GATE Keeper revealed next GATE: {gateName}");

                    npcPredictedNextGate = gateName;
                    npcPredictionTime = DateTime.UtcNow;
                    break;
                }
            }
        }
        else if (inGateKeeperDialogue && string.IsNullOrEmpty(speaker))
        {
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

    private bool gateKeeperHandlerCaptured;

    /// <summary>
    /// Called automatically during GATE Keeper dialogue — enumerates ALL active event handlers
    /// from EventFramework.EventHandlerModule.EventHandlerMap (StdMap&lt;uint, Pointer&lt;EventHandler&gt;&gt;).
    /// The GATE Keeper's handler is NOT stored on the GameObject — it lives in this map.
    /// For LuaEventHandler-derived handlers, dumps LuaClass/LuaKey to identify the prediction script.
    /// </summary>
    private unsafe void CaptureGateKeeperHandler()
    {
        if (gateKeeperHandlerCaptured) return;

        try
        {
            EnumerateEventHandlerMap(fullDump: true);
            gateKeeperHandlerCaptured = true;
        }
        catch (Exception ex)
        {
            Log.Error($"{Tag} CaptureGateKeeperHandler crashed: {ex.Message}\n{ex.StackTrace}");
            ChatGui.Print($"{Tag} Handler capture failed safely: {ex.Message}");
        }
    }

    /// <summary>
    /// Enumerates EventFramework.EventHandlerModule.EventHandlerMap.
    /// fullDump=true logs 1024 bytes per handler + ASCII scan + Lua field probing.
    /// fullDump=false logs just key/vtable/Lua strings (lightweight).
    /// </summary>
    private unsafe void EnumerateEventHandlerMap(bool fullDump)
    {
        var moduleBase = SigScanner.Module.BaseAddress;
        var modSize = SigScanner.Module.ModuleMemorySize;

        Log.Information($"{Tag} === Enumerating EventHandlerMap (fullDump={fullDump}) ===");

        var ef = EventFramework.Instance();
        if (ef == null)
        {
            ChatGui.Print($"{Tag} EventFramework is null");
            return;
        }

        // EventHandlerModule at EF+0x00, EventHandlerMap (StdMap) at EHM+0x40
        var ehmAddr = (byte*)ef;
        var mapHead = *(nint*)(ehmAddr + 0x40);
        var mapSize = *(long*)(ehmAddr + 0x48);

        Log.Information($"{Tag} EventHandlerMap: sentinel=0x{mapHead:X}, count={mapSize}");
        ChatGui.Print($"{Tag} EventHandlerMap: {mapSize} active handlers");

        if (mapSize <= 0 || mapSize > 10000 || mapHead == 0)
        {
            Log.Information($"{Tag} EventHandlerMap empty or invalid (count={mapSize})");
            return;
        }

        // Root = sentinel->_Parent (node offset +0x08)
        var root = *(nint*)(mapHead + 0x08);
        if (root == 0 || root == mapHead)
        {
            Log.Information($"{Tag} Map root is sentinel — empty tree");
            return;
        }

        // Walk the red-black tree
        var handlers = new List<(uint key, nint handlerPtr)>();
        try
        {
            WalkStdMapTree(root, mapHead, handlers, 0);
        }
        catch (Exception ex)
        {
            Log.Error($"{Tag} Tree walk crashed after {handlers.Count} nodes: {ex.Message}");
            ChatGui.Print($"{Tag} Tree walk crashed after {handlers.Count} nodes. Check log.");
            // Continue with whatever we collected
        }

        Log.Information($"{Tag} Enumerated {handlers.Count} handlers");

        // Collect GATE Keeper BaseIds for cross-reference
        var gateKeeperIds = new HashSet<uint>();
        try
        {
            foreach (var obj in ObjectTable)
            {
                if (obj == null) continue;
                var name = obj.Name.TextValue;
                if (name.Contains("GATE Keeper", StringComparison.OrdinalIgnoreCase))
                {
                    var goPtr = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)(nint)obj.Address;
                    if (goPtr != null)
                        gateKeeperIds.Add(goPtr->BaseId);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning($"{Tag} ObjectTable scan failed: {ex.Message}");
        }
        if (gateKeeperIds.Count > 0)
            Log.Information($"{Tag} GATE Keeper BaseIds in scene: {string.Join(", ", gateKeeperIds)}");

        foreach (var (key, handlerPtr) in handlers)
        {
            if (handlerPtr == 0) continue;

            try
            {
                DumpSingleHandler(key, handlerPtr, moduleBase, modSize, gateKeeperIds, fullDump);
            }
            catch (Exception ex)
            {
                Log.Warning($"{Tag} Handler 0x{key:X8} dump failed: {ex.Message}");
            }
        }

        ChatGui.Print($"{Tag} Enumerated {handlers.Count} handlers. Check Dalamud log.");
    }

    /// <summary>
    /// Dump a single handler from the EventHandlerMap. All memory reads are try/catch guarded.
    /// </summary>
    private unsafe void DumpSingleHandler(uint key, nint handlerPtr, nint moduleBase, int modSize, HashSet<uint> gateKeeperIds, bool fullDump)
    {
        nint vtable = 0;
        long vtableRva = 0;
        try
        {
            vtable = *(nint*)handlerPtr;
            vtableRva = vtable - moduleBase;
        }
        catch
        {
            Log.Warning($"{Tag} Handler 0x{key:X8}: cannot read vtable at 0x{handlerPtr:X}");
            return;
        }

        var isModule = vtableRva > 0 && vtableRva < modSize;
        var isGateKeeper = gateKeeperIds.Contains(key);
        var marker = isGateKeeper ? " *** GATE KEEPER ***" : "";

        Log.Information($"{Tag} Handler key=0x{key:X8} ({key}): ptr=0x{handlerPtr:X} vtableRVA=0x{vtableRva:X}{marker}");

        // Dump vtable entries (guarded)
        if (isModule && fullDump)
        {
            try
            {
                for (var i = 0; i < 20; i++)
                {
                    var funcAddr = *(nint*)(vtable + i * 8);
                    var fRva = funcAddr - moduleBase;
                    if (fRva <= 0 || fRva >= modSize) break;
                    Log.Information($"{Tag}   vtable[{i,2}] RVA 0x{fRva:X}");
                }
            }
            catch (Exception ex)
            {
                Log.Warning($"{Tag}   Vtable read failed: {ex.Message}");
            }
        }

        // Try reading handler memory (start small, expand if successful)
        byte[]? handlerBytes = null;
        var dumpSize = fullDump ? 1024 : 256;
        try
        {
            // First try a safe small read
            handlerBytes = new byte[64];
            System.Runtime.InteropServices.Marshal.Copy(handlerPtr, handlerBytes, 0, 64);

            // If that worked, try the full dump
            handlerBytes = new byte[dumpSize];
            System.Runtime.InteropServices.Marshal.Copy(handlerPtr, handlerBytes, 0, dumpSize);
        }
        catch
        {
            Log.Warning($"{Tag}   Cannot read handler memory (tried {dumpSize} bytes)");
            handlerBytes = null;
        }

        if (handlerBytes != null && fullDump)
        {
            for (var row = 0; row < handlerBytes.Length; row += 16)
            {
                var hex = string.Join(" ", handlerBytes.Skip(row).Take(16).Select(b => b.ToString("X2")));
                Log.Information($"{Tag}   +0x{row:X3}: {hex}");
            }

            ScanForAsciiStrings(handlerPtr, handlerBytes, handlerBytes.Length);
        }

        // Probe Lua fields at known offsets (all guarded by TryReadUtf8String's try/catch)
        foreach (var (fieldName, offset) in new[] {
            ("LuaClass_A", 0x1E8), ("LuaKey_A", 0x250),
            ("LuaClass_B", 0x248), ("LuaKey_B", 0x2B0) })
        {
            TryReadUtf8String(handlerPtr, offset, fieldName);
        }

        // Probe LuaState/LuaThread pointers (guarded)
        foreach (var (label, offset) in new[] {
            ("LuaState_A", 0x1B8), ("LuaState_B", 0x218),
            ("LuaThread_A", 0x2C8), ("LuaThread_B", 0x328) })
        {
            try
            {
                if (offset < dumpSize)
                {
                    var ptr = *(nint*)(handlerPtr + offset);
                    if (ptr != 0 && ptr > 0x10000)
                        Log.Information($"{Tag}   {label}@+0x{offset:X}: 0x{ptr:X}");
                }
            }
            catch { }
        }

        if (isGateKeeper)
            ChatGui.Print($"{Tag} *** Handler 0x{key:X8} matches GATE Keeper! ***");
    }

    /// <summary>
    /// Walk MSVC std::map red-black tree in-order.
    /// Node layout: +0x00=_Left, +0x08=_Parent, +0x10=_Right, +0x18=_Color, +0x19=_Isnil,
    /// +0x20=key (uint), +0x28=value (EventHandler*).
    /// </summary>
    private unsafe void WalkStdMapTree(nint node, nint sentinel, List<(uint, nint)> results, int depth)
    {
        if (node == 0 || node == sentinel || depth > 30) return;

        try
        {
            var isNil = *(byte*)(node + 0x19);
            if (isNil != 0) return;

            var left = *(nint*)node;
            var right = *(nint*)(node + 0x10);

            WalkStdMapTree(left, sentinel, results, depth + 1);

            var key = *(uint*)(node + 0x20);
            var value = *(nint*)(node + 0x28);
            results.Add((key, value));

            WalkStdMapTree(right, sentinel, results, depth + 1);
        }
        catch (Exception ex)
        {
            Log.Warning($"{Tag} Tree node read failed at 0x{node:X} depth={depth}: {ex.Message}");
        }
    }

    /// <summary>
    /// Scan raw memory for readable ASCII sequences (length >= 4).
    /// Catches embedded Utf8String values, Lua script names, etc.
    /// </summary>
    private void ScanForAsciiStrings(nint baseAddr, byte[] data, int size)
    {
        var current = new List<byte>();
        var startOffset = 0;

        for (var i = 0; i < size; i++)
        {
            var b = data[i];
            if (b >= 0x20 && b < 0x7F)
            {
                if (current.Count == 0) startOffset = i;
                current.Add(b);
            }
            else
            {
                if (current.Count >= 4)
                {
                    var text = System.Text.Encoding.ASCII.GetString(current.ToArray());
                    Log.Information($"{Tag}   ASCII@+0x{startOffset:X3} ({current.Count}ch): \"{text}\"");
                }
                current.Clear();
            }
        }

        if (current.Count >= 4)
        {
            var text = System.Text.Encoding.ASCII.GetString(current.ToArray());
            Log.Information($"{Tag}   ASCII@+0x{startOffset:X3} ({current.Count}ch): \"{text}\"");
        }
    }

    /// <summary>
    /// Try reading a Utf8String at the given offset within a handler.
    /// Utf8String has multiple possible layouts — we try reading the pointer at offset+0x00
    /// and following it to find string data.
    /// </summary>
    private unsafe void TryReadUtf8String(nint handlerPtr, int offset, string fieldName)
    {
        try
        {
            // Utf8String in FFXIV: the actual string pointer is somewhere in the struct.
            // Common layout: StringPtr at +0x00 of the Utf8String, or the struct itself
            // contains inline buffer data. Try multiple strategies.

            // Strategy 1: Read as direct pointer to null-terminated string
            var ptr = *(nint*)(handlerPtr + offset);
            if (ptr == 0) return;

            // Check if ptr looks like a valid heap address (not a small number or code address)
            if (ptr < 0x10000) return;

            var buf = new byte[256];
            System.Runtime.InteropServices.Marshal.Copy(ptr, buf, 0, 256);

            // Find null terminator
            var len = Array.IndexOf(buf, (byte)0);
            if (len <= 0 || len > 200) return;

            // Check if it's readable ASCII/UTF8
            var text = System.Text.Encoding.UTF8.GetString(buf, 0, len);
            if (text.Length >= 2 && text.All(c => c >= 0x20 && c < 0x7F))
            {
                Log.Information($"{Tag}   {fieldName}@+0x{offset:X}: \"{text}\"");
                ChatGui.Print($"{Tag}   {fieldName}: \"{text}\"");
            }
        }
        catch { /* ignore read failures */ }
    }

    #region Hook Helpers

    private void InstallHook<T>(string name, string sig, T detour, out Hook<T>? hook) where T : Delegate
    {
        hook = null;
        try
        {
            var h = GameInteropProvider.HookFromSignature<T>(sig, detour);
            var rva = (long)h.Address - (long)SigScanner.Module.BaseAddress;
            h.Enable();
            hook = h;
            Log.Information($"{Tag} {name} hook at 0x{h.Address:X} (RVA 0x{rva:X})");
        }
        catch (Exception ex)
        {
            Log.Warning($"{Tag} {name} hook failed: {ex.Message}");
        }
    }

    private void CaptureContext(nint ctx, string source)
    {
        try
        {
            if (ctx == 0) return;
            var isNew = ctx != capturedHandlerContext;
            capturedHandlerContext = ctx;
            lastHandlerContextCapture = DateTime.UtcNow;
            capturedBy = source;
            if (isNew)
            {
                Log.Information($"{Tag} Handler context captured by {source}: 0x{ctx:X}");
                DumpHandlerContext();
            }
        }
        catch (Exception ex)
        {
            Log.Error($"{Tag} CaptureContext error: {ex.Message}");
        }
    }

    private unsafe void DumpHandlerContext()
    {
        if (capturedHandlerContext == 0) return;

        Log.Information($"{Tag} === HANDLER CONTEXT @ 0x{capturedHandlerContext:X} ===");

        var winBytes = new byte[128];
        System.Runtime.InteropServices.Marshal.Copy(capturedHandlerContext + 0x48, winBytes, 0, 128);
        for (var row = 0; row < 128; row += 16)
        {
            var hex = string.Join(" ", winBytes.Skip(row).Take(16).Select(b => b.ToString("X2")));
            Log.Information($"{Tag}   +0x{(0x48 + row):X3}: {hex}");
        }

        var stateBytes = new byte[64];
        System.Runtime.InteropServices.Marshal.Copy(capturedHandlerContext + 0x1FBC, stateBytes, 0, 64);
        for (var row = 0; row < 64; row += 16)
        {
            var hex = string.Join(" ", stateBytes.Skip(row).Take(16).Select(b => b.ToString("X2")));
            Log.Information($"{Tag}   +0x{(0x1FBC + row):X3}: {hex}");
        }

        var poolBytes = new byte[720];
        System.Runtime.InteropServices.Marshal.Copy(capturedHandlerContext + 0x202C, poolBytes, 0, 720);
        for (var entry = 0; entry < 60; entry++)
        {
            var entryBytes = poolBytes.Skip(entry * 12).Take(12).ToArray();
            if (entryBytes.Any(b => b != 0))
            {
                var hex = string.Join(" ", entryBytes.Select(b => b.ToString("X2")));
                Log.Information($"{Tag}   pool[{entry,2}] +0x{(0x202C + entry * 12):X4}: {hex}");
            }
        }

        var pre = new byte[4];
        System.Runtime.InteropServices.Marshal.Copy(capturedHandlerContext + 0x2028, pre, 0, 4);
        Log.Information($"{Tag}   +0x2028 (pre-pool): {string.Join(" ", pre.Select(b => b.ToString("X2")))}");

        var postPool = new byte[32];
        System.Runtime.InteropServices.Marshal.Copy(capturedHandlerContext + 0x22FC, postPool, 0, 32);
        Log.Information($"{Tag}   +0x22FC (post-pool): {string.Join(" ", postPool.Select(b => b.ToString("X2")))}");
    }

    #endregion

    #region Packet Capture

    private unsafe void HandleNetCommand(string args)
    {
        if (args.Equals("vtable", StringComparison.OrdinalIgnoreCase))
        {
            DumpPacketDispatcherVtable();
            return;
        }

        // Install hook lazily on first toggle
        if (packetReceiveHook == null)
        {
            if (!InstallPacketHook())
            {
                ChatGui.Print($"{Tag} Packet hook installation failed. Check log.");
                return;
            }
        }

        packetCaptureEnabled = !packetCaptureEnabled;
        if (packetCaptureEnabled)
        {
            packetsCaptured = 0;
            var dir = PluginInterface.GetPluginConfigDirectory();
            packetLogPath = Path.Combine(dir, $"packets_{DateTime.UtcNow:yyyyMMdd_HHmmss}.csv");
            using var writer = new StreamWriter(packetLogPath);
            writer.WriteLine("utc_time,ipc_opcode,ipc_opcode_hex,raw_param,raw_param_hex,data_hex_128");
        }
        ChatGui.Print($"{Tag} Packet capture: {(packetCaptureEnabled ? "ON → " + packetLogPath : "OFF — " + packetsCaptured + " packets captured")}");
    }

    private unsafe bool InstallPacketHook()
    {
        try
        {
            // Resolve OnReceivePacket from live vtable (survives patches as long as vtable index is stable).
            // NetworkModulePacketReceiverCallback vtable[8] = OnReceivePacket(uint opcode, nint data)
            const int vtableIndex = 8;

            var fw = FFXIVClientStructs.FFXIV.Client.System.Framework.Framework.Instance();
            if (fw == null)
            {
                Log.Error($"{Tag} Packet hook: Framework instance is null");
                ChatGui.Print($"{Tag} Hook failed: Framework not ready");
                return false;
            }

            var proxy = fw->NetworkModuleProxy;
            if (proxy == null)
            {
                Log.Error($"{Tag} Packet hook: NetworkModuleProxy is null");
                ChatGui.Print($"{Tag} Hook failed: NetworkModuleProxy not ready");
                return false;
            }

            var vtable = *(nint**)proxy;
            var addr = vtable[vtableIndex];

            if (addr == nint.Zero)
            {
                Log.Error($"{Tag} Packet hook: vtable[{vtableIndex}] is null");
                ChatGui.Print($"{Tag} Hook failed: vtable entry is null");
                return false;
            }

            // Sanity check: address should be within the game module
            var moduleBase = SigScanner.Module.BaseAddress;
            var moduleEnd = moduleBase + SigScanner.Module.ModuleMemorySize;
            if (addr < moduleBase || addr >= moduleEnd)
            {
                Log.Error($"{Tag} Packet hook: vtable[{vtableIndex}] = 0x{addr:X} is outside game module");
                ChatGui.Print($"{Tag} Hook failed: vtable points outside game module");
                return false;
            }

            packetReceiveHook = GameInteropProvider.HookFromAddress<OnReceivePacketDelegate>(addr,
                (NetworkModulePacketReceiverCallback* self, uint opcode, nint data) =>
                {
                    packetReceiveHook!.Original(self, opcode, data);
                    OnPacketReceived(opcode, data);
                });
            packetReceiveHook.Enable();
            var rva = addr - moduleBase;
            ChatGui.Print($"{Tag} Packet hook installed via vtable[{vtableIndex}] (RVA 0x{rva:X})");
            Log.Information($"{Tag} PacketReceive hook at 0x{addr:X} (RVA 0x{rva:X})");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error($"{Tag} Packet hook install failed: {ex.Message}\n{ex.StackTrace}");
            ChatGui.Print($"{Tag} Hook failed: {ex.Message}");
            return false;
        }
    }

    private unsafe void DumpPacketDispatcherVtable()
    {
        try
        {
            var fw = FFXIVClientStructs.FFXIV.Client.System.Framework.Framework.Instance();
            if (fw == null)
            {
                ChatGui.Print($"{Tag} Framework.Instance() is null");
                return;
            }

            var networkModule = fw->NetworkModuleProxy;
            if (networkModule == null)
            {
                ChatGui.Print($"{Tag} Framework->NetworkModuleProxy is null");
                return;
            }

            // Dump NetworkModuleProxy structure
            var proxyBytes = new byte[64];
            System.Runtime.InteropServices.Marshal.Copy((nint)networkModule, proxyBytes, 0, 64);
            for (var row = 0; row < 64; row += 16)
            {
                var hex = string.Join(" ", proxyBytes.Skip(row).Take(16).Select(b => b.ToString("X2")));
                Log.Information($"{Tag} NetworkModuleProxy+0x{row:X2}: {hex}");
            }

            var moduleBase = SigScanner.Module.BaseAddress;
            var modSize = SigScanner.Module.ModuleMemorySize;

            // Also dump raw bytes at pointer targets for analysis
            for (var off = 0x08; off <= 0x10; off += 8)
            {
                var ptr = *(nint*)((byte*)networkModule + off);
                if (ptr == 0) continue;
                try
                {
                    var rawBytes = new byte[64];
                    System.Runtime.InteropServices.Marshal.Copy(ptr, rawBytes, 0, 64);
                    for (var row = 0; row < 64; row += 16)
                    {
                        var hex = string.Join(" ", rawBytes.Skip(row).Take(16).Select(b => b.ToString("X2")));
                        Log.Information($"{Tag} *(proxy+0x{off:X2})+0x{row:X2}: {hex}");
                    }
                }
                catch (Exception ex)
                {
                    Log.Warning($"{Tag} Cannot read *(proxy+0x{off:X2}): {ex.Message}");
                }
            }

            // Follow each pointer and dump its vtable.
            for (var off = 0; off < 32; off += 8)
            {
                var ptr = *(nint*)((byte*)networkModule + off);
                if (ptr == 0) continue;

                // First check if this offset IS a vtable pointer (direct vtable, e.g. proxy's own vtable)
                var firstVal = *(nint*)ptr;
                var directRva = firstVal - moduleBase;
                if (directRva > 0 && directRva < modSize)
                {
                    Log.Information($"{Tag} proxy+0x{off:X2} = 0x{ptr:X} (direct vtable):");
                    DumpVtableEntries((nint**)((byte*)networkModule + off), moduleBase, modSize, $"proxy+0x{off:X2}");
                }

                // Also follow the pointer: if proxy+off contains a heap pointer,
                // read the object at that address and check ITS vtable
                try
                {
                    var targetVtablePtr = *(nint*)ptr;
                    // If the target itself starts with a module-range pointer, it has a vtable
                    var targetFirstEntry = *(nint*)targetVtablePtr;
                    var targetRva = targetFirstEntry - moduleBase;
                    if (targetRva > 0 && targetRva < modSize)
                    {
                        Log.Information($"{Tag} *proxy+0x{off:X2} = 0x{ptr:X} → object with vtable at 0x{targetVtablePtr:X}:");
                        DumpVtableEntries((nint**)ptr, moduleBase, modSize, $"*proxy+0x{off:X2}");

                        // Also check +0x08 within this object (multi-inheritance: PacketDispatcher vtable)
                        var innerPtr = *(nint*)(ptr + 0x08);
                        if (innerPtr != 0)
                        {
                            var innerFirst = *(nint*)innerPtr;
                            var innerRva = innerFirst - moduleBase;
                            if (innerRva > 0 && innerRva < modSize)
                            {
                                Log.Information($"{Tag} *proxy+0x{off:X2}+0x08 → second vtable at 0x{innerPtr:X}:");
                                DumpVtableEntries((nint**)(ptr + 0x08), moduleBase, modSize, $"*proxy+0x{off:X2}+0x08");
                            }
                        }
                    }
                }
                catch
                {
                    // Not readable — skip
                }
            }

            ChatGui.Print($"{Tag} Vtable dump written to log. Check Dalamud log for details.");
        }
        catch (Exception ex)
        {
            ChatGui.Print($"{Tag} Vtable dump failed: {ex.Message}");
            Log.Error($"{Tag} Vtable dump failed: {ex}");
        }
    }

    private unsafe void DumpVtableEntries(nint** objPtr, nint moduleBase, int modSize, string label)
    {
        try
        {
            var vtable = *objPtr;
            for (var i = 0; i < 30; i++)
            {
                var funcAddr = vtable[i];
                var fRva = funcAddr - moduleBase;
                if (fRva <= 0 || fRva >= modSize)
                {
                    Log.Information($"{Tag}   [{i,2}] end of vtable");
                    break;
                }

                var prologue = new byte[16];
                System.Runtime.InteropServices.Marshal.Copy(funcAddr, prologue, 0, 16);
                var prologueHex = string.Join(" ", prologue.Select(b => b.ToString("X2")));
                Log.Information($"{Tag}   [{i,2}] RVA 0x{fRva:X} prologue: {prologueHex}");
            }
            ChatGui.Print($"{Tag} {label}: vtable dumped to log");
        }
        catch (Exception ex)
        {
            Log.Warning($"{Tag} {label} vtable read failed: {ex.Message}");
        }
    }

    private unsafe void OnPacketReceived(uint rawParam, nint data)
    {
        if (!packetCaptureEnabled || packetLogPath == null)
            return;

        try
        {
            packetsCaptured++;

            // Read first 128 bytes of packet data
            var readSize = 128;
            var bytes = new byte[readSize];
            System.Runtime.InteropServices.Marshal.Copy(data, bytes, 0, readSize);

            // Real IPC opcode is at data[2:4] (little-endian uint16)
            var ipcOpcode = (ushort)(bytes[2] | (bytes[3] << 8));
            var hex = string.Join(" ", bytes.Select(b => b.ToString("X2")));

            var now = DateTime.UtcNow;

            // Append to CSV
            using var writer = new StreamWriter(packetLogPath, append: true);
            writer.WriteLine($"{now:O},{ipcOpcode},0x{ipcOpcode:X4},{rawParam},0x{rawParam:X8},{hex}");
        }
        catch (Exception ex)
        {
            Log.Error($"{Tag} Packet capture error: {ex.Message}");
            packetCaptureEnabled = false;
        }
    }

    #endregion

    #region Director Polling & Scanning

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

        var slotMinute = (DateTime.UtcNow.Minute / 20) * 20;
        if (slotMinute != lastDirectorSlot)
        {
            lastDirectorGateType = 0;
            lastDirectorSlot = slotMinute;
        }

        if (gateTypeByte == lastDirectorGateType || gateTypeByte == 0)
        {
            lastDirectorGateType = gateTypeByte;
            return;
        }

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
        Log.Information($"{Tag} Director: GATE change [{now:O}] -> {gateName} (byte={gateTypeByte}, pos={posType}, endTs={endTs}, flags=0x{flags:X8})");

        currentGateName = gateName;
        currentGateTypeByte = gateTypeByte;

        LogDirectorDump(now, gateTypeByte, gateName, posType, endTs, flags);

        // Upload memory scan (research data) — gate reporting left to GateNotifier
        var currentSlot = GateScheduler.GetCurrentGateTime(now);
        var world = GetCurrentWorldName();
        if (world != null)
        {
            CaptureAndUploadScan(world, gateName, gateTypeByte, currentSlot.Minute);
        }
    }

    private unsafe void CaptureAndUploadScan(string world, string? gateName, byte gateTypeByte, int slot, string phase = "active", string? course = null)
    {
        try
        {
            var mgr = GoldSaucerManager.Instance();
            if (mgr == null) return;

            var mgrPtr = (byte*)mgr;
            var gsmBytes = new byte[0x840];
            new ReadOnlySpan<byte>(mgrPtr, 0x840).CopyTo(gsmBytes);

            byte[]? gfdBytes = null;
            var director = mgr->CurrentGFateDirector;
            if (director != null)
            {
                var dirPtr = (byte*)director;
                gfdBytes = new byte[0x8B0];
                new ReadOnlySpan<byte>(dirPtr, 0x8B0).CopyTo(gfdBytes);
            }

            var reportedType = (int)gateTypeByte;
            if (reportedType == 0 && gfdBytes != null && gfdBytes.Length > 0x79E)
                reportedType = gfdBytes[0x79E];

            ApiService.ReportScan(world, slot, gateName, reportedType, gsmBytes, gfdBytes, phase, course);
        }
        catch (Exception ex)
        {
            Log.Warning($"{Tag} Memory scan capture failed: {ex.Message}");
        }
    }

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

        var mgrPtr = (byte*)mgr;
        var mgrPath = Path.Combine(dir, $"gsm_{timestamp}.hex");
        DumpMemoryToFile(mgrPtr, 0x840, mgrPath, "GoldSaucerManager");

        var director = mgr->CurrentGFateDirector;
        if (director != null)
        {
            var dirPtr = (byte*)director;
            var dirPath = Path.Combine(dir, $"gfd_{timestamp}.hex");
            DumpMemoryToFile(dirPtr, 0x8B0, dirPath, "GFateDirector");
            ChatGui.Print($"{Tag} Scan saved: {mgrPath} + {dirPath}");
        }
        else
        {
            ChatGui.Print($"{Tag} Scan saved: {mgrPath} (no Director active)");
        }

        var scanContext = $"{Tag} Scan context [{DateTime.UtcNow:O}]: current={currentGateName ?? "none"}, " +
                         $"npcPredicted={npcPredictedNextGate ?? "none"}, " +
                         $"director={(director != null ? "active" : "null")}";
        ChatGui.Print(scanContext);
        Log.Information(scanContext);
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

    #endregion

    #region Director State & Logging

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

        var dirPtr = (byte*)director;
        var regions = new (int offset, int size, string label)[]
        {
            (0x788, 32, "EndTs/GateType/Flags"),
            (0x1FB0, 32, "State/GATE data"),
            (0x1FE0, 32, "GATE data cont"),
            (0x2020, 64, "Pool config start"),
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

        LogDirectorDump(now, gateType, gateName, posType, endTs, flags);
    }

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

    #endregion

    #region Signature & EXD Dumps

    private unsafe void DumpFunctionSignatures()
    {
        try
        {
            var textBase = SigScanner.TextSectionBase;
            var textSize = SigScanner.TextSectionSize;
            var moduleBase = SigScanner.Module.BaseAddress;

            ChatGui.Print($"{Tag} Module base: 0x{moduleBase:X}");
            ChatGui.Print($"{Tag} Text section: 0x{textBase:X} size=0x{textSize:X}");

            var functions = new (nint rva, string name)[]
            {
                (0x1972350, "PoolConfigSerializer"),
                (0x19716C0, "EntryWriter"),
            };

            foreach (var (rva, name) in functions)
            {
                var addr = moduleBase + rva;
                ChatGui.Print($"{Tag} {name} @ 0x{addr:X} (RVA 0x{rva:X}):");

                var bytes = new byte[64];
                System.Runtime.InteropServices.Marshal.Copy(addr, bytes, 0, 64);

                var hex = string.Join(" ", bytes.Select(b => b.ToString("X2")));
                Log.Information($"{Tag} {name} @ 0x{addr:X}: {hex}");

                var hexShort = string.Join(" ", bytes.Take(32).Select(b => b.ToString("X2")));
                ChatGui.Print($"  {hexShort}");
            }

            var preAddr = moduleBase + (nint)0x1972350 - 256;
            var preBytes = new byte[256];
            System.Runtime.InteropServices.Marshal.Copy(preAddr, preBytes, 0, 256);
            var preHex = string.Join(" ", preBytes.Select(b => b.ToString("X2")));
            Log.Information($"{Tag} Pre-serializer (-256 bytes) @ 0x{preAddr:X}: {preHex}");
            ChatGui.Print($"{Tag} Pre-serializer bytes logged (256 bytes before RVA 0x1972350)");

            ChatGui.Print($"{Tag} Checking GSM pointers for handler context...");
            var mgr = GoldSaucerManager.Instance();
            if (mgr != null)
            {
                var mgrPtr = (byte*)mgr;
                var deepOffsets = new[] { 0x01D0, 0x0260, 0x0530, 0x06E0 };
                var quickOffsets = new[] { 0x0028, 0x0080, 0x0088, 0x01A8, 0x0340, 0x04A0, 0x0650, 0x0800 };

                foreach (var off in deepOffsets)
                {
                    var ptr = *(nint*)(mgrPtr + off);
                    if (ptr == 0) continue;

                    try
                    {
                        var winBytes = new byte[96];
                        System.Runtime.InteropServices.Marshal.Copy(ptr + 0x48, winBytes, 0, 96);

                        var poolBytes = new byte[720];
                        System.Runtime.InteropServices.Marshal.Copy(ptr + 0x202C, poolBytes, 0, 720);

                        var stateBytes = new byte[32];
                        System.Runtime.InteropServices.Marshal.Copy(ptr + 0x1FBC, stateBytes, 0, 32);

                        Log.Information($"{Tag} === DEEP READ: GSM+0x{off:X3} -> 0x{ptr:X} ===");
                        for (var row = 0; row < 96; row += 16)
                        {
                            var hex2 = string.Join(" ", winBytes.Skip(row).Take(16).Select(b => b.ToString("X2")));
                            Log.Information($"{Tag}   win+0x{(0x48 + row):X3}: {hex2}");
                        }

                        for (var entry = 0; entry < 20 && entry * 12 < 720; entry++)
                        {
                            var hex2 = string.Join(" ", poolBytes.Skip(entry * 12).Take(12).Select(b => b.ToString("X2")));
                            Log.Information($"{Tag}   pool[{entry,2}] +0x{(0x202C + entry * 12):X4}: {hex2}");
                        }
                        var nonZeroEntries = 0;
                        for (var entry = 20; entry < 60; entry++)
                        {
                            if (poolBytes.Skip(entry * 12).Take(12).Any(b => b != 0)) nonZeroEntries++;
                        }
                        Log.Information($"{Tag}   pool[20-59]: {nonZeroEntries} non-zero entries");

                        var stateHex = string.Join(" ", stateBytes.Select(b => b.ToString("X2")));
                        Log.Information($"{Tag}   state+0x1FBC: {stateHex}");

                        ChatGui.Print($"{Tag} Deep read GSM+0x{off:X3} logged (win=96B, pool=720B, state=32B)");
                    }
                    catch (Exception ex)
                    {
                        Log.Warning($"{Tag} GSM+0x{off:X3} -> 0x{ptr:X}: deep read failed: {ex.Message}");
                    }
                }

                foreach (var off in quickOffsets)
                {
                    var ptr = *(nint*)(mgrPtr + off);
                    if (ptr == 0) continue;

                    try
                    {
                        var winBytes = new byte[16];
                        System.Runtime.InteropServices.Marshal.Copy(ptr + 0x48, winBytes, 0, 16);
                        var winHex = string.Join(" ", winBytes.Select(b => b.ToString("X2")));
                        Log.Information($"{Tag} GSM+0x{off:X3} -> 0x{ptr:X}: win={winHex}");
                    }
                    catch
                    {
                        Log.Debug($"{Tag} GSM+0x{off:X3} -> 0x{ptr:X}: unreadable");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            ChatGui.Print($"{Tag} Sig dump failed: {ex.Message}");
            Log.Error($"{Tag} Sig dump failed: {ex}");
        }
    }

    private void DumpExdSheets()
    {
        var sheets = new[] { "GFateRoulette", "GFATE", "GFateType" };
        foreach (var sheetName in sheets)
        {
            var sheet = DataManager.GameData.GetExcelSheet<RawRow>(name: sheetName);
            if (sheet == null)
            {
                Log.Warning($"{Tag} EXD sheet '{sheetName}' not found");
                ChatGui.Print($"{Tag} Sheet '{sheetName}' not found");
                continue;
            }

            Log.Information($"{Tag} === EXD: {sheetName} ===");
            ChatGui.Print($"{Tag} === {sheetName} ===");

            var firstRow = sheet.GetRowOrDefault(0);
            if (firstRow.HasValue)
            {
                var cols = firstRow.Value.Columns;
                for (var i = 0; i < cols.Count; i++)
                    Log.Information($"{Tag}   col[{i}] offset={cols[i].Offset} type={cols[i].Type}");
            }

            foreach (var row in sheet)
            {
                var values = new List<string>();
                for (var i = 0; i < row.Columns.Count; i++)
                {
                    try
                    {
                        var val = row.ReadColumn(i);
                        values.Add($"{val}");
                    }
                    catch
                    {
                        values.Add("?");
                    }
                }
                var line = $"  [{row.RowId}] {string.Join(" | ", values)}";
                Log.Information($"{Tag} {line}");
                ChatGui.Print($"{Tag}{line}");
            }
        }
        ChatGui.Print($"{Tag} EXD dump complete. Full data in Dalamud log.");
    }

    #endregion

    #region GATE Keeper Game Data Investigation

    /// <summary>
    /// Dump GATE Keeper NPC game data: ENpcBase rows, CustomTalk references, Lua script paths.
    /// This reads from game data sheets (safe, no memory poking).
    /// </summary>
    private void DumpGateKeeperGameData()
    {
        var gateKeeperIds = new uint[] { 1011093, 1011084, 1011080 };

        ChatGui.Print($"{Tag} === GATE Keeper Game Data Investigation ===");

        // 1. Dump ENpcBase rows for each GATE Keeper
        var enpcSheet = DataManager.GameData.GetExcelSheet<RawRow>(name: "ENpcBase");
        if (enpcSheet == null)
        {
            ChatGui.Print($"{Tag} ENpcBase sheet not found!");
            return;
        }

        // Get column metadata from first row
        var firstRow = enpcSheet.GetRowOrDefault(0);
        if (firstRow.HasValue)
        {
            Log.Information($"{Tag} ENpcBase has {firstRow.Value.Columns.Count} columns");
            ChatGui.Print($"{Tag} ENpcBase: {firstRow.Value.Columns.Count} columns");
        }

        // Track all non-zero column values across GATE Keepers for CustomTalk/event references
        var interestingColumns = new Dictionary<int, List<(uint npcId, object value)>>();

        foreach (var npcId in gateKeeperIds)
        {
            var row = enpcSheet.GetRowOrDefault(npcId);
            if (!row.HasValue)
            {
                ChatGui.Print($"{Tag} ENpcBase[{npcId}]: NOT FOUND");
                continue;
            }

            Log.Information($"{Tag} === ENpcBase[{npcId}] (GATE Keeper) ===");
            ChatGui.Print($"{Tag} ENpcBase[{npcId}]:");

            for (var i = 0; i < row.Value.Columns.Count; i++)
            {
                try
                {
                    var val = row.Value.ReadColumn(i);
                    if (val == null) continue;

                    var str = val.ToString() ?? "";

                    // Log non-zero/non-empty values
                    var isInteresting = false;
                    if (val is uint u && u != 0) isInteresting = true;
                    else if (val is int si && si != 0) isInteresting = true;
                    else if (val is ushort us && us != 0) isInteresting = true;
                    else if (val is short ss && ss != 0) isInteresting = true;
                    else if (val is byte b && b != 0) isInteresting = true;
                    else if (val is sbyte sb && sb != 0) isInteresting = true;
                    else if (val is bool bo && bo) isInteresting = true;
                    else if (val is string s && !string.IsNullOrEmpty(s)) isInteresting = true;
                    else if (val is float f && f != 0) isInteresting = true;

                    if (isInteresting)
                    {
                        var colType = row.Value.Columns[i].Type;
                        var colOffset = row.Value.Columns[i].Offset;
                        Log.Information($"{Tag}   col[{i}] offset=0x{colOffset:X} type={colType}: {str}");

                        if (!interestingColumns.ContainsKey(i))
                            interestingColumns[i] = new List<(uint, object)>();
                        interestingColumns[i].Add((npcId, val));
                    }
                }
                catch (Exception ex)
                {
                    Log.Warning($"{Tag}   col[{i}]: read error: {ex.Message}");
                }
            }
        }

        // Summarize columns that had non-zero values
        ChatGui.Print($"{Tag} Non-zero columns across all 3 GATE Keepers:");
        foreach (var (colIdx, values) in interestingColumns)
        {
            var valStr = string.Join(", ", values.Select(v => $"{v.npcId}={v.value}"));
            Log.Information($"{Tag} Summary col[{colIdx}]: {valStr}");
            ChatGui.Print($"{Tag}   col[{colIdx}]: {valStr}");
        }

        // 2. Look up potential CustomTalk references — common ENpcBase columns for event data
        // ENpcBase stores event handler IDs. Values that look like row IDs (>0, <1000000) in uint columns
        // are likely references to CustomTalk, DefaultTalk, or other event sheets.
        var candidateRefs = new List<uint>();
        foreach (var (colIdx, values) in interestingColumns)
        {
            foreach (var (npcId, val) in values)
            {
                if (val is uint u && u > 0 && u < 10000000)
                    candidateRefs.Add(u);
            }
        }
        candidateRefs = candidateRefs.Distinct().ToList();

        // 3. Try looking up each candidate in CustomTalk
        var customTalkSheet = DataManager.GameData.GetExcelSheet<RawRow>(name: "CustomTalk");
        if (customTalkSheet != null)
        {
            ChatGui.Print($"{Tag} Checking {candidateRefs.Count} candidate refs in CustomTalk...");
            foreach (var refId in candidateRefs)
            {
                var ctRow = customTalkSheet.GetRowOrDefault(refId);
                if (!ctRow.HasValue) continue;

                Log.Information($"{Tag} === CustomTalk[{refId}] ===");
                ChatGui.Print($"{Tag} CustomTalk[{refId}] FOUND!");

                for (var i = 0; i < ctRow.Value.Columns.Count; i++)
                {
                    try
                    {
                        var val = ctRow.Value.ReadColumn(i);
                        if (val == null) continue;
                        var str = val.ToString() ?? "";
                        if (string.IsNullOrEmpty(str) || str == "0" || str == "False") continue;

                        var colType = ctRow.Value.Columns[i].Type;
                        Log.Information($"{Tag}   col[{i}] type={colType}: {str}");
                    }
                    catch { }
                }
            }
        }

        // 4. Also check DefaultTalk, Behavior, and other event-related sheets
        foreach (var sheetName in new[] { "DefaultTalk", "CustomTalkNestHandlers", "ENpcDressUpDress",
            "Behavior", "ScreenText", "EventAction", "Quest" })
        {
            var sheet = DataManager.GameData.GetExcelSheet<RawRow>(name: sheetName);
            if (sheet == null) continue;

            foreach (var refId in candidateRefs)
            {
                var row = sheet.GetRowOrDefault(refId);
                if (!row.HasValue) continue;

                Log.Information($"{Tag} === {sheetName}[{refId}] ===");
                ChatGui.Print($"{Tag} {sheetName}[{refId}] FOUND!");

                for (var i = 0; i < row.Value.Columns.Count; i++)
                {
                    try
                    {
                        var val = row.Value.ReadColumn(i);
                        if (val == null) continue;
                        var str = val.ToString() ?? "";
                        if (string.IsNullOrEmpty(str) || str == "0" || str == "False") continue;
                        Log.Information($"{Tag}   col[{i}]: {str}");
                    }
                    catch { }
                }
            }
        }

        // 5. Collect script names found in CustomTalk string columns
        var scriptNames = new List<string>();
        if (customTalkSheet != null)
        {
            foreach (var refId in candidateRefs)
            {
                var ctRow = customTalkSheet.GetRowOrDefault(refId);
                if (!ctRow.HasValue) continue;
                for (var i = 0; i < ctRow.Value.Columns.Count; i++)
                {
                    try
                    {
                        var val = ctRow.Value.ReadColumn(i);
                        // ReadColumn may return SeString or string — use ToString()
                        var str = val?.ToString() ?? "";
                        if (!string.IsNullOrEmpty(str) && str.Length > 3 &&
                            (str.Contains("Cmn", StringComparison.OrdinalIgnoreCase) ||
                             str.Contains("Sub", StringComparison.OrdinalIgnoreCase)))
                        {
                            scriptNames.Add(str);
                            Log.Information($"{Tag} Script name in CustomTalk[{refId}] col[{i}]: {str}");
                        }
                    }
                    catch { }
                }
            }
        }
        Log.Information($"{Tag} Script names found: {string.Join(", ", scriptNames)}");

        // 6. Try to find Lua script files in game data
        ChatGui.Print($"{Tag} Searching for Lua scripts (names: {string.Join(", ", scriptNames)})...");
        var luaPaths = new List<string>();

        // Build search paths from script names (primary), NPC IDs, and ref IDs
        var searchNames = new List<string>(scriptNames);
        foreach (var npcId in gateKeeperIds)
            searchNames.Add(npcId.ToString());
        foreach (var refId in candidateRefs)
            searchNames.Add(refId.ToString());

        // Known paths + comprehensive GATE-related script search
        var knownPaths = new List<string>
        {
            // Confirmed paths
            "game_script/custom/002/CmnGscGATENotice_00242.luab",
            "game_script/system/GoldSaucerDirector.luab",

            // Potential GATE director scripts (system folder)
            "game_script/system/GFateDirector.luab",
            "game_script/system/GoldSaucerGFateDirector.luab",
            "game_script/system/GateDirector.luab",

            // Individual GATE directors — try various naming conventions
            "game_script/system/AirForceOneDirector.luab",
            "game_script/system/LeapOfFaithDirector.luab",
            "game_script/system/SliceIsRightDirector.luab",
            "game_script/system/AnyWayTheWindBlowsDirector.luab",
            "game_script/system/CliffhangerDirector.luab",

            // Content-type GATE scripts
            "game_script/content/GoldSaucerDirector.luab",
            "game_script/content/GFateDirector.luab",
            "game_script/content/GateDirector.luab",
        };

        // GATE-related script name patterns to search across all folder types
        var gateScriptPatterns = new[]
        {
            "GFateDirector", "GoldSaucerGFateDirector", "GateDirector",
            "AirForceOne", "LeapOfFaith", "SliceIsRight", "AnyWayTheWindBlows", "Cliffhanger",
            "GoldSaucerFate", "GscGFate", "GscFate", "GscGate",
            // CmnDef prefixed (common definition scripts)
            "CmnDefGoldSaucerFate", "CmnDefGscGFate", "CmnDefGate",
            // Public content director pattern
            "PublicContentGoldSaucer", "PublicContentGFate",
        };

        // Search these patterns across content/, fate/, system/, public_content/
        var searchFolders = new[]
        {
            "game_script/system",
            "game_script/content",
            "game_script/fate",
            "game_script/public_content",
        };

        foreach (var pattern in gateScriptPatterns)
        {
            foreach (var folder in searchFolders)
                knownPaths.Add($"{folder}/{pattern}.luab");
        }

        // Also search referenced script names in custom/NNN and quest/NNN buckets
        foreach (var name in scriptNames)
        {
            for (var bucket = 0; bucket <= 9; bucket++)
                knownPaths.Add($"game_script/custom/{bucket:D3}/{name}.luab");
            for (var bucket = 0; bucket <= 54; bucket++)
                knownPaths.Add($"game_script/quest/{bucket:D3}/{name}.luab");
        }

        // CRC32 index scan: find ALL files in game_script that contain "gate", "gfate",
        // "goldsaucer" in their name by checking known file hashes
        var gateFileNameCandidates = new List<string>();
        foreach (var pattern in gateScriptPatterns)
            gateFileNameCandidates.Add($"{pattern.ToLower()}.luab");

        // Also try with underscored versions
        var underscoredNames = new[] {
            "air_force_one", "leap_of_faith", "slice_is_right", "any_way_the_wind_blows",
            "cliffhanger", "gold_saucer_fate", "gold_saucer_gfate", "gfate_director",
            "gate_director", "gold_saucer_director", "gold_saucer_gate",
        };
        foreach (var name in underscoredNames)
            gateFileNameCandidates.Add($"{name}.luab");

        // Check CRC32 of each candidate against the game_script index
        try
        {
            var lumina = DataManager.GameData;
            if (lumina.Repositories.TryGetValue("ffxiv", out var repo) &&
                repo.Categories.TryGetValue(0x0B, out var gsCategoryList))
            {
                foreach (var cat in gsCategoryList)
                {
                    if (cat.IndexHashTableEntries == null) continue;

                    foreach (var candidate in gateFileNameCandidates)
                    {
                        var fileCrc = Lumina.Misc.Crc32.Get(candidate);
                        foreach (var kvp in cat.IndexHashTableEntries)
                        {
                            var entryFileHash = (uint)(kvp.Key & 0xFFFFFFFF);
                            if (entryFileHash == fileCrc)
                            {
                                var folderHash = (uint)(kvp.Key >> 32);
                                Log.Information($"{Tag} CRC32 MATCH: {candidate} (0x{fileCrc:X8}) in folder 0x{folderHash:X8}");
                                ChatGui.Print($"{Tag} CRC32 match: {candidate} folder=0x{folderHash:X8}");

                                // Try to resolve folder using known folders
                                var knownFolderMap = new Dictionary<string, string[]>
                                {
                                    { "system", new[] { "system" } },
                                    { "content", new[] { "content" } },
                                    { "fate", new[] { "fate" } },
                                    { "public_content", new[] { "public_content" } },
                                };
                                foreach (var (folderName, _) in knownFolderMap)
                                {
                                    if (Lumina.Misc.Crc32.Get(folderName) == folderHash)
                                    {
                                        var fullPath = $"game_script/{folderName}/{candidate}";
                                        ChatGui.Print($"{Tag} RESOLVED: {fullPath}");
                                        if (!knownPaths.Contains(fullPath))
                                            knownPaths.Add(fullPath);
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning($"{Tag} CRC32 scan failed: {ex.Message}");
        }

        var foundCount = 0;
        foreach (var path in knownPaths.Distinct())
        {
            try
            {
                if (DataManager.FileExists(path) && !luaPaths.Contains(path))
                {
                    luaPaths.Add(path);
                    foundCount++;
                    Log.Information($"{Tag} Lua FOUND: {path}");
                    ChatGui.Print($"{Tag} Lua FOUND: {path}");
                }
            }
            catch { }
        }
        ChatGui.Print($"{Tag} Searched {knownPaths.Distinct().Count()} paths, found {foundCount} new scripts");

        // 6. If any Lua scripts found, dump their raw bytes to files
        foreach (var luaPath in luaPaths)
        {
            try
            {
                var file = DataManager.GetFile(luaPath);
                if (file != null)
                {
                    var dir = PluginInterface.GetPluginConfigDirectory();
                    var safeName = luaPath.Replace("/", "_").Replace("\\", "_");
                    var outPath = Path.Combine(dir, $"lua_{safeName}");
                    File.WriteAllBytes(outPath, file.Data);
                    Log.Information($"{Tag} Lua bytecode saved: {outPath} ({file.Data.Length} bytes)");
                    ChatGui.Print($"{Tag} Lua saved: {outPath} ({file.Data.Length} bytes)");

                    // Dump first 256 bytes as hex
                    var previewSize = Math.Min(256, file.Data.Length);
                    for (var row = 0; row < previewSize; row += 16)
                    {
                        var hex = string.Join(" ", file.Data.Skip(row).Take(16).Select(b => b.ToString("X2")));
                        Log.Information($"{Tag}   lua+0x{row:X3}: {hex}");
                    }

                    // Scan for ASCII strings in the bytecode (function names, variable names, etc.)
                    var luaStrings = new List<string>();
                    var current = new List<byte>();
                    for (var bi = 0; bi < file.Data.Length; bi++)
                    {
                        var b = file.Data[bi];
                        if (b >= 0x20 && b < 0x7F)
                        {
                            current.Add(b);
                        }
                        else
                        {
                            if (current.Count >= 4)
                            {
                                var s = System.Text.Encoding.ASCII.GetString(current.ToArray());
                                luaStrings.Add($"+0x{bi - current.Count:X4}: {s}");
                            }
                            current.Clear();
                        }
                    }
                    if (current.Count >= 4)
                        luaStrings.Add($"+0x{file.Data.Length - current.Count:X4}: {System.Text.Encoding.ASCII.GetString(current.ToArray())}");

                    ChatGui.Print($"{Tag} Found {luaStrings.Count} ASCII strings in {luaPath}");
                    foreach (var s in luaStrings)
                        Log.Information($"{Tag}   str {s}");
                }
            }
            catch (Exception ex)
            {
                Log.Warning($"{Tag} Failed to read Lua script {luaPath}: {ex.Message}");
            }
        }

        if (luaPaths.Count == 0)
            ChatGui.Print($"{Tag} No Lua scripts found at common paths. Check log for CustomTalk refs.");

        ChatGui.Print($"{Tag} NPC investigation complete. Check Dalamud log for full data.");
    }

    #endregion

    #region Force Post & Course Tags

    private unsafe void ForceDirectorPost(string? course = null)
    {
        var mgr = GoldSaucerManager.Instance();
        var now = DateTime.UtcNow;
        var currentSlot = GateScheduler.GetCurrentGateTime(now);
        var world = GetCurrentWorldName();

        byte gateTypeByte = 0;
        byte posType = 0;
        int endTs = 0;
        uint flags = 0;
        string gateName = currentGateName ?? "unknown";

        if (mgr != null)
        {
            var director = mgr->CurrentGFateDirector;
            if (director != null)
            {
                gateTypeByte = (byte)director->GateType;
                posType = (byte)director->GatePositionType;
                endTs = director->EndTimestamp;
                flags = (uint)director->Flags;
                var mapped = MapDirectorGateType(gateTypeByte);
                gateName = mapped != null ? GateDefinitions.DisplayNames[mapped.Value] : $"unknown({gateTypeByte})";
            }
        }

        var courseStr = course != null ? $" course={course}" : "";
        ChatGui.Print($"{Tag} Force POST: {gateName} byte={gateTypeByte} pos={posType} flags=0x{flags:X8} slot={currentSlot.Minute}{courseStr}");
        Log.Information($"{Tag} Force POST [{now:O}]: {gateName} byte={gateTypeByte} pos={posType} endTs={endTs} flags=0x{flags:X8} slot={currentSlot.Minute}{courseStr}");

        if (world != null)
        {
            ApiService.ReportGate(world, gateName, currentSlot.Minute, "memory_director",
                $"byte={gateTypeByte},pos={posType},endTs={endTs},flags=0x{flags:X8}",
                gateTypeByte: gateTypeByte, positionType: posType, flags: (int)flags, course: course);

            if (course != null)
                ApiService.ReportScan(world, currentSlot.Minute, gateName, gateTypeByte, Array.Empty<byte>(), null, "active", course);
        }
    }

    private static readonly string[] AfoCourses = { "gold_saucer", "cieldalaes_cliff", "cieldalaes_cave", "cieldalaes_ship" };
    private static readonly string[] LofCourses = { "belahdia", "nym", "sylphstep" };

    private string? ResolveCourseTag(string? input)
    {
        if (string.IsNullOrEmpty(input)) return null;
        if (!int.TryParse(input, out var idx)) return input;

        var courses = currentGateName switch
        {
            "Air Force One" => AfoCourses,
            "Leap of Faith" => LofCourses,
            _ => null
        };

        if (courses == null)
        {
            ChatGui.Print($"{Tag} Numeric course tags only supported for AFO (0-3) and LoF (0-2)");
            return null;
        }
        if (idx < 0 || idx >= courses.Length)
        {
            ChatGui.Print($"{Tag} Index {idx} out of range (0-{courses.Length - 1}): {string.Join(", ", courses.Select((c, i) => $"{i}={c}"))}");
            return null;
        }

        ChatGui.Print($"{Tag} Course: {idx} → {courses[idx]}");
        return courses[idx];
    }

    #endregion

    #region NPC Prediction Mismatch

    private void CheckNpcPredictionMismatch(string announcedGate)
    {
        if (npcPredictedNextGate == null)
            return;

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

            LogSequenceBreak(npcPredictedNextGate, announcedGate, npcPredictionTime, DateTime.UtcNow);
            // Sequence break event reporting left to GateNotifier
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
            writer.WriteLine($"{predictionUtc:O},{announceUtc:O},{predicted},{actual},{GateScheduler.GetCurrentSlot(DateTime.UtcNow)}");
        }
        catch (Exception ex)
        {
            Log.Error($"{Tag} Failed to log sequence break: {ex.Message}");
        }
    }

    #endregion

    #region Event Framework

    /// <summary>
    /// Safe raw dump of EventFramework memory to file — NO pointer following, NO tree walking.
    /// Dumps first 512 bytes of EventFramework so we can verify struct layout before attempting traversal.
    /// </summary>
    private unsafe void DumpEventFrameworkState()
    {
        try
        {
            var ef = EventFramework.Instance();
            if (ef == null)
            {
                ChatGui.Print($"{Tag} EventFramework.Instance() is null");
                return;
            }

            var efAddr = (nint)ef;
            var moduleBase = SigScanner.Module.BaseAddress;
            Log.Information($"{Tag} EventFramework @ 0x{efAddr:X}, module base @ 0x{moduleBase:X}");
            ChatGui.Print($"{Tag} EventFramework @ 0x{efAddr:X}");

            // Dump 512 bytes of raw EventFramework memory to file
            var dumpSize = 512;
            var efBytes = new byte[dumpSize];
            System.Runtime.InteropServices.Marshal.Copy(efAddr, efBytes, 0, dumpSize);

            var dir = PluginInterface.GetPluginConfigDirectory();
            var path = Path.Combine(dir, $"ef_dump_{DateTime.UtcNow:yyyyMMdd_HHmmss}.hex");
            DumpMemoryToFile((byte*)efAddr, dumpSize, path, "EventFramework");

            // Also log to Dalamud log for quick viewing
            for (var row = 0; row < dumpSize; row += 16)
            {
                var hex = string.Join(" ", efBytes.Skip(row).Take(16).Select(b => b.ToString("X2")));
                Log.Information($"{Tag} EF+0x{row:X3}: {hex}");
            }

            // Annotate pointer-sized values that look like heap or module addresses
            for (var off = 0; off < dumpSize; off += 8)
            {
                var val = BitConverter.ToUInt64(efBytes, off);
                if (val == 0) continue;
                var asPtr = (long)val;
                var rva = asPtr - (long)moduleBase;
                if (rva > 0 && rva < SigScanner.Module.ModuleMemorySize)
                    Log.Information($"{Tag}   EF+0x{off:X3} = 0x{val:X} (module RVA 0x{rva:X})");
                else if (val > 0x10000000000 && val < 0x800000000000) // likely heap pointer
                    Log.Information($"{Tag}   EF+0x{off:X3} = 0x{val:X} (heap?)");
            }

            ChatGui.Print($"{Tag} Raw EF dump saved to {path}");
            ChatGui.Print($"{Tag} Check Dalamud log for annotated pointers.");
        }
        catch (Exception ex)
        {
            Log.Error($"{Tag} EF dump failed: {ex.Message}");
            ChatGui.Print($"{Tag} EF dump failed: {ex.Message}");
        }
    }

    private unsafe void DumpEventHandler(FFXIVClientStructs.FFXIV.Client.Game.Event.EventHandler* handler, string npcName, nint moduleBase, int modSize)
    {
        var handlerAddr = (nint)handler;
        Log.Information($"{Tag}   EventHandler @ 0x{handlerAddr:X}");

        // Dump vtable
        var vtablePtr = *(nint*)handlerAddr;
        var vtableRva = vtablePtr - moduleBase;
        Log.Information($"{Tag}   Vtable @ 0x{vtablePtr:X} (RVA 0x{vtableRva:X})");

        // Read vtable entries
        for (var i = 0; i < 20; i++)
        {
            var funcAddr = *(nint*)(vtablePtr + i * 8);
            var fRva = funcAddr - moduleBase;
            if (fRva <= 0 || fRva >= modSize) break;

            var prologue = new byte[16];
            System.Runtime.InteropServices.Marshal.Copy(funcAddr, prologue, 0, 16);
            var prologueHex = string.Join(" ", prologue.Select(b => b.ToString("X2")));
            Log.Information($"{Tag}   vtable[{i,2}] RVA 0x{fRva:X} prologue: {prologueHex}");
        }

        // Dump handler Info field (EventHandlerInfo)
        var info = handler->Info;
        Log.Information($"{Tag}   Info @ 0x{(nint)(&info):X}");
        var infoBytes = new byte[64];
        System.Runtime.InteropServices.Marshal.Copy((nint)(&info), infoBytes, 0, 64);
        for (var row = 0; row < 64; row += 16)
        {
            var hex = string.Join(" ", infoBytes.Skip(row).Take(16).Select(b => b.ToString("X2")));
            Log.Information($"{Tag}   Info+0x{row:X2}: {hex}");
        }

        // Dump handler memory (first 512 bytes to see structure)
        var handlerBytes = new byte[512];
        System.Runtime.InteropServices.Marshal.Copy(handlerAddr, handlerBytes, 0, 512);
        for (var row = 0; row < 512; row += 16)
        {
            var hex = string.Join(" ", handlerBytes.Skip(row).Take(16).Select(b => b.ToString("X2")));
            Log.Information($"{Tag}   handler+0x{row:X3}: {hex}");
        }

        // Check EventSceneModule
        var sceneModule = handler->EventSceneModule;
        if (sceneModule != null)
        {
            Log.Information($"{Tag}   EventSceneModule @ 0x{(nint)sceneModule:X}");
            var sceneBytes = new byte[128];
            System.Runtime.InteropServices.Marshal.Copy((nint)sceneModule, sceneBytes, 0, 128);
            for (var row = 0; row < 128; row += 16)
            {
                var hex = string.Join(" ", sceneBytes.Skip(row).Take(16).Select(b => b.ToString("X2")));
                Log.Information($"{Tag}   SceneModule+0x{row:X2}: {hex}");
            }
        }

        ChatGui.Print($"{Tag} \"{npcName}\" handler dumped — vtable RVA 0x{vtableRva:X}");
    }

    #endregion

    #region Utilities

    private static GateType? MapDirectorGateType(byte gateTypeByte)
    {
        return gateTypeByte switch
        {
            1 => GateType.Cliffhanger,
            5 => GateType.AnyWayTheWindBlows,
            6 => GateType.LeapOfFaith,
            7 => GateType.AirForceOne,
            8 => GateType.TheSliceIsRight,
            _ => null,
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

    #endregion
}
