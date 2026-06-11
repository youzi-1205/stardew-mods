using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Menus;
using StardewValley.Objects;
using StardewValley.Tools;

namespace AutoFish;

internal sealed class ModEntry : Mod
{
    private const double VanillaBaseChanceForTreasure = 0.15;

    private ModConfig config = new();
    private bool warnedAboutReflection;
    private bool handledCurrentBite;
    private int configCheckTicks;
    private DateTime lastConfigWriteTimeUtc;
    private uint lastBoostedFishingTreasureCount;

    // Auto-fishing button state.
    private bool autoFishEnabled;
    private bool hasFishedHere;
    private bool autoFishButtonVisible;
    private Rectangle autoFishButtonBounds; // in absolute world pixels
    private int recastCooldownTicks;
    private int catchContinueDelay;

    public override void Entry(IModHelper helper)
    {
        this.config = helper.ReadConfig<ModConfig>();
        this.lastConfigWriteTimeUtc = this.GetConfigWriteTimeUtc();

        helper.Events.GameLoop.UpdateTicked += this.OnUpdateTicked;
        helper.Events.Display.RenderedWorld += this.OnRenderedWorld;
        helper.Events.Display.MenuChanged += this.OnMenuChanged;
        helper.Events.Input.ButtonPressed += this.OnButtonPressed;
        helper.Events.Player.Warped += this.OnWarped;
        helper.Events.GameLoop.SaveLoaded += this.OnSaveLoaded;
        helper.Events.GameLoop.ReturnedToTitle += this.OnReturnedToTitle;

        helper.ConsoleCommands.Add(
            "fa_toggle",
            "Toggle Fishing Assist on/off.",
            (_, _) =>
            {
                this.config.EnableMod = !this.config.EnableMod;
                this.Helper.WriteConfig(this.config);
                this.Monitor.Log($"Fishing Assist is now {(this.config.EnableMod ? "enabled" : "disabled")}.", LogLevel.Info);
            }
        );

        helper.ConsoleCommands.Add(
            "fa_auto",
            "Toggle automatic fish-following on/off.",
            (_, _) =>
            {
                this.config.AutoFollowFish = !this.config.AutoFollowFish;
                this.Helper.WriteConfig(this.config);
                this.Monitor.Log($"Auto-follow is now {(this.config.AutoFollowFish ? "enabled" : "disabled")}.", LogLevel.Info);
            }
        );
    }

    private void OnUpdateTicked(object? sender, UpdateTickedEventArgs e)
    {
        if (!Context.IsWorldReady)
        {
            if (Math.Abs(FishingRod.baseChanceForTreasure - VanillaBaseChanceForTreasure) > 0.0001)
                FishingRod.baseChanceForTreasure = VanillaBaseChanceForTreasure;

            return;
        }

        this.ReloadConfigIfChanged();
        this.ApplyTreasureChanceSetting();

        if (!this.config.EnableMod)
            return;

        FishingRod? rod = Game1.player?.CurrentTool as FishingRod;

        // Once the player has fished here, the auto-fish button may appear.
        if (rod != null && (rod.isFishing || rod.fishCaught))
            this.hasFishedHere = true;

        this.UpdateAutoFishButton(rod);

        if (this.autoFishEnabled)
        {
            this.TryAutoContinueCatch(rod);
            this.TryAutoCast(rod);
        }

        bool auto = this.autoFishEnabled;

        if (this.config.AutoHookFish || auto)
            this.TryAutoHookFish();

        if (Game1.activeClickableMenu is not BobberBar bobberBar)
            return;

        try
        {
            if (this.config.AutoFollowFish || auto)
            {
                bool followingTreasure = this.config.PrioritizeTreasureChest && this.TryFollowTreasure(bobberBar);

                if (!followingTreasure)
                    this.FollowFish(bobberBar);
                else if (this.config.ProtectFishProgressWhileCatchingTreasure)
                    this.KeepCatchProgressHigh(bobberBar, this.config.TreasureFishProgressFloor);
            }

            if (this.config.KeepCatchProgressFromDropping)
                this.KeepCatchProgressHigh(bobberBar, this.config.MinimumCatchProgress);
        }
        catch (Exception ex)
        {
            if (!this.warnedAboutReflection)
            {
                this.warnedAboutReflection = true;
                this.Monitor.Log($"Couldn't control the fishing minigame. Stardew Valley's BobberBar fields may have changed. Details: {ex}", LogLevel.Warn);
            }
        }
    }

