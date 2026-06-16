using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Menus;

namespace BiggerBackpack;

internal sealed class ModConfig
{
    // Target backpack size in slots. 36 = vanilla full; 48 = 4 rows. Whole rows of 12 only.
    public int BackpackSize { get; set; } = 48;
}

/// <summary>GMCM's API surface — only the bits we use. Optional dependency, resolved at runtime.</summary>
public interface IGenericModConfigMenuApi
{
    void Register(IManifest mod, Action reset, Action save, bool titleScreenOnly = false);
    void AddNumberOption(IManifest mod, Func<int> getValue, Action<int> setValue, Func<string> name, Func<string>? tooltip = null, int? min = null, int? max = null, int? interval = null, Func<int, string>? formatValue = null, string? fieldId = null);
}

/// <summary>Expands the player's backpack past the vanilla 36-slot cap. Two layers, both Harmony-free:
/// (1) data — raise <c>Farmer.MaxItems</c> via the game's own <c>increaseBackpackSize</c> (pads Items
/// with empty slots, saved + synced; picking up items genuinely fills all 48). (2) UI — the vanilla
/// inventory menus hard-code 36 slots / 3 rows in <c>InventoryMenu.draw</c>, so on MenuChanged we swap
/// the main backpack page's InventoryMenu for a 4-row one (InventoryMenu itself draws any row count).</summary>
internal sealed class ModEntry : Mod
{
    private const int VanillaMax = 36;

    private ModConfig config = new();

    public override void Entry(IModHelper helper)
    {
        this.config = helper.ReadConfig<ModConfig>();
        helper.Events.GameLoop.SaveLoaded += (_, _) => this.ApplySize();
        helper.Events.GameLoop.DayStarted += (_, _) => this.ApplySize();
        helper.Events.Display.MenuChanged += this.OnMenuChanged;
        helper.Events.GameLoop.GameLaunched += this.OnGameLaunched;
    }

    /// <summary>Configured size, snapped to whole rows and clamped to [36, 48].</summary>
    private int Target()
    {
        return Math.Clamp((this.config.BackpackSize / 12) * 12, VanillaMax, 48);
    }

    /// <summary>Grow the backpack to the target via the game's own routine (idempotent, grow-only so
    /// items are never lost).</summary>
    private void ApplySize()
    {
        if (!Context.IsWorldReady || Game1.player is not Farmer player)
            return;

        int target = this.Target();
        if (player.MaxItems < target)
            player.increaseBackpackSize(target - player.MaxItems);
    }

    /// <summary>Swap the main backpack page's 3-row InventoryMenu for a taller one so all rows render.</summary>
    private void OnMenuChanged(object? sender, MenuChangedEventArgs e)
    {
        int target = this.Target();
        int rows = target / 12;
        if (rows <= 3 || e.NewMenu is null)
            return; // 36 or fewer: the vanilla UI already shows every slot

        // GameMenu holds the backpack page (and crafting page) in its pages list; the shop / chest /
        // standalone crafting menus carry the player backpack via MenuWithInventory.
        if (e.NewMenu is GameMenu gameMenu)
        {
            foreach (IClickableMenu page in gameMenu.pages)
                this.TryExpand(page, target, rows);
        }
        else
        {
            this.TryExpand(e.NewMenu, target, rows);
        }
    }

    /// <summary>Replace a menu's player-backpack InventoryMenu with a taller one so the extra rows
    /// render. Covers the backpack page (<see cref="InventoryPage.inventory"/>) and everything built on
    /// <see cref="MenuWithInventory"/> (shop / chest / crafting). The slot list is built in the ctor,
    /// so we swap the whole instance rather than bump capacity.</summary>
    private void TryExpand(IClickableMenu? menu, int target, int rows)
    {
        InventoryMenu? old = menu switch
        {
            InventoryPage page => page.inventory,
            MenuWithInventory withInventory => withInventory.inventory,
            _ => null
        };
        if (old is null || old.capacity >= target)
            return;

        // The extra rows grow downward and would overlap whatever sits below the backpack (money text,
        // trash can, crafting grid, divider line). Shift the menu up by the added height so it expands
        // into the space above instead, keeping everything below it clear.
        int yShift = (rows - 3) * 64;
        var rebuilt = new InventoryMenu(
            old.xPositionOnScreen,
            old.yPositionOnScreen - yShift,
            playerInventory: true,
            actualInventory: Game1.player.Items,
            highlightMethod: old.highlightMethod,
            capacity: target,
            rows: rows);

        switch (menu)
        {
            case InventoryPage page:
                page.inventory = rebuilt;
                break;
            case MenuWithInventory withInventory:
                withInventory.inventory = rebuilt;
                break;
        }
    }

    /// <summary>Wire up Generic Mod Config Menu if installed (optional).</summary>
    private void OnGameLaunched(object? sender, GameLaunchedEventArgs e)
    {
        var gmcm = this.Helper.ModRegistry.GetApi<IGenericModConfigMenuApi>("spacechase0.GenericModConfigMenu");
        if (gmcm is null)
            return;

        gmcm.Register(
            mod: this.ModManifest,
            reset: () => this.config = new ModConfig(),
            save: () => { this.Helper.WriteConfig(this.config); this.ApplySize(); });

        gmcm.AddNumberOption(
            this.ModManifest,
            getValue: () => this.config.BackpackSize,
            setValue: v => this.config.BackpackSize = v,
            name: () => "背包格子数",
            tooltip: () => "背包总格子数（每 12 格一行，36 或 48）。只增不减，存档后生效。",
            min: 36, max: 48, interval: 12);
    }
}
