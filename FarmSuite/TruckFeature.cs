using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Buffs;
using StardewValley.Characters;
using StardewValley.Menus;
using StardewValley.Objects;
using StardewValley.WorldMaps;

namespace FarmSuite;

/// <summary>Saved truck state (written via helper.Data save data, never the game serializer).</summary>
internal sealed class TruckState
{
    public bool Owned { get; set; }
    public string LocationName { get; set; } = "Farm";
    public int TileX { get; set; } = 64;
    public int TileY { get; set; } = 18;
}

/// <summary>Feature 3: a purchasable pickup truck.
///
/// Implemented as a plain vanilla <see cref="Horse"/> (never a subclass — the save serializer only
/// accepts known types) tagged via modData and given a custom sprite sheet that mirrors the
/// Animals/horse layout. The truck is spawned after load and removed before save, so it never
/// reaches the serializer at all. Extra speed comes from an invisible endless speed buff while
/// riding (the mounted speed formula reads buffs via addedSpeed). Cargo lives in a team global
/// inventory (vanilla-persisted and multiplayer-synced) accessed through a transient Chest shim.</summary>
internal sealed class TruckFeature
{
    private const string SheetAsset = "Mods/Codex.FarmSuite/TruckSheet";
    private const string ModDataKey = "Codex.FarmSuite/IsTruck";
    private const string CargoId = "Codex.FarmSuite/TruckCargo";
    private const string BuffId = "Codex.FarmSuite/TruckSpeed";
    private const string SaveKey = "truck-state";
    private static readonly Guid TruckId = new("5b3c9a7e-2d41-4a8f-9c66-0f1e2d3c4b5a");

    private readonly IModHelper helper;
    private readonly IMonitor monitor;
    private readonly Func<ModConfig> config;
    private readonly TruckState state = new();
    private Horse? truck;
    private Chest? cargoShim;

    public TruckFeature(IModHelper helper, IMonitor monitor, Func<ModConfig> config)
    {
        this.helper = helper;
        this.monitor = monitor;
        this.config = config;

        helper.Events.Content.AssetRequested += this.OnAssetRequested;
        helper.Events.GameLoop.SaveLoaded += (_, _) => this.OnSaveLoaded();
        helper.Events.GameLoop.DayStarted += (_, _) => this.SpawnTruck();
        helper.Events.GameLoop.Saving += (_, _) => this.OnSaving();
        helper.Events.GameLoop.Saved += (_, _) => this.SpawnTruck();
        helper.Events.GameLoop.ReturnedToTitle += (_, _) => this.Reset();
        helper.Events.GameLoop.UpdateTicked += this.OnUpdateTicked;
        helper.Events.Display.MenuChanged += this.OnMenuChanged;
        helper.Events.Display.RenderedActiveMenu += this.OnRenderedActiveMenu;
        helper.Events.Input.ButtonPressed += this.OnButtonPressed;
    }

    private void OnAssetRequested(object? sender, AssetRequestedEventArgs e)
    {
        if (e.NameWithoutLocale.IsEquivalentTo(SheetAsset))
            e.LoadFromModFile<Texture2D>("assets/truck.png", AssetLoadPriority.Exclusive);
    }

    // ── lifecycle ────────────────────────────────────────────────────────────

    private void OnSaveLoaded()
    {
        // ReadSaveData throws on remote farmhands (save data lives on the host), and a farmhand
        // must never mutate the synced characters lists either. Single-player/host only.
        if (!Context.IsMainPlayer)
            return;

        TruckState? saved = this.helper.Data.ReadSaveData<TruckState>(SaveKey);
        if (saved != null)
        {
            this.state.Owned = saved.Owned;
            this.state.LocationName = saved.LocationName;
            this.state.TileX = saved.TileX;
            this.state.TileY = saved.TileY;
        }
        this.SweepStrays();
    }

    private void SpawnTruck()
    {
        if (!this.config().EnableTruck || !Context.IsMainPlayer || !this.state.Owned || this.truck != null)
            return;

        GameLocation location = Game1.getLocationFromName(this.state.LocationName) ?? Game1.getFarm();
        var horse = new Horse(TruckId, this.state.TileX, this.state.TileY);
        horse.Name = "FarmSuiteTruck";
        horse.displayName = "小皮卡";
        horse.modData[ModDataKey] = "1";
        horse.Sprite = new AnimatedSprite(SheetAsset, 0, 32, 32)
        {
            textureUsesFlippedRightForLeft = true,
            loop = true,
        };
        horse.ateCarrotToday = true; // free +0.4 mounted speed
        horse.currentLocation = location; // the Horse ctor wrongly uses Game1.currentLocation

        // The default parking tile assumes the standard farm layout — snap to an open tile.
        Vector2 open = Utility.recursiveFindOpenTileForCharacter(horse, location, new Vector2(this.state.TileX, this.state.TileY), 30);
        if (open != Vector2.Zero)
            horse.Position = open * 64f;

        location.characters.Add(horse);
        this.truck = horse;
    }