    private void OnWarped(object? sender, WarpedEventArgs e)
    {
        if (!e.IsLocalPlayer)
            return;

        // A new water area means a fresh start: hide the button and stop auto-fishing so
        // the rod doesn't start casting somewhere the player didn't choose.
        this.hasFishedHere = false;
        this.autoFishEnabled = false;
        this.autoFishButtonVisible = false;
    }

    private void OnSaveLoaded(object? sender, SaveLoadedEventArgs e)
    {
        this.lastBoostedFishingTreasureCount = Game1.player?.stats.Get("FishingTreasures") ?? 0;
        this.ApplyTreasureChanceSetting();
    }

    private void OnReturnedToTitle(object? sender, ReturnedToTitleEventArgs e)
    {
        FishingRod.baseChanceForTreasure = VanillaBaseChanceForTreasure;
        this.lastBoostedFishingTreasureCount = 0;
        this.autoFishEnabled = false;
        this.autoFishButtonVisible = false;
    }

    private void OnMenuChanged(object? sender, MenuChangedEventArgs e)
    {
        if (!this.config.EnableMod || !this.config.BoostFishingTreasureRarity || !Context.IsWorldReady)
            return;

        if (e.NewMenu is not ItemGrabMenu menu || menu.source != ItemGrabMenu.source_fishingChest)
            return;

        if (menu.ItemsToGrabMenu?.actualInventory == null)
            return;

        uint treasureCount = Game1.player.stats.Get("FishingTreasures");
        if (treasureCount == 0 || treasureCount == this.lastBoostedFishingTreasureCount)
            return;

        this.lastBoostedFishingTreasureCount = treasureCount;

        Item? bonus = this.RollBonusFishingTreasure(menu.ItemsToGrabMenu.actualInventory);
        if (bonus == null)
            return;

        IList<Item> inventory = menu.ItemsToGrabMenu.actualInventory;
        int emptyIndex = -1;
        for (int i = 0; i < inventory.Count; i++)
        {
            if (inventory[i] == null)
            {
                emptyIndex = i;
                break;
            }
        }

        if (emptyIndex >= 0)
        {
            inventory[emptyIndex] = bonus;
        }
        else
        {
            if (inventory.Count >= menu.ItemsToGrabMenu.capacity)
                return;

            inventory.Add(bonus);
        }

        Game1.playSound("reward");
        this.Monitor.Log($"Added bonus fishing treasure: {bonus.DisplayName}.", LogLevel.Trace);
    }

    private void ApplyTreasureChanceSetting()
    {
        double multiplier = 1.0;
        if (this.config.EnableMod && this.config.BoostFishingTreasureRarity)
        {
            multiplier = Math.Clamp(this.config.TreasureChestChanceMultiplier, 0.1f, 3f);
            if (this.ShouldPrioritizeMissingFishingWeapons(Game1.player))
                multiplier = Math.Max(multiplier, Math.Clamp(this.config.MissingFishingWeaponChestChanceMultiplier, 0.1f, 4f));
        }

        double targetChance = VanillaBaseChanceForTreasure * multiplier;

        if (Math.Abs(FishingRod.baseChanceForTreasure - targetChance) > 0.0001)
            FishingRod.baseChanceForTreasure = targetChance;
    }

