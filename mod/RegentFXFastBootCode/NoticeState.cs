using System;
using System.IO;
using System.Text;
using System.Text.Json;

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
///   - Only valid JSON boolean properties count as state. A partial or unreadable file must
///     not turn an unknown flag into false and then overwrite the last valid record.
///
/// Reads retain the last known true flags for this user-data path. Writes stage the complete
/// union beside the destination and commit atomically, so a failed write leaves the previous
/// file intact. This uses the runtime's JSON reader, not reflection-based serialization.
/// </summary>
internal static class NoticeState
{
    private const string ModFolderName = "RegentFXFastBoot";
    private const string FileName = "notice.json";

    private const string LateOrderKey = "noticeShown";
    private const string SuccessKey = "successShown";

    private static readonly object Sync = new();
    private static string _knownPath = "";
    private static bool _lateOrderShown;
    private static bool _successShown;

    /// <summary>
    /// True when this notice is known to have been displayed. Missing or unreadable state
    /// never invents success; a read failure retains any previously known true flag.
    /// </summary>
    internal static bool IsShown(NoticeKind kind)
    {
        lock (Sync)
        {
            string path = ResolvePath();
            if (path.Length != 0)
                TryRead(path, out _);
            return KnownShown(kind);
        }
    }

    /// <summary>
    /// Records that the notice of this kind was displayed, preserving the other flag.
    /// Returns true only when the record was actually written; the caller reports the real
    /// outcome instead of assuming success.
    /// </summary>
    internal static bool MarkShown(NoticeKind kind)
    {
        lock (Sync)
        {
            string? temporaryPath = null;
            bool ownsTemporary = false;
            try
            {
                string path = ResolvePath();
                if (path.Length == 0 || !TryRead(path, out bool existed))
                    return false;

                string? dir = Path.GetDirectoryName(path);
                if (string.IsNullOrEmpty(dir))
                    return false;
                Directory.CreateDirectory(dir);

                bool lateOrder = kind == NoticeKind.LateOrder || _lateOrderShown;
                bool success = kind == NoticeKind.Succeeded || _successShown;
                string payload = "{\n" +
                    $"  \"{LateOrderKey}\": {(lateOrder ? "true" : "false")},\n" +
                    $"  \"{SuccessKey}\": {(success ? "true" : "false")}\n" +
                    "}\n";
                temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, System.IO.FileAccess.Write, FileShare.None))
                {
                    ownsTemporary = true;
                    using var writer = new StreamWriter(stream, new UTF8Encoding(false), 1024, leaveOpen: true);
                    writer.Write(payload);
                    writer.Flush();
                    stream.Flush(flushToDisk: true);
                }

                // Sync serializes this process's read/merge/commit. Do not overwrite a
                // competing first creation: a missing read commits by non-overwriting Move.
                // Existing records use atomic Replace; a concurrent deletion also fails.
                if (existed)
                    File.Replace(temporaryPath, path, null);
                else
                    File.Move(temporaryPath, path);
                ownsTemporary = false;
                _lateOrderShown = lateOrder;
                _successShown = success;
                return true;
            }
            catch (Exception e)
            {
                MainFile.Log.Warn($"NOTICE-STATE: could not write {FileName} ({e.GetType().Name}: {e.Message}); the {kind} notice may appear again next launch");
                return false;
            }
            finally
            {
                if (ownsTemporary)
                {
                    try
                    {
                        File.Delete(temporaryPath!);
                    }
                    catch (Exception e)
                    {
                        MainFile.Log.Warn($"NOTICE-STATE: could not remove the uncommitted temporary record ({e.GetType().Name}: {e.Message})");
                    }
                }
            }
        }
    }

    private static bool KnownShown(NoticeKind kind) =>
        kind == NoticeKind.LateOrder ? _lateOrderShown : _successShown;

    // Called under Sync. Missing is a valid first-launch state, not proof of display.
    // Other read failures forbid a write: neither another kind's flag nor corrupt evidence
    // may be replaced by an invented false value.
    private static bool TryRead(string path, out bool existed)
    {
        existed = false;
        if (!string.Equals(_knownPath, path, StringComparison.Ordinal))
        {
            _knownPath = path;
            _lateOrderShown = false;
            _successShown = false;
        }
        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new JsonException("Notice state must be an object.");

            bool lateOrder = false, success = false;
            bool hasLateOrder = false, hasSuccess = false;
            foreach (JsonProperty property in document.RootElement.EnumerateObject())
            {
                bool isLateOrder = property.NameEquals(LateOrderKey);
                bool isSuccess = property.NameEquals(SuccessKey);
                if (!isLateOrder && !isSuccess)
                    continue;
                if (property.Value.ValueKind != JsonValueKind.True && property.Value.ValueKind != JsonValueKind.False)
                    throw new JsonException("Notice flags must be booleans.");
                if (isLateOrder)
                {
                    if (hasLateOrder)
                        throw new JsonException("Duplicate noticeShown flag.");
                    hasLateOrder = true;
                    lateOrder = property.Value.GetBoolean();
                }
                else
                {
                    if (hasSuccess)
                        throw new JsonException("Duplicate successShown flag.");
                    hasSuccess = true;
                    success = property.Value.GetBoolean();
                }
            }
            _lateOrderShown |= lateOrder;
            _successShown |= success;
            existed = true;
            return true;
        }
        catch (FileNotFoundException)
        {
            return true;
        }
        catch (DirectoryNotFoundException)
        {
            return true;
        }
        catch (Exception e)
        {
            MainFile.Log.Warn($"NOTICE-STATE: could not read {FileName} ({e.GetType().Name}: {e.Message}); retaining the last known notice state and not overwriting this record");
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
