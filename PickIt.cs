using ExileCore;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.Elements;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared;
using ExileCore.Shared.Cache;
using ExileCore.Shared.Enums;
using ExileCore.Shared.Helpers;
using ImGuiNET;
using ItemFilterLibrary;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using ExileCore.PoEMemory;
using SharpDX;
using SDxVector2 = SharpDX.Vector2;
using Vector2 = System.Numerics.Vector2;
using Vector3 = System.Numerics.Vector3;

namespace PickIt;

public partial class PickIt : BaseSettingsPlugin<PickItSettings>
{
    private readonly CachedValue<List<LabelOnGround>> _chestLabels;
    private readonly CachedValue<LabelOnGround> _portalLabel;
    private readonly CachedValue<List<LabelOnGround>> _corpseLabels;
    private readonly CachedValue<bool[,]> _inventorySlotsCache;
    private ServerInventory _inventoryItems;
    private SyncTask<bool> _pickUpTask;
    public List<ItemFilter> ItemFilters;
    private bool _pluginBridgeModeOverride;
    public static PickIt Main;
    private bool[,] InventorySlots => _inventorySlotsCache.Value;
    private readonly Stopwatch _sinceLastClick = Stopwatch.StartNew();
    private DateTime _disableLazyLootingTill; // Declare this field at the class level

    // Corrected UIHoverWithFallback definition, directly providing the logic.
    private Element UIHoverWithFallback => GameController.IngameState.UIHover switch { null or { Address: 0 } => GameController.IngameState.UIHoverElement, var s => s };

    public PickIt()
    {
        Name = "PickIt With Linq";
        // Initialize the inventory slots cache, relying on the GetContainer2DArray method defined later.
        // The actual implementation of GetContainer2DArray should be in another partial class file or provided by the user.
        _inventorySlotsCache = new FrameCache<bool[,]>(() => GetContainer2DArray(_inventoryItems)); 
        _chestLabels = new TimeCache<List<LabelOnGround>>(UpdateChestList, 200);
        _corpseLabels = new TimeCache<List<LabelOnGround>>(UpdateCorpseList, 200);
        _portalLabel = new TimeCache<LabelOnGround>(() => GetLabel(@"^Metadata/(MiscellaneousObjects|Effects/Microtransactions)/.*Portal"), 200);
    }

    public override bool Initialise()
    {
        Main = this;

        #region Register keys
        // Registering hotkeys for pick-up and profiler functionality.
        Settings.PickUpKey.OnValueChanged += () => Input.RegisterKey(Settings.PickUpKey);
        Settings.ProfilerHotkey.OnValueChanged += () => Input.RegisterKey(Settings.ProfilerHotkey);

        Input.RegisterKey(Settings.PickUpKey);
        Input.RegisterKey(Settings.ProfilerHotkey);
        Input.RegisterKey(Keys.Escape);

        #endregion

        // Loading and applying item filter rules.
        Task.Run(RulesDisplay.LoadAndApplyRules);
        // Registering methods for external plugin bridge interaction.
        GameController.PluginBridge.SaveMethod("PickIt.ListItems", () => GetItemsToPickup(false).Select(x => x.QueriedItem).ToList());
        GameController.PluginBridge.SaveMethod("PickIt.IsActive", () => _pickUpTask?.GetAwaiter().IsCompleted == false);
        GameController.PluginBridge.SaveMethod("PickIt.SetWorkMode", (bool running) => { _pluginBridgeModeOverride = running; });
        return true;
    }

    // Enumeration for defining the different operational modes of the plugin.
    private enum WorkMode
    {
        Stop,   // Plugin is inactive.
        Lazy,   // Lazy looting mode (automated within certain conditions).
        Manual  // Manual pick-up triggered by hotkey.
    }

