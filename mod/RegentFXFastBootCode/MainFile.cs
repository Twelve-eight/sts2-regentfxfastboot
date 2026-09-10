using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;

using Godot;
using HarmonyLib;

using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;

namespace RegentFXFastBoot.RegentFXFastBootCode;

/// <summary>
/// Fixes RegentFX's boot stall (measured 4.1s on the user's machine, 2026-09-11).
///
/// RegentFX v0.5.1 Entry.Init runs LoadScenes() synchronously inside its mod
/// initializer: ~32 PackedScene ResourceLoader.Load calls on the main thread while
/// the boot splash is still up - the single biggest contributor to the pulsed
/// black-screen freeze during startup.
///
/// This mod is load-order independent: it postfixes ModManager.TryLoadMod and
/// activates the moment RegentFX finishes loading (wherever it sits in the order).
/// It then:
///  1. Harmony-prefixes RegentFX.Scripts.Entry.LoadScenes (private static) and
///     skips it entirely. IMPORTANT: this must happen BEFORE RegentFX's own
///     initializer runs - inside TryLoadMod, the assembly is loaded but the
///     initializer is called AFTER (ModManager calls TryLoadMod which loads dll,
///     then initializer). Verifying that order in ModManager: TryLoadMod loads
///     the assembly AND calls the initializer in one method, so a TryLoadMod
///     prefix would be too late. Solution: patch AssemblyLoadContext-level? No -
///     we instead patch the LoadScenes method the moment the RegentFX ASSEMBLY
///     appears in AppDomain.AssemblyLoad, which fires during LoadFromAssemblyPath,
///     strictly before any initializer code runs.
///  2. Rebuilds the asset path list via reflection (CollectAssetPathsSafely) and
///     warms Entry.ModSceneCache (public static ConcurrentDictionary) ONE SCENE
///     PER FRAME from a Node attached after NGame is constructed - idle-time work
///     instead of boot work. Identical warm cache before the first combat, zero
///     boot cost. Effects are never broken either way: RegentFX's GenVFXNode
///     falls back to PreloadManager.Cache.GetScene on a cache miss.
///
/// No RegentFX installed -> stays dormant.
/// </summary>
[ModInitializer(nameof(Initialize))]
public partial class MainFile : Node
{
    public const string ModId = "RegentFXFastBoot";

    public static MegaCrit.Sts2.Core.Logging.Logger Log { get; } = new(ModId, LogType.Generic);

    private static readonly List<string> Pending = new();
    private static readonly List<string> Failed = new();
    private static object? _modSceneCache;
    private static MethodInfo? _cacheTryAdd;
    private static int _warmed;
    private static bool _activated;

    public static void Initialize()
    {
        try
        {
            // Fire on every subsequent assembly load; the RegentFX assembly load
            // event happens mid-TryLoadMod, before its initializer can run.
            AppDomain.CurrentDomain.AssemblyLoad += OnAssemblyLoad;

            // Also handle the case where RegentFX is already loaded (we sort after it).
            TryActivate(AppDomain.CurrentDomain.GetAssemblies());

            Log.Info($"{ModId} initialized (order-independent RegentFX boot-stall fix)");
        }
        catch (Exception e)
        {
            Log.Error($"Init failed: {e}");
        }
    }

    private static void OnAssemblyLoad(object? sender, AssemblyLoadEventArgs args)
        => TryActivate(new[] { args.LoadedAssembly });

