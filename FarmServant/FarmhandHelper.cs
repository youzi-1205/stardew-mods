using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Buildings;
using StardewValley.Characters;
using StardewValley.Extensions;
using StardewValley.GameData.Characters;
using StardewValley.GameData.Crops;
using StardewValley.GameData.FarmAnimals;
using StardewValley.GameData.Machines;
using StardewValley.Pathfinding;
using StardewValley.TerrainFeatures;
using StardewValley.Objects;
using StardewValley.Tools;

namespace FarmServant;

/// <summary>Routes items harvested via Crop.harvest's junimoHarvester hook into farm chests.
/// NEVER added to location.characters — it exists only as a method argument, so it can never reach
/// the save serializer (a mod-defined NPC subclass in a characters list corrupts the save).</summary>
internal sealed class ChestJunimo : JunimoHarvester
{
    /// <summary>Deposits one item and returns the leftover (null when fully stored). Assigned by
    /// the helper before each harvest so storage uses the same smart chest-matching everywhere.</summary>
    public Func<Item, Item?>? Deposit;

    public override void tryToAddItemToHut(Item i)
    {
        Item? remaining = this.Deposit != null ? this.Deposit(i) : i;
        if (remaining != null && this.currentLocation != null)
            Game1.createItemDebris(remaining, this.Position, -1, this.currentLocation);
    }
}

/// <summary>Feature 2: a farmhand NPC. He idles at a corner of the farm; interact with him to start
/// the day's chores. He then physically walks tile to tile — watering, harvesting (into chests),
/// fertilizing and replanting from chest materials — and returns to his corner when done.
///
/// He is a PLAIN NPC (not a subclass) created at day start and removed before save, driven by the
/// game's own PathFindController (NPCs animate their walk cycle natively under a controller).</summary>
internal sealed class FarmhandHelper
{
    private enum TaskKind { Water, Harvest, Fertilize, Plant, ChopTree, BreakStone, ClearWeeds, TendAnimals, Deliver, CollectMachine, CollectBuilding, CutGrass }

    private sealed record FarmTask(TaskKind Kind, Point Tile, string? ItemId = null, string? LocationName = null, string? FertilizerId = null);
    private sealed record WorkDebrisSite(Point Tile, HashSet<Debris> ExistingDebris);
    private sealed record FertilizerOption(string Id, FertilizerKind Kind, float Strength);
    private sealed record FertilizerCandidate(Point Tile, string SeedId, CropData CropData, Crop? Crop);
    private sealed record FertilizerScore(Point Tile, string FertilizerId, float Score);

    private enum FertilizerKind { Quality, Speed, Retaining }

    private const string NpcName = "FarmSuiteHelper";
    private const string NpcTextureName = "FarmSuiteHelper";
    private const string DefaultSpriteAsset = "Characters/FarmSuiteHelper";
    private const string DefaultPortraitAsset = "Portraits/FarmSuiteHelper";
    private const string CustomSpriteAsset = "Characters/FarmSuiteHelper";
    private const string CustomPortraitAsset = "Portraits/FarmSuiteHelper";
    private const string SourceSpriteAsset = "Characters/Lewis";
    private const string SourcePortraitAsset = "Portraits/Lewis";
    private const long VisualFarmerId = -701_202_501L;
    private static readonly FertilizerOption[] Fertilizers =
    {
        new("919", FertilizerKind.Quality, 0.20f),    // Deluxe Fertilizer
        new("369", FertilizerKind.Quality, 0.12f),    // Quality Fertilizer
        new("368", FertilizerKind.Quality, 0.06f),    // Basic Fertilizer
        new("918", FertilizerKind.Speed, 0.33f),      // Hyper Speed-Gro
        new("466", FertilizerKind.Speed, 0.25f),      // Deluxe Speed-Gro
        new("465", FertilizerKind.Speed, 0.10f),      // Speed-Gro
        new("920", FertilizerKind.Retaining, 1.00f),  // Deluxe Retaining Soil
        new("371", FertilizerKind.Retaining, 0.66f),  // Quality Retaining Soil
        new("370", FertilizerKind.Retaining, 0.33f),  // Basic Retaining Soil
    };
    private static readonly Point[] HomeCandidates =
    {
        new(61, 17),
        new(58, 17),
        new(61, 20),
        new(66, 22),
        new(72, 17),
        new(67, 17),
    };

    private readonly IModHelper helper;
    private readonly IMonitor monitor;
    private readonly Func<ModConfig> config;
    private readonly ChestJunimo chestJunimo = new();

    private NPC? npc;
    private readonly List<FarmTask> tasks = new();
    private FarmTask? current;
    private int workTimerMs;
    private int workDurationMs;
    private bool working;
    private bool returningToCorner;
    private bool summonedToPlayer;
    private bool autoStartedToday;
    private int presenceTicks;
    private readonly List<Item> carried = new();
    private readonly List<WorkDebrisSite> recentWorkSites = new();
    private bool warnedChestsFull;
    private Point corner = new(67, 17);
    private Tool? helperAxe;
    private Tool? helperPickaxe;
    private Tool? helperScythe;
    private WateringCan? helperWateringCan;
    private Farmer? visualFarmer;

    public FarmhandHelper(IModHelper helper, IMonitor monitor, Func<ModConfig> config)
    {
        this.helper = helper;
        this.monitor = monitor;
        this.config = config;

        helper.Events.GameLoop.DayStarted += (_, _) => this.OnDayStarted();
        helper.Events.GameLoop.Saving += (_, _) => this.RemoveNpc();
        helper.Events.GameLoop.DayEnding += (_, _) => this.RemoveNpc();
        // NOTE: no respawn on Saved — re-adding the NPC in the middle of the game's new-day
        // processing can get wiped by later steps. DayStarted + the EnsurePresent self-heal cover it.
        helper.Events.GameLoop.SaveLoaded += (_, _) => this.SweepStrays();
        helper.Events.GameLoop.ReturnedToTitle += (_, _) => this.Reset();
        helper.Events.GameLoop.UpdateTicked += this.OnUpdateTicked;
        helper.Events.Display.RenderedWorld += this.OnRenderedWorld;
        helper.Events.Input.ButtonPressed += this.OnButtonPressed;
        helper.Events.Content.AssetRequested += this.OnAssetRequested;
    }

    // ── lifecycle ────────────────────────────────────────────────────────────

    private void OnAssetRequested(object? sender, AssetRequestedEventArgs e)
    {
        if (e.NameWithoutLocale.IsEquivalentTo("Data/Characters"))
        {
            e.Edit(asset =>
            {
                string spriteAsset = this.GetHelperSpriteAsset();
                string portraitAsset = this.GetHelperPortraitAsset(spriteAsset);
                var data = asset.AsDictionary<string, CharacterData>().Data;
                data[NpcName] = new CharacterData
                {
                    DisplayName = this.config().HelperName,
                    HomeRegion = "Other",
                    IsDarkSkinned = true,
                    CanSocialize = "FALSE",
                    CanReceiveGifts = false,
                    CanGreetNearbyCharacters = false,
                    IntroductionsQuest = false,
                    ItemDeliveryQuests = "FALSE",
                    PerfectionScore = false,
                    SpawnIfMissing = false,
                    TextureName = GetTextureName(spriteAsset),
                    Appearance = new List<CharacterAppearanceData>
                    {
                        new()
                        {
                            Id = "Default",
                            Sprite = spriteAsset,
                            Portrait = portraitAsset,
                        },
                    },
                    Size = new Point(16, 32),
                    Breather = false,
                    Home = new List<CharacterHomeData>
                    {
                        new()
                        {
                            Id = "Farm",
                            Location = "Farm",
                            Tile = HomeCandidates[0],
                            Direction = "down",
                        },
                    },
                };
            });
        }
        else if (e.NameWithoutLocale.IsEquivalentTo(CustomSpriteAsset))
            e.LoadFrom(this.CreateTransparentHelperTexture, AssetLoadPriority.Exclusive);
        else if (e.NameWithoutLocale.IsEquivalentTo(CustomPortraitAsset))
            e.LoadFrom(() => this.CreateDarkSkinLewisTexture(SourcePortraitAsset, isPortrait: true), AssetLoadPriority.Exclusive);
    }

    private void OnDayStarted()
    {
        // NOTE: deliberately no festival-day skip — the festival happens in town while the helper
        // stays on the farm, and the silent absence confused the player. Game1.eventUp still
        // pauses his work during actual event scenes.
        if (!this.config().EnableHelper || !Game1.IsMasterGame)
            return;
        if (this.npc != null)
            return;

        Farm farm = Game1.getFarm();
        this.SweepStrays();

        this.corner = this.FindHomeTile(farm);
        string spriteAsset = this.GetHelperSpriteAsset();
        var sprite = new AnimatedSprite(spriteAsset, 0, 16, 32);
        Texture2D portrait = this.LoadHelperPortrait(this.GetHelperPortraitAsset(spriteAsset));
        var npc = new NPC(sprite, this.corner.ToVector2() * 64f, "Farm", 2, NpcName, portrait, eventActor: false);
        npc.speed = Math.Clamp(this.config().HelperSpeed, 2, 8);
        npc.farmerPassesThrough = true;
        npc.IsInvisible = false;
        npc.HideShadow = true;
        npc.currentLocation = farm; // the ctor doesn't set this
        npc.reloadData();
        npc.ChooseAppearance();
        npc.faceDirection(2);

        // The default corner assumes the standard farm layout; on other layouts it may be water
        // or cliffs, so snap to the nearest open tile.
        Vector2 open = Utility.recursiveFindOpenTileForCharacter(npc, farm, this.corner.ToVector2(), 30);
        if (open != Vector2.Zero)
        {
            this.corner = new Point((int)open.X, (int)open.Y);
            npc.Position = open * 64f;
        }

        farm.addCharacter(npc);
        this.npc = npc;
        this.visualFarmer = this.UsesGeneratedFarmerAppearance()
            ? this.CreateVisualFarmer(npc)
            : null;

        this.working = false;
        this.returningToCorner = false;
        this.summonedToPlayer = false;
        this.autoStartedToday = false;
        this.current = null;
        this.tasks.Clear();
        this.monitor.Log($"Helper spawned at {this.corner}.", LogLevel.Info);
    }

    /// <summary>Self-heal: whatever removes the helper (new-day resets, other mods, anything we
    /// haven't predicted), put him back within a couple of seconds.</summary>
    private void EnsurePresent()
    {
        if (++this.presenceTicks < 120)
            return;
        this.presenceTicks = 0;

        if (!this.config().EnableHelper)
            return;

        if (this.npc == null)
        {
            this.OnDayStarted();
            if (this.npc != null)
                this.monitor.Log("Helper respawned (was missing).", LogLevel.Info);
            return;
        }

        GameLocation? location = this.npc.currentLocation;
        if (location != null && !location.characters.Contains(this.npc))
        {
            location.characters.Add(this.npc);
            this.monitor.Log("Helper re-added to its location (was removed by something).", LogLevel.Info);
        }
    }

    private Point FindHomeTile(Farm farm)
    {
        var probe = new NPC(new AnimatedSprite(this.GetHelperSpriteAsset(), 0, 16, 32), Vector2.Zero, 2, NpcName);
        foreach (Point candidate in HomeCandidates)
        {
            Vector2 open = Utility.recursiveFindOpenTileForCharacter(probe, farm, candidate.ToVector2(), 8);
            if (open == Vector2.Zero)
                continue;

            var tile = new Point((int)open.X, (int)open.Y);
            if (!this.IsGoodHomeTile(farm, tile))
                continue;

            return tile;
        }

        return HomeCandidates[0];
    }

    private bool IsGoodHomeTile(Farm farm, Point tile)
    {
        var tileVector = new Vector2(tile.X, tile.Y);
        if (farm.objects.ContainsKey(tileVector))
            return false;

        foreach (var pair in farm.objects.Pairs)
        {
            if (pair.Value is Chest && Math.Abs((int)pair.Key.X - tile.X) + Math.Abs((int)pair.Key.Y - tile.Y) <= 2)
                return false;
        }

        return true;
    }

    private string GetHelperSpriteAsset()
    {
        string sprite = NormalizeAssetName(this.config().HelperSprite);
        return string.IsNullOrWhiteSpace(sprite) ? DefaultSpriteAsset : sprite;
    }

