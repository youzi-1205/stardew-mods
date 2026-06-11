using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.GameData.Buildings;
using StardewValley.Locations;
using StardewValley.Menus;
using StardewValley.Objects;

namespace ChestPay;

/// <summary>Item-cost purchases draw from all your chests.
///
/// Mechanism (no Harmony): while a shop or construction menu is open, the mod tops up your
/// inventory JUST IN TIME from your chests — for the shop entry you're hovering (its trade-item
/// cost, e.g. metal bars for tool upgrades or omni geodes at the desert trader) and for the
/// building blueprint currently shown at Robin's (wood/stone/etc.). Vanilla's own checks then
/// pass naturally. When the menu closes, everything that wasn't consumed is returned to chests,
/// preferring chests that already hold the same item.</summary>
internal sealed class ModEntry : Mod
{
    private readonly Dictionary<string, int> pulled = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> announced = new(StringComparer.OrdinalIgnoreCase);
    private bool sessionActive;
    private bool locationSession; // BoatTunnel-style: fronted on entry, returned on leaving
    private bool warnedNoSpace;
    private int tick;

    public override void Entry(IModHelper helper)
    {
        helper.Events.Display.MenuChanged += this.OnMenuChanged;
        helper.Events.GameLoop.UpdateTicked += this.OnUpdateTicked;
        helper.Events.Player.Warped += this.OnWarped;
        helper.Events.GameLoop.ReturnedToTitle += (_, _) =>
        {
            this.pulled.Clear();
            this.announced.Clear();
            this.sessionActive = false;
            this.locationSession = false;
        };
    }

    /// <summary>Willy's boat repair checks your items the instant you CLICK a boat part (before any
    /// dialogue), so front the materials when entering the boat tunnel and return them on leaving.</summary>
    private void OnWarped(object? sender, WarpedEventArgs e)
    {
        if (!e.IsLocalPlayer)
            return;

        if (e.NewLocation?.Name == "BoatTunnel")
        {
            this.locationSession = true;
            this.sessionActive = false;
            this.pulled.Clear();
            this.announced.Clear();
            this.warnedNoSpace = false;

            if (!Game1.MasterPlayer.hasOrWillReceiveMail("willyBoatFixed"))
            {
                if (!Game1.MasterPlayer.hasOrWillReceiveMail("willyBoatHull"))
                    this.EnsureInInventory("(O)709", 200); // hardwood
                if (!Game1.MasterPlayer.hasOrWillReceiveMail("willyBoatAnchor"))
                    this.EnsureInInventory("(O)337", 5);   // iridium bars
                if (!Game1.MasterPlayer.hasOrWillReceiveMail("willyBoatTicketMachine"))
                    this.EnsureInInventory("(O)787", 5);   // battery packs
            }
        }
        else if (this.locationSession)
        {
            this.ReturnLeftovers();
            this.locationSession = false;
        }
    }

    private void OnMenuChanged(object? sender, MenuChangedEventArgs e)
    {
        // While a location session (boat tunnel) owns the pulled pool, menu open/close must not
        // return the fronted materials mid-stay.
        if (this.locationSession)
            return;

        bool newRelevant = IsRelevantMenu(e.NewMenu);
        bool oldRelevant = IsRelevantMenu(e.OldMenu);

        if (newRelevant && !this.sessionActive)
        {
            this.sessionActive = true;
            this.pulled.Clear();
            this.announced.Clear();
            this.warnedNoSpace = false;

            // Robin's HOUSE UPGRADE is a plain dialogue (not a CarpenterMenu): front the materials
            // as soon as her question dialogue opens, so answering "yes" just works.
            if (e.NewMenu is DialogueBox && Game1.currentLocation?.Name == "ScienceHouse")
            {
                switch (Game1.player.HouseUpgradeLevel)
                {
                    case 0:
                        this.EnsureInInventory("(O)388", 450); // wood
                        break;
                    case 1:
                        this.EnsureInInventory("(O)709", 100); // hardwood
                        break;
                    default:
                        // Community upgrade (Pam's house) needs 950 wood.
                        if (!Game1.MasterPlayer.mailReceived.Contains("pamHouseUpgrade"))
                            this.EnsureInInventory("(O)388", 950);
                        break;
                }
            }
        }
        else if (!newRelevant && oldRelevant && this.sessionActive)
        {
            this.ReturnLeftovers();
            this.sessionActive = false;
        }
    }

    private static bool IsRelevantMenu(IClickableMenu? menu)
    {
        return menu is ShopMenu or CarpenterMenu
            || (menu is DialogueBox && Game1.currentLocation?.Name == "ScienceHouse");
    }

