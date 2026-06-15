using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.BellsAndWhistles;
using StardewValley.Inventories;
using StardewValley.Locations;
using StardewValley.Menus;
using StardewValley.Objects;

namespace ChestHub;

internal sealed class StockOverviewFeature
{
    private readonly IModHelper helper;
    private readonly Func<ModConfig> config;

    public StockOverviewFeature(IModHelper helper, Func<ModConfig> config)
    {
        this.helper = helper;
        this.config = config;
        helper.Events.Input.ButtonPressed += this.OnButtonPressed;
    }

    private void OnButtonPressed(object? sender, ButtonPressedEventArgs e)
    {
        if (!Context.IsWorldReady)
            return;
        if (!Enum.TryParse(this.config().StockKey, ignoreCase: true, out SButton openKey) || e.Button != openKey)
            return;

        if (Game1.activeClickableMenu is StockMenu)
        {
            Game1.exitActiveMenu();
            this.helper.Input.Suppress(e.Button);
        }
        else if (Context.IsPlayerFree && Game1.activeClickableMenu == null
            && !Game1.IsChatting && Game1.keyboardDispatcher.Subscriber == null)
        {
            Game1.activeClickableMenu = new StockMenu(StockAggregator.Collect());
            Game1.playSound("bigSelect");
            this.helper.Input.Suppress(e.Button);
        }
    }
}

/// <summary>One aggregated row: a sample item for drawing, the grand total, and where it lives.</summary>
internal sealed class StockEntry
{
    public StockEntry(Item sample) => this.Sample = sample;

    public Item Sample { get; }
    public int Total;
    public readonly Dictionary<string, int> Breakdown = new();
}

internal static class StockAggregator
{
    /// <summary>Sum every item the player owns: backpack, all player chests everywhere
    /// (mini-fridges included), kitchen fridges, and shared inventories (Junimo chests, truck
    /// cargo). Duplicate inventory references are counted once.</summary>
    public static List<StockEntry> Collect()
    {
        var entries = new Dictionary<string, StockEntry>(StringComparer.OrdinalIgnoreCase);
        var seenInventories = new HashSet<object>(ObjectReferenceComparer.Instance);

        void AddItem(Item? item, string place)
        {
            if (item == null)
                return;

            string key = item.QualifiedItemId;
            if (!entries.TryGetValue(key, out StockEntry? entry))
            {
                entry = new StockEntry(item.getOne());
                entries[key] = entry;
            }

            entry.Total += item.Stack;
            entry.Breakdown.TryGetValue(place, out int count);
            entry.Breakdown[place] = count + item.Stack;
        }

        void AddInventory(IInventory? inventory, string place)
        {
            if (inventory == null || !seenInventories.Add(inventory))
                return;
            foreach (Item? item in inventory)
                AddItem(item, place);
        }

        // Backpack.
        foreach (Item? item in Game1.player.Items)
            AddItem(item, "背包");

        // Chests everywhere (player chests; GetItemsForPlayer routes Junimo chests to the shared
        // team inventory, which the seen-set dedupes).
        Utility.ForEachLocation(location =>
        {
            string place = GetPlaceName(location);
            foreach (StardewValley.Object obj in location.objects.Values)
            {
                if (obj is Chest chest && chest.playerChest.Value)
                    AddInventory(chest.GetItemsForPlayer(), chest.SpecialChestType == Chest.SpecialChestTypes.JunimoChest ? "祝尼魔箱子" : place);
            }

            switch (location)
            {
                case FarmHouse farmHouse when farmHouse.fridge.Value != null:
                    AddInventory(farmHouse.fridge.Value.Items, "冰箱");
                    break;
                case IslandFarmHouse islandHouse when islandHouse.fridge.Value != null:
                    AddInventory(islandHouse.fridge.Value.Items, "冰箱");
                    break;
            }

            return true;
        }, includeInteriors: true);

        // Shared team inventories not reachable via a placed chest (e.g. the truck cargo).
        foreach (var pair in Game1.player.team.globalInventories.Pairs)
        {
            string place = pair.Key switch
            {
                "JunimoChests" => "祝尼魔箱子",
                "Codex.FarmSuite/TruckCargo" => "皮卡货箱",
                _ => pair.Key,
            };
            AddInventory(pair.Value, place);
        }

        return entries.Values
            .OrderBy(entry => entry.Sample.Category)
            .ThenBy(entry => entry.Sample.DisplayName, StringComparer.CurrentCulture)
            .ToList();
    }

    private static string GetPlaceName(GameLocation location)
    {
        string? name = location.DisplayName;
        return string.IsNullOrWhiteSpace(name) ? location.Name : name;
    }
}

