namespace AutoFish;

internal sealed class ModConfig
{
    public bool EnableMod { get; set; } = true;

    public bool AutoFollowFish { get; set; } = true;

    public bool AutoHookFish { get; set; } = true;

    public bool PrioritizeTreasureChest { get; set; } = true;

    public float FollowStrength { get; set; } = 1.0f;

    public float TreasureFollowStrength { get; set; } = 1.0f;

    public bool FreezeBarMomentum { get; set; } = true;

    public bool KeepCatchProgressFromDropping { get; set; } = false;

    public float MinimumCatchProgress { get; set; } = 0.35f;

    public bool ProtectFishProgressWhileCatchingTreasure { get; set; } = true;

    public float TreasureFishProgressFloor { get; set; } = 0.75f;

    // Shows a clickable "auto-fish" button next to the player after they've fished
    // in the current location. Clicking it auto-casts and fishes hands-free.
    public bool ShowAutoFishButton { get; set; } = true;

    public string AutoFishButtonLabel { get; set; } = "自动钓鱼";

    public string AutoFishButtonActiveLabel { get; set; } = "停止钓鱼";

    // Cast power used by auto-fishing (0 = shortest, 1 = max distance). Farther casts reach
    // deeper open water, which improves fish quality/size and treasure odds.
    public float AutoCastPower { get; set; } = 1.0f;

    // Slightly boosts fishing treasure without replacing the vanilla loot table.
    public bool BoostFishingTreasureRarity { get; set; } = true;

    // Vanilla starts at 0.15 before luck/bait/tackle/profession bonuses. 1.20 means 0.18.
    public float TreasureChestChanceMultiplier { get; set; } = 1.20f;

    // Extra chance per opened fishing treasure chest to add one non-weapon rare reward.
    public float BonusRareTreasureChance { get; set; } = 0.08f;

    // Extra chance per opened fishing treasure chest to add a vanilla fishing weapon.
    public float WeaponTreasureBonusChance { get; set; } = 0.035f;

    public bool RespectUniqueFishingWeapons { get; set; } = true;

    public int MinimumFishingLevelForWeaponBoost { get; set; } = 2;

    // Temporarily makes the two vanilla fishing weapons much easier to get. Once both are
    // owned, this catch-up boost stops and the normal treasure settings apply again.
    public bool PrioritizeMissingFishingWeapons { get; set; } = true;

    public float MissingFishingWeaponChestChanceMultiplier { get; set; } = 4.00f;

    public float MissingFishingWeaponBonusChance { get; set; } = 0.95f;
}
