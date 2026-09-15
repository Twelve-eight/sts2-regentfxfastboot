# RegentFXFastBoot - 设计与契约

本文件记录 RFX 的**行为契约**与**引擎事实依据**.实现细节看代码注释,历史过程看 DEVLOG.md.

## 1. 问题与定位

RegentFX v0.5.1 在 mod 初始化阶段同步预加载 32 个特效场景(2026-09-11 实测 4.176s 主线程阻塞).
RFX 的定位:在顺序正确时把这次同步预加载替换为**异步、逐帧、不抢占引擎必需带宽**的预热.

## 2. 硬性前提:加载顺序

RFX **必须早于** RegentFX 加载,否则 `LoadScenes` 前缀装上时初始化器已经跑完,无任何可拦截的东西.

顺序的载体是 `settings.save` 的 `mod_settings.mod_list`.引擎事实(反编译 `engine-dllsrc`):

| 事实 | 证据 |
|---|---|
| 游戏本体模组界面**没有**调序功能,只有启用/禁用 | `NModdingScreen` / `NModMenuRow` 只写 `Mod.IsEnabled`;全引擎无 move/reorder 符号 |
| `ModSettings.ModList` 只在 `ModManager.Initialize` 内被写 | `ModManager.cs:146`(用运行时顺序重建)与 `:150`(清空) |
| manifest 无法表达"早于" | `ModManifest` 无 priority/order 字段;`dependencies` 语义是**后加载**;依赖缺失会让 mod `state = Failed` |
| 排序优先级**只按 id**、后出现的下标覆盖前面的 | `SortModList` 建 `dictionary2[manualOrdering[i].Id] = i` |

**同 id 重复行陷阱**:本地副本 + 工坊副本同时存在时,引擎会把被禁用的那份追加到列表尾部,
而尾部那一行携带同样的 id、下标更大 -> 覆盖启用行的位置 -> 每次启动都晚序,且该行会被写回
`mod_list`,于是下次启动继续存在.因此**只允许装一份**.

## 3. 交付路径:两条,都不由 RFX 自己写顺序

用户决定(2026-09-16):RFX **不**改写 `settings.save`.顺序由用户用调序工具手动写入.

1. **Load Order Manager**(Steam Workshop `3747605109`) - 在模组界面注入"加载顺序"按钮,
   写入 `SettingsSave.ModSettings.ModList` 后 `SaveManager.SaveSettings()`.
   RFX 的弹窗把它作为推荐路径.
2. 任何其它能写 `mod_list` 的工具.

## 4. 加速失败时的用户可见行为(RFX-3,2026-09-16)

**触发条件**:本次启动判定为 `OrderProvenance.Late`(RegentFX 的 `ModSceneCache` 已被填充,
证明同步预加载已经发生且不可撤回).这是唯一"确定性失败"的判据;`Unknown` 不算失败
(它只是不可观测,前缀仍可能生效).

**时机**:进入主菜单后(`NGame.Instance.MainMenu != null`),延迟若干帧再弹,避免与引擎
自己的模态(如 `NConfirmModLoadingPopup`)抢 `NModalContainer` 的唯一槽位.

**呈现**:引擎原生模态容器 `NModalContainer.Instance.Add(node)` + 自绘内容节点
(与 Load Order Manager 同款骨架:全屏 `Control` -> 半透明 `ColorRect` 遮罩 ->
居中 `PanelContainer` -> `MarginContainer` -> `VBoxContainer`).
理由:`NModalContainer` 自带背板、输入拦截与 `ActiveScreenContext` 登记;它的 `Add` 会把节点
强转成 `IScreenContext`,而该接口只有 `DefaultFocusedControl` 一个成员,自绘节点实现它成本极低.

**两个按钮**(用户决定 2026-09-16):

| 按钮 | 行为 |
|---|---|
| 使生效(指引) | 显示具体操作步骤(装 Load Order Manager -> 打开"加载顺序" -> 把 RegentFXFastBoot 移到 RegentFX 上面 -> 应用 -> 重启).**不写任何文件**. |
| 不再提示 | 写入一次性状态文件,之后**永不再弹**.加速功能**保持待命**:若用户以后自己把顺序修好,加速仍会自动生效. |

**频率**:仅提示一次.写入状态后不再出现,即使用户没点按钮就关掉游戏 - 只在实际展示过之后才记.

**状态文件**:`OS.GetUserDataDir()/RegentFXFastBoot/notice.json`
(Windows 即 `%APPDATA%/SlayTheSpire2/RegentFXFastBoot/notice.json`).
只存一个布尔语义(`noticeShown`).读写全部包在 try/catch 内:**任何失败都不得影响游戏**,
最坏情况只是多弹一次.

## 5. 不变式(不得破坏)

1. **绝不写 `settings.save`**,绝不改用户的 mod 列表顺序,绝不改 RegentFX 的配置.
2. **绝不新增运行时依赖**(BaseLib 已移除;RitsuLib 未被引用).只用引擎 API + Godot API + 反射.
3. 顺序不满足时如实报告,不伪造成功;`LATE-ORDER` 与"加速成功"是互斥的.
4. 顺序正确且 `PreloadEffects=false` 时**不做任何预热**(保持 RegentFX 原生关闭策略).
5. 任何 UI/状态代码失败都必须降级为"只记日志",不得崩、不得卡、不得吞输入.
6. 弹窗**不是**游戏流程的一部分:不进 `NModalContainer` 之外的地方,不影响任何屏幕栈.

## 6. 构建与发布

- 构建必须带 `-p:CopyToModsFolderOnBuild=false -p:ModsPath="G:/omp works/.tmp/mods-scratch/"`,
  否则 csproj 的 after-target 会把 DLL 写回真实 mods 目录,重新制造"同 id 两行"的晚序陷阱.
- 载荷:`workshop/content/RegentFXFastBoot/` 下 `RegentFXFastBoot.{dll,json,pdb}`,
  json 为 `mod/RegentFXFastBoot.json` 的**逐字节 CRLF 副本**.
- 上传:`workshop/workshop_upload.vdf`(publishedfileid `3799305611`).
