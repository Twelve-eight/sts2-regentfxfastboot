
---

## 2026-09-12 (夜) astra-advice 项 10: 因果断链与诚实状态报告 (主会话单线)

### 因果断链 (全部对实机证据与反编译复核)

1. 卡顿源: RegentFX v0.5.1 的 `[ModInitializer] Entry.Init` → `LoadScenes()` →
   32 次 `ResourceLoader.Load` 主线程同步执行 (godot.log 21:02:56→21:03:00,
   4.176s, "Preloading 32 RegentFX assets synchronously")。
2. FastBoot 挂载点: ModsDirectory, 而 RegentFX 是 SteamWorkshop —— 引擎加载顺序
   工坊先于本地 (ModManager.ReadModsInDirRecursive 次序), 本轮实机日志:
   RegentFX 21:02:56 初始化完毕, FastBoot 21:03:03 才加载。
3. 结论: **任何后加载 mod 的 Harmony 补丁都不可能拦截已运行完的初始化器**。
   AssemblyLoad 早期钩子设计本身成立, 但前提是本 mod 先于 RegentFX 加载;
   实机顺序下它永远晚到 → skip patch 装上时 LoadScenes 已跑完
   (Init 是唯一调用点), defer 队列按构造恒为 0 ("queued: 0 scenes" 的真因)。
4. 可行的生效路径: ModManager.SortModList 以**用户模组列表顺序**为拓扑优先级
   (engine dllsrc ModManager.cs:193, manualOrdering) —— 用户在游戏内模组列表把
   RegentFXFastBoot 移到 RegentFX 之上, AssemblyLoad 钩子即可先于其初始化器武装,
   skip 生效 + 逐帧预热接棒。

### 改动 (不造假修复)

- TryActivate 检测 `ModSceneCache.Count > 0`: 判定为 LATE-ARMED, 日志如实声明
  "启动卡顿本次已经发生且无法撤回", 并给出可执行解法 (模组列表中把本 mod 移到
  RegentFX 之上; 引擎尊重该顺序)。
- EARLY-ARMED (缓存为空) 时日志明示 skip 已生效 + 预热队列长度, 用户可从日志
  验证顺序调整是否成功。
- 原有的 skip/预热机制本身保留未动 (设计成立, 之前只是加载槽位不对)。

### 验证

- 隔离构建 0 错误, 已部署实机。
- 实机验收: ①用户调整模组列表顺序后重启 → 日志应出现 "EARLY-ARMED: ... queued:
  32 scenes", 启动到主菜单时间应减少约 4s; ②不调整顺序 → 日志出现 LATE-ARMED
  提示 (证明检测路径工作)。
- 备选解法 (不改顺序): RitsuLib 设置 PreloadEffects=false 也可消除卡顿
  (RegentFX 自带开关), FastBoot 随后同样走逐帧预热路径。

---

## 2026-09-13 (凌晨) FastBoot 真机全链路验证通过 (主会话单线)

### 手动排序已由主会话代为完成 (带备份)

- 文件: `settings.save` (active profile 76561199466878739), 备份:
  `settings.save.pre-fastboot-reorder.bak`。
- **机制发现** (调试过程, 对未来有用):
  1. 手动顺序存于 `mod_settings.mod_list` (id/is_enabled/source, snake_case), 不是
     顶层 ModList;
  2. 同 id 双源 (本地+工坊) 在列表里有**两条**条目, `SortModList` 的
     `dictionary2[id]=index` 取**最后出现**的索引 —— 只移动第一条无效;
  3. 本 mod 的 manifest 依赖 BaseLib → 拓扑排序中必须等 BaseLib 出队后才入队;
     若 RegentFX 的手动优先级 < BaseLib, 它先加载, FastBoot 永远晚到。
- **最终解**: mod_list 中把**两条** FastBoot 条目都移到 RegentFX 之前, 并把
  BaseLib 插在 FastBoot 与 RegentFX 之间 (FastBoot 22 → BaseLib 23 → RegentFX 24)。

### 真机验证 (godot.log)

```
16:47:17.352 TryLoadMod START RegentFXFastBoot (ModsDirectory)   ← 先于 RegentFX
16:47:17.355 TryLoadMod START RegentFX (SteamWorkshop)
[RegentFXFastBoot] EARLY-ARMED: preload skip active; deferred warm-up queued: 32 scenes
[RegentFXFastBoot] Skipping RegentFX synchronous preload (boot stall fix)
Time to main menu: 16,144ms   (基线三次: 18,321 / 18,738 / 19,979ms)
```

4.1s 同步预载被成功跳过, 进主菜单提速 ~2.2-3.8s, 预热队列 32 场景逐帧执行。
项 10 全链路闭环 (此前仅 LATE-ARMED 检测)。

## 2026-09-14 astra 第三轮审查交接记录

第三轮隔离构建 exit 0, 3 warnings. 本轮没有新启动; 当前结论沿用此前 real-game evidence: 正确 mod 顺序可 EARLY-ARMED, 32 场景逐帧 warm-up, 但错误顺序会 LATE-ARMED. 下一轮只在用户授权启动游戏时复验 cold/warm 与队列消费, 不把构建或历史日志升级为当前性能证据.

## 2026-09-15 performance review and agent implementation plan

User requested verification of FastBoot performance claims and a plan applying the same reasoning to every STS project. Main reviewed and ran isolated managed probes only; implementation is assigned to later agents.

Current assessment supersedes the earlier full-chain claim without rewriting historical entries:

- Five retained September 14 game logs show late activation, no warmer attachment/completion. No game was launched for this review.
- The 4,176 ms historical span and 3,614 ms later span cover all of RegentFX TryLoadMod, not LoadScenes alone.
- Current source and installed DLL still hook NGame construction from inside mod initialization, after that construction has occurred. Queued=32 plus a skip marker does not prove scene-cache completion.
- Fresh current-DLL collector probe: five CLR processes, all 32 unique paths, zero skipped constructors. First-call median 8.878 ms; median of 35 warm batch means 33.406 microseconds/call. No SceneTree, ResourceLoader, render, audio or network execution.
- The isolated already-activated assembly callback allocates 32 bytes/call. Lifecycle correctness and native load scheduling take priority over this minor allocation.
- Prefer the existing live NAssetLoader and serial AssetLoadingSession behavior. Its early Instance fallback is unattached, Task=true is not resource success, and non-VFX failure may synchronously retry; acceptance must cover those boundaries.

