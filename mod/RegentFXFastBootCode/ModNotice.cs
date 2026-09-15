using System;

using Godot;

using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Screens.ScreenContext;
using MegaCrit.Sts2.addons.mega_text;

namespace RegentFXFastBoot.RegentFXFastBootCode;

/// <summary>
/// Which one-shot notice is being shown. Both kinds share this modal, its hosting, its
/// failure policy and its one-shot bookkeeping; only the content and the button set differ.
/// Generalising rather than copying is deliberate: the close path carries a subtle
/// modal-container hazard (see CloseSelf), and a second copy of that logic would be a second
/// chance to get it wrong.
/// </summary>
internal enum NoticeKind
{
    /// <summary>
    /// Acceleration could not run this launch because RegentFX loaded first. Actionable:
    /// the player can fix the order with a load-order editor.
    /// </summary>
    LateOrder,

    /// <summary>
    /// Acceleration ran this launch: RegentFX's synchronous scene preload was replaced by the
    /// background warm-up. Informational, no action required.
    /// </summary>
    Succeeded,
}

/// <summary>
/// The mod's one-shot modal notices, shown on the main menu (RFX-3 late-order notice,
/// 2026-09-16; RFX-4 success notice, 2026-09-17).
///
/// Why this exists: the acceleration is otherwise silent in both directions. On failure the
/// only trace was a LATE-ORDER log line, so a subscriber had no way to learn that a
/// load-order editor is needed. On success there was no trace at all, so the player could not
/// tell whether the mod did anything. The mod now says which happened, where the player looks.
///
/// Hosting: the engine's own modal container, NModalContainer.Instance.Add(this). That path
/// gives the dimmed backstop, the input capture and the ActiveScreenContext registration for
/// free, and it is exactly how the engine shows its own mod-loading confirmation
/// (NModdingScreen.OnSubmenuOpened -> NModalContainer.Instance.Add(NConfirmModLoadingPopup.Create())).
/// Add() casts to IScreenContext, whose only member is DefaultFocusedControl - hence the one
/// property below.
///
/// Node shape: a full-rect Control carrying a ColorRect scrim and a centred PanelContainer,
/// mirroring the shape Load Order Manager uses in-game on this engine version (its own panel
/// is built the same way). Deliberately NOT the engine's vertical_popup scene: that scene is
/// not in the boot preload set (AssetSets), and reaching for it would pull a resource load
/// into the main menu for a popup this mod can draw itself. Theme property names come from the
/// engine's ThemeConstants rather than bare strings, so a typo cannot silently degrade the
/// layout to theme defaults.
///
/// Failure policy: every construction and handler path is wrapped; a failure logs and leaves
/// the game untouched. A notice is never allowed to block the main menu.
/// </summary>
internal sealed partial class ModNotice : Control, IScreenContext
{
    private const string NoticeNodeName = "RegentFXFastBoot_ModNotice";

    private const string LoadOrderManagerUrl = "https://steamcommunity.com/sharedfiles/filedetails/?id=3747605109";

    private NoticeKind _kind;
    private int _warmed;
    private bool _dismissed;

    private Label? _stepsLabel;

    /// <summary>Controller focus target. The button that always closes the modal.</summary>
    public Control? DefaultFocusedControl { get; private set; }

