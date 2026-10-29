using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Interface.Windowing;

namespace QuestItemAssistant.Windows;

internal sealed class SettingsWindow : Window, IDisposable
{
    private readonly Configuration configuration;
    private readonly Func<IReadOnlyList<MainWindow.KeyItemEntry>> getEntries;
    private readonly System.Action resetBarPosition;
    private bool waitingForKey;
    private bool lastKeyDown;

    public SettingsWindow(Configuration configuration, Func<IReadOnlyList<MainWindow.KeyItemEntry>> getEntries, System.Action resetBarPosition)
        : base("Quest Actions Settings##QuestItemAssistantSettings", ImGuiWindowFlags.NoCollapse)
    {
        this.configuration = configuration;
        this.getEntries = getEntries;
        this.resetBarPosition = resetBarPosition;
        Size = new Vector2(440, 650);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public void Dispose() { }

    private static bool IsVertical(BarGrowthDirection direction) => direction is BarGrowthDirection.Up or BarGrowthDirection.Down;
    private static bool IsHorizontal(BarGrowthDirection direction) => direction is BarGrowthDirection.Left or BarGrowthDirection.Right;

    public override void Draw()
    {
        ImGui.Separator();

        var direction = (int)configuration.GrowthDirection;
        string[] directions = { "Down", "Up", "Right", "Left" };
        ImGui.Text("Primary growth direction");
        ImGui.SetNextItemWidth(-1);
        if (ImGui.Combo("##PrimaryGrowthDirection", ref direction, directions, directions.Length))
        {
            configuration.GrowthDirection = (BarGrowthDirection)Math.Clamp(direction, 0, 3);
            if (IsVertical(configuration.GrowthDirection))
            {
                if (!IsHorizontal(configuration.SecondaryGrowthDirection))
                    configuration.SecondaryGrowthDirection = BarGrowthDirection.Right;
            }
            else if (!IsVertical(configuration.SecondaryGrowthDirection))
            {
                configuration.SecondaryGrowthDirection = BarGrowthDirection.Down;
            }
            configuration.Save();
        }

        var secondaryOptions = IsVertical(configuration.GrowthDirection)
            ? new[] { BarGrowthDirection.Right, BarGrowthDirection.Left }
            : new[] { BarGrowthDirection.Down, BarGrowthDirection.Up };
        var secondaryIndex = Array.IndexOf(secondaryOptions, configuration.SecondaryGrowthDirection);
        if (secondaryIndex < 0)
            secondaryIndex = 0;
        var secondaryLabels = secondaryOptions.Select(x => x.ToString()).ToArray();
        ImGui.Text("Secondary growth direction");
        ImGui.SetNextItemWidth(-1);
        if (ImGui.Combo("##SecondaryGrowthDirection", ref secondaryIndex, secondaryLabels, secondaryLabels.Length))
        {
            configuration.SecondaryGrowthDirection = secondaryOptions[Math.Clamp(secondaryIndex, 0, secondaryOptions.Length - 1)];
            configuration.Save();
        }

        var buttonsPerLine = Math.Max(1, configuration.ButtonsPerLine);
        ImGui.Text("Buttons per row / column");
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputInt("##ButtonsPerLine", ref buttonsPerLine))
        {
            buttonsPerLine = Math.Max(1, buttonsPerLine);
            configuration.ButtonsPerLine = buttonsPerLine;
            configuration.Save();
        }

        ImGui.Separator();
        var size = configuration.IconSize;
        ImGui.Text("Button size");
        ImGui.SetNextItemWidth(-1);
        if (ImGui.SliderFloat("##IconSize", ref size, 24f, 128f, "%.0f px"))
        {
            configuration.IconSize = size;
            configuration.Save();
        }
        var lockBarPosition = configuration.LockBarPosition;
        if (ImGui.Checkbox("Lock bar position", ref lockBarPosition))
        {
            configuration.LockBarPosition = lockBarPosition;
            configuration.Save();
        }
        ImGui.SameLine();
        if (ImGui.Button("Reset bar position"))
            resetBarPosition();

        var hideBackgroundPanel = configuration.HideBackgroundPanel;
        if (ImGui.Checkbox("Hide background panel", ref hideBackgroundPanel))
        {
            configuration.HideBackgroundPanel = hideBackgroundPanel;
            configuration.Save();
        }
        var showNames = configuration.ShowNames;
        if (ImGui.Checkbox("Show action name", ref showNames))
        {
            configuration.ShowNames = showNames;
            configuration.Save();
        }

        ImGui.Separator();
        ImGui.Text("Action key");
        ImGui.SameLine();
        if (waitingForKey)
        {
            ImGui.Button("Press a key…");
            CaptureKey();
        }
        else if (ImGui.Button($"{(configuration.UseKey == 0 ? "None" : ((VirtualKey)configuration.UseKey).ToString())}##SetKey"))
        {
            waitingForKey = true;
        }
        ImGui.SameLine();
        if (ImGui.Button("Clear"))
        {
            configuration.UseKey = 0;
            configuration.Save();
        }
        ImGui.TextDisabled("Right-click an item to lock selection.");

        ImGui.Separator();
        var sortByQuestProximity = configuration.SortByQuestProximity;
        if (ImGui.Checkbox("Sort by nearest quest objective", ref sortByQuestProximity))
        {
            configuration.SortByQuestProximity = sortByQuestProximity;
            configuration.Save();
        }
        var filterActionsByCurrentZone = configuration.FilterActionsByCurrentZone;
        if (ImGui.Checkbox("Only show actions for the current zone", ref filterActionsByCurrentZone))
        {
            configuration.FilterActionsByCurrentZone = filterActionsByCurrentZone;
            configuration.Save();
        }
        var showActionsWithUnknownZone = configuration.ShowActionsWithUnknownZone;
        ImGui.Indent(20f);
        ImGui.BeginDisabled(!filterActionsByCurrentZone);
        if (ImGui.Checkbox("Show actions when the objective zone is unknown", ref showActionsWithUnknownZone))
        {
            configuration.ShowActionsWithUnknownZone = showActionsWithUnknownZone;
            configuration.Save();
        }
        ImGui.EndDisabled();
        ImGui.Unindent(20f);

        var hideHuntBills = configuration.HideHuntBills;
        if (ImGui.Checkbox("Hide hunt bills", ref hideHuntBills))
        {
            configuration.HideHuntBills = hideHuntBills;
            configuration.Save();
        }

        ImGui.TextWrapped("Manually hide individual key items:");
        ImGui.BeginChild("QiaBlacklistList", new Vector2(0, 190), true);
        var entries = getEntries().OrderBy(x => x.Name).ThenBy(x => x.ItemId).ToArray();
        if (entries.Length == 0)
        {
            ImGui.TextDisabled("No visible key items found.");
        }
        foreach (var entry in entries)
        {
            var blacklisted = configuration.IsBlacklisted(entry.ItemId, entry.Name);
            ImGui.PushID($"blacklist-{entry.ItemId}-{entry.Slot}");
            if (ImGui.Checkbox($"{entry.Name}##blacklist", ref blacklisted))
            {
                if (blacklisted)
                {
                    configuration.WhitelistedItemIds.Remove(entry.ItemId);
                    if (!configuration.BlacklistedItemIds.Contains(entry.ItemId))
                        configuration.BlacklistedItemIds.Add(entry.ItemId);
                }
                else
                {
                    configuration.BlacklistedItemIds.Remove(entry.ItemId);
                    if (!configuration.WhitelistedItemIds.Contains(entry.ItemId))
                        configuration.WhitelistedItemIds.Add(entry.ItemId);
                }
                configuration.Save();
            }
            ImGui.PopID();
        }
        ImGui.EndChild();

    }

    private void CaptureKey()
    {
        var candidates = Plugin.KeyState.GetValidVirtualKeys()
            .Concat(Plugin.KeyState.GetExtendedVirtualKeys())
            .Distinct()
            .Where(k => k is not VirtualKey.SHIFT and not VirtualKey.CONTROL and not VirtualKey.MENU
                        and not VirtualKey.LSHIFT and not VirtualKey.RSHIFT
                        and not VirtualKey.LCONTROL and not VirtualKey.RCONTROL
                        and not VirtualKey.LMENU and not VirtualKey.RMENU
                        and not VirtualKey.ESCAPE and not VirtualKey.NO_KEY)
            .ToArray();

        var anyDown = false;
        foreach (var key in candidates)
        {
            var down = Plugin.KeyState[key];
            anyDown |= down;
            if (down && !lastKeyDown)
            {
                configuration.UseKey = (int)key;
                configuration.Save();
                waitingForKey = false;
                break;
            }
        }
        lastKeyDown = anyDown;
    }
}