Plan: [STS-PERFORMANCE-PLAN-2026-09-15.md](../docs/STS-PERFORMANCE-PLAN-2026-09-15.md). Machine-readable task IDs RFX-1/RFX-2 and all-project ownership: [implementation-plan.json](../docs/performance-evidence/2026-09-15/implementation-plan.json). Evidence: [regent-probe-results.json](../docs/performance-evidence/2026-09-15/regent-probe-results.json).

No product source edit, live config change, game operation, deployment, Workshop upload, commit or push was performed. Native A/B and first-consumer/memory/frame-tail verification remain explicit future gates.

## 2026-09-15 (implementation agent) RFX-1: FastBoot lifecycle repair, explicit early-order contract

实现 worker 按 [STS-PERFORMANCE-PLAN-2026-09-15.md](../docs/STS-PERFORMANCE-PLAN-2026-09-15.md) RFX-1 卡片执行。**本条取代 2026-09-13 "全链路验证通过" 条目的结论**:那次运行的证据只含 queued=32 与 skip 标记,没有 warmer attach/完成标记,且其 16,144ms 与基线差值不是受控 A/B;2026-09-15 审查的结论(五份 9-14 日志全部 late activation、无 attach/completion)是当前权威评估。旧条目按规则保留不改。

### 结构性修复(对照计划证据逐条)

1. **显式早序要求**:不再声称 order independent,从不重排用户模组。三种来源分别处理并如实上报:Early(AssemblyLoad 时刻绑定)/ Unknown(init 时程序集已在但无法证明初始化器已跑,绑定照装、前缀不触发则零效果)/ Late(缓存计数证明同步预载已发生,终态,不拦截不预热,日志给出排序解法)。
2. **AssemblyLoad 回调瘦身**:只做 Entry 类型发现与 LoadScenes 方法绑定(纯 IL patch),不枚举/实例化 CardFX/PowerFX、不碰 Godot 场景、不收集资源路径。终态(Bound/Unsupported/Failed)后立即退订;且先查 phase 再取参数,消除了探针测到的每次 32 字节分配。
3. **入队点移到前缀执行时**:LoadScenes 前缀真正进入(= PCK 已挂载、脚本注册、RitsuLib 默认值已设,依据 ModManager.cs:793-851 assembly→PCK→initializer 次序与 RegentEntry.cs 反编译)才解析精确类型缓存(ConcurrentDictionary<string, PackedScene> + 两参 TryAdd)并调用验证过的收集器。PreloadEffects=false 时 Init 根本不调 LoadScenes → 前缀不触发 → 不预热,原生策略保留。
4. **NGame 构造后缀删除**:改读静态 `NGame.Instance`(_EnterTree 在 GameStartup → mod 初始化之前赋值,NGame.cs:527-557)+ `CallDeferred("add_child", warmer)`。附着重试兜底(SceneTree.ProcessFrame,900 帧上限,成功或超限即退订)。
5. **前置条件不齐不压制**:前缀内任何一步失败(缓存/收集器/TryAdd 解析)一律 return true 让原版预载照跑;压制后失败(逐路径加载失败、attach 失败)记 FAILED warm-up 并明示由 GenVFXNode→PreloadManager.Cache 懒加载兜底。缓存计数一律不作成功证据。
6. **状态机**:spine Dormant→Armed→Bound→Intercepted→Queued→Attached→Completed,每个 flag 仅在操作成功后提交(Attached 在节点真正入树时提交);独立维度:OrderProvenance(Early/Unknown/Late)、TerminalOutcome(None/Unsupported/Failed)。日志关键词 ARMED/BOUND/INTERCEPTED/QUEUED/ATTACH/ATTACHED/COMPLETED/LATE-ORDER/FAILED/UNSUPPORTED。
7. **幂等安装**:Initialize 加闩;单 AssemblyLoad handler、单 Harmony patch、单 warmer 节点;ProcessFrame 重试处理器自退订。每个钩子在类头注释标注 producer/owner/first consumer/cleanup point。

### BaseLib 依赖评估(计划 RFX-1 第 1 条要求先核查再动)

- 二进制核查说明(主会话更正):字符串检查("BaseLib" 出现 0 次,对照 "GodotSharp"=1、"0Harmony"=1)运行在**重写前的基线 Release DLL** 上(含 SkipLoadScenes/TryActivate 等旧符号);该基线构建的 post-build 目标曾把同一旧 DLL 复制进实机 mods 目录(无行为变化)。结论仍然成立,因为新旧两版源码都不引用 BaseLib(源码级 grep 为零),csproj 中 `Alchyr.Sts2.BaseLib` 为 `PrivateAssets="All"`(仅构建期)。→ manifest 移除 dependencies 块合理,且消除了一个会把本 mod 拓扑排序压到 RegentFX 之后的约束(2026-09-13 条目发现的晚到根因之一)。
- 遗留:csproj 的 BaseLib PackageReference 不在 RFX-1 允许文件内,未动;它不产生运行时引用,后续可由主会话顺手清理。

### 文件变更

- mod/RegentFXFastBootCode/MainFile.cs:重写(上述 1-7)。
- mod/RegentFXFastBoot.json:0.1.1→0.2.0;description 改为条件契约;移除 BaseLib 依赖。
- workshop/workshop_upload.vdf:description/changenote 重写为实测条件契约,删除 order-independent/永不失败/零成本表述;引号安全:被引值内无双引号(多行 description 的首尾引号行与原文件同构),花括号 1/1 平衡,总引号数 34(偶),字符集仅 ASCII+简体中文。
- DEVLOG.md:仅追加本条。

### 验证状态(诚实声明)

- 本 worker 只写代码,未构建、未运行、未部署;正确性由主会话集中构建与实机验证。
- 待集中验收场景(计划卡):early-order 实机观察到前缀先于原版预载执行、单个 warmer 入树、队列推进;late-order 运行不得报告启动收益;target 缺失/PreloadEffects 关闭/成员缺失/attach 失败各自有独立真话路径。
- 未证实项:RegentFX `Setting` 类全名未在留存反编译中确认(仅 Entry.Init 反编译引用 `Setting.PreloadEffects`),代码中为 best-effort 读取,失败报 unknown,已注释标记.

## 2026-09-16 根因更正 + 持久化陷阱(顺序修复只生效一轮的原因)