    /// <summary>
    /// Builds and shows a notice inside the engine's modal container. Returns false when the
    /// container is unavailable or its single modal slot is taken, or when construction
    /// failed - the caller reports that truthfully, retries within its budget, and never
    /// records the notice as shown.
    ///
    /// The slot check is done here rather than inferred from Add(): NModalContainer.Add
    /// returns void and, when a modal is already open, logs a warning and drops the node
    /// WITHOUT adding it to the tree (NModalContainer.cs Add()). Treating that call as
    /// success would both record a popup the player never saw and leak an unparented node.
    /// </summary>
    internal static bool TryShow(NoticeKind kind, int warmed = 0)
    {
        try
        {
            NModalContainer? container = NModalContainer.Instance;
            if (container == null || !GodotObject.IsInstanceValid(container))
            {
                MainFile.Log.Info($"{PrefixFor(kind)}: the engine modal container is not available yet; the notice was not shown");
                return false;
            }
            if (container.OpenModal != null)
            {
                // Something else owns the single slot (the engine's own mod-loading
                // confirmation or the early-access disclaimer are the usual holders on the
                // main menu). Back off without logging per attempt; the caller retries on an
                // interval and gives up on a bounded budget.
                return false;
            }

            var notice = new ModNotice { Name = NoticeNodeName, _kind = kind, _warmed = warmed };
            notice.BuildUi();
            container.Add(notice);
            // Read back instead of assuming: success means the container actually adopted it.
            if (!ReferenceEquals(container.OpenModal, notice))
            {
                MainFile.Log.Warn($"{PrefixFor(kind)}: the engine modal container did not adopt the notice node; not shown");
                if (GodotObject.IsInstanceValid(notice))
                    notice.QueueFree();
                return false;
            }
            MainFile.Log.Info(kind == NoticeKind.LateOrder
                ? "NOTICE: late-order popup shown on the main menu (once). It explains the required load order and offers " +
                  "'apply (instructions)' and 'do not show again'; it writes no settings and never reorders mods."
                : $"NOTICE-SUCCESS: popup shown on the main menu (once): acceleration ran and {warmed} scene(s) were warmed. " +
                  "It writes no settings and requires no action.");

            // Recorded at DISPLAY time, not at button time: the user-facing decision was
            // "show it once", so a player who simply quits without pressing anything must not
            // be shown it again. Recording here is also the only point that can prove the
            // notice was actually on screen.
            if (NoticeState.MarkShown(kind))
            {
                MainFile.Log.Info($"{PrefixFor(kind)}: one-shot state recorded at {NoticeState.ResolvePath()}; this notice will not be shown again");
            }
            else
            {
                MainFile.Log.Warn($"{PrefixFor(kind)}: the notice was shown but its one-shot state could not be written; it may appear once more next launch");
            }
            return true;
        }
        catch (Exception e)
        {
            MainFile.Log.Warn($"{PrefixFor(kind)}: could not show the notice ({e.GetType().Name}: {e.Message}); the game is unaffected");
            return false;
        }
    }

    private void BuildUi()
    {
        SetAnchorsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Stop;

        var scrim = new ColorRect
        {
            Color = new Color(0f, 0f, 0f, 0.62f),
            MouseFilter = MouseFilterEnum.Stop
        };
        scrim.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(scrim);

        // Centred and sized by content. The root is a plain Control, so SizeFlags alone would
        // do nothing; a full-rect CenterContainer gives the panel its minimum size and centres
        // it, and the VBox's minimum width is what makes the long body text wrap instead of
        // running off screen.
        var center = new CenterContainer { MouseFilter = MouseFilterEnum.Ignore };
        center.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(center);

        var panel = new PanelContainer { MouseFilter = MouseFilterEnum.Stop };
        center.AddChild(panel);

        var margin = new MarginContainer();
        margin.AddThemeConstantOverride(ThemeConstants.MarginContainer.MarginLeft, 18);
        margin.AddThemeConstantOverride(ThemeConstants.MarginContainer.MarginTop, 16);
        margin.AddThemeConstantOverride(ThemeConstants.MarginContainer.MarginRight, 18);
        margin.AddThemeConstantOverride(ThemeConstants.MarginContainer.MarginBottom, 16);
        panel.AddChild(margin);

        var column = new VBoxContainer();
        column.AddThemeConstantOverride(ThemeConstants.BoxContainer.Separation, 10);
        // A minimum width is what makes the long body/steps text wrap into a readable block
        // instead of stretching the panel to one very wide line.
        column.CustomMinimumSize = new Vector2(620f, 0f);
        margin.AddChild(column);

        var title = new Label
        {
            Text = Text.Title,
            HorizontalAlignment = HorizontalAlignment.Center,
            AutowrapMode = TextServer.AutowrapMode.WordSmart
        };
        title.AddThemeFontSizeOverride(ThemeConstants.Label.FontSize, 22);
        column.AddChild(title);

        var body = new Label
        {
            Text = _kind == NoticeKind.LateOrder ? Text.LateOrderBody : Text.SuccessBody(_warmed),
            AutowrapMode = TextServer.AutowrapMode.WordSmart
        };
        column.AddChild(body);

        // Only the late-order notice carries the fix steps: the success notice has nothing to
        // ask of the player, so it must not present instructions they do not need.
        if (_kind == NoticeKind.LateOrder)
        {
            var steps = new Label
            {
                Text = Text.Steps,
                AutowrapMode = TextServer.AutowrapMode.WordSmart
            };
            column.AddChild(steps);
            _stepsLabel = steps;
        }

        var buttons = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        buttons.AddThemeConstantOverride(ThemeConstants.BoxContainer.Separation, 16);
        column.AddChild(buttons);

        if (_kind == NoticeKind.LateOrder)
        {
            Button apply = MakeButton(Text.ApplyButton, OnApplyPressed);
            buttons.AddChild(apply);
            Button dismiss = MakeButton(Text.DismissButton, OnDismissPressed);
            buttons.AddChild(dismiss);
            DefaultFocusedControl = dismiss;
        }
        else
        {
            Button ok = MakeButton(Text.OkButton, OnOkPressed);
            buttons.AddChild(ok);
            DefaultFocusedControl = ok;
        }

        // Focus is NOT grabbed here: the engine assigns focus from
        // ActiveScreenContext.FocusOnDefaultControl() -> Control.TryGrabFocus(), which only
        // acts when the player is using directional (controller) navigation (NodeUtil.cs:107).
        // Grabbing it unconditionally would steal focus from a mouse player for no reason.
    }

