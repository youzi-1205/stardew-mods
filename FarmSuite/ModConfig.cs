namespace FarmSuite;

internal sealed class ModConfig
{
    // ── Feature 1: craft from chests ──────────────────────────────────────────

    // Use materials from the player's chests when crafting/cooking.
    public bool CraftFromChests { get; set; } = true;

    // If true, scan chests in every location; if false, only the farm + farmhouse.
    public bool CraftFromChestsEverywhere { get; set; } = true;

    // ── Feature 2: farmhand helper ────────────────────────────────────────────

    // Enable the farmhand helper NPC on the farm.
    public bool EnableHelper { get; set; } = true;

    // Display name of the helper.
    public string HelperName { get; set; } = "仆人";

    // The helper replants harvested crops when matching seeds are found in chests.
    public bool HelperReplants { get; set; } = true;

    // The helper applies fertilizer from chests to unfertilized tilled soil.
    public bool HelperFertilizes { get; set; } = true;

    // Key that calls the helper to the player for nearby work.
    public string HelperCallKey { get; set; } = "H";

    // The helper can clear nearby trees, stones, twigs, and weeds when called.
    public bool HelperClearsDebris { get; set; } = true;

    // The helper pets, feeds, and collects ready animal produce.
    public bool HelperTendsAnimals { get; set; } = true;

    // The helper picks up loose animal products from barns/coops and nearby farm ground.
    public bool HelperCollectsAnimalProducts { get; set; } = true;

    // Radius around the player scanned when calling the helper.
    public int HelperWorkRadius { get; set; } = 12;

    // Walk speed of the helper (player walk = 2, player run = 5).
    public int HelperSpeed { get; set; } = 5;

    // Work speed multiplier: 2.0 = each task takes half the base time.
    public float HelperWorkSpeedMultiplier { get; set; } = 2.0f;

    // Automatically start the daily farm chores in the morning without being asked.
    public bool HelperAutoStartDaily { get; set; } = true;

    // After clearing debris, the helper picks up the drops (wood/stone/fiber...) and hauls them
    // to a chest — preferring the nearest chest that already holds the same item.
    public bool HelperHaulsDrops { get; set; } = true;

    // Character sprite sheet used by the helper.
    public string HelperSprite { get; set; } = "Characters\\FarmSuiteHelper";

    // ── Feature 3: pickup truck ───────────────────────────────────────────────

    // Enable the purchasable pickup truck.
    public bool EnableTruck { get; set; } = true;

    // Purchase price at Robin's carpenter shop.
    public int TruckPrice { get; set; } = 50000;

    // Extra speed while driving (added on top of horse-riding speed).
    public float TruckSpeedBonus { get; set; } = 3.0f;

    // Key that opens the truck's cargo bed (while driving or standing within 3 tiles).
    public string TruckCargoKey { get; set; } = "U";
}
