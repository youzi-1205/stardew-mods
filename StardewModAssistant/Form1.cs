using System.Diagnostics;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace StardewModAssistant;

public sealed class Form1 : Form
{
    private const string GameDir = @"D:\app\steam\steamapps\common\Stardew Valley";
    private const string SmapiPath = @"D:\app\steam\steamapps\common\Stardew Valley\StardewModdingAPI.exe";
    private const string ModsDir = @"D:\app\steam\steamapps\common\Stardew Valley\Mods";
    private const string SourceRoot = @"D:\gamescript";

    private readonly string[] steamArgs;
    private readonly JsonSerializerOptions jsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private CheckBox enableFishing = null!;
    private CheckBox autoHook = null!;
    private CheckBox autoFollow = null!;
    private CheckBox treasureFirst = null!;
    private CheckBox freezeMomentum = null!;
    private CheckBox protectTreasureProgress = null!;
    private CheckBox keepProgress = null!;
    private TrackBar followStrength = null!;
    private TrackBar treasureStrength = null!;
    private TrackBar treasureFloor = null!;
    private TrackBar minimumProgress = null!;
    private Label followStrengthValue = null!;
    private Label treasureStrengthValue = null!;
    private Label treasureFloorValue = null!;
    private Label minimumProgressValue = null!;

    private CheckBox showLoved = null!;
    private CheckBox showLiked = null!;
    private CheckBox onlyGiftData = null!;
    private NumericUpDown maxGifts = null!;

    private Label statusLabel = null!;
    private bool loading;

    public Form1(string[] args)
    {
        this.steamArgs = args;

        this.Text = "Stardew Mod Assistant";
        this.StartPosition = FormStartPosition.CenterScreen;
        this.MinimumSize = new Size(920, 620);
        this.Size = new Size(980, 680);
        this.BackColor = Color.FromArgb(246, 242, 235);
        this.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular);

        this.BuildUi();
        this.LoadConfigs();
        this.UpdateStatus();

