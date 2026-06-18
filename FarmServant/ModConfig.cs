namespace FarmServant;

internal sealed class ModConfig
{
    // Enable the farmhand helper NPC on the farm.
    public bool EnableHelper { get; set; } = true;

    // Display name of the helper.
    public string HelperName { get; set; } = "鹏神";

    // The helper replants harvested crops when matching seeds are found in chests.
    public bool HelperReplants { get; set; } = true;

    // The helper picks the best fertilizer from chests for each crop based on value and time left.
    public bool HelperFertilizes { get; set; } = true;

    // Key that calls the helper command menu.
    public string HelperCallKey { get; set; } = "H";

    // Daily rounds also clear debris (weeds/twigs/stones; never mature trees).
    public bool HelperClearsDebris { get; set; } = true;

    // The helper pets, feeds, and collects ready animal produce.
    public bool HelperTendsAnimals { get; set; } = true;

    // The helper picks up loose animal products from barns/coops.
    public bool HelperCollectsAnimalProducts { get; set; } = true;

    // Radius around the player scanned when calling the helper.
    public int HelperWorkRadius { get; set; } = 12;

    // Walk speed of the helper (player walk = 2, player run = 5). Allowed range 2–12.
    public int HelperSpeed { get; set; } = 8;

    // Work speed multiplier: higher = faster. 4.0 = each task takes a quarter of the base time.
    public float HelperWorkSpeedMultiplier { get; set; } = 4.0f;

    // Automatically start the daily farm chores in the morning without being asked.
    public bool HelperAutoStartDaily { get; set; } = true;

    // After clearing debris, the helper picks up the drops and hauls them to a matching chest.
    public bool HelperHaulsDrops { get; set; } = true;

    // The helper collects finished machine outputs into chests.
    public bool HelperCollectsMachines { get; set; } = true;

    // Menu command: scythe farm grass into the silo as hay (only while the silo has room).
    public bool HelperCutsGrass { get; set; } = true;

    // Character sprite sheet used by the helper.
    public string HelperSprite { get; set; } = "Characters\\FarmSuiteHelper";
}
