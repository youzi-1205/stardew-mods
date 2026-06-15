using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.ItemTypeDefinitions;

namespace BirthdayGiftReminder;

internal sealed class ModEntry : Mod
{
    private readonly Dictionary<string, string> categoryLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["-2"] = "宝石",
        ["-4"] = "鱼类",
        ["-5"] = "蛋类",
        ["-6"] = "奶类",
        ["-7"] = "料理",
        ["-12"] = "矿物",
        ["-14"] = "肉类",
        ["-15"] = "资源",
        ["-17"] = "种子",
        ["-18"] = "动物产品",
        ["-20"] = "垃圾",
        ["-21"] = "诱饵",
        ["-22"] = "鱼钩",
        ["-23"] = "鱼具",
        ["-24"] = "装饰物",
        ["-25"] = "家具",
        ["-26"] = "工匠物品",
        ["-27"] = "糖浆",
        ["-28"] = "怪物掉落",
        ["-74"] = "种子",
        ["-75"] = "蔬菜",
        ["-79"] = "水果",
        ["-80"] = "花",
        ["-81"] = "采集物"
    };

    private readonly Dictionary<string, string> tagLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ancient_item"] = "古物",
        ["book_item"] = "书类物品",
        ["doll_item"] = "玩偶",
        ["edible_mushroom"] = "可食用蘑菇",
        ["forage_item_beach"] = "海滩采集物",
        ["toy_item"] = "玩具"
    };

    private ModConfig config = new();

    public override void Entry(IModHelper helper)
    {
        this.config = helper.ReadConfig<ModConfig>();

        helper.Events.GameLoop.DayStarted += this.OnDayStarted;
        helper.ConsoleCommands.Add(
            "bgr_today",
            "Show today's birthday and gift reminder again.",
            (_, _) => this.ShowTodayBirthdayReminders()
        );
    }

    private void OnDayStarted(object? sender, DayStartedEventArgs e)
    {
        this.ShowTodayBirthdayReminders();
    }

    private void ShowTodayBirthdayReminders()
    {
        if (!Context.IsWorldReady)
            return;

        this.config = this.Helper.ReadConfig<ModConfig>();

        Dictionary<string, string> giftTastes = this.LoadGiftTastes();
        string todaySeason = Utility.getSeasonKey(Game1.season);

        foreach (KeyValuePair<string, StardewValley.GameData.Characters.CharacterData> character in Game1.characterData)
        {
            string characterId = character.Key;
            StardewValley.GameData.Characters.CharacterData data = character.Value;

            if (data.BirthDay != Game1.dayOfMonth)
                continue;

            if (!string.Equals(data.BirthSeason?.ToString(), todaySeason, StringComparison.OrdinalIgnoreCase))
                continue;

            if (this.config.OnlyShowCharactersWithGiftData && !giftTastes.ContainsKey(characterId))
                continue;

            string displayName = NPC.GetDisplayName(characterId);
            GiftSuggestions suggestions = this.GetGiftSuggestions(characterId, giftTastes);
            this.ShowReminder(displayName, suggestions);
        }
    }

    private Dictionary<string, string> LoadGiftTastes()
    {
        try
        {
            return Game1.content.Load<Dictionary<string, string>>("Data/NPCGiftTastes");
        }
        catch (Exception ex)
        {
            this.Monitor.Log($"Couldn't load Data/NPCGiftTastes: {ex}", LogLevel.Warn);
            return new Dictionary<string, string>();
        }
    }

    private GiftSuggestions GetGiftSuggestions(string characterId, Dictionary<string, string> giftTastes)
    {
        if (!giftTastes.TryGetValue(characterId, out string? rawTastes))
            return new GiftSuggestions(Array.Empty<string>(), Array.Empty<string>());

        string[] fields = rawTastes.Split('/');
        IReadOnlyList<string> loved = fields.Length > 1
            ? this.ParseGiftList(fields[1])
            : Array.Empty<string>();
        IReadOnlyList<string> liked = fields.Length > 3
            ? this.ParseGiftList(fields[3])
            : Array.Empty<string>();

        return new GiftSuggestions(loved, liked);
    }

    private IReadOnlyList<string> ParseGiftList(string rawList)
    {
        return rawList
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(this.GetGiftDisplayName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(Math.Max(1, this.config.MaxGiftsPerTaste))
            .ToArray();
    }

    private string GetGiftDisplayName(string token)
    {
        if (this.categoryLabels.TryGetValue(token, out string? categoryLabel))
            return categoryLabel;

        if (this.tagLabels.TryGetValue(token, out string? tagLabel))
            return tagLabel;

        if (token.StartsWith("category_", StringComparison.OrdinalIgnoreCase))
            return token.Replace("category_", "类别: ").Replace('_', ' ');

        string qualifiedId = token.StartsWith('(') ? token : $"(O){token}";

        // allowNull: invalid ids (or tag/category tokens) return null instead of an Error Item.
        // The Error Item display name is localized (e.g. "错误物品" in Chinese), so string matching can't reliably filter it.
        if (ItemRegistry.Create(qualifiedId, allowNull: true) is Item item && !string.IsNullOrWhiteSpace(item.DisplayName))
            return item.DisplayName;

        // Unrecognized context tags (food_*, season_*, color_*, item_*, ...) would otherwise leak raw English.
        // Return empty so the caller's IsNullOrWhiteSpace filter drops them.
        return string.Empty;
    }

    private void ShowReminder(string displayName, GiftSuggestions suggestions)
    {
        List<string> lines = new() { $"今天是 {displayName} 的生日！" };

        if (this.config.ShowLovedGifts && suggestions.Loved.Count > 0)
            lines.Add($"最爱: {string.Join(", ", suggestions.Loved)}");

        if (this.config.ShowLikedGifts && suggestions.Liked.Count > 0)
            lines.Add($"喜欢: {string.Join(", ", suggestions.Liked)}");

        if (lines.Count == 1)
            lines.Add("没找到可显示的礼物喜好数据。");

        foreach (string line in lines)
            Game1.addHUDMessage(new HUDMessage(line, HUDMessage.newQuest_type));

        this.Monitor.Log(string.Join(" | ", lines), LogLevel.Info);
    }
}

internal sealed record GiftSuggestions(IReadOnlyList<string> Loved, IReadOnlyList<string> Liked);
