namespace AutoSort;

internal sealed class ModConfig
{
    // 立即整理所有箱子的快捷键。
    public string SortKey { get; set; } = "L";

    // true = 整理全世界所有地点的箱子；false = 只整理农场、农场建筑内、农舍里的箱子。
    public bool SortEverywhere { get; set; } = false;

    // 关闭任意箱子后自动整理一次。
    public bool SortOnChestClose { get; set; } = false;

    // 每天早晨起床时自动整理一次。
    public bool SortOnDayStart { get; set; } = false;

    // 整理后在屏幕角落弹出汇总提示。
    public bool ShowSummary { get; set; } = true;

    // 在每个分类箱头顶显示它所装的分类标签。
    public bool ShowChestLabels { get; set; } = true;
}
