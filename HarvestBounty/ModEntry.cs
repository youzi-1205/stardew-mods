using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Locations;

namespace HarvestBounty;

internal sealed class ModConfig
{
    // Multiply drops from chopping/shaking trees: wood, hardwood, sap, seeds (1 = vanilla, capped at 10x).
    public float TreeDropMultiplier { get; set; } = 1f;

    // Multiply drops from cutting grass: hay, mixed seeds (1 = vanilla, capped at 10x).
    public float GrassDropMultiplier { get; set; } = 1f;
}

/// <summary>GMCM's API surface — only the bits we use. Resolved at runtime via the mod registry, so
/// the dependency stays optional (no compile- or load-time coupling to GMCM).</summary>
public interface IGenericModConfigMenuApi
{
    void Register(IManifest mod, Action reset, Action save, bool titleScreenOnly = false);
    void AddSectionTitle(IManifest mod, Func<string> text, Func<string>? tooltip = null);
    void AddNumberOption(IManifest mod, Func<float> getValue, Action<float> setValue, Func<string> name, Func<string>? tooltip = null, float? min = null, float? max = null, float? interval = null, Func<float, string>? formatValue = null, string? fieldId = null);
}

/// <summary>Boosts tree chopping and grass cutting: when their drops land on the ground (as "debris"),
/// it spawns a few extra copies per the configured multiplier. Same trick as MineHelper's ore boost —
/// the game settles XP outside of debris, so adding debris only adds items, never experience. Mines are
/// excluded on purpose (mining drops are MineHelper's job). Host only (the host owns the world's debris).
/// Forage isn't handled here: picking up forage goes straight to the inventory without debris, so it
/// needs a different hook — planned for a later version.</summary>
internal sealed class ModEntry : Mod
{
    // Tree drops worth boosting (ids WITHOUT the "(O)" prefix): wood 388, hardwood 709, sap 92,
    // acorn 309, maple seed 310, pine cone 311, mahogany seed 292.
    private static readonly HashSet<string> TreeDropIds = new()
    {
        "388", "709", "92", "309", "310", "311", "292"
    };

    // Grass drops: hay 178, mixed seeds 770.
    private static readonly HashSet<string> GrassDropIds = new()
    {
        "178", "770"
    };

    private ModConfig config = new();

    // Debris we injected ourselves; skipped on the next DebrisListChanged to avoid exponential growth.
    private readonly HashSet<Debris> injectedDebris = new();

    public override void Entry(IModHelper helper)
    {
        this.config = helper.ReadConfig<ModConfig>();
        helper.Events.GameLoop.GameLaunched += this.OnGameLaunched;
        helper.Events.Player.Warped += this.OnWarped;
        helper.Events.World.DebrisListChanged += this.OnDebrisListChanged;
    }

    private void OnWarped(object? sender, WarpedEventArgs e)
    {
        // New location → the old location's debris is gone; drop our refs so the set can't leak.
        this.injectedDebris.Clear();
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

        gmcm.AddSectionTitle(this.ModManifest, () => "掉落倍率");
        gmcm.AddNumberOption(
            this.ModManifest,
            getValue: () => this.config.TreeDropMultiplier,
            setValue: v => this.config.TreeDropMultiplier = v,
            name: () => "砍树掉落倍率",
            tooltip: () => "砍树/摇树掉落的木材、硬木、树液、树种的倍率（1 = 原版，仅主机生效）。",
            min: 1f, max: 10f, interval: 0.5f);
        gmcm.AddNumberOption(
            this.ModManifest,
            getValue: () => this.config.GrassDropMultiplier,
            setValue: v => this.config.GrassDropMultiplier = v,
            name: () => "割草掉落倍率",
            tooltip: () => "割草掉落的干草、混合种子的倍率（1 = 原版，仅主机生效）。注意：干草会优先进筒仓，进仓的部分不受影响。",
            min: 1f, max: 10f, interval: 0.5f);
    }

    /// <summary>For each fresh tree/grass debris that lands outdoors, drop extra copies per the
    /// configured multiplier (host only).</summary>
    private void OnDebrisListChanged(object? sender, DebrisListChangedEventArgs e)
    {
        // Outdoors only, host only. Mines are off-limits — mining drops belong to MineHelper.
        if (!Game1.IsMasterGame || e.Location is MineShaft || !e.Location.IsOutdoors)
            return;
        if (this.config.TreeDropMultiplier <= 1f && this.config.GrassDropMultiplier <= 1f)
            return;

        foreach (Debris d in e.Added)
        {
            // Skip debris we injected ourselves — otherwise each extra drop would trigger more drops.
            if (this.injectedDebris.Remove(d))
                continue;

            var type = d.debrisType.Value;
            if (type != Debris.DebrisType.RESOURCE && type != Debris.DebrisType.OBJECT)
                continue;

            string id = StripObjectPrefix(d.itemId.Value);
            if (id.Length == 0)
                continue;

            float mult = this.ClassifyMultiplier(id);
            if (mult <= 1f)
                continue;

            int extra = RoundProbabilistic(mult - 1f);
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

    /// <summary>Pick the multiplier for a dropped item id: tree drops or grass drops. Returns 1
    /// (no boost) for anything else — crops, forage, monster loot, etc.</summary>
    private float ClassifyMultiplier(string id)
    {
        if (TreeDropIds.Contains(id))
            return this.config.TreeDropMultiplier;
        if (GrassDropIds.Contains(id))
            return this.config.GrassDropMultiplier;
        return 1f;
    }

    /// <summary>Normalize an item id by dropping a leading "(O)" qualifier so "(O)388" and "388" match.</summary>
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
}
