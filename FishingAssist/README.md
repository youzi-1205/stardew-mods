# Fishing Assist

一个星露谷 SMAPI 钓鱼辅助 Mod。进入钓鱼小游戏后，它会让绿色钓鱼条自动靠近鱼的位置，减少手动按键难度。

## 功能

- 自动让绿色钓鱼条跟随鱼。
- 鱼咬钩出现提示时自动提竿，直接进入钓鱼小游戏。
- 宝箱出现时优先跟随宝箱，拿到宝箱后再回去跟鱼。
- 可调跟随力度：从轻微辅助到基本自动。
- 可单独调整宝箱跟随力度。
- 可选冻结钓鱼条惯性，避免绿条来回弹。
- 可选防止捕获进度掉得太低。
- 提供 SMAPI 控制台命令快速开关。

## 控制台命令

在 SMAPI 控制台输入：

```text
fa_toggle
```

开关整个 Mod。

```text
fa_auto
```

只开关自动跟鱼功能。

## 配置

`config.json` 默认如下：

```json
{
  "EnableMod": true,
  "AutoFollowFish": true,
  "AutoHookFish": true,
  "PrioritizeTreasureChest": true,
  "FollowStrength": 1.0,
  "TreasureFollowStrength": 1.0,
  "FreezeBarMomentum": true,
  "KeepCatchProgressFromDropping": false,
  "MinimumCatchProgress": 0.35,
  "ProtectFishProgressWhileCatchingTreasure": true,
  "TreasureFishProgressFloor": 0.75
}
```

- `EnableMod`: 是否启用 Mod。
- `AutoFollowFish`: 是否自动跟随鱼。
- `AutoHookFish`: 鱼咬钩时是否自动提竿。
- `PrioritizeTreasureChest`: 宝箱出现时是否优先跟宝箱。
- `FollowStrength`: 跟随力度，`0.2` 是轻微辅助，`1.0` 是直接跟随。
- `TreasureFollowStrength`: 宝箱跟随力度。
- `FreezeBarMomentum`: 是否清除绿条惯性。
- `KeepCatchProgressFromDropping`: 是否防止捕获进度掉得太低。
- `MinimumCatchProgress`: 捕获进度最低保持值，仅在上一项为 `true` 时生效。
- `ProtectFishProgressWhileCatchingTreasure`: 追宝箱时是否保住鱼的捕获进度。
- `TreasureFishProgressFloor`: 追宝箱时鱼的捕获进度最低保持值。

## 编译

需要 PC 版 Stardew Valley、SMAPI 4.0+、.NET 6 SDK。

在这个文件夹运行：

```powershell
dotnet restore
dotnet build -c Release
```

编译后的 Mod 通常在：

```text
bin/Release/net6.0/FishingAssist
```

把 `FishingAssist` 文件夹放进：

```text
Stardew Valley/Mods
```

再通过 SMAPI 启动游戏。

## 注意

这个 Mod 是为单机或和朋友协商好的联机体验准备的。公共联机或挑战玩法里，建议先确认规则。
