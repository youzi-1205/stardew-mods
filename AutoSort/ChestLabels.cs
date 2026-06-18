using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Objects;

namespace AutoSort;

/// <summary>在当前地点每个“归属箱”头顶画出它的分类名。分类名由 <see cref="AutoSorter"/> 整理时
/// 写进箱子的 <c>modData</c>（随存档持久、多人自动同步），这里只负责读取并绘制，不做任何判断。</summary>
internal sealed class ChestLabels
{
    /// <summary>箱子 modData 里存放分类名的键。</summary>
    public const string CategoryKey = "Codex.AutoSort/Category";

    private readonly Func<ModConfig> config;

    public ChestLabels(IModHelper helper, Func<ModConfig> config)
    {
        this.config = config;
        helper.Events.Display.RenderedWorld += this.OnRenderedWorld;
    }

    private void OnRenderedWorld(object? sender, RenderedWorldEventArgs e)
    {
        if (!this.config().ShowChestLabels || !Context.IsWorldReady || Game1.currentLocation == null)
            return;

        SpriteBatch b = e.SpriteBatch;
        foreach (var pair in Game1.currentLocation.objects.Pairs)
        {
            if (pair.Value is Chest chest
                && chest.modData.TryGetValue(CategoryKey, out string? label)
                && !string.IsNullOrEmpty(label))
            {
                DrawLabel(b, pair.Key, label);
            }
        }
    }

    /// <summary>在某个箱子格子的正上方画一个半透明小标签。</summary>
    private static void DrawLabel(SpriteBatch b, Vector2 tile, string text)
    {
        const float scale = 0.75f;
        const int pad = 5;

        // 格子顶部中点的屏幕坐标。
        Vector2 screen = Game1.GlobalToLocal(Game1.viewport, new Vector2(tile.X * 64f + 32f, tile.Y * 64f));
        Vector2 size = Game1.smallFont.MeasureString(text) * scale;

        int w = (int)size.X + pad * 2;
        int h = (int)size.Y + pad * 2;
        int x = (int)(screen.X - w / 2f);
        int y = (int)(screen.Y - 8 - h); // 悬在箱子上方一点

        b.Draw(Game1.staminaRect, new Rectangle(x, y, w, h), Color.Black * 0.5f);

        var textPos = new Vector2(x + pad, y + pad);
        b.DrawString(Game1.smallFont, text, textPos + new Vector2(1f, 1f), Color.Black * 0.5f, 0f, Vector2.Zero, scale, SpriteEffects.None, 1f);
        b.DrawString(Game1.smallFont, text, textPos, Color.White, 0f, Vector2.Zero, scale, SpriteEffects.None, 1f);
    }
}
