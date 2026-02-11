using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace GateNotifier.Windows;

public class OverlayWindow : Window, IDisposable
{
    private readonly Plugin plugin;
    private bool expanded = true;

    private static readonly Vector4 GoldSaucerBg = new(0.12f, 0.08f, 0.18f, 0.92f);
    private static readonly Vector4 GoldBorder = new(0.85f, 0.65f, 0.13f, 0.80f);
    private static readonly Vector4 GoldText = new(1.00f, 0.84f, 0.00f, 1.00f);
    private static readonly Vector4 PurpleAccent = new(0.72f, 0.45f, 0.90f, 1.00f);
    private static readonly Vector4 DimText = new(0.60f, 0.55f, 0.65f, 1.00f);
    private static readonly Vector4 GoldButton = new(0.55f, 0.35f, 0.08f, 1.00f);
    private static readonly Vector4 GoldButtonHover = new(0.70f, 0.50f, 0.10f, 1.00f);
    private static readonly Vector4 GoldButtonActive = new(0.85f, 0.65f, 0.13f, 1.00f);
    private static readonly Vector4 TitleBg = new(0.35f, 0.15f, 0.50f, 0.90f);
    private static readonly Vector4 SeparatorColor = new(0.85f, 0.65f, 0.13f, 0.40f);
    private static readonly Vector4 OrangeHighlight = new(0.95f, 0.60f, 0.15f, 1.00f);

    public OverlayWindow(Plugin plugin)
        : base("GATE Timer##GateOverlay", ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoCollapse)
    {
        this.plugin = plugin;
    }

    public void Dispose() { }

    public override void PreDraw()
    {
        ImGui.PushStyleColor(ImGuiCol.WindowBg, GoldSaucerBg);
        ImGui.PushStyleColor(ImGuiCol.Border, GoldBorder);
        ImGui.PushStyleColor(ImGuiCol.TitleBg, TitleBg);
        ImGui.PushStyleColor(ImGuiCol.TitleBgActive, TitleBg);
        ImGui.PushStyleColor(ImGuiCol.Button, GoldButton);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, GoldButtonHover);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, GoldButtonActive);
        ImGui.PushStyleColor(ImGuiCol.Separator, SeparatorColor);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 2.0f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 6.0f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(10, 8));
    }

    public override void PostDraw()
    {
        ImGui.PopStyleVar(3);
        ImGui.PopStyleColor(8);
    }

    public override void Draw()
    {
        var remaining = plugin.GetTimeUntilNextGate();
        var minutes = (int)remaining.TotalMinutes;
        var seconds = remaining.Seconds;
        var countdown = $"{minutes:D2}:{seconds:D2}";

        var possibleGates = plugin.GetPossibleGates();
        var hasTracked = plugin.HasEnabledGateInSlot(possibleGates);

        // Countdown line with toggle arrow
        var arrow = expanded ? "\u25BC" : "\u25B2";
        var countdownColor = hasTracked ? GoldText : DimText;
        ImGui.PushStyleColor(ImGuiCol.Text, countdownColor);
        if (ImGui.Selectable($"{arrow} Next GATE in {countdown}"))
        {
            expanded = !expanded;
        }
        ImGui.PopStyleColor();

        if (!hasTracked)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, DimText);
            ImGui.TextUnformatted("  No tracked GATEs this slot");
            ImGui.PopStyleColor();
        }

        if (!expanded)
            return;

        ImGui.Spacing();

        // Active GATE section
        ImGui.PushStyleColor(ImGuiCol.Text, GoldText);
        ImGui.TextUnformatted("Active GATE");
        ImGui.PopStyleColor();

        if (plugin.CurrentGateName != null)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, OrangeHighlight);
            ImGui.TextUnformatted($"  {plugin.CurrentGateName}");
            ImGui.PopStyleColor();
        }
        else
        {
            var currentGates = plugin.GetCurrentPossibleGates();
            foreach (var gate in currentGates)
            {
                var color = plugin.IsGateNameEnabled(gate) ? GoldText : DimText;
                ImGui.PushStyleColor(ImGuiCol.Text, color);
                ImGui.BulletText(gate);
                ImGui.PopStyleColor();
            }
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // Next GATE section
        ImGui.PushStyleColor(ImGuiCol.Text, GoldText);
        ImGui.TextUnformatted("Next GATE");
        ImGui.PopStyleColor();

        if (plugin.LastDetectedGateName != null)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, PurpleAccent);
            ImGui.TextUnformatted($"  {plugin.LastDetectedGateName}");
            ImGui.PopStyleColor();
        }
        else
        {
            foreach (var gate in possibleGates)
            {
                var color = plugin.IsGateNameEnabled(gate) ? GoldText : DimText;
                ImGui.PushStyleColor(ImGuiCol.Text, color);
                ImGui.BulletText(gate);
                ImGui.PopStyleColor();
            }

            ImGui.Spacing();
            ImGui.PushStyleColor(ImGuiCol.Text, DimText);
            ImGui.TextWrapped("One of the above will be chosen at random.\nVisit Gold Saucer for live detection.");
            ImGui.PopStyleColor();
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // Upcoming schedule
        ImGui.PushStyleColor(ImGuiCol.Text, GoldText);
        ImGui.TextUnformatted("Upcoming Schedule");
        ImGui.PopStyleColor();

        ImGui.Spacing();

        var slots = plugin.GetUpcomingSlots(3);
        foreach (var (time, gates) in slots)
        {
            var localTime = time.ToLocalTime();
            var timeStr = localTime.ToString("HH:mm");
            var slotHasTracked = plugin.HasEnabledGateInSlot(gates);

            var lineColor = slotHasTracked ? PurpleAccent : DimText;
            ImGui.PushStyleColor(ImGuiCol.Text, lineColor);
            ImGui.TextUnformatted($"  {timeStr}  {string.Join(" / ", gates)}");
            ImGui.PopStyleColor();
        }

        ImGui.PushStyleColor(ImGuiCol.Text, DimText);
        ImGui.TextUnformatted("  Schedule repeats every hour.");
        ImGui.PopStyleColor();

        ImGui.Spacing();

        if (ImGui.Button("Settings"))
        {
            plugin.ToggleConfigUi();
        }
    }
}
