using StardewModdingAPI;

namespace FarmSuite;

internal sealed class ModEntry : Mod
{
    private ModConfig config = new();

    public override void Entry(IModHelper helper)
    {
        this.config = helper.ReadConfig<ModConfig>();
        this.MigrateConfig(helper);

        _ = new CraftFromChests(helper, this.Monitor, () => this.config);
        _ = new FarmhandHelper(helper, this.Monitor, () => this.config);
        _ = new TruckFeature(helper, this.Monitor, () => this.config);
        _ = new MachineRefill(helper, this.Monitor, () => this.config);
    }

    private void MigrateConfig(IModHelper helper)
    {
        if (!IsAsset(this.config.HelperSprite, "Characters/Lewis"))
            return;

        this.config.HelperSprite = "Characters\\FarmSuiteHelper";
        helper.WriteConfig(this.config);
    }

    private static bool IsAsset(string value, string expected)
    {
        return value.Replace('/', '\\').Equals(expected, StringComparison.OrdinalIgnoreCase);
    }
}
