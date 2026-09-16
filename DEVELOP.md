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

## 4. 用户可见行为:两种一次性弹窗(RFX-3/RFX-4)

两种通知共用同一个模态,同一套宿主逻辑与同一份一次性记账,只有内容与按钮集不同
(`NoticeKind.LateOrder` / `NoticeKind.Succeeded`).泛化而非复制是刻意的:关闭路径带着一个
`NModalContainer.Clear()` 的隐患(见下),复制一份就是第二次犯错的机会.

**触发条件(失败弹窗)**:本次启动判定为 `OrderProvenance.Late`(RegentFX 的 `ModSceneCache` 已被填充,
证明同步预加载已经发生且不可撤回).这是唯一"确定性失败"的判据;`Unknown` 不算失败
(它只是不可观测,前缀仍可能生效).

**触发条件(成功弹窗)**:`CompleteWarmUp` 中 `_warmed > 0 && FailedPaths.Count == 0 && Pending.Count == 0`.
刻意收窄:只有整条队列提交完毕,至少真的预热了一个场景,且无一失败时才算"确实加速了".
部分完成或有失败时不弹 - 此弹窗绝不能宣称一个没有发生的节省.
`_warmed == 0` 的零工作早退分支根本不会走到 `CompleteWarmUp`(根本没建预热节点),所以
"RegentFX 的预加载本来就会是空操作"的那次启动同样保持静默.

**时机**:进入主菜单后(`NGame.Instance.MainMenu != null`),延迟若干帧再弹,避免与引擎
自己的模态(如 `NConfirmModLoadingPopup`)抢 `NModalContainer` 的唯一槽位.
本机实测(BootTimer,39 mod):主菜单约 4.0s 出现,而预热跑在 4-22s 之后 -
即成功弹窗的观察节点创建时主菜单已在,这是它的常态而非边缘情况.

**有界预算(三段,互不共享计数器)**:
- 等待主菜单:3600 帧(仅在尚未看到主菜单时计数);
- 离开菜单宽限:`MenuDepartureToleranceFrames = 3` 帧(仅在已看到主菜单后计数);
- 等待模态槽位:120 次 x 30 帧间隔(约 60s).
"等待主菜单"与"等待模态槽位"曾共用一个计数器:成功路径上计数器恰好在第 120 次尝试那一帧到达上限,
于是报出"主菜单未出现"(而它早已出现)且模态槽位那段预算永远不会运行.
现已拆开:主菜单出现后"等待主菜单"冻结,模态槽位那段才是真正生效的上限.

**第三段:离开菜单的终态(WS-0916-03)**.观察器挂在 SceneTree **根**上(为了不被 NGame 的
生命周期带走),所以**离开主菜单不会释放它**.原先的 `_Process` 在 `_menuSeen` 之后,
只要 `nGame.MainMenu == null` 就直接 `return`:"等待主菜单"那段预算已按设计冻结,
而"等待模态槽位"那段在同一个函数的更下方,永远走不到,于是玩家在弹窗出现前离开菜单会留下一个
**每帧轮询到会话结束**的根节点,而通知既没弹出也没被报告为丢弃
(审查模型 `notice-lifecycle-model.json` 记录的状态:
`menuSeen=true, frames=1, attempts=0, freed=false`).

现在:`_menuSeen` 之后菜单连续缺席超过 `MenuDepartureToleranceFrames` 帧即视为**离开**,
记一条如实的丢弃日志并 `QueueFree()`.宽限帧的用途是"菜单构建/重载期间的一两帧 `MainMenu == null`
不是玩家离开":`NGame.MainMenu` 是 `RootSceneContainer.CurrentScene as NMainMenu`,
而 `CurrentScene` 在持有场景被排队释放或正在替换时报 null(`NSceneContainer.cs`),
把它当成离开会让一个本来会弹出的通知被放弃.3 帧(~50ms)覆盖前者且远小于后者
(真实离开是 RunManager 清理 + 淡出 + 资源加载,以秒计).

三条终态路径(未看到菜单超时/离开菜单/槽位预算耗尽)与成功路径**都** `QueueFree()`,
且**只有** `ModNotice.TryShow` 成功时才会写一次性状态 - 被丢弃的通知永远不记为已显示.

