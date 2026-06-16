using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Locations;
using StardewValley.Objects;
using SObject = StardewValley.Object;

namespace MineHelper;

internal sealed class ModConfig
{
    // Mark a nearby stone as the "down-ladder stone" and reveal the ladder when you break it.
    public bool GuideToLadder { get; set; } = true;

    // Multiply the mining drops dug out of mine stones (1 = vanilla, capped at 10x).
    public float OreDropMultiplier { get; set; } = 1f;

    // Multiply the count of ore nodes spawned on each mine level (1 = vanilla, capped at 5x).
    public float OreSpawnMultiplier { get; set; } = 1f;

    // Per-ore spawn overrides: 0 = follow OreSpawnMultiplier, otherwise this exact multiplier.
    public float CopperSpawnMultiplier { get; set; }
    public float IronSpawnMultiplier { get; set; }
    public float GoldSpawnMultiplier { get; set; }
    public float IridiumSpawnMultiplier { get; set; }
    public float CoalSpawnMultiplier { get; set; }
}

/// <summary>GMCM's API surface — only the bits we use. Resolved at runtime via the mod registry, so
/// the dependency stays optional (no compile- or load-time coupling to GMCM).</summary>
public interface IGenericModConfigMenuApi
{
    void Register(IManifest mod, Action reset, Action save, bool titleScreenOnly = false);
    void AddSectionTitle(IManifest mod, Func<string> text, Func<string>? tooltip = null);
    void AddBoolOption(IManifest mod, Func<bool> getValue, Action<bool> setValue, Func<string> name, Func<string>? tooltip = null, string? fieldId = null);
    void AddNumberOption(IManifest mod, Func<float> getValue, Action<float> setValue, Func<string> name, Func<string>? tooltip = null, float? min = null, float? max = null, float? interval = null, Func<float, string>? formatValue = null, string? fieldId = null);
}

/// <summary>Takes the luck out of finding the down-ladder. On each normal mine level that hasn't
/// spawned a ladder yet, it picks the nearest breakable stone, highlights it ("dig here to go down"),
/// and when you break that stone it forces the ladder to spawn right there via the game's own
/// <c>MineShaft.createLadderDown</c>. Infested/monster levels keep their rule (clear all monsters
/// first) and just get a heads-up. Once a ladder exists (yours or the game's) it's highlighted, with
/// an edge pointer when off-screen. Pure assist — it never moves, mines, or fights for you.</summary>
internal sealed class ModEntry : Mod
{
    private const int LadderTile = 173; // down-ladder on the Buildings layer
    private const int ShaftTile = 174;  // shaft (drops you straight to the next level)

    // Mining-drop item ids worth boosting (ore/coal/gems), stored WITHOUT the "(O)" prefix so we can
    // match a Debris.itemId whether or not it carries one. checkStoneForItems emits these via
    // createObjectDebris: ores 378/380/384/386, coal 382, gems 535/536/537/749/72.
    private static readonly HashSet<string> DropWhitelist = new()
    {
        "378", "380", "384", "386", "382", "535", "536", "537", "749", "72"
    };

    private ModConfig config = new();
    private Texture2D? pixel;
    private readonly List<Point> ladders = new();
    private Point? targetStone;   // the stone we've designated to hide the ladder
    private int rescanTimer;

    // Debris instances we injected ourselves; skipped on the next DebrisListChanged to avoid exponential growth.
    private readonly HashSet<Debris> injectedDebris = new();

    public override void Entry(IModHelper helper)
    {
        this.config = helper.ReadConfig<ModConfig>();
        helper.Events.GameLoop.GameLaunched += this.OnGameLaunched;
        helper.Events.GameLoop.UpdateTicked += this.OnUpdateTicked;
        helper.Events.Player.Warped += this.OnWarped;
        helper.Events.World.DebrisListChanged += this.OnDebrisListChanged;
        helper.Events.Display.RenderedWorld += this.OnRenderedWorld;
    }

    private void OnWarped(object? sender, WarpedEventArgs e)
    {
        // New level → forget everything and re-evaluate promptly.
        this.ladders.Clear();
        this.targetStone = null;
        this.rescanTimer = 0;
        this.injectedDebris.Clear(); // the old level's debris is gone; drop our refs so the set can't leak

        // Ore-spawn boost: on a fresh mine level the host tops up ore nodes (multiplayer: host only).
        if (e.IsLocalPlayer && e.NewLocation is MineShaft mine && Game1.IsMasterGame)
            this.BoostOreSpawns(mine);
    }