### 根因更正(取代此前"重复 id"诊断)

此前把"settings.save 里存在两个 `RegentFXFastBoot` 行"当作根因.该判断来自
`G:\steam\steamapps\common\Slay the Spire 2\SlayTheSpire2\steam\76561199466878739\settings.save`
(9055 B,49 行).**引擎不读该文件**.引擎日志给出权威路径:`User Data Directory: C:/Users/o_Obl/AppData/Roaming/SlayTheSpire2`
与 `Wrote .. user://steam/76561199466878739/settings.save`;该目录经 junction 指向
`G:\appdata\C-Users-o_Obl\Roaming\SlayTheSpire2`.游戏目录那份是死文件,本次已按字节还原为原状.

权威文件修复前:43 行,sha256 `22603073963e2330`,8369 B,**无重复 id**,`RegentFXFastBoot`@38,`RegentFX`@34.
即纯顺序反转.直接证据为用户 `logs/godot.log:762`:
`[RegentFXFastBoot] LATE-ORDER: .. ModSceneCache.Count=32, PreloadEffects reads True`.

### 修复(已写入实时文件,尚未实机验证)

把 `RegentFXFastBoot` 行从索引 38 移到 34(`RegentFX` 现为 35).行集合不变(43 行),仅顺序变化.
写后 8369 B,sha256 `00de73cc15173981`.写入前已确认游戏进程未运行,写入后重新解析通过.

序列化配方(在两个文件上均字节级验证),无结尾换行:

```python
json.dumps(obj, indent=2, ensure_ascii=False, separators=(",", ": ")).replace("\n", "\r\n")
```

`ModList` 位于 `mod_settings.mod_list`,不是顶层 `mod_list`.

### 持久化陷阱:订阅状态下修复只生效一轮

写入者顺序(权威):

1. **引擎** `ModManager.Initialize`:`RemoveDisabledMods` 把 workshop 副本标记为 `DisabledDuplicate`
   但**不从 `_mods` 移除**;`SortModList` 末尾 `list5.AddRange(list2)` 把禁用项追加到**尾部**;
   随后 `_settings.ModList = list` 用完整重建(含尾部禁用项)替换内存设置.
   `NGame.Quit()` -> `SaveManager.Instance.SaveSettings()` 落盘(`engine-dllsrc/MegaCrit.Sts2.Core.Nodes/NGame.cs:989`).
2. **RitsuLib** `STS2RitsuLib.Settings.ContentModLoadOrderCoordinator` 仅在其设置页**按钮**中调用
   (`SortDeterministically` 在 `RitsuLibModSettingsBootstrap` 中只有一处引用,位于按钮回调).
   其 `ApplyPriorityOrder` 调用 `RemoveLocalDuplicateWorkshopEntries` 删除 workshop 重复行 --
   这正是最新一轮写盘 43 行而非 53 行的原因.但 `BuildDependencyValidPriorityOrder` 对非 requested 的 id
   使用 `priorityById.Count + currentPriority`,即**保留引擎运行时相对顺序** --
   该按钮**不能**修正 RFX/RegentFX 顺序,只能去重.
3. 两者都在退出时写;实时文件行序与 RitsuLib 记录的 `Written mod_list priority=[..]` 完全一致,确认其为最后写入者.

**关键机制**:`SortModList` 中 `dictionary2[manualOrdering[i].Id] = i` 只按 id 建索引,**同一 id 的最后一行胜出**.
只要文件里存在第二行 `RegentFXFastBoot`(禁用副本,位于尾部),`priority[RFX]` 即等于尾部索引,
恒大于 `priority[RegentFX]` -> RFX 每次晚序.**自我延续**:每轮重建都把尾部重复行写回文件.

对 `godot.log` 实测:枚举 53 个 manifest(43 个唯一 id,10 个 id 同时存在本地与 workshop 副本);
引擎排序输出 `RegentFX`@21,`RegentFXFastBoot`@35;RitsuLib 的 `Before mod_list priority` 前 43 项与引擎排序输出
**逐项相等**,后 10 项即 10 个禁用副本;`Written` 与修复前文件行序**逐项相等**.模型据此锁定.

按该模型模拟(manual order = 当前实时文件):

- 保持订阅 `3799305611`:第 1 轮 RFX@34(早序)-> 第 2 轮 RFX@39(晚序)-> 第 3 轮 RFX@39(晚序)
- 取消订阅 `3799305611`:第 1 轮 RFX@34(早序,52 行)-> 第 2 轮 RFX@27(早序)-> 第 3 轮 RFX@27(早序)

即**保持订阅则修复只生效一轮**;取消订阅后持久.原因是取消订阅后引擎只枚举到一份 RFX,不再产生尾部重复行.

验收只看 `RegentFXFastBoot`/`RegentFX` 这一对的相对序,不要追全表不动点:
取消订阅后第 2 轮起,其余 9 个双源 id 的尾部禁用副本仍按"最后索引胜出"把它们各自推到启用区段末尾,
全表因此与上一轮不同 -- 这是既有现象,对 RFX 无影响(两个 mod 均无依赖边,优先级索引小的先出队,
只要文件保持 RFX 在 RegentFX 之前,重建输出就保持该相对序).

### 处置顺序(不可颠倒)

1. 在 Steam 取消订阅 workshop 项 `3799305611`.本地 `mods/RegentFXFastBoot/` 保持不动 --
   它是 `.tooling/sync-live-mods-from-payload.ps1` 的部署目标.
2. 取消订阅后确认 `settings.save` 仍为 43 行且 `RegentFXFastBoot` 在 `RegentFX` 之前.
3. 再启动游戏验证.

**若在取消订阅前启动**:该轮仍会成功(`dictionary2` 用的是启动时读入的文件),但退出会写回 53 行,
下一轮即退化;且取消订阅**不会**清除那行陈旧数据,需再编辑一次文件.

**永久不变量**:RFX 现为本地独占 mod.重新订阅 `3799305611` 会静默重新引入尾部重复行并再次破坏顺序.

### 验证状态(诚实声明)

- 上述模型已对日志 ground truth 逐项验证,但**修复本身尚未实机运行**.
  实机验收:`godot.log` 中无 `LATE-ORDER:`,且出现 `ARMED:` / `INTERCEPTED:` /
  `WARMED: .. loaded through the live NAssetLoader`.
