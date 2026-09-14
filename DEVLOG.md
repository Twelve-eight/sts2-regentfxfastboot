
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
- 未证实项:RegentFX `Setting` 类全名未在留存反编译中确认(仅 Entry.Init 反编译引用 `Setting.PreloadEffects`),代码中为 best-effort 读取、失败报 unknown,已注释标记。
