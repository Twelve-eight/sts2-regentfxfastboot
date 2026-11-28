# RegentFX Fast Boot（万象辉星启动加速）

针对 [RegentFX](https://steamcommunity.com/sharedfiles/filedetails/?id=3747497501) 启动期同步预加载卡顿的条件加速补丁：当排在 RegentFX 之前加载且 RegentFX 自身的 `PreloadEffects` 开启时，跳过其启动阶段同步预加载，改为在主菜单空闲帧通过引擎原生异步加载接口逐项预热缓存。

> **English**: Conditional boot-stall fix for RegentFX. When loaded **before** RegentFX, skips RegentFX's synchronous startup preload (when RegentFX's `PreloadEffects` is enabled) and warms the same scene cache asynchronously during idle main-menu frames. Zero BaseLib dependency; never edits your `settings.save` or load order.

- **Current Version**: `0.4.2`（rebuilt & verified against Slay the Spire 2 **`v0.114.0`**, `min_game_version: 0.111.0`）
- **Steam Workshop**: [3799305611](https://steamcommunity.com/sharedfiles/filedetails/?id=3799305611)
- **Runtime Dependencies**: 无（不依赖 BaseLib；配合 [RegentFX](https://steamcommunity.com/sharedfiles/filedetails/?id=3747497501) 使用，未安装时无副作用）

## 加载顺序要求 / Load-Order Requirement

- **硬性顺序**：`RegentFXFastBoot` 必须排在 `RegentFX` **之前**加载。
- 游戏自带 Mod 界面无法调整顺序，请使用调序工具（如 [Load Order Manager](https://steamcommunity.com/sharedfiles/filedetails/?id=3747605109)）将 `RegentFXFastBoot` 移至 `RegentFX` 上方后应用并重启。
- 请只保留**一份** `RegentFXFastBoot` 安装（不要同时保留工坊版与本地手动版）。
- 主菜单会通过一次性提示告知本次启动是否成功加速（若顺序晚于 RegentFX 会明确提示原因，且不会擅自改动配置文件）。

## 安装 / Installation

- **创意工坊**：订阅 [RegentFX Fast Boot](https://steamcommunity.com/sharedfiles/filedetails/?id=3799305611) 并按上文调整加载顺序。
- **手动安装**：从本仓库 [Releases](https://github.com/Twelve-eight/sts2-regentfxfastboot/releases) 下载压缩包，解压至 `<Slay the Spire 2>/mods/`，得到 `mods/RegentFXFastBoot/{RegentFXFastBoot.dll,RegentFXFastBoot.json}`（**请勿**同时启用工坊版与手动本地版）。

## 从源码构建 / Building from source

前置要求：**.NET 9 SDK** 与已安装的《杀戮尖塔 2》。

```bash
dotnet build mod/RegentFXFastBoot.csproj -c Release \
  -p:Sts2Path="<path-to-Slay-the-Spire-2>" \
  -p:CopyToModsFolderOnBuild=false
```

构建产物位于 `mod/.godot/mono/temp/bin/Release/RegentFXFastBoot.dll`（配合 `mod/RegentFXFastBoot.json`）。详细设计与验证记录见 [DEVELOP.md](DEVELOP.md) 与 [DEVLOG.md](DEVLOG.md)。