    // Determines the current operational mode of the plugin based on game state and user input.
    private WorkMode GetWorkMode()
    {
        // If the game window is not in foreground, plugin is disabled, or Escape key is pressed, stop operations.
        if (!GameController.Window.IsForeground() ||
            !Settings.Enable ||
            Input.GetKeyState(Keys.Escape))
        {
            _pluginBridgeModeOverride = false;
            return WorkMode.Stop;
        }

        // If profiler hotkey is pressed, measure and log the time taken to find items.
        if (Input.GetKeyState(Settings.ProfilerHotkey.Value))
        {
            var sw = Stopwatch.StartNew();
            var looseVar2 = GetItemsToPickup(false).FirstOrDefault();
            sw.Stop();
            LogMessage($"GetItemsToPickup Elapsed Time: {sw.ElapsedTicks} Item: {looseVar2?.BaseName} Distance: {looseVar2?.Distance}");
        }

        // If the manual pick-up key is pressed or plugin bridge mode is overridden, operate in manual mode.
        if (Input.GetKeyState(Settings.PickUpKey.Value) || _pluginBridgeModeOverride)
        {
            return WorkMode.Manual;
        }

        // If lazy looting is enabled and permissible, operate in lazy mode.
        if (CanLazyLoot())
        {
            return WorkMode.Lazy;
        }

        // Otherwise, stop operations.
        return WorkMode.Stop;
    }

    // This override method is called every game tick.
    public override Job Tick()
    {
        // Check if player inventory data is available.
        var playerInvCount = GameController?.Game?.IngameState?.Data?.ServerData?.PlayerInventories?.Count;
        if (playerInvCount is null or 0)
            return null; // No inventory data, nothing to do.

        // Logic for auto-clicking hovered loot if enabled.
        if (Settings.AutoClickHoveredLootInRange.Value)
        {
            // Get the currently hovered item icon, using the fallback logic.
            var hoverItemIcon = UIHoverWithFallback.AsObject<HoverItemIcon>();
            // Check if an item is hovered, inventory panel is not open, and left mouse button is not held down.
            if (hoverItemIcon != null && !GameController.IngameState.IngameUi.InventoryPanel.IsVisible &&
                !Input.IsKeyDown(Keys.LButton))
            {
                // If a valid item is hovered and it's okay to click (determined by OkayToClick method).
                if (hoverItemIcon.Item != null && OkayToClick()) 
                {
                    // Find the ground item label corresponding to the hovered item icon.
                    var groundItem = GameController.IngameState.IngameUi.ItemsOnGroundLabelElement.VisibleGroundItemLabels
                        .FirstOrDefault(e => e.Label.Address == hoverItemIcon.Address);
                    // If a ground item is found.
                    if (groundItem != null)
                    {
                        // Check if this item should be picked up based on filters.
                        var doWePickThis = DoWePickThis(new PickItItemData(groundItem, GameController));
                        // If the item should be picked and is within a short distance from the player.
                        if (doWePickThis && groundItem.Entity.DistancePlayer < 20f)
                        {
                            _sinceLastClick.Restart(); // Reset the click timer.
                            Input.Click(MouseButtons.Left); // Simulate a left-click.
                        }
                    }
                }
            }
        }

        // Update the cached inventory items.
        _inventoryItems = GameController.Game.IngameState.Data.ServerData.PlayerInventories[0].Inventory;
        // Draw inventory cell settings for debugging/configuration.
        DrawIgnoredCellsSettings();
        // Pause lazy looting for a duration if the pause key is pressed.
        if (Input.GetKeyState(Settings.LazyLootingPauseKey)) _disableLazyLootingTill = DateTime.Now.AddSeconds(2);

        return null; // Return null as this Tick method doesn't need to queue a job.
    }

    // This override method is called every frame for rendering.
    public override void Render()
    {
        // If debug highlighting is enabled, draw frames around items to be picked up.
        if (Settings.DebugHighlight)
        {
            foreach (var item in GetItemsToPickup(false))
            {
                Graphics.DrawFrame(item.QueriedItem.ClientRect, Color.Violet, 5);
            }
        }

        // If the plugin is in an active work mode, run or restart the pick-up task.
        if (GetWorkMode() != WorkMode.Stop)
        {
            TaskUtils.RunOrRestart(ref _pickUpTask, RunPickerIterationAsync);
        }
        else
        {
            _pickUpTask = null; // If stopped, clear the pick-up task.
        }

        // Debugging for item filter testing.
        if (Settings.FilterTest.Value is { Length: > 0 } &&
            GameController.IngameState.UIHover is { Address: not 0 } h &&
            h.Entity.IsValid)
        {
            var f = ItemFilter.LoadFromString(Settings.FilterTest);
            var matched = f.Matches(new ItemData(h.Entity, GameController));
            DebugWindow.LogMsg($"Debug item match: {matched}");
        }
    }

