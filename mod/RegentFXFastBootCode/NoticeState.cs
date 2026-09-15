using System;
using System.IO;
using System.Text.RegularExpressions;

using Godot;

namespace RegentFXFastBoot.RegentFXFastBootCode;

/// <summary>
/// One-shot notice state (RFX-3 late-order notice, RFX-4 success notice).
///
/// Contract: this file is a UI convenience only. It never influences the warm-up, and a
/// read or write failure must never affect the game - the worst outcome of any failure
/// here is that a notice is shown once more than intended.
///
/// Location: OS.GetUserDataDir()/RegentFXFastBoot/notice.json (Windows:
/// %APPDATA%/SlayTheSpire2/RegentFXFastBoot/notice.json). Godot's user data dir is the
/// engine's own user:// root; the engine writes its logs and saves there too
/// (AutoSlayLog.cs:83 uses OS.GetUserDataDir()+"logs"), so no new convention is invented.
///
/// Two independent flags share one payload. Two consequences are load-bearing:
///   - Every write emits BOTH flags (the union), because the file is replaced wholesale -
///     writing only the flag that just changed would erase the other one and resurrect the
///     notice it suppresses.
///   - Every read matches its OWN key with a value-specific pattern. A bare search for
///     "true" would read {"noticeShown": false, "successShown": true} as "the late-order
///     notice was already shown", silently suppressing the notice that still needs to appear.
///
/// Deliberately a hand-written ~60-byte JSON object rather than a serializer: the payload is
/// two booleans, and pulling a serialization dependency (or System.Text.Json reflection) into
/// a mod initializer for that is not worth the surface.
/// </summary>
internal static class NoticeState
{
    private const string ModFolderName = "RegentFXFastBoot";
    private const string FileName = "notice.json";

    private const string LateOrderKey = "noticeShown";
    private const string SuccessKey = "successShown";

    /// <summary>
    /// True when the notice of this kind has already been displayed once, i.e. it must not be
    /// displayed again. A missing or unreadable file reads as false (show the notice).
    /// </summary>
    internal static bool IsShown(NoticeKind kind)
    {
        try
        {
            string path = ResolvePath();
            if (path.Length == 0 || !File.Exists(path))
                return false;

            string text = File.ReadAllText(path);
            // Key-scoped and value-specific: see the class comment for why a bare "true"
            // search would be wrong once two flags share the file.
            return Regex.IsMatch(text, $"\"{KeyFor(kind)}\"\\s*:\\s*true", RegexOptions.IgnoreCase);
        }
        catch (Exception e)
        {
            MainFile.Log.Warn($"NOTICE-STATE: could not read {FileName} ({e.GetType().Name}: {e.Message}); treating the {kind} notice as not yet shown");
            return false;
        }
    }

    /// <summary>
    /// Records that the notice of this kind was displayed, preserving the other flag.
    /// Returns true only when the record was actually written; the caller reports the real
    /// outcome instead of assuming success.
    /// </summary>
    internal static bool MarkShown(NoticeKind kind)
    {
        try
        {
            string path = ResolvePath();
            if (path.Length == 0)
                return false;

            string? dir = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(dir))
                return false;
            Directory.CreateDirectory(dir);

            // Union of both flags: the other one may already be true from an earlier launch,
            // and a wholesale write that dropped it would bring its notice back.
            bool lateOrder = kind == NoticeKind.LateOrder || IsShown(NoticeKind.LateOrder);
            bool success = kind == NoticeKind.Succeeded || IsShown(NoticeKind.Succeeded);
            File.WriteAllText(
                path,
                "{\n" +
                $"  \"{LateOrderKey}\": {(lateOrder ? "true" : "false")},\n" +
                $"  \"{SuccessKey}\": {(success ? "true" : "false")}\n" +
                "}\n");
            return true;
        }
        catch (Exception e)
        {
            MainFile.Log.Warn($"NOTICE-STATE: could not write {FileName} ({e.GetType().Name}: {e.Message}); the {kind} notice may appear again next launch");
            return false;
        }
    }

    private static string KeyFor(NoticeKind kind) =>
        kind == NoticeKind.LateOrder ? LateOrderKey : SuccessKey;

    /// <summary>Absolute path of the notice file, or "" when the user data dir is unavailable.</summary>
    internal static string ResolvePath()
    {
        try
        {
            string root = OS.GetUserDataDir();
            if (string.IsNullOrEmpty(root))
                return "";
            return Path.Combine(root, ModFolderName, FileName);
        }
        catch
        {
            return "";
        }
    }
}