    /// <summary>Wire up Generic Mod Config Menu if it's installed (optional dependency).</summary>
    private void OnGameLaunched(object? sender, GameLaunchedEventArgs e)
    {
        var gmcm = this.Helper.ModRegistry.GetApi<IGenericModConfigMenuApi>("spacechase0.GenericModConfigMenu");
        if (gmcm is null)
            return;

        gmcm.Register(
            mod: this.ModManifest,
            reset: () => this.config = new ModConfig(),
            save: () => this.Helper.WriteConfig(this.config));

        gmcm.AddSectionTitle(this.ModManifest, () => "梯子向导");
        gmcm.AddBoolOption(
            this.ModManifest,
            getValue: () => this.config.GuideToLadder,
            setValue: v => this.config.GuideToLadder = v,
            name: () => "梯子向导",
            tooltip: () => "在矿洞标出下楼梯子/竖井，并指引最近可挖的下楼石头。");

        gmcm.AddSectionTitle(this.ModManifest, () => "采矿掉落");
        gmcm.AddNumberOption(
            this.ModManifest,
            getValue: () => this.config.OreDropMultiplier,
            setValue: v => this.config.OreDropMultiplier = v,
            name: () => "采矿掉落倍率",
            tooltip: () => "挖矿洞石头时矿石/煤/宝石掉落的倍率（1 = 原版，仅主机生效）。",
            min: 1f, max: 10f, interval: 0.5f);

        gmcm.AddSectionTitle(this.ModManifest, () => "矿石刷新");
        gmcm.AddNumberOption(
            this.ModManifest,
            getValue: () => this.config.OreSpawnMultiplier,
            setValue: v => this.config.OreSpawnMultiplier = v,
            name: () => "矿石刷新总倍率",
            tooltip: () => "每层矿洞矿石节点数量的总倍率（1 = 原版，仅主机生效）。",
            min: 1f, max: 5f, interval: 0.5f);
        gmcm.AddNumberOption(
            this.ModManifest,
            getValue: () => this.config.CopperSpawnMultiplier,
            setValue: v => this.config.CopperSpawnMultiplier = v,
            name: () => "铜矿刷新倍率",
            tooltip: () => "0 = 跟随总倍率。",
            min: 0f, max: 5f, interval: 0.5f);
        gmcm.AddNumberOption(
            this.ModManifest,
            getValue: () => this.config.IronSpawnMultiplier,
            setValue: v => this.config.IronSpawnMultiplier = v,
            name: () => "铁矿刷新倍率",
            tooltip: () => "0 = 跟随总倍率。",
            min: 0f, max: 5f, interval: 0.5f);
        gmcm.AddNumberOption(
            this.ModManifest,
            getValue: () => this.config.GoldSpawnMultiplier,
            setValue: v => this.config.GoldSpawnMultiplier = v,
            name: () => "金矿刷新倍率",
            tooltip: () => "0 = 跟随总倍率。",
            min: 0f, max: 5f, interval: 0.5f);
        gmcm.AddNumberOption(
            this.ModManifest,
            getValue: () => this.config.IridiumSpawnMultiplier,
            setValue: v => this.config.IridiumSpawnMultiplier = v,
            name: () => "铱矿刷新倍率",
            tooltip: () => "0 = 跟随总倍率。",
            min: 0f, max: 5f, interval: 0.5f);
        gmcm.AddNumberOption(
            this.ModManifest,
            getValue: () => this.config.CoalSpawnMultiplier,
            setValue: v => this.config.CoalSpawnMultiplier = v,
            name: () => "煤矿刷新倍率",
            tooltip: () => "0 = 跟随总倍率。",
            min: 0f, max: 5f, interval: 0.5f);
    }

