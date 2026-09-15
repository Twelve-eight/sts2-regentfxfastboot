using System;

using Godot;

namespace RegentFXFastBoot.RegentFXFastBootCode;

/// <summary>
/// Watches for the main menu and shows a one-shot notice there (RFX-3 late-order notice,
/// 2026-09-16; RFX-4 success notice, 2026-09-17).
///
/// Two independent uses, never both in one launch:
///   - LateOrder: scheduled when this launch is DEFINITIVELY late order (RegentFX's scene
///     cache was already populated at initializer time, so the synchronous preload provably
///     ran and cannot be undone). Never scheduled for the Unknown or Early cases: those may
///     still succeed, and a failure popup would then be wrong.
///   - Succeeded: scheduled when the warm-up actually ran and completed with no failures, so
///     the player learns the mod did something rather than silently changing boot behaviour.
///
/// Lifecycle: producer = MainFile (Schedule); owner = this mod; first consumer = _Process;
/// cleanup = QueueFree on every terminal path (shown, gave up, no main menu). It is attached
/// to the scene tree ROOT rather than to NGame so that it survives whatever the engine does
/// with the game node, and it is freed as soon as its single job is done - it never becomes a
/// permanent per-frame cost.
///
/// Bounded on every axis: a frame budget for the menu to appear and a small retry budget for
/// the modal container to be free. A notice that cannot be shown is reported, never retried
/// forever, and never recorded as shown.
/// </summary>
internal sealed partial class ModNoticeWatcher : Node
{
    private const string WatcherNodeName = "RegentFXFastBoot_NoticeWatcher";

    /// <summary>Frames to wait for the main menu after this node is created.</summary>
    private const int MenuWaitFrameLimit = 3600;   // ~60s at 60fps

    /// <summary>Extra frames after the menu appears, so the engine's own startup UI settles first.</summary>
    private const int SettleFrames = 30;

    /// <summary>Attempts to claim the engine's single modal slot.</summary>
    private const int ShowAttemptLimit = 120;

    /// <summary>Frames between modal-slot attempts (the slot is often taken for a frame or two).</summary>
    private const int ShowRetryInterval = 30;

    private NoticeKind _kind;
    private int _warmed;
    private int _frames;
    private int _settleFrames;
    private int _attempts;
    private int _framesSinceAttempt;
    private bool _menuSeen;

    /// <summary>
    /// Attaches a watcher when the notice still needs to be shown. Every failure path is
    /// contained: the notice is a convenience, and the game must never be affected by it.
    /// </summary>
    internal static void Schedule(NoticeKind kind, int warmed = 0)
    {
        try
        {
            if (NoticeState.IsShown(kind))
            {
                MainFile.Log.Info($"{PrefixFor(kind)}: popup already shown in an earlier launch; not scheduled again");
                return;
            }

            var mainLoop = Engine.GetMainLoop() as SceneTree;
            if (mainLoop?.Root == null)
            {
                MainFile.Log.Warn($"{PrefixFor(kind)}: no SceneTree root available; the popup is not scheduled this launch");
                return;
            }

            var watcher = new ModNoticeWatcher
            {
                Name = WatcherNodeName,
                _kind = kind,
                _warmed = warmed,
                ProcessMode = ProcessModeEnum.Always
            };
            // Deferred, matching the warmer's attachment path in MainFile: this runs from a
            // mod initializer inside NGame's _EnterTree, so the tree is mid-traversal and a
            // direct AddChild is not safe.
            mainLoop.Root.CallDeferred("add_child", watcher);
            MainFile.Log.Info(
                $"{PrefixFor(kind)}: popup scheduled; it will appear once the main menu is up (this launch only)");
        }
        catch (Exception e)
        {
            MainFile.Log.Warn($"{PrefixFor(kind)}: scheduling the popup failed ({e.GetType().Name}: {e.Message}); the game is unaffected");
        }
    }

    public override void _Process(double delta)
    {
        try
        {
            // The frame budget applies ONLY to waiting for the menu. It used to be charged on
            // every frame of the node's life, which broke the success path: that watcher is
            // created once the warm-up has already finished, i.e. with the menu already up, so
            // the counter reached the limit at exactly the frame the last modal-slot attempt
            // would have run. The attempt limit was then unreachable and the drop line reported
            // "the main menu did not appear within N frames" about a menu that had been up for a
            // minute - a false explanation, and the failure mode most likely on a slow boot
            // (exactly the machine this mod exists for).
            if (!_menuSeen && ++_frames > MenuWaitFrameLimit)
            {
                MainFile.Log.Warn(
                    $"{PrefixFor(_kind)}: the main menu did not appear within {MenuWaitFrameLimit} frames; the popup is dropped " +
                    "for this launch and will be offered again next launch");
                QueueFree();
                return;
            }

            MegaCrit.Sts2.Core.Nodes.NGame? nGame = MegaCrit.Sts2.Core.Nodes.NGame.Instance;
            if (nGame == null || !GodotObject.IsInstanceValid(nGame) || nGame.MainMenu == null)
                return;

            if (!_menuSeen)
            {
                _menuSeen = true;
                MainFile.Log.Info($"{PrefixFor(_kind)}: the main menu is up; waiting for it to settle before showing the popup");
            }

            // Let the menu finish its own entry animation and any engine modal it raises.
            if (++_settleFrames < SettleFrames)
                return;

            // One attempt per interval: the modal slot is single and may be transiently held.
            if (_attempts > 0 && ++_framesSinceAttempt < ShowRetryInterval)
                return;
            _framesSinceAttempt = 0;

            // The post-menu bound is this attempt limit, and it is what actually runs: the
            // counter above is frozen once the menu is seen. 120 attempts x 30 frames is the
            // same ~60s at 60fps.
            if (_attempts >= ShowAttemptLimit)
            {
                MainFile.Log.Warn(
                    $"{PrefixFor(_kind)}: the engine's modal slot stayed busy for {ShowAttemptLimit} attempts; the popup is dropped " +
                    "for this launch and will be offered again next launch");
                QueueFree();
                return;
            }
            _attempts++;

            if (ModNotice.TryShow(_kind, _warmed))
                QueueFree();
        }
        catch (Exception e)
        {
            MainFile.Log.Warn($"{PrefixFor(_kind)}: the popup watcher failed ({e.GetType().Name}: {e.Message}); dropping it for this launch");
            QueueFree();
        }
    }

    /// <summary>Per-kind log prefix, so the acceptance script can scope each notice's assertions.</summary>
    private static string PrefixFor(NoticeKind kind) =>
        kind == NoticeKind.LateOrder ? "NOTICE" : "NOTICE-SUCCESS";
}