/// <summary>Scrollable grid of everything the player owns.</summary>
internal sealed class StockMenu : IClickableMenu
{
    private const int Columns = 12;
    private const int VisibleRows = 7;
    private const int Cell = 68;

    private readonly List<StockEntry> entries;
    private int topRow;
    private string hoverText = "";

    private int GridX => this.xPositionOnScreen + 32;
    private int GridY => this.yPositionOnScreen + 96;
    private int MaxTopRow => Math.Max(0, (this.entries.Count + Columns - 1) / Columns - VisibleRows);

    public StockMenu(List<StockEntry> entries)
        : base(0, 0, Columns * Cell + 64, VisibleRows * Cell + 160, showUpperRightCloseButton: true)
    {
        this.entries = entries;
        this.xPositionOnScreen = (Game1.uiViewport.Width - this.width) / 2;
        this.yPositionOnScreen = (Game1.uiViewport.Height - this.height) / 2;
        this.initializeUpperRightCloseButton();
    }

    public override void receiveScrollWheelAction(int direction)
    {
        this.topRow = Math.Clamp(this.topRow - Math.Sign(direction), 0, this.MaxTopRow);
    }

    public override void performHoverAction(int x, int y)
    {
        base.performHoverAction(x, y);
        this.hoverText = "";

        StockEntry? entry = this.EntryAt(x, y);
        if (entry == null)
            return;

        var lines = new System.Text.StringBuilder();
        lines.Append(entry.Sample.DisplayName).Append(" ×").Append(entry.Total);
        foreach (var pair in entry.Breakdown.OrderByDescending(p => p.Value))
            lines.Append('\n').Append(pair.Key).Append("：").Append(pair.Value);
        this.hoverText = lines.ToString();
    }

    private StockEntry? EntryAt(int x, int y)
    {
        int col = (x - this.GridX) / Cell;
        int row = (y - this.GridY) / Cell;
        if (col < 0 || col >= Columns || row < 0 || row >= VisibleRows)
            return null;
        if (x < this.GridX || y < this.GridY)
            return null;

        int index = (this.topRow + row) * Columns + col;
        return index >= 0 && index < this.entries.Count ? this.entries[index] : null;
    }

    public override void draw(SpriteBatch b)
    {
        // Dim the world, then the menu box.
        b.Draw(Game1.fadeToBlackRect, Game1.graphics.GraphicsDevice.Viewport.Bounds, Color.Black * 0.5f);
        Game1.drawDialogueBox(this.xPositionOnScreen, this.yPositionOnScreen, this.width, this.height, speaker: false, drawOnlyBox: true);

        // Title + page info.
        int totalRows = Math.Max(1, (this.entries.Count + Columns - 1) / Columns);
        string title = $"物品总览（{this.entries.Count} 种）";
        SpriteText.drawStringWithScrollCenteredAt(b, title, this.xPositionOnScreen + this.width / 2, this.yPositionOnScreen + 4);
        string pageInfo = $"滚轮翻页  {Math.Min(this.topRow + VisibleRows, totalRows)}/{totalRows} 行";
        Vector2 pageSize = Game1.smallFont.MeasureString(pageInfo);
        Utility.drawTextWithShadow(b, pageInfo, Game1.smallFont,
            new Vector2(this.xPositionOnScreen + this.width - pageSize.X - 48, this.yPositionOnScreen + this.height - 56), Game1.textColor);

        // Item grid.
        for (int row = 0; row < VisibleRows; row++)
        {
            for (int col = 0; col < Columns; col++)
            {
                int index = (this.topRow + row) * Columns + col;
                if (index >= this.entries.Count)
                    break;

                StockEntry entry = this.entries[index];
                var position = new Vector2(this.GridX + col * Cell, this.GridY + row * Cell);

                b.Draw(Game1.menuTexture, position, Game1.getSourceRectForStandardTileSheet(Game1.menuTexture, 10), Color.White * 0.6f);
                entry.Sample.Stack = entry.Total;
                entry.Sample.drawInMenu(b, position, 1f, 1f, 0.9f, StackDrawType.Draw, Color.White, drawShadow: false);
            }
        }

        base.draw(b); // close button

        if (!string.IsNullOrEmpty(this.hoverText))
            drawHoverText(b, this.hoverText, Game1.smallFont);

        this.drawMouse(b);
    }
}

/// <summary>Reference-equality comparer for the inventory dedupe set.</summary>
internal sealed class ObjectReferenceComparer : IEqualityComparer<object>
{
    public static readonly ObjectReferenceComparer Instance = new();

    bool IEqualityComparer<object>.Equals(object? x, object? y) => ReferenceEquals(x, y);

    int IEqualityComparer<object>.GetHashCode(object obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
}
