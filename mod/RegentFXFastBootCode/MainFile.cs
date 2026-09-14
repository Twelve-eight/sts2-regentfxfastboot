using System;
using System.Collections.Generic;
using System.Reflection;

using Godot;
using HarmonyLib;

using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;

namespace RegentFXFastBoot.RegentFXFastBootCode;

/// <summary>
/// RegentFX boot-stall mitigation (the measured stall was 4.1s on this machine, 2026-09-11).
///
/// EXPLICIT ORDER REQUIREMENT (RFX-1, 2026-09-15): this mod must initialize BEFORE the
/// RegentFX assembly is loaded. It is NOT load-order independent, it never reorders user
/// mods, and it never writes user configuration. The user must place RegentFXFastBoot
/// ABOVE RegentFX in the in-game mod list; the loader honors that manual order
/// (ModManager.SortModList, engine ModManager.cs:193). If RegentFX's initializer already
/// ran this launch, that is LATE order: nothing is suppressed, nothing is warmed, and the
/// log says so truthfully.
///
/// Binding chain (engine facts, all verified against decompiled sources):
///  - ModManager.TryLoadMod loads a mod's DLL, then mounts its PCK, then calls its
///    initializer, sequentially per mod (engine ModManager.cs:793-851, sts2-spire1
///    research\engine-dllsrc). AppDomain.AssemblyLoad therefore fires BEFORE the RegentFX
///    PCK is mounted and BEFORE RegentFX.Entry.Init runs. Entry.Init is the ONLY
///    LoadScenes call site (RegentEntry.cs decompile).
///  - RegentFX.Entry.Init order: FMOD banks -> its own Harmony PatchAll -> script
///    registration -> RitsuLib config defaults -> if (Setting.PreloadEffects) LoadScenes()
///    (docs\performance-evidence\2026-09-15\decompiled\RegentEntry.cs). Only a LoadScenes
///    PREFIX EXECUTION has every prerequisite established. If PreloadEffects is false,
///    LoadScenes is never called, the prefix never fires, and this mod schedules NO
///    warm-up: no interception means no replacement work, preserving the native
///    disabled-preload policy.
///  - NGame._EnterTree assigns the static NGame.Instance BEFORE GameStartup ->
///    OneTimeInitialization.ExecuteVeryEarly -> ModManager.Initialize runs mod
///    initializers (engine NGame.cs:527-557). The live NGame is therefore already
///    constructed when any mod initializer or LoadScenes prefix executes. No NGame
///    constructor postfix exists here (the old one could only miss the already-existing
///    instance; removed).
///  - Effects missing from the cache fall back through RegentFX's own lazy consumer path:
///    VFXUtil.GenVFXNode -> PreloadManager.Cache.GetScene (RegentVFXUtil.cs:164-180).
///
/// State machine. Spine flags are committed only AFTER their operation succeeded:
///   Dormant -> Armed       AssemblyLoad subscription installed (early order possible).
///   Armed   -> Bound       RegentFX assembly appeared; LoadScenes binding installed;
///                          AssemblyLoad handler unsubscribed (terminal binding outcome).
///   Bound   -> Intercepted LoadScenes prefix actually entered (PCK + script + config done).
///   Intercepted -> Queued  typed ModSceneCache + validated collector resolved; paths
///                          queued; original preload suppressed (only when ALL
///                          prerequisites resolved; otherwise it is left enabled).
///   Queued  -> Attached    warmer node actually entered the live NGame's tree.
///   Attached -> Completed  queue drained; per-path outcomes reported.
/// Order provenance is tracked separately and distinctly: Early (bound from AssemblyLoad),
/// Unknown (RegentFX assembly already loaded at our initializer; whether its initializer
/// ran is not observable, so the binding is installed but may never fire), Late (definitive:
/// the synchronous preload already populated the cache this launch). Terminal outcome
/// markers: Unsupported (required members missing; native behavior untouched) and Failed
/// (a committed stage failed; the original preload is left enabled, or after suppression
/// the lazy first-consumer path serves the effects). A cache COUNT is never treated as
/// evidence that any load succeeded.
///
/// Lifecycle contract of every hook/subscription kept or added:
///  - AppDomain.AssemblyLoad handler: producer = CLR assembly loading; owner = this mod;
///    first consumer = OnAssemblyLoad; cleanup = unsubscribed when a terminal binding
///    outcome is reached (Bound or Unsupported), or at process exit if RegentFX never
///    loads. Exactly one handler (Initialize is latched; no accumulation).
///  - Harmony prefix on RegentFX.Scripts.Entry.LoadScenes: producer = this mod at bind
///    time; owner = this mod; first consumer = RegentFX.Entry.Init calling LoadScenes;
///    cleanup = none (single patch for the process lifetime, idempotent install, harmless
///    if it never fires; unpatching would not restore anything that matters).
///  - SceneTree.ProcessFrame retry handler (only when NGame.Instance is unexpectedly null
///    at prefix time): producer = engine main loop; owner = this mod; first consumer =
///    RetryAttachOnProcessFrame; cleanup = unsubscribed on attach submission or after a
///    bounded 900-frame give-up (then reported Failed). Never subscribed twice.
///  - Warmer node (this class instance, named RegentFXFastBootWarmer): producer =
///    LoadScenesPrefix; owner = the live NGame (deferred add_child); first consumer = its
///    own _Process draining the queue; cleanup = QueueFree on Completed, or freed with
///    NGame at teardown. Exactly one node is ever created per launch (only on the
///    Queued transition with a non-empty queue).
/// </summary>
[ModInitializer(nameof(Initialize))]
public partial class MainFile : Node
{
    public const string ModId = "RegentFXFastBoot";