        System.Windows.Forms.Timer timer = new() { Interval = 1500 };
        timer.Tick += (_, _) => this.UpdateStatus();
        timer.Start();
    }

    private string FishingConfigPath => Path.Combine(ModsDir, "FishingAssist", "config.json");

    private string FishingSourceConfigPath => Path.Combine(SourceRoot, "FishingAssist", "config.json");

    private string BirthdayConfigPath => Path.Combine(ModsDir, "BirthdayGiftReminder", "config.json");

    private string BirthdaySourceConfigPath => Path.Combine(SourceRoot, "BirthdayGiftReminder", "config.json");

    private void BuildUi()
    {
        TableLayoutPanel root = new()
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Padding = new Padding(18)
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 280));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        this.Controls.Add(root);

        Panel side = new()
        {
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(42, 67, 72),
            Padding = new Padding(18)
        };
        root.Controls.Add(side, 0, 0);

        FlowLayoutPanel sideFlow = new()
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false
        };
        side.Controls.Add(sideFlow);

        Label title = new()
        {
            Text = "星露谷 Mod 助手",
            ForeColor = Color.White,
            Font = new Font("Microsoft YaHei UI", 17F, FontStyle.Bold),
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 8)
        };
        sideFlow.Controls.Add(title);

        Label subtitle = new()
        {
            Text = "启动游戏、隐藏 SMAPI 控制台、调整辅助选项。",
            ForeColor = Color.FromArgb(216, 230, 224),
            MaximumSize = new Size(230, 0),
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 24)
        };
        sideFlow.Controls.Add(subtitle);

        Button launchButton = this.CreateSideButton("启动游戏");
        launchButton.Click += (_, _) => this.StartGameHidden();
        sideFlow.Controls.Add(launchButton);

        Button modsButton = this.CreateSideButton("打开 Mods 文件夹");
        modsButton.Click += (_, _) => Process.Start(new ProcessStartInfo(ModsDir) { UseShellExecute = true });
        sideFlow.Controls.Add(modsButton);

        Button scriptButton = this.CreateSideButton("打开脚本文件夹");
        scriptButton.Click += (_, _) => Process.Start(new ProcessStartInfo(SourceRoot) { UseShellExecute = true });
        sideFlow.Controls.Add(scriptButton);

        this.statusLabel = new Label()
        {
            ForeColor = Color.FromArgb(238, 235, 208),
            MaximumSize = new Size(230, 0),
            AutoSize = true,
            Margin = new Padding(0, 28, 0, 0)
        };
        sideFlow.Controls.Add(this.statusLabel);

        TabControl tabs = new()
        {
            Dock = DockStyle.Fill,
            Padding = new Point(16, 8)
        };
        root.Controls.Add(tabs, 1, 0);

        TabPage fishingTab = new("钓鱼辅助") { BackColor = Color.FromArgb(246, 242, 235), Padding = new Padding(16) };
        tabs.TabPages.Add(fishingTab);
        this.BuildFishingTab(fishingTab);

        TabPage birthdayTab = new("生日提醒") { BackColor = Color.FromArgb(246, 242, 235), Padding = new Padding(16) };
        tabs.TabPages.Add(birthdayTab);
        this.BuildBirthdayTab(birthdayTab);
    }

    private Button CreateSideButton(string text)
    {
        Button button = new()
        {
            Text = text,
            Width = 230,
            Height = 42,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(233, 181, 85),
            ForeColor = Color.FromArgb(38, 36, 30),
            Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold),
            Margin = new Padding(0, 0, 0, 12)
        };
        button.FlatAppearance.BorderSize = 0;
        return button;
    }

    private void BuildFishingTab(Control parent)
    {
        FlowLayoutPanel panel = new()
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true
        };
        parent.Controls.Add(panel);

        this.enableFishing = this.AddCheck(panel, "启用钓鱼辅助");
        this.autoHook = this.AddCheck(panel, "自动提竿");
        this.autoFollow = this.AddCheck(panel, "自动跟随鱼");
        this.treasureFirst = this.AddCheck(panel, "宝箱优先");
        this.freezeMomentum = this.AddCheck(panel, "清除绿条惯性");
        this.protectTreasureProgress = this.AddCheck(panel, "追宝箱时保住鱼进度");
        this.keepProgress = this.AddCheck(panel, "始终防止鱼进度掉太低");

        this.followStrength = this.AddSlider(panel, "跟鱼力度", out this.followStrengthValue);
        this.treasureStrength = this.AddSlider(panel, "跟宝箱力度", out this.treasureStrengthValue);
        this.treasureFloor = this.AddSlider(panel, "追宝箱时鱼进度保底", out this.treasureFloorValue);
        this.minimumProgress = this.AddSlider(panel, "普通鱼进度保底", out this.minimumProgressValue);

        this.WireFishingConfigEvents();

        Label hint = new()
        {
            Text = "这些选项会写入 FishingAssist/config.json。游戏运行中也会自动重新读取，通常一秒内生效。",
            AutoSize = true,
            MaximumSize = new Size(620, 0),
            ForeColor = Color.FromArgb(92, 78, 62),
            Margin = new Padding(0, 18, 0, 0)
        };
        panel.Controls.Add(hint);
    }

    private void BuildBirthdayTab(Control parent)
    {
        FlowLayoutPanel panel = new()
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true
        };
        parent.Controls.Add(panel);

        this.showLoved = this.AddCheck(panel, "显示最爱礼物");
        this.showLiked = this.AddCheck(panel, "显示喜欢礼物");
        this.onlyGiftData = this.AddCheck(panel, "只提醒有礼物数据的角色");

        Label maxLabel = new()
        {
            Text = "每类最多显示礼物数量",
            AutoSize = true,
            Margin = new Padding(0, 16, 0, 6),
            Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold)
        };
        panel.Controls.Add(maxLabel);

        this.maxGifts = new NumericUpDown()
        {
            Minimum = 1,
            Maximum = 20,
            Width = 120,
            Height = 32,
            Margin = new Padding(0, 0, 0, 10)
        };
        this.maxGifts.ValueChanged += (_, _) => this.SaveBirthdayConfigIfReady();
        panel.Controls.Add(this.maxGifts);

        Label hint = new()
        {
            Text = "生日提醒只在每天早上触发。改这里的选项后，下次触发提醒时会使用新配置。",
            AutoSize = true,
            MaximumSize = new Size(620, 0),
            ForeColor = Color.FromArgb(92, 78, 62),
            Margin = new Padding(0, 18, 0, 0)
        };
        panel.Controls.Add(hint);
    }

    private CheckBox AddCheck(Control parent, string text)
    {
        CheckBox box = new()
        {
            Text = text,
            AutoSize = true,
            Margin = new Padding(0, 7, 0, 7),
            Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Regular)
        };
        parent.Controls.Add(box);
        return box;
    }

    private TrackBar AddSlider(Control parent, string text, out Label valueLabel)
    {
        Panel row = new()
        {
            Width = 640,
            Height = 72,
            Margin = new Padding(0, 10, 0, 0)
        };
        parent.Controls.Add(row);

        Label label = new()
        {
            Text = text,
            Location = new Point(0, 0),
            AutoSize = true,
            Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold)
        };
        row.Controls.Add(label);

        valueLabel = new Label()
        {
            Text = "1.00",
            Location = new Point(560, 0),
            Width = 70,
            TextAlign = ContentAlignment.TopRight
        };
        row.Controls.Add(valueLabel);

        TrackBar bar = new()
        {
            Minimum = 0,
            Maximum = 100,
            TickFrequency = 10,
            Width = 620,
            Location = new Point(0, 25)
        };
        row.Controls.Add(bar);

        return bar;
    }

    private void WireFishingConfigEvents()
    {
        foreach (CheckBox box in new[] {
            this.enableFishing, this.autoHook, this.autoFollow, this.treasureFirst,
            this.freezeMomentum, this.protectTreasureProgress, this.keepProgress
        })
        {
            box.CheckedChanged += (_, _) => this.SaveFishingConfigIfReady();
        }

        foreach (TrackBar bar in new[] { this.followStrength, this.treasureStrength, this.treasureFloor, this.minimumProgress })
            bar.ValueChanged += (_, _) => this.SaveFishingConfigIfReady();
    }

    private void LoadConfigs()
    {
        this.loading = true;
        try
        {
            FishingConfig fishing = this.ReadJson<FishingConfig>(this.FishingConfigPath) ?? new FishingConfig();

            this.enableFishing.Checked = fishing.EnableMod;
            this.autoHook.Checked = fishing.AutoHookFish;
            this.autoFollow.Checked = fishing.AutoFollowFish;
            this.treasureFirst.Checked = fishing.PrioritizeTreasureChest;
            this.freezeMomentum.Checked = fishing.FreezeBarMomentum;
            this.protectTreasureProgress.Checked = fishing.ProtectFishProgressWhileCatchingTreasure;
            this.keepProgress.Checked = fishing.KeepCatchProgressFromDropping;
            this.SetSlider(this.followStrength, this.followStrengthValue, fishing.FollowStrength);
            this.SetSlider(this.treasureStrength, this.treasureStrengthValue, fishing.TreasureFollowStrength);
            this.SetSlider(this.treasureFloor, this.treasureFloorValue, fishing.TreasureFishProgressFloor);
            this.SetSlider(this.minimumProgress, this.minimumProgressValue, fishing.MinimumCatchProgress);

            BirthdayConfig birthday = this.ReadJson<BirthdayConfig>(this.BirthdayConfigPath) ?? new BirthdayConfig();
            this.showLoved.Checked = birthday.ShowLovedGifts;
            this.showLiked.Checked = birthday.ShowLikedGifts;
            this.onlyGiftData.Checked = birthday.OnlyShowCharactersWithGiftData;
            this.maxGifts.Value = Math.Clamp(birthday.MaxGiftsPerTaste, 1, 20);

            foreach (CheckBox box in new[] { this.showLoved, this.showLiked, this.onlyGiftData })
                box.CheckedChanged += (_, _) => this.SaveBirthdayConfigIfReady();
        }
        finally
        {
            this.loading = false;
        }
    }

    private void SetSlider(TrackBar slider, Label label, float value)
    {
        int intValue = Math.Clamp((int)Math.Round(value * 100), slider.Minimum, slider.Maximum);
        slider.Value = intValue;
        label.Text = (intValue / 100f).ToString("0.00");
    }

    private void SaveFishingConfigIfReady()
    {
        if (this.loading)
            return;

        this.followStrengthValue.Text = this.SliderValue(this.followStrength).ToString("0.00");
        this.treasureStrengthValue.Text = this.SliderValue(this.treasureStrength).ToString("0.00");
        this.treasureFloorValue.Text = this.SliderValue(this.treasureFloor).ToString("0.00");
        this.minimumProgressValue.Text = this.SliderValue(this.minimumProgress).ToString("0.00");

        FishingConfig config = new()
        {
            EnableMod = this.enableFishing.Checked,
            AutoFollowFish = this.autoFollow.Checked,
            AutoHookFish = this.autoHook.Checked,
            PrioritizeTreasureChest = this.treasureFirst.Checked,
            FollowStrength = this.SliderValue(this.followStrength),
            TreasureFollowStrength = this.SliderValue(this.treasureStrength),
            FreezeBarMomentum = this.freezeMomentum.Checked,
            KeepCatchProgressFromDropping = this.keepProgress.Checked,
            MinimumCatchProgress = this.SliderValue(this.minimumProgress),
            ProtectFishProgressWhileCatchingTreasure = this.protectTreasureProgress.Checked,
            TreasureFishProgressFloor = this.SliderValue(this.treasureFloor)
        };

        this.WriteJson(this.FishingConfigPath, config);
        this.WriteJson(this.FishingSourceConfigPath, config);
        this.UpdateStatus("钓鱼辅助配置已保存。");
    }

    private void SaveBirthdayConfigIfReady()
    {
        if (this.loading)
            return;

        BirthdayConfig config = new()
        {
            MaxGiftsPerTaste = (int)this.maxGifts.Value,
            ShowLovedGifts = this.showLoved.Checked,
            ShowLikedGifts = this.showLiked.Checked,
            OnlyShowCharactersWithGiftData = this.onlyGiftData.Checked
        };

        this.WriteJson(this.BirthdayConfigPath, config);
        this.WriteJson(this.BirthdaySourceConfigPath, config);
        this.UpdateStatus("生日提醒配置已保存。");
    }

    private float SliderValue(TrackBar slider)
    {
        return slider.Value / 100f;
    }

    private T? ReadJson<T>(string path)
    {
        if (!File.Exists(path))
            return default;

        return JsonSerializer.Deserialize<T>(File.ReadAllText(path), this.jsonOptions);
    }

    private void WriteJson<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(value, this.jsonOptions));
    }

    private void StartGameHidden()
    {
        if (!File.Exists(SmapiPath))
        {
            MessageBox.Show("找不到 StardewModdingAPI.exe。", "无法启动", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (Process.GetProcessesByName("StardewModdingAPI").Any())
        {
            this.UpdateStatus("SMAPI 已经在运行。");
            return;
        }

        ProcessStartInfo info = new()
        {
            FileName = SmapiPath,
            WorkingDirectory = GameDir,
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            Arguments = string.Join(" ", this.steamArgs.Select(QuoteArgument))
        };

        Process.Start(info);
        this.UpdateStatus("已隐藏启动 SMAPI。");
    }

    private static string QuoteArgument(string arg)
    {
        if (string.IsNullOrWhiteSpace(arg))
            return "\"\"";

        return arg.Contains(' ') || arg.Contains('"')
            ? $"\"{arg.Replace("\"", "\\\"")}\""
            : arg;
    }

    private void UpdateStatus(string? extra = null)
    {
        bool smapiRunning = Process.GetProcessesByName("StardewModdingAPI").Any();
        bool gameRunning = Process.GetProcessesByName("Stardew Valley").Any();
        string state = smapiRunning
            ? "SMAPI 正在运行，Mod 已启用。"
            : gameRunning
                ? "普通游戏正在运行，未加载 Mod。"
                : "游戏未运行。";

        this.statusLabel.Text = extra is null ? state : $"{extra}\n{state}";
    }
}

internal sealed class FishingConfig
{
    public bool EnableMod { get; set; } = true;
    public bool AutoFollowFish { get; set; } = true;
    public bool AutoHookFish { get; set; } = true;
    public bool PrioritizeTreasureChest { get; set; } = true;
    public float FollowStrength { get; set; } = 1.0f;
    public float TreasureFollowStrength { get; set; } = 1.0f;
    public bool FreezeBarMomentum { get; set; } = true;
    public bool KeepCatchProgressFromDropping { get; set; }
    public float MinimumCatchProgress { get; set; } = 0.35f;
    public bool ProtectFishProgressWhileCatchingTreasure { get; set; } = true;
    public float TreasureFishProgressFloor { get; set; } = 0.75f;
}

internal sealed class BirthdayConfig
{
    public int MaxGiftsPerTaste { get; set; } = 6;
    public bool ShowLovedGifts { get; set; } = true;
    public bool ShowLikedGifts { get; set; } = true;
    public bool OnlyShowCharactersWithGiftData { get; set; } = true;
}