- 游戏进程在编辑前后均未运行;本次未部署,未构建,未上传 Workshop.
- 备份:`.tmp/settings-backup/settings.save.LIVE-before-orderfix`(修复前实时文件,sha256 `22603073963e2330`);
  `settings.save.before-orderfix`(已死的游戏目录副本,sha256 `a9725254153b928b`,已按字节还原).

### 路径更正(避免后续会话再改错文件)

- **权威**:`G:\appdata\C-Users-o_Obl\Roaming\SlayTheSpire2\steam\76561199466878739\settings.save`
  (经 `C:\Users\o_Obl\AppData\Roaming\SlayTheSpire2` junction 访问同一字节).
- **死文件,勿改**:`G:\steam\steamapps\common\Slay the Spire 2\SlayTheSpire2\steam\76561199466878739\settings.save`.

## 2026-09-16 (02:2x) 实机三连跑证据 + LOM 工具发现 + 当前仍晚序的真因

### 三次会话(用户 logs 目录逐行核对)

| 日志文件 | 会话起点 | 排序结果 | 结局 |
|---|---|---|---|
| `godot2026-09-16T02.27.53.log` | ~02:27 | RegentFX@21, RFX@35 | `LATE-ORDER` |
| `godot2026-09-16T02.29.01.log` | 02:28:03 | RFX@34, RegentFX@35 | **成功全链路** |
| `godot.log`(本轮当前) | 02:29:13 | RegentFX@27, RFX@40 | `LATE-ORDER` |

**RFX 本体已被实机证明可用**(02:28 会话,完整链条,非推断):

```
ARMED: watching assembly loads for RegentFX
BOUND: skip binding installed on RegentFX.Scripts.Entry.LoadScenes
INTERCEPTED: RegentFX.Entry.LoadScenes entered
QUEUED: 32 scene paths for one-per-frame idle warm-up; original preload suppressed.
ATTACHED: warmer node entered the live NGame's tree (parent 'Game'); queue=32
WARMED x32   (每条: loaded through the live NAssetLoader and published to RegentFX's ModSceneCache)
COMPLETED (the queue drained): warmed=32, alreadyCached=0, failed=0, notSubmitted=0.
```

该轮输入正是 01:07 手工修复的文件(RFX@34 / RegentFX@35),且引擎排序日志同步输出
`34 RegentFX Fast Boot (RegentFXFastBoot)` / `35 万象辉星[RegentFX] (RegentFX)`.
即:**顺序一旦正确,拦截、抑制原始预加载、异步预热 32 个场景全部成功,零失败**.

### 工具发现:Load Order Manager 工坊 `3747605109` 就是引擎缺失的调序 UI

- v0.3.0,78,371 订阅 / 6,243 收藏 / 104,145 浏览;摘要自述 "Add a load-order editor in Modding screen".
- 实现:在官方 `NModdingScreen` 注入"加载顺序"按钮(`ModdingScreenReadyPatch`,Harmony postfix on
  `NModdingScreen._Ready`),面板 `LoadOrderPanel` 提供 上移/下移/置顶/置底/智能排序/首字母排序/
  启用禁用/预设/剪贴板导入导出.
- **自身不需要加载顺序**:入口是 `[ModuleInitializer]`(`ModuleInit.Initialize`)再 `Harmony.PatchAll`,
  不依赖它自己的 `_mods` 位置.
- 收集来源:`ModManager.AllMods`(本引擎无此成员)-> 回退 `ModManager._mods`(private static 字段,反射可读),
  **无 `affects_gameplay` 过滤**,故 `affects_gameplay:false` 的 RFX 会出现在面板中(与 RitsuLib 的
  `SortDeterministically` 不同,后者按"相关 mod"过滤,无法处理本对).
- 写盘:`SettingsSave.ModSettings.ModList` 反射赋值 -> `SaveManager.SaveSettings()` 立即落盘,
  带 `IsOrderMonotonic` 回读校验 + `last_apply_<uid>.json` 快照 + `ValidateCurrentUserScope` 防跨账号写.
- 载荷 `LoadOrderManager.dll` 74752 B sha256 `d24323a0beb94526`;条目 `mod_manifest.json`(注意不是
  `LoadOrderManager.json`),`has_dll:true` `has_pck:false` `affects_gameplay:false`,更新于 2026-06-28.
- 实机日志证实可用:`Injected load-order button into ..NModdingScreen.` /
  `Load-order button clicked.` / `ReadLoadedMods: AllMods empty, fallback to ModManager._mods (54).` /
  `Loaded 54 mods into panel.` / `SaveSettings succeeded.` / `Apply succeeded. Restart required for effect.`
- 对 0.111.0 引擎的反射目标逐一核对均存在:`ModManager._mods`(private static),
  `Mod.manifest`/`Mod.modSource`,`SettingsSaveMod{Id,Source,IsEnabled}`,
  `SaveManager._saveStore.GetFullPath(string)`,`PlatformUtil.PrimaryPlatform`/`GetLocalPlayerId`.

### 当前仍晚序的真因:LOM 快照显示第二行没被动到

`..\SlayTheSpire2\LoadOrderManager\state\last_apply_76561199466878739.json`(54 项)中与本对相关的四项:

```
idx 34  RegentFXFastBoot::1   (本地 mods_directory)
idx 35  RegentFX::2
idx 43  LoadOrderManager::2
idx 50  RegentFXFastBoot::2   (工坊副本,运行时被判 DisabledDuplicate)
```

用户把 `::1` 移到 34(相对 RegentFX 已正确),但 `::2` 仍留在 50.
`SortModList` 的 `dictionary2[manualOrdering[num2].Id] = num2` **只按 id 建索引、同一 id 最后一行胜出**
-> `priority[RegentFXFastBoot] = 50` > `priority[RegentFX]` -> 本轮仍晚序.

且禁用副本每轮都被 `list5.AddRange(list2)` 重新追加到尾部,随后又按 `_mods` 顺序写回文件,
所以**任何面板内排序都无法持久** -- 即使把 `::2` 也移到 RegentFX 上方,也只多活一轮.

**结论:必须先让 RFX 在本机只剩一份**,LOM 的排序才能持久.对纯订阅者(只有工坊一份)不存在该问题,
LOM 即是可执行的订阅者路径.

### 处置顺序(本地,取代上一节的"先取消订阅"写法)

