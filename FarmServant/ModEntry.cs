using StardewModdingAPI;

namespace FarmServant;

internal sealed class ModEntry : Mod
{
    private ModConfig config = new();

    public override void Entry(IModHelper helper)
    {
        this.config = helper.ReadConfig<ModConfig>();
        _ = new FarmhandHelper(helper, this.Monitor, () => this.config);
    }
}