    private bool UsesGeneratedFarmerAppearance()
    {
        return NormalizeAssetName(this.GetHelperSpriteAsset()).Equals(CustomSpriteAsset, StringComparison.OrdinalIgnoreCase);
    }

    private string GetHelperPortraitAsset(string spriteAsset)
    {
        string normalized = NormalizeAssetName(spriteAsset);
        if (normalized.Equals(CustomSpriteAsset, StringComparison.OrdinalIgnoreCase))
            return CustomPortraitAsset;

        return normalized.StartsWith("Characters/", StringComparison.OrdinalIgnoreCase)
            ? "Portraits/" + normalized["Characters/".Length..]
            : DefaultPortraitAsset;
    }

    private Texture2D LoadHelperPortrait(string portraitAsset)
    {
        try
        {
            return Game1.content.Load<Texture2D>(portraitAsset);
        }
        catch (Exception ex)
        {
            this.monitor.Log($"Couldn't load helper portrait '{portraitAsset}', using {DefaultPortraitAsset}: {ex.Message}", LogLevel.Trace);
            return Game1.content.Load<Texture2D>(DefaultPortraitAsset);
        }
    }

    private Farmer CreateVisualFarmer(NPC source)
    {
        var farmer = new Farmer(new FarmerSprite("Characters\\Farmer\\farmer_base"), source.Position, source.speed, this.config().HelperName, new List<Item>(), isMale: true)
        {
            Name = NpcName,
            displayName = this.config().HelperName,
            UniqueMultiplayerID = VisualFarmerId,
            isFakeEventActor = true,
            currentLocation = source.currentLocation,
            CanMove = false,
        };

        farmer.changeGender(male: true);
        farmer.changeSkinColor(8, force: true);
        farmer.changeHairStyle(24);
        farmer.changeHairColor(new Color(150, 142, 126));
        farmer.changeEyeColor(new Color(86, 54, 35));
        farmer.changeShirt("1000");
        farmer.changePantStyle("0");
        farmer.changePantsColor(new Color(92, 45, 44));
        farmer.changeShoeColor("8");
        farmer.accessory.Set(-1);
        farmer.hat.Set(null);
        farmer.boots.Set(null);
        farmer.UpdateClothing();
        farmer.faceDirection(source.FacingDirection);
        farmer.FarmerSprite.setCurrentSingleFrame(GetFarmerIdleFrame(source.FacingDirection), 32000, secondaryArm: false, flip: source.FacingDirection == 3);
        return farmer;
    }

    private Farmer GetVisualFarmer()
    {
        if (this.npc == null)
            throw new InvalidOperationException("Helper NPC is not available.");

        if (this.visualFarmer == null)
            this.visualFarmer = this.CreateVisualFarmer(this.npc);

        return this.visualFarmer;
    }

    private static string NormalizeAssetName(string asset)
    {
        return asset.Trim().Replace('\\', '/');
    }

    private static string GetTextureName(string spriteAsset)
    {
        string normalized = NormalizeAssetName(spriteAsset);
        return normalized.StartsWith("Characters/", StringComparison.OrdinalIgnoreCase)
            ? normalized["Characters/".Length..]
            : NpcTextureName;
    }

    private Texture2D CreateDarkSkinLewisTexture(string sourceAsset, bool isPortrait)
    {
        Texture2D source = Game1.content.Load<Texture2D>(sourceAsset);
        var pixels = new Color[source.Width * source.Height];
        source.GetData(pixels);

        for (int i = 0; i < pixels.Length; i++)
        {
            int x = i % source.Width;
            int y = i / source.Width;
            if (ShouldRecolorLewisSkin(pixels[i], x, y, isPortrait))
                pixels[i] = ToDarkSkin(pixels[i]);
        }

        var result = new Texture2D(Game1.graphics.GraphicsDevice, source.Width, source.Height);
        result.SetData(pixels);
        return result;
    }

    private Texture2D CreateTransparentHelperTexture()
    {
        Texture2D source = Game1.content.Load<Texture2D>(SourceSpriteAsset);
        var result = new Texture2D(Game1.graphics.GraphicsDevice, source.Width, source.Height);
        result.SetData(Enumerable.Repeat(Color.Transparent, source.Width * source.Height).ToArray());
        return result;
    }

    private static bool ShouldRecolorLewisSkin(Color color, int x, int y, bool isPortrait)
    {
        if (!IsWarmSkinColor(color))
            return false;

        if (isPortrait)
        {
            bool face = Math.Pow((x - 32) / 18.0, 2) + Math.Pow((y - 34) / 22.0, 2) <= 1.0 && y >= 16;
            bool neck = x >= 23 && x <= 43 && y >= 43 && y <= 59;
            bool ears = (x is >= 12 and <= 20 || x is >= 44 and <= 52) && y >= 26 && y <= 42;
            return face || neck || ears;
        }

        int frameX = x % 16;
        int frameY = y % 32;
        int row = y / 32;
        bool faceZone = row switch
        {
            0 => frameX is >= 4 and <= 11 && frameY is >= 8 and <= 17,
            1 => frameX is >= 5 and <= 13 && frameY is >= 7 and <= 17,
            2 => false,
            3 => frameX is >= 2 and <= 10 && frameY is >= 7 and <= 17,
            _ => false,
        };
        bool handZone = (frameX <= 4 || frameX >= 11) && frameY is >= 13 and <= 21;
        return faceZone || handZone;
    }

    private static bool IsWarmSkinColor(Color color)
    {
        if (color.A == 0)
            return false;
        if (color.R < 70 || color.G < 35 || color.B < 18 || color.B > 155)
            return false;
        if (color.R <= color.G + 8 || color.G <= color.B + 6)
            return false;
        return color.R - color.B >= 30;
    }

    private static Color ToDarkSkin(Color color)
    {
        double luminance = color.R * 0.299 + color.G * 0.587 + color.B * 0.114;
        if (luminance > 185)
            return new Color((byte)174, (byte)118, (byte)76, color.A);
        if (luminance > 145)
            return new Color((byte)135, (byte)84, (byte)54, color.A);
        if (luminance > 100)
            return new Color((byte)92, (byte)55, (byte)38, color.A);
        if (luminance > 65)
            return new Color((byte)58, (byte)34, (byte)26, color.A);
        return new Color((byte)33, (byte)21, (byte)17, color.A);
    }

    private void RemoveNpc()
    {
        if (Context.IsWorldReady && Game1.IsMasterGame)
            this.SweepRecentWorkDebris(Game1.getFarm(), radius: 8);
        this.FlushCarried(); // never let carried items vanish with him
        if (this.npc != null)
        {
            this.npc.currentLocation?.characters.Remove(this.npc);
            this.npc = null;
        }
        this.visualFarmer = null;
        this.working = false;
        this.current = null;
        this.returningToCorner = false;
        this.summonedToPlayer = false;
        this.tasks.Clear();
        this.recentWorkSites.Clear();
    }

    private void SweepStrays()
    {
        // Never mutate the synced characters lists from a farmhand client — the removal would
        // propagate to the host and delete its live helper.
        if (!Game1.IsMasterGame)
            return;

        Utility.ForEachLocation(location =>
        {
            for (int i = location.characters.Count - 1; i >= 0; i--)
            {
                if (location.characters[i].Name == NpcName)
                    location.characters.RemoveAt(i);
            }
            return true;
        });
    }

    private void Reset()
    {
        if (Context.IsWorldReady && Game1.IsMasterGame)
        {
            this.SweepRecentWorkDebris(Game1.getFarm(), radius: 8);
            this.FlushCarried();
        }
        else
        {
            this.carried.Clear();
        }

        this.npc = null;
        this.visualFarmer = null;
        this.working = false;
        this.current = null;
        this.returningToCorner = false;
        this.summonedToPlayer = false;
        this.autoStartedToday = false;
        this.tasks.Clear();
        this.recentWorkSites.Clear();
        this.warnedChestsFull = false;
        this.presenceTicks = 0;
        this.chestJunimo.Deposit = null;
        this.chestJunimo.currentLocation = null;
    }

    // ── interaction: poke him to start (or pause) the day's work ────────────

    private void OnButtonPressed(object? sender, ButtonPressedEventArgs e)
    {
        if (!Context.IsWorldReady || this.npc == null || Game1.activeClickableMenu != null)
            return;

        if (this.IsCallButton(e.Button))
        {
            this.helper.Input.Suppress(e.Button);
            this.ShowCommandMenu();
            return;
        }

        if (!e.Button.IsActionButton() && !e.Button.IsUseToolButton())
            return;
        if (Game1.currentLocation != this.npc.currentLocation)
            return;

        Vector2 grabTile = e.Cursor.GrabTile;
        if (Game1.currentLocation.objects.ContainsKey(grabTile))
            return;

        Point clickedTile = new((int)grabTile.X, (int)grabTile.Y);
        Point npcTile = this.npc.TilePoint;
        bool onNpc = clickedTile.Equals(npcTile);
        bool playerClose = Math.Abs(Game1.player.TilePoint.X - npcTile.X) + Math.Abs(Game1.player.TilePoint.Y - npcTile.Y) <= 2;
        if (!onNpc || !playerClose)
            return;

        this.helper.Input.Suppress(e.Button);
        this.ShowCommandMenu();
    }

    /// <summary>Context-sensitive command menu: scans around the player and only offers the kinds
    /// of work that actually exist nearby, so "what will he do" is explicit instead of guessed.</summary>
    private void ShowCommandMenu()
    {
        if (this.npc == null)
            return;

        if (Game1.currentLocation is not Farm farm)
        {
            Game1.addHUDMessage(HUDMessage.ForCornerTextbox($"{this.config().HelperName}在农场待命，回农场再指挥他。"));
            return;
        }

        Point center = Game1.player.TilePoint;
        int radius = Math.Max(2, this.config().HelperWorkRadius);

        var scratch = new List<FarmTask>();
        this.AddCropTasks(scratch, farm, center, radius);
        int crops = scratch.Count;

        scratch.Clear();
        this.AddStoneTasks(scratch, farm, center, radius);
        int stones = scratch.Count;

        scratch.Clear();
        this.AddWoodTasks(scratch, farm, center, radius);
        int wood = scratch.Count;

        scratch.Clear();
        this.AddFiberTasks(scratch, farm, center, radius);
        int fiber = scratch.Count;

        scratch.Clear();
        this.AddGrassTasks(scratch, farm, center, radius);
        int grass = scratch.Count;
        int haySpace = farm.GetHayCapacity() - farm.piecesOfHay.Value;

        scratch.Clear();
        this.AddTreeTasks(scratch, farm, center, radius);
        int trees = scratch.Count;

        scratch.Clear();
        this.AddAnimalTasks(scratch, farm, center, radius);
        int animals = scratch.Count;

        scratch.Clear();
        this.AddMachineTasks(scratch, farm, center, radius);
        int machines = scratch.Count;

        var responses = new List<Response>();
        if (crops > 0)
            responses.Add(new Response("crops", $"照料附近庄稼（{crops} 处：浇水/收获/补种）"));
        if (machines > 0)
            responses.Add(new Response("machines", $"收取机器产物（{machines} 处）"));
        if (animals > 0)
            responses.Add(new Response("animals", $"照料动物（{animals} 栋畜舍）"));
        if (stones > 0)
            responses.Add(new Response("stones", $"碎石头（{stones} 处 → 石头/矿石）"));
        if (wood > 0)
            responses.Add(new Response("wood", $"清木头（{wood} 处 → 树枝/木桩）"));
        if (fiber > 0)
            responses.Add(new Response("fiber", $"除草（{fiber} 处 → 纤维）"));
        if (grass > 0)
            responses.Add(new Response("grass", $"割草存干草（{grass} 丛，筒仓余 {haySpace}）"));
        if (trees > 0)
            responses.Add(new Response("trees", $"砍附近的树（{trees} 棵）"));
        responses.Add(new Response("daily", "全农场日常巡一遍"));
        if (this.working)
            responses.Add(new Response("stop", "先停下休息"));
        responses.Add(new Response("cancel", "没事了"));

        farm.createQuestionDialogue($"让{this.config().HelperName}做什么？", responses.ToArray(),
            (Farmer _, string answer) => this.OnHelperCommand(answer, farm, center, radius));
    }