    private Item? RollBonusFishingTreasure(IList<Item> currentTreasure)
    {
        Farmer farmer = Game1.player;
        int clearWaterDistance = Math.Clamp((farmer.CurrentTool as FishingRod)?.clearWaterDistance ?? 5, 1, 5);
        float depthAndLuck = Math.Clamp((1f + (float)farmer.DailyLuck) * (clearWaterDistance / 5f), 0.15f, 1.25f);
        bool prioritizingMissingWeapon = this.ShouldPrioritizeMissingFishingWeapons(farmer);
        float weaponChance = prioritizingMissingWeapon
            ? Math.Clamp(this.config.MissingFishingWeaponBonusChance, 0f, 1f)
            : Math.Clamp(this.config.WeaponTreasureBonusChance, 0f, 1f) * depthAndLuck;

        if (farmer.FishingLevel >= Math.Max(0, this.config.MinimumFishingLevelForWeaponBoost)
            && Game1.random.NextDouble() < weaponChance)
        {
            Item? weapon = this.CreateBonusFishingWeapon(farmer, currentTreasure);
            if (weapon != null)
                return weapon;
        }

        if (Game1.random.NextDouble() < Math.Clamp(this.config.BonusRareTreasureChance, 0f, 1f) * depthAndLuck)
            return CreateBonusRareTreasure(farmer, clearWaterDistance);

        return null;
    }

    private Item? CreateBonusFishingWeapon(Farmer farmer, IList<Item> currentTreasure)
    {
        List<string> weaponIds = new() { "14", "51" };
        if (this.config.RespectUniqueFishingWeapons)
        {
            weaponIds.RemoveAll(id => this.HasFishingWeapon(farmer, id));
            weaponIds.RemoveAll(id => currentTreasure.Any(item => item?.QualifiedItemId == $"(W){id}"));
        }

        if (weaponIds.Count == 0)
            return null;

        string weaponId = weaponIds[Game1.random.Next(weaponIds.Count)];
        Item weapon = MeleeWeapon.attemptAddRandomInnateEnchantment(ItemRegistry.Create($"(W){weaponId}"), Game1.random);
        weapon.specialItem = true;
        return weapon;
    }

    private bool ShouldPrioritizeMissingFishingWeapons(Farmer? farmer)
    {
        return this.config.PrioritizeMissingFishingWeapons
            && farmer != null
            && farmer.FishingLevel >= Math.Max(0, this.config.MinimumFishingLevelForWeaponBoost)
            && (!this.HasFishingWeapon(farmer, "14") || !this.HasFishingWeapon(farmer, "51"));
    }

    private bool HasFishingWeapon(Farmer farmer, string weaponId)
    {
        string qualifiedId = $"(W){weaponId}";
        if (farmer.specialItems.Contains(weaponId))
            return true;

        foreach (Item? item in farmer.Items)
        {
            if (item?.QualifiedItemId == qualifiedId)
                return true;
        }

        bool foundInStorage = false;
        Utility.iterateChestsAndStorage(item =>
        {
            if (item?.QualifiedItemId == qualifiedId)
                foundInStorage = true;
        });

        return foundInStorage;
    }

    private static Item CreateBonusRareTreasure(Farmer farmer, int clearWaterDistance)
    {
        if (farmer.FishingLevel >= 6 && clearWaterDistance >= 4 && Game1.random.NextDouble() < 0.03)
            return ItemRegistry.Create("(O)74");

        return Game1.random.Next(7) switch
        {
            0 => ItemRegistry.Create("(O)72"),
            1 => ItemRegistry.Create("(O)166"),
            2 => ItemRegistry.Create(Game1.random.NextDouble() < 0.5 ? "(O)126" : "(O)127"),
            3 => new Ring(Game1.random.NextDouble() < 0.5 ? "516" : "518"),
            4 => new Ring(Game1.random.Next(529, 535).ToString()),
            5 => ItemRegistry.Create($"(B){Game1.random.Next(504, 514)}"),
            _ => ItemRegistry.Create("(O)337", Game1.random.Next(1, 3))
        };
    }