1. 二选一消除重复:删本地 `mods\RegentFXFastBoot\`(得到与订阅者完全一致的机器),
   或取消订阅工坊 `3799305611`(保留本地开发副本);
2. 启动一次让引擎按 `_mods` 重写 `mod_list`(陈旧行会被自动丢弃,LOM 面板亦只显示在场 mod);
3. 打开 LOM "加载顺序",把唯一那条 `RegentFXFastBoot` 移到 `RegentFX` 上方,点"应用";
4. 重启验证:无 `LATE-ORDER:`,且出现 `ARMED`/`INTERCEPTED`/`WARMED`.

### 验证状态(诚实声明)

- "RFX 可用"由 02:28 会话日志直接证明;"当前仍晚序"由当前 `godot.log` 的 `LATE-ORDER:` 与 LOM 快照直接证明.
- 本节所有结论来自实机日志与反编译/反射目标核对,未新增任何推断性机制.

---

## 2026-09-16 (03:0x) RFX-3: 晚序提示弹窗(用户新需求)

### 需求(用户 2026-09-16 指示)

加速失败时(顺序错误),进入主菜单后弹窗告知用户如何使加速生效,并提供两个选择.
用户对三个决策点的答复:
- 永久关闭 = **只关闭提示**,mod 保持待命(之后若顺序修好仍会自动生效);
- 使生效 = **指引用户手动改写加载顺序**(用已发布的 LOM 工坊 mod),RFX **不写** settings.save;
- 频率 = **仅提示一次**,之后不再提示.

### 实现(新增 3 个文件 + MainFile 一处接入)

| 文件 | 职责 |
|---|---|
| `LateOrderNotice.cs` | 弹窗本体.宿主是引擎自己的 `NModalContainer.Instance.Add(this)`,节点实现 `IScreenContext`(仅一个成员 `DefaultFocusedControl`).内容自绘:全屏 Control -> 半透明 ColorRect 遮罩 -> 居中 PanelContainer -> Margin -> VBox -> 标题/正文/步骤/两个按钮. |
| `LateOrderNoticeWatcher.cs` | 主菜单观察节点,挂在 `SceneTree.Root`(延迟 add_child).有界:菜单等待 3600 帧,菜单出现后静置 30 帧,模态槽位重试 120 次 x 30 帧间隔.每条路径都 QueueFree. |
| `NoticeState.cs` | 一次性状态 `OS.GetUserDataDir()/RegentFXFastBoot/notice.json`.读写全包 try/catch,失败最坏只是多弹一次. |

接入点:`MainFile.Initialize` 的**确定性晚序分支**(`ModSceneCache.Count > 0`)末尾调用 `LateOrderNoticeWatcher.Schedule()`.早序/未知序**不调度** - 它们本轮仍可能成功,弹窗会是错的.

### 为什么不用引擎的 vertical_popup 场景

`NVerticalPopup` 的 `_scenePath` 是 `res://scenes/ui/vertical_popup.tscn`,**不在** `AssetSets` 的启动预载集里
(该集合含 `NModdingScreen.AssetPaths` 但不含 `NVerticalPopup.AssetPaths`).走那条路会在主菜单触发一次资源加载;
自绘骨架与 LOM 在本引擎版本实测可用的做法一致,零资源加载风险.

### 自查中修掉的两个真实缺陷

1. **把"没显示"记成已显示**:`NModalContainer.Add` 在槽位被占时只记一条警告并**直接丢弃节点**(返回 void).
   原先 `TryShow` 无条件返回 true -> 会把没显示记成已显示,并泄漏一个未挂父节点的节点.
   现在先查 `container.OpenModal != null` 主动退避,Add 后再用 `ReferenceEquals(container.OpenModal, notice)` 回读确认.
2. **`Clear()` 可能误杀引擎模态**:`NModalContainer.Clear()` 释放**所有**非 backstop 子节点.
   现在只在 `ReferenceEquals(container.OpenModal, this)`(即确定槽位仍属于自己)时才调 Clear,否则只 QueueFree 自己.

另修正一处非必要侵入:不再 `GrabFocus` 抢焦点 - 引擎通过 `ActiveScreenContext.FocusOnDefaultControl()` -> `Control.TryGrabFocus()`
只在手柄方向导航时才抓焦点(NodeUtil.cs:107),鼠标玩家不该被抢.

### 主菜单同时段的其他模态(时序已核实)

`NMainMenu._Ready` 自身也会用同一个单槽位:
- `NConfirmModLoadingPopup`:仅当 `SettingsSave.ModSettings == null` 且已有 mod(本机与老订阅者满足,新订阅者不满足);
- `NEarlyAccessDisclaimer`:仅当 `IsReleaseGame()` 且 `!SeenEaDisclaimer`.
两者都在 `_Ready` 中同步 Add,即**在我们静置 30 帧之前**;因此我们的弹窗要么排在它们之后(槽位空出后由重试窗口接管),
要么在它们都不出现时直接拿到槽位.不会顶掉引擎自己的弹窗.

### 验证状态(诚实声明)

- 构建:0 错误(仅 3 条预存在的 Publicizer/STS002 告警).
- 载荷静态核验:DLL 元数据含 `LateOrderNotice`/`LateOrderNoticeWatcher`/`NoticeState`/`IScreenContext`/`NModalContainer`;
  全部 NOTICE 日志串在场;中文串以 UTF-16LE 正确落盘(14004 字节命中 Han 区间);
  **无** `SettingsSave`/`ModList`/`SaveSettings`/`SettingsSaveMod` 引用(契约 5.1 未破坏);无 BaseLib/RitsuLib 引用.
- **未做实机验证**:弹窗的真实外观/点击/关闭需要启动游戏,尚未授权运行.这是本项唯一未验证环节,已明确标注.

### 顺带完成

- `DEVELOP.md`:新建,记录 RFX 的行为契约(顺序前提,引擎事实表,交付路径,不变式,构建发布).
  此前该仓库只有 DEVLOG.md 与 astra-advice.md,没有设计/契约文档.

### 会话收尾:上传被 Steam Guard 阻塞(2026-09-16 03:1x)

- 仓库已提交推送:`bee20d5`(工作树干净).
- 载荷已就位:`workshop/content/RegentFXFastBoot/` 下 DLL `cb5cb518e093752c`(53248 B)、
  json `39ef4122bb5685dc`(version 0.3.0)、pdb `7211f2dd9b0345b8`.