    private void OnHelperCommand(string answer, Farm farm, Point center, int radius)
    {
        if (this.npc == null || answer == "cancel")
            return;

        if (answer == "stop")
        {
            this.SweepRecentWorkDebris(farm, radius: 8);
            this.FlushCarried();
            this.tasks.Clear();
            this.working = false;
            this.returningToCorner = false;
            this.summonedToPlayer = false;
            this.npc.controller = null;
            this.npc.Halt();
            this.current = null;
            this.recentWorkSites.Clear();
            this.npc.showTextAboveHead("行，先歇会儿。");
            return;
        }

        this.tasks.Clear();
        this.current = null;
        this.npc.controller = null;
        bool nearby = true;

        switch (answer)
        {
            case "crops":
                this.AddCropTasks(this.tasks, farm, center, radius);
                break;
            case "animals":
                this.AddAnimalTasks(this.tasks, farm, center, radius);
                break;
            case "machines":
                this.AddMachineTasks(this.tasks, farm, center, radius);
                break;
            case "stones":
                this.AddStoneTasks(this.tasks, farm, center, radius);
                break;
            case "wood":
                this.AddWoodTasks(this.tasks, farm, center, radius);
                break;
            case "fiber":
                this.AddFiberTasks(this.tasks, farm, center, radius);
                break;
            case "grass":
                this.AddGrassTasks(this.tasks, farm, center, radius);
                break;
            case "trees":
                this.AddTreeTasks(this.tasks, farm, center, radius);
                break;
            case "daily":
                this.BuildTaskQueue(farm, includeDebris: this.config().HelperClearsDebris);
                nearby = false;
                break;
            default:
                return;
        }

        if (this.tasks.Count == 0)
        {
            this.npc.showTextAboveHead("没找到要干的活呀。");
            return;
        }

        this.summonedToPlayer = nearby;
        this.returningToCorner = false;
        this.working = true;

        if (nearby)
        {
            Vector2 open = Utility.recursiveFindOpenTileForCharacter(this.npc, farm, Game1.player.Tile, 8);
            if (open != Vector2.Zero && this.npc.controller == null)
                this.npc.controller = new PathFindController(this.npc, farm, new Point((int)open.X, (int)open.Y), 2);
        }

        this.npc.showTextAboveHead($"好嘞，{this.tasks.Count} 件活儿，这就去！");
        Game1.playSound("dwop");
    }

    // ── day-start scan ───────────────────────────────────────────────────────

    private void BuildTaskQueue(Farm farm, bool includeDebris = true, bool includeAnimals = true)
    {
        this.tasks.Clear();
        this.AddCropTasks(this.tasks, farm, center: null, radius: int.MaxValue);

        if (includeAnimals && this.config().HelperTendsAnimals)
            this.AddAnimalTasks(this.tasks, farm, center: null, radius: int.MaxValue);

        this.AddMachineTasks(this.tasks, farm, center: null, radius: int.MaxValue);

        if (includeDebris && this.config().HelperClearsDebris)
            this.AddDebrisTasks(this.tasks, farm, center: null, radius: int.MaxValue, includeMatureTrees: false);

        this.monitor.Log($"Helper task queue: {this.tasks.Count} tasks.", LogLevel.Trace);
    }

    /// <summary>Machines with finished output: on the farm surface (per machine) and inside farm
    /// buildings — sheds, barns, greenhouse — where he walks to the door and empties the building.</summary>
    private void AddMachineTasks(List<FarmTask> target, Farm farm, Point? center, int radius)
    {
        if (!this.config().HelperCollectsMachines)
            return;

        foreach (var pair in farm.objects.Pairs)
        {
            var tile = new Point((int)pair.Key.X, (int)pair.Key.Y);
            if (IsWithin(tile, center, radius) && IsReadyMachine(pair.Value))
                target.Add(new FarmTask(TaskKind.CollectMachine, tile));
        }

        foreach (Building building in farm.buildings)
        {
            GameLocation? indoors = building.GetIndoors();
            if (indoors == null || !indoors.objects.Values.Any(IsReadyMachine))
                continue;

            Point door = building.getPointForHumanDoor();
            if (center == null || Distance(door, center.Value) <= radius || BuildingDistance(building, center.Value) <= radius)
                target.Add(new FarmTask(TaskKind.CollectBuilding, door, LocationName: indoors.NameOrUniqueName));
        }
    }

    private static bool IsReadyMachine(StardewValley.Object obj)
    {
        return obj.readyForHarvest.Value && obj.heldObject.Value != null;
    }

    /// <summary>Crop chores (water/harvest/fertilize/plant) within a radius, budgeted by chest stock.</summary>
    private void AddCropTasks(List<FarmTask> target, Farm farm, Point? center, int radius)
    {
        bool raining = farm.IsRainingHere();
        List<Chest> chests = GetFarmChests(farm);
        var plannedTasks = new List<FarmTask>();
        var fertilizerCandidates = new List<FertilizerCandidate>();

        // Resolve materials once and queue only as many tasks as the chests can supply, so he
        // doesn't spend the afternoon pantomiming at tiles he has no seeds for.
        string? seed = this.config().HelperReplants ? FindBestSeed(chests, farm) : null;
        int seedBudget = seed != null ? chests.Sum(c => c.Items.CountId(seed)) : 0;

        foreach (var pair in farm.terrainFeatures.Pairs)
        {
            if (pair.Value is not HoeDirt dirt)
                continue;
            var tile = new Point((int)pair.Key.X, (int)pair.Key.Y);
            if (!IsWithin(tile, center, radius))
                continue;

            if (dirt.readyForHarvest())
            {
                plannedTasks.Add(new FarmTask(TaskKind.Harvest, tile));
                continue;
            }

            if (dirt.crop != null && !dirt.crop.dead.Value)
            {
                if (!raining && dirt.state.Value != HoeDirt.watered && dirt.needsWatering())
                    plannedTasks.Add(new FarmTask(TaskKind.Water, tile));

                if (this.config().HelperFertilizes
                    && string.IsNullOrEmpty(dirt.fertilizer.Value)
                    && dirt.crop.currentPhase.Value == 0
                    && !string.IsNullOrWhiteSpace(dirt.crop.netSeedIndex.Value)
                    && Crop.TryGetData(dirt.crop.netSeedIndex.Value, out CropData cropData))
                {
                    fertilizerCandidates.Add(new FertilizerCandidate(tile, dirt.crop.netSeedIndex.Value, cropData, dirt.crop));
                }
            }
            else if (dirt.crop == null && seed != null && seedBudget > 0)
            {
                plannedTasks.Add(new FarmTask(TaskKind.Plant, tile, seed));
                if (this.config().HelperFertilizes && Crop.TryGetData(seed, out CropData seedData))
                    fertilizerCandidates.Add(new FertilizerCandidate(tile, seed, seedData, Crop: null));
                seedBudget--;
            }
        }

        Dictionary<Point, string> fertilizerAssignments = this.AssignSmartFertilizers(fertilizerCandidates, chests, farm);
        foreach (FarmTask task in plannedTasks)
        {
            if (task.Kind == TaskKind.Plant
                && fertilizerAssignments.TryGetValue(task.Tile, out string? plantFertilizer)
                && plantFertilizer != null)
                target.Add(task with { FertilizerId = plantFertilizer });
            else
                target.Add(task);
        }

        foreach (FertilizerCandidate candidate in fertilizerCandidates)
        {
            if (candidate.Crop != null
                && fertilizerAssignments.TryGetValue(candidate.Tile, out string? fertilizer)
                && fertilizer != null)
                target.Add(new FarmTask(TaskKind.Fertilize, candidate.Tile, fertilizer));
        }
    }

    private bool IsCallButton(SButton button)
    {
        return Enum.TryParse(this.config().HelperCallKey, ignoreCase: true, out SButton callKey)
            && button == callKey;
    }

    /// <summary>Animal-house chores within a radius (null center = whole farm).</summary>
    private void AddAnimalTasks(List<FarmTask> target, Farm farm, Point? center, int radius)
    {
        if (!this.config().HelperTendsAnimals)
            return;

        foreach (Building building in farm.buildings)
        {
            if (building.GetIndoors() is not AnimalHouse house || !this.AnimalHouseNeedsCare(house))
                continue;

            Point door = building.getPointForHumanDoor();
            if (center == null || Distance(door, center.Value) <= radius || BuildingDistance(building, center.Value) <= radius)
                target.Add(new FarmTask(TaskKind.TendAnimals, door, LocationName: house.NameOrUniqueName));
        }
    }

    private bool AnimalHouseNeedsCare(AnimalHouse house)
    {
        if (this.GetAnimals(house).Any(animal => !animal.wasPet.Value || this.CanCollectProduce(animal)))
            return true;

        if (this.config().HelperCollectsAnimalProducts && house.objects.Values.Any(IsLooseAnimalProduct))
            return true;

        return this.CountEmptyTroughs(house) > 0;
    }

    /// <summary>All debris kinds together (used by the daily rounds). Mature trees are excluded
    /// unless asked for — felling the player's grown trees as "debris" was the old surprise.</summary>
    private void AddDebrisTasks(List<FarmTask> target, Farm farm, Point? center, int radius, bool includeMatureTrees)
    {
        this.AddStoneTasks(target, farm, center, radius);
        this.AddWoodTasks(target, farm, center, radius);
        this.AddFiberTasks(target, farm, center, radius);
        if (includeMatureTrees)
            this.AddTreeTasks(target, farm, center ?? Point.Zero, center == null ? int.MaxValue : radius);
    }

    /// <summary>Stone sources: breakable stones, small boulders, and stone/meteorite clumps.</summary>
    private void AddStoneTasks(List<FarmTask> target, Farm farm, Point? center, int radius)
    {
        foreach (var pair in farm.objects.Pairs)
        {
            var tile = new Point((int)pair.Key.X, (int)pair.Key.Y);
            if (IsWithin(tile, center, radius) && (pair.Value.IsBreakableStone() || IsSmallBoulder(pair.Value)))
                target.Add(new FarmTask(TaskKind.BreakStone, tile));
        }

        foreach (ResourceClump clump in farm.resourceClumps)
        {
            var tile = new Point((int)clump.Tile.X, (int)clump.Tile.Y);
            if (IsWithin(tile, center, radius) && !IsWoodClump(clump))
                target.Add(new FarmTask(TaskKind.BreakStone, tile));
        }
    }

    /// <summary>Wood sources: twigs, stumps/hollow logs, and small trees (seeds/sprouts).</summary>
    private void AddWoodTasks(List<FarmTask> target, Farm farm, Point? center, int radius)
    {
        foreach (var pair in farm.terrainFeatures.Pairs)
        {
            var tile = new Point((int)pair.Key.X, (int)pair.Key.Y);
            if (IsWithin(tile, center, radius) && pair.Value is Tree tree && this.IsSmallTree(tree))
                target.Add(new FarmTask(TaskKind.ChopTree, tile));
        }

        foreach (var pair in farm.objects.Pairs)
        {
            var tile = new Point((int)pair.Key.X, (int)pair.Key.Y);
            if (IsWithin(tile, center, radius) && IsTwig(pair.Value))
                target.Add(new FarmTask(TaskKind.ClearWeeds, tile)); // twig execution uses the axe
        }

        foreach (ResourceClump clump in farm.resourceClumps)
        {
            var tile = new Point((int)clump.Tile.X, (int)clump.Tile.Y);
            if (IsWithin(tile, center, radius) && IsWoodClump(clump))
                target.Add(new FarmTask(TaskKind.ChopTree, tile));
        }
    }

    /// <summary>Fiber sources: weeds.</summary>
    private void AddFiberTasks(List<FarmTask> target, Farm farm, Point? center, int radius)
    {
        foreach (var pair in farm.objects.Pairs)
        {
            var tile = new Point((int)pair.Key.X, (int)pair.Key.Y);
            if (IsWithin(tile, center, radius) && IsClearableWeed(pair.Value))
                target.Add(new FarmTask(TaskKind.ClearWeeds, tile));
        }
    }

    /// <summary>Hay source: scythe farm GRASS into the silo. Only queues roughly as many clumps as
    /// the silo can absorb, so he never mows the whole pasture for nothing.</summary>
    private void AddGrassTasks(List<FarmTask> target, Farm farm, Point? center, int radius)
    {
        if (!this.config().HelperCutsGrass)
            return;

        int space = farm.GetHayCapacity() - farm.piecesOfHay.Value;
        if (space <= 0)
            return;

        int budget = space; // ~2 hay per clump at 50% odds; stay conservative
        foreach (var pair in farm.terrainFeatures.Pairs)
        {
            if (budget <= 0)
                break;
            if (pair.Value is not Grass)
                continue;

            var tile = new Point((int)pair.Key.X, (int)pair.Key.Y);
            if (!IsWithin(tile, center, radius))
                continue;

            target.Add(new FarmTask(TaskKind.CutGrass, tile));
            budget -= 2;
        }
    }

