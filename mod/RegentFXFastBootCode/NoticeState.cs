using System;
using System.IO;

using Godot;

namespace RegentFXFastBoot.RegentFXFastBootCode;

/// <summary>
/// One-shot notice state for the late-order popup (RFX-3, 2026-09-16).
///
/// Contract: this file is a UI convenience only. It never influences the warm-up, and a
/// read or write failure must never affect the game - the worst outcome of any failure
/// here is that the notice is shown once more than intended.
///
/// Location: OS.GetUserDataDir()/RegentFXFastBoot/notice.json (Windows:
/// %APPDATA%/SlayTheSpire2/RegentFXFastBoot/notice.json). Godot's user data dir is the
/// engine's own user:// root; the engine writes its logs and saves there too
/// (AutoSlayLog.cs:83 uses OS.GetUserDataDir()+"logs"), so no new convention is invented.
///
/// Deliberately a hand-written 30-byte JSON object rather than a serializer: the payload is
/// one boolean, and pulling a serialization dependency (or System.Text.Json reflection) into
/// a mod initializer for that is not worth the surface.
/// </summary>
internal static class NoticeState
{
    private const string ModFolderName = "RegentFXFastBoot";
    private const string FileName = "notice.json";

    /// <summary>
    /// True when the late-order notice has already been displayed once, i.e. it must not be
    /// displayed again. A missing or unreadable file reads as false (show the notice).
    /// </summary>
    internal static bool IsNoticeShown()
    {
        try
        {
            string path = ResolvePath();
            if (path.Length == 0 || !File.Exists(path))
                return false;

            string text = File.ReadAllText(path);
            // Intentionally forgiving: any payload that does not say true reads as false.
            return text.Contains("\"noticeShown\"", StringComparison.Ordinal) &&
                   text.Contains("true", StringComparison.Ordinal);
        }
        catch (Exception e)
        {
            MainFile.Log.Warn($"NOTICE-STATE: could not read {FileName} ({e.GetType().Name}: {e.Message}); treating the notice as not yet shown");
            return false;
        }
    }

    /// <summary>
    /// Records that the notice was displayed. Returns true only when the record was
    /// actually written; the caller reports the real outcome instead of assuming success.
    /// </summary>
    internal static bool MarkNoticeShown()
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
            File.WriteAllText(path, "{\n  \"noticeShown\": true\n}\n");
            return true;
        }
        catch (Exception e)
        {
            MainFile.Log.Warn($"NOTICE-STATE: could not write {FileName} ({e.GetType().Name}: {e.Message}); the notice may appear again next launch");
            return false;
        }
    }

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