    private void UpdateAutoFishButton(FishingRod? rod)
    {
        this.autoFishButtonVisible = false;

        if (!this.config.ShowAutoFishButton || rod == null || Game1.player == null)
            return;

        if (Game1.eventUp || Game1.farmEvent != null || Game1.activeClickableMenu != null)
            return;

        // While auto-fishing, always show the button so it can be stopped. Otherwise only show it
        // when the player has fished here and is still standing by the water — walking away hides it.
        bool show = this.autoFishEnabled || (this.hasFishedHere && this.IsNearWater(Game1.player));
        if (!show)
            return;

        string label = this.autoFishEnabled ? this.config.AutoFishButtonActiveLabel : this.config.AutoFishButtonLabel;
        Vector2 size = Game1.smallFont.MeasureString(label);
        int width = (int)size.X + 48;
        int height = Math.Max(64, (int)size.Y + 36);

        Vector2 position = Game1.player.Position;
        int x = (int)(position.X + 32f) - width / 2; // centered over the player
        int y = (int)position.Y - 112 - height;       // above the head

        this.autoFishButtonBounds = new Rectangle(x, y, width, height);
        this.autoFishButtonVisible = true;
    }

    private bool IsNearWater(Farmer player)
    {
        GameLocation? location = player.currentLocation;
        if (location == null)
            return false;

        Point tile = player.TilePoint;
        for (int dx = -2; dx <= 2; dx++)
        {
            for (int dy = -2; dy <= 2; dy++)
            {
                if (location.isTileFishable(tile.X + dx, tile.Y + dy))
                    return true;
            }
        }

        return false;
    }

    private void OnRenderedWorld(object? sender, RenderedWorldEventArgs e)
    {
        if (!this.autoFishButtonVisible)
            return;

        SpriteBatch b = e.SpriteBatch;
        Rectangle bounds = this.autoFishButtonBounds;
        Vector2 topLeft = Game1.GlobalToLocal(Game1.viewport, new Vector2(bounds.X, bounds.Y));
        Rectangle screenRect = new((int)topLeft.X, (int)topLeft.Y, bounds.Width, bounds.Height);

        Vector2 cursor = this.Helper.Input.GetCursorPosition().AbsolutePixels;
        bool hovered = bounds.Contains((int)cursor.X, (int)cursor.Y);

        // Use the game's own wood menu box so it matches the UI style. Tint green while active.
        Color boxColor = this.autoFishEnabled ? new Color(150, 235, 150) : Color.White;
        if (hovered)
            boxColor = this.autoFishEnabled ? new Color(195, 255, 195) : new Color(255, 250, 205);

        IClickableMenu.drawTextureBox(
            b, Game1.menuTexture, new Rectangle(0, 256, 60, 60),
            screenRect.X, screenRect.Y, screenRect.Width, screenRect.Height,
            boxColor, 1f, drawShadow: false);

        string label = this.autoFishEnabled ? this.config.AutoFishButtonActiveLabel : this.config.AutoFishButtonLabel;
        Vector2 size = Game1.smallFont.MeasureString(label);
        Vector2 textPosition = new(
            screenRect.X + (screenRect.Width - size.X) / 2f,
            screenRect.Y + (screenRect.Height - size.Y) / 2f);
        Utility.drawTextWithShadow(b, label, Game1.smallFont, textPosition, Game1.textColor);
    }

    private void OnButtonPressed(object? sender, ButtonPressedEventArgs e)
    {
        if (!this.config.EnableMod || !Context.IsWorldReady)
            return;

        if (e.Button != SButton.MouseLeft && e.Button != SButton.ControllerA)
            return;

        // Only react when the button is actually showing and nothing else has focus.
        if (!this.autoFishButtonVisible || Game1.activeClickableMenu != null)
            return;

        Vector2 cursor = e.Cursor.AbsolutePixels;
        if (!this.autoFishButtonBounds.Contains((int)cursor.X, (int)cursor.Y))
            return;

        this.autoFishEnabled = !this.autoFishEnabled;
        this.recastCooldownTicks = 0;
        this.Helper.Input.Suppress(e.Button); // don't also swing the rod
        this.Monitor.Log($"Auto-fishing {(this.autoFishEnabled ? "enabled" : "disabled")}.", LogLevel.Info);
    }