    private void OnSaving()
    {
        if (!Context.IsMainPlayer)
            return;

        // Never let the truck (or a mounted rider state) reach the serializer.
        if (Game1.player.mount != null && IsTruck(Game1.player.mount))
            Game1.player.mount.dismount();

        if (this.truck != null)
        {
            this.state.LocationName = this.truck.currentLocation?.Name ?? this.state.LocationName;
            this.state.TileX = this.truck.TilePoint.X;
            this.state.TileY = this.truck.TilePoint.Y;
        }

        this.SweepStrays();
        this.truck = null;
        this.helper.Data.WriteSaveData(SaveKey, this.state);
    }

    private void SweepStrays()
    {
        Utility.ForEachLocation(location =>
        {
            for (int i = location.characters.Count - 1; i >= 0; i--)
            {
                if (location.characters[i] is Horse h && h.modData.ContainsKey(ModDataKey))
                    location.characters.RemoveAt(i);
            }
            return true;
        });
    }

    private void Reset()
    {
        this.truck = null;
        this.cargoShim = null;
        this.state.Owned = false;
        this.state.LocationName = "Farm";
    }

    private static bool IsTruck(Horse horse) => horse.modData.ContainsKey(ModDataKey);

    // ── per-tick: orphan re-add + speed buff ─────────────────────────────────

    private void OnUpdateTicked(object? sender, UpdateTickedEventArgs e)
    {
        if (!Context.IsWorldReady)
            return;

        // A stable-less horse is orphaned (in no characters list) after every dismount — re-add it.
        if (Context.IsMainPlayer && this.truck != null && this.truck.rider == null)
        {
            GameLocation? location = this.truck.currentLocation;
            if (location != null && !location.characters.Contains(this.truck))
                location.characters.Add(this.truck);
        }

        // Speed buff while driving (mounted speed reads buffs.Speed via addedSpeed).
        bool driving = Game1.player.mount is Horse mount && IsTruck(mount);
        bool hasBuff = Game1.player.hasBuff(BuffId);
        if (driving && !hasBuff)
        {
            var buff = new Buff(
                id: BuffId,
                duration: Buff.ENDLESS,
                effects: new BuffEffects { Speed = { Value = this.config().TruckSpeedBonus } },
                displayName: "小皮卡")
            {
                visible = false,
            };
            Game1.player.applyBuff(buff);
        }
        else if (!driving && hasBuff)
        {
            Game1.player.buffs.Remove(BuffId);
        }
    }

    // ── purchase: inject a voucher row into Robin's carpenter shop ───────────

    private void OnMenuChanged(object? sender, MenuChangedEventArgs e)
    {
        // Host only: a farmhand purchase couldn't spawn or persist the truck — it would just
        // destroy the farmhand's money.
        if (!this.config().EnableTruck || this.state.Owned || !Context.IsMainPlayer)
            return;
        if (e.NewMenu is not ShopMenu shop || shop.ShopId != "Carpenter")
            return;
        if (shop.forSale.Any(s => s is TruckVoucher))
            return;

        var voucher = new TruckVoucher(this.config().TruckPrice, this.GrantTruck);
        shop.forSale.Insert(0, voucher);
        shop.itemPriceAndStock.Add(voucher, new ItemStockInformation(this.config().TruckPrice, 1, syncedKey: "Codex.FarmSuite.TruckVoucher"));
    }

    private void GrantTruck()
    {
        this.state.Owned = true;
        this.state.LocationName = "Farm";
        this.state.TileX = 64;
        this.state.TileY = 18;
        this.SpawnTruck();
        Game1.playSound("coin");
        Game1.addHUDMessage(HUDMessage.ForCornerTextbox("罗宾把小皮卡停到你的农场门口了！"));
        this.monitor.Log("Truck purchased.", LogLevel.Info);
    }

    // ── cargo ────────────────────────────────────────────────────────────────

    private void OnButtonPressed(object? sender, ButtonPressedEventArgs e)
    {
        if (!Context.IsWorldReady || !this.state.Owned || Game1.activeClickableMenu != null)
            return;
        if (!Enum.TryParse(this.config().TruckCargoKey, ignoreCase: true, out SButton cargoKey) || e.Button != cargoKey)
            return;

        bool driving = Game1.player.mount is Horse mount && IsTruck(mount);
        bool nearby = this.truck != null
            && this.truck.currentLocation == Game1.currentLocation
            && Math.Abs(this.truck.TilePoint.X - Game1.player.TilePoint.X) + Math.Abs(this.truck.TilePoint.Y - Game1.player.TilePoint.Y) <= 3;

        if (!driving && !nearby)
            return;

        this.cargoShim ??= new Chest(playerChest: true) { GlobalInventoryId = CargoId };
        this.cargoShim.ShowMenu();
        this.helper.Input.Suppress(e.Button);
    }

