# Stardew Valley Mods

自用星露谷物语 SMAPI mod 合集（Stardew Valley 1.6 / SMAPI 4 / .NET 6）。

## Mod 列表

| Mod | 说明 |
|---|---|
| **FarmSuite** | 综合 mod：制作/烹饪直接消耗所有箱子里的材料；农场仆人 NPC（自动浇水、收获进箱、施肥、补种、照料动物、清理杂物并把掉落物送回箱子，H 键呼出指令菜单）；可在木匠店购买的小皮卡（骑乘加速、车斗货箱、世界地图标记） |
| **FishingAssist** | 钓鱼助手：自动跟鱼、自动咬钩、宝箱优先、一键全自动钓鱼（背包真满才停）、满力抛竿、宝箱概率/稀有奖励增强 |
| **AutoWalk** | 自动寻路：按 M 打开世界地图点击目的地，跨地图自动奔跑前往（走到建筑正门停下），点击鼠标随时停止 |
| **BirthdayGiftReminder** | 村民生日提醒：当天登录时提示寿星及其喜好礼物 |
| **StardewModAssistant** | 独立桌面启动器：管理 mod、通过 SMAPI 启动游戏 |

## 构建

每个 mod 目录下 `dotnet build -c Release` 即可；借助 [ModBuildConfig](https://www.nuget.org/packages/Pathoschild.Stardew.ModBuildConfig)，编译产物会自动部署到游戏的 `Mods` 文件夹（需要游戏未运行）。

## 致谢

由 Claude 与 Codex 协助开发。
