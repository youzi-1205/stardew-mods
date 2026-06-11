using StardewModdingAPI;
using StardewValley;
using StardewValley.Inventories;
using StardewValley.Objects;

namespace ChestHub;

internal sealed class ModEntry : Mod
{
    private ModConfig config = new();

    public override void Entry(IModHelper helper)
    {
        this.config = helper.ReadConfig<ModConfig>();

        _ = new CraftFromChests(helper, this.Monitor, () => this.config);
        _ = new MachineRefill(helper, this.Monitor, () => this.config);
        _ = new ChestPayFeature(helper);
        _ = new StockOverviewFeature(helper, () => this.config);

        // One-time rescue: the retired pickup-truck mod kept cargo in a team global inventory.
        // If anything is still in there, move it into farm chests so it isn't stranded.
        helper.Events.GameLoop.DayStarted += (_, _) => this.RescueTruckCargo();
    }

    private void RescueTruckCargo()
    {
        if (!Context.IsMainPlayer)
            return;

        if (!Game1.player.team.globalInventories.TryGetValue("Codex.FarmSuite/TruckCargo", out Inventory? cargo)
            || cargo == null || !cargo.HasAny())
        {
            return;
        }

        Farm farm = Game1.getFarm();
        var chests = new List<Chest>();
        foreach (StardewValley.Object obj in farm.objects.Values)
        {
            if (obj is Chest chest && chest.playerChest.Value
                && chest.SpecialChestType is Chest.SpecialChestTypes.None or Chest.SpecialChestTypes.BigChest)
            {
                chests.Add(chest);
            }
        }

        int moved = 0;
        for (int i = cargo.Count - 1; i >= 0; i--)
        {
            Item? item = cargo[i];
            if (item == null)
            {
                cargo.RemoveAt(i);
                continue;
            }

            Item? remaining = item;
            foreach (Chest chest in chests)
            {
                remaining = chest.addItem(remaining);
                if (remaining == null)
                    break;
            }

            if (remaining != null)
                Game1.createItemDebris(remaining, Game1.player.Position, -1, Game1.player.currentLocation);

            cargo.RemoveAt(i);
            moved++;
        }

        if (moved > 0)
        {
            Game1.addHUDMessage(HUDMessage.ForCornerTextbox($"小皮卡已退役：货箱里的 {moved} 组物品已搬回农场箱子。"));
            this.Monitor.Log($"Rescued {moved} item stacks from the retired truck cargo.", LogLevel.Info);
        }
    }
}
