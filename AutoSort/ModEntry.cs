using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Menus;
using StardewValley.Objects;

namespace AutoSort;

/// <summary>GMCM 的 API 子集，仅声明本 mod 用到的部分；运行时通过 mod 注册表解析，
/// 因此对 GMCM 的依赖是可选的（不装也能正常工作）。</summary>
public interface IGenericModConfigMenuApi
{
    void Register(IManifest mod, Action reset, Action save, bool titleScreenOnly = false);
    void AddSectionTitle(IManifest mod, Func<string> text, Func<string>? tooltip = null);
    void AddBoolOption(IManifest mod, Func<bool> getValue, Action<bool> setValue, Func<string> name, Func<string>? tooltip = null, string? fieldId = null);
    void AddKeybind(IManifest mod, Func<SButton> getValue, Action<SButton> setValue, Func<string> name, Func<string>? tooltip = null, string? fieldId = null);
}

internal sealed class ModEntry : Mod
{
    private ModConfig config = new();
    private AutoSorter sorter = null!;

    public override void Entry(IModHelper helper)
    {
        this.config = helper.ReadConfig<ModConfig>();
        this.sorter = new AutoSorter(this.Monitor, () => this.config);
        _ = new ChestLabels(helper, () => this.config);

        helper.Events.GameLoop.GameLaunched += this.OnGameLaunched;
        helper.Events.Input.ButtonPressed += this.OnButtonPressed;
        helper.Events.GameLoop.DayStarted += this.OnDayStarted;
        helper.Events.Display.MenuChanged += this.OnMenuChanged;
    }

    private void OnButtonPressed(object? sender, ButtonPressedEventArgs e)
    {
        if (!Context.IsWorldReady || !Context.IsPlayerFree || Game1.activeClickableMenu != null)
            return;
        if (Game1.IsChatting || Game1.keyboardDispatcher.Subscriber != null)
            return;
        if (!Enum.TryParse(this.config.SortKey, ignoreCase: true, out SButton sortKey) || e.Button != sortKey)
            return;

        this.Helper.Input.Suppress(e.Button);
        this.sorter.Organize();
    }

    private void OnDayStarted(object? sender, DayStartedEventArgs e)
    {
        if (this.config.SortOnDayStart)
            this.sorter.Organize();
    }

    private void OnMenuChanged(object? sender, MenuChangedEventArgs e)
    {
        // 关闭一个箱子界面后自动整理（仅当开启该选项）。
        if (this.config.SortOnChestClose && e.NewMenu == null && e.OldMenu is ItemGrabMenu { context: Chest })
            this.sorter.Organize();
    }

    /// <summary>装了 Generic Mod Config Menu 时接入图形配置界面（可选依赖）。</summary>
    private void OnGameLaunched(object? sender, GameLaunchedEventArgs e)
    {
        var gmcm = this.Helper.ModRegistry.GetApi<IGenericModConfigMenuApi>("spacechase0.GenericModConfigMenu");
        if (gmcm is null)
            return;

        gmcm.Register(
            mod: this.ModManifest,
            reset: () => this.config = new ModConfig(),
            save: () => this.Helper.WriteConfig(this.config));

        gmcm.AddSectionTitle(this.ModManifest, () => "自动整理");
        gmcm.AddKeybind(
            this.ModManifest,
            getValue: () => Enum.TryParse(this.config.SortKey, ignoreCase: true, out SButton k) ? k : SButton.L,
            setValue: k => this.config.SortKey = k.ToString(),
            name: () => "整理快捷键",
            tooltip: () => "按此键立即把所有箱子按类别归整。");
        gmcm.AddBoolOption(
            this.ModManifest,
            getValue: () => this.config.SortEverywhere,
            setValue: v => this.config.SortEverywhere = v,
            name: () => "整理全世界的箱子",
            tooltip: () => "关：只整理农场、农场建筑内、农舍里的箱子；开：包含所有地点的箱子。");
        gmcm.AddBoolOption(
            this.ModManifest,
            getValue: () => this.config.SortOnChestClose,
            setValue: v => this.config.SortOnChestClose = v,
            name: () => "关闭箱子后自动整理",
            tooltip: () => "每次关掉一个箱子界面后自动整理一次。");
        gmcm.AddBoolOption(
            this.ModManifest,
            getValue: () => this.config.SortOnDayStart,
            setValue: v => this.config.SortOnDayStart = v,
            name: () => "每天早晨自动整理",
            tooltip: () => "每天起床时自动整理一次。");
        gmcm.AddBoolOption(
            this.ModManifest,
            getValue: () => this.config.ShowSummary,
            setValue: v => this.config.ShowSummary = v,
            name: () => "整理后显示提示",
            tooltip: () => "整理完成后在屏幕角落弹出一条汇总消息。");
        gmcm.AddBoolOption(
            this.ModManifest,
            getValue: () => this.config.ShowChestLabels,
            setValue: v => this.config.ShowChestLabels = v,
            name: () => "箱子头顶显示分类标签",
            tooltip: () => "在每个分类箱上方显示它所装的分类名。");
    }
}
