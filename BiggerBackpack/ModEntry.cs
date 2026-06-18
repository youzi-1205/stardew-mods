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
/// inventory menus hard-code 36 slots / 3 rows, so on MenuChanged we give the backpack a taller
/// InventoryMenu (which itself draws any row count). The full GameMenu inventory tab needs more than a
/// taller grid — see <see cref="ExpandInventoryPage"/> — because its divider and lower panel are pinned
/// just below the third row; the chest / shipping-bin host (MenuWithInventory) just needs the taller
/// grid centred in its box — see <see cref="TryExpand"/>.</summary>
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

    /// <summary>When a menu opens, grow the player backpack it hosts so every row renders.</summary>
    private void OnMenuChanged(object? sender, MenuChangedEventArgs e)
    {
        if (!Context.IsWorldReady || Game1.player is null)
            return;

        int target = this.Target();
        int rows = target / 12;
        if (rows <= 3 || e.NewMenu is null)
            return; // 36 or fewer: the vanilla UI already shows every slot

        // The full-screen GameMenu inventory tab needs a rebuilt page (its divider/equipment/money are
        // pinned right below row 3); every other backpack host carries it via MenuWithInventory and only
        // needs a taller grid.
        if (e.NewMenu is GameMenu gameMenu)
        {
            for (int i = 0; i < gameMenu.pages.Count; i++)
            {
                if (gameMenu.pages[i] is InventoryPage)
                    this.ExpandInventoryPage(gameMenu, i, target, rows);
            }
        }
        else
        {
            this.TryExpand(e.NewMenu, target, rows);
        }
    }

    /// <summary>Rebuild the GameMenu's InventoryPage so the backpack shows <paramref name="rows"/> rows.
    ///
    /// Why a rebuild and not just a taller InventoryMenu: vanilla InventoryPage hard-codes its horizontal
    /// divider, equipment slots, portrait and money text at <c>yPositionOnScreen + 192…448</c>, leaving
    /// room for exactly three inventory rows between the top border and the divider. Swapping in a 4-row
    /// grid and nudging it up (the old approach) shoved the top row out over the window's wooden border,
    /// because nothing made vertical room for it.
    ///
    /// Instead we reconstruct the page one row lower, so the divider and everything under it slide down a
    /// row's worth — InventoryPage positions all of its own parts (draws AND click-boxes) from its
    /// yPositionOnScreen, so a fresh instance stays perfectly self-consistent. We also make it a row
    /// taller, which makes GameMenu draw a matching taller frame (GameMenu.draw sizes the box from
    /// <c>pages[currentTab].height</c>, so the change is per-tab and leaves the other tabs alone). Finally
    /// we drop the real 4-row backpack back at the original top-of-frame position; the extra row now lands
    /// in genuine panel space with the beige box behind it.</summary>
    private void ExpandInventoryPage(GameMenu gameMenu, int pageIndex, int target, int rows)
    {
        var page = (InventoryPage)gameMenu.pages[pageIndex];
        if (page.inventory.capacity >= target)
            return; // already expanded

        int extra = (rows - 3) * 64;
        int frameTop = gameMenu.yPositionOnScreen;
        int invLeft = gameMenu.xPositionOnScreen + IClickableMenu.spaceToClearSideBorder + IClickableMenu.borderWidth;
        int invTop = frameTop + IClickableMenu.spaceToClearTopBorder + IClickableMenu.borderWidth;

        // The page (divider + everything below) starts one row lower; its height grows to match so the
        // frame GameMenu draws around it is a row taller.
        var rebuilt = new InventoryPage(gameMenu.xPositionOnScreen, frameTop + extra, page.width, page.height + extra);

        // Put the real backpack back at the original top of the frame, growing down into the freed row.
        rebuilt.inventory = new InventoryMenu(
            invLeft,
            invTop,
            playerInventory: true,
            actualInventory: Game1.player.Items,
            highlightMethod: rebuilt.inventory.highlightMethod,
            capacity: target,
            rows: rows);

        gameMenu.pages[pageIndex] = rebuilt;

        if (gameMenu.currentTab == pageIndex)
        {
            rebuilt.populateClickableComponentList();
            gameMenu.AddTabsToClickableComponents(rebuilt);
            if (Game1.options.SnappyMenus)
                gameMenu.snapToDefaultClickableComponent();
        }
    }

    /// <summary>Replace a <see cref="MenuWithInventory"/> host's player backpack (the chest / shipping-bin
    /// grid) with a taller one. The slot list is
    /// built in the ctor, so we swap the whole instance rather than bump capacity.
    ///
    /// ItemGrabMenu's lower backpack panel is sized from the host menu height. Growing the grid without
    /// growing that panel leaves the extra row outside the beige background (or pushed into the overlap
    /// between the chest and backpack panels). So chest-style hosts grow one row taller and keep the
    /// backpack on its vanilla anchor; other hosts keep their original centre.</summary>
    private void TryExpand(IClickableMenu? menu, int target, int rows)
    {
        if (menu is not MenuWithInventory withInventory)
            return;

        InventoryMenu old = withInventory.inventory;
        if (old is null || old.capacity >= target)
            return;

        int newTop;
        if (menu is ItemGrabMenu)
        {
            int extraHeight = (rows - 3) * 64;
            menu.height += extraHeight;
            this.MoveMenuButtonsDown(withInventory, extraHeight);
            newTop = old.yPositionOnScreen;
        }
        else
        {
            // Any other host: keep the grid centred on its original centre (grow half a row each way).
            newTop = old.yPositionOnScreen - (rows - 3) * 32;
        }

        var rebuilt = new InventoryMenu(
            old.xPositionOnScreen,
            newTop,
            playerInventory: old.playerInventory,
            actualInventory: Game1.player.Items,
            highlightMethod: old.highlightMethod,
            capacity: target,
            rows: rows);
        rebuilt.moveItemSound = old.moveItemSound;
        withInventory.inventory = rebuilt;
    }

    /// <summary>Keep MenuWithInventory's side buttons attached to the bottom after increasing the panel height.</summary>
    private void MoveMenuButtonsDown(MenuWithInventory menu, int delta)
    {
        if (delta == 0)
            return;

        if (menu.okButton != null)
            menu.okButton.bounds.Y += delta;

        if (menu.trashCan != null)
            menu.trashCan.bounds.Y += delta;
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