    // Draws the ImGui window for configuring ignored inventory cells.
    private void DrawIgnoredCellsSettings()
    {
        if (!Settings.ShowInventoryView.Value)
            return;

        var opened = true; // Controls the visibility of the ImGui window.

        // ImGui window flags for moveable and non-moveable states.
        const ImGuiWindowFlags moveableFlag = ImGuiWindowFlags.NoScrollbar |
                                              ImGuiWindowFlags.NoTitleBar |
                                              ImGuiWindowFlags.NoFocusOnAppearing;

        const ImGuiWindowFlags nonMoveableFlag = ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoBackground |
                                                  ImGuiWindowFlags.NoTitleBar |
                                                  ImGuiWindowFlags.NoInputs |
                                                  ImGuiWindowFlags.NoFocusOnAppearing;

        // Begin the ImGui window.
        if (ImGui.Begin($"{Name}##InventoryCellMap", ref opened,
                Settings.MoveInventoryView.Value ? moveableFlag : nonMoveableFlag))
        {
            var numb = 0;
            // Iterate through a 5x12 grid representing inventory cells.
            for (var i = 0; i < 5; i++)
            for (var j = 0; j < 12; j++)
            {
                var toggled = Convert.ToBoolean(InventorySlots[i, j]);
                // Create a checkbox for each cell to toggle its ignored state.
                if (ImGui.Checkbox($"##{numb}IgnoredCells", ref toggled)) InventorySlots[i, j] = toggled;

                if (j != 11) ImGui.SameLine(); // Keep checkboxes on the same line for each row.

                numb += 1;
            }

            ImGui.End(); // End the ImGui window.
        }
    }

    // Determines if an item should be picked up based on settings and item filters.
    private bool DoWePickThis(PickItItemData item)
    {
        return Settings.PickUpEverything || (ItemFilters?.Any(filter => filter.Matches(item)) ?? false);
    }

    // Updates the list of visible chest labels.
    private List<LabelOnGround> UpdateChestList()
    {
        // Helper function to check if an entity is a fitting chest type.
        bool IsFittingEntity(Entity entity)
        {
            return entity?.Path is { } path &&
                   (Settings.ClickQuestChests && path.StartsWith("Metadata/Chests/QuestChests/", StringComparison.Ordinal) ||
                    path.StartsWith("Metadata/Chests/LeaguesExpedition/", StringComparison.Ordinal) ||
                    path.StartsWith("Metadata/Chests/LegionChests/", StringComparison.Ordinal) ||
                    path.StartsWith("Metadata/Chests/Blight", StringComparison.Ordinal) ||
                    path.StartsWith("Metadata/Chests/Breach/", StringComparison.Ordinal) ||
                    path.StartsWith("Metadata/Chests/IncursionChest", StringComparison.Ordinal) ||
                    path.StartsWith("Metadata/Chests/LeagueSanctum/")) &&
                   entity.HasComponent<Chest>();
        }

        // If there are any fitting chest entities in the game.
        if (GameController.EntityListWrapper.OnlyValidEntities.Any(IsFittingEntity))
        {
            // Return visible ground labels that correspond to fitting chest entities, ordered by distance.
            return GameController?.Game?.IngameState?.IngameUi?.ItemsOnGroundLabelsVisible
                .Where(x => x.Address != 0 &&
                            x.IsVisible &&
                            IsFittingEntity(x.ItemOnGround))
                .OrderBy(x => x.ItemOnGround.DistancePlayer)
                .ToList() ?? [];
        }

        return []; // No fitting chests found.
    }

    // Updates the list of visible corpse labels.
    private List<LabelOnGround> UpdateCorpseList()
    {
        // Helper function to check if an entity is a Necropolis corpse marker.
        bool IsFittingEntity(Entity entity)
        {
            return entity?.Path is "Metadata/Terrain/Leagues/Necropolis/Objects/NecropolisCorpseMarker";
        }

        // If there are any fitting corpse entities in the game.
        if (GameController.EntityListWrapper.OnlyValidEntities.Any(IsFittingEntity))
        {
            // Return visible ground labels that correspond to fitting corpse entities, ordered by distance.
            return GameController?.Game?.IngameState?.IngameUi?.ItemsOnGroundLabelsVisible
                .Where(x => x.Address != 0 &&
                            x.IsVisible &&
                            IsFittingEntity(x.ItemOnGround))
                .OrderBy(x => x.ItemOnGround.DistancePlayer)
                .ToList() ?? [];
        }

        return []; // No fitting corpses found.
    }