    /// <summary>Mature trees within a radius — only offered as an explicit "砍树" command.</summary>
    private void AddTreeTasks(List<FarmTask> target, Farm farm, Point center, int radius)
    {
        foreach (var pair in farm.terrainFeatures.Pairs)
        {
            var tile = new Point((int)pair.Key.X, (int)pair.Key.Y);
            if (!IsWithin(tile, center, radius))
                continue;

            if (pair.Value is Tree tree && this.ShouldClearTree(tree) && !this.IsSmallTree(tree))
                target.Add(new FarmTask(TaskKind.ChopTree, tile));
        }
    }

    private bool IsSmallTree(Tree tree)
    {
        return tree.growthStage.Value <= 2 && !tree.tapped.Value;
    }

    private static List<Chest> GetFarmChests(Farm farm)
    {
        var chests = new List<Chest>();
        foreach (StardewValley.Object obj in farm.objects.Values)
        {
            if (obj is Chest chest && chest.playerChest.Value
                && chest.SpecialChestType is Chest.SpecialChestTypes.None or Chest.SpecialChestTypes.BigChest)
            {
                chests.Add(chest);
            }
        }
        return chests;
    }

    private Dictionary<Point, string> AssignSmartFertilizers(List<FertilizerCandidate> candidates, List<Chest> chests, Farm farm)
    {
        var assignments = new Dictionary<Point, string>();
        if (candidates.Count == 0 || chests.Count == 0)
            return assignments;

        Dictionary<string, int> stock = GetFertilizerStock(chests);
        if (stock.Count == 0)
            return assignments;

        List<FertilizerScore> scores = new();
        foreach (FertilizerCandidate candidate in candidates)
        {
            foreach (FertilizerOption fertilizer in Fertilizers)
            {
                if (!stock.ContainsKey(fertilizer.Id))
                    continue;

                float score = this.ScoreFertilizer(candidate, fertilizer, farm);
                if (score > 0.1f)
                    scores.Add(new FertilizerScore(candidate.Tile, fertilizer.Id, score));
            }
        }

        foreach (FertilizerScore score in scores.OrderByDescending(score => score.Score))
        {
            if (assignments.ContainsKey(score.Tile))
                continue;
            if (!stock.TryGetValue(score.FertilizerId, out int count) || count <= 0)
                continue;

            assignments[score.Tile] = score.FertilizerId;
            stock[score.FertilizerId] = count - 1;
        }

        return assignments;
    }

    private static Dictionary<string, int> GetFertilizerStock(List<Chest> chests)
    {
        var stock = new Dictionary<string, int>();
        foreach (FertilizerOption fertilizer in Fertilizers)
        {
            int count = chests.Sum(chest => chest.Items.CountId(fertilizer.Id));
            if (count > 0)
                stock[fertilizer.Id] = count;
        }

        return stock;
    }

    private float ScoreFertilizer(FertilizerCandidate candidate, FertilizerOption fertilizer, Farm farm)
    {
        float harvestValue = EstimateHarvestValue(candidate.CropData);
        if (harvestValue <= 0f)
            return 0f;

        int growingDaysLeft = GetRemainingAllowedGrowingDays(candidate.CropData);
        if (growingDaysLeft <= 0)
            return 0f;

        int daysToFirstHarvest = candidate.Crop != null
            ? EstimateDaysToFirstHarvest(candidate.Crop)
            : EstimateDaysToFirstHarvest(candidate.CropData);
        int normalHarvests = EstimateHarvestCount(daysToFirstHarvest, candidate.CropData.RegrowDays, growingDaysLeft);
        int fertilizerCost = GetItemPrice(fertilizer.Id);

        return fertilizer.Kind switch
        {
            FertilizerKind.Quality => ScoreQualityFertilizer(harvestValue, normalHarvests, fertilizer.Strength, fertilizerCost),
            FertilizerKind.Speed => ScoreSpeedFertilizer(candidate.CropData, harvestValue, daysToFirstHarvest, growingDaysLeft, fertilizer.Strength, fertilizerCost),
            FertilizerKind.Retaining => ScoreRetainingFertilizer(candidate.CropData, normalHarvests, daysToFirstHarvest, growingDaysLeft, fertilizer.Strength, fertilizerCost),
            _ => 0f,
        };
    }

    private static float ScoreQualityFertilizer(float harvestValue, int harvests, float qualityBoost, int fertilizerCost)
    {
        if (harvests <= 0)
            return 0f;

        // Expected value uplift from better quality. The exact vanilla curve also depends on
        // Farming level, so this deliberately stays conservative and spends deluxe fertilizer
        // on crops where even a modest quality bump is valuable.
        return harvestValue * harvests * qualityBoost - fertilizerCost;
    }

    private static float ScoreSpeedFertilizer(CropData cropData, float harvestValue, int daysToFirstHarvest, int growingDaysLeft, float speedBoost, int fertilizerCost)
    {
        int spedDays = Math.Max(1, (int)Math.Ceiling(daysToFirstHarvest * (1f - speedBoost)));
        int normalHarvests = EstimateHarvestCount(daysToFirstHarvest, cropData.RegrowDays, growingDaysLeft);
        int spedHarvests = EstimateHarvestCount(spedDays, cropData.RegrowDays, growingDaysLeft);
        int extraHarvests = spedHarvests - normalHarvests;
        if (extraHarvests <= 0)
            return 0f;

        return harvestValue * extraHarvests - fertilizerCost;
    }

    private static float ScoreRetainingFertilizer(CropData cropData, int harvests, int daysToFirstHarvest, int growingDaysLeft, float retainChance, int fertilizerCost)
    {
        if (!cropData.NeedsWatering || cropData.IsPaddyCrop || harvests <= 0)
            return 0f;

        int relevantDays = Math.Min(growingDaysLeft, Math.Max(daysToFirstHarvest, harvests * Math.Max(1, cropData.RegrowDays)));
        float savedWorkValue = relevantDays * retainChance * 2f;
        return savedWorkValue - fertilizerCost;
    }

    private static float EstimateHarvestValue(CropData cropData)
    {
        if (string.IsNullOrWhiteSpace(cropData.HarvestItemId))
            return 0f;
        if (ItemRegistry.Create(cropData.HarvestItemId, allowNull: true) is not StardewValley.Object produce)
            return 0f;

        float averageStack = (cropData.HarvestMinStack + cropData.HarvestMaxStack) / 2f;
        averageStack += Math.Max(0f, cropData.HarvestMaxIncreasePerFarmingLevel) * Game1.player.FarmingLevel / 2f;
        if (cropData.ExtraHarvestChance > 0 && cropData.ExtraHarvestChance < 1)
            averageStack += (float)(cropData.ExtraHarvestChance / (1.0 - cropData.ExtraHarvestChance));

        return produce.Price * Math.Max(1f, averageStack);
    }

    private static int EstimateDaysToFirstHarvest(CropData cropData)
    {
        return Math.Max(1, cropData.DaysInPhase.Sum());
    }

    private static int EstimateDaysToFirstHarvest(Crop crop)
    {
        // The last phaseDays entry is the 99999 "fully grown" sentinel (Crop.finalPhaseLength);
        // vanilla skips it when totalling growth days (HoeDirt.applySpeedIncreases) and so must we,
        // or every sprouted crop scores as "100k days to harvest" and never gets fertilizer.
        int days = 0;
        int phase = Math.Clamp(crop.currentPhase.Value, 0, Math.Max(0, crop.phaseDays.Count - 1));
        for (int i = phase; i < crop.phaseDays.Count - 1; i++)
            days += crop.phaseDays[i];

        days -= Math.Max(0, crop.dayOfCurrentPhase.Value);
        return Math.Max(1, days);
    }

    private static int EstimateHarvestCount(int daysToFirstHarvest, int regrowDays, int growingDaysLeft)
    {
        if (daysToFirstHarvest > growingDaysLeft)
            return 0;
        if (regrowDays <= 0)
            return 1;

        return 1 + (growingDaysLeft - daysToFirstHarvest) / regrowDays;
    }

    private static int GetRemainingAllowedGrowingDays(CropData cropData)
    {
        Season season = Game1.season;
        if (!cropData.Seasons.Contains(season))
            return 0;

        int days = 28 - Game1.dayOfMonth;
        Season next = NextSeason(season);
        for (int i = 0; i < 3 && cropData.Seasons.Contains(next); i++)
        {
            days += 28;
            next = NextSeason(next);
        }

        return Math.Max(0, days);
    }

    private static Season NextSeason(Season season)
    {
        return season switch
        {
            Season.Spring => Season.Summer,
            Season.Summer => Season.Fall,
            Season.Fall => Season.Winter,
            _ => Season.Spring,
        };
    }

    private static int GetItemPrice(string itemId)
    {
        return ItemRegistry.Create(itemId, allowNull: true) is StardewValley.Object item
            ? Math.Max(0, item.Price)
            : 0;
    }

    /// <summary>Pick the in-season seed the chests hold the most of.</summary>
    private static string? FindBestSeed(List<Chest> chests, Farm farm)
    {
        string? best = null;
        int bestCount = 0;
        var counted = new Dictionary<string, int>();
        foreach (Chest chest in chests)
        {
            foreach (Item item in chest.Items)
            {
                if (item == null || !Game1.cropData.ContainsKey(item.ItemId))
                    continue;
                counted.TryGetValue(item.ItemId, out int count);
                counted[item.ItemId] = count + item.Stack;
            }
        }
        foreach (var pair in counted)
        {
            if (pair.Value > bestCount && Crop.IsInSeason(farm, pair.Key))
            {
                best = pair.Key;
                bestCount = pair.Value;
            }
        }
        return best;
    }

    private static bool ConsumeFromChests(List<Chest> chests, string itemId, int count)
    {
        foreach (Chest chest in chests)
        {
            int available = chest.Items.CountId(itemId);
            if (available <= 0)
                continue;
            int take = Math.Min(available, count);
            chest.Items.ReduceId(itemId, take);
            count -= take;
            if (count <= 0)
                return true;
        }
        return count <= 0;
    }

    // ── per-tick state machine ───────────────────────────────────────────────

    private void OnUpdateTicked(object? sender, UpdateTickedEventArgs e)
    {
        if (!Context.IsWorldReady || !Game1.IsMasterGame || Game1.eventUp)
            return;

        // Self-heal first: whatever removed the helper, put him back.
        this.EnsurePresent();
        if (this.npc == null)
            return;

        // Vanilla NPC logic occasionally resets speed — enforce the configured value every tick.
        int wantSpeed = Math.Clamp(this.config().HelperSpeed, 2, 8);
        if (this.npc.speed != wantSpeed)
            this.npc.speed = wantSpeed;

        // Don't work while the single-player game is paused by a menu (mirrors PathFindController).
        if (Game1.activeClickableMenu != null && !Game1.IsMultiplayer)
            return;

        // Morning auto-start: begin the daily rounds without being poked.
        if (!this.working && !this.autoStartedToday && this.config().HelperAutoStartDaily && Game1.timeOfDay >= 610)
        {
            this.autoStartedToday = true;
            this.BuildTaskQueue(Game1.getFarm(), includeDebris: this.config().HelperClearsDebris);
            if (this.tasks.Count > 0)
            {
                this.working = true;
                this.returningToCorner = false;
                this.summonedToPlayer = false;
                Game1.addHUDMessage(HUDMessage.ForCornerTextbox($"{this.config().HelperName}开始今天的农活了（{this.tasks.Count} 件）。"));
            }
        }

        // The NPC only really ticks while the farm updates (a player is there) — that's fine:
        // PathFindController teleports him to the endpoint when nobody is watching anyway.
        if (!this.working)
            return;

        if (this.npc.controller != null)
        {
            // While the player is away, the farm doesn't update and nothing ticks the controller.
            // Pump it ourselves: with no farmers present it advances him straight to the endpoint,
            // so the chores keep progressing while you're out and about.
            GameLocation? npcLocation = this.npc.currentLocation;
            if (npcLocation != null && !npcLocation.farmers.Any()
                && this.npc.controller.update(Game1.currentGameTime))
            {
                this.npc.controller = null;
            }
            return; // still walking
        }

        if (this.current != null)
        {
            // Working at the tile.
            this.workTimerMs -= Game1.currentGameTime.ElapsedGameTime.Milliseconds;
            if (this.workTimerMs > 0)
                return;

            if (this.ExecuteTask(this.current))
            {
                // Multi-hit target (stump/log/boulder) not finished — stay put and keep working
                // until it's actually done instead of wandering off to another task.
                this.workTimerMs = this.GetWorkTime(this.current);
                this.workDurationMs = this.workTimerMs;
                return;
            }

            this.current = null;
            return;
        }

        if (this.returningToCorner)
        {
            if (this.config().HelperHaulsDrops)
            {
                Farm farm = Game1.getFarm();
                this.SweepRecentWorkDebris(farm, radius: 8);
                if (this.TryPlanDelivery(farm))
                {
                    this.returningToCorner = false;
                    this.StartNextTask();
                    return;
                }
            }

            this.working = false;
            this.returningToCorner = false;
            this.summonedToPlayer = false;
            this.recentWorkSites.Clear();
            this.npc.faceDirection(2);
            this.npc.showTextAboveHead("活儿干完啦！");
            Game1.addHUDMessage(HUDMessage.ForCornerTextbox($"{this.config().HelperName}把今天的农活干完了。"));
            return;
        }

        this.StartNextTask();
    }