**决策模型(不是实机验证)**:`tools/notice-lifecycle/model.py` 逐帧建模 `_Process` 的
状态机,并从 C# 源解析全部常量(防止模型与源码漂移),覆盖:菜单始终不出现、看到后离开、
槽位始终被占、正常成功、单帧瞬时缺席、缺席超过宽限、槽位稍后释放、尝试开始后才离开.
它**不**启动游戏/Steam,也**不**证明引擎时序或渲染,只证明决策流.运行:
`python tools/notice-lifecycle/model.py`.

**呈现**:引擎原生模态容器 `NModalContainer.Instance.Add(node)` + 自绘内容节点
(与 Load Order Manager 同款骨架:全屏 `Control` -> 半透明 `ColorRect` 遮罩 ->
居中 `PanelContainer` -> `MarginContainer` -> `VBoxContainer`).
理由:`NModalContainer` 自带背板、输入拦截与 `ActiveScreenContext` 登记;它的 `Add` 会把节点
强转成 `IScreenContext`,而该接口只有 `DefaultFocusedControl` 一个成员,自绘节点实现它成本极低.

**失败弹窗的两个按钮**(用户决定 2026-09-16);成功弹窗只有一个“知道了”按钮(无需任何操作):

| 按钮 | 行为 |
|---|---|
| 使生效(指引) | 显示具体操作步骤(装 Load Order Manager -> 打开"加载顺序" -> 把 RegentFXFastBoot 移到 RegentFX 上面 -> 应用 -> 重启),并打开 LOM 的工坊页.**不写任何文件,不改顺序**. |
| 不再提示 | 关闭弹窗.一次性状态**在弹窗展示时就已经记录**(见"频率"),所以此按钮只是让用户主动关掉它,而不是记录点.加速功能**保持待命**:若用户以后自己把顺序修好,加速仍会自动生效. |

**频率**:每种弹窗各仅提示一次(两个独立标志).状态在**展示成功时**记录(而不是按钮按下时),因此用户不点任何按钮直接退出游戏,下次也不会再弹.
只有"确实显示过"才会记录 - 没能显示(模态槽位被占/主菜单未出现/看到菜单后离开)时不记录,下次仍会尝试.

**槽位判定按“存活”而非“非空”**:引擎的 `NConfirmModLoadingPopup` 与 `NGenericPopup`
都以 `QueueFreeSafely()` 结束且**不**调 `Clear()`,而 `NModalContainer` 没有子节点离树回调,
于是 `OpenModal` 会持续指向一个已释放对象.引擎自己的 `Add()` 在该状态下**也会拒绝**
(它测的是同一个字段),所以这个状态不会自愈.可达路径恰好是新订阅者:
`NConfirmModLoadingPopup` 的条件是 `SettingsSave.ModSettings == null && ModManager.Mods.Count > 0`
(`NMainMenu.cs:484-486`),答完它后槽位就悬空一整个会话.
`SlotBusy` 因此按存活判定:存活持有者只读不动,证明已释放的持有者用引擎自己的 `Clear()`
释放一次(不递归,抛异常则报 busy 交给重试).

**状态文件**:`OS.GetUserDataDir()/RegentFXFastBoot/notice.json`
(Windows 即 `%APPDATA%/SlayTheSpire2/RegentFXFastBoot/notice.json`).
两个独立标志共用一份载荷(`noticeShown` / `successShown`).由此有两条硬约束:

- **每次写入都写两个标志的并集**.文件是整体覆写,只写刚变化的那个标志会抹掉另一个,
  让被抑制的弹窗复活.
- **每次读取只匹配自己那个键** (`"noticeShown"\s*:\s*true`).裸搜 `true` 会把
  `{"noticeShown": false, "successShown": true}` 读成"失败弹窗已经显示过",静默压掉本该出现的那个.

读写全部包在 try/catch 内:**任何失败都不得影响游戏**,最坏情况只是多弹一次.
本文件是 mod 唯一写入的文件:弹窗**不写** `settings.save`,也**不重排**任何 mod.

**日志前缀**:失败弹窗用 `NOTICE:`,成功弹窗用 `NOTICE-SUCCESS:`(两个前缀互不为子串,
所以 `NOTICE:` 的模式不会命中成功行).验收脚本的断言全部按前缀分域 - 否则成功行的
"one-shot state recorded" 会被算到失败弹窗头上.

### 验收顺序(不可颠倒)