    // Determines if lazy looting is currently allowed.
    private bool CanLazyLoot()
    {
        if (!Settings.LazyLooting) return false; // Lazy looting is disabled.
        if (_disableLazyLootingTill > DateTime.Now) return false; // Lazy looting is temporarily paused.
        try
        {
            // If "No Lazy Looting While Enemy Close" is enabled, check for nearby hostile monsters.
            if (Settings.NoLazyLootingWhileEnemyClose && GameController.EntityListWrapper.ValidEntitiesByType[EntityType.Monster]
                    .Any(x => x?.GetComponent<Monster>() != null && x.IsValid && x.IsHostile && x.IsAlive
                              && !x.IsHidden && !x.Path.Contains("ElementalSummoned")
                              && Vector3.Distance(GameController.Player.PosNum, x.GetComponent<Render>().PosNum) < Settings.PickupRange)) return false;
        }
        catch (NullReferenceException)
        {
            // Handle potential null reference exceptions gracefully.
        }

        return true; // Lazy looting is allowed.
    }

    // Determines if a specific item should be lazy looted based on player and item positions.
    private bool ShouldLazyLoot(PickItItemData item)
    {
        if (item == null)
        {
            return false;
        }

        var itemPos = item.QueriedItem.Entity.PosNum;
        var playerPos = GameController.Player.PosNum;
        // Check if item's Z-coordinate is close to player's and horizontal distance is within a certain range.
        return Math.Abs(itemPos.Z - playerPos.Z) <= 50 &&
               itemPos.Xy().DistanceSquared(playerPos.Xy()) <= 275 * 275;
    }

    // Checks if a given UI element (label) is clickable within the game window.
    private bool IsLabelClickable(Element element, RectangleF? customRect)
    {
        if (element is not { IsValid: true, IsVisible: true, IndexInParent: not null })
        {
            return false; // Element is not valid, visible, or lacks an index in parent.
        }

        var center = (customRect ?? element.GetClientRect()).Center; // Get the center point of the element.

        // Get the game window rectangle and inflate it to account for borders.
        var gameWindowRect = GameController.Window.GetWindowRectangleTimeCache with { Location = SDxVector2.Zero };
        gameWindowRect.Inflate(-36, -36);
        return gameWindowRect.Contains(center.X, center.Y); // Check if the element's center is within the clickable area.
    }

    // Checks if the portal label is currently targeted by the UI.
    private bool IsPortalTargeted(LabelOnGround portalLabel)
    {
        if (portalLabel == null)
        {
            return false; // No portal label provided.
        }

        // Perform various checks against different UI hover elements to confirm portal targeting.
        return
            GameController.IngameState.UIHover.Address == portalLabel.Address ||
            GameController.IngameState.UIHover.Address == portalLabel.ItemOnGround.Address ||
            GameController.IngameState.UIHover.Address == portalLabel.Label.Address ||
            GameController.IngameState.UIHoverElement.Address == portalLabel.Address ||
            GameController.IngameState.UIHoverElement.Address == portalLabel.ItemOnGround.Address ||
            GameController.IngameState.UIHoverElement.Address == portalLabel.Label.Address || // This is typically the correct one.
            GameController.IngameState.UIHoverTooltip.Address == portalLabel.Address ||
            GameController.IngameState.UIHoverTooltip.Address == portalLabel.ItemOnGround.Address ||
            GameController.IngameState.UIHoverTooltip.Address == portalLabel.Label.Address ||
            portalLabel.ItemOnGround?.HasComponent<Targetable>() == true &&
            portalLabel.ItemOnGround?.GetComponent<Targetable>()?.isTargeted == true;
    }

    // Checks if a portal label is nearby a given element.
    private static bool IsPortalNearby(LabelOnGround portalLabel, Element element)
    {
        if (portalLabel == null) return false;
        var rect1 = portalLabel.Label.GetClientRectCache;
        var rect2 = element.GetClientRectCache;
        rect1.Inflate(100, 100); // Inflate rectangles for a broader intersection check.
        rect2.Inflate(100, 100);
        return rect1.Intersects(rect2); // Check for intersection.
    }

