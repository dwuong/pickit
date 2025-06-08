using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Windows.Forms;
using ExileCore.Shared.Attributes;
using ExileCore.Shared.Interfaces;
using ExileCore.Shared.Nodes;
using ImGuiNET;
using Newtonsoft.Json;

namespace PickIt;

public class PickItSettings : ISettings
{
    public ToggleNode Enable { get; set; } = new ToggleNode(false);
    public ToggleNode ShowInventoryView { get; set; } = new ToggleNode(true);
    public ToggleNode MoveInventoryView { get; set; } = new ToggleNode(false);
    public HotkeyNode ProfilerHotkey { get; set; } = Keys.None;
    public HotkeyNode PickUpKey { get; set; } = Keys.F;
    public ToggleNode PickUpWhenInventoryIsFull { get; set; } = new ToggleNode(false);
    public RangeNode<int> PickupRange { get; set; } = new RangeNode<int>(600, 1, 1000);
    public ToggleNode IgnoreMoving { get; set; } = new ToggleNode(false);
    public RangeNode<int> ItemDistanceToIgnoreMoving { get; set; } = new RangeNode<int>(20, 0, 1000);
    public RangeNode<int> PauseBetweenClicks { get; set; } = new RangeNode<int>(100, 0, 500);
    public ToggleNode LazyLooting { get; set; } = new ToggleNode(false);
    public ToggleNode NoLazyLootingWhileEnemyClose { get; set; } = new ToggleNode(false);
    public HotkeyNode LazyLootingPauseKey { get; set; } = new HotkeyNode(Keys.Space);
    public ToggleNode PickUpEverything { get; set; } = new ToggleNode(false);
    public ToggleNode ClickChests { get; set; } = new ToggleNode(true);
    public ToggleNode ClickQuestChests { get; set; } = new ToggleNode(true);
    public ToggleNode ItemizeCorpses { get; set; } = new ToggleNode(true);

    // New setting for auto-clicking hovered loot
    public ToggleNode AutoClickHoveredLootInRange { get; set; } = new ToggleNode(false);

    [JsonIgnore]
    public TextNode FilterTest { get; set; } = new TextNode();

    [Menu("Use a Custom \"\\config\\custom_folder\" folder ")]
    public TextNode CustomConfigDir { get; set; } = new TextNode();

    public List<PickitRule> PickitRules = new List<PickitRule>();

    [JsonIgnore]
    public ToggleNode DebugHighlight { get; set; } = new ToggleNode(false);

    [JsonIgnore]
    public FilterNode Filters { get; } = new FilterNode();
}

[Submenu(RenderMethod = nameof(Render))]
public class FilterNode
{
    public void Render()
    {
        RulesDisplay.DrawSettings();
    }
}

public record PickitRule(string Name, string Location, bool Enabled)
{
    public bool Enabled = Enabled;
}