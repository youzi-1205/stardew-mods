using StardewModdingAPI;
using StardewValley;
using StardewValley.Inventories;
using StardewValley.Locations;
using StardewValley.Objects;

namespace AutoSort;

/// <summary>核心：把作用范围内所有箱子的物品按大类归整到“每类一个（或几个）箱子”。
///
/// 归属箱完全由现有内容智能认领、无需手动设置：
///   1) 内容主导——某分类是某箱子里数量最多的分类，则该箱成为这一类的归属箱；
///   2) 就近聚拢——还没归属的分类，认领“已经装了最多该类物品”的空闲箱子；
///   3) 空箱兜底——仍没着落的分类，认领一个空箱（或剩余空间最大的空闲箱）。
/// 每类先保证拿到一个主箱；剩下的空闲箱进入“溢出池”。搬运时若主箱装满，自动从溢出池
/// 取下一个空箱接力，于是大仓库里同一类可以横跨多个箱子。每个归属箱（含溢出箱）会把
/// 自己的分类名写进 <c>modData</c>，供头顶标签显示、且随存档持久。
/// 全程只移动物品数据、复用原版 <c>Chest.addItem</c>，不需要玩家走动、不写 Harmony 补丁。</summary>
internal sealed class AutoSorter
{
    private readonly IMonitor monitor;
    private readonly Func<ModConfig> config;

    public AutoSorter(IMonitor monitor, Func<ModConfig> config)
    {
        this.monitor = monitor;
        this.config = config;
    }

    public void Organize()
    {
        // 仅主机执行：避免多人下两端同时搬运同一批物品产生竞态。
        if (!Context.IsWorldReady || !Context.IsMainPlayer)
            return;

        List<Chest> chests = this.CollectChests();
        if (chests.Count == 0)
        {
            if (this.config().ShowSummary)
                Game1.addHUDMessage(HUDMessage.ForCornerTextbox("没有找到可整理的箱子。"));
            return;
        }

        Dictionary<string, List<Chest>> homes = AssignHomes(chests, out int bucketsPresent, out List<Chest> freePool);

        // 把每件物品搬到它所属分类的归属箱；主箱满了就从溢出池接力。
        int movedStacks = 0;
        int blockedStacks = 0;
        foreach (Chest source in chests)
        {
            IInventory items = source.GetItemsForPlayer();
            for (int i = items.Count - 1; i >= 0; i--)
            {
                Item? item = items[i];
                if (item == null)
                    continue;

                string bucket = Categorizer.Resolve(item);
                if (!homes.TryGetValue(bucket, out List<Chest>? homeList))
                    continue; // 这一类没有归属箱（类别比箱子多），留在原处
                if (homeList.Contains(source))
                    continue; // 已经在自己分类的归属箱里，保持不动

                int before = item.Stack;
                Item? remaining = item;

                // 先塞进这一类已有的归属箱（主箱 + 已认领的溢出箱）。
                foreach (Chest home in homeList)
                {
                    remaining = home.addItem(remaining);
                    if (remaining == null)
                        break;
                }

                // 还有剩余，就从溢出池为这一类再认领空箱接着装。
                while (remaining != null)
                {
                    Chest? extra = ClaimOverflow(bucket, homes, freePool, source);
                    if (extra == null)
                        break;
                    remaining = extra.addItem(remaining);
                }

                if (remaining == null)
                {
                    items.RemoveAt(i);
                    movedStacks++;
                }
                else if (remaining.Stack < before)
                {
                    items[i] = remaining; // 部分搬走，剩余留在原箱
                    movedStacks++;
                    blockedStacks++;
                }
                else
                {
                    blockedStacks++; // 全部归属箱都满了，整堆原地不动
                }
            }
        }

        // 写入/清除标签，再合并每个归属箱内的零散堆。
        ApplyLabels(chests, homes);
        foreach (List<Chest> list in homes.Values)
        {
            foreach (Chest home in list)
                CompactInventory(home.GetItemsForPlayer());
        }

        int chestCount = homes.Values.Sum(list => list.Count);
        this.Report(homes.Count, chestCount, bucketsPresent, movedStacks, blockedStacks);
    }