    // Retrieves a ground label entity based on a regular expression ID.
    private LabelOnGround GetLabel(string id)
    {
        var labels = GameController?.Game?.IngameState?.IngameUi?.ItemsOnGroundLabels;
        if (labels == null)
        {
            return null; // No ground labels available.
        }

        var regex = new Regex(id);
        // Query for visible and valid labels whose item's metadata matches the regex, ordered by distance.
        var labelQuery =
            from labelOnGround in labels
            where labelOnGround?.Label is { IsValid: true, Address: > 0, IsVisible: true }
            let itemOnGround = labelOnGround.ItemOnGround
            where itemOnGround?.Metadata is { } metadata && regex.IsMatch(metadata)
            let dist = GameController?.Player?.GridPosNum.DistanceSquared(itemOnGround.GridPosNum)
            orderby dist
            select labelOnGround;

        return labelQuery.FirstOrDefault(); // Return the closest matching label.
    }

    // Asynchronous task to run a single iteration of the pick-up logic.
    private async SyncTask<bool> RunPickerIterationAsync()
    {
        if (!GameController.Window.IsForeground()) return true; // Only operate if game window is in foreground.

        var pickUpThisItem = GetItemsToPickup(true).FirstOrDefault(); // Get the item to pick up.

        var workMode = GetWorkMode(); // Determine current work mode.
        // If in manual mode, or lazy mode and the item qualifies for lazy looting.
        if (workMode == WorkMode.Manual || workMode == WorkMode.Lazy && ShouldLazyLoot(pickUpThisItem))
        {
            // Logic for itemizing (clicking) corpses.
            if (Settings.ItemizeCorpses)
            {
                var corpseLabel = _corpseLabels?.Value.FirstOrDefault(x =>
                    x.ItemOnGround.DistancePlayer < Settings.PickupRange &&
                    IsLabelClickable(x.Label, null));

                if (corpseLabel != null)
                {
                    // Attempt to pick up the corpse.
                    await PickAsync(corpseLabel.ItemOnGround, corpseLabel.Label?.GetChildFromIndices(0, 2, 1), null, _corpseLabels.ForceUpdate);
                    return true;
                }
            }

            // Logic for clicking chests.
            if (Settings.ClickChests)
            {
                var chestLabel = _chestLabels?.Value.FirstOrDefault(x =>
                    x.ItemOnGround.DistancePlayer < Settings.PickupRange &&
                    IsLabelClickable(x.Label, null));

                // If a chest is found and it's closer than or equal to the current item to pick up.
                if (chestLabel != null && (pickUpThisItem == null || pickUpThisItem.Distance >= chestLabel.ItemOnGround.DistancePlayer))
                {
                    // Attempt to pick up the chest.
                    await PickAsync(chestLabel.ItemOnGround, chestLabel.Label, null, _chestLabels.ForceUpdate);
                    return true;
                }
            }

            if (pickUpThisItem == null)
            {
                return true; // No item to pick up.
            }

            pickUpThisItem.AttemptedPickups++; // Increment attempt counter for the item.
            // Attempt to pick up the item.
            await PickAsync(pickUpThisItem.QueriedItem.Entity, pickUpThisItem.QueriedItem.Label, pickUpThisItem.QueriedItem.ClientRect, () => { });
        }

        return true;
    }

    // Gets a filtered list of items to pick up.
    private IEnumerable<PickItItemData> GetItemsToPickup(bool filterAttempts)
    {
        // Get visible ground item labels within pickup range, ordered by distance.
        var labels = GameController.Game.IngameState.IngameUi.ItemsOnGroundLabelElement.VisibleGroundItemLabels?
            .Where(x=> x.Entity?.DistancePlayer is {} distance && distance < Settings.PickupRange)
            .OrderBy(x => x.Entity?.DistancePlayer ?? int.MaxValue);

        // Filter labels based on clickability, item data, attempt count, and inventory space.
        return labels?
            .Where(x => x.Entity?.Path != null && IsLabelClickable(x.Label, x.ClientRect))
            .Select(x => new PickItItemData(x, GameController))
            .Where(x => x.Entity != null
                        && (!filterAttempts || x.AttemptedPickups == 0) // Filter out items with pick-up attempts if specified.
                        && DoWePickThis(x) // Check if the item matches pick-up filters.
                        && (Settings.PickUpWhenInventoryIsFull || CanFitInventory(x))) ?? []; // Check if item can fit or if picking up even when full is allowed.
    }