    private void StartNextTask()
    {
        Farm farm = Game1.getFarm();

        while (this.tasks.Count > 0)
        {
            // Nearest-first so he works like a person, not a teleporting robot.
            Point from = this.npc!.TilePoint;
            int bestIndex = 0;
            int bestDistance = int.MaxValue;
            for (int i = 0; i < this.tasks.Count; i++)
            {
                int distance = Math.Abs(this.tasks[i].Tile.X - from.X) + Math.Abs(this.tasks[i].Tile.Y - from.Y);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    bestIndex = i;
                }
            }

            FarmTask task = this.tasks[bestIndex];
            this.tasks.RemoveAt(bestIndex);

            if (!this.IsTaskStillValid(task, farm))
                continue;

            foreach (Point stand in this.GetStandTiles(task, farm))
            {
                var controller = new PathFindController(this.npc, farm, stand, FacingToward(stand, task.Tile));
                if (controller.pathToEndPoint == null)
                    continue;

                this.npc.controller = controller;
                this.current = task;
                this.workTimerMs = this.GetWorkTime(task);
                this.workDurationMs = this.workTimerMs;
                return;
            }
            // No adjacent tile reachable — drop the task.
        }

        // Queue empty: sweep around recent work sites first (a felled tree drops its wood a beat
        // later and a few tiles away), then haul everything to chests before knocking off.
        if (this.config().HelperHaulsDrops)
        {
            this.SweepRecentWorkDebris(farm, radius: 8);

            if (this.TryPlanDelivery(farm))
            {
                this.StartNextTask(); // re-enter: the Deliver task is now in the queue
                return;
            }
        }

        // Daily work returns to the corner; called work checks back near the player.
        Point returnTile = this.corner;
        if (this.summonedToPlayer)
        {
            Vector2 open = Utility.recursiveFindOpenTileForCharacter(this.npc!, farm, Game1.player.Tile, 8);
            if (open != Vector2.Zero)
                returnTile = new Point((int)open.X, (int)open.Y);
        }
        if (returnTile == Point.Zero)
            returnTile = this.corner;