    /// <summary>Mining-drop boost: for each fresh ore/coal/gem debris dug out of a mine stone, drop a
    /// few extra copies (multiplayer: host only). The game settles XP outside of debris, so spawning
    /// extra debris never touches experience — that's the whole point of doing it here.</summary>
    private void OnDebrisListChanged(object? sender, DebrisListChangedEventArgs e)
    {
        if (this.config.OreDropMultiplier <= 1f || e.Location is not MineShaft || !Game1.IsMasterGame)
            return;

        foreach (Debris d in e.Added)
        {
            // Skip debris we injected ourselves — otherwise each extra drop would trigger more drops.
            if (this.injectedDebris.Remove(d))
                continue;

            // Only object/resource debris carrying a whitelisted ore/coal/gem id.
            var type = d.debrisType.Value;
            if (type != Debris.DebrisType.RESOURCE && type != Debris.DebrisType.OBJECT)
                continue;
            string id = StripObjectPrefix(d.itemId.Value);
            if (id.Length == 0 || !DropWhitelist.Contains(id))
                continue;

            int extra = RoundProbabilistic(this.config.OreDropMultiplier - 1f);
            if (extra <= 0)
                continue;

            // Drop the extras at the original debris' position when available, else near the player.
            Vector2 origin = d.Chunks.Count > 0
                ? d.Chunks[0].position.Value
                : Game1.player.getStandingPosition();
            for (int i = 0; i < extra; i++)
            {
                var copy = new Debris(d.itemId.Value, origin, Game1.player.getStandingPosition());
                this.injectedDebris.Add(copy);
                e.Location.debris.Add(copy);
            }
        }
    }

    /// <summary>Ore-spawn boost: count the ore nodes the game already placed on this level and add more
    /// of each kind per the configured multipliers. Runs on Warped (nodes are 100% placed by then).</summary>
    private void BoostOreSpawns(MineShaft mine)
    {
        // Tally existing ore nodes by ore kind.
        var counts = new Dictionary<OreKind, int>();
        var templates = new Dictionary<OreKind, SObject>();
        foreach (var pair in mine.objects.Pairs)
        {
            SObject obj = pair.Value;
            if (!obj.IsBreakableStone())
                continue;
            if (!TryClassifyOre(obj.ItemId, out OreKind kind))
                continue;
            counts[kind] = counts.GetValueOrDefault(kind) + 1;
            templates.TryAdd(kind, obj); // remember one real node to clone (preserves ColoredObject color)
        }

        foreach (var (kind, count) in counts)
        {
            float effMult = this.EffectiveSpawnMultiplier(kind);
            int extra = (int)Math.Round(count * (effMult - 1f), MidpointRounding.AwayFromZero);
            if (extra <= 0)
                continue;

            SObject template = templates[kind];
            for (int i = 0; i < extra; i++)
            {
                if (this.TryFindEmptyTile(mine, out Vector2 tile))
                {
                    mine.objects.Add(tile, CloneNode(template));
                    // Counter checkStoneForItems' stonesLeftOnThisLevel-- so ladder odds stay vanilla.
                    mine.stonesLeftOnThisLevel += 1;
                }
            }
        }
    }

    /// <summary>Pick a random tile where an ore node can legally sit (clear, solid, walkable). Retries a
    /// bounded number of times before giving up for this node.</summary>
    private bool TryFindEmptyTile(MineShaft mine, out Vector2 tile)
    {
        for (int attempt = 0; attempt < 20; attempt++)
        {
            tile = mine.getRandomTile(Game1.random);
            if (mine.CanItemBePlacedHere(tile, false, CollisionMask.All, CollisionMask.None)
                && mine.isTileOnClearAndSolidGround(tile))
            {
                return true;
            }
        }
        tile = Vector2.Zero;
        return false;
    }

    /// <summary>Clone an ore node so the copy mines/colours/drops identically. ColoredObject nodes (e.g.
    /// hard-mode iron 290) need their color copied or they'd render and drop wrong.</summary>
    private static SObject CloneNode(SObject original)
    {
        if (original is ColoredObject colored)
            return new ColoredObject(original.ItemId, 1, colored.color.Value) { MinutesUntilReady = original.MinutesUntilReady };
        return new SObject(original.ItemId, 1) { MinutesUntilReady = original.MinutesUntilReady };
    }

    private float EffectiveSpawnMultiplier(OreKind kind)
    {
        float over = kind switch
        {
            OreKind.Copper => this.config.CopperSpawnMultiplier,
            OreKind.Iron => this.config.IronSpawnMultiplier,
            OreKind.Gold => this.config.GoldSpawnMultiplier,
            OreKind.Iridium => this.config.IridiumSpawnMultiplier,
            OreKind.Coal => this.config.CoalSpawnMultiplier,
            _ => 0f
        };
        return over > 0f ? over : this.config.OreSpawnMultiplier;
    }

    private enum OreKind { Copper, Iron, Gold, Iridium, Coal }