    private void OnUpdateTicked(object? sender, UpdateTickedEventArgs e)
    {
        if (!this.sessionActive || !Context.IsWorldReady || ++this.tick % 10 != 0)
            return;

        switch (Game1.activeClickableMenu)
        {
            case ShopMenu shop:
                this.TopUpForShopHover(shop);
                break;
            case CarpenterMenu carpenter:
                this.TopUpForBlueprint(carpenter);
                break;
        }
    }

    // ── just-in-time top-up ───────────────────────────────────────────────────

    private void TopUpForShopHover(ShopMenu shop)
    {
        ISalable? hovered = shop.hoveredItem;
        if (hovered == null || !shop.itemPriceAndStock.TryGetValue(hovered, out ItemStockInformation stock))
            return;

        string? tradeItem = stock.TradeItem;
        if (string.IsNullOrEmpty(tradeItem))
            return;

        // Stock 5 purchases' worth so shift-click batch buying works too (leftovers go back).
        int perPurchase = stock.TradeItemCount ?? 5; // vanilla's default trade count
        this.EnsureInInventory(tradeItem, perPurchase * 5);
    }

    private void TopUpForBlueprint(CarpenterMenu menu)
    {
        CarpenterMenu.BlueprintEntry? blueprint = menu.Blueprint;
        if (blueprint == null)
            return;

        foreach (BuildingMaterial material in blueprint.BuildMaterials)
            this.EnsureInInventory(material.ItemId, material.Amount);
    }

    private void EnsureInInventory(string itemId, int needed)
    {
        int have = Game1.player.Items.CountId(itemId);
        if (have >= needed)
            return;

        int deficit = needed - have;
        int movedTotal = 0;

        foreach (Chest chest in GetAllChests())
        {
            if (deficit <= 0)
                break;

            int available = chest.Items.CountId(itemId);
            if (available <= 0)
                continue;

            int take = Math.Min(available, deficit);
            chest.Items.ReduceId(itemId, take);

            Item? item = ItemRegistry.Create(itemId, take, allowNull: true);
            if (item == null)
            {
                chest.addItem(ItemRegistry.Create(itemId, take));
                break;
            }

            Item? leftover = Game1.player.addItemToInventory(item);
            int moved = take - (leftover?.Stack ?? 0);
            deficit -= moved;
            movedTotal += moved;

            if (leftover != null)
            {
                // Inventory is full — put the rest back where it came from.
                chest.addItem(leftover);
                if (!this.warnedNoSpace)
                {
                    this.warnedNoSpace = true;
                    Game1.addHUDMessage(HUDMessage.ForCornerTextbox("背包放不下更多代付材料了，请清出一格。"));
                }
                break;
            }
        }

        if (movedTotal > 0)
        {
            this.pulled[itemId] = this.pulled.GetValueOrDefault(itemId) + movedTotal;

            if (this.announced.Add(itemId))
            {
                string name = ItemRegistry.GetDataOrErrorItem(itemId).DisplayName;
                Game1.addHUDMessage(HUDMessage.ForCornerTextbox($"已从箱子垫付 {name} ×{movedTotal}（多余的关店后自动放回）"));
            }
        }
    }

    // ── return what wasn't consumed ───────────────────────────────────────────

    private void ReturnLeftovers()
    {
        foreach ((string itemId, int amount) in this.pulled)
        {
            int have = Game1.player.Items.CountId(itemId);
            int give = Math.Min(amount, have);
            if (give <= 0)
                continue;

            Game1.player.Items.ReduceId(itemId, give);
            Item? remaining = ItemRegistry.Create(itemId, give, allowNull: true);
            if (remaining == null)
                continue;

            // Chests already holding the item first, then any chest.
            foreach (Chest chest in GetAllChests().OrderByDescending(c => c.Items.CountId(itemId) > 0))
            {
                remaining = chest.addItem(remaining);
                if (remaining == null)
                    break;
            }

            // Every chest full? Keep it in the player's inventory rather than losing it.
            if (remaining != null)
                Game1.player.addItemToInventory(remaining);
        }

        this.pulled.Clear();
        this.announced.Clear();
    }

    private static IEnumerable<Chest> GetAllChests()
    {
        var chests = new List<Chest>();
        Utility.ForEachLocation(location =>
        {
            foreach (StardewValley.Object obj in location.objects.Values)
            {
                if (obj is Chest chest && chest.playerChest.Value
                    && chest.SpecialChestType is Chest.SpecialChestTypes.None or Chest.SpecialChestTypes.BigChest)
                {
                    chests.Add(chest);
                }
            }
            return true;
        }, includeInteriors: true);
        return chests;
    }
}
