using System;

using Godot;

namespace RegentFXFastBoot.RegentFXFastBootCode;

/// <summary>
/// Watches for the main menu and shows the late-order notice once (RFX-3, 2026-09-16).
///
/// Only created when this launch is DEFINITIVELY late order (RegentFX's scene cache was
/// already populated when this mod initialized, so the synchronous preload provably ran and
/// cannot be undone). It is never created for the Unknown or Early cases: those may still
/// succeed, and a popup would then be wrong.
///
/// Lifecycle: producer = MainFile.Initialize; owner = this mod; first consumer = _Process;
/// cleanup = QueueFree on every terminal path (shown, gave up, no main menu). It is attached
/// to the scene tree ROOT rather than to NGame so that it survives whatever the engine does
/// with the game node, and it is freed as soon as its single job is done - it never becomes a
/// permanent per-frame cost.
///
/// Bounded on every axis: a frame budget for the menu to appear and a small retry budget for
/// the modal container to be free. A popup that cannot be shown is reported, never retried
/// forever, and never recorded as shown.
/// </summary>
internal sealed partial class LateOrderNoticeWatcher : Node
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

    private int _frames;
    private int _settleFrames;
    private int _attempts;
    private int _framesSinceAttempt;
    private bool _menuSeen;

    /// <summary>
    /// Attaches the watcher when the notice still needs to be shown. Every failure path is
    /// contained: the popup is a convenience, and the game must never be affected by it.
    /// </summary>
    internal static void Schedule()
    {
        try
        {
            if (NoticeState.IsNoticeShown())
            {
                MainFile.Log.Info("NOTICE: late-order popup already shown in an earlier launch; not scheduled again");
                return;
            }

            var mainLoop = Engine.GetMainLoop() as SceneTree;
            if (mainLoop?.Root == null)
            {
                MainFile.Log.Warn("NOTICE: no SceneTree root available; the late-order popup is not scheduled this launch");
                return;
            }

            var watcher = new LateOrderNoticeWatcher
            {
                Name = WatcherNodeName,
                ProcessMode = ProcessModeEnum.Always
            };
            // Deferred, matching the warmer's attachment path in MainFile: this runs from a
            // mod initializer inside NGame's _EnterTree, so the tree is mid-traversal and a
            // direct AddChild is not safe.
            mainLoop.Root.CallDeferred("add_child", watcher);
            MainFile.Log.Info("NOTICE: late-order popup scheduled; it will appear once the main menu is up (this launch only)");
        }
        catch (Exception e)
        {
            MainFile.Log.Warn($"NOTICE: scheduling the late-order popup failed ({e.GetType().Name}: {e.Message}); the game is unaffected");
        }
    }

    public override void _Process(double delta)
    {
        try
        {
            if (++_frames > MenuWaitFrameLimit)
            {
                MainFile.Log.Warn(
                    $"NOTICE: the main menu did not appear within {MenuWaitFrameLimit} frames; the late-order popup is dropped " +
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
                MainFile.Log.Info("NOTICE: the main menu is up; waiting for it to settle before showing the late-order popup");
            }

            // Let the menu finish its own entry animation and any engine modal it raises.
            if (++_settleFrames < SettleFrames)
                return;

            // One attempt per interval: the modal slot is single and may be transiently held.
            if (_attempts > 0 && ++_framesSinceAttempt < ShowRetryInterval)
                return;
            _framesSinceAttempt = 0;

            if (++_attempts > ShowAttemptLimit)
            {
                MainFile.Log.Warn(
                    $"NOTICE: the engine's modal slot stayed busy for {ShowAttemptLimit} attempts; the late-order popup is dropped " +
                    "for this launch and will be offered again next launch");
                QueueFree();
                return;
            }

            if (LateOrderNotice.TryShow())
                QueueFree();
        }
        catch (Exception e)
        {
            MainFile.Log.Warn($"NOTICE: the late-order popup watcher failed ({e.GetType().Name}: {e.Message}); dropping it for this launch");
            QueueFree();
        }
    }
}
