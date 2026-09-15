using System;

using Godot;

using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Screens.ScreenContext;

namespace RegentFXFastBoot.RegentFXFastBootCode;

/// <summary>
/// The late-order notice: a two-button modal shown once, on the main menu, when this launch
/// could not accelerate anything because RegentFX loaded first (RFX-3, 2026-09-16).
///
/// Why this exists: the acceleration is silent when it fails - the only trace was a
/// LATE-ORDER log line. A subscriber has no way to learn that a load-order editor is needed,
/// so the mod now says so where the player actually looks.
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
/// into the main menu for a popup this mod can draw itself.
///
/// Failure policy: every construction and handler path is wrapped; a failure logs and leaves
/// the game untouched. The notice is never allowed to block the main menu.
/// </summary>
internal sealed partial class LateOrderNotice : Control, IScreenContext
{
    private const string NoticeNodeName = "RegentFXFastBoot_LateOrderNotice";

    private const string LoadOrderManagerUrl = "https://steamcommunity.com/sharedfiles/filedetails/?id=3747605109";

    private bool _dismissed;

    private Label? _stepsLabel;

    /// <summary>Controller focus target. The dismiss button is the safe default.</summary>
    public Control? DefaultFocusedControl { get; private set; }

    /// <summary>
    /// Builds and shows the notice inside the engine's modal container. Returns false when
    /// the container is unavailable or its single modal slot is taken, or when construction
    /// failed - the caller reports that truthfully, retries within its budget, and never
    /// records the notice as shown.
    ///
    /// The slot check is done here rather than inferred from Add(): NModalContainer.Add
    /// returns void and, when a modal is already open, logs a warning and drops the node
    /// WITHOUT adding it to the tree (NModalContainer.cs Add()). Treating that call as
    /// success would both record a popup the player never saw and leak an unparented node.
    /// </summary>
    internal static bool TryShow()
    {
        try
        {
            NModalContainer? container = NModalContainer.Instance;
            if (container == null || !GodotObject.IsInstanceValid(container))
            {
                MainFile.Log.Info("NOTICE: the engine modal container is not available yet; the late-order notice was not shown");
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

            var notice = new LateOrderNotice { Name = NoticeNodeName };
            notice.BuildUi();
            container.Add(notice);
            // Read back instead of assuming: success means the container actually adopted it.
            if (!ReferenceEquals(container.OpenModal, notice))
            {
                MainFile.Log.Warn("NOTICE: the engine modal container did not adopt the notice node; not shown");
                if (GodotObject.IsInstanceValid(notice))
                    notice.QueueFree();
                return false;
            }
            MainFile.Log.Info(
                "NOTICE: late-order popup shown on the main menu (once). It explains the required load order and offers " +
                "'apply (instructions)' and 'do not show again'; it writes no settings and never reorders mods.");

            // Recorded at DISPLAY time, not at button time: the user-facing decision was
            // "show it once, then never again", so a player who simply quits without pressing
            // anything must not be shown it again next launch. Recording here is also the only
            // point that can prove the notice was actually on screen.
            if (NoticeState.MarkNoticeShown())
            {
                MainFile.Log.Info($"NOTICE: one-shot state recorded at {NoticeState.ResolvePath()}; the notice will not be shown again");
            }
            else
            {
                MainFile.Log.Warn("NOTICE: the notice was shown but its one-shot state could not be written; it may appear once more next launch");
            }
            return true;
        }
        catch (Exception e)
        {
            MainFile.Log.Warn($"NOTICE: could not show the late-order popup ({e.GetType().Name}: {e.Message}); the game is unaffected");
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
        // it, and the VBox's minimum width is what makes the long steps text wrap instead of
        // running off screen.
        var center = new CenterContainer { MouseFilter = MouseFilterEnum.Ignore };
        center.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(center);

        var panel = new PanelContainer { MouseFilter = MouseFilterEnum.Stop };
        center.AddChild(panel);

        var margin = new MarginContainer();
        margin.AddThemeConstantOverride("margin_left", 18);
        margin.AddThemeConstantOverride("margin_top", 16);
        margin.AddThemeConstantOverride("margin_right", 18);
        margin.AddThemeConstantOverride("margin_bottom", 16);
        panel.AddChild(margin);

        var column = new VBoxContainer();
        column.AddThemeConstantOverride("separation", 10);
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
        title.AddThemeFontSizeOverride("font_size", 22);
        column.AddChild(title);

        var body = new Label
        {
            Text = Text.Body,
            AutowrapMode = TextServer.AutowrapMode.WordSmart
        };
        column.AddChild(body);

        var steps = new Label
        {
            Text = Text.Steps,
            AutowrapMode = TextServer.AutowrapMode.WordSmart
        };
        column.AddChild(steps);
        _stepsLabel = steps;

        var buttons = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        buttons.AddThemeConstantOverride("separation", 16);
        column.AddChild(buttons);

        Button apply = MakeButton(Text.ApplyButton, OnApplyPressed);
        buttons.AddChild(apply);
        Button dismiss = MakeButton(Text.DismissButton, OnDismissPressed);
        buttons.AddChild(dismiss);

        // Controller/default focus: dismissing is the conservative default, and it is the
        // button that always closes the modal. Focus is NOT grabbed here: the engine assigns
        // focus from ActiveScreenContext.FocusOnDefaultControl() -> Control.TryGrabFocus(),
        // which only acts when the player is using directional (controller) navigation
        // (NodeUtil.cs:107). Grabbing it unconditionally would steal focus from a mouse
        // player for no reason.
        DefaultFocusedControl = dismiss;
    }

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
            OS.ShellOpen(LoadOrderManagerUrl);
        }
        catch (Exception e)
        {
            MainFile.Log.Warn($"NOTICE: showing the apply instructions failed ({e.GetType().Name}: {e.Message})");
        }
    }

    /// <summary>
    /// Closes the notice. The one-shot state was already recorded when the notice was shown
    /// (see TryShow), so this only reports the player's choice; the button exists so the
    /// player can dismiss the modal deliberately rather than by quitting.
    /// </summary>
    private void OnDismissPressed()
    {
        MainFile.Log.Info(
            "NOTICE: player dismissed the late-order notice. The acceleration stays armed: if the load order is fixed later, " +
            "it will take effect automatically.");
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

        internal static string Body => Chinese
            ? "\u672C\u6B21\u542F\u52A8\u6CA1\u6709\u52A0\u901F\uFF1ARegentFX \u6BD4\u672C\u6A21\u7EC4\u5148\u52A0\u8F7D\uFF0C" +
              "\u5B83\u7684\u540C\u6B65\u9884\u52A0\u8F7D\u5DF2\u7ECF\u8DD1\u5B8C\uFF0C\u65E0\u6CD5\u64A4\u56DE\u3002" +
              "\u672C\u6A21\u7EC4\u4E0D\u4F1A\u66FF\u4F60\u6539\u52A8\u6A21\u7EC4\u987A\u5E8F\uFF0C\u987A\u5E8F\u9700\u8981\u4F60\u81EA\u5DF1\u8BBE\u7F6E\u3002"
            : "Acceleration did not run this launch: RegentFX loaded before this mod, so its synchronous scene preload " +
              "already happened and cannot be undone. This mod never reorders your mods - the order has to be set by you.";

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