    // ── 收集箱子 ──────────────────────────────────────────────────────────────

    private List<Chest> CollectChests()
    {
        bool everywhere = this.config().SortEverywhere;
        var chests = new List<Chest>();
        var seen = new HashSet<Chest>();

        Utility.ForEachLocation(location =>
        {
            if (everywhere || IsHomeBase(location))
            {
                foreach (StardewValley.Object obj in location.objects.Values)
                {
                    if (obj is Chest chest && chest.playerChest.Value && IsSortable(chest) && seen.Add(chest))
                        chests.Add(chest);
                }
            }
            return true;
        }, includeInteriors: true);

        return chests;
    }

    /// <summary>“农场基地”：农场本体、农舍，以及农场上各建筑（牲口棚/小屋/木屋等）的室内。</summary>
    private static bool IsHomeBase(GameLocation location)
        => location is Farm or FarmHouse || location.ParentBuilding?.GetParentLocation() is Farm;

    /// <summary>只整理普通收纳箱（含大箱子）。祝尼魔箱子、料斗等功能性箱子内容另有用途，跳过。</summary>
    private static bool IsSortable(Chest chest)
        => chest.SpecialChestType is Chest.SpecialChestTypes.None or Chest.SpecialChestTypes.BigChest;

    // ── 分配归属箱 ────────────────────────────────────────────────────────────

    /// <summary>给每个分类挑一个主归属箱，并把没用上的箱子按“空箱优先、空间大优先”排成溢出池。</summary>
    private static Dictionary<string, List<Chest>> AssignHomes(List<Chest> chests, out int bucketsPresent, out List<Chest> freePool)
    {
        // 统计每个箱子里各分类的物品数量，以及全局各分类总量。
        var perChest = new Dictionary<Chest, Dictionary<string, int>>();
        var totals = new Dictionary<string, int>();
        foreach (Chest c in chests)
        {
            var counts = new Dictionary<string, int>();
            foreach (Item? item in c.GetItemsForPlayer())
            {
                if (item == null)
                    continue;
                string bucket = Categorizer.Resolve(item);
                counts[bucket] = counts.GetValueOrDefault(bucket) + item.Stack;
                totals[bucket] = totals.GetValueOrDefault(bucket) + item.Stack;
            }
            perChest[c] = counts;
        }

        bucketsPresent = totals.Count;

        var homes = new Dictionary<string, List<Chest>>();
        var claimed = new HashSet<Chest>();

        void AddHome(string bucket, Chest c)
        {
            if (!homes.TryGetValue(bucket, out List<Chest>? list))
            {
                list = new List<Chest>();
                homes[bucket] = list;
            }
            list.Add(c);
            claimed.Add(c);
        }

        // 处理顺序：按全局总量从多到少，让占比最大的分类先挑归属箱。
        List<string> buckets = totals.Keys.OrderByDescending(b => totals[b]).ToList();

        // Pass 0：归属粘住——上次整理打过标签、且该分类现在仍有物品的箱子，继续作为它的归属箱。
        // 这让多次整理结果稳定：溢出箱不会掉标签，分类也不会在箱子间乱跳。
        foreach (Chest c in chests)
        {
            if (!claimed.Contains(c)
                && c.modData.TryGetValue(ChestLabels.CategoryKey, out string? prev)
                && !string.IsNullOrEmpty(prev)
                && totals.ContainsKey(prev))
            {
                AddHome(prev, c);
            }
        }

        // Pass 1：内容主导——该分类是某未占用箱子里数量最多的分类。
        foreach (string bucket in buckets)
        {
            if (homes.ContainsKey(bucket))
                continue;
            Chest? best = PickHolder(chests, claimed, perChest, bucket, requireDominant: true);
            if (best != null)
                AddHome(bucket, best);
        }

        // Pass 2：就近聚拢——认领“已装最多该类”的未占用箱子（不要求该类主导）。
        foreach (string bucket in buckets)
        {
            if (homes.ContainsKey(bucket))
                continue;
            Chest? best = PickHolder(chests, claimed, perChest, bucket, requireDominant: false);
            if (best != null)
                AddHome(bucket, best);
        }

        // Pass 3：空箱兜底——认领一个空箱（优先），否则剩余空间最大的未占用箱。
        foreach (string bucket in buckets)
        {
            if (homes.ContainsKey(bucket))
                continue;
            Chest? best = null;
            foreach (Chest c in chests)
            {
                if (claimed.Contains(c))
                    continue;
                if (best == null || BetterEmpty(c, best))
                    best = c;
            }
            if (best != null)
                AddHome(bucket, best);
            // 否则：箱子不够，这一类没有归属箱，物品留在原处（汇总里会提示）。
        }

        // 没被任何分类选为主箱的箱子 → 溢出池（空箱优先、空间大优先）。
        freePool = chests.Where(c => !claimed.Contains(c)).ToList();
        freePool.Sort(CompareForOverflow);

        return homes;
    }

