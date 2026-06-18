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

/// <summary>Scrollable, category-grouped list of everything the player owns. Each item shows its
/// icon, name and total under a header for its category.</summary>
internal sealed class StockMenu : IClickableMenu
{
    private const int Columns = 3;     // item cards per row
    private const int CardW = 232;     // width of one icon+name card
    private const int CardH = 64;      // icon size and card height
    private const int RowH = CardH + 12;
    private const int HeaderH = 44;
    private const int TopPad = 88;     // space above the list (title)
    private const int BotPad = 72;     // space below the list (hint)

    private enum RowKind { Header, Items }

    /// <summary>One drawn line: either a category header or a row of up to <see cref="Columns"/> item cards.</summary>
    private sealed class Row
    {
        public RowKind Kind;
        public int Height;
        public string Title = "";
        public Color TitleColor;
        public StockEntry[] Items = Array.Empty<StockEntry>();
    }

    private readonly List<Row> rows;
    private readonly int itemCount;
    private readonly int visibleH;
    private readonly int maxTopRow;
    private int topRow;
    private string hoverText = "";

    private int GridX => this.xPositionOnScreen + 32;
    private int GridY => this.yPositionOnScreen + TopPad;

    public StockMenu(List<StockEntry> entries)
        : base(0, 0, Columns * CardW + 64, 0, showUpperRightCloseButton: true)
    {
        this.itemCount = entries.Count;
        this.rows = BuildRows(entries);

        // Fit ~7 item rows, but never spill past the screen on small resolutions.
        this.visibleH = Math.Min(7 * RowH, Math.Max(3 * RowH, Game1.uiViewport.Height - 220));
        this.height = this.visibleH + TopPad + BotPad;

        this.xPositionOnScreen = (Game1.uiViewport.Width - this.width) / 2;
        this.yPositionOnScreen = (Game1.uiViewport.Height - this.height) / 2;
        this.initializeUpperRightCloseButton();

        this.maxTopRow = this.ComputeMaxTopRow();
    }

    /// <summary>Group the (already category-sorted) entries and lay each group out as a header row
    /// followed by item rows.</summary>
    private static List<Row> BuildRows(List<StockEntry> entries)
    {
        var rows = new List<Row>();
        foreach (var group in entries.GroupBy(GroupName))
        {
            rows.Add(new Row
            {
                Kind = RowKind.Header,
                Height = HeaderH,
                Title = group.Key,
                TitleColor = HeaderColor(group.First().Sample.getCategoryColor()),
            });

            var items = group.ToList();
            for (int i = 0; i < items.Count; i += Columns)
            {
                int n = Math.Min(Columns, items.Count - i);
                var cards = new StockEntry[n];
                items.CopyTo(i, cards, 0, n);
                rows.Add(new Row { Kind = RowKind.Items, Height = RowH, Items = cards });
            }
        }
        return rows;
    }

    private static string GroupName(StockEntry entry)
    {
        string name = entry.Sample.getCategoryName();
        return string.IsNullOrWhiteSpace(name) ? "其他" : name;
    }

    /// <summary>Category colors can be near-white (unreadable on the parchment box); clamp those to gray.</summary>
    private static Color HeaderColor(Color c)
        => c.R + c.G + c.B > 620 ? Color.Gray : c;

    /// <summary>Highest scroll position that still fills the viewport from the bottom.</summary>
    private int ComputeMaxTopRow()
    {
        int sum = 0;
        for (int i = this.rows.Count - 1; i >= 0; i--)
        {
            sum += this.rows[i].Height;
            if (sum > this.visibleH)
                return i + 1;
        }
        return 0;
    }

    public override void receiveScrollWheelAction(int direction)
    {
        this.topRow = Math.Clamp(this.topRow - Math.Sign(direction), 0, this.maxTopRow);
    }

