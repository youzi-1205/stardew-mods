# Birthday Gift Reminder

一个轻量 SMAPI Mod：每天早上如果有村民生日，会在右下角 HUD 提醒，并列出该村民的“最爱”和“喜欢”礼物。

## 功能

- 读取游戏自带 `Data/Characters` 判断今日生日。
- 读取游戏自带 `Data/NPCGiftTastes` 显示礼物喜好。
- 使用游戏本地化名称；如果你用中文玩，NPC 和物品名会尽量显示中文。
- 支持大多数通过 Content Patcher 添加了角色生日和礼物喜好的 NPC。
- 在 SMAPI 控制台输入 `bgr_today` 可以重复显示当天提醒。

## 安装前提

- PC 版 Stardew Valley
- SMAPI 4.0 或更新版本
- .NET 6 SDK，用于从源码编译

## 编译

在这个文件夹打开终端：

```powershell
dotnet restore
dotnet build -c Release
```

编译后的 Mod 通常会在：

```text
bin/Release/net6.0/BirthdayGiftReminder
```

把这个文件夹复制到你的游戏 `Mods` 目录：

```text
Stardew Valley/Mods/BirthdayGiftReminder
```

然后通过 SMAPI 启动游戏。

## 配置

第一次运行后，SMAPI 会在 Mod 文件夹生成或读取 `config.json`：

```json
{
  "MaxGiftsPerTaste": 6,
  "ShowLovedGifts": true,
  "ShowLikedGifts": true,
  "OnlyShowCharactersWithGiftData": true
}
```

- `MaxGiftsPerTaste`: 每类最多显示几个礼物。
- `ShowLovedGifts`: 是否显示“最爱”。
- `ShowLikedGifts`: 是否显示“喜欢”。
- `OnlyShowCharactersWithGiftData`: 只提醒有礼物数据的角色。

## 注意

这个 Mod 只显示提醒，不会自动送礼、改存档或操作角色。