    public static MegaCrit.Sts2.Core.Logging.Logger Log { get; } = new(ModId, LogType.Generic);

    private const string EntryTypeName = "RegentFX.Scripts.Entry";
    private const string LoadScenesName = "LoadScenes";
    private const string CacheFieldName = "ModSceneCache";
    private const string CollectorName = "CollectAssetPathsSafely";
    private const string WarmerNodeName = "RegentFXFastBootWarmer";
    private const int AttachRetryFrameLimit = 900;

    /// <summary>Spine of the warm-up lifecycle; every value is committed after success.</summary>
    private enum BindPhase
    {
        Dormant = 0,
        Armed = 1,
        Bound = 2,
        Intercepted = 3,
        Queued = 4,
        Attached = 5,
        Completed = 6
    }

    /// <summary>How this launch's load order relates to RegentFX, tracked distinctly.</summary>
    private enum OrderProvenance
    {
        Undetermined = 0,
        Early = 1,
        Unknown = 2,
        Late = 3
    }

    /// <summary>Terminal outcome markers, distinct from the spine and from provenance.</summary>
    private enum TerminalOutcome
    {
        None = 0,
        Unsupported = 1,
        Failed = 2
    }

    private static BindPhase _phase;
    private static OrderProvenance _provenance;
    private static TerminalOutcome _outcome;
    private static bool _initializeLatch;

    private static Type? _entryType;

    // Bound warm-up resources. Resolved exactly once, at the Intercepted point (the
    // LoadScenes prefix), never inside AssemblyLoad where PCK/script/config prerequisites
    // are not established yet.
    private static object? _modSceneCache;   // runtime ConcurrentDictionary<string, PackedScene>
    private static MethodInfo? _cacheTryAdd; // bool TryAdd(string, PackedScene) on the cache
    private static MethodInfo? _collector;   // static List<string> CollectAssetPathsSafely()

    private static readonly List<string> Pending = new();
    private static readonly List<string> FailedPaths = new();
    private static int _warmed;
    private static int _alreadyCached;

    // Bounded NGame.Instance retry state (see class doc, ProcessFrame entry).
    private static SceneTree? _retryTree;
    private static int _retryFrames;
    private static bool _attachSubmitted;