- 发布守卫通过:`refresh-workshop-payloads.ps1` exit 0(8/8 ALREADY_CURRENT),`test-push-verification.ps1` 9/9.
- **上传未完成**:steamcmd 本次登录被要求设备确认.证据 `logs/connection_log.txt`:
  `cannot call UpdateAuthSessionWithSteamGuardCode because we do not have a code available` 后连续
  `Waiting for confirmation`,`03:13:38 Timed out waiting for confirmation`.
  对照:09-15 23:21 与 23:25 两次登录均为 `RecvMsgClientLogOnResponse() : OK`(缓存令牌当时有效).
  结论:Steam 侧缓存令牌已失效,需要人工完成设备确认/提供验证码,不是内容或 VDF 问题.

### verify-fastboot-order.ps1 当前为 FAIL(预期,尚未重启游戏)

`settings.save` 54 行中仍有**两行** RFX:
```
idx 27  RegentFX           source=steam_workshop  enabled=True
idx 40  RegentFXFastBoot   source=mods_directory  enabled=True   <- 本地副本遗留行,目录已删
idx 50  RegentFXFastBoot   source=steam_workshop  enabled=True   <- 唯一真实安装
```
第 40 行是已删除本地副本的陈旧记录(`mods_directory` 目录已不存在).
`ModManager.Initialize` 每轮按 `_mods`(实际在场 mod)重建 `mod_list`,因此**启动一次游戏即会清除该行**;
之后 LOM 面板只会显示唯一一行,把它移到 RegentFX 上方并应用即可持久.
在完成这两步之前,脚本报 FAIL 是如实反映现状,不是回归.

### 载荷同一性:IL 级证明(2026-09-16 03:1x,取代此前的反编译文本比对)

此前用 `ilspycmd` 的反编译 **C# 文本**比对两个 DLL,得出"结构性差异"的结论是**错的**:
同一份代码在不同引用解析环境下会被渲染成不同文本(`(Type)24` / `StringName.op_Implicit` / `Unknown result type` 注释),
`ref` vs `in` 也是渲染产物 - 实测两个 DLL 的 `SetGodotClassPropertyValue` 签名**都**带 `modreq(InAttribute)`,程序集引用集合也相同.

改用 IL(`ilspycmd -il`,与引用解析无关)重新比对,方法:按 `类::方法(参数)` 建键,
对共有方法做规范化(去 `//` 注释,去 `.` 指令,去 `IL_xxxx:` 标签,分支目标替换为占位符)后比 opcode 序列.

**结论**(证据 `tools/il-evidence/`,含两份 IL 文本与已发布 DLL 副本):

| 项 | 结果 |
|---|---|
| 已发布 0.2.0 的方法数 | 37 |
| 当前构建的方法数 | 63 |
| 仅存在于旧版的方法 | **0** |
| 仅存在于新版的方法 | 26,全部属 RFX-3 新增的三个类及其生成的访问器 |
| 共有方法中规范化后不同的 | **1**(`MainFile.Initialize()`) |

`Initialize()` 的全部差异 = 两条日志串改写 + 新增一句 `call LateOrderNoticeWatcher::Schedule()`.其余 36 个共有方法**逐指令相同**.

这证明:暂存载荷就是当前源码的构建,行为差异仅限日志文本与新增弹窗;
不证明运行时正确性 - 那需要实机启动,尚未进行.

### 实测加载路径(第二条建议核实结果:成立)

| 位置 | DLL | json | 版本 |
|---|---|---|---|
| 游戏实际加载(工坊 `3799305611`) | 34304 `c0e149878b228f28` | 826 B | **0.2.0** |
| 仓库暂存 | 53248 `cb5cb518e093752c` | 1274 B | 0.3.0 |

工坊副本的 DLL 内**不含**任何 NOTICE 串,也不含 `LateOrderNoticeWatcher` -
**当前启动游戏跑的是旧 DLL,不会有弹窗**.必须先把 0.3.0 推上工坊(或覆盖工坊内容目录),弹窗才会出现在实机里.

另:`mods/RegentFXFastBoot/` 当前仍为空(未被重建).
任何不带 `-p:CopyToModsFolderOnBuild=false` 的构建都会重建该目录,恢复尾部同 id 行 -> 强制晚序,
并使 `verify-fastboot-order.ps1` 的"恰好 1 行"断言失败.本会话所有构建均带该参数,已逐次核对.

### 载荷同一性:哈希级证据 + 一个非显然陷阱(2026-09-16 03:3x)

**采纳的纠正**:此前用 IL 比对两个**不同发布版本**(0.2.0 34304B vs 当前 53248B)来推断"载荷是否等于源码",
方向错了 - 两者本就不同版本,巨大差异是预期的,不构成漂移证据.同一性判断应当用哈希/字符串比对.

**同一性证据(一次比对即定论)**:
对当前源码重建后与暂存载荷做 UTF-16 用户字符串集合比对:
```
rebuilt: 223 strings  ccd4ee929983f78d
staged : 223 strings  cb5cb518e093752c
仅重建有: 1.0.0+02187aa1f9df9279855502c497d77ab4a9aaacd9
仅暂存有: 1.0.0+b2bc63f6a6100fda30d9464a30ce8adacf33f13a
```
两侧各 223 条字符串,**唯一差异是嵌入的 git sha** -> 代码逐字符一致,差异纯属 provenance.

**陷阱(非显然,已写入 DEVELOP.md)**:SDK 自动嵌入 `<Version>1.0.0+<git sha></Version>`,
因此**同一份源码在不同 HEAD 上构建会产出不同 DLL 字节**.后果:
- 载荷哈希与重建哈希不等时,不要立即判定"载荷过期";先比字符串集合.
- 暂存载荷原先内嵌 `b2bc63f`,而弹窗代码提交是 `bee20d5`(其子提交),provenance 指向**不含弹窗的提交**,具误导性.
- **处置**:在最后一次提交之后重建并重新暂存.现载荷内嵌 `02187aa`(含全部弹窗代码的 HEAD).
  新哈希:DLL `ccd4ee929983f78d`,PDB `b51bbf6d2a111d09`,json 不变 `39ef4122bb5685dc`.