    /// <summary>Visit each row visible from the current scroll position with its top Y, stopping
    /// before any row that would overflow the viewport (so nothing draws past the box).</summary>
    private void ForEachVisibleRow(Action<Row, int> onRow)
    {
        int y = this.GridY;
        int bottom = this.GridY + this.visibleH;
        for (int i = this.topRow; i < this.rows.Count; i++)
        {
            Row row = this.rows[i];
            if (y + row.Height > bottom)
                break;
            onRow(row, y);
            y += row.Height;
        }
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

    private StockEntry? EntryAt(int mx, int my)
    {
        StockEntry? found = null;
        this.ForEachVisibleRow((row, y) =>
        {
            if (found != null || row.Kind != RowKind.Items)
                return;
            for (int c = 0; c < row.Items.Length; c++)
            {
                if (new Rectangle(this.GridX + c * CardW, y, CardW, CardH).Contains(mx, my))
                {
                    found = row.Items[c];
                    return;
                }
            }
        });
        return found;
    }

    public override void draw(SpriteBatch b)
    {
        // Dim the world, then the menu box.
        b.Draw(Game1.fadeToBlackRect, Game1.graphics.GraphicsDevice.Viewport.Bounds, Color.Black * 0.5f);
        Game1.drawDialogueBox(this.xPositionOnScreen, this.yPositionOnScreen, this.width, this.height, speaker: false, drawOnlyBox: true);

        string title = $"物品总览（{this.itemCount} 种）";
        SpriteText.drawStringWithScrollCenteredAt(b, title, this.xPositionOnScreen + this.width / 2, this.yPositionOnScreen + 4);

        this.ForEachVisibleRow((row, y) =>
        {
            if (row.Kind == RowKind.Header)
                this.DrawHeader(b, row, y);
            else
                this.DrawItems(b, row, y);
        });

        if (this.maxTopRow > 0)
        {
            const string hint = "滚轮翻页";
            Vector2 size = Game1.smallFont.MeasureString(hint);
            Utility.drawTextWithShadow(b, hint, Game1.smallFont,
                new Vector2(this.xPositionOnScreen + this.width - size.X - 48, this.yPositionOnScreen + this.height - 56), Game1.textColor);
        }

        base.draw(b); // close button

        if (!string.IsNullOrEmpty(this.hoverText))
            drawHoverText(b, this.hoverText, Game1.smallFont);

        this.drawMouse(b);
    }

    private void DrawHeader(SpriteBatch b, Row row, int y)
    {
        // Colored bar + category name.
        b.Draw(Game1.staminaRect, new Rectangle(this.GridX, y + 10, 8, HeaderH - 16), row.TitleColor);
        Utility.drawTextWithShadow(b, row.Title, Game1.smallFont, new Vector2(this.GridX + 20, y + 8), Game1.textColor);
    }

    private void DrawItems(SpriteBatch b, Row row, int y)
    {
        for (int c = 0; c < row.Items.Length; c++)
        {
            StockEntry entry = row.Items[c];
            int x = this.GridX + c * CardW;

            // Icon slot + icon (we draw the total ourselves, so suppress the built-in stack number).
            var iconPos = new Vector2(x, y);
            b.Draw(Game1.menuTexture, iconPos, Game1.getSourceRectForStandardTileSheet(Game1.menuTexture, 10), Color.White * 0.6f);
            entry.Sample.Stack = 1;
            entry.Sample.drawInMenu(b, iconPos, 1f, 1f, 0.9f, StackDrawType.Hide, Color.White, drawShadow: false);

            // Name + total, to the right of the icon.
            int textX = x + CardH + 10;
            float textW = CardW - CardH - 24;
            string name = Truncate(entry.Sample.DisplayName, Game1.smallFont, textW);
            Utility.drawTextWithShadow(b, name, Game1.smallFont, new Vector2(textX, y + 8), Game1.textColor);
            Utility.drawTextWithShadow(b, "×" + entry.Total, Game1.smallFont, new Vector2(textX, y + 36), Game1.textColor * 0.85f);
        }
    }

    /// <summary>Trim text with an ellipsis so it fits within <paramref name="maxWidth"/>.</summary>
    private static string Truncate(string text, SpriteFont font, float maxWidth)
    {
        if (font.MeasureString(text).X <= maxWidth)
            return text;

        var sb = new System.Text.StringBuilder();
        foreach (char ch in text)
        {
            if (font.MeasureString(sb.ToString() + ch + "…").X > maxWidth)
                break;
            sb.Append(ch);
        }
        return sb.Append('…').ToString();
    }
}

/// <summary>Reference-equality comparer for the inventory dedupe set.</summary>
internal sealed class ObjectReferenceComparer : IEqualityComparer<object>
{
    public static readonly ObjectReferenceComparer Instance = new();

    bool IEqualityComparer<object>.Equals(object? x, object? y) => ReferenceEquals(x, y);

    int IEqualityComparer<object>.GetHashCode(object obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
}
