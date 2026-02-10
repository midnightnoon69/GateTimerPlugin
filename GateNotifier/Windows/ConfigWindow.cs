using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace GateNotifier.Windows;

public class ConfigWindow : Window, IDisposable
{
    private readonly Configuration configuration;

    private static readonly int[] AvailableAlertMinutes = { 10, 5, 3, 1 };

    private static readonly Vector4 GoldSaucerBg = new(0.12f, 0.08f, 0.18f, 0.95f);
    private static readonly Vector4 GoldBorder = new(0.85f, 0.65f, 0.13f, 0.80f);
    private static readonly Vector4 GoldText = new(1.00f, 0.84f, 0.00f, 1.00f);
    private static readonly Vector4 HeaderBg = new(0.35f, 0.15f, 0.50f, 0.80f);
    private static readonly Vector4 HeaderHover = new(0.45f, 0.25f, 0.60f, 0.80f);
    private static readonly Vector4 CheckMark = new(1.00f, 0.84f, 0.00f, 1.00f);
    private static readonly Vector4 FrameBg = new(0.20f, 0.12f, 0.28f, 0.80f);
    private static readonly Vector4 FrameHover = new(0.30f, 0.18f, 0.40f, 0.80f);

    public ConfigWindow(Plugin plugin) : base("GATE Notifier Settings###GateNotifierConfig")
    {
        Flags = ImGuiWindowFlags.AlwaysAutoResize;
        configuration = plugin.Configuration;
    }

    public void Dispose() { }

    public override void PreDraw()
    {
        ImGui.PushStyleColor(ImGuiCol.WindowBg, GoldSaucerBg);
        ImGui.PushStyleColor(ImGuiCol.Border, GoldBorder);
        ImGui.PushStyleColor(ImGuiCol.TitleBgActive, HeaderBg);
        ImGui.PushStyleColor(ImGuiCol.Header, HeaderBg);
        ImGui.PushStyleColor(ImGuiCol.HeaderHovered, HeaderHover);
        ImGui.PushStyleColor(ImGuiCol.HeaderActive, HeaderHover);
        ImGui.PushStyleColor(ImGuiCol.CheckMark, CheckMark);
        ImGui.PushStyleColor(ImGuiCol.FrameBg, FrameBg);
        ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, FrameHover);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 2.0f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 6.0f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(12, 10));
    }

    public override void PostDraw()
    {
        ImGui.PopStyleVar(3);
        ImGui.PopStyleColor(9);
    }

    public override void Draw()
    {
        ImGui.PushStyleColor(ImGuiCol.Text, GoldText);
        ImGui.TextUnformatted("GATE Notifications");
        ImGui.PopStyleColor();
        ImGui.Separator();

        // General settings
        if (ImGui.CollapsingHeader("General", ImGuiTreeNodeFlags.DefaultOpen))
        {
            var overlay = configuration.ShowOverlay;
            if (ImGui.Checkbox("Show countdown overlay", ref overlay))
            {
                configuration.ShowOverlay = overlay;
                configuration.Save();
            }

            var suppress = configuration.SuppressInDuty;
            if (ImGui.Checkbox("Suppress notifications in duties", ref suppress))
            {
                configuration.SuppressInDuty = suppress;
                configuration.Save();
            }
        }

        ImGui.Spacing();

        // GATE toggles
        if (ImGui.CollapsingHeader("Enabled GATEs", ImGuiTreeNodeFlags.DefaultOpen))
        {
            foreach (var gate in Enum.GetValues<GateType>())
            {
                var enabled = configuration.EnabledGates.TryGetValue(gate, out var val) && val;
                if (ImGui.Checkbox(GateDefinitions.DisplayNames[gate], ref enabled))
                {
                    configuration.EnabledGates[gate] = enabled;
                    configuration.Save();
                }
            }
        }

        ImGui.Spacing();

        // Alert timing
        if (ImGui.CollapsingHeader("Alert Timing", ImGuiTreeNodeFlags.DefaultOpen))
        {
            foreach (var minutes in AvailableAlertMinutes)
            {
                var active = configuration.AlertMinutesBefore.Contains(minutes);
                if (ImGui.Checkbox($"{minutes} min before", ref active))
                {
                    if (active && !configuration.AlertMinutesBefore.Contains(minutes))
                    {
                        configuration.AlertMinutesBefore.Add(minutes);
                        configuration.AlertMinutesBefore.Sort((a, b) => b.CompareTo(a));
                    }
                    else if (!active)
                    {
                        configuration.AlertMinutesBefore.Remove(minutes);
                    }

                    configuration.Save();
                }
            }
        }

        ImGui.Spacing();

        // Notification methods
        if (ImGui.CollapsingHeader("Notification Method", ImGuiTreeNodeFlags.DefaultOpen))
        {
            var chat = configuration.NotifyViaChat;
            if (ImGui.Checkbox("Chat messages", ref chat))
            {
                configuration.NotifyViaChat = chat;
                configuration.Save();
            }

            var toast = configuration.NotifyViaToast;
            if (ImGui.Checkbox("On-screen toast popup", ref toast))
            {
                configuration.NotifyViaToast = toast;
                configuration.Save();
            }
        }

        ImGui.Spacing();

        // Detection methods
        if (ImGui.CollapsingHeader("Detection Method", ImGuiTreeNodeFlags.DefaultOpen))
        {
            var timer = configuration.EnableTimerAlerts;
            if (ImGui.Checkbox("Timer-based alerts (works everywhere)", ref timer))
            {
                configuration.EnableTimerAlerts = timer;
                configuration.Save();
            }

            var chatDetect = configuration.EnableChatDetection;
            if (ImGui.Checkbox("Chat detection (Gold Saucer only)", ref chatDetect))
            {
                configuration.EnableChatDetection = chatDetect;
                configuration.Save();
            }
        }

    }
}