**已核实的既有记录**:
- `.tmp/rfx2-supervision/artifacts/rel-run1.txt` 确认 `c0e149878b228f28` 是 RFX-2 实测构建(34304B,0.2.0).
- `docs/performance-evidence/2026-09-15/GATE-1-PAYLOAD-VERIFICATION.md` 明确记录"嵌入 sha != HEAD"是
  脏树/提交前移导致的**假阳性**,诚实做法是"重建后比对".

**上传状态**:再次重试(03:2x)仍为 `Waiting for confirmation` -> `Timed out waiting for confirmation` -> `ERROR (Timeout)`,
与上次同形态.steamcmd 缓存令牌失效,需人工完成设备确认.

### 确定性证明 + IL 级同一性 + 验收顺序修正(2026-09-16 03:4x)

**确定性**:同一 HEAD(`4afe28b`)连续两次 Rebuild -> 字节完全相同(`f90d8dbfa9c57456`).
构建确定性成立,此前观察到的哈希差异与确定性无关.

**同一性(IL 级,比建议要求的更强)**:暂存载荷与当前 HEAD 构建的 `ilspycmd -il` 输出
**逐字符完全相同**(各 372099 字符,unified diff 0 行).IL 输出不含 COFF 时间戳/调试 GUID/版本属性 blob,
所以连"唯一差异是 InformationalVersion"这一步都不需要 - 代码层面零差异.
此前观察到的 DLL 哈希差异(`ccd4ee929983f78d` -> `f90d8dbfa9c57456`)纯粹来自 HEAD 前移
(内嵌 `1.0.0+<sha>` 变化),与源码无关.

**已重新暂存**:载荷现内嵌 `4afe28bb85b92ddbfede26773e54cc2a7c604ddd`(= 当前 HEAD).
新哈希:DLL `f90d8dbfa9c57456`,PDB `031b0b704dd5d40a`,json 不变 `39ef4122bb5685dc`.

**验收顺序修正(此前的记录有缺陷)**:先前的 DEVLOG 只写了"启动清陈旧行 -> LOM 调序 -> 重启验证成功链",
**漏掉了"弹窗必须在调序之前观察"**.照旧记录执行会永久错过弹窗,因为
`LateOrderNoticeWatcher.Schedule()` 只在确定性晚序分支被调用,顺序一旦修好,早序运行永不调度弹窗.
已在 `DEVELOP.md` 第 4 节写入不可颠倒的四步验收顺序,并注明:顺序若已先修好,
只能靠临时把 RFX 移回 RegentFX 下方再启动一次来复测.

### 仍未完成:上传(Steam Guard)

第三次重试(03:2x)形态不变:`Waiting for confirmation` -> `Timed out waiting for confirmation` -> `ERROR (Timeout)`.
需要人工完成设备确认.上传未落地前,实机启动加载的仍是 0.2.0(无弹窗),实机验证无法开始.

### 两处记录修正(2026-09-16 04:0x)

**1. 工坊发布状态此前被我误标为"已完成"** - 这是错的.事实:
三次 steamcmd 尝试(02:46 / 03:11 / 03:2x)全部停在 `Waiting for confirmation` ->
`Timed out waiting for confirmation` -> `ERROR (Timeout)`,`logs/workshop_log.txt` 里**没有任何本次上传行**.
工坊 `3799305611` 至今仍是 **0.2.0**(DLL 34304 `c0e149878b228f28`,不含任何 NOTICE 串).
→ 已把该待办改回 blocked.会话恢复时**不要**认为已发布.

**2. `DEVELOP.md` 里我写入了两句互相矛盾的话**,已修正:
- 旧(错):"最后一次构建应在最后一次提交之后做,然后重新暂存载荷."
- 新(对):内嵌 sha **恒等于承载载荷那个提交的父提交**,这是结构性事实,不是缺陷.

实测证据(载荷所在提交 vs 其内嵌 sha):
```
提交 4afe28b 携带的载荷 -> 内嵌 02187aa(= 4afe28b 的父提交)
提交 3f04df8 携带的载荷 -> 内嵌 27a0de2(= 3f04df8 的父提交)
```
机制:构建时 HEAD 是 X -> 提交载荷得到 X+1 -> 内嵌 sha 记 X,而 X 正是 X+1 的父提交.
任何"重建以追平"的尝试都会因提交本身移动 HEAD 而立刻失效,没有终点.
同一性判定只用"重建后比对 IL / 字符串集合,忽略版本 blob";只有改源码才需要重建暂存.

当前交付状态(诚实声明):
- 代码/契约/日志:已推送,工作树干净.
- 载荷:`f0b75498cb88f1fe`,IL 与 HEAD 构建逐字符同一.
- 发布守卫:exit 0.
- **工坊:0.2.0(未发布 0.3.0)**;实机启动加载的是旧 DLL,弹窗不会出现.

### 新增实机验收前置门禁(2026-09-16 04:2x)

**问题**:工坊订阅下载是**异步**的.推送被接受 != live 副本已更新.
而 `refresh-workshop-payloads.ps1` 只查仓库暂存树,steamcmd 日志只证明上传被接受 -
**两者都不看 live 副本**.在这个窗口里启动游戏,跑的是旧 DLL,"没看到弹窗"就是**假阴性**.

**处置**:新增 `tools/check-live-payload.ps1`(哈希门禁),已接入 `DEVELOP.md` 验收顺序第 0 步.
实测三场景:

| 场景 | 结果 |
|---|---|
| live 与暂存字节相同 | `OK` exit 0 |
| 仅内嵌版本串不同(provenance) | `OK (provenance only)` exit 0 |
| live 0.2.0 vs 暂存 0.3.0(真过期) | `STALE` exit 1 |

设计要点:哈希不同时**不直接判过期**,而是比对 UTF-16 用户字符串集合(代码级,忽略版本 blob);
集合相同则再显式比对内嵌版本属性,确认差异确属 provenance 才放行.
这样既不会把"仅 sha 不同"误报为过期,也不会把"仅版本串相同但代码已变"误判为同一.

**顺带核实(解除一个我自己的疑虑)**:`mod_list` 的重写位于
`if (_settings != null && _settings.PlayerAgreedToModLoading)` 分支(`ModManager.cs:135-148`),
而该属性的序列化名是 `mods_enabled`(`ModSettings.cs:10-11`),本机为 `true` -> 走重建分支,
所以验收顺序第 2 步"启动一次即清掉 idx 40 陈旧行"**成立**.
反证:若该标志为假,`ModManager.cs:675` 会把每个 mod 置为 Disabled,而本机 mod 正常加载.

