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
| 使生效(指引) | 显示具体操作步骤(装 Load Order Manager -> 打开"加载顺序" -> 把 RegentFXFastBoot 移到 RegentFX 上面 -> 应用 -> 重启),并打开 LOM 的工坊页.**不写任何文件,不改顺序**. |
| 不再提示 | 关闭弹窗.一次性状态**在弹窗展示时就已经记录**(见"频率"),所以此按钮只是让用户主动关掉它,而不是记录点.加速功能**保持待命**:若用户以后自己把顺序修好,加速仍会自动生效. |

**频率**:仅提示一次.状态在**展示成功时**记录,因此用户不点任何按钮直接退出游戏,下次也不会再弹.
只有"确实显示过"才会记录 - 没能显示(模态槽位被占/主菜单未出现)时不记录,下次仍会尝试.

**状态文件**:`OS.GetUserDataDir()/RegentFXFastBoot/notice.json`
(Windows 即 `%APPDATA%/SlayTheSpire2/RegentFXFastBoot/notice.json`).
只存一个布尔语义(`noticeShown`).读写全部包在 try/catch 内:**任何失败都不得影响游戏**,
最坏情况只是多弹一次.

### 验收顺序(不可颠倒)

弹窗**只在确定性晚序**被调度(`LateOrderNoticeWatcher.Schedule()` 仅出现在
`ModSceneCache.Count > 0` 分支),所以观察它必须发生在修好顺序**之前**:

0. **先跑 `tools/check-live-payload.ps1`(exit 0 才继续)**.工坊订阅下载是**异步**的:
   推送被接受不等于 live 副本已更新,而 `refresh-workshop-payloads.ps1`(查仓库暂存树)与
   steamcmd 日志(只证明上传被接受)**都不看 live 副本**.若在此窗口启动,跑的是旧 DLL,
   "没看到弹窗"就是**假阴性**.该脚本按哈希比对 live 与暂存,并在哈希不同但仅内嵌版本串不同时
   正确放行(provenance 差异不是代码差异).
1. 推出 0.3.0(或覆盖工坊内容目录),使实机加载的是含弹窗的 DLL.
2. **在顺序仍然错误的状态下启动游戏**(此时 `settings.save` 为 RFX@50 vs RegentFX@27,仍晚序):
   进主菜单后应出现一次弹窗,然后退出.预期日志:
   `NOTICE: late-order popup scheduled` -> `NOTICE: late-order popup shown on the main menu` ->
   `NOTICE: one-shot state recorded at ..`;预期文件:
   `%APPDATA%/SlayTheSpire2/RegentFXFastBoot/notice.json`(内容 `{"noticeShown": true}`).
   这一次启动同时会清掉 `settings.save` 里的陈旧行(见下),不要为此重复启动.
3. 之后才用 LOM 打开"加载顺序",把唯一那条 `RegentFXFastBoot` 移到 `RegentFX` 上方,点"应用".
4. 重启验证加速生效:`tools/verify-fastboot-order.ps1` 应 exit 0(无 `LATE-ORDER`,出现
   `ARMED`/`BOUND`/`INTERCEPTED`/`WARMED`),且此时**不应**再出现弹窗(日志应显示
   `already shown in an earlier launch`).

顺序若已先修好,弹窗只能靠临时把 RFX 移回 RegentFX 下方再启动一次来复测.
早序运行永远不会调度弹窗 - 这是设计使然(早序本轮可能成功,弹窗会是错的),不是缺陷.

**本机当前仍有两行 RFX**:`settings.save` 的 `idx 40` 是已删除本地副本的陈旧行
(`mods_directory`),`idx 50` 是工坊行.`ModManager.Initialize` 每轮按 `_mods`(实际在场 mod)
重建 `mod_list`,所以上面第 2 步那次启动会一并清除 idx 40,此后 LOM 只显示唯一一行.

重建的前提已核实:`mod_list` 的重写位于 `if (_settings != null && _settings.PlayerAgreedToModLoading)`
分支内(`ModManager.cs:135-148`),而 `PlayerAgreedToModLoading` 的序列化名是
`mods_enabled`(`ModSettings.cs:10-11`),本机该字段为 `true`;否则走 else 分支把列表清空
(`ModManager.cs:150`).反证:若该标志为假,`ModManager.cs:675` 会把每个 mod 置为 Disabled,
而本机 mod 正常加载.

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

### DLL 哈希会随 HEAD 漂移(不是源码漂移)

SDK 会自动嵌入 `<Version>1.0.0+<git sha></Version>`,所以**同一份源码在不同 HEAD 上构建会得到不同的 DLL 字节**.
后果与处置:

- 载荷哈希 != 重建哈希时,**先比对字符串集合再下结论**:两侧 UTF-16 用户字符串应完全一致,
  唯一差异应为那条 `1.0.0+<sha>`(实测:223 vs 223 条,仅版本串不同).
- `refresh-workshop-payloads.ps1` 与 `GATE-1-PAYLOAD-VERIFICATION.md` 里的"嵌入 sha != HEAD"
  属于**已记录的假阳性**;该文档同时说明"重建后比对"才是诚实做法.
- **内嵌 sha 恒等于"承载载荷那个提交的父提交",这是结构性事实,不是缺陷,不要去追平**:
  构建时的 HEAD 是 X,构建后提交载荷得到 X+1,于是内嵌 sha 记录 X,而 X 正是 X+1 的父提交.
  实测:`4afe28b` 携带的载荷内嵌 `02187aa`(=`4afe28b` 的父提交);
  `3f04df8` 携带的载荷内嵌 `27a0de2`(=`3f04df8` 的父提交).
  任何"重建以追平 sha"的尝试都会因为提交本身移动 HEAD 而立刻失效(实测循环:
  `4afe28b` 暂存 -> 提交变 `27a0de2` -> 再暂存 -> 提交变 `3f04df8`),**没有终点**.
- **同一性判定只用"重建后比对 IL / 字符串集合,忽略版本 blob"**:
  暂存载荷的 `ilspycmd -il` 输出与当前 HEAD 构建逐字符相同(372099 字符,零差异)即视为同一.
  IL 输出不含 COFF 时间戳/调试 GUID/版本属性 blob,因此该比对与内嵌 sha 无关.
- 只有**改动了源码**才需要重新构建暂存;仅提交文档/日志不需要(不改变 DLL 代码).
  载荷的内嵌 provenance 指向"构建时 HEAD",这对它本身是完全正确的语义 - 不要把它读成"载荷过期".