    public static void Initialize()
    {
        // Idempotent install: repeated initialization must not add handlers, nodes or
        // patches (RFX-1 requirement 7).
        if (_initializeLatch)
        {
            Log.Warn($"{ModId} Initialize called again; ignored (idempotent install)");
            return;
        }
        _initializeLatch = true;
        try
        {
            Type? entryType = FindEntryType(AppDomain.CurrentDomain.GetAssemblies());
            if (entryType != null)
            {
                // The RegentFX assembly is already in the AppDomain. ModManager.TryLoadMod
                // loads a mod's DLL and calls its initializer inside one sequential call
                // (ModManager.cs:793-851), so an already-loaded assembly usually means the
                // initializer already ran. It could also mean a third party loaded
                // RegentFX.dll before RegentFX's own TryLoadMod; that cannot be observed
                // here, so order is Late only when the cache proves the preload ran.
                int cachedCount = TryCountModSceneCache(entryType);
                if (cachedCount > 0)
                {
                    // Definitive Late order: only Entry.LoadScenes populates the cache this
                    // early in the launch (no combat consumer can have run yet), so the
                    // synchronous preload ALREADY happened and cannot be undone. The count
                    // itself is NOT evidence of what loaded successfully.
                    _provenance = OrderProvenance.Late;
                    bool? preloadSetting = TryReadPreloadEffectsSetting(entryType.Assembly);
                    Log.Warn(
                        "LATE-ORDER: RegentFX initialized before this mod. RegentFXFastBoot must sit ABOVE RegentFX in the game's mod list " +
                        "(the loader honors that manual order, ModManager.SortModList). This launch cannot be intercepted: the synchronous " +
                        "preload (if one ran) already happened and cannot be undone. No suppression and no warm-up will be scheduled; " +
                        $"native RegentFX behavior is untouched. Observations (not proof of success): ModSceneCache.Count={cachedCount}, " +
                        $"RegentFX PreloadEffects setting reads {(preloadSetting.HasValue ? preloadSetting.Value.ToString() : "unknown")}.");
                    return;
                }

                // UNKNOWN order: the assembly is present but we cannot prove whether its
                // initializer ran. Installing the binding is safe either way: it only takes
                // effect if LoadScenes is actually invoked later this launch. If the
                // initializer already ran (typical late order with PreloadEffects=false),
                // the prefix never fires and native behavior is untouched. No warm-up is
                // ever scheduled without an actual interception.
                BindLoadScenes(entryType, OrderProvenance.Unknown);
                return;
            }

            // Early order candidate: RegentFX is not loaded yet. Subscribe to AssemblyLoad.
            // The handler only discovers the Entry type and installs the LoadScenes binding;
            // it never touches Godot objects, effect classes or asset paths (RFX-1 req 2).
            AppDomain.CurrentDomain.AssemblyLoad += OnAssemblyLoad;
            _phase = BindPhase.Armed;
            _provenance = OrderProvenance.Early;
            Log.Info(
                "ARMED: watching assembly loads for RegentFX. ORDER REQUIREMENT: RegentFXFastBoot must load BEFORE RegentFX " +
                "(place it above RegentFX in the mod list). If RegentFX was already initialized this launch, the launch is LATE " +
                "and nothing will be suppressed or warmed.");
        }
        catch (Exception e)
        {
            CommitFailed($"Initialize failed: {e}");
        }
    }

    private static void OnAssemblyLoad(object? sender, AssemblyLoadEventArgs args)
    {
        // Phase check first: cheap, and avoids allocating the single-item array the old
        // implementation allocated on every unrelated assembly load (32 bytes/call per the
        // 2026-09-15 probe). All exceptions are contained: one escaping from this handler
        // would fail the mod that triggered the assembly load.
        try
        {
            if (_phase != BindPhase.Armed)
                return;
            Assembly loaded = args.LoadedAssembly;
            string? name = loaded.GetName().Name;
            if (name == null || name.IndexOf("RegentFX", StringComparison.OrdinalIgnoreCase) < 0)
                return;

            Type? entryType = loaded.GetType(EntryTypeName);
            if (entryType == null)
            {
                // Non-terminal: a RegentFX-named assembly without the Entry type is not the
                // mod we bind to; the real assembly may still load. Stay Armed.
                Log.Info($"Assembly '{name}' matches the RegentFX name but has no {EntryTypeName}; staying Armed");
                return;
            }

            BindLoadScenes(entryType, OrderProvenance.Early);
        }
        catch (Exception e)
        {
            CommitFailed($"AssemblyLoad handler error: {e}");
        }
        finally
        {
            // Terminal binding outcome reached (Bound, Unsupported or Failed): unsubscribe.
            // If still Armed (non-terminal), the subscription persists by design. A terminal
            // OUTCOME (Unsupported/Failed) is also terminal for the subscription even when
            // the spine phase never advanced past Armed - CommitUnsupported/CommitFailed do
            // not touch _phase by design, so _outcome is the terminal signal here.
            if (_phase != BindPhase.Armed || _outcome != TerminalOutcome.None)
                AppDomain.CurrentDomain.AssemblyLoad -= OnAssemblyLoad;
        }
    }