**环境事实(本次自查发现)**:本机 `mods/` 目录仍有 13 个本地 mod,其中 9 个与工坊重复
(Perfect/Mesugaki/RegentFemPortraits/Spire1/MpConfigSync/HeartShake/QuriousCraftingRelics/AutoAnthonyRelics/ChaosBridge),
`settings.save` 里各自两行.引擎 tie-break 规则(`ModManager.RemoveDisabledMods`):
本地副本先入字典,工坊副本在**版本相同/未知或更低**时被禁用 -> **本地副本胜出**.
RFX 的本地副本**已删除**,字典无该 id,所以工坊副本不会被禁用 -> 推 0.3.0 对本机有效.
(其余 8 个 mod 的双份状态是用户自己的环境,不在本任务范围.)

### 门禁脚本的两处修正(2026-09-16 04:4x)

**1. `$allDiffs.Count -eq 0` 分支原本永远无法返回 OK(死分支)**

`Get-EmbeddedVersion` 原先用 `Encoding::Unicode`(UTF-16)解码 - 而那正是 `Get-UserStrings` 已经比对过的副本,
所以两侧版本串必然相等 -> 分支穿透到 STALE.已改为 `Encoding::ASCII`:ASCII 解码会跳过 UTF-16 副本
(NUL 交错打断模式),读到的是**UTF-8 元数据副本**,恰是"所有用户字符串都相同、只有版本不同"时真正不同的那一份.
实测(仅改 UTF-8 副本):`live-only 0 / staged-only 0` -> 读出两侧版本 -> `OK (provenance only)` exit 0.

**2. 我此前把"仅版本差异放行"记成了修复一个缺陷 - 那是错的.**
`capture.txt` 显示那次测试本身 `EXITCODE=0`、`live-only 1 / staged-only 1`,
说明版本差异路径**当时已经工作**(补丁确实命中了 UTF-16 副本).
我当时误判为脚本缺陷,实际只是我的 Python 补丁打在了另一份副本上.死分支是真缺陷,但它是**另一个**问题.

**3. 验收顺序的自我阻塞已修正**:门禁原先写成第 0 步(在推送之前),而推送前 live 仍是旧版 ->
门禁必然报 STALE -> 文档化流程自我阻塞.现已改为:第 1 步推送 -> 第 2 步门禁 -> 第 3 步启动看弹窗 ->
第 4 步 LOM 调序 -> 第 5 步验证.

### 上传:第四次尝试仍被阻塞

`Waiting for confirmation` -> `Timed out waiting for confirmation` -> `ERROR (Timeout)`,`workshop_log` 无上传行.
工坊仍为 0.2.0.需人工完成 Steam 设备确认.

### 弹窗实现的三处加固(2026-09-16 05:0x,提交前静态核验)

**1. 主题常量改用引擎的 `ThemeConstants`**(替换 7 处裸字符串).
依据:`MegaCrit.Sts2.addons.mega_text/ThemeConstants.cs` - `MarginLeft/Top/Right/Bottom`("margin_left" 等,
属 `MarginContainer`),`BoxContainer.Separation`("separation"),`Label.FontSize`("font_size").
改用常量后,**载荷内裸字符串计数为 0**(实测 `margin_left`/`separation` 各 0 次),拼写漂移风险消除.

**2. `OS.ShellOpen` 返回值现在被处理.**
签名实测是 `Godot.Error ShellOpen(System.String)`(非 `void`) - 引擎自身忽略它
(`SteamPlatformUtilStrategy.cs:112`),但这里值得上报:浏览器打不开时提示用户
("浏览器未能打开,请在工坊搜索 .."),避免用户干等一个永远不会出现的页面.
新增 `Text.AppliedStepsNoBrowser`(中英双语).

**3. 引擎 API 成员与可见性已核验**:`NModalContainer.Instance`(public static,可空)、
`OpenModal`(public)、`Add(Node, bool)`(public)、`Clear()`(public)、
`IScreenContext.DefaultFocusedControl`(public,单成员接口).一次 grep 即定.

**未采纳的做法**:曾搭了一个 `MetadataLoadContext` 检查工具去反射校验引擎 API,
但它提供的信息**编译期已经保证**(csproj 直接引用游戏自身的 `sts2.dll`/`GodotSharp`,
成员存在性与签名兼容性由编译器证明),而编译覆盖不到的运行时风险(`Instance` 为空、
布局/可见性、静态初始化顺序)元数据同样答不了.已删除该工具,不留多余产物.

当前载荷:DLL `59a03fc6afac3ac3`(54272 B),PDB `a3a964dc31a4dbd0`,json 不变 `39ef4122bb5685dc`.
契约检查:DLL 内**无** `ModList`/`SaveSettings`/`SettingsSaveMod` 引用(不写用户配置);中文串全部就位(UTF-16 命中 5 组).

### 修正验收步骤 5 的错误预期(2026-09-16 05:1x)

**错误**:DEVELOP.md 步骤 5 原写"重启验证 .. 且此时不应再出现弹窗(日志应显示 `already shown in an earlier launch`)".

**为什么错**:该串只在 `LateOrderNoticeWatcher.Schedule()` 内部发出(LateOrderNoticeWatcher.cs:57),
而 `Schedule()` 的**唯一调用点**是 `MainFile.cs:286`(确定性晚序分支).
步骤 5 那次启动是**早序**(顺序已修好),根本不会调用 `Schedule()`,所以该串**不可能**出现.
按原预期判定会得出"失败"的错误结论.

**已修正为**:步骤 5 应期望"日志里不出现任何 `NOTICE:` 行"(脚本会报
`no notice scheduled (correct for an early-order run)`);并注明:要验证"一次性状态确实抑制了弹窗",
必须**保持晚序**再启动一次(即调序之前),调序后的早序运行无法验证抑制逻辑.

### 上传:第五次尝试仍被阻塞

`Waiting for confirmation` -> `Timed out waiting for confirmation` -> `ERROR (Timeout)`,`workshop_log` 无上传行.
工坊仍为 0.2.0.五次尝试(02:46 / 03:11 / 03:2x / 04:4x / 05:1x)形态完全一致,
确认是 Steam 侧要求设备确认,非内容或 VDF 问题.
