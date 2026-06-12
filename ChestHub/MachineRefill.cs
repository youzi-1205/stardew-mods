using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Inventories;
using StardewValley.Menus;
using StardewValley.Objects;

namespace ChestHub;

/// <summary>Feature: a "一键补货" button floating over the nearest EMPTY machine. Clicking it
/// loads the machine from your chests using the game's own hopper auto-load mechanism
/// (Object.AttemptAutoLoad), which matches the recipe, consumes fuel like coal, and starts the
/// machine — exactly as if you had inserted the items yourself.</summary>
internal sealed class MachineRefill
{
    private readonly IModHelper helper;
    private readonly IMonitor monitor;
    private readonly Func<ModConfig> config;

    private bool buttonVisible;
    private Rectangle buttonBounds; // absolute world pixels
    private Point machineTile;

    public MachineRefill(IModHelper helper, IMonitor monitor, Func<ModConfig> config)
    {
        this.helper = helper;
        this.monitor = monitor;
        this.config = config;

        helper.Events.GameLoop.UpdateTicked += this.OnUpdateTicked;
        helper.Events.Display.RenderedWorld += this.OnRenderedWorld;
        helper.Events.Input.ButtonPressed += this.OnButtonPressed;
    }

    private void OnUpdateTicked(object? sender, UpdateTickedEventArgs e)
    {
        this.buttonVisible = false;

        if (!this.config().MachineRefillButton || !Context.IsWorldReady || !Context.IsPlayerFree)
            return;

        GameLocation location = Game1.currentLocation;
        if (location == null)
            return;

        // Nearest empty, idle machine within 2 tiles of the player.
        Point player = Game1.player.TilePoint;
        StardewValley.Object? best = null;
        Point bestTile = Point.Zero;
        int bestDistance = int.MaxValue;

        foreach (var pair in location.objects.Pairs)
        {
            var tile = new Point((int)pair.Key.X, (int)pair.Key.Y);
            int distance = Math.Abs(tile.X - player.X) + Math.Abs(tile.Y - player.Y);
            if (distance > 2 || distance >= bestDistance)
                continue;

            StardewValley.Object obj = pair.Value;
            if (obj is Chest || obj.GetMachineData() == null)
                continue;
            if (obj.heldObject.Value != null || obj.readyForHarvest.Value || obj.MinutesUntilReady > 0)
                continue; // only empty + idle machines need a refill

            best = obj;
            bestTile = tile;
            bestDistance = distance;
        }

        if (best == null)
            return;

        string label = "一键补货";
        Vector2 size = Game1.smallFont.MeasureString(label);
        int width = (int)size.X + 36;
        int height = Math.Max(48, (int)size.Y + 22);
        int x = bestTile.X * 64 + 32 - width / 2;
        int y = bestTile.Y * 64 - 80 - height;

        this.machineTile = bestTile;
        this.buttonBounds = new Rectangle(x, y, width, height);
        this.buttonVisible = true;
    }

    private void OnRenderedWorld(object? sender, RenderedWorldEventArgs e)
    {
        if (!this.buttonVisible)
            return;

        SpriteBatch b = e.SpriteBatch;
        Vector2 topLeft = Game1.GlobalToLocal(Game1.viewport, new Vector2(this.buttonBounds.X, this.buttonBounds.Y));
        Rectangle screenRect = new((int)topLeft.X, (int)topLeft.Y, this.buttonBounds.Width, this.buttonBounds.Height);

        Vector2 cursor = this.helper.Input.GetCursorPosition().AbsolutePixels;
        bool hovered = this.buttonBounds.Contains((int)cursor.X, (int)cursor.Y);

        Color boxColor = hovered ? new Color(255, 250, 205) : Color.White;
        IClickableMenu.drawTextureBox(
            b, Game1.menuTexture, new Rectangle(0, 256, 60, 60),
            screenRect.X, screenRect.Y, screenRect.Width, screenRect.Height,
            boxColor, 1f, drawShadow: false);

        string label = "一键补货";
        Vector2 size = Game1.smallFont.MeasureString(label);
        Vector2 textPosition = new(
            screenRect.X + (screenRect.Width - size.X) / 2f,
            screenRect.Y + (screenRect.Height - size.Y) / 2f);
        Utility.drawTextWithShadow(b, label, Game1.smallFont, textPosition, Game1.textColor);
    }