    /// <summary>
    /// Installs the LoadScenes binding ONLY (a method patch). Never collects asset paths,
    /// never instantiates effect classes, never touches Godot scene objects: at bind time
    /// the RegentFX PCK may not be mounted and its initializer has not run.
    /// </summary>
    private static void BindLoadScenes(Type entryType, OrderProvenance provenance)
    {
        if (_phase != BindPhase.Dormant && _phase != BindPhase.Armed)
            return; // already bound or terminal; never double-patch
        try
        {
            MethodInfo? loadScenes = AccessTools.Method(entryType, LoadScenesName);
            if (loadScenes == null)
            {
                CommitUnsupported(
                    $"RegentFX found but {EntryTypeName}.{LoadScenesName} is missing (RegentFX version changed?). " +
                    "No suppression, no warm-up, native behavior untouched.");
                return;
            }

            var harmony = new Harmony(ModId);
            var prefix = new HarmonyMethod(typeof(MainFile).GetMethod(
                nameof(LoadScenesPrefix), BindingFlags.Static | BindingFlags.NonPublic));
            harmony.Patch(loadScenes, prefix: prefix);
            _entryType = entryType;
            _phase = BindPhase.Bound;
            _provenance = provenance;
            Log.Info(
                "BOUND: skip binding installed on RegentFX.Scripts.Entry.LoadScenes. " +
                "INTERCEPTED will be logged only if Entry.Init actually calls LoadScenes this launch " +
                "(it does so only when RegentFX's PreloadEffects setting is true). " +
                "No warm-up is scheduled by this step alone.");
        }
        catch (Exception e)
        {
            CommitFailed($"binding RegentFX.Scripts.Entry.LoadScenes failed: {e}");
        }
    }

    /// <summary>
    /// Prefix on RegentFX.Scripts.Entry.LoadScenes. Producer: Entry.Init (the only call
    /// site); owner: this mod; first consumer: this method; cleanup: none (state machine).
    /// Returns false (suppress the original) ONLY after every warm-up prerequisite resolved
    /// and the queue was filled. Any prerequisite failure returns true so the ORIGINAL
    /// preload runs (RFX-1 requirement 5). Never throws into RegentFX's initializer.
    /// </summary>
    private static bool LoadScenesPrefix()
    {
        try
        {
            if (_phase >= BindPhase.Queued)
            {
                Log.Info("LoadScenes entered again after a queued warm-up; suppressing again (no re-queue, no new work)");
                return false;
            }
            if (_phase == BindPhase.Intercepted)
            {
                // A previous entry failed prerequisites (phase stuck at Intercepted, no queue):
                // never suppress without established prerequisites - let the ORIGINAL run.
                Log.Warn("LoadScenes re-entered after a prerequisite failure; running the original preload");
                return true;
            }

            _phase = BindPhase.Intercepted;
            Log.Info(
                "INTERCEPTED: RegentFX.Entry.LoadScenes entered. At this point RegentFX's initializer has run " +
                "(PCK mounted, scripts registered, RitsuLib defaults set), per engine TryLoadMod order: assembly -> PCK -> initializer.");

            // Resolve the exact typed cache and a validated collector reference ONCE, here.
            if (!TryResolveWarmUpPrerequisites(out string problem))
            {
                CommitFailed($"prerequisites unusable, original preload LEFT ENABLED: {problem}");
                return true;
            }

            MethodInfo? collector = _collector;
            if (collector == null)
            {
                CommitFailed("collector reference lost, original preload LEFT ENABLED");
                return true;
            }
            IEnumerable<string>? paths;
            try
            {
                paths = collector.Invoke(null, null) as IEnumerable<string>;
            }
            catch (Exception e)
            {
                CommitFailed($"collector invocation failed, original preload LEFT ENABLED: {e.Message}");
                return true;
            }
            if (paths == null)
            {
                CommitFailed("collector returned null, original preload LEFT ENABLED");
                return true;
            }

            // Queue the same path list the original preload would have built. The cache is
            // expected to be empty here (the prefix runs before the original body); paths
            // already present are counted, not re-queued.
            var dict = (System.Collections.IDictionary)_modSceneCache!;
            int queuedNow = 0;
            int presentAlready = 0;
            foreach (string path in paths)
            {
                if (string.IsNullOrEmpty(path))
                    continue;
                if (dict.Contains(path))
                {
                    presentAlready++;
                    continue;
                }
                Pending.Add(path);
                queuedNow++;
            }

            _phase = BindPhase.Queued;
            if (queuedNow == 0)
            {
                // The original LoadScenes no-ops on an empty list (decompile: early return
                // when list.Count <= 0), so suppression is exactly equivalent.
                _phase = BindPhase.Completed;
                Log.Info(
                    $"COMPLETED (zero work): queued 0 new paths ({presentAlready} already cached). " +
                    "Original preload suppressed; it would have been a no-op. No warmer node created.");
                return false;
            }

            Log.Info($"QUEUED: {queuedNow} scene paths for one-per-frame idle warm-up; original preload suppressed.");
            TryAttachWarmer();
            return false;
        }
        catch (Exception e)
        {
            // Never break RegentFX's initializer: fall back to the original preload.
            CommitFailed($"LoadScenes prefix error, original preload LEFT ENABLED: {e}");
            return true;
        }
    }

