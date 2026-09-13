## 第二轮复审 (2026-09-13)

当前隔离构建 exit 0, 3 warning/0 error. 当前 live `godot.log:938-940` 再次显示 RegentFX 已先同步预加载 32 scenes, FastBoot 随后才安装 skip prefix; 本次功能仍是 late-armed, 只在 mod 顺序满足前置时才有价值. 历史 early-armed run 不能推广成当前 profile 的通用结果.

建议继续把 early-armed/late-armed 明确分级, 不打印无条件修复成功. 本轮没有重新启动游戏, 没有修改 mod list. 真实冷启动/暖启动/最大单帧和 warmer 队列仍需按同一 mod 集合复验.

# Astra advice - RegentFXFastBoot

日期: 2026-09-12. 主会话单线. 本轮隔离构建 exit 0, 3 警告, 0 错误. 已查看真实历史启动日志与当前 RegentFX/引擎控制流, 没有新启动游戏.

## P1 RFX-1: "load-order independent" 不成立, 已有运行反证

位置: `mod/RegentFXFastBootCode/MainFile.cs:58-69,80-109`.

历史 godot.log 同一次启动:

- :429 UTC 21:02:56.518, RegentFX 开始加载.
- :433-434 同步预加载 32 场景, 全成功.
- :437 UTC 21:03:00.694, RegentFX 结束, 差 4.176 秒.
- :894-899 UTC 21:03:03.113, RegentFXFastBoot 才初始化, 安装 LoadScenes prefix, queued 0 scenes.

所以一次 "patched" 日志只证明以后调用可能被拦截, 不可能消除已经完成的 4 秒等待. 扫描已加载程序集不是时间机器. 当前 manifest 只依赖 BaseLib, 不能保证在 RegentFX 前执行; 此次 BaseLib 自己也晚于 RegentFX.

建议先解决真实加载顺序/启动入口前置, 再宣传降低 stall. 如果前置条件无法保证, 如实输出 too-late/dormant, 不打印 order-independent fixed. 不为此随意修改第三方工坊 DLL 或用户 mod 排序配置.

验收: 冷启动在 RegentFX 的 LoadScenes 首次执行前已挂载; 原同步预加载日志不出现; 相同 mod 集合/顺序下重复测量主菜单到达和最大帧停顿. 暖缓存结果单独列出.

## P1 RFX-2: warmer 挂在已完成的 NGame 构造器上

位置: MainFile.cs:143-181.

引擎顺序: NGame 已构造并进入 _EnterTree -> GameStartupWrapper -> GameStartup -> OneTimeInitialization.ExecuteVeryEarly -> ModManager.Initialize -> 模组 initializer.

所以在 initializer 中才给 NGame 构造器打 postfix, 不会对当前这个 NGame 回溯执行. 若成功拦住 RegentFX.LoadScenes 并排入 Pending, 当前游戏又没有新 NGame 构造, warmer 不会挂载, 队列不被消费.

建议在明确主线程且场景树可用时对已经存在的 NGame 安全 defer AddChild, 或使用仍会到来的生命周期事件. 一次性安装/销毁, 不在后台线程直接调用 Godot ResourceLoader.

验收: 先跳过同步加载, 后确实出现 warmer node, Pending 逐帧清空, first combat 前缓存按约定完成, 退出不会留节点/重复排队. 原构造器 patch info 非空不算 node 存在证明.

## P2 RFX-3: 32 次同步 Load 分散到帧, 仍可能有单帧尖峰

_Process 每帧 ResourceLoader.Load 一次, 这不是异步资源加载. 单个重场景仍可卡一帧. 若目标是避免黑屏长冻结, 这种调度可能已够; 若目标是帧时间平滑, 必须测最慢单项, 不能只称 total boot cost zero.

建议先运行证据支持最小方案, 必要时使用 Godot 明确线程加载接口并在主线程取资源/实例化. 不把 ConcurrentDictionary 误当资源加载线程安全保证.

此外 CollectAssetPathsSafely 会反射实例化 CardFX/PowerFX, 不是注释所说绝对无副作用. OnAssemblyLoad 可能不是适合 Godot 的线程, 该阶段只做可证明安全的托管准备, 真正场景操作移回主线程.

## 交付边界

- 不修改 gameplay, 但启动顺序和资源缓存有真实影响.
- 不以 "0 构建错误" 或 "queued 0" 当性能改善.
- 报告来源快照与冷/暖状态. BootTimer 很适合提供时间证据, 但要提前加载才能看到目标.
- no PCK/无内容的 STS002 警告是模板分析器噪声, 不是此处核心风险; 不为清 warning 创建无意义 localization.

证据: `../astra-advice-evidence/2026-09-12/historical-startup-evidence.txt`, `build-results.json`; 对照源 NGame._EnterTree/GameStartup 和 OneTimeInitialization.ExecuteVeryEarly. 历史日志不是本轮新冒烟.

## 附录: 先证明先后关系, 再谈性能数字

通用流程见 [总建议附录](../astra-advice.md).

- 把承诺写成三个可观察时点: patch 安装先于首次同步加载; warmer 挂载晚于合法主线程/场景树就绪; 消费缓存先于需要它的效果. 某一步已经过去, 后来订阅事件不可能补做.
- 对提前/正常/太晚三种安装时机各推演一次. 扫到已加载程序集只证明发现目标, 不证明避免过启动开销. 对太晚要如实失败或按已定契约降级, 不印一条 fixed 日志.
- queued=0 要问原因: 没有需要处理, 原版已经全部处理, 还是路径收集失败? 同一个计数能对应三种相反的性能结论.
- 总耗时, 最慢单帧, 首次战斗缓存命中是不同指标. 分帧同步加载可能改善长冻结但保留单项尖峰, 必须测目标指标, 不把工作移动到稍后就称 zero cost.

最短复验: 同一真实 mod 集合和冷暖条件, 先读时间线确认安装顺序, 再看是否跳过原同步路径, 最后观察队列消费/首个效果. 别只对比主菜单总时间, 它可能被其他 mod 或缓存状态主导.