    /// <summary>
    /// Per-kind log prefix. The acceptance script (tools/verify-fastboot-order.ps1) scopes
    /// its assertions by these, so a success notice can never be mistaken for a late-order
    /// notice when both appear in one log.
    /// </summary>
    private static string PrefixFor(NoticeKind kind) =>
        kind == NoticeKind.LateOrder ? "NOTICE" : "NOTICE-SUCCESS";

    private static Button MakeButton(string text, Action onPressed)
    {
        var button = new Button
        {
            Text = text,
            FocusMode = Control.FocusModeEnum.All,
            MouseFilter = MouseFilterEnum.Stop,
            CustomMinimumSize = new Vector2(160f, 40f)
        };
        button.Pressed += onPressed;
        return button;
    }

    /// <summary>
    /// "Apply" here means: show the player how to make it take effect. This mod does NOT
    /// write settings.save and does NOT reorder mods - the order is written by a load-order
    /// editor at the player's explicit action.
    /// </summary>
    private void OnApplyPressed()
    {
        try
        {
            MainFile.Log.Info(
                "NOTICE: player asked how to make the acceleration take effect. Instructions: install Load Order Manager " +
                "(Steam Workshop 3747605109), open Modding -> Load Order, move RegentFXFastBoot above RegentFX, Apply, restart. " +
                "This mod writes no settings and reorders nothing.");
            if (_stepsLabel != null && GodotObject.IsInstanceValid(_stepsLabel))
                _stepsLabel.Text = Text.AppliedSteps;
            // ShellOpen returns Godot.Error (the engine itself ignores it, at
            // SteamPlatformUtilStrategy.cs:112). Here the result is worth reporting: the
            // steps text is the real payload, and telling the player the browser did not
            // open prevents them waiting for a page that will never appear.
            Godot.Error openResult = OS.ShellOpen(LoadOrderManagerUrl);
            if (openResult != Godot.Error.Ok)
            {
                MainFile.Log.Warn($"NOTICE: the browser could not be opened ({openResult}); the steps are shown in the popup instead");
                if (_stepsLabel != null && GodotObject.IsInstanceValid(_stepsLabel))
                    _stepsLabel.Text = Text.AppliedStepsNoBrowser;
            }
        }
        catch (Exception e)
        {
            MainFile.Log.Warn($"NOTICE: showing the apply instructions failed ({e.GetType().Name}: {e.Message})");
        }
    }

    /// <summary>
    /// Closes the late-order notice. The one-shot state was already recorded when it was
    /// shown (see TryShow), so this only reports the player's choice; the button exists so the
    /// player can dismiss the modal deliberately rather than by quitting.
    /// </summary>
    private void OnDismissPressed()
    {
        MainFile.Log.Info(
            "NOTICE: player dismissed the late-order notice. The acceleration stays armed: if the load order is fixed later, " +
            "it will take effect automatically.");
        CloseSelf();
    }

    private void OnOkPressed()
    {
        MainFile.Log.Info("NOTICE-SUCCESS: player acknowledged the success notice.");
        CloseSelf();
    }

