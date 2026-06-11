# 星露谷物语 Mod 合集（Stardew Valley Mods）

自用星露谷物语 SMAPI mod 合集，主打**减少重复劳动**：农场仆人、自动钓鱼、跨图寻路、仓储一体化等。每个 mod 单一职责、互相独立，按需安装。

- 运行环境：Stardew Valley **1.6+** / SMAPI **4.0+**
- 平台：**Windows / macOS / Linux 通用**（同一份 zip；仅「隐藏控制台」为 Windows 专属，在其它平台自动停用）
- 界面语言：简体中文
- 玩家直接安装：前往 [Releases](https://github.com/youzi-1205/stardew-mods/releases) 下载 zip，解压到游戏 `Mods` 文件夹即可

---

## 🚀 安装教程（从零开始，约 10 分钟）

> 适用于刚装好游戏、从没用过 mod 的玩家（Windows + Steam）。

### 第 1 步：安装 SMAPI（mod 加载器，装一次即可）

1. 打开 [smapi.io](https://smapi.io/)，点击大按钮下载
2. 解压后双击运行 `install on Windows.bat`
3. 输入 `1`（安装），程序会自动找到游戏目录，一路确认

### 第 2 步：设置 Steam 启动选项

SMAPI 安装结束时会显示一行启动参数（类似 `"C:\...\Stardew Valley\StardewModdingAPI.exe" %command%`），复制它，然后：

> Steam 库 → 右键星露谷物语 → **属性** → **通用** → 粘贴到底部「启动选项」

之后从 Steam 正常启动即可加载 mod，成就、云存档、手柄、好友功能均不受影响。

### 第 3 步：下载并解压 mod

1. 打开本仓库的 [Releases 页面](https://github.com/youzi-1205/stardew-mods/releases)，下载想要的 mod zip（各 mod 互相独立，按需选择）
2. 找到游戏的 `Mods` 文件夹：Steam 库 → 右键游戏 → **管理** → **浏览本地文件** → 进入 `Mods`
3. 把 zip **直接解压到 `Mods` 文件夹**——压缩包里自带 mod 文件夹，解压后自动变成 `Mods\FarmServant\` 这样的结构，**不需要手动新建任何文件夹**

### 第 4 步：启动游戏验证

进入存档后试试：按 **K**（物品总览）或 **M**（地图寻路）有反应，说明安装成功。各 mod 的快捷键和参数可在 `Mods\<名称>\config.json` 中修改（首次运行后生成）。

> 启动游戏时旁边的黑色控制台窗口是 SMAPI 日志窗口，所有 mod 玩家都有；装上本合集的「隐藏控制台」mod 后它会自动隐藏（日志仍写入文件）。

---

## 📦 Mod 一览

| 文件夹 | 显示名 | 一句话简介 |
|---|---|---|
| `FarmServant` | **农场仆人** | 帮工 NPC：指挥他干全部农活 |
| `ChestHub` | **万能仓储** | 制作取料 + 箱子代付 + 一键补货 + 物品总览 |
| `AutoFish` | **自动钓鱼** | 全自动钓鱼与宝箱增强 |
| `AutoWalk` | **自动寻路** | 世界地图点击寻路，跨地图自动奔跑 |
| `BirthdayGiftReminder` | **生日礼物提醒** | 村民生日与送礼提醒 |
| `NoTerminal` | **隐藏控制台** | 自动隐藏 SMAPI 黑窗口 |

---

## 农场仆人（FarmServant）

一个住在农场的帮工 NPC（默认名「仆人」），真人式逐格走动干活。

- **指挥方式**：按 `H`（或右键点他）弹出指令菜单，按附近实际存在的活动态显示选项：
  - 照料附近庄稼（浇水 / 收获 / 施肥 / 补种）
  - 收取机器产物（熔炉、酒桶、鱼饵机等；建筑内的走到门口整栋收）
  - 照料动物（抚摸、补草料、收产物）
  - 碎石头（散石 / 巨石 / 大石块 → 石头矿石）
  - 清木头（树枝 / 树桩 / 原木 / 树苗 → 木头硬木）
  - 除草（杂草 → 纤维）
  - 割草存干草（农场绿草 → 筒仓，按余位限量）
  - 砍附近的树（成年大树，需明确下令）
  - 全农场日常巡一遍
- **早晨自动开工**：每天 6:10 自动巡逻全农场（可关）
- **智能归仓**：收获物与捡到的掉落物优先放进「已有同类物品」的箱子（最近优先），其次最近的空位箱子；建筑内产出优先同屋箱子
- **经验照拿**：收获耕种经验、砍树伐木经验、碎石采矿经验都记到玩家头上，与亲手做一致
- **离场继续干**：玩家离开农场后任务以快进模式继续推进
- 种子、肥料消耗自农场箱子；干完活回农舍旁角落待机

主要配置：`HelperCallKey`（`H`）、`HelperSpeed`（5）、`HelperWorkSpeedMultiplier`（2.0）、`HelperAutoStartDaily`（true）、`HelperWorkRadius`（12）等。

## 万能仓储（ChestHub）

箱子相关的四个便利功能合一：

1. **制作取料**：制作/烹饪菜单的判定与扣料自动计入全世界所有箱子（含迷你冰箱、厨房冰箱、祝尼魔箱子）
2. **箱子代付**：物品支付类消费免搬运——罗宾盖房/房屋升级/社区升级、克林特工具升级、修船、以物易物商店；所需材料临时从箱子调入背包，未消耗的关界面后自动放回（优先放回有同类物品的箱子）
3. **一键补货**：站在空闲机器旁出现按钮，点击自动从箱子扣原料和燃料并开机；**原料燃料可分散在不同箱子**
4. **物品总览**：按 `K` 汇总背包+所有箱子+冰箱的每种物品总量，悬停查看存放分布；滚轮翻页

配置：`StockKey`（`K`）、`CraftFromChestsEverywhere`、`MachineRefillButton`。

## 自动钓鱼（AutoFish）

- 自动跟鱼、自动咬钩（出现「!」自动起竿）、宝箱优先且保住进度
- 水边「自动钓鱼」按钮：甩竿→咬钩→收鱼全自动循环；有宝箱或背包真满才停
- 满力抛竿（落点更深，鱼质与宝箱概率更好）
- 宝箱增强（可关）：出现率小幅提升，开箱概率附加稀有奖励

配置要点：`AutoCastPower`、`TreasureChestChanceMultiplier`、`BoostFishingTreasureRarity` 等。

## 自动寻路（AutoWalk）

- 按 `M` 打开世界地图，点击任意地名/建筑 → 自动跨地图奔跑前往；室内目的地自动停在正门口
- 点击鼠标或按移动键立即停止；被挡路 0.75 秒自动绕行

配置：`OpenMapKey`（`M`）、`RunWhilePathing`、`StopOnMouseClick`。

## 生日礼物提醒（BirthdayGiftReminder）

村民生日当天登录提醒，列出最爱/喜欢的礼物清单。

## 隐藏控制台（NoTerminal）

启动后自动隐藏 SMAPI 的黑色控制台窗口。日志仍写入 `ErrorLogs/SMAPI-latest.txt`，排查问题不受影响。配置 `HideConsole` 可关闭。

---

## StardewModAssistant（可选）

Windows 桌面启动器：管理 mod、经由 SMAPI 启动游戏。普通玩家用 Steam 启动选项指向 SMAPI 即可，**不需要它**。

> ⚠️ 注意：此工具及仓库根目录的若干 `.ps1`/`.bat` 脚本是作者的个人工具，内部**硬编码了作者本机的游戏路径**（`D:\app\steam\...`），其他人直接运行会失败；需自行修改路径后使用。六个 SMAPI mod 本体无任何硬编码路径，开箱即用。

## 从源码构建

```powershell
# 任一 mod 目录下（需 .NET 6 SDK、已安装游戏与 SMAPI）
dotnet build -c Release
```

借助 [Pathoschild.Stardew.ModBuildConfig](https://www.nuget.org/packages/Pathoschild.Stardew.ModBuildConfig)：自动定位游戏目录、编译产物自动部署到 `Mods`（需游戏未运行）、同时生成可分发的发布 zip。

## 历史与迁移说明

- v0.2 起重组结构：原 `FarmSuite` 拆分为 `FarmServant` 与 `ChestHub`，原 `ChestPay`/`StockOverview` 并入 `ChestHub`，原 `FishingAssist` 更名 `AutoFish`；**小皮卡功能已移除**（旧存档货箱内的物品会在首次加载时自动搬回农场箱子）
- 升级方式：删除 `Mods` 下的旧文件夹（FarmSuite / ChestPay / StockOverview / FishingAssist），解压新版即可

## 已知限制

- **多人联机**：仆人核心逻辑运行在房主侧；制作取料在多人下未加箱子互斥锁，极端并发可能竞态
- 箱子代付的材料会短暂经过背包（占格子）；背包完全满时会提示清格
- 浣熊许愿（1.6 以物换物）暂未被箱子代付覆盖

## 致谢

由 Claude 与 Codex 协助开发。游戏机制分析基于对原版程序集的反编译研究，全部功能尽量复用原版公开接口实现（零 Harmony 补丁）。