    private void TryAutoCast(FishingRod? rod)
    {
        if (rod == null)
            return;

        // Pause while the player can't freely act. Context.IsPlayerFree is false whenever a
        // menu is open (treasure chest, full-inventory discard prompt, level-up, dialogue) or
        // during cutscenes / the fishing minigame, which is exactly when we must not recast.
        if (!Context.IsPlayerFree)
        {
            this.recastCooldownTicks = 30;
            return;
        }

        if (rod.inUse() || rod.isCasting || rod.isTimingCast || rod.isFishing
            || rod.castedButBobberStillInAir || rod.pullingOutOfWater || rod.isReeling
            || rod.fishCaught || rod.showingTreasure)
            return;

        if (this.recastCooldownTicks > 0)
        {
            this.recastCooldownTicks--;
            return;
        }

        if (Game1.player.Stamina <= 1f)
        {
            this.autoFishEnabled = false;
            this.Monitor.Log("Stopped auto-fishing: out of energy.", LogLevel.Info);
            return;
        }

        // beginUsing starts charging the cast; with no button held the game releases it on the
        // next tick (FishingRod.tickUpdate -> startCasting) and throws the bobber. We override
        // castingPower to cast at (by default) max distance, which reaches deeper open water for
        // better fish quality/size and treasure odds. This mirrors a real cast, so hooking/the
        // minigame proceed normally.
        this.recastCooldownTicks = 30;
        Point standing = Game1.player.StandingPixel;
        rod.beginUsing(Game1.currentLocation, standing.X, standing.Y, Game1.player);
        rod.castingPower = Math.Clamp(this.config.AutoCastPower, 0f, 1f);
    }

    private void TryAutoContinueCatch(FishingRod? rod)
    {
        if (rod == null || !rod.fishCaught)
        {
            this.catchContinueDelay = 0;
            return;
        }

        // A treasure chest is waiting: leave it for the player to open themselves.
        if (rod.treasureCaught)
            return;

        // Only stop when the catch genuinely can't fit (an existing stack with room still counts
        // as space), so the player can clear the inventory in time rather than forcing it through.
        if (!this.CanInventoryAcceptCatch(rod))
        {
            if (this.autoFishEnabled)
            {
                this.autoFishEnabled = false;
                this.Monitor.Log("Stopped auto-fishing: no room for the catch.", LogLevel.Info);
            }
            return;
        }

        // Brief pause so the caught fish is visible before we auto-acknowledge it.
        if (this.catchContinueDelay < 12)
        {
            this.catchContinueDelay++;
            return;
        }

        this.catchContinueDelay = 0;
        rod.doneHoldingFish(Game1.player); // the click that finishes the "fish caught" hold
    }

    private bool CanInventoryAcceptCatch(FishingRod rod)
    {
        try
        {
            if (rod.whichFish == null)
                return Game1.player.freeSpotsInInventory() > 0;

            // Match the catch's quality so a same-fish-but-different-quality stack isn't mistaken
            // for free space.
            int quality = this.Helper.Reflection.GetField<int>(rod, "fishQuality").GetValue();
            return Game1.player.couldInventoryAcceptThisItem(rod.whichFish.QualifiedItemId, 1, quality);
        }
        catch
        {
            // If the rod's internal fields ever change, fall back to a simple free-slot check.
            return Game1.player.freeSpotsInInventory() > 0;
        }
    }

    private void ReloadConfigIfChanged()
    {
        this.configCheckTicks++;

        if (this.configCheckTicks < 30)
            return;

        this.configCheckTicks = 0;

        DateTime currentWriteTimeUtc = this.GetConfigWriteTimeUtc();
        if (currentWriteTimeUtc <= this.lastConfigWriteTimeUtc)
            return;

        this.config = this.Helper.ReadConfig<ModConfig>();
        this.lastConfigWriteTimeUtc = currentWriteTimeUtc;
        this.Monitor.Log("Reloaded config.json.", LogLevel.Trace);
    }

    private DateTime GetConfigWriteTimeUtc()
    {
        string path = Path.Combine(this.Helper.DirectoryPath, "config.json");
        return File.Exists(path)
            ? File.GetLastWriteTimeUtc(path)
            : DateTime.MinValue;
    }