    /// <summary>
    /// Closes the notice and releases the engine's single modal slot.
    ///
    /// NModalContainer.Clear() frees EVERY non-backstop child, so it is called only while the
    /// container still reports this notice as its OpenModal - i.e. only while this notice is
    /// provably the sole modal. If something else owns the slot by now (the engine replaced or
    /// already cleared it), this notice is not the container's modal anymore and is freed
    /// directly, so an engine popup can never be destroyed by this mod.
    /// </summary>
    private void CloseSelf()
    {
        if (_dismissed)
            return;
        _dismissed = true;
        try
        {
            NModalContainer? container = NModalContainer.Instance;
            if (container != null && GodotObject.IsInstanceValid(container) &&
                ReferenceEquals(container.OpenModal, this))
            {
                container.Clear();
            }
            else if (GodotObject.IsInstanceValid(this))
            {
                QueueFree();
            }
        }
        catch (Exception e)
        {
            MainFile.Log.Warn($"NOTICE: closing the popup failed ({e.GetType().Name}: {e.Message})");
            try
            {
                if (GodotObject.IsInstanceValid(this))
                    QueueFree();
            }
            catch
            {
                // nothing further can be done; the node dies with the container
            }
        }
    }

    public override void _Input(InputEvent @event)
    {
        // Escape closes. _Input (not _UnhandledInput) is deliberate: it is what Load Order
        // Manager's own in-game panel uses on this engine version, and the engine's input
        // managers (NControllerManager/NInputManager) consume keys earlier in the chain, so
        // an unhandled-input handler can be skipped entirely. The event is marked handled so
        // the key does not also reach the main menu behind the modal.
        try
        {
            if (@event is InputEventKey { Pressed: true, Echo: false } key &&
                key.Keycode == Key.Escape)
            {
                CloseSelf();
                GetViewport()?.SetInputAsHandled();
            }
        }
        catch
        {
            // never let input handling throw into the engine
        }
    }

    /// <summary>Bilingual strings. Chinese when the game language is Chinese, English otherwise.</summary>
    private static class Text
    {
        internal static readonly bool Chinese = IsChineseLanguage();

        internal const string Title = "RegentFX Fast Boot";

        internal static string ApplyButton => Chinese ? "\u4F7F\u751F\u6548" : "Apply";

        internal static string DismissButton => Chinese ? "\u4E0D\u518D\u63D0\u793A" : "Do not show again";

        internal static string OkButton => Chinese ? "\u77E5\u9053\u4E86" : "OK";

        internal static string LateOrderBody => Chinese
            ? "\u672C\u6B21\u542F\u52A8\u6CA1\u6709\u52A0\u901F\uFF1ARegentFX \u6BD4\u672C\u6A21\u7EC4\u5148\u52A0\u8F7D\uFF0C" +
              "\u5B83\u7684\u540C\u6B65\u9884\u52A0\u8F7D\u5DF2\u7ECF\u8DD1\u5B8C\uFF0C\u65E0\u6CD5\u64A4\u56DE\u3002" +
              "\u672C\u6A21\u7EC4\u4E0D\u4F1A\u66FF\u4F60\u6539\u52A8\u6A21\u7EC4\u987A\u5E8F\uFF0C\u987A\u5E8F\u9700\u8981\u4F60\u81EA\u5DF1\u8BBE\u7F6E\u3002"
            : "Acceleration did not run this launch: RegentFX loaded before this mod, so its synchronous scene preload " +
              "already happened and cannot be undone. This mod never reorders your mods - the order has to be set by you.";

        /// <summary>
        /// Reports the warm-up that actually ran. The count is the per-path outcome the mod
        /// logged, not a cache size, and it is stated as a fact about this launch rather than
        /// as a promise about boot time.
        /// </summary>
        internal static string SuccessBody(int warmed) => Chinese
            ? "\u672C\u6B21\u542F\u52A8\u5DF2\u628A RegentFX \u7684\u540C\u6B65\u573A\u666F\u9884\u52A0\u8F7D" +
              $"\u66FF\u6362\u4E3A\u540E\u53F0\u9010\u5E27\u9884\u70ED\uFF1A\u9884\u70ED\u4E86 {warmed} \u4E2A\u573A\u666F\uFF0C\u5168\u90E8\u6210\u529F\u3002" +
              "\u542F\u52A8\u65F6\u7684\u8FD9\u6B21\u963B\u585E\u5DF2\u7ECF\u6D88\u5931\u3002\u6B64\u63D0\u793A\u53EA\u663E\u793A\u4E00\u6B21\u3002"
            : $"This launch replaced RegentFX's synchronous scene preload with a background frame-by-frame warm-up: " +
              $"{warmed} scene(s) warmed, none failed. The boot stall from that preload is gone. This notice is shown once.";