失败弹窗**只在确定性晚序**被调度(`ModNoticeWatcher.Schedule(NoticeKind.LateOrder)` 仅出现在
`ModSceneCache.Count > 0` 分支),所以观察它必须发生在修好顺序**之前**;
成功弹窗则**只在成功的早序运行**被调度(`CompleteWarmUp` 内),两者不会同一次出现:

1. 推出 0.4.0(或覆盖工坊内容目录).
2. **跑 `tools/check-live-payload.ps1`,exit 0 才继续**.
   工坊订阅下载是**异步**的:推送被接受不等于 live 副本已更新,而
   `refresh-workshop-payloads.ps1`(查仓库暂存树)与 steamcmd 日志(只证明上传被接受)
   **都不看 live 副本**.在此窗口启动会跑旧 DLL,"没看到弹窗"就是**假阴性**.
   该脚本按哈希比对 live 与暂存,并在"哈希不同但代码级字符串一致、仅内嵌版本串不同"时
   正确放行(provenance 差异不是代码差异).**此步必须在第 1 步之后** - 推送前 live 仍是旧版,
   门禁会(正确地)报 STALE.
3. **在顺序仍然错误的状态下启动游戏**(此时 `settings.save` 为 RFX@50 vs RegentFX@27,仍晚序):
   进主菜单后应出现一次弹窗,然后退出.预期日志:
   `NOTICE: popup scheduled` -> `NOTICE: late-order popup shown on the main menu` ->
   `NOTICE: one-shot state recorded at ..`;预期文件:
   `%APPDATA%/SlayTheSpire2/RegentFXFastBoot/notice.json`(含 `"noticeShown": true`).
   **这一次不会弹成功窗**(本次是晚序,根本没预热),所以日志里不应出现 `NOTICE-SUCCESS:` 行.
   这一次启动同时会清掉 `settings.save` 里的陈旧行(见下),不要为此重复启动.
4. 之后才用 LOM 打开"加载顺序",把唯一那条 `RegentFXFastBoot` 移到 `RegentFX` 上方,点"应用".
5. 重启验证加速生效:`tools/verify-fastboot-order.ps1` 应 exit 0(无 `LATE-ORDER`,出现
   `ARMED`/`BOUND`/`INTERCEPTED`/`WARMED`).
   **这一次是早序运行,失败弹窗不会出现** - 脚本应报
   `no late-order notice scheduled (correct for an early-order run)`;
   日志里也不应出现任何 `NOTICE:` 行(失败弹窗专有前缀).
   但**成功弹窗会**在这台机器上首次出现(这是它的设计触发条件):预期
   `NOTICE-SUCCESS: popup scheduled` -> `NOTICE-SUCCESS: the main menu is up` ->
   `NOTICE-SUCCESS: popup shown on the main menu (once): acceleration ran and 32 scene(s) were warmed` ->
   `NOTICE-SUCCESS: one-shot state recorded at ..`;脚本会报 `success notice shown once` 与
   `success notice backed by the log: warmed=32 failed=0 notSubmitted=0`(它会交叉核对弹窗文本里的
   场景数与 `COMPLETED` 行,两者不一致即失败).**再启动一次**则该行变为
   `success notice correctly suppressed: already shown in an earlier launch`.
   **不要**期望失败弹窗出现 `already shown in an earlier launch`:该串只在 `Schedule()` 内部,
   而失败弹窗的唯一调用点是确定性晚序分支(`MainFile.cs:286`),早序根本不会调用它.
   若想验证"一次性状态确实抑制了失败弹窗",必须**保持晚序**再启动一次(即在第 4 步调序之前),
   那时才会出现该串;调序之后的早序运行无法验证抑制逻辑.

顺序若已先修好,弹窗只能靠临时把 RFX 移回 RegentFX 下方再启动一次来复测.
早序运行永远不会调度弹窗 - 这是设计使然(早序本轮可能成功,弹窗会是错的),不是缺陷.

**本机当前仍有两行 RFX**:`settings.save` 的 `idx 40` 是已删除本地副本的陈旧行
(`mods_directory`),`idx 50` 是工坊行.`ModManager.Initialize` 每轮按 `_mods`(实际在场 mod)
重建 `mod_list`,所以上面第 3 步那次启动会一并清除 idx 40,此后 LOM 只显示唯一一行.

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