    private void OnButtonPressed(object? sender, ButtonPressedEventArgs e)
    {
        if (!this.buttonVisible || Game1.activeClickableMenu != null)
            return;
        if (e.Button != SButton.MouseLeft && e.Button != SButton.ControllerA)
            return;

        Vector2 cursor = e.Cursor.AbsolutePixels;
        if (!this.buttonBounds.Contains((int)cursor.X, (int)cursor.Y))
            return;

        this.helper.Input.Suppress(e.Button);

        GameLocation location = Game1.currentLocation;
        if (!location.objects.TryGetValue(new Vector2(this.machineTile.X, this.machineTile.Y), out StardewValley.Object? machine))
            return;

        // Feed the loader copied stacks from every chest, then apply the consumed amounts back to
        // the real chest inventories. This keeps vanilla machine matching without sharing Item
        // references between the temporary inventory and the source chests.
        List<Chest> chests = GetSourceChests(location);
        var combined = new Inventory();
        var sourceStacks = new List<SourceStack>();
        foreach (Chest chest in chests)
        {
            foreach (Item? item in chest.GetItemsForPlayer())
            {
                if (item == null || item.Stack <= 0)
                    continue;

                Item copy = item.getOne();
                copy.Stack = item.Stack;
                combined.Add(copy);
                sourceStacks.Add(new SourceStack(chest, item, item.QualifiedItemId, item.Stack, copy));
            }
        }

        if (machine.AttemptAutoLoad(combined, Game1.player))
        {
            foreach (SourceStack sourceStack in sourceStacks)
            {
                int remaining = combined.Contains(sourceStack.Copy)
                    ? Math.Max(0, sourceStack.Copy.Stack)
                    : 0;
                int consumed = sourceStack.InitialStack - remaining;
                if (consumed <= 0)
                    continue;

                int removed = sourceStack.Chest.GetItemsForPlayer().Reduce(sourceStack.Source, consumed, reduceRemainderFromInventory: false);
                if (removed < consumed)
                    removed += sourceStack.Chest.GetItemsForPlayer().ReduceId(sourceStack.ItemId, consumed - removed);
                if (removed < consumed)
                    this.monitor.Log($"Machine refill consumed {consumed} x {sourceStack.ItemId}, but only {removed} could be removed from the source chest.", LogLevel.Warn);
            }

            Game1.addHUDMessage(HUDMessage.ForCornerTextbox($"已为「{machine.DisplayName}」补货并启动。"));
            this.monitor.Log($"Refilled {machine.QualifiedItemId} at {this.machineTile} from chests.", LogLevel.Trace);
            return;
        }

        Game1.addHUDMessage(HUDMessage.ForCornerTextbox("箱子里没有这台机器需要的材料。"));
    }

    private sealed record SourceStack(Chest Chest, Item Source, string ItemId, int InitialStack, Item Copy);

    private static List<Chest> GetSourceChests(GameLocation location)
    {
        var chests = new List<Chest>();
        void Collect(GameLocation loc)
        {
            foreach (StardewValley.Object obj in loc.objects.Values)
            {
                if (obj is Chest chest && chest.playerChest.Value
                    && chest.SpecialChestType is Chest.SpecialChestTypes.None or Chest.SpecialChestTypes.BigChest
                    && !chests.Contains(chest))
                {
                    chests.Add(chest);
                }
            }
        }

        Collect(location);
        if (location is not Farm)
            Collect(Game1.getFarm());
        return chests;
    }
}
