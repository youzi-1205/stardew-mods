namespace ChestHub;

internal sealed class ModConfig
{
    // ── 制作取料 ──────────────────────────────────────────────────────────────

    // Use materials from the player's chests when crafting/cooking.
    public bool CraftFromChests { get; set; } = true;

    // If true, scan chests in every location; if false, only the farm + farmhouse.
    public bool CraftFromChestsEverywhere { get; set; } = true;

    // ── 一键补货 ──────────────────────────────────────────────────────────────

    // Show a "一键补货" button next to an empty machine: refills it from your chests and starts it.
    public bool MachineRefillButton { get; set; } = true;

    // ── 物品总览 ──────────────────────────────────────────────────────────────

    // Key that opens the stock overview.
    public string StockKey { get; set; } = "K";
}