    /// <summary>在未占用的箱子里，挑“装该分类最多”的一个；<paramref name="requireDominant"/> 时还要求
    /// 该分类是这个箱子里数量最多的分类。</summary>
    private static Chest? PickHolder(List<Chest> chests, HashSet<Chest> claimed,
        Dictionary<Chest, Dictionary<string, int>> perChest, string bucket, bool requireDominant)
    {
        Chest? best = null;
        int bestAmount = 0;
        foreach (Chest c in chests)
        {
            if (claimed.Contains(c))
                continue;
            int amount = perChest[c].GetValueOrDefault(bucket);
            if (amount <= 0 || (requireDominant && !IsDominant(perChest[c], bucket)))
                continue;
            if (best == null || BetterHolder(amount, c, bestAmount, best))
            {
                best = c;
                bestAmount = amount;
            }
        }
        return best;
    }

    /// <summary>主箱都满了时，从溢出池为这一类再认领一个能装的箱子（跳过正在遍历的源箱与已满的箱）。</summary>
    private static Chest? ClaimOverflow(string bucket, Dictionary<string, List<Chest>> homes, List<Chest> freePool, Chest source)
    {
        for (int k = 0; k < freePool.Count; k++)
        {
            Chest c = freePool[k];
            if (ReferenceEquals(c, source) || FreeSlots(c) <= 0)
                continue;
            freePool.RemoveAt(k);
            homes[bucket].Add(c);
            return c;
        }
        return null;
    }

    /// <summary>该分类是否为这个箱子里数量最多的分类（并列也算）。</summary>
    private static bool IsDominant(Dictionary<string, int> counts, string bucket)
    {
        int mine = counts.GetValueOrDefault(bucket);
        if (mine <= 0)
            return false;
        foreach (int v in counts.Values)
        {
            if (v > mine)
                return false;
        }
        return true;
    }

    /// <summary>归属箱候选比较：装得多优先；并列时剩余空间大者优先；再并列大箱子优先。</summary>
    private static bool BetterHolder(int amount, Chest c, int bestAmount, Chest best)
    {
        if (amount != bestAmount)
            return amount > bestAmount;
        int cFree = FreeSlots(c), bestFree = FreeSlots(best);
        if (cFree != bestFree)
            return cFree > bestFree;
        return c.GetActualCapacity() > best.GetActualCapacity();
    }

    /// <summary>空箱候选比较：空箱优先；再比剩余空间；再比容量。</summary>
    private static bool BetterEmpty(Chest c, Chest best)
    {
        bool cEmpty = IsEmpty(c), bestEmpty = IsEmpty(best);
        if (cEmpty != bestEmpty)
            return cEmpty;
        int cFree = FreeSlots(c), bestFree = FreeSlots(best);
        if (cFree != bestFree)
            return cFree > bestFree;
        return c.GetActualCapacity() > best.GetActualCapacity();
    }