    /// <summary>
    /// Resolves and validates, once: the ModSceneCache instance (runtime type must be
    /// ConcurrentDictionary of string -> PackedScene, per the RegentEntry decompile), its
    /// two-argument TryAdd, and the parameterless static string-enumerable collector.
    /// Everything resolved here is used by the warmer node later.
    /// </summary>
    private static bool TryResolveWarmUpPrerequisites(out string problem)
    {
        problem = "";
        Type? entryType = _entryType;
        if (entryType == null)
        {
            problem = "entry type not captured at bind time";
            return false;
        }
        object? cache = AccessTools.Field(entryType, CacheFieldName)?.GetValue(null);
        if (cache == null)
        {
            problem = $"{CacheFieldName} missing";
            return false;
        }
        Type cacheType = cache.GetType();
        Type[] genericArgs = cacheType.GetGenericArguments();
        if (genericArgs.Length != 2 || genericArgs[0] != typeof(string) || genericArgs[1] != typeof(PackedScene))
        {
            problem = $"{CacheFieldName} runtime type {cacheType} is not a dictionary of string -> PackedScene";
            return false;
        }
        MethodInfo? tryAdd = null;
        foreach (MethodInfo candidate in cacheType.GetMethods())
        {
            if (!candidate.Name.Equals("TryAdd", StringComparison.Ordinal))
                continue;
            ParameterInfo[] parameters = candidate.GetParameters();
            if (parameters.Length == 2
                && parameters[0].ParameterType == typeof(string)
                && parameters[1].ParameterType == typeof(PackedScene))
            {
                tryAdd = candidate;
                break;
            }
        }
        if (tryAdd == null)
        {
            problem = "TryAdd(string, PackedScene) not found on the cache";
            return false;
        }
        MethodInfo? collector = AccessTools.Method(entryType, CollectorName);
        if (collector == null)
        {
            problem = $"{CollectorName} missing";
            return false;
        }
        if (!collector.IsStatic || collector.GetParameters().Length != 0)
        {
            problem = $"{CollectorName} signature changed (expected static parameterless)";
            return false;
        }
        if (!typeof(IEnumerable<string>).IsAssignableFrom(collector.ReturnType))
        {
            problem = $"{CollectorName} return type changed (expected IEnumerable of string)";
            return false;
        }
        _modSceneCache = cache;
        _cacheTryAdd = tryAdd;
        _collector = collector;
        return true;
    }