    /// <summary>Map an ore-node ItemId to its ore kind. Node ids (from MineShaft.getAppropriateOre):
    /// copper 751, iron 290/850, gold 764, iridium 765, coal BasicCoalNode0/1.</summary>
    private static bool TryClassifyOre(string itemId, out OreKind kind)
    {
        switch (StripObjectPrefix(itemId))
        {
            case "751": kind = OreKind.Copper; return true;
            case "290":
            case "850": kind = OreKind.Iron; return true;
            case "764": kind = OreKind.Gold; return true;
            case "765": kind = OreKind.Iridium; return true;
            case "BasicCoalNode0":
            case "BasicCoalNode1": kind = OreKind.Coal; return true;
            default: kind = default; return false;
        }
    }

    /// <summary>Normalize an item id by dropping a leading "(O)" qualifier so "(O)378" and "378" match.</summary>
    private static string StripObjectPrefix(string itemId)
    {
        if (string.IsNullOrEmpty(itemId))
            return string.Empty;
        return itemId.StartsWith("(O)", StringComparison.Ordinal) ? itemId.Substring(3) : itemId;
    }

    /// <summary>Round to an int, treating the fractional part as a probability for the last unit.</summary>
    private static int RoundProbabilistic(float value)
    {
        if (value <= 0f)
            return 0;
        int whole = (int)value;
        float frac = value - whole;
        if (frac > 0f && Game1.random.NextDouble() < frac)
            whole++;
        return whole;
    }

    private void OnUpdateTicked(object? sender, UpdateTickedEventArgs e)
    {
        if (!this.config.GuideToLadder || !Context.IsWorldReady)
            return;

        if (Game1.currentLocation is not MineShaft mine)
        {
            if (this.ladders.Count > 0)
                this.ladders.Clear();
            this.targetStone = null;
            return;
        }

        // A ladder already exists on this level → just track and highlight the real one.
        if (mine.ladderHasSpawned)
        {
            this.targetStone = null;
            if (this.rescanTimer-- <= 0)
            {
                this.rescanTimer = 30;
                this.ScanLadders(mine);
            }
            return;
        }

        // No ladder yet: don't keep stale real-ladder tiles around.
        if (this.ladders.Count > 0)
            this.ladders.Clear();

        // If we've designated a stone, watch for the player breaking it — then force the ladder there.
        if (this.targetStone.HasValue)
        {
            var tile = new Vector2(this.targetStone.Value.X, this.targetStone.Value.Y);
            if (!mine.objects.ContainsKey(tile))
            {
                // The designated stone is gone (player dug it out). Spawn the ladder on the spot,
                // unless the game already spawned one this frame (ladderHasSpawned guards re-entry).
                if (!mine.ladderHasSpawned && CanGenerateLadder(mine))
                    mine.createLadderDown(this.targetStone.Value.X, this.targetStone.Value.Y);
                this.targetStone = null;
            }
            return; // stone still there, or we just spawned the ladder — re-evaluate next pass
        }

        // No designated stone yet: pick one periodically (skip infested levels and non-ladder levels).
        if (this.rescanTimer-- > 0)
            return;
        this.rescanTimer = 30;
        if (CanGenerateLadder(mine) && !mine.mustKillAllMonstersToAdvance())
            this.targetStone = this.PickNearestStone(mine);
        // Infested / skill-cavern / level-120: leave targetStone null; the renderer shows a hint.
    }

    /// <summary>Skill cavern side-branches (77377) and the bottom of the mine (120) never spawn a
    /// down-ladder — matches the game's own shouldCreateLadderOnThisLevel check.</summary>
    private static bool CanGenerateLadder(MineShaft mine)
    {
        return mine.mineLevel != 77377 && mine.mineLevel != 120;
    }

    private Point? PickNearestStone(MineShaft mine)
    {
        Point from = Game1.player.TilePoint;
        Point? best = null;
        int bestDist = int.MaxValue;
        foreach (var pair in mine.objects.Pairs)
        {
            if (!pair.Value.IsBreakableStone())
                continue;
            int x = (int)pair.Key.X;
            int y = (int)pair.Key.Y;
            int dist = Math.Abs(x - from.X) + Math.Abs(y - from.Y);
            if (dist < bestDist)
            {
                bestDist = dist;
                best = new Point(x, y);
            }
        }
        return best;
    }

    private void ScanLadders(GameLocation location)
    {
        this.ladders.Clear();
        var layer = location.Map?.GetLayer("Buildings");
        if (layer == null)
            return;

        for (int x = 0; x < layer.LayerWidth; x++)
        {
            for (int y = 0; y < layer.LayerHeight; y++)
            {
                var tile = layer.Tiles[x, y];
                if (tile != null && (tile.TileIndex == LadderTile || tile.TileIndex == ShaftTile))
                    this.ladders.Add(new Point(x, y));
            }
        }
    }