    // ── world map marker ─────────────────────────────────────────────────────

    private void OnRenderedActiveMenu(object? sender, RenderedActiveMenuEventArgs e)
    {
        if (!Context.IsWorldReady || !this.state.Owned)
            return;

        MapPage? page = Game1.activeClickableMenu switch
        {
            MapPage mapPage => mapPage,
            GameMenu gameMenu when gameMenu.currentTab == GameMenu.mapTab => gameMenu.GetCurrentPage() as MapPage,
            _ => null,
        };
        if (page == null)
            return;

        // Where is the truck right now? While ridden it isn't in any location — follow the rider.
        GameLocation? location;
        Point tile;
        if (this.truck?.rider != null)
        {
            location = this.truck.rider.currentLocation;
            tile = this.truck.rider.TilePoint;
        }
        else if (this.truck?.currentLocation != null)
        {
            location = this.truck.currentLocation;
            tile = this.truck.TilePoint;
        }
        else
        {
            location = Game1.getLocationFromName(this.state.LocationName);
            tile = new Point(this.state.TileX, this.state.TileY);
        }
        if (location == null)
            return;

        MapAreaPositionWithContext? position = WorldMapManager.GetPositionData(location, tile);
        if (position == null || position.Value.Data.Region.Id != page.mapRegion.Id)
            return; // e.g. truck in the Valley while viewing the Ginger Island map

        Vector2 mapPixel = position.Value.GetMapPixelPosition();
        var screen = new Vector2(mapPixel.X + page.mapBounds.X, mapPixel.Y + page.mapBounds.Y);

        // Draw the truck's side-view frame as the marker, with a subtle backing plate.
        Texture2D sheet = Game1.content.Load<Texture2D>(SheetAsset);
        var src = new Rectangle(0, 32, 32, 32); // row 1 = facing right
        const float scale = 2f;
        e.SpriteBatch.Draw(Game1.staminaRect,
            new Rectangle((int)(screen.X - 16 * scale / 2) - 2, (int)(screen.Y - 16 * scale) - 2, (int)(16 * scale) + 4, (int)(16 * scale) + 4),
            Color.Black * 0.35f);
        e.SpriteBatch.Draw(sheet, screen - new Vector2(16f * scale, 28f * scale / 2f), src, Color.White, 0f, Vector2.Zero, scale, SpriteEffects.None, 1f);
    }
}

/// <summary>A purchasable shop row that is NOT an Item. <see cref="actionWhenPurchased"/> returns
/// true, which makes the shop discard it after charging the player — it can never reach the player
/// inventory or the save file.</summary>
internal sealed class TruckVoucher : ISalable
{
    private readonly int price;
    private readonly Action onBought;

    public TruckVoucher(int price, Action onBought)
    {
        this.price = price;
        this.onBought = onBought;
    }

    public string TypeDefinitionId => "(FarmSuiteTruck)";
    public string QualifiedItemId => "(FarmSuiteTruck)Truck";
    public string DisplayName => "小皮卡";
    public string Name => "FarmSuite.Truck";
    public bool IsRecipe { get => false; set { } }
    public int Stack { get => 1; set { } }
    public int Quality { get => 0; set { } }

    public string GetItemTypeId() => this.TypeDefinitionId;
    public bool ShouldDrawIcon() => true;

    public void drawInMenu(SpriteBatch spriteBatch, Vector2 location, float scaleSize, float transparency, float layerDepth, StackDrawType drawStackNumber, Color color, bool drawShadow)
    {
        Texture2D sheet = Game1.content.Load<Texture2D>("Mods/Codex.FarmSuite/TruckSheet");
        spriteBatch.Draw(sheet, location + new Vector2(0f, 0f), new Rectangle(0, 32, 32, 32), color * transparency, 0f, Vector2.Zero, 2f * scaleSize, SpriteEffects.None, layerDepth);
    }

    public string getDescription() => "一辆结实的小皮卡：开起来比跑步快得多，\n车斗还能装货。罗宾会把它停到农场。\n（按配置的货箱键打开车斗，骑乘交互上下车）";
    public int maximumStackSize() => 1;
    public int addToStack(Item stack) => 1;
    public int sellToStorePrice(long specificPlayerID = -1L) => -1;
    public int salePrice(bool ignoreProfitMargins = false) => this.price;
    public bool appliesProfitMargins() => false;

    public bool actionWhenPurchased(string shopId)
    {
        this.onBought(); // ShopMenu.chargePlayer already deducted the money
        return true;     // discard the voucher: it never enters the inventory (save-safe)
    }

    public bool canStackWith(ISalable other) => false;
    public bool CanBuyItem(Farmer farmer) => farmer.Money >= this.price;
    public bool IsInfiniteStock() => false;
    public ISalable GetSalableInstance() => this;
    public void FixStackSize() { }
    public void FixQuality() { }
}