    /// <summary>
    /// Attaches the warmer to the EXISTING NGame. Mechanism (documented choice): read the
    /// static NGame.Instance property - NGame._EnterTree assigns it before GameStartup runs
    /// mod initialization (engine NGame.cs:527-557), so the instance is guaranteed live at
    /// prefix time without constructing or patching NGame. The add_child is submitted via
    /// CallDeferred, which is valid whether the prefix runs inside NGame._EnterTree's call
    /// stack or in a post-_EnterTree continuation of the async GameStartup, and is
    /// main-thread-safe by engine contract. This mod does not wait for menu readiness
    /// before draining: the drain yields every frame, and RFX-2 owns any scheduling change.
    /// </summary>
    private static bool TryAttachWarmer()
    {
        if (_attachSubmitted)
            return true;
        MegaCrit.Sts2.Core.Nodes.NGame? nGame = MegaCrit.Sts2.Core.Nodes.NGame.Instance;
        if (nGame != null)
        {
            SubmitDeferredAttach(nGame);
            return true;
        }

        // Unexpected (defensive): NGame.Instance not assigned at prefix time. Retry per
        // frame on the main loop with a bounded give-up; the handler unsubscribes itself.
        if (Engine.GetMainLoop() is SceneTree tree)
        {
            _retryTree = tree;
            _retryFrames = 0;
            tree.ProcessFrame += RetryAttachOnProcessFrame;
            Log.Warn("NGame.Instance was null at interception time; retrying attachment once per frame (bounded)");
            return true;
        }
        CommitFailed(
            "attachment failed: no live NGame and no SceneTree main loop. FAILED warm-up: the queue never drains; " +
            "RegentFX's lazy first-consumer path (VFXUtil.GenVFXNode -> PreloadManager.Cache.GetScene) serves all effects.");
        return false;
    }

    private static void SubmitDeferredAttach(MegaCrit.Sts2.Core.Nodes.NGame nGame)
    {
        var warmer = new MainFile();
        warmer.Name = WarmerNodeName;
        nGame.CallDeferred("add_child", warmer);
        _attachSubmitted = true;
        Log.Info(
            $"ATTACH submitted: warmer node handed to the LIVE NGame via deferred add_child; queue={Pending.Count}. " +
            "ATTACHED is committed only when the node actually enters the tree.");
    }

    private static void RetryAttachOnProcessFrame()
    {
        try
        {
            _retryFrames++;
            MegaCrit.Sts2.Core.Nodes.NGame? nGame = MegaCrit.Sts2.Core.Nodes.NGame.Instance;
            if (nGame != null)
            {
                DetachRetryHandler();
                SubmitDeferredAttach(nGame);
                return;
            }
            if (_retryFrames >= AttachRetryFrameLimit)
            {
                DetachRetryHandler();
                CommitFailed(
                    $"attachment failed: NGame.Instance still null after {AttachRetryFrameLimit} frames. FAILED warm-up: the queue " +
                    "never drains; RegentFX's lazy first-consumer path serves all effects.");
            }
        }
        catch (Exception e)
        {
            DetachRetryHandler();
            CommitFailed($"attachment retry error: {e}");
        }
    }

    private static void DetachRetryHandler()
    {
        SceneTree? tree = _retryTree;
        if (tree != null)
        {
            tree.ProcessFrame -= RetryAttachOnProcessFrame;
            _retryTree = null;
        }
    }

    // ----------------------- warmer node (instance members) -----------------------

    public override void _EnterTree()
    {
        // Entering the tree is the proof of attachment: commit Attached here, after the
        // operation succeeded. The only MainFile instance that ever enters a tree is the
        // warmer created by LoadScenesPrefix (the mod initializer is a static method call).
        base._EnterTree();
        if (_phase == BindPhase.Queued)
        {
            _phase = BindPhase.Attached;
            Log.Info($"ATTACHED: warmer node entered the live NGame's tree (parent '{GetParent()?.Name}'); queue={Pending.Count}");
        }
    }

