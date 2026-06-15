using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.GameData.Buildings;
using StardewValley.Inventories;
using StardewValley.Locations;
using StardewValley.Menus;
using StardewValley.Objects;

namespace ChestHub;

/// <summary>Item-cost purchases draw from all your chests.
///
/// Mechanism (no Harmony): while a shop or construction menu is open, the mod tops up your
/// inventory JUST IN TIME from your chests — for the shop entry you're hovering (its trade-item
/// cost, e.g. metal bars for tool upgrades or omni geodes at the desert trader) and for the
/// building blueprint currently shown at Robin's (wood/stone/etc.). Vanilla's own checks then
/// pass naturally. When the menu closes, everything that wasn't consumed is returned to chests,
/// preferring chests that already hold the same item.</summary>
internal sealed class ChestPayFeature
{
    private readonly Dictionary<string, int> pulled = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> announced = new(StringComparer.OrdinalIgnoreCase);
    private bool sessionActive;
    private bool locationSession; // BoatTunnel-style: fronted on entry, returned on leaving
    private bool warnedNoSpace;
    private int tick;

    public ChestPayFeature(IModHelper helper)
    {
        helper.Events.Display.MenuChanged += this.OnMenuChanged;
        helper.Events.GameLoop.UpdateTicked += this.OnUpdateTicked;
        helper.Events.Player.Warped += this.OnWarped;
        helper.Events.GameLoop.Saving += (_, _) => this.EndSession(returnItems: true);
        helper.Events.GameLoop.ReturnedToTitle += (_, _) => this.EndSession(returnItems: Context.IsWorldReady);
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
            this.EndSession(returnItems: true);
        }
    }

    private void OnMenuChanged(object? sender, MenuChangedEventArgs e)
    {
        // While a location session (boat tunnel) owns the pulled pool, menu open/close must not
        // return the fronted materials mid-stay.
        if (this.locationSession)
            return;

        bool newRelevant = this.IsRelevantMenu(e.NewMenu);

        if (newRelevant && !this.sessionActive)
        {
            this.sessionActive = true;
            this.pulled.Clear();
            this.announced.Clear();
            this.warnedNoSpace = false;

            // Robin's HOUSE UPGRADE is a plain dialogue (not a CarpenterMenu): front the materials
            // as soon as her question dialogue opens, so answering "yes" just works.
            if (this.IsRobinHouseUpgradePrompt(e.NewMenu))
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
        else if (!newRelevant && this.sessionActive)
        {
            this.EndSession(returnItems: true);
        }
    }

    private void EndSession(bool returnItems)
    {
        if (returnItems && Context.IsWorldReady && this.pulled.Count > 0)
            this.ReturnLeftovers();
        else
        {
            this.pulled.Clear();
            this.announced.Clear();
        }

        this.sessionActive = false;
        this.locationSession = false;
        this.warnedNoSpace = false;
    }

    private bool IsRelevantMenu(IClickableMenu? menu)
    {
        return menu is ShopMenu or CarpenterMenu
            || this.IsRobinHouseUpgradePrompt(menu);
    }

    private bool IsRobinHouseUpgradePrompt(IClickableMenu? menu)
    {
        if (menu is not DialogueBox dialogue)
            return false;
        if (Game1.currentLocation?.Name != "ScienceHouse")
            return false;
        if (!string.Equals(Game1.currentSpeaker?.Name, "Robin", StringComparison.OrdinalIgnoreCase))
            return false;
        if (!PlayerCanBuyHouseUpgrade())
            return false;

        // Normal Robin chatter and her shop-choice dialogue shouldn't pull hundreds of materials
        // into the backpack. The actual house/community-upgrade confirmation is a yes/no prompt.
        return DialogueHasAffirmativeResponse(dialogue);
    }

    private static bool PlayerCanBuyHouseUpgrade()
    {
        if (Game1.player.HouseUpgradeLevel < 2)
            return true;

        return !Game1.MasterPlayer.mailReceived.Contains("pamHouseUpgrade");
    }

    private static bool DialogueHasAffirmativeResponse(DialogueBox dialogue)
    {
        foreach (object response in GetDialogueResponses(dialogue))
        {
            foreach (string text in GetResponseStrings(response))
            {
                if (IsAffirmativeResponse(text))
                    return true;
            }
        }

        return false;
    }

    private static IEnumerable<object> GetDialogueResponses(DialogueBox dialogue)
    {
        const System.Reflection.BindingFlags flags =
            System.Reflection.BindingFlags.Instance
            | System.Reflection.BindingFlags.Public
            | System.Reflection.BindingFlags.NonPublic;

        foreach (System.Reflection.FieldInfo field in dialogue.GetType().GetFields(flags))
        {
            if (!field.Name.Contains("response", StringComparison.OrdinalIgnoreCase))
                continue;
            object? rawValue = field.GetValue(dialogue);
            if (rawValue is string || rawValue is not System.Collections.IEnumerable values)
                continue;

            foreach (object? value in values)
            {
                if (value != null)
                    yield return value;
            }
        }
    }

    private static IEnumerable<string> GetResponseStrings(object response)
    {
        const System.Reflection.BindingFlags flags =
            System.Reflection.BindingFlags.Instance
            | System.Reflection.BindingFlags.Public
            | System.Reflection.BindingFlags.NonPublic;

        foreach (System.Reflection.FieldInfo field in response.GetType().GetFields(flags))
        {
            if (!LooksLikeResponseTextMember(field.Name))
                continue;
            if (field.GetValue(response) is string text)
                yield return text;
        }

        foreach (System.Reflection.PropertyInfo property in response.GetType().GetProperties(flags))
        {
            if (!LooksLikeResponseTextMember(property.Name) || property.GetIndexParameters().Length > 0)
                continue;

            string? text = null;
            try
            {
                text = property.GetValue(response) as string;
            }
            catch
            {
                // Some reflected properties can be stateful; ignore anything we can't read safely.
            }

            if (text != null)
                yield return text;
        }
    }

    private static bool LooksLikeResponseTextMember(string name)
    {
        return name.Contains("key", StringComparison.OrdinalIgnoreCase)
            || name.Contains("text", StringComparison.OrdinalIgnoreCase)
            || name.Contains("response", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAffirmativeResponse(string text)
    {
        string value = text.Trim();
        return value.Equals("Yes", StringComparison.OrdinalIgnoreCase)
            || value.Equals("OK", StringComparison.OrdinalIgnoreCase)
            || value.Equals("Accept", StringComparison.OrdinalIgnoreCase)
            || value.Equals("Confirm", StringComparison.OrdinalIgnoreCase)
            || value.Equals("是", StringComparison.OrdinalIgnoreCase)
            || value.Equals("好的", StringComparison.OrdinalIgnoreCase)
            || value.Equals("确定", StringComparison.OrdinalIgnoreCase);
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
        if (hovered == null || !shop.itemPriceAndStock.TryGetValue(hovered, out ItemStockInformation? stock) || stock == null)
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
        bool inventoryFull = false;

        foreach (Chest chest in GetAllChests())
        {
            if (deficit <= 0 || inventoryFull)
                break;

            IInventory items = chest.GetItemsForPlayer();
            int available = items.CountId(itemId);
            if (available <= 0)
                continue;

            // Move in batches no bigger than the item's max stack size, so counts above 999 don't
            // get silently truncated when the stack is created. Only reduce the chest by what the
            // backpack actually accepted, so nothing is ever lost or duplicated.
            int take = Math.Min(available, deficit);
            while (take > 0)
            {
                Item? item = ItemRegistry.Create(itemId, 1, allowNull: true);
                if (item == null)
                    break; // unknown item id: leave the chest untouched rather than spawning an Error Item

                int batch = Math.Min(take, item.maximumStackSize());
                item.Stack = batch;

                Item? leftover = Game1.player.addItemToInventory(item);
                int moved = batch - (leftover?.Stack ?? 0);
                if (moved > 0)
                {
                    items.ReduceId(itemId, moved);
                    deficit -= moved;
                    movedTotal += moved;
                    take -= moved;
                }

                if (leftover != null)
                {
                    // Inventory is full — the unaccepted part was never taken from the chest.
                    inventoryFull = true;
                    if (!this.warnedNoSpace)
                    {
                        this.warnedNoSpace = true;
                        Game1.addHUDMessage(HUDMessage.ForCornerTextbox("背包放不下更多代付材料了，请清出一格。"));
                    }
                    break;
                }
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
            int give = Math.Min(amount, Game1.player.Items.CountId(itemId));

            // Hand back in batches no bigger than the max stack size so counts above 999 aren't
            // truncated when the stack is created. The player stack is reduced only by what was
            // actually moved into a chest (or kept in the backpack), so nothing is lost.
            while (give > 0)
            {
                Item? remaining = ItemRegistry.Create(itemId, 1, allowNull: true);
                if (remaining == null)
                    break; // unknown item id: leave it in the backpack rather than losing it

                int batch = Math.Min(give, remaining.maximumStackSize());
                remaining.Stack = batch;
                Game1.player.Items.ReduceId(itemId, batch);
                give -= batch;

                // Chests already holding the item first, then any chest.
                foreach (Chest chest in GetAllChests().OrderByDescending(c => c.GetItemsForPlayer().CountId(itemId) > 0))
                {
                    remaining = chest.addItem(remaining);
                    if (remaining == null)
                        break;
                }

                // Every chest full? Keep it in the player's inventory rather than losing it.
                if (remaining != null)
                    Game1.player.addItemToInventory(remaining);
            }
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
