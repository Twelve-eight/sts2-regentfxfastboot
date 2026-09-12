
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