    public override void _Process(double delta)
    {
        // Producer: Godot main loop; owner: the live NGame (parent); first consumer: this
        // method; cleanup: QueueFree on completion, or freed with NGame at teardown.
        if (_phase == BindPhase.Completed)
        {
            QueueFree();
            return;
        }
        if (_phase != BindPhase.Attached)
            return;

        if (Pending.Count == 0)
        {
            // The last item was processed in a previous frame; the drain finished.
            _phase = BindPhase.Completed;
            Log.Info($"COMPLETED: warm-up queue drained. warmed={_warmed}, alreadyCached={_alreadyCached}, failed={FailedPaths.Count}.");
            if (FailedPaths.Count > 0)
            {
                // Requirement 5: failures after suppression are reported as FAILED warm-up;
                // the lazy first-consumer path is preserved. Cache counts are never cited
                // as evidence of success - the per-path outcomes above are the evidence.
                Log.Warn(
                    $"FAILED warm-up for {FailedPaths.Count} path(s): {string.Join(", ", FailedPaths)}. Those effects were NOT " +
                    "pre-warmed; RegentFX's lazy first-consumer path (VFXUtil.GenVFXNode -> PreloadManager.Cache.GetScene) serves " +
                    "them at first use.");
            }
            QueueFree();
            return;
        }

        // RFX-2 owns the loading strategy; this intentionally keeps the existing one
        // synchronous ResourceLoader.Load per frame. One item per frame bounds item count,
        // not the duration of a heavy item - that is RFX-2's concern, not changed here.
        string path = Pending[0];
        Pending.RemoveAt(0);
        try
        {
            PackedScene? scene = ResourceLoader.Load<PackedScene>(path, null, ResourceLoader.CacheMode.Reuse);
            if (scene == null)
            {
                FailedPaths.Add(path);
                Log.Warn($"Warm-up load returned null: {path}");
                return;
            }
            bool added = false;
            if (_modSceneCache != null && _cacheTryAdd != null)
                added = _cacheTryAdd.Invoke(_modSceneCache, new object?[] { path, scene }) is true;
            if (added)
                _warmed++;
            else
                _alreadyCached++; // a lazy consumer populated this path before we did
        }
        catch (Exception e)
        {
            FailedPaths.Add(path);
            Log.Warn($"Warm-up load failed for {path}: {e.Message}");
        }
    }

    // ----------------------------- helpers -----------------------------

    private static Type? FindEntryType(IEnumerable<Assembly> assemblies)
    {
        foreach (Assembly assembly in assemblies)
        {
            string? name = assembly.GetName().Name;
            if (name == null || name.IndexOf("RegentFX", StringComparison.OrdinalIgnoreCase) < 0)
                continue;
            Type? entryType = assembly.GetType(EntryTypeName);
            if (entryType != null)
                return entryType;
        }
        return null;
    }

    /// <summary>
    /// Reads ModSceneCache.Count without invoking anything else. Reading the static field
    /// triggers Entry's static initializer, which (per the decompile) only allocates the
    /// ConcurrentDictionary - no PCK, Godot or resource access. Returns -1 when unknown.
    /// The count is diagnostic only and is never treated as evidence of load success.
    /// </summary>
    private static int TryCountModSceneCache(Type entryType)
    {
        try
        {
            if (AccessTools.Field(entryType, CacheFieldName)?.GetValue(null) is System.Collections.IDictionary dict)
                return dict.Count;
        }
        catch
        {
            // fall through to unknown
        }
        return -1;
    }

    /// <summary>
    /// Best-effort read of RegentFX's PreloadEffects setting for LATE-order reporting only.
    /// FLAG: the Setting type name is UNCONFIRMED - no Setting decompile is retained in the
    /// workspace; the member is known only from the Entry.Init decompile
    /// (Setting.PreloadEffects). Any failure returns null and is reported as unknown.
    /// </summary>
    private static bool? TryReadPreloadEffectsSetting(Assembly regentAssembly)
    {
        try
        {
            Type? settingType = regentAssembly.GetType("RegentFX.Scripts.Setting") ?? regentAssembly.GetType("Setting");
            if (settingType == null)
                return null;
            FieldInfo? field = settingType.GetField("PreloadEffects", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (field?.GetValue(null) is bool fieldValue)
                return fieldValue;
            PropertyInfo? property = settingType.GetProperty("PreloadEffects", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (property?.GetValue(null) is bool propertyValue)
                return propertyValue;
        }
        catch
        {
            // fall through to unknown
        }
        return null;
    }

    private static void CommitFailed(string detail)
    {
        _outcome = TerminalOutcome.Failed;
        Log.Error($"FAILED (phase={_phase}, provenance={_provenance}): {detail}");
    }

    private static void CommitUnsupported(string detail)
    {
        _outcome = TerminalOutcome.Unsupported;
        Log.Warn($"UNSUPPORTED (phase={_phase}, provenance={_provenance}): {detail}");
    }
}