    private void OnRenderedWorld(object? sender, RenderedWorldEventArgs e)
    {
        if (!this.config.GuideToLadder || Game1.currentLocation is not MineShaft mine)
            return;

        SpriteBatch b = e.SpriteBatch;
        int vw = Game1.viewport.Width;
        int vh = Game1.viewport.Height;
        float pulse = 0.55f + (0.45f * (float)Math.Sin(Game1.currentGameTime.TotalGameTime.TotalSeconds * 4.0));
        Point playerTile = Game1.player.TilePoint;

        // Real ladders (gold).
        foreach (Point ladder in this.ladders)
            this.DrawMarker(b, ladder, vw, vh, pulse, playerTile, Color.Gold, "↓ 梯子");

        // Designated stone to dig (orange).
        if (this.targetStone.HasValue)
            this.DrawMarker(b, this.targetStone.Value, vw, vh, pulse, playerTile, Color.Orange, "⛏ 敲此下楼");

        // Infested level with no ladder yet: tell the player why and what's left.
        if (!mine.ladderHasSpawned && CanGenerateLadder(mine) && mine.mustKillAllMonstersToAdvance())
        {
            int monsters = mine.characters.Count(c => c.IsMonster);
            if (monsters > 0)
                this.DrawTopBanner(b, vw, $"先清光本层怪物（剩 {monsters} 只）才会出现梯子");
        }
    }

    /// <summary>Draws a marker for a tile: a pulsing box + label when on-screen, or an edge marker +
    /// distance pointing toward it when off-screen.</summary>
    private void DrawMarker(SpriteBatch b, Point tile, int vw, int vh, float pulse, Point playerTile, Color color, string label)
    {
        Vector2 world = new(tile.X * 64f, tile.Y * 64f);
        Vector2 screen = Game1.GlobalToLocal(Game1.viewport, world);
        bool onScreen = screen.X >= -64f && screen.X <= vw && screen.Y >= -64f && screen.Y <= vh;
        Texture2D px = this.Pixel();

        if (onScreen)
        {
            Color c = color * (0.35f + (0.5f * pulse));
            const int t = 5;
            var r = new Rectangle((int)screen.X, (int)screen.Y, 64, 64);
            b.Draw(px, new Rectangle(r.X, r.Y, r.Width, t), c);
            b.Draw(px, new Rectangle(r.X, r.Bottom - t, r.Width, t), c);
            b.Draw(px, new Rectangle(r.X, r.Y, t, r.Height), c);
            b.Draw(px, new Rectangle(r.Right - t, r.Y, t, r.Height), c);

            Vector2 sz = Game1.smallFont.MeasureString(label);
            Utility.drawTextWithShadow(b, label, Game1.smallFont,
                new Vector2(r.X + (32f - (sz.X / 2f)), r.Y - 28f), Color.White);
        }
        else
        {
            const float margin = 56f;
            Vector2 center = new(vw / 2f, vh / 2f);
            Vector2 dir = screen - center;
            if (dir == Vector2.Zero)
                return;
            dir.Normalize();
            float scale = Math.Min(
                ((vw / 2f) - margin) / Math.Max(1e-3f, Math.Abs(dir.X)),
                ((vh / 2f) - margin) / Math.Max(1e-3f, Math.Abs(dir.Y)));
            Vector2 edge = center + (dir * scale);

            b.Draw(px, new Rectangle((int)edge.X - 14, (int)edge.Y - 14, 28, 28), color * (0.6f + (0.4f * pulse)));
            int dist = Math.Abs(tile.X - playerTile.X) + Math.Abs(tile.Y - playerTile.Y);
            string txt = $"{label} {dist}";
            Vector2 sz = Game1.smallFont.MeasureString(txt);
            Utility.drawTextWithShadow(b, txt, Game1.smallFont, new Vector2(edge.X - (sz.X / 2f), edge.Y + 16f), Color.White);
        }
    }

    private void DrawTopBanner(SpriteBatch b, int vw, string text)
    {
        Vector2 sz = Game1.smallFont.MeasureString(text);
        Utility.drawTextWithShadow(b, text, Game1.smallFont, new Vector2((vw / 2f) - (sz.X / 2f), 96f), Color.White);
    }

    private Texture2D Pixel()
    {
        if (this.pixel == null)
        {
            this.pixel = new Texture2D(Game1.graphics.GraphicsDevice, 1, 1);
            this.pixel.SetData(new[] { Color.White });
        }
        return this.pixel;
    }
}