        internal static string Steps => Chinese
            ? "\u4F7F\u52A0\u901F\u751F\u6548\u7684\u6B65\u9AA4\uFF1A\n" +
              "1. \u8BA2\u9605 Load Order Manager\uFF08\u5DE5\u574A 3747605109\uFF09\n" +
              "2. \u8FDB\u6E38\u620F\u7684\u6A21\u7EC4\u754C\u9762\uFF0C\u70B9\u201C\u52A0\u8F7D\u987A\u5E8F\u201D\n" +
              "3. \u628A RegentFXFastBoot \u79FB\u5230 RegentFX \u4E0A\u9762\uFF0C\u70B9\u201C\u5E94\u7528\u201D\n" +
              "4. \u91CD\u542F\u6E38\u620F\n" +
              "\u53E6\u5916\uFF1A\u672C\u6A21\u7EC4\u53EA\u80FD\u88C5\u4E00\u4EFD\uFF08\u672C\u5730\u526F\u672C\u548C\u5DE5\u574A\u526F\u672C\u540C\u65F6\u5B58\u5728\u4F1A\u6C38\u8FDC\u665A\u5E8F\uFF09\u3002"
            : "To make the acceleration take effect:\n" +
              "1. Subscribe to Load Order Manager (Workshop 3747605109)\n" +
              "2. Open Modding in game and press \"Load Order\"\n" +
              "3. Move RegentFXFastBoot above RegentFX, then press Apply\n" +
              "4. Restart the game\n" +
              "Also: keep exactly ONE RegentFXFastBoot install (a local copy plus a workshop copy forces late order forever).";

        internal static string AppliedSteps => Chinese
            ? "\u5DF2\u6253\u5F00 Load Order Manager \u7684\u5DE5\u574A\u9875\u3002\u82E5\u6D4F\u89C8\u5668\u672A\u6253\u5F00\uFF0C" +
              "\u8BF7\u5728\u5DE5\u574A\u641C\u7D22 3747605109\u3002\u5B89\u88C5\u540E\u5728\u6A21\u7EC4\u754C\u9762\u70B9\u201C\u52A0\u8F7D\u987A\u5E8F\u201D\uFF0C" +
              "\u628A RegentFXFastBoot \u79FB\u5230 RegentFX \u4E0A\u9762\uFF0C\u70B9\u201C\u5E94\u7528\u201D\uFF0C\u7136\u540E\u91CD\u542F\u6E38\u620F\u3002"
            : "The Load Order Manager Workshop page has been opened in your browser. If it did not open, search Workshop " +
              "3747605109. After subscribing, open Modding -> Load Order, move RegentFXFastBoot above RegentFX, press Apply, " +
              "then restart the game.";

        internal static string AppliedStepsNoBrowser => Chinese
            ? "\u6D4F\u89C8\u5668\u672A\u80FD\u6253\u5F00\u3002\u8BF7\u5728\u5DE5\u574A\u641C\u7D22 Load Order Manager\uFF083747605109\uFF09\uFF0C" +
              "\u8BA2\u9605\u540E\u8FDB\u6A21\u7EC4\u754C\u9762\u70B9\u201C\u52A0\u8F7D\u987A\u5E8F\u201D\uFF0C" +
              "\u628A RegentFXFastBoot \u79FB\u5230 RegentFX \u4E0A\u9762\uFF0C\u70B9\u201C\u5E94\u7528\u201D\uFF0C\u7136\u540E\u91CD\u542F\u6E38\u620F\u3002"
            : "The browser could not be opened. Search the Workshop for Load Order Manager (3747605109); after subscribing, " +
              "open Modding -> Load Order, move RegentFXFastBoot above RegentFX, press Apply, then restart the game.";

        private static bool IsChineseLanguage()
        {
            try
            {
                string? code = MegaCrit.Sts2.Core.Saves.SaveManager.Instance?.SettingsSave?.Language;
                return code != null && code.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }
    }
}