    /// <summary>溢出池排序：空箱优先、剩余空间大优先、容量大优先。</summary>
    private static int CompareForOverflow(Chest x, Chest y)
    {
        bool xe = IsEmpty(x), ye = IsEmpty(y);
        if (xe != ye)
            return xe ? -1 : 1;
        int xf = FreeSlots(x), yf = FreeSlots(y);
        if (xf != yf)
            return yf - xf;
        return y.GetActualCapacity() - x.GetActualCapacity();
    }

    private static int FreeSlots(Chest c) => c.GetActualCapacity() - c.GetItemsForPlayer().CountItemStacks();

    private static bool IsEmpty(Chest c) => !c.GetItemsForPlayer().HasAny();

    // ── 标签 ──────────────────────────────────────────────────────────────────

    /// <summary>把每个归属箱的分类名写进它的 <c>modData</c>（供头顶标签显示）；不再是归属箱的清掉标签。</summary>
    private static void ApplyLabels(List<Chest> chests, Dictionary<string, List<Chest>> homes)
    {
        var labelOf = new Dictionary<Chest, string>();
        foreach (var pair in homes)
        {
            foreach (Chest c in pair.Value)
                labelOf[c] = pair.Key;
        }

        foreach (Chest c in chests)
        {
            if (labelOf.TryGetValue(c, out string? bucket))
                c.modData[ChestLabels.CategoryKey] = bucket;
            else
                c.modData.Remove(ChestLabels.CategoryKey);
        }
    }

    // ── 合并零散堆 ────────────────────────────────────────────────────────────

    /// <summary>把一个箱子里能堆叠的零散堆并到一起，移除清空的格子。</summary>
    private static void CompactInventory(IInventory inv)
    {
        for (int i = 0; i < inv.Count; i++)
        {
            Item? a = inv[i];
            if (a == null)
                continue;
            for (int j = inv.Count - 1; j > i; j--)
            {
                Item? b = inv[j];
                if (b == null || !a.canStackWith(b))
                    continue;
                int space = a.maximumStackSize() - a.Stack;
                if (space <= 0)
                    break;
                int move = Math.Min(space, b.Stack);
                a.Stack += move;
                b.Stack -= move;
                if (b.Stack <= 0)
                    inv.RemoveAt(j);
            }
        }
    }

    // ── 汇报 ──────────────────────────────────────────────────────────────────

    private void Report(int categoryCount, int chestCount, int bucketsPresent, int movedStacks, int blockedStacks)
    {
        int homeless = Math.Max(0, bucketsPresent - categoryCount);
        this.monitor.Log(
            $"AutoSort: moved {movedStacks} stacks into {categoryCount} categories across {chestCount} chests "
            + $"({bucketsPresent} categories total, {homeless} without a chest, {blockedStacks} blocked by full chests).",
            LogLevel.Trace);

        if (!this.config().ShowSummary)
            return;

        if (movedStacks == 0 && blockedStacks == 0 && homeless == 0)
        {
            Game1.addHUDMessage(HUDMessage.ForCornerTextbox("箱子已经是整理好的状态。"));
            return;
        }

        string msg = $"整理完成：{movedStacks} 组物品归入 {categoryCount} 类。";
        var notes = new List<string>();
        if (homeless > 0)
            notes.Add($"{homeless} 类没箱子可放");
        if (blockedStacks > 0)
            notes.Add($"{blockedStacks} 组箱子已满放不下");
        if (notes.Count > 0)
            msg += $"（{string.Join("，", notes)}，再加几个空箱子即可）";

        Game1.addHUDMessage(HUDMessage.ForCornerTextbox(msg));
        Game1.playSound("Ship");
    }
}
