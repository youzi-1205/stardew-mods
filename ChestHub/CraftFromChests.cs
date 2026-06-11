using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Inventories;
using StardewValley.Locations;
using StardewValley.Menus;
using StardewValley.Objects;

namespace ChestHub;

/// <summary>Feature 1: crafting and cooking draw materials from the player's chests, not just the
/// backpack. CraftingPage exposes a public <c>_materialContainers</c> list which it consults live
/// for both the "can craft" display (getContainerContents) and ingredient consumption
/// (CraftingRecipe.consumeIngredients), so injecting chest inventories right after the menu opens
/// is sufficient — no Harmony needed.</summary>
internal sealed class CraftFromChests
{
    private readonly IMonitor monitor;
    private readonly Func<ModConfig> config;

    public CraftFromChests(IModHelper helper, IMonitor monitor, Func<ModConfig> config)
    {
        this.monitor = monitor;
        this.config = config;
        helper.Events.Display.MenuChanged += this.OnMenuChanged;
    }

    private void OnMenuChanged(object? sender, MenuChangedEventArgs e)
    {
        if (!this.config().CraftFromChests || !Context.IsWorldReady)
            return;

        CraftingPage? page = e.NewMenu switch
        {
            CraftingPage standalone => standalone, // kitchen cooking / Workbench
            GameMenu gameMenu => gameMenu.pages.Count > GameMenu.craftingTab ? gameMenu.pages[GameMenu.craftingTab] as CraftingPage : null,
            _ => null,
        };
        if (page == null)
            return;

        List<IInventory> containers = page._materialContainers ?? new List<IInventory>();
        var seen = new HashSet<IInventory>(ReferenceEqualityComparer.Instance.As<IInventory>());
        foreach (IInventory existing in containers)
            seen.Add(existing);

        int added = 0;
        foreach (IInventory inventory in this.CollectChestInventories())
        {
            if (seen.Add(inventory))
            {
                containers.Add(inventory);
                added++;
            }
        }

        page._materialContainers = containers;
        this.monitor.Log($"Crafting menu opened: linked {added} chest inventories.", LogLevel.Trace);
    }

    /// <summary>All chest inventories the player can craft from.</summary>
    private IEnumerable<IInventory> CollectChestInventories()
    {
        var inventories = new List<IInventory>();
        bool everywhere = this.config().CraftFromChestsEverywhere;

        Utility.ForEachLocation(location =>
        {
            if (!everywhere && location is not Farm && location is not FarmHouse && !IsFarmInterior(location))
                return true;

            // Storage chests placed in the world (includes mini-fridges). GetItemsForPlayer
            // (rather than Items) is what makes Junimo chests work: their contents live in a
            // shared team inventory, and the local Items list stays empty.
            foreach (StardewValley.Object obj in location.objects.Values)
            {
                if (obj is Chest chest && chest.playerChest.Value && IsUsableStorage(chest))
                    inventories.Add(chest.GetItemsForPlayer());
            }

            // The built-in kitchen fridge isn't in location.objects.
            switch (location)
            {
                case FarmHouse farmHouse when farmHouse.fridge.Value != null:
                    inventories.Add(farmHouse.fridge.Value.Items);
                    break;
                case IslandFarmHouse islandHouse when islandHouse.fridge.Value != null:
                    inventories.Add(islandHouse.fridge.Value.Items);
                    break;
            }

            return true;
        }, includeInteriors: true);

        return inventories;
    }

    private static bool IsUsableStorage(Chest chest)
    {
        // Exclude functional chests whose contents have a different purpose.
        return chest.SpecialChestType is Chest.SpecialChestTypes.None
            or Chest.SpecialChestTypes.BigChest
            or Chest.SpecialChestTypes.JunimoChest;
    }

    private static bool IsFarmInterior(GameLocation location)
    {
        return location.ParentBuilding?.GetParentLocation() is Farm;
    }
}

/// <summary>Reference-equality comparer helper (Inventory doesn't override Equals, but be explicit).</summary>
internal sealed class ReferenceEqualityComparer
{
    public static readonly ReferenceEqualityComparer Instance = new();

    public IEqualityComparer<T> As<T>() where T : class => new Impl<T>();

    private sealed class Impl<T> : IEqualityComparer<T> where T : class
    {
        public bool Equals(T? x, T? y) => ReferenceEquals(x, y);
        public int GetHashCode(T obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
    }
}
