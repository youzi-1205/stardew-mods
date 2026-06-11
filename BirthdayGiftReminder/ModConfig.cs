namespace BirthdayGiftReminder;

internal sealed class ModConfig
{
    public int MaxGiftsPerTaste { get; set; } = 6;

    public bool ShowLovedGifts { get; set; } = true;

    public bool ShowLikedGifts { get; set; } = true;

    public bool OnlyShowCharactersWithGiftData { get; set; } = true;
}