    // Asynchronously attempts to pick up an item by clicking its label.
    private async SyncTask<bool> PickAsync(Entity item, Element label, RectangleF? customRect, Action onNonClickable)
    {
        var tryCount = 0;
        while (tryCount < 3) // Attempt to click up to 3 times.
        {
            if (!IsLabelClickable(label, customRect))
            {
                onNonClickable(); // Callback for non-clickable labels.
                return true; // Label is not clickable, exit.
            }

            // If "Ignore Moving" is disabled and player is moving, check item distance.
            if (!Settings.IgnoreMoving && GameController.Player.GetComponent<Actor>().isMoving)
            {
                if (item.DistancePlayer > Settings.ItemDistanceToIgnoreMoving.Value)
                {
                    await TaskUtils.NextFrame(); // Wait for next frame and continue loop.
                    continue;
                }
            }

            // Calculate a random click position within the label's client rect.
            var position = label.GetClientRect().ClickRandomNum(5, 3) + GameController.Window.GetWindowRectangleTimeCache.TopLeft.ToVector2Num();
            // If enough time has passed since the last click.
            if (_sinceLastClick.ElapsedMilliseconds > Settings.PauseBetweenClicks)
            {
                if (!IsTargeted(item, label)) // If item is not yet targeted, set cursor position.
                {
                    await SetCursorPositionAsync(position, item, label);
                }
                else // Item is targeted, proceed to click.
                {
                    if (await CheckPortal(label)) return true; // Handle nearby portals to avoid accidental clicks.
                    if (!IsTargeted(item, label)) // Re-check targeting after portal check.
                    {
                        await TaskUtils.NextFrame();
                        continue;
                    }

                    Input.Click(MouseButtons.Left); // Perform the click.
                    _sinceLastClick.Restart(); // Reset click timer.
                    tryCount++; // Increment try count.
                }
            }

            await TaskUtils.NextFrame(); // Wait for next frame.
        }

        return true;
    }

    // Asynchronously checks for nearby portals and if one is targeted.
    private async Task<bool> CheckPortal(Element label)
    {
        if (!IsPortalNearby(_portalLabel.Value, label)) return false; // No portal nearby.
        // If a portal is nearby, perform extra checks with delays to confirm targeting.
        if (IsPortalTargeted(_portalLabel.Value))
        {
            return true;
        }

        await Task.Delay(25); // Small delay.
        return IsPortalTargeted(_portalLabel.Value); // Re-check portal targeting.
    }

    // Checks if an item or its label is currently targeted in the UI.
    private static bool IsTargeted(Entity item, Element label)
    {
        if (item == null) return false;
        // Check if the item's Targetable component indicates it's targeted.
        if (item.GetComponent<Targetable>()?.isTargeted is { } isTargeted)
        {
            return isTargeted;
        }

        return label is { HasShinyHighlight: true }; // Alternatively, check if the label has a shiny highlight.
    }

    // Asynchronously sets the cursor position and waits until the item is targeted.
    private static async SyncTask<bool> SetCursorPositionAsync(Vector2 position, Entity item, Element label)
    {
        DebugWindow.LogMsg($"Set cursor pos: {position}"); // Log the cursor position.
        Input.SetCursorPos(position); // Set the cursor's physical position.
        // Wait for the item to become targeted, with a 60ms timeout.
        return await TaskUtils.CheckEveryFrame(() => IsTargeted(item, label), new CancellationTokenSource(60).Token);
    }

    // --- Placeholder Methods ---
    // You will need to implement these methods with your specific game logic.

    /// <summary>
    /// Placeholder method to determine if an item can fit into the player's inventory.
    /// This requires detailed logic based on item size and available inventory slots.
    /// </summary>
    /// <param name="item">The PickItItemData representing the item to check.</param>
    /// <returns>True if the item can fit, false otherwise.</returns>
    private bool CanFitInventory(PickItItemData item)
    {
        // TODO: Implement actual inventory fitting logic here.
        // This is a placeholder. You'll need to implement the actual logic
        // to check if the item can fit in the player's inventory.
        // This might involve checking the item's size against available inventory slots
        // and the 'InventorySlots' array (which is populated by GetContainer2DArray).
        return true; 
    }

    /// <summary>
    /// Placeholder method to determine if it is currently safe and desirable to perform a click action.
    /// This could involve checks for game focus, active UI elements, etc.
    /// </summary>
    /// <returns>True if it's okay to click, false otherwise.</returns>
    private bool OkayToClick()
    {
        // TODO: Implement actual click safety checks here.
        // This is a placeholder. You'll need to implement the actual logic
        // to determine if it's okay to click.
        // This could involve checks like whether the game window is active,
        // if there are any blocking UI elements, etc.
        return true; 
    }
}