        var home = new PathFindController(this.npc!, farm, returnTile, 2);
        if (home.pathToEndPoint != null)
            this.npc!.controller = home;
        this.returningToCorner = true;
    }

    private bool IsTaskStillValid(FarmTask task, Farm farm)
    {
        Vector2 tile = new(task.Tile.X, task.Tile.Y);
        if (task.Kind is TaskKind.Water or TaskKind.Harvest or TaskKind.Fertilize or TaskKind.Plant)
        {
            if (!farm.terrainFeatures.TryGetValue(tile, out TerrainFeature? feature) || feature is not HoeDirt dirt)
                return false;

            return task.Kind switch
            {
                TaskKind.Water => dirt.crop != null && !dirt.crop.dead.Value && dirt.state.Value != HoeDirt.watered && dirt.needsWatering(),
                TaskKind.Harvest => dirt.readyForHarvest(),
                TaskKind.Fertilize => dirt.crop != null && string.IsNullOrEmpty(dirt.fertilizer.Value) && dirt.crop.currentPhase.Value == 0,
                TaskKind.Plant => dirt.crop == null,
                _ => false,
            };
        }

        return task.Kind switch
        {
            TaskKind.ChopTree => this.HasClearableTreeOrWoodClump(farm, tile),
            TaskKind.BreakStone => this.HasBreakableStone(farm, tile),
            TaskKind.ClearWeeds => farm.objects.TryGetValue(tile, out StardewValley.Object? obj) && (IsClearableWeed(obj) || IsTwig(obj)),
            TaskKind.TendAnimals => task.LocationName != null
                && Game1.getLocationFromName(task.LocationName) is AnimalHouse house
                && this.AnimalHouseNeedsCare(house),
            TaskKind.Deliver => farm.objects.TryGetValue(tile, out StardewValley.Object? chestObj) && chestObj is Chest,
            TaskKind.CollectMachine => farm.objects.TryGetValue(tile, out StardewValley.Object? machineObj) && IsReadyMachine(machineObj),
            TaskKind.CollectBuilding => task.LocationName != null
                && Game1.getLocationFromName(task.LocationName) is GameLocation indoors
                && indoors.objects.Values.Any(IsReadyMachine),
            TaskKind.CutGrass => farm.terrainFeatures.TryGetValue(tile, out TerrainFeature? grassFeature)
                && grassFeature is Grass
                && farm.GetHayCapacity() - farm.piecesOfHay.Value > 0,
            _ => false,
        };
    }

    /// <summary>Execute the task once. Returns true when the target needs MORE work cycles at the
    /// same spot (multi-hit stumps/logs/boulders) — the caller then stays put and repeats.</summary>
    private bool ExecuteTask(FarmTask task)
    {
        Farm farm = Game1.getFarm();
        if (task.Kind is TaskKind.ChopTree or TaskKind.BreakStone or TaskKind.ClearWeeds or TaskKind.TendAnimals
            or TaskKind.Deliver or TaskKind.CollectMachine or TaskKind.CollectBuilding or TaskKind.CutGrass)
        {
            return this.ExecuteUtilityTask(task, farm);
        }

        if (!farm.terrainFeatures.TryGetValue(new Vector2(task.Tile.X, task.Tile.Y), out TerrainFeature? feature) || feature is not HoeDirt dirt)
            return false;

        List<Chest> chests = GetFarmChests(farm);
        var tileVector = new Vector2(task.Tile.X, task.Tile.Y);

        switch (task.Kind)
        {
            case TaskKind.Water:
            {
                // Same effect as the watering can (with vanilla's beach-forage exclusion).
                if (dirt.crop == null || !dirt.crop.forageCrop.Value || dirt.crop.whichForageCrop.Value != "2")
                    dirt.state.Value = HoeDirt.watered;
                farm.playSound("wateringCan");
                Game1.Multiplayer.broadcastSprites(farm, new TemporaryAnimatedSprite(
                    13, tileVector * 64f, Color.White, 10, Game1.random.NextBool(), 70f, 0, 64,
                    (tileVector.Y * 64f + 32f) / 10000f - 0.01f));
                break;
            }

            case TaskKind.Harvest:
            {
                if (dirt.crop == null)
                    return false;

                string seedId = dirt.crop.netSeedIndex.Value ?? "";
                string harvestId = dirt.crop.indexOfHarvest.Value ?? "";
                bool regrows = dirt.crop.RegrowsAfterHarvest();

                // Crop.harvest with a junimoHarvester computes vanilla quality/stack/extra drops
                // and routes every item through our ChestJunimo into the farm chests — preferring
                // a chest that already holds the same item, nearest first.
                this.chestJunimo.currentLocation = farm;
                this.chestJunimo.Position = this.npc!.Position;
                List<DepositChest> depositChests = GetDepositChests(farm);
                this.chestJunimo.Deposit = item => SmartDeposit(item, depositChests, task.Tile);

                bool spent = dirt.crop.harvest(task.Tile.X, task.Tile.Y, dirt, this.chestJunimo);
                bool succeeded = spent || !dirt.readyForHarvest(); // regrow crops return false on a successful pick

                // Vanilla skips the farming XP when a junimo harvests — re-grant it to the player
                // using the same formula as Crop.harvest (16 * ln(0.018 * price + 1)).
                if (succeeded && harvestId.Length > 0
                    && ItemRegistry.Create(harvestId, allowNull: true) is StardewValley.Object produce)
                {
                    int xp = (int)Math.Round(16.0 * Math.Log(0.018 * produce.Price + 1.0, Math.E));
                    if (xp > 0)
                        Game1.MasterPlayer.gainExperience(0, xp);
                }

                if (spent)
                {
                    dirt.destroyCrop(farm.farmers.Any());

                    if (!regrows && this.config().HelperReplants && seedId.Length > 0
                        && chests.Any(c => c.Items.CountId(seedId) > 0))
                    {
                        this.tasks.Add(new FarmTask(TaskKind.Plant, task.Tile, seedId));
                    }
                }
                else if (succeeded)
                {
                    this.QueueWaterTaskIfNeeded(farm, dirt, task.Tile);
                }
                break;
            }

            case TaskKind.Fertilize:
            {
                if (task.ItemId != null && chests.Any(c => c.Items.CountId(task.ItemId) > 0)
                    && dirt.plant(task.ItemId, Game1.MasterPlayer, isFertilizer: true))
                {
                    ConsumeFromChests(chests, task.ItemId, 1);
                }
                break;
            }

            case TaskKind.Plant:
            {
                string? seed = task.ItemId;
                if (seed == null || !Crop.TryGetData(seed, out _) || !Crop.IsInSeason(farm, seed))
                    return false;
                if (!chests.Any(c => c.Items.CountId(seed) > 0))
                    return false;

                if (task.FertilizerId != null
                    && string.IsNullOrEmpty(dirt.fertilizer.Value)
                    && chests.Any(c => c.Items.CountId(task.FertilizerId) > 0)
                    && dirt.plant(task.FertilizerId, Game1.MasterPlayer, isFertilizer: true))
                {
                    ConsumeFromChests(chests, task.FertilizerId, 1);
                }

                // Plant manually: HoeDirt.plant's seed branch validates against the FARMER's
                // current location, which is wrong for NPC-driven planting.
                dirt.crop = new Crop(seed, task.Tile.X, task.Tile.Y, farm);
                dirt.applySpeedIncreases(Game1.MasterPlayer);
                farm.playSound("dirtyHit");
                Game1.stats.SeedsSown++;
                ConsumeFromChests(chests, seed, 1);
                this.QueueWaterTaskIfNeeded(farm, dirt, task.Tile);
                break;
            }
        }

        return false;
    }

    /// <summary>Returns true when the target still needs more work cycles at the same spot.</summary>
    private bool ExecuteUtilityTask(FarmTask task, Farm farm)
    {
        Vector2 tile = new(task.Tile.X, task.Tile.Y);
        bool stay = false;

        if (task.Kind is TaskKind.ChopTree or TaskKind.BreakStone or TaskKind.ClearWeeds
            && this.config().HelperHaulsDrops && !this.recentWorkSites.Any(site => site.Tile == task.Tile))
        {
            this.RememberWorkSite(farm, task.Tile);
        }

        switch (task.Kind)
        {
            case TaskKind.ChopTree:
            {
                bool progressed = this.ChopAt(farm, tile);
                if (progressed && this.HasClearableTreeOrWoodClump(farm, tile))
                    stay = true; // multi-hit target: stay until it's gone
                else if (!progressed)
                    this.monitor.Log($"Helper axe too weak for obstacle at {tile}; skipping.", LogLevel.Trace);
                break;
            }

            case TaskKind.BreakStone:
            {
                bool progressed = this.BreakStoneAt(farm, tile);
                if (progressed && this.HasBreakableStone(farm, tile))
                    stay = true;
                else if (!progressed)
                    this.monitor.Log($"Helper pickaxe too weak for obstacle at {tile}; skipping.", LogLevel.Trace);
                break;
            }

            case TaskKind.ClearWeeds:
                this.ClearWeedsAt(farm, tile);
                break;

            case TaskKind.TendAnimals:
                if (task.LocationName != null && Game1.getLocationFromName(task.LocationName) is AnimalHouse house)
                    this.TendAnimalHouse(house);
                break;

            case TaskKind.Deliver:
                this.ExecuteDelivery(farm, task.Tile);
                break;

            case TaskKind.CollectMachine:
                if (farm.objects.TryGetValue(tile, out StardewValley.Object? farmMachine) && IsReadyMachine(farmMachine))
                    this.CollectMachineOutput(farmMachine, farm);
                break;

            case TaskKind.CutGrass:
            {
                // The player clears a whole clump in one visible swing (the swing's animation
                // frames hit the same grass several times), so do the same: burst-cut the clump
                // in this one work cycle. Each cut rolls hay into the silo exactly like vanilla.
                for (int i = 0; i < 8; i++)
                {
                    if (farm.GetHayCapacity() - farm.piecesOfHay.Value <= 0)
                        break;
                    if (!farm.terrainFeatures.TryGetValue(tile, out TerrainFeature? grassFeature) || grassFeature is not Grass grass)
                        break;

                    if (grass.performToolAction(this.GetHelperScythe(), 0, tile))
                    {
                        farm.terrainFeatures.Remove(tile);
                        break;
                    }
                }
                break;
            }

            case TaskKind.CollectBuilding:
            {
                if (task.LocationName == null || Game1.getLocationFromName(task.LocationName) is not GameLocation indoors)
                    break;

                int collected = 0;
                foreach (StardewValley.Object machine in indoors.objects.Values.ToArray())
                {
                    if (IsReadyMachine(machine) && this.CollectMachineOutput(machine, indoors))
                        collected++;
                }
                if (collected > 0)
                    Game1.addHUDMessage(HUDMessage.ForCornerTextbox($"{this.config().HelperName}收取了 {indoors.DisplayName ?? task.LocationName} 里 {collected} 台机器的产物。"));
                break;
            }
        }

        // Scoop up whatever his work just knocked loose around this tile (skip while staying:
        // one sweep when the target finally breaks is enough).
        if (!stay && this.config().HelperHaulsDrops && task.Kind is TaskKind.ChopTree or TaskKind.BreakStone or TaskKind.ClearWeeds)
            this.CollectNearbyDebris(farm, task.Tile, radius: 8, this.GetExistingDebrisAtWorkSite(task.Tile));

        return stay;
    }

    private void QueueWaterTaskIfNeeded(Farm farm, HoeDirt dirt, Point tile)
    {
        if (farm.IsRainingHere() || dirt.crop == null || dirt.crop.dead.Value || dirt.state.Value == HoeDirt.watered || !dirt.needsWatering())
            return;
        if (this.tasks.Any(task => task.Kind == TaskKind.Water && task.Tile == tile))
            return;

        this.tasks.Add(new FarmTask(TaskKind.Water, tile));
    }

    /// <summary>Collect a machine's finished output into chests, mirroring the vanilla
    /// Object.checkForAction harvest path: RecalculateOnCollect, stat/XP grants, state reset,
    /// OutputCollected auto-restart (crystalarium), and tapper rebinding.</summary>
    private bool CollectMachineOutput(StardewValley.Object machine, GameLocation location)
    {
        StardewValley.Object? output = machine.heldObject.Value;
        if (output == null)
            return false;

        MachineData? machineData = machine.GetMachineData();

        // Some outputs re-roll on collect (vanilla: lastOutputRuleId + RecalculateOnCollect).
        if (machineData != null && machine.lastOutputRuleId.Value != null)
        {
            MachineOutputRule? recalcRule = machineData.OutputRules?.FirstOrDefault(p => p.Id == machine.lastOutputRuleId.Value);
            if (recalcRule != null && recalcRule.RecalculateOnCollect)
            {
                machine.heldObject.Value = null;
                machine.OutputMachine(machineData, recalcRule, machine.lastInputItem.Value, Game1.MasterPlayer, location, probe: false, heldObjectOnly: true);
                if (machine.heldObject.Value != null)
                    output = machine.heldObject.Value;
                else
                    machine.heldObject.Value = output;
            }
        }

        machine.heldObject.Value = null;
        machine.readyForHarvest.Value = false;
        machine.showNextIndex.Value = false;
        machine.ResetParentSheetIndex();

        MachineDataUtility.UpdateStats(machineData?.StatsToIncrementWhenHarvested, output, output.Stack);

        // Vanilla grants harvest XP to the collector — credit the player.
        if (machineData?.ExperienceGainOnHarvest != null)
        {
            string[] parts = machineData.ExperienceGainOnHarvest.Split(' ');
            for (int i = 0; i + 1 < parts.Length; i += 2)
            {
                int skill = Farmer.getSkillNumberFromName(parts[i]);
                if (skill != -1 && int.TryParse(parts[i + 1], out int amount))
                    Game1.MasterPlayer.gainExperience(skill, amount);
            }
        }

        // Machines like the crystalarium immediately start their next batch on collection.
        if (MachineDataUtility.TryGetMachineOutputRule(machine, machineData, MachineOutputTrigger.OutputCollected, output.getOne(), Game1.MasterPlayer, location, out MachineOutputRule restartRule, out _, out _, out _))
            machine.OutputMachine(machineData, restartRule, machine.lastInputItem.Value, Game1.MasterPlayer, location, probe: false);

        // Tappers stay attached to their tree and queue the next product.
        if (machine.IsTapper() && location.terrainFeatures.TryGetValue(machine.TileLocation, out TerrainFeature? feature) && feature is Tree tree)
            tree.UpdateTapperProduct(machine, output);

        // Route the output into chests: prefer one already holding the same item, nearest first.
        var machineTile = new Point((int)machine.TileLocation.X, (int)machine.TileLocation.Y);
        Item? remaining = SmartDeposit(output, GetDepositChests(location), machineTile);
        if (remaining != null)
            Game1.createItemDebris(remaining, machine.TileLocation * 64f, -1, location);

        location.playSound("coin");
        return true;
    }

    private sealed record DepositChest(Chest Chest, Point Tile, int BaseCost);

    /// <summary>Chests usable for storage: the given location's own chests first (cost 0 — shed
    /// chests beside the kegs), then the farm's chests at a distance penalty.</summary>
    private static List<DepositChest> GetDepositChests(GameLocation location)
    {
        var result = new List<DepositChest>();
        void Collect(GameLocation loc, int baseCost)
        {
            foreach (var pair in loc.objects.Pairs)
            {
                if (pair.Value is Chest chest && chest.playerChest.Value
                    && chest.SpecialChestType is Chest.SpecialChestTypes.None or Chest.SpecialChestTypes.BigChest
                    && !result.Any(entry => ReferenceEquals(entry.Chest, chest)))
                {
                    result.Add(new DepositChest(chest, new Point((int)pair.Key.X, (int)pair.Key.Y), baseCost));
                }
            }
        }

        Collect(location, 0);
        if (location is not Farm)
            Collect(Game1.getFarm(), 500);
        return result;
    }

    /// <summary>Store an item the way the player asked for: chests that ALREADY hold the same item
    /// win (nearest first), then the nearest chest with a free slot. Returns the leftover.</summary>
    private static Item? SmartDeposit(Item item, List<DepositChest> chests, Point from)
    {
        Item? remaining = item;

        // Pass 1: chests already holding this exact item.
        foreach (DepositChest entry in chests
                     .Where(entry => entry.Chest.Items.CountId(item.QualifiedItemId) > 0)
                     .OrderBy(entry => entry.BaseCost + Distance(from, entry.Tile))
                     .ToList())
        {
            remaining = entry.Chest.addItem(remaining);
            if (remaining == null)
                return null;
        }

        // Pass 2: nearest chests with room.
        foreach (DepositChest entry in chests
                     .Where(entry => entry.Chest.Items.CountItemStacks() < entry.Chest.GetActualCapacity())
                     .OrderBy(entry => entry.BaseCost + Distance(from, entry.Tile))
                     .ToList())
        {
            remaining = entry.Chest.addItem(remaining);
            if (remaining == null)
                return null;
        }

        return remaining;
    }

    // ── drop hauling: pick up his own debris and walk it to a sensible chest ──

    private void RememberWorkSite(Farm farm, Point tile)
    {
        this.recentWorkSites.Add(new WorkDebrisSite(tile, farm.debris.ToHashSet()));
    }

    private HashSet<Debris>? GetExistingDebrisAtWorkSite(Point tile)
    {
        return this.recentWorkSites.FirstOrDefault(site => site.Tile == tile)?.ExistingDebris;
    }

    private void CollectNearbyDebris(Farm farm, Point tile, int radius, HashSet<Debris>? existingDebris = null)
    {
        var center = new Vector2(tile.X * 64 + 32, tile.Y * 64 + 32);
        float maxDistance = radius * 64f;
        bool pickedAny = false;

        for (int i = farm.debris.Count - 1; i >= 0; i--)
        {
            Debris debris = farm.debris[i];
            if (existingDebris?.Contains(debris) == true)
                continue;
            if (debris.Chunks.Count == 0)
                continue;
            if (!debris.Chunks.Any(chunk => Vector2.Distance(chunk.position.Value, center) <= maxDistance))
                continue;

            List<Item> items = ExtractDebrisItems(debris);
            if (items.Count == 0)
                continue;

            farm.debris.RemoveAt(i);
            foreach (Item item in items)
                this.AddToCarried(item);
            pickedAny = true;
        }

        if (pickedAny)
            farm.playSound("pickUpItem");
    }

    private void SweepRecentWorkDebris(Farm farm, int radius)
    {
        foreach (WorkDebrisSite site in this.recentWorkSites)
            this.CollectNearbyDebris(farm, site.Tile, radius, site.ExistingDebris);
    }

    /// <summary>Convert a debris entry into the items a player would get from collecting it.</summary>
    private static List<Item> ExtractDebrisItems(Debris debris)
    {
        var result = new List<Item>();

        if (debris.item != null)
        {
            return result;
        }

        Debris.DebrisType type = debris.debrisType.Value;
        if (type is Debris.DebrisType.ARCHAEOLOGY or Debris.DebrisType.LETTERS
            or Debris.DebrisType.NUMBERS or Debris.DebrisType.SPRITECHUNKS)
        {
            return result; // visual-only or player-specific debris: leave for the player
        }
        if (type == Debris.DebrisType.CHUNKS && debris.chunkType.Value == 8)
            return result; // coins

        string? itemId = debris.itemId.Value;
        if (string.IsNullOrEmpty(itemId))
            return result;

        int count = Math.Max(1, debris.Chunks.Count);
        Item? item = ItemRegistry.Create(itemId, count, debris.itemQuality, allowNull: true);
        if (item != null)
            result.Add(item);
        return result;
    }

    private void AddToCarried(Item item)
    {
        foreach (Item existing in this.carried)
        {
            if (existing.canStackWith(item))
            {
                int leftover = existing.addToStack(item);
                if (leftover <= 0)
                    return;
                item.Stack = leftover;
            }
        }
        this.carried.Add(item);
    }

    /// <summary>Plan the next chest run: per item, prefer the nearest chest already holding that
    /// item; otherwise the nearest chest with room. Queue one Deliver task for the closest pick.</summary>
    private bool TryPlanDelivery(Farm farm)
    {
        if (this.carried.Count == 0)
            return false;

        List<(Chest chest, Point tile)> chests = GetFarmChestsWithTiles(farm);
        if (chests.Count == 0)
            return false;

        Point from = this.npc!.TilePoint;
        Point? bestTile = null;
        int bestScore = int.MaxValue;

        foreach (Item item in this.carried)
        {
            if (PickChestForItem(chests, item, from) is (_, Point tile, int score) && score < bestScore)
            {
                bestScore = score;
                bestTile = tile;
            }
        }

        if (bestTile == null)
        {
            if (!this.warnedChestsFull)
            {
                this.warnedChestsFull = true;
                this.npc.showTextAboveHead("箱子都满了，捡的东西先背着了。");
            }
            return false;
        }

        this.tasks.Add(new FarmTask(TaskKind.Deliver, bestTile.Value));
        return true;
    }

    /// <summary>Best chest for one item: chests that already hold the item beat chests that don't;
    /// ties broken by distance. Returns null when no chest holds it and none has room.</summary>
    private static (Chest chest, Point tile, int score)? PickChestForItem(List<(Chest chest, Point tile)> chests, Item item, Point from)
    {
        (Chest chest, Point tile, int score)? best = null;

        foreach ((Chest chest, Point tile) in chests)
        {
            bool holdsSame = chest.Items.CountId(item.QualifiedItemId) > 0;
            bool hasRoom = chest.Items.CountItemStacks() < chest.GetActualCapacity();
            if (!holdsSame && !hasRoom)
                continue;

            int score = (holdsSame ? 0 : 100_000) + Distance(from, tile);
            if (best == null || score < best.Value.score)
                best = (chest, tile, score);
        }

        return best;
    }

    private void ExecuteDelivery(Farm farm, Point chestTile)
    {
        if (!farm.objects.TryGetValue(new Vector2(chestTile.X, chestTile.Y), out StardewValley.Object? obj) || obj is not Chest chest)
            return;

        List<(Chest chest, Point tile)> chests = GetFarmChestsWithTiles(farm);
        Point from = this.npc!.TilePoint;
        int deposited = 0;

        for (int i = this.carried.Count - 1; i >= 0; i--)
        {
            Item item = this.carried[i];
            (Chest chest, Point tile, int score)? pick = PickChestForItem(chests, item, from);
            if (pick == null || !ReferenceEquals(pick.Value.chest, chest))
                continue; // this item belongs to a different chest — a later Deliver run handles it

            Item? leftover = chest.addItem(item);
            if (leftover == null)
            {
                this.carried.RemoveAt(i);
                deposited += item.Stack;
            }
            else
            {
                this.carried[i] = leftover;
                deposited += Math.Max(0, item.Stack - leftover.Stack);
            }
        }

        // Safety net: if the matching rules deposited nothing (edge cases like re-planning from a
        // different position), force items into THIS chest so he can never loop at it forever.
        if (deposited == 0)
        {
            for (int i = this.carried.Count - 1; i >= 0; i--)
            {
                Item item = this.carried[i];
                Item? leftover = chest.addItem(item);
                if (leftover == null)
                {
                    this.carried.RemoveAt(i);
                    deposited += item.Stack;
                }
                else if (leftover.Stack < item.Stack)
                {
                    this.carried[i] = leftover;
                    deposited += item.Stack - leftover.Stack;
                }
            }
        }

        if (deposited > 0)
        {
            farm.playSound("Ship");
            this.warnedChestsFull = false;
        }
    }

    private static List<(Chest chest, Point tile)> GetFarmChestsWithTiles(Farm farm)
    {
        var chests = new List<(Chest, Point)>();
        foreach (var pair in farm.objects.Pairs)
        {
            if (pair.Value is Chest chest && chest.playerChest.Value
                && chest.SpecialChestType is Chest.SpecialChestTypes.None or Chest.SpecialChestTypes.BigChest)
            {
                chests.Add((chest, new Point((int)pair.Key.X, (int)pair.Key.Y)));
            }
        }
        return chests;
    }

    /// <summary>Dump everything he's carrying into chests (ignoring item matching), dropping any
    /// overflow at his feet — called before he's removed so items can never be lost.</summary>
    private void FlushCarried()
    {
        if (this.carried.Count == 0)
            return;

        if (!Context.IsWorldReady)
        {
            this.carried.Clear();
            return;
        }

        Farm farm = Game1.getFarm();
        List<Chest> chests = GetFarmChests(farm);
        Vector2 dropPosition = this.npc?.Position ?? this.corner.ToVector2() * 64f;

        for (int i = this.carried.Count - 1; i >= 0; i--)
        {
            Item? remaining = this.carried[i];
            foreach (Chest chest in chests)
            {
                remaining = chest.addItem(remaining);
                if (remaining == null)
                    break;
            }

            if (remaining != null)
                Game1.createItemDebris(remaining, dropPosition, -1, farm);

            this.carried.RemoveAt(i);
        }
    }

    private bool ChopAt(Farm farm, Vector2 tile)
    {
        Tool axe = this.GetHelperAxe();

        if (farm.terrainFeatures.TryGetValue(tile, out TerrainFeature? feature) && feature is Tree tree)
        {
            if (tree.performToolAction(axe, 0, tile))
                farm.terrainFeatures.Remove(tile);
            return true; // any axe damages a tree
        }

        if (farm.objects.TryGetValue(tile, out StardewValley.Object? obj) && IsTwig(obj) && obj.performToolAction(axe))
        {
            obj.performRemoveAction();
            farm.objects.Remove(tile);
            return true;
        }

        for (int i = farm.resourceClumps.Count - 1; i >= 0; i--)
        {
            ResourceClump clump = farm.resourceClumps[i];
            if (!clump.occupiesTile((int)tile.X, (int)tile.Y) || !IsWoodClump(clump))
                continue;

            float healthBefore = clump.health.Value;
            if (clump.performToolAction(axe, 1, clump.Tile))
            {
                farm.resourceClumps.RemoveAt(i);
                return true;
            }
            return clump.health.Value < healthBefore; // unchanged = axe level too low
        }

        return false;
    }

    private bool BreakStoneAt(Farm farm, Vector2 tile)
    {
        Tool pickaxe = this.GetHelperPickaxe();

        if (farm.objects.TryGetValue(tile, out StardewValley.Object? obj))
        {
            if (obj.IsBreakableStone())
            {
                if (obj.performToolAction(pickaxe))
                {
                    TemporaryAnimatedSprite sprite = new(47, tile * 64f, Color.Gray, 10, flipped: false, 80f);
                    Game1.Multiplayer.broadcastSprites(farm, sprite);
                    farm.OnStoneDestroyed(obj.ItemId, (int)tile.X, (int)tile.Y, Game1.MasterPlayer);
                    obj.performRemoveAction();
                    farm.objects.Remove(tile);
                    Game1.stats.RocksCrushed++;
                }
                return true; // plain stones break with any pickaxe
            }

            if (IsSmallBoulder(obj))
            {
                obj.MinutesUntilReady -= Math.Max(1, pickaxe.UpgradeLevel + 1);
                farm.playSound("hammer", tile);
                obj.shakeTimer = 190;
                if (obj.MinutesUntilReady <= 0)
                    farm.removeObject(tile, showDestroyedObject: false);
                return true; // we decrement manually, so this always progresses
            }
        }

        for (int i = farm.resourceClumps.Count - 1; i >= 0; i--)
        {
            ResourceClump clump = farm.resourceClumps[i];
            if (!clump.occupiesTile((int)tile.X, (int)tile.Y) || IsWoodClump(clump))
                continue;

            float healthBefore = clump.health.Value;
            if (clump.performToolAction(pickaxe, 1, clump.Tile))
            {
                farm.resourceClumps.RemoveAt(i);
                return true;
            }
            return clump.health.Value < healthBefore; // unchanged = pickaxe level too low
        }

        return false;
    }

    private void ClearWeedsAt(Farm farm, Vector2 tile)
    {
        if (!farm.objects.TryGetValue(tile, out StardewValley.Object? obj))
            return;

        Tool tool = IsTwig(obj) ? this.GetHelperAxe() : this.GetHelperScythe();
        if (obj.performToolAction(tool))
        {
            obj.performRemoveAction();
            farm.objects.Remove(tile);
        }
    }

    private void TendAnimalHouse(AnimalHouse house)
    {
        List<DepositChest> chests = GetDepositChests(house);
        Point from = this.npc?.TilePoint ?? Point.Zero;
        int fed = this.FeedAnimals(house);
        int petted = 0;
        int collected = 0;
        int looseCollected = this.config().HelperCollectsAnimalProducts ? this.CollectLooseAnimalProducts(house, chests, from) : 0;

        foreach (FarmAnimal animal in this.GetAnimals(house))
        {
            if (!animal.wasPet.Value)
            {
                animal.pet(Game1.MasterPlayer, is_auto_pet: true);
                petted++;
            }

            if (this.CollectProduce(animal, chests, from))
                collected++;
        }

        if (petted > 0 || fed > 0 || collected > 0 || looseCollected > 0)
            Game1.addHUDMessage(HUDMessage.ForCornerTextbox($"{this.config().HelperName}照料了 {petted} 只动物，补了 {fed} 份草料，收了 {collected + looseCollected} 份产物。"));
    }

    private IEnumerable<Point> GetStandTiles(FarmTask task, Farm farm)
    {
        if (task.Kind == TaskKind.TendAnimals)
        {
            foreach (Point tile in AdjacentTiles(task.Tile))
                yield return tile;
            yield break;
        }

        if (task.Kind is TaskKind.ChopTree or TaskKind.BreakStone)
        {
            Rectangle area = this.GetTargetTileArea(farm, task);
            for (int x = area.Left; x < area.Right; x++)
            {
                yield return new Point(x, area.Bottom);
                yield return new Point(x, area.Top - 1);
            }
            for (int y = area.Top; y < area.Bottom; y++)
            {
                yield return new Point(area.Left - 1, y);
                yield return new Point(area.Right, y);
            }
            yield break;
        }

        foreach (Point tile in AdjacentTiles(task.Tile))
            yield return tile;
    }

    private Rectangle GetTargetTileArea(Farm farm, FarmTask task)
    {
        Vector2 tile = new(task.Tile.X, task.Tile.Y);
        foreach (ResourceClump clump in farm.resourceClumps)
        {
            if (clump.occupiesTile(task.Tile.X, task.Tile.Y))
                return new Rectangle((int)clump.Tile.X, (int)clump.Tile.Y, clump.width.Value, clump.height.Value);
        }
        return new Rectangle(task.Tile.X, task.Tile.Y, 1, 1);
    }

    private int GetWorkTime(FarmTask task)
    {
        int baseMs = task.Kind switch
        {
            TaskKind.ChopTree or TaskKind.BreakStone => 650,
            TaskKind.ClearWeeds => 420,
            TaskKind.CutGrass => 300,
            TaskKind.TendAnimals => 1000,
            TaskKind.Deliver => 350,
            TaskKind.CollectMachine => 400,
            TaskKind.CollectBuilding => 800,
            _ => 900,
        };
        float multiplier = Math.Max(0.25f, this.config().HelperWorkSpeedMultiplier);
        return Math.Max(120, (int)(baseMs / multiplier));
    }

    // Helper tools are pinned at iridium: matching the player's tool level made the GAME spam its
    // "your axe isn't strong enough" red message on every blocked swing, which was just annoying.
    private Tool GetHelperAxe()
    {
        this.helperAxe ??= new Axe { UpgradeLevel = 4, lastUser = Game1.MasterPlayer };
        this.helperAxe.lastUser = Game1.MasterPlayer;
        this.helperAxe.swingTicker++; // resource clumps reject repeat hits from the same "swing"
        return this.helperAxe;
    }

    private Tool GetHelperPickaxe()
    {
        this.helperPickaxe ??= new Pickaxe { UpgradeLevel = 4, lastUser = Game1.MasterPlayer };
        this.helperPickaxe.lastUser = Game1.MasterPlayer;
        this.helperPickaxe.swingTicker++;
        return this.helperPickaxe;
    }

    private Tool GetHelperScythe()
    {
        this.helperScythe ??= new MeleeWeapon("47") { lastUser = Game1.MasterPlayer };
        this.helperScythe.lastUser = Game1.MasterPlayer;
        this.helperScythe.swingTicker++;
        return this.helperScythe;
    }

    private bool HasClearableTreeOrWoodClump(Farm farm, Vector2 tile)
    {
        if (farm.terrainFeatures.TryGetValue(tile, out TerrainFeature? feature) && feature is Tree tree)
            return this.ShouldClearTree(tree);

        if (farm.objects.TryGetValue(tile, out StardewValley.Object? obj) && IsTwig(obj))
            return true;

        return farm.resourceClumps.Any(clump => clump.occupiesTile((int)tile.X, (int)tile.Y) && IsWoodClump(clump));
    }

    private bool HasBreakableStone(Farm farm, Vector2 tile)
    {
        if (farm.objects.TryGetValue(tile, out StardewValley.Object? obj) && (obj.IsBreakableStone() || IsSmallBoulder(obj)))
            return true;

        return farm.resourceClumps.Any(clump => clump.occupiesTile((int)tile.X, (int)tile.Y) && !IsWoodClump(clump));
    }

    private bool ShouldClearTree(Tree tree)
    {
        return tree.growthStage.Value >= 0 && !tree.tapped.Value;
    }

    private bool CanCollectProduce(FarmAnimal animal)
    {
        return !string.IsNullOrWhiteSpace(animal.currentProduce.Value)
            && animal.isAdult()
            && animal.GetHarvestType().GetValueOrDefault() != FarmAnimalHarvestType.DigUp;
    }

    private bool CollectProduce(FarmAnimal animal, List<DepositChest> chests, Point from)
    {
        if (!this.CanCollectProduce(animal))
            return false;

        StardewValley.Object produce = ItemRegistry.Create<StardewValley.Object>(animal.currentProduce.Value);
        produce.Quality = animal.produceQuality.Value;
        if (animal.hasEatenAnimalCracker.Value)
            produce.Stack = 2;

        animal.currentProduce.Value = null;
        animal.ReloadTextureIfNeeded();
        animal.HandleStatsOnProduceCollected(produce, (uint)produce.Stack);

        Item? remaining = SmartDeposit(produce, chests, from);
        if (remaining != null)
        {
            GameLocation location = animal.currentLocation ?? Game1.getFarm();
            Game1.createItemDebris(remaining, animal.Position, -1, location);
        }
        return true;
    }

    private int FeedAnimals(AnimalHouse house)
    {
        int before = house.objects.Values.Count(obj => obj.QualifiedItemId == "(O)178");
        house.feedAllAnimals();
        int after = house.objects.Values.Count(obj => obj.QualifiedItemId == "(O)178");
        return Math.Max(0, after - before);
    }

    private int CollectLooseAnimalProducts(GameLocation location, List<DepositChest> chests, Point from)
    {
        int collected = 0;
        foreach (var pair in location.objects.Pairs.ToArray())
        {
            StardewValley.Object obj = pair.Value;
            if (!IsLooseAnimalProduct(obj))
                continue;

            location.objects.Remove(pair.Key);
            Item? remaining = SmartDeposit(obj, chests, from);
            if (remaining != null)
                Game1.createItemDebris(remaining, pair.Key * 64f, -1, location);
            collected++;
        }
        return collected;
    }

    private IEnumerable<FarmAnimal> GetAnimals(AnimalHouse house)
    {
        foreach (FarmAnimal animal in house.animals.Values)
            yield return animal;
    }

    private int CountEmptyTroughs(AnimalHouse house)
    {
        int count = 0;
        for (int x = 0; x < house.map.Layers[0].LayerWidth; x++)
        {
            for (int y = 0; y < house.map.Layers[0].LayerHeight; y++)
            {
                if (house.doesTileHaveProperty(x, y, "Trough", "Back") != null && !house.objects.ContainsKey(new Vector2(x, y)))
                    count++;
            }
        }
        return count;
    }

    private static bool IsClearableWeed(StardewValley.Object obj)
    {
        return obj.IsWeeds();
    }

    private static bool IsTwig(StardewValley.Object obj)
    {
        return obj.IsTwig();
    }

    private static bool IsSmallBoulder(StardewValley.Object obj)
    {
        return obj.Name.Equals("Boulder", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLooseAnimalProduct(StardewValley.Object obj)
    {
        return obj.Category is StardewValley.Object.EggCategory or StardewValley.Object.MilkCategory
                or StardewValley.Object.meatCategory
            || obj.QualifiedItemId is "(O)430" or "(O)107" or "(O)289" or "(O)446" or "(O)444";
    }

    private static bool IsWoodClump(ResourceClump clump)
    {
        return clump.parentSheetIndex.Value is 600 or 602;
    }

    private static bool IsWithin(Point tile, Point? center, int radius)
    {
        return center == null || Distance(tile, center.Value) <= radius;
    }

    private static int Distance(Point a, Point b)
    {
        return Math.Abs(a.X - b.X) + Math.Abs(a.Y - b.Y);
    }

    private static int BuildingDistance(Building building, Point point)
    {
        int left = building.tileX.Value;
        int right = building.tileX.Value + building.tilesWide.Value - 1;
        int top = building.tileY.Value;
        int bottom = building.tileY.Value + building.tilesHigh.Value - 1;
        int x = Math.Clamp(point.X, left, right);
        int y = Math.Clamp(point.Y, top, bottom);
        return Distance(point, new Point(x, y));
    }

    private void OnRenderedWorld(object? sender, RenderedWorldEventArgs e)
    {
        if (!Context.IsWorldReady || this.npc == null)
            return;
        if (Game1.currentLocation != this.npc.currentLocation)
            return;
        if (!this.UsesGeneratedFarmerAppearance())
            return;

        Farmer farmer = this.GetVisualFarmer();
        this.SyncVisualFarmer(farmer);
        farmer.DrawShadow(e.SpriteBatch);
        farmer.draw(e.SpriteBatch);
    }

    private void SyncVisualFarmer(Farmer farmer)
    {
        NPC npc = this.npc!;
        farmer.currentLocation = npc.currentLocation;
        farmer.Position = npc.Position;
        farmer.speed = npc.speed;
        farmer.FacingDirection = npc.FacingDirection;
        farmer.jitter = Vector2.Zero;
        farmer.armOffset = Vector2.Zero;
        farmer.yOffset = npc.yOffset;
        farmer.yJumpOffset = npc.yJumpOffset;
        farmer.CanMove = false;
        farmer.UsingTool = false;
        farmer.canReleaseTool = false;
        farmer.toolPower.Value = 0;

        float progress = this.current == null || this.npc?.controller != null
            ? 0f
            : 1f - MathHelper.Clamp(this.workTimerMs / Math.Max(1f, this.workDurationMs), 0f, 1f);

        if (this.current != null && this.npc?.controller == null)
            this.ApplyWorkAnimation(farmer, this.current, progress);
        else if (npc.controller != null)
            this.ApplyWalkAnimation(farmer);
        else
            this.ApplyIdleAnimation(farmer);
    }

    private void ApplyIdleAnimation(Farmer farmer)
    {
        farmer.CurrentTool = null;
        farmer.FarmerSprite.setCurrentSingleFrame(GetFarmerIdleFrame(farmer.FacingDirection), 32000, secondaryArm: false, flip: farmer.FacingDirection == 3);
    }

    private void ApplyWalkAnimation(Farmer farmer)
    {
        farmer.CurrentTool = null;
        int animation = farmer.FacingDirection switch
        {
            0 => 16,
            1 => 8,
            3 => 24,
            _ => 0,
        };
        farmer.FarmerSprite.animate(animation, Game1.currentGameTime);
    }

    private void ApplyWorkAnimation(Farmer farmer, FarmTask task, float progress)
    {
        Tool? tool = this.GetVisualTool(task);
        farmer.CurrentTool = tool;

        if (tool == null)
        {
            int frame = MathF.Sin(MathHelper.Clamp(progress, 0f, 1f) * MathF.PI) > 0.55f
                ? GetFarmerStepFrame(farmer.FacingDirection)
                : GetFarmerIdleFrame(farmer.FacingDirection);
            farmer.FarmerSprite.setCurrentSingleFrame(frame, 32000, secondaryArm: false, flip: farmer.FacingDirection == 3);
            return;
        }

        tool.lastUser = farmer;
        farmer.UsingTool = true;
        farmer.canReleaseTool = true;

        if (task.Kind == TaskKind.Water)
        {
            ((WateringCan)tool).WaterLeft = 100;
            int frameIndex = WorkFrameIndex(progress, 4);
            int animation = GetWateringAnimation(farmer.FacingDirection);
            farmer.FarmerSprite.setCurrentFrame(animation, Math.Min(frameIndex, 3), 125, 4, farmer.FacingDirection == 3, secondaryArm: false);
            tool.Update(farmer.FacingDirection, frameIndex, farmer);
            return;
        }

        // Pick the animation family by the TOOL actually in hand (a twig task carries the axe, so
        // it must swing like a tool, not like the scythe).
        if (tool is MeleeWeapon)
        {
            int frameIndex = WorkFrameIndex(progress, 6);
            int animation = GetWeaponAnimation(farmer.FacingDirection);
            farmer.FarmerSprite.setCurrentFrame(animation, Math.Min(frameIndex, 5), 80, 6, farmer.FacingDirection == 3, secondaryArm: true);
            farmer.FarmerSprite.CurrentToolIndex = tool.CurrentParentTileIndex;
            return;
        }

        int toolFrame = WorkFrameIndex(progress, 8);
        int toolAnimation = GetToolAnimation(farmer.FacingDirection);
        farmer.FarmerSprite.setCurrentFrame(toolAnimation, Math.Min(toolFrame, 7), 60, 8, farmer.FacingDirection == 3, secondaryArm: false);
        tool.Update(farmer.FacingDirection, toolFrame, farmer);
    }

    /// <summary>The tool shown in his hands — resolved from the ACTUAL target so it always matches
    /// what the work logic uses: twigs take the axe (not the scythe), hand-picked crops show no
    /// tool (players harvest most crops bare-handed), scythe only for scythe-harvest crops.</summary>
    private Tool? GetVisualTool(FarmTask task)
    {
        Farm farm = Game1.getFarm();
        var tile = new Vector2(task.Tile.X, task.Tile.Y);

        return task.Kind switch
        {
            TaskKind.Water => this.GetHelperWateringCan(),
            TaskKind.ChopTree => this.GetHelperAxe(),
            TaskKind.BreakStone => this.GetHelperPickaxe(),
            TaskKind.CutGrass => this.GetHelperScythe(),
            TaskKind.ClearWeeds => farm.objects.TryGetValue(tile, out StardewValley.Object? obj) && IsTwig(obj)
                ? this.GetHelperAxe()
                : this.GetHelperScythe(),
            TaskKind.Harvest => this.IsScytheHarvest(farm, tile) ? this.GetHelperScythe() : null,
            _ => null,
        };
    }

    private bool IsScytheHarvest(Farm farm, Vector2 tile)
    {
        return farm.terrainFeatures.TryGetValue(tile, out TerrainFeature? feature)
            && feature is HoeDirt dirt
            && dirt.crop != null
            && dirt.crop.GetHarvestMethod() == HarvestMethod.Scythe;
    }

    private WateringCan GetHelperWateringCan()
    {
        this.helperWateringCan ??= new WateringCan { UpgradeLevel = 4, WaterLeft = 100 };
        this.helperWateringCan.lastUser = this.visualFarmer ?? Game1.MasterPlayer;
        return this.helperWateringCan;
    }

    private static int WorkFrameIndex(float progress, int frameCount)
    {
        progress = MathHelper.Clamp(progress, 0f, 0.999f);
        return Math.Clamp((int)(progress * frameCount), 0, frameCount - 1);
    }

    private static int GetFarmerIdleFrame(int direction)
    {
        return direction switch
        {
            0 => 12,
            1 => 6,
            3 => 6,
            _ => 0,
        };
    }

    private static int GetFarmerStepFrame(int direction)
    {
        return direction switch
        {
            0 => 13,
            1 => 7,
            3 => 7,
            _ => 1,
        };
    }

    private static int GetToolAnimation(int direction)
    {
        return direction switch
        {
            0 => 176,
            1 => 168,
            3 => 184,
            _ => 160,
        };
    }

    private static int GetWateringAnimation(int direction)
    {
        return direction switch
        {
            0 => 180,
            1 => 172,
            3 => 188,
            _ => 164,
        };
    }

    private static int GetWeaponAnimation(int direction)
    {
        return direction switch
        {
            0 => 248,
            1 => 240,
            3 => 256,
            _ => 232,
        };
    }

    private static IEnumerable<Point> AdjacentTiles(Point tile)
    {
        yield return new Point(tile.X, tile.Y + 1); // below first: working "up" reads best
        yield return new Point(tile.X - 1, tile.Y);
        yield return new Point(tile.X + 1, tile.Y);
        yield return new Point(tile.X, tile.Y - 1);
    }

    private static int FacingToward(Point from, Point to)
    {
        int dx = to.X - from.X;
        int dy = to.Y - from.Y;
        if (Math.Abs(dx) >= Math.Abs(dy))
            return dx >= 0 ? 1 : 3;
        return dy >= 0 ? 2 : 0;
    }
}