    private static void TryActivate(Assembly[] assemblies)
    {
        if (_activated)
            return;
        Type? entryType = null;
        foreach (Assembly asm in assemblies)
        {
            // Fast name check before the (slow) GetType call.
            if (!asm.GetName().Name!.Contains("RegentFX"))
                continue;
            entryType = asm.GetType("RegentFX.Scripts.Entry");
            if (entryType != null)
                break;
        }
        if (entryType == null)
            return;
        _activated = true;

        try
        {
            var harmony = new Harmony(ModId);

            // 1) Neutralize the synchronous preload BEFORE it can ever run.
            MethodInfo? loadScenes = AccessTools.Method(entryType, "LoadScenes");
            if (loadScenes != null)
            {
                var skip = new HarmonyMethod(typeof(MainFile).GetMethod(nameof(SkipLoadScenes),
                    BindingFlags.Static | BindingFlags.NonPublic));
                harmony.Patch(loadScenes, prefix: skip);
                Log.Info("Patched RegentFX.Entry.LoadScenes -> will be skipped");
            }
            else
            {
                Log.Warn("RegentFX.Entry.LoadScenes not found (version changed?); only deferring warm-up");
            }

            // 2) Capture the cache + path list for the deferred warm-up. The path
            //    collector is static and side-effect free, safe to call now.
            FieldInfo? cacheField = AccessTools.Field(entryType, "ModSceneCache");
            _modSceneCache = cacheField?.GetValue(null);
            MethodInfo? collect = AccessTools.Method(entryType, "CollectAssetPathsSafely");
            if (_modSceneCache != null && collect != null)
            {
                // The cache is ConcurrentDictionary<string, PackedScene> at runtime;
                // use non-generic IDictionary for Contains and reflect TryAdd off
                // the ACTUAL runtime type (generic invariance rules out a typed cast).
                var dict = (System.Collections.IDictionary)_modSceneCache;
                if (collect.Invoke(null, null) is IEnumerable<string> paths)
                {
                    foreach (string p in paths)
                    {
                        if (!dict.Contains(p))
                            Pending.Add(p);
                    }
                }
                _cacheTryAdd = _modSceneCache.GetType().GetMethod("TryAdd");
                Log.Info($"Deferred warm-up queued: {Pending.Count} scenes");
            }
            else
            {
                Log.Warn("ModSceneCache/CollectAssetPathsSafely unavailable; RegentFX lazy-loads via PreloadManager (acceptable)");
            }

            // 3) Attach the per-frame warmer once NGame exists (constructor postfix).
            ConstructorInfo? nGameCtor = AccessTools.Constructor(typeof(MegaCrit.Sts2.Core.Nodes.NGame));
            if (nGameCtor != null)
            {
                var hook = new HarmonyMethod(typeof(MainFile).GetMethod(nameof(OnNGameConstructed),
                    BindingFlags.Static | BindingFlags.NonPublic));
                harmony.Patch(nGameCtor, postfix: hook);
            }
            else
            {
                Log.Warn("NGame constructor not found; warm-up will not auto-attach (acceptable)");
            }
        }
        catch (Exception e)
        {
            Log.Error($"Activation failed: {e}");
        }
    }

    private static bool SkipLoadScenes()
    {
        // Prefix returning false skips the original entirely.
        Log.Info("Skipping RegentFX synchronous preload (boot stall fix)");
        return false;
    }

    private static bool _warmStarted;

    private static void OnNGameConstructed(MegaCrit.Sts2.Core.Nodes.NGame __instance)
    {
        if (_warmStarted || Pending.Count == 0 || _modSceneCache == null)
            return;
        _warmStarted = true;
        try
        {
            var node = new MainFile();
            node.Name = "RegentFXFastBootWarmer";
            // We are inside NGame's constructor: schedule the add for the next idle frame.
            __instance.CallDeferred("add_child", node);
            Log.Info("Warm-up node attaching to NGame; one scene per idle frame");
        }
        catch (Exception e)
        {
            Log.Error($"Warm-up attach failed: {e.Message}");
        }
    }

    public override void _Process(double delta)
    {
        if (Pending.Count == 0)
        {
            if (_warmed > 0 || Failed.Count > 0)
            {
                Log.Info($"Warm-up complete: {_warmed} warmed, {Failed.Count} failed");
                _warmed = 0;
                Failed.Clear();
            }
            QueueFree();
            return;
        }
        string path = Pending[0];
        Pending.RemoveAt(0);
        try
        {
            var scene = ResourceLoader.Load<PackedScene>(path, null, ResourceLoader.CacheMode.Reuse);
            if (scene != null)
            {
                _cacheTryAdd?.Invoke(_modSceneCache, new object?[] { path, scene });
                _warmed++;
            }
            else
            {
                Failed.Add(path);
            }
        }
        catch (Exception)
        {
            Failed.Add(path);
        }
    }
}