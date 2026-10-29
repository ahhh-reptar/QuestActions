using System;
using System.Collections.Generic;
using Dalamud.Configuration;
using Dalamud.Plugin;

namespace QuestItemAssistant;

public enum BarGrowthDirection
{
    Down = 0,
    Up = 1,
    Right = 2,
    Left = 3,
}

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 10;
    public bool ShowHotbar { get; set; } = true;
    public bool HideBackgroundPanel { get; set; } = false;
    public bool LockBarPosition { get; set; } = false;
    public bool Horizontal { get; set; } = false;
    public bool ShowNames { get; set; } = false;
    public float IconSize { get; set; } = 48f;
    public int ButtonsPerLine { get; set; } = 10;
    public int UseKey { get; set; } = 0x78;
    public BarGrowthDirection GrowthDirection { get; set; } = BarGrowthDirection.Down;
    public BarGrowthDirection SecondaryGrowthDirection { get; set; } = BarGrowthDirection.Right;

    public bool FirstRunSetupCompleted { get; set; } = false;

    public List<uint> BlacklistedItemIds { get; set; } = new();
    public List<uint> WhitelistedItemIds { get; set; } = new();

    public bool HideHuntBills { get; set; } = true;

    public bool SortByQuestProximity { get; set; } = true;

    public bool FilterActionsByCurrentZone { get; set; } = false;
    public bool ShowActionsWithUnknownZone { get; set; } = true;

    [NonSerialized] private IDalamudPluginInterface? pluginInterface;

    public void Initialize(IDalamudPluginInterface pi) => pluginInterface = pi;
    public void Save() => pluginInterface?.SavePluginConfig(this);

    public bool IsBlacklisted(uint itemId, string name)
    {
        if (WhitelistedItemIds.Contains(itemId))
            return false;

        if (BlacklistedItemIds.Contains(itemId))
            return true;

        if (HideHuntBills)
        {
            var normalized = name.Trim();
            if (normalized.Contains("hunt bill", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("hunt mark bill", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("mark bill", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