    private void TryAutoHookFish()
    {
        if (Game1.player?.CurrentTool is not FishingRod fishingRod)
        {
            this.handledCurrentBite = false;
            return;
        }

        // isNibbling is the bite window (the "!" prompt) where the player is meant to click.
        // fishingRod.hit only turns true *after* the fish is already hooked, so gating on it
        // meant the auto-hook never fired before a manual click.
        if (!fishingRod.isNibbling)
        {
            this.handledCurrentBite = false;
            return;
        }

        if (this.handledCurrentBite || fishingRod.hit || fishingRod.isReeling || fishingRod.pullingOutOfWater || fishingRod.fishCaught)
            return;

        this.handledCurrentBite = true;

        // Mirror the game's own Auto-Hook enchantment: hooking is performed by invoking the
        // rod's DoFunction during the nibble, which is more reliable than simulating a click.
        Farmer who = Game1.player;
        fishingRod.DoFunction(who.currentLocation, (int)fishingRod.bobber.X, (int)fishingRod.bobber.Y, 1, who);
    }

    private void FollowFish(BobberBar bobberBar)
    {
        float fishPosition = this.Helper.Reflection.GetField<float>(bobberBar, "bobberPosition").GetValue();
        float barPosition = this.Helper.Reflection.GetField<float>(bobberBar, "bobberBarPos").GetValue();
        int barHeight = this.Helper.Reflection.GetField<int>(bobberBar, "bobberBarHeight").GetValue();

        float targetPosition = fishPosition - barHeight / 2f;
        targetPosition = ClampToFishingArea(targetPosition, bobberBar.height, barHeight);

        this.MoveBarToTarget(bobberBar, barPosition, targetPosition, this.config.FollowStrength);
    }

    private bool TryFollowTreasure(BobberBar bobberBar)
    {
        bool hasTreasure = this.Helper.Reflection.GetField<bool>(bobberBar, "treasure").GetValue();
        bool treasureCaught = this.Helper.Reflection.GetField<bool>(bobberBar, "treasureCaught").GetValue();

        if (!hasTreasure || treasureCaught)
            return false;

        float barPosition = this.Helper.Reflection.GetField<float>(bobberBar, "bobberBarPos").GetValue();
        int barHeight = this.Helper.Reflection.GetField<int>(bobberBar, "bobberBarHeight").GetValue();
        float treasurePosition = this.Helper.Reflection.GetField<float>(bobberBar, "treasurePosition").GetValue();

        // The treasure icon is roughly 64px tall, so aim the bar at its center.
        float targetPosition = treasurePosition + 32f - barHeight / 2f;
        targetPosition = ClampToFishingArea(targetPosition, bobberBar.height, barHeight);

        this.MoveBarToTarget(bobberBar, barPosition, targetPosition, this.config.TreasureFollowStrength);

        return true;
    }

    private void MoveBarToTarget(BobberBar bobberBar, float barPosition, float targetPosition, float rawStrength)
    {
        float strength = Math.Clamp(rawStrength, 0.05f, 1f);
        float nextPosition = barPosition + (targetPosition - barPosition) * strength;

        this.Helper.Reflection.GetField<float>(bobberBar, "bobberBarPos").SetValue(nextPosition);

        if (this.config.FreezeBarMomentum)
            this.Helper.Reflection.GetField<float>(bobberBar, "bobberBarSpeed").SetValue(0f);
    }

    private void KeepCatchProgressHigh(BobberBar bobberBar, float minimumCatchProgress)
    {
        IReflectedField<float> progressField = this.Helper.Reflection.GetField<float>(bobberBar, "distanceFromCatching");
        float currentProgress = progressField.GetValue();
        float minimumProgress = Math.Clamp(minimumCatchProgress, 0f, 0.95f);

        if (currentProgress < minimumProgress)
            progressField.SetValue(minimumProgress);
    }

    private static float ClampToFishingArea(float position, int menuHeight, int barHeight)
    {
        int usableHeight = menuHeight > 0 ? menuHeight : 568;
        return Math.Clamp(position, 0f, Math.Max(0f, usableHeight - barHeight));
    }
}
