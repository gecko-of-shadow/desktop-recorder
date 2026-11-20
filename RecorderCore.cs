// Shared settings, hotkey and ffmpeg argument rules.
// C# 5, so the Windows build can still use the compiler that ships with Windows.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

sealed class StoredSettings
{
    public string Folder;
    public double Fps = RecorderCore.DefaultFps;
    public int Quality = 1;
    public int SplitMinutes = 30;
    public bool RecordOnLaunch = true;
    public bool CopyToClipboard = true;
    public bool PlaySounds = true;
    // Draw the mouse pointer in the video. Off = no pointer: a mouse move on a still screen
    // changes no pixel in the video, and the pointer cannot cover text.
    public bool ShowPointer = true;
    public int HotkeyModifiers = 4 | 8;   // 1 = Alt, 2 = Ctrl, 4 = Shift, 8 = Win
    public int HotkeyKey = (int)'D';
    // Capture and encoding (HANDOFF 4.3). Strings, so the settings file stays readable.
    public string CaptureBackend = RecorderCore.BackendDdagrab;   // ddagrab | gdigrab
    public string Encoder = RecorderCore.EncoderAuto;             // auto | h264_nvenc | libx264
    public string ChromaFormat = RecorderCore.Chroma420;          // yuv420 | yuv444
    // Default "changes": Bran's decision after Phase B (HANDOFF 5, 6.8). Same read result as fixed fps, and a still
    // screen costs almost nothing.
    public string FrameRateMode = RecorderCore.ModeChanges;       // fixed | changes
    public int NvencCq = RecorderCore.DefaultNvencCq;
    // Physical px, virtual-screen coordinates. Width or height 0 = no region = whole desktop.
    public int RegionX, RegionY, RegionWidth, RegionHeight;
    public int LastRegionX, LastRegionY, LastRegionWidth, LastRegionHeight;
    // Region selector options (HANDOFF 4.1). Keys C, M and P in the selector change them.
    public bool SelectorCrosshair = false;
    public bool SelectorMagnifier = true;
    public bool SelectorPosition = true;
    // AI capture folder (AI capture brief, Phase 1). "folder" (default, Bran 2026-11-19) or "mp4" (the old output).
    public string OutputMode = RecorderCore.OutputFolder;
    public int SettleMs = 400;                 // settled frame: no change for this time
    public int StillIntervalSeconds = 5;       // a frame at this interval when the screen does not settle
    public int ScrollPercent = 60;             // scroll-distance rule (condition A, Bran 2026-11-19): start value
    public int SizeLimit = RecorderCore.DefaultSizeLimit;   // long side in px; index.md warns above it
    // Lines this program does not understand. Written back so another OS can keep its keys.
    public List<string> ExtraLines = new List<string>();
}

// A rectangle in physical px, in virtual-screen coordinates
// (origin = top left of the primary monitor, the same origin as gdigrab).
public struct CaptureRegion
{
    public int X, Y, Width, Height;

    public CaptureRegion(int x, int y, int width, int height)
    {
        X = x;
        Y = y;
        Width = width;
        Height = height;
    }

    // No region: record the whole desktop.
    public bool IsEmpty { get { return Width <= 0 || Height <= 0; } }

    // H.264 needs even sizes. Shrink by 1 px (never grow), so the region stays inside its monitor.
    public CaptureRegion MakeEven()
    {
        if (IsEmpty) return new CaptureRegion(0, 0, 0, 0);
        CaptureRegion r = new CaptureRegion(X, Y, Width & ~1, Height & ~1);
        if (r.IsEmpty) return new CaptureRegion(0, 0, 0, 0);
        return r;
    }
}

// One DXGI output of adapter 0, as NativeMethods.GetDxgiOutputs reads it.
// Index is the position in IDXGIAdapter::EnumOutputs, which is the ddagrab output_idx.
public struct DxgiOutputInfo
{
    public int Index, X, Y, Width, Height;
    public string DeviceName;   // for example \\.\DISPLAY1, the same text as Screen.DeviceName
    public bool Attached;
}

// One monitor, as the caller measured it, in physical px and virtual-screen coordinates.
// Index is the DXGI output index that ddagrab uses (output_idx), from RecorderCore.MatchDxgiOutput.
// It is not the position in Screen.AllScreens. Index -1 = ddagrab cannot record this monitor
// (no DXGI output on adapter 0 matches it), so PlanCapture uses gdigrab for it.
public struct MonitorBounds
{
    public int Index, X, Y, Width, Height;

    public MonitorBounds(int index, int x, int y, int width, int height)
    {
        Index = index;
        X = x;
        Y = y;
        Width = width;
        Height = height;
    }

    public bool Contains(CaptureRegion r)
    {
        if (r.IsEmpty || Width <= 0 || Height <= 0) return false;
        return r.X >= X && r.Y >= Y
            && (long)r.X + r.Width <= (long)X + Width
            && (long)r.Y + r.Height <= (long)Y + Height;
    }
}

// What the argument builder really does, after the fallbacks. The UI can show Note.
sealed class CapturePlan
{
    public string Backend;          // ddagrab | gdigrab
    public string Encoder;          // h264_nvenc | libx264
    public bool WholeDesktop;       // gdigrab only: no region, all monitors
    public int OutputIndex;         // ddagrab only
    public int OffsetX, OffsetY;    // gdigrab: virtual screen; ddagrab: relative to the monitor
    public int Width, Height;       // even; 0 when WholeDesktop
    public string Note = "";        // empty, or the reason for a fallback
}

// Cumulative values from one FFmpeg stats line.
public struct FfmpegProgress
{
    public long Frames, Duplicates, Dropped;
    // time= of the stats line in seconds (the time of the newest packet in the file). -1 when not given.
    public double TimeSeconds;
}

// Values for the status line after a recording.
public struct RecordingStats
{
    public bool HasProgress;        // false when FFmpeg printed no stats line
    public long FramesCaptured, DuplicateFrames, DroppedFrames, FileBytes;
}

static class RecorderCore
{
    // 10 fps: safe up to about 4400 page px/s of scroll in an 892 px area (HANDOFF 6.8 rule, 1.5 x margin).
    // With "Changes only" a still screen still costs almost nothing. Bran decided on 2026-11-14.
    public const double DefaultFps = 10;
    public const double MinFps = 0.2;
    public const double MaxFps = 60;
    public static readonly int[] Crf = { 32, 27, 21 };

    public const string BackendDdagrab = "ddagrab";
    public const string BackendGdigrab = "gdigrab";
    public const string EncoderAuto = "auto";
    public const string EncoderNvenc = "h264_nvenc";
    public const string EncoderX264 = "libx264";
    public const string Chroma420 = "yuv420";
    public const string Chroma444 = "yuv444";
    public const string ModeFixed = "fixed";
    public const string ModeChanges = "changes";
    // Phase A (HANDOFF 6.5) used CQ 23 with -preset p4 -rc vbr.
    public const int DefaultNvencCq = 23;
    public const string OutputFolder = "folder";
    public const string OutputMp4 = "mp4";
    // First size limit: Claude scales an image with a long side above 1568 px down (docs/export-frames.md 2.2,
    // condition G, Bran 2026-11-19). Phase 1 only warns. Phase 4 cuts tiles.
    public const int DefaultSizeLimit = 1568;

    public static string NormalizeOutputMode(string v)
    {
        return v == OutputMp4 ? OutputMp4 : OutputFolder;
    }

    public static int ClampInt(int v, int min, int max)
    {
        return v < min ? min : (v > max ? max : v);
    }

    // The settled-frame options from the settings.
    public static SettledOptions SettledOptionsOf(StoredSettings s)
    {
        SettledOptions o = new SettledOptions();
        if (s == null) return o;
        o.SettleSeconds = ClampInt(s.SettleMs, 50, 10000) / 1000.0;
        o.StillIntervalSeconds = ClampInt(s.StillIntervalSeconds, 1, 600);
        o.ScrollFraction = ClampInt(s.ScrollPercent, 10, 95) / 100.0;
        return o;
    }

    public static string NormalizeBackend(string v)
    {
        return v == BackendGdigrab ? BackendGdigrab : BackendDdagrab;
    }

    public static string NormalizeEncoder(string v)
    {
        if (v == EncoderNvenc || v == EncoderX264) return v;
        return EncoderAuto;
    }

    public static string NormalizeChroma(string v)
    {
        return v == Chroma444 ? Chroma444 : Chroma420;
    }

    public static string NormalizeMode(string v)
    {
        return v == ModeChanges ? ModeChanges : ModeFixed;
    }

    // NVENC reads CQ 0 as "automatic", so the range starts at 1.
    public static int ClampNvencCq(int cq)
    {
        if (cq < 1) return 1;
        if (cq > 51) return 51;
        return cq;
    }

    // The CQ for one quality level. NvencCq is the "Balanced" value.
    // "Smallest" and "Sharpest" use the same steps as the x264 CRF ladder (32/27/21 = +5/-6),
    // so the three levels keep the spacing of the old app and of the Grok test (HANDOFF 3.1).
    // With the default 23 the result is 28 / 23 / 17. Phase B ran these three values (HANDOFF 6.8, B9, B3 and B11).
    // With one read for each run, the data did not show a quality effect. The decision about the
    // default is put off until more reads are done (HANDOFF 5, decision 11).
    public static int NvencCqForQuality(int baseCq, int quality)
    {
        int q = ClampQuality(quality);
        return ClampNvencCq(ClampNvencCq(baseCq) + Crf[q] - Crf[1]);
    }

    public static CaptureRegion MakeEven(CaptureRegion r)
    {
        return r.MakeEven();
    }

    static void NormalizeRegion(ref int x, ref int y, ref int w, ref int h)
    {
        if (w > 0 && h > 0) return;
        x = 0;
        y = 0;
        w = 0;
        h = 0;
    }

    public static CaptureRegion RegionOf(StoredSettings s)
    {
        if (s == null) return new CaptureRegion();
        return new CaptureRegion(s.RegionX, s.RegionY, s.RegionWidth, s.RegionHeight);
    }

    public static CaptureRegion LastRegionOf(StoredSettings s)
    {
        if (s == null) return new CaptureRegion();
        return new CaptureRegion(s.LastRegionX, s.LastRegionY, s.LastRegionWidth, s.LastRegionHeight);
    }

    public static double ClampFps(double fps)
    {
        // NaN and Infinity pass through Math.Min / Math.Max and then crash the settings form.
        if (double.IsNaN(fps) || double.IsInfinity(fps)) return DefaultFps;
        if (fps < MinFps) return MinFps;
        if (fps > MaxFps) return MaxFps;
        return fps;
    }

    public static int ClampQuality(int quality)
    {
        if (quality < 0) return 0;
        if (quality > 2) return 2;
        return quality;
    }

    public static int ClampSplitMinutes(int minutes)
    {
        if (minutes < 0) return 0;
        if (minutes > 1440) return 1440;
        return minutes;
    }

    // Virtual-key codes that match System.Windows.Forms.Keys for letters, digits and F1-F24.
    // Anything else is rejected. Enum.TryParse would accept "Enter", "13" or "Control".
    public static bool TryParseKey(string name, out int virtualKey)
    {
        virtualKey = 0;
        if (name == null) return false;
        name = name.Trim();
        if (name.Length == 0) return false;
        if (name.Length == 1)
        {
            char c = name[0];
            if (c >= 'a' && c <= 'z') c = (char)(c - 'a' + 'A');
            if (c >= 'A' && c <= 'Z') { virtualKey = c; return true; }
            if (c >= '0' && c <= '9') { virtualKey = c; return true; }
            return false;
        }
        if (name.Length == 2 && (name[0] == 'D' || name[0] == 'd') && name[1] >= '0' && name[1] <= '9')
        {
            virtualKey = name[1];
            return true;
        }
        if (name[0] == 'F' || name[0] == 'f')
        {
            int n;
            if (int.TryParse(name.Substring(1), NumberStyles.None, CultureInfo.InvariantCulture, out n)
                && n >= 1 && n <= 24)
            {
                virtualKey = 0x70 + (n - 1);
                return true;
            }
        }
        return false;
    }

    // Shift alone must not steal letters or digits. Shift+A still has to type a capital A.
    public static bool IsValidHotkey(int mods, int virtualKey)
    {
        if (mods == 0 || virtualKey == 0) return false;
        if ((mods & (1 | 2 | 8)) != 0) return true;
        return virtualKey >= 0x70 && virtualKey <= 0x70 + 23;
    }

    public static string KeyName(int virtualKey)
    {
        if (virtualKey >= 'A' && virtualKey <= 'Z') return ((char)virtualKey).ToString();
        if (virtualKey >= '0' && virtualKey <= '9') return ((char)virtualKey).ToString();
        if (virtualKey >= 0x70 && virtualKey <= 0x70 + 23)
            return "F" + (virtualKey - 0x70 + 1).ToString(CultureInfo.InvariantCulture);
        return virtualKey.ToString(CultureInfo.InvariantCulture);
    }

    // Leaves mods and key unchanged when text is not a usable hotkey.
    public static bool TrySetHotkey(string text, ref int mods, ref int key)
    {
        if (text == null) return false;
        int newMods = 0;
        int found = 0;
        bool sawKey = false;
        string[] parts = text.Split('+');
        for (int i = 0; i < parts.Length; i++)
        {
            string part = parts[i].Trim();
            if (part.Length == 0) return false;
            if (part.Equals("Win", StringComparison.OrdinalIgnoreCase)) newMods |= 8;
            else if (part.Equals("Ctrl", StringComparison.OrdinalIgnoreCase)) newMods |= 2;
            else if (part.Equals("Alt", StringComparison.OrdinalIgnoreCase)) newMods |= 1;
            else if (part.Equals("Shift", StringComparison.OrdinalIgnoreCase)) newMods |= 4;
            else
            {
                int vk;
                if (!TryParseKey(part, out vk) || sawKey) return false;
                found = vk;
                sawKey = true;
            }
        }
        if (!IsValidHotkey(newMods, found)) return false;
        mods = newMods;
        key = found;
        return true;
    }

    public static string HotkeyText(int mods, int key)
    {
        return ((mods & 8) != 0 ? "Win+" : "")
            + ((mods & 2) != 0 ? "Ctrl+" : "")
            + ((mods & 1) != 0 ? "Alt+" : "")
            + ((mods & 4) != 0 ? "Shift+" : "")
            + KeyName(key);
    }

    public static StoredSettings ParseSettings(string text, StoredSettings defaults)
    {
        StoredSettings s = CopySettings(defaults);
        if (text == null) text = "";
        string[] lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            if (line.Trim().Length == 0) continue;
            int eq = line.IndexOf('=');
            if (eq < 1)
            {
                s.ExtraLines.Add(line.Trim());
                continue;
            }
            string key = line.Substring(0, eq).Trim();
            string val = line.Substring(eq + 1).Trim();
            if (!ApplyKey(s, key, val)) s.ExtraLines.Add(key + "=" + val);
        }
        s.Fps = ClampFps(s.Fps);
        s.Quality = ClampQuality(s.Quality);
        s.SplitMinutes = ClampSplitMinutes(s.SplitMinutes);
        s.CaptureBackend = NormalizeBackend(s.CaptureBackend);
        s.Encoder = NormalizeEncoder(s.Encoder);
        s.ChromaFormat = NormalizeChroma(s.ChromaFormat);
        s.FrameRateMode = NormalizeMode(s.FrameRateMode);
        s.NvencCq = ClampNvencCq(s.NvencCq);
        s.OutputMode = NormalizeOutputMode(s.OutputMode);
        s.SettleMs = ClampInt(s.SettleMs, 50, 10000);
        s.StillIntervalSeconds = ClampInt(s.StillIntervalSeconds, 1, 600);
        s.ScrollPercent = ClampInt(s.ScrollPercent, 10, 95);
        s.SizeLimit = ClampInt(s.SizeLimit, 256, 16384);
        NormalizeRegion(ref s.RegionX, ref s.RegionY, ref s.RegionWidth, ref s.RegionHeight);
        NormalizeRegion(ref s.LastRegionX, ref s.LastRegionY, ref s.LastRegionWidth, ref s.LastRegionHeight);
        return s;
    }

    static bool ApplyKey(StoredSettings s, string key, string val)
    {
        double d;
        int i;
        if (key == "folder")
        {
            if (val.Length > 0 && val.IndexOf('\n') < 0 && val.IndexOf('\r') < 0) s.Folder = val;
            return true;
        }
        if (key == "fps")
        {
            if (double.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) s.Fps = d;
            return true;
        }
        if (key == "quality")
        {
            if (int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out i)) s.Quality = i;
            return true;
        }
        if (key == "split_minutes")
        {
            if (int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out i)) s.SplitMinutes = i;
            return true;
        }
        if (key == "record_on_launch") { s.RecordOnLaunch = val == "1"; return true; }
        if (key == "copy_to_clipboard") { s.CopyToClipboard = val == "1"; return true; }
        if (key == "play_sounds") { s.PlaySounds = val == "1"; return true; }
        if (key == "show_pointer") { s.ShowPointer = val == "1"; return true; }
        // A bad value keeps the value from before (the default for an old file).
        if (key == "capture_backend")
        {
            if (val == BackendDdagrab || val == BackendGdigrab) s.CaptureBackend = val;
            return true;
        }
        if (key == "encoder")
        {
            if (val == EncoderAuto || val == EncoderNvenc || val == EncoderX264) s.Encoder = val;
            return true;
        }
        if (key == "chroma")
        {
            if (val == Chroma420 || val == Chroma444) s.ChromaFormat = val;
            return true;
        }
        if (key == "frame_rate_mode")
        {
            if (val == ModeFixed || val == ModeChanges) s.FrameRateMode = val;
            return true;
        }
        if (key == "nvenc_cq")
        {
            if (int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out i)) s.NvencCq = i;
            return true;
        }
        if (key == "output_mode")
        {
            if (val == OutputFolder || val == OutputMp4) s.OutputMode = val;
            return true;
        }
        if (key == "settle_ms" || key == "still_interval_s" || key == "scroll_percent" || key == "size_limit")
        {
            if (int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out i))
            {
                if (key == "settle_ms") s.SettleMs = i;
                else if (key == "still_interval_s") s.StillIntervalSeconds = i;
                else if (key == "scroll_percent") s.ScrollPercent = i;
                else s.SizeLimit = i;
            }
            return true;
        }
        if (key == "selector_crosshair") { s.SelectorCrosshair = val == "1"; return true; }
        if (key == "selector_magnifier") { s.SelectorMagnifier = val == "1"; return true; }
        if (key == "selector_position") { s.SelectorPosition = val == "1"; return true; }
        if (key == "region")
        {
            TryParseRegion(val, ref s.RegionX, ref s.RegionY, ref s.RegionWidth, ref s.RegionHeight);
            return true;
        }
        if (key == "last_region")
        {
            TryParseRegion(val, ref s.LastRegionX, ref s.LastRegionY, ref s.LastRegionWidth, ref s.LastRegionHeight);
            return true;
        }
        if (key == "hotkey")
        {
            int mods = s.HotkeyModifiers;
            int hotkey = s.HotkeyKey;
            if (TrySetHotkey(val, ref mods, ref hotkey))
            {
                s.HotkeyModifiers = mods;
                s.HotkeyKey = hotkey;
            }
            return true;
        }
        // Removed on 2026-11-14 (Bran): the "record last region" hotkey and the separate stop hotkey.
        // The one hotkey starts and stops. Read and drop the old keys, so they do not stay in the file.
        if (key == "last_region_hotkey" || key == "stop_hotkey") return true;
        return false;
    }

    // "x,y,width,height". A bad value changes nothing.
    static bool TryParseRegion(string val, ref int x, ref int y, ref int w, ref int h)
    {
        string[] parts = (val ?? "").Split(',');
        if (parts.Length != 4) return false;
        int[] n = new int[4];
        for (int k = 0; k < 4; k++)
        {
            if (!int.TryParse(parts[k].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out n[k])) return false;
        }
        x = n[0];
        y = n[1];
        w = n[2];
        h = n[3];
        return true;
    }

    static string RegionText(int x, int y, int w, int h)
    {
        NormalizeRegion(ref x, ref y, ref w, ref h);
        return x.ToString(CultureInfo.InvariantCulture) + "," + y.ToString(CultureInfo.InvariantCulture) + ","
            + w.ToString(CultureInfo.InvariantCulture) + "," + h.ToString(CultureInfo.InvariantCulture);
    }

    public static string SerializeSettings(StoredSettings s)
    {
        if (s == null) s = new StoredSettings();
        StringBuilder sb = new StringBuilder();
        Append(sb, "folder", s.Folder ?? "");
        Append(sb, "fps", ClampFps(s.Fps).ToString("0.###", CultureInfo.InvariantCulture));
        Append(sb, "quality", ClampQuality(s.Quality).ToString(CultureInfo.InvariantCulture));
        Append(sb, "split_minutes", ClampSplitMinutes(s.SplitMinutes).ToString(CultureInfo.InvariantCulture));
        Append(sb, "record_on_launch", s.RecordOnLaunch ? "1" : "0");
        Append(sb, "copy_to_clipboard", s.CopyToClipboard ? "1" : "0");
        Append(sb, "play_sounds", s.PlaySounds ? "1" : "0");
        Append(sb, "show_pointer", s.ShowPointer ? "1" : "0");
        Append(sb, "hotkey", HotkeyText(s.HotkeyModifiers, s.HotkeyKey));
        Append(sb, "capture_backend", NormalizeBackend(s.CaptureBackend));
        Append(sb, "encoder", NormalizeEncoder(s.Encoder));
        Append(sb, "chroma", NormalizeChroma(s.ChromaFormat));
        Append(sb, "frame_rate_mode", NormalizeMode(s.FrameRateMode));
        Append(sb, "nvenc_cq", ClampNvencCq(s.NvencCq).ToString(CultureInfo.InvariantCulture));
        Append(sb, "output_mode", NormalizeOutputMode(s.OutputMode));
        Append(sb, "settle_ms", ClampInt(s.SettleMs, 50, 10000).ToString(CultureInfo.InvariantCulture));
        Append(sb, "still_interval_s", ClampInt(s.StillIntervalSeconds, 1, 600).ToString(CultureInfo.InvariantCulture));
        Append(sb, "scroll_percent", ClampInt(s.ScrollPercent, 10, 95).ToString(CultureInfo.InvariantCulture));
        Append(sb, "size_limit", ClampInt(s.SizeLimit, 256, 16384).ToString(CultureInfo.InvariantCulture));
        Append(sb, "selector_crosshair", s.SelectorCrosshair ? "1" : "0");
        Append(sb, "selector_magnifier", s.SelectorMagnifier ? "1" : "0");
        Append(sb, "selector_position", s.SelectorPosition ? "1" : "0");
        Append(sb, "region", RegionText(s.RegionX, s.RegionY, s.RegionWidth, s.RegionHeight));
        Append(sb, "last_region", RegionText(s.LastRegionX, s.LastRegionY, s.LastRegionWidth, s.LastRegionHeight));
        if (s.ExtraLines != null)
        {
            for (int i = 0; i < s.ExtraLines.Count; i++)
            {
                if (s.ExtraLines[i] == null) continue;
                string line = s.ExtraLines[i].Trim();
                if (line.Length == 0) continue;
                sb.Append(line);
                sb.Append("\r\n");
            }
        }
        return sb.ToString();
    }

    static void Append(StringBuilder sb, string key, string val)
    {
        sb.Append(key);
        sb.Append('=');
        sb.Append(val ?? "");
        sb.Append("\r\n");
    }

    static StoredSettings CopySettings(StoredSettings defaults)
    {
        StoredSettings s = new StoredSettings();
        if (defaults == null) return s;
        s.Folder = defaults.Folder;
        s.Fps = defaults.Fps;
        s.Quality = defaults.Quality;
        s.SplitMinutes = defaults.SplitMinutes;
        s.RecordOnLaunch = defaults.RecordOnLaunch;
        s.CopyToClipboard = defaults.CopyToClipboard;
        s.PlaySounds = defaults.PlaySounds;
        s.ShowPointer = defaults.ShowPointer;
        s.HotkeyModifiers = defaults.HotkeyModifiers;
        s.HotkeyKey = defaults.HotkeyKey;
        s.CaptureBackend = defaults.CaptureBackend;
        s.Encoder = defaults.Encoder;
        s.ChromaFormat = defaults.ChromaFormat;
        s.FrameRateMode = defaults.FrameRateMode;
        s.NvencCq = defaults.NvencCq;
        s.OutputMode = defaults.OutputMode;
        s.SettleMs = defaults.SettleMs;
        s.StillIntervalSeconds = defaults.StillIntervalSeconds;
        s.ScrollPercent = defaults.ScrollPercent;
        s.SizeLimit = defaults.SizeLimit;
        s.SelectorCrosshair = defaults.SelectorCrosshair;
        s.SelectorMagnifier = defaults.SelectorMagnifier;
        s.SelectorPosition = defaults.SelectorPosition;
        s.RegionX = defaults.RegionX;
        s.RegionY = defaults.RegionY;
        s.RegionWidth = defaults.RegionWidth;
        s.RegionHeight = defaults.RegionHeight;
        s.LastRegionX = defaults.LastRegionX;
        s.LastRegionY = defaults.LastRegionY;
        s.LastRegionWidth = defaults.LastRegionWidth;
        s.LastRegionHeight = defaults.LastRegionHeight;
        return s;
    }

    public static void AtomicWrite(string path, string text)
    {
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, text ?? "", new UTF8Encoding(false));
        if (!File.Exists(path))
        {
            File.Move(tmp, path);
            return;
        }
        try
        {
            File.Replace(tmp, path, null);
        }
        catch (Exception)
        {
            File.Copy(tmp, path, true);
            try { File.Delete(tmp); } catch (Exception) { }
        }
    }

    public static string FindFfmpeg(string exeDirectory, string pathVariable, bool windows)
    {
        string fileName = windows ? "ffmpeg.exe" : "ffmpeg";
        if (exeDirectory != null && exeDirectory.Length > 0)
        {
            try
            {
                string local = Path.Combine(exeDirectory, fileName);
                if (File.Exists(local)) return local;
            }
            catch (Exception) { }
        }
        if (windows)
        {
            try
            {
                string winget = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Microsoft", "WinGet", "Links", "ffmpeg.exe");
                if (File.Exists(winget)) return winget;
            }
            catch (Exception) { }
        }
        if (pathVariable == null) return null;
        char sep = windows ? ';' : ':';
        string[] dirs = pathVariable.Split(sep);
        for (int i = 0; i < dirs.Length; i++)
        {
            string dir = dirs[i].Trim().Trim('"');
            if (dir.Length == 0) continue;
            try
            {
                string candidate = Path.Combine(dir, fileName);
                if (File.Exists(candidate)) return candidate;
            }
            catch (Exception) { }
        }
        return null;
    }

    // "auto" = NVENC when the probe found it, else libx264 (HANDOFF 6.5 fallback order).
    // An explicit "h264_nvenc" is kept even when the probe failed: the user asked for it,
    // and FFmpeg then reports the real error.
    // NVENC H.264 frame size limits. Maximum: 4096 x 4096 on NVIDIA GPUs. Minimum: about 145 x 49
    // (NV_ENC_CAPS_WIDTH_MIN / HEIGHT_MIN), rounded up to even sizes. FFmpeg stops with an error outside them.
    // UNVERIFIED: the exact limits of Bran's GPU (the minimum changes with the GPU).
    public const int NvencMaxSize = 4096;
    public const int NvencMinWidth = 146;
    public const int NvencMinHeight = 50;

    public static bool NvencCanEncodeSize(int width, int height)
    {
        return width >= NvencMinWidth && height >= NvencMinHeight
            && width <= NvencMaxSize && height <= NvencMaxSize;
    }

    // Finds the ddagrab output_idx for one monitor (Screen.DeviceName and Screen.Bounds, physical px).
    // outputs: the DXGI outputs of adapter 0 (NativeMethods.GetDxgiOutputs), or null when DXGI failed.
    // Match by device name first, then by the desktop rectangle. Returns -1 when no output matches:
    // the monitor is not on the adapter that ddagrab uses, so ddagrab cannot record it.
    // With no DXGI data, a PC with one monitor still gets 0 (ddagrab output 0 is that monitor).
    public static int MatchDxgiOutput(string deviceName, int x, int y, int width, int height,
        IList<DxgiOutputInfo> outputs, int screenCount)
    {
        if (outputs == null) return screenCount == 1 ? 0 : -1;
        if (deviceName != null && deviceName.Length > 0)
        {
            for (int i = 0; i < outputs.Count; i++)
            {
                if (string.Equals(outputs[i].DeviceName, deviceName, StringComparison.OrdinalIgnoreCase))
                    return outputs[i].Index;
            }
        }
        for (int i = 0; i < outputs.Count; i++)
        {
            DxgiOutputInfo o = outputs[i];
            if (o.X == x && o.Y == y && o.Width == width && o.Height == height) return o.Index;
        }
        return -1;
    }

    // What to change when FFmpeg stops by itself soon after the start (HANDOFF 6.5 fallback order).
    // An NVENC error with Encoder=auto: use libx264. Any error with ddagrab: use gdigrab.
    // Any other error with gdigrab and NVENC from "auto": use libx264.
    // Returns false when no fallback is left. reason is a short text for the balloon.
    public static bool ChooseFallback(CapturePlan plan, string encoderSetting, string ffmpegError,
        out bool useGdigrab, out bool useX264, out string reason)
    {
        useGdigrab = false;
        useX264 = false;
        reason = "";
        if (plan == null) return false;
        bool auto = NormalizeEncoder(encoderSetting) == EncoderAuto;
        bool nvenc = plan.Encoder == EncoderNvenc;
        if (nvenc && auto && LooksLikeNvencError(ffmpegError))
        {
            useX264 = true;
            reason = "NVENC failed. Recording again with libx264.";
            return true;
        }
        if (plan.Backend == BackendDdagrab)
        {
            useGdigrab = true;
            reason = "ddagrab failed. Recording again with gdigrab.";
            return true;
        }
        if (nvenc && auto)
        {
            useX264 = true;
            reason = "FFmpeg failed with NVENC. Recording again with libx264.";
            return true;
        }
        return false;
    }

    public static bool LooksLikeNvencError(string text)
    {
        if (text == null) return false;
        string t = text.ToLowerInvariant();
        string[] marks = { "nvenc", "nvcuda", "cuda", "nvencodeapi", "no capable devices", "openencodesessionex" };
        for (int i = 0; i < marks.Length; i++) if (t.IndexOf(marks[i], StringComparison.Ordinal) >= 0) return true;
        return false;
    }

    public static string ResolveEncoder(string setting, bool nvencAvailable)
    {
        string e = NormalizeEncoder(setting);
        if (e == EncoderX264) return EncoderX264;
        if (e == EncoderNvenc) return EncoderNvenc;
        return nvencAvailable ? EncoderNvenc : EncoderX264;
    }

    // Decides the backend, the monitor and the offsets. Pure: the caller measures the monitors.
    // ddagrab captures one DXGI output only. Two cases cannot use it:
    //  - a region that is not fully inside one monitor;
    //  - no region (whole desktop) on a PC with more than one monitor (or an empty monitor list).
    // In both cases the plan FALLS BACK to gdigrab (not an error), and Note says why.
    // Reason: HANDOFF 6.5 names gdigrab as the fallback backend, and a recording that starts
    // is better than an error after the hotkey. The UI can show Note in the status line.
    // After the geometry, PlanCapture checks the NVENC size limits. Outside them it uses libx264,
    // also for an explicit "h264_nvenc" (NVENC cannot encode that size), and Note says why.
    public static CapturePlan PlanCapture(StoredSettings s, CaptureRegion region,
        IList<MonitorBounds> monitors, bool nvencAvailable)
    {
        CapturePlan plan = PlanGeometry(s, region, monitors, nvencAvailable);
        if (plan.Encoder != EncoderNvenc) return plan;
        int w = plan.Width, h = plan.Height;
        if (plan.WholeDesktop)
        {
            // gdigrab records the bounding box of all monitors (cropped to even sizes).
            int monitorCount = monitors == null ? 0 : monitors.Count;
            if (monitorCount == 0) return plan;
            long left = long.MaxValue, top = long.MaxValue, right = long.MinValue, bottom = long.MinValue;
            for (int i = 0; i < monitorCount; i++)
            {
                MonitorBounds m = monitors[i];
                left = Math.Min(left, m.X);
                top = Math.Min(top, m.Y);
                right = Math.Max(right, (long)m.X + m.Width);
                bottom = Math.Max(bottom, (long)m.Y + m.Height);
            }
            w = (int)Math.Min(int.MaxValue, right - left) & ~1;
            h = (int)Math.Min(int.MaxValue, bottom - top) & ~1;
        }
        if (NvencCanEncodeSize(w, h)) return plan;
        plan.Encoder = EncoderX264;
        string size = w.ToString(CultureInfo.InvariantCulture) + "x" + h.ToString(CultureInfo.InvariantCulture);
        string why = (w > NvencMaxSize || h > NvencMaxSize)
            ? "NVENC cannot encode " + size + " (maximum " + NvencMaxSize.ToString(CultureInfo.InvariantCulture) + " px). libx264 records it."
            : "NVENC cannot encode " + size + " (too small). libx264 records it.";
        plan.Note = plan.Note.Length > 0 ? plan.Note + " " + why : why;
        return plan;
    }

    static CapturePlan PlanGeometry(StoredSettings s, CaptureRegion region,
        IList<MonitorBounds> monitors, bool nvencAvailable)
    {
        if (s == null) s = new StoredSettings();
        CapturePlan plan = new CapturePlan();
        plan.Encoder = ResolveEncoder(s.Encoder, nvencAvailable);
        CaptureRegion r = region.MakeEven();
        int monitorCount = monitors == null ? 0 : monitors.Count;

        if (NormalizeBackend(s.CaptureBackend) == BackendDdagrab)
        {
            if (r.IsEmpty)
            {
                if (monitorCount == 1 && monitors[0].Width > 1 && monitors[0].Height > 1 && monitors[0].Index >= 0)
                {
                    MonitorBounds m = monitors[0];
                    plan.Backend = BackendDdagrab;
                    plan.OutputIndex = m.Index;
                    plan.Width = m.Width & ~1;
                    plan.Height = m.Height & ~1;
                    return plan;
                }
                plan.Note = monitorCount == 0
                    ? "No monitor data. gdigrab records the whole desktop."
                    : monitorCount == 1
                        ? "ddagrab cannot find this monitor. gdigrab records the whole desktop."
                        : "ddagrab records one monitor only. gdigrab records the whole desktop.";
            }
            else
            {
                bool notOnDdagrabGpu = false;
                for (int i = 0; i < monitorCount; i++)
                {
                    MonitorBounds m = monitors[i];
                    if (!m.Contains(r)) continue;
                    if (m.Index < 0)
                    {
                        notOnDdagrabGpu = true;
                        break;
                    }
                    plan.Backend = BackendDdagrab;
                    plan.OutputIndex = m.Index;
                    plan.OffsetX = r.X - m.X;
                    plan.OffsetY = r.Y - m.Y;
                    plan.Width = r.Width;
                    plan.Height = r.Height;
                    return plan;
                }
                plan.Note = notOnDdagrabGpu
                    ? "ddagrab cannot record this monitor (no DXGI output on its GPU). gdigrab records it."
                    : "The region is not inside one monitor. gdigrab records it.";
            }
        }

        plan.Backend = BackendGdigrab;
        if (r.IsEmpty)
        {
            plan.WholeDesktop = true;
            return plan;
        }
        plan.OffsetX = r.X;
        plan.OffsetY = r.Y;
        plan.Width = r.Width;
        plan.Height = r.Height;
        return plan;
    }

    // Old entry point, kept so the current UI compiles and behaves as before:
    // gdigrab and libx264 (now preset veryfast, see HANDOFF 6.5), region from the settings
    // (none = whole desktop). It does not use ddagrab or NVENC, because it has no monitor
    // list and no NVENC probe.
    public static string[] BuildWindowsArguments(StoredSettings s, DateTime now)
    {
        StoredSettings c = CopySettings(s ?? new StoredSettings());
        c.ExtraLines = new List<string>();
        c.CaptureBackend = BackendGdigrab;
        c.Encoder = EncoderX264;
        c.FrameRateMode = ModeFixed;   // as before: the default changed to "changes" after Phase B
        return BuildCaptureArguments(c, RegionOf(c), null, now, false);
    }

    // Overload with the signature in the task. It assumes that NVENC is available for "auto".
    // Prefer the overload with nvencAvailable, after a call to HasNvencEncoder / TestNvencEncode.
    public static string[] BuildCaptureArguments(StoredSettings s, CaptureRegion region,
        IList<MonitorBounds> monitors, DateTime now)
    {
        return BuildCaptureArguments(s, region, monitors, now, true);
    }

    public static string[] BuildCaptureArguments(StoredSettings s, CaptureRegion region,
        IList<MonitorBounds> monitors, DateTime now, bool nvencAvailable)
    {
        return BuildCaptureArguments(s, region, monitors, now, nvencAvailable, null);
    }

    // workFile != null: folder mode. FFmpeg records a lossless 4:4:4 work file to that path, with no split.
    // The pass after the recording (AfterRecording.cs) makes clip.mp4 and the frames from it.
    // Reason for lossless: NVENC keeps refining a still picture (HANDOFF 6.6), so lossy frames after a scroll
    // look like changes. A lossless file has no such noise.
    public static string[] BuildCaptureArguments(StoredSettings s, CaptureRegion region,
        IList<MonitorBounds> monitors, DateTime now, bool nvencAvailable, string workFile)
    {
        if (s == null) s = new StoredSettings();
        bool lossless = workFile != null;
        CapturePlan plan = PlanCapture(s, region, monitors, nvencAvailable);
        double fps = ClampFps(s.Fps);
        int quality = ClampQuality(s.Quality);
        int split = ClampSplitMinutes(s.SplitMinutes);
        bool nvenc = plan.Encoder == EncoderNvenc;
        bool chroma444 = lossless || NormalizeChroma(s.ChromaFormat) == Chroma444;
        bool changes = NormalizeMode(s.FrameRateMode) == ModeChanges;
        string pixFmt = chroma444 ? "yuv444p" : "yuv420p";
        string fpsText = fps.ToString("0.###", CultureInfo.InvariantCulture);
        int gop = Math.Max(2, (int)Math.Round(fps * 30));
        int lookahead = Math.Max(0, Math.Min(40, (int)Math.Round(fps * 2)));
        int threads = Math.Max(1, Math.Min(16, (int)Math.Ceiling(fps)));
        string stamp = now.ToString("yyyy-MM-dd_HH-mm-ss_fff", CultureInfo.InvariantCulture);
        string folder = s.Folder ?? "";
        string mouse = s.ShowPointer ? "1" : "0";
        List<string> a = new List<string>();
        a.Add("-hide_banner");
        a.Add("-loglevel");
        a.Add("error");
        // With -loglevel error, FFmpeg still writes the stats line (frame=, dup=, drop=) to stderr.
        // RecordingSession parses it for the status line. Checked with this FFmpeg build.
        a.Add("-stats");
        a.Add("-y");

        // Filters. Never scale down: no filter here changes the size, except the crop to even
        // sizes for the whole desktop (at most 1 px on the right or bottom edge).
        List<string> vf = new List<string>();
        if (plan.Backend == BackendDdagrab)
        {
            a.Add("-f");
            a.Add("lavfi");
            a.Add("-i");
            a.Add("ddagrab=output_idx=" + plan.OutputIndex.ToString(CultureInfo.InvariantCulture)
                + ":framerate=" + fpsText
                + ":draw_mouse=" + mouse
                + ":offset_x=" + plan.OffsetX.ToString(CultureInfo.InvariantCulture)
                + ":offset_y=" + plan.OffsetY.ToString(CultureInfo.InvariantCulture)
                + ":video_size=" + plan.Width.ToString(CultureInfo.InvariantCulture)
                + "x" + plan.Height.ToString(CultureInfo.InvariantCulture));
            // ddagrab gives D3D11 frames in GPU memory. h264_nvenc reads them directly
            // (Phase A run A4 had no download filter). libx264 and mpdecimate are CPU filters,
            // so only these two cases copy the frames to the CPU first.
            if (!nvenc || changes) vf.Add("hwdownload,format=bgra");
        }
        else
        {
            a.Add("-f");
            a.Add("gdigrab");
            a.Add("-framerate");
            a.Add(fpsText);
            a.Add("-draw_mouse");
            a.Add(mouse);
            if (!plan.WholeDesktop)
            {
                a.Add("-offset_x");
                a.Add(plan.OffsetX.ToString(CultureInfo.InvariantCulture));
                a.Add("-offset_y");
                a.Add(plan.OffsetY.ToString(CultureInfo.InvariantCulture));
                a.Add("-video_size");
                a.Add(plan.Width.ToString(CultureInfo.InvariantCulture) + "x" + plan.Height.ToString(CultureInfo.InvariantCulture));
            }
            a.Add("-i");
            a.Add("desktop");
            if (plan.WholeDesktop) vf.Add("crop=trunc(iw/2)*2:trunc(ih/2)*2:0:0");
        }
        if (changes)
        {
            // mpdecimate does not accept BGRA. Without an explicit format, FFmpeg picks yuva444p,
            // which NVENC cannot read (checked with -v verbose on this FFmpeg build).
            // Convert once to the target chroma, then drop the frames that do not change.
            vf.Add("format=" + pixFmt);
            // max = the longest run of dropped frames. Without it, a still screen at the end of a
            // recording is lost: a 10 s recording of a still page gave a 0.1 s file (checked on
            // Bran's PC). With max = ceil(fps) - 1, the file keeps one frame each second at least,
            // so the length of the file is correct to 1 s.
            int maxDrop = Math.Max(1, (int)Math.Ceiling(fps) - 1);
            // hi=256, lo=128 (defaults 768, 320): the defaults dropped one-character look-alike changes
            // (x.y to x,y, a:b to a;b, a'b to a`b) when the change stayed less than 1 s, and caught a longer
            // change up to 0.7 s late. The lower values caught 15 of 15 changes at once, with the same frame
            // count on a still page (test/lookalike.py, HANDOFF 6.10).
            vf.Add("mpdecimate=hi=256:lo=128:frac=0.33:max=" + maxDrop.ToString(CultureInfo.InvariantCulture));
        }
        if (vf.Count > 0)
        {
            a.Add("-vf");
            a.Add(string.Join(",", vf.ToArray()));
        }
        if (changes)
        {
            // Keep the real time of each kept frame. Without it, FFmpeg copies frames to fill the gaps.
            a.Add("-fps_mode");
            a.Add("vfr");
        }

        if (nvenc && lossless)
        {
            a.Add("-c:v");
            a.Add(EncoderNvenc);
            a.Add("-preset");
            a.Add("p4");
            a.Add("-tune");
            a.Add("lossless");
            a.Add("-profile:v");
            a.Add("high444p");
            // RGB input (gdigrab, or ddagrab frames on the GPU): keep 4:4:4. In "changes" mode the input is yuv444p.
            if (!changes)
            {
                a.Add("-rgb_mode");
                a.Add("yuv444");
            }
            a.Add("-g");
            a.Add(gop.ToString(CultureInfo.InvariantCulture));
        }
        else if (lossless)
        {
            a.Add("-c:v");
            a.Add(EncoderX264);
            a.Add("-preset");
            a.Add("ultrafast");
            a.Add("-qp");
            a.Add("0");
            a.Add("-threads");
            a.Add(threads.ToString(CultureInfo.InvariantCulture));
            a.Add("-g");
            a.Add(gop.ToString(CultureInfo.InvariantCulture));
            a.Add("-pix_fmt");
            a.Add("yuv444p");
        }
        else if (nvenc)
        {
            // Phase A settings (HANDOFF 6.5). -b:v 0 lets the CQ value control the size.
            a.Add("-c:v");
            a.Add(EncoderNvenc);
            a.Add("-preset");
            a.Add("p4");
            a.Add("-rc");
            a.Add("vbr");
            a.Add("-cq");
            a.Add(NvencCqForQuality(s.NvencCq, quality).ToString(CultureInfo.InvariantCulture));
            a.Add("-b:v");
            a.Add("0");
            if (chroma444)
            {
                a.Add("-profile:v");
                a.Add("high444p");
                // RGB input (gdigrab, or ddagrab frames that stay on the GPU): NVENC converts
                // RGB to YUV itself, and its default is 4:2:0. rgb_mode=yuv444 keeps 4:4:4 on the GPU,
                // so no hwdownload is necessary. In "changes" mode the input is yuv444p already.
                // Checked on Bran's PC (HANDOFF 6.7, runs dda444 and gdi444, fixed fps): this encode
                // gave High 4:4:4 / yuv444p and lost no frames. Phase B run B8 (HANDOFF 6.8) also used it.
                // Not checked: 4:4:4 in "changes" mode (the path without -rgb_mode). No run in 6.7 or 6.8 used it.
                if (!changes)
                {
                    a.Add("-rgb_mode");
                    a.Add("yuv444");
                }
            }
            a.Add("-g");
            a.Add(gop.ToString(CultureInfo.InvariantCulture));
            // No -pix_fmt: with D3D11 frames, a format conversion would need a CPU copy.
        }
        else
        {
            // Old app settings, with preset veryfast: Phase A (A1 against A2) showed that
            // medium gives no better capture rate.
            a.Add("-c:v");
            a.Add(EncoderX264);
            a.Add("-preset");
            a.Add("veryfast");
            a.Add("-tune");
            a.Add("stillimage");
            a.Add("-crf");
            a.Add(Crf[quality].ToString(CultureInfo.InvariantCulture));
            a.Add("-x264-params");
            a.Add("rc-lookahead=" + lookahead.ToString(CultureInfo.InvariantCulture));
            a.Add("-threads");
            a.Add(threads.ToString(CultureInfo.InvariantCulture));
            a.Add("-g");
            a.Add(gop.ToString(CultureInfo.InvariantCulture));
            // yuv444p makes x264 use the High 4:4:4 Predictive profile by itself.
            a.Add("-pix_fmt");
            a.Add(pixFmt);
        }
        if (split > 0 && !lossless)
        {
            // The segment muxer only cuts on a keyframe. Force one at each boundary
            // so "every N minutes" is the real cut, not "N minutes plus up to one GOP".
            int seconds = split * 60;
            a.Add("-force_key_frames");
            a.Add("expr:gte(t,n_forced*" + seconds.ToString(CultureInfo.InvariantCulture) + ")");
            if (nvenc)
            {
                // NVENC makes a forced keyframe a non-IDR I frame by default. A new segment
                // must start on an IDR frame to play alone.
                a.Add("-forced-idr");
                a.Add("1");
            }
            a.Add("-f");
            a.Add("segment");
            a.Add("-segment_time");
            a.Add(seconds.ToString(CultureInfo.InvariantCulture));
            a.Add("-segment_format");
            a.Add("mp4");
            a.Add("-segment_format_options");
            a.Add("movflags=+frag_keyframe+empty_moov:frag_duration=2000000:flush_packets=1");
            a.Add("-reset_timestamps");
            a.Add("1");
            // %03d is the segment index. Percent signs in the folder must be doubled
            // or the muxer treats them as its own format tokens.
            a.Add(CombinePath(folder.Replace("%", "%%"), "desktop_" + stamp + "_%03d.mp4", true));
        }
        else
        {
            a.Add("-movflags");
            a.Add("+frag_keyframe+empty_moov");
            a.Add("-frag_duration");
            a.Add("2000000");
            a.Add("-flush_packets");
            a.Add("1");
            a.Add(lossless ? workFile : CombinePath(folder, "desktop_" + stamp + ".mp4", true));
        }
        return a.ToArray();
    }

    // Name of the recording folder in folder mode (brief section 6): desktop_yyyy-MM-dd_HH-mm-ss.
    public static string RecordingFolderName(DateTime start)
    {
        return "desktop_" + start.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture);
    }

    // The FFmpeg arguments that make clip.mp4 from the lossless work file.
    // keyframeTimes != null: a keyframe at each time and no other keyframe (condition C, Bran 2026-11-19):
    // -copyts keeps the time line of the work file, so the times in index.md are the times in clip.mp4.
    // -force_key_frames gets each time minus 0.01 s, so a rounded time cannot miss its frame.
    // -g 999999 stops the periodic keyframes. -forced-idr 1 makes each forced keyframe an IDR frame.
    // x264 also needs scenecut=0, or it adds keyframes at scene cuts.
    // keyframeTimes == null: a normal clip (fallback when the pass fails, condition D).
    public static string[] BuildClipArguments(StoredSettings s, string workFile, string clipFile,
        IList<double> keyframeTimes, double fps, bool nvencAvailable)
    {
        if (s == null) s = new StoredSettings();
        string encoder = ResolveEncoder(s.Encoder, nvencAvailable);
        bool nvenc = encoder == EncoderNvenc;
        bool chroma444 = NormalizeChroma(s.ChromaFormat) == Chroma444;
        int quality = ClampQuality(s.Quality);
        bool keys = keyframeTimes != null;
        int gop = keys ? 999999 : Math.Max(2, (int)Math.Round(ClampFps(fps) * 30));
        List<string> a = new List<string>();
        a.Add("-hide_banner");
        a.Add("-loglevel");
        a.Add("error");
        a.Add("-y");
        a.Add("-copyts");
        a.Add("-i");
        a.Add(workFile);
        a.Add("-map");
        a.Add("0:v:0");
        a.Add("-fps_mode");
        a.Add("passthrough");
        a.Add("-c:v");
        a.Add(encoder);
        if (nvenc)
        {
            a.Add("-preset");
            a.Add("p4");
            a.Add("-rc");
            a.Add("vbr");
            a.Add("-cq");
            a.Add(NvencCqForQuality(s.NvencCq, quality).ToString(CultureInfo.InvariantCulture));
            a.Add("-b:v");
            a.Add("0");
            if (chroma444)
            {
                a.Add("-profile:v");
                a.Add("high444p");
            }
        }
        else
        {
            a.Add("-preset");
            a.Add("veryfast");
            a.Add("-tune");
            a.Add("stillimage");
            a.Add("-crf");
            a.Add(Crf[quality].ToString(CultureInfo.InvariantCulture));
            if (keys)
            {
                a.Add("-x264-params");
                a.Add("scenecut=0");
            }
        }
        a.Add("-pix_fmt");
        a.Add(chroma444 ? "yuv444p" : "yuv420p");
        a.Add("-g");
        a.Add(gop.ToString(CultureInfo.InvariantCulture));
        if (keys && keyframeTimes.Count > 0)
        {
            if (nvenc)
            {
                a.Add("-forced-idr");
                a.Add("1");
            }
            a.Add("-force_key_frames");
            a.Add(KeyframeList(keyframeTimes));
        }
        a.Add("-movflags");
        a.Add("+faststart");
        a.Add(clipFile);
        return a.ToArray();
    }

    // "0.59,3.21,..." Each time minus 0.01 s, never below 0, sorted, no copies.
    public static string KeyframeList(IList<double> times)
    {
        List<double> list = new List<double>();
        foreach (double t in times) list.Add(Math.Max(0, t - 0.01));
        list.Sort();
        StringBuilder sb = new StringBuilder();
        string last = null;
        foreach (double t in list)
        {
            string v = t.ToString("0.000", CultureInfo.InvariantCulture);
            if (v == last) continue;
            if (sb.Length > 0) sb.Append(',');
            sb.Append(v);
            last = v;
        }
        return sb.ToString();
    }

    // Reads the output of "ffmpeg -hide_banner -encoders". A line looks like:
    //  " V....D h264_nvenc           NVIDIA NVENC H.264 encoder (codec h264)"
    // This proves only that the FFmpeg build has the encoder, not that a GPU can run it.
    public static bool HasNvencEncoder(string encodersText)
    {
        if (encodersText == null) return false;
        string[] lines = encodersText.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        char[] space = { ' ', '\t' };
        for (int i = 0; i < lines.Length; i++)
        {
            string[] parts = lines[i].Split(space, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && parts[0].Length > 0 && parts[0][0] == 'V' && parts[1] == EncoderNvenc) return true;
        }
        return false;
    }

    // Runs "ffmpeg -encoders" and reads it. Not called by the unit tests.
    public static bool ProbeNvencListed(string ffmpegPath, int timeoutMs)
    {
        string output;
        int exit;
        if (!RunFfmpeg(ffmpegPath, "-hide_banner -encoders", timeoutMs, out output, out exit)) return false;
        return HasNvencEncoder(output);
    }

    // Encodes 5 small black frames with h264_nvenc and discards them. Exit code 0 means
    // that the driver and the GPU can encode. It does not capture the screen.
    // Recorder.IsNvencUsable calls it once for each ffmpeg path ("auto" encoder). Not called by the unit tests.
    public static bool TestNvencEncode(string ffmpegPath, int timeoutMs)
    {
        string output;
        int exit;
        string args = "-hide_banner -loglevel error -f lavfi -i color=c=black:s=256x256:r=10 -frames:v 5 -c:v h264_nvenc -f null -";
        if (!RunFfmpeg(ffmpegPath, args, timeoutMs, out output, out exit)) return false;
        return exit == 0;
    }

    internal static bool RunFfmpeg(string ffmpegPath, string args, int timeoutMs, out string output, out int exitCode)
    {
        output = "";
        exitCode = -1;
        if (ffmpegPath == null || ffmpegPath.Length == 0) return false;
        try
        {
            using (System.Diagnostics.Process p = new System.Diagnostics.Process())
            {
                p.StartInfo = new System.Diagnostics.ProcessStartInfo(ffmpegPath, args);
                p.StartInfo.UseShellExecute = false;
                p.StartInfo.CreateNoWindow = true;
                p.StartInfo.RedirectStandardInput = true;
                p.StartInfo.RedirectStandardOutput = true;
                p.StartInfo.RedirectStandardError = true;
                // Read both pipes with events (.NET 4), so a full pipe cannot block the wait below.
                StringBuilder text = new StringBuilder();
                System.Diagnostics.DataReceivedEventHandler collect = delegate(object sender, System.Diagnostics.DataReceivedEventArgs e)
                {
                    if (e.Data == null) return;
                    lock (text) { if (text.Length < 200000) text.AppendLine(e.Data); }
                };
                p.OutputDataReceived += collect;
                p.ErrorDataReceived += collect;
                p.Start();
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                p.StandardInput.Close();
                if (!p.WaitForExit(Math.Max(1000, timeoutMs)))
                {
                    try { p.Kill(); } catch (Exception) { }
                    return false;
                }
                // The overload without a timeout also waits for the end of the redirected pipes.
                p.WaitForExit();
                lock (text) { output = text.ToString(); }
                exitCode = p.ExitCode;
                return true;
            }
        }
        catch (Exception)
        {
            return false;
        }
    }

    // Reads one FFmpeg stats line, for example:
    //  "frame=  100 fps= 10 q=23.0 size=     256KiB time=00:00:10.00 bitrate= 209.7kbits/s dup=3 drop=1 speed=1x"
    // The values are cumulative. This FFmpeg build omits dup= and drop= when they are 0,
    // so a line with frame= and no dup= means 0 duplicates at that time.
    // Returns false when the line is not a stats line.
    public static bool TryParseProgress(string line, out FfmpegProgress progress)
    {
        progress = new FfmpegProgress();
        progress.TimeSeconds = -1;
        if (line == null) return false;
        string t = line.Trim();
        if (!t.StartsWith("frame=", StringComparison.Ordinal)) return false;
        bool sawFrame = false;
        int i = 0;
        while (i < t.Length)
        {
            int eq = t.IndexOf('=', i);
            if (eq < 0) break;
            int keyStart = eq;
            while (keyStart > i && t[keyStart - 1] != ' ') keyStart--;
            string key = t.Substring(keyStart, eq - keyStart);
            int v = eq + 1;
            while (v < t.Length && t[v] == ' ') v++;
            int end = v;
            while (end < t.Length && t[end] != ' ') end++;
            string val = t.Substring(v, end - v);
            long n;
            bool isNumber = long.TryParse(val, NumberStyles.None, CultureInfo.InvariantCulture, out n);
            if (key == "frame")
            {
                if (!isNumber) return false;
                progress.Frames = n;
                sawFrame = true;
            }
            else if (key == "dup" && isNumber) progress.Duplicates = n;
            else if (key == "time") progress.TimeSeconds = ParseClockTime(val);
            else if (key == "drop" && isNumber) progress.Dropped = n;
            i = end;
        }
        return sawFrame;
    }

    // "00:01:02.50" -> 62.5. Returns -1 for "N/A" or a bad value.
    public static double ParseClockTime(string val)
    {
        if (val == null) return -1;
        string[] p = val.Split(':');
        if (p.Length != 3) return -1;
        int h, m;
        double s;
        if (!int.TryParse(p[0].TrimStart('-'), NumberStyles.Integer, CultureInfo.InvariantCulture, out h)
            || !int.TryParse(p[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out m)
            || !double.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out s)) return -1;
        double t = h * 3600 + m * 60 + s;
        return val.StartsWith("-", StringComparison.Ordinal) ? -t : t;
    }

    // The status line after a recording.
    public static string FormatStats(RecordingStats stats)
    {
        string size = (stats.FileBytes / (1024.0 * 1024.0)).ToString("0.00", CultureInfo.InvariantCulture) + " MB";
        if (!stats.HasProgress) return "Frames: unknown. File size: " + size + ".";
        // FFmpeg frame= counts all frames in the file, with the duplicates that FFmpeg added when the
        // capture was late. Real frames = frame - dup. ddagrab can also repeat a frame itself when the
        // screen does not change; FFmpeg does not count these as dup.
        long real = Math.Max(0, stats.FramesCaptured - stats.DuplicateFrames);
        return "Real frames: " + real.ToString(CultureInfo.InvariantCulture)
            + " of " + stats.FramesCaptured.ToString(CultureInfo.InvariantCulture)
            + ". Duplicates: " + stats.DuplicateFrames.ToString(CultureInfo.InvariantCulture)
            + ". Dropped: " + stats.DroppedFrames.ToString(CultureInfo.InvariantCulture)
            + ". File size: " + size + ".";
    }

    public static string CombinePath(string folder, string file, bool windows)
    {
        if (folder == null || folder.Length == 0) return file ?? "";
        char last = folder[folder.Length - 1];
        if (last == '\\' || last == '/') return folder + (file ?? "");
        return folder + (windows ? '\\' : '/') + (file ?? "");
    }

    public static string JoinWindowsArguments(string[] args)
    {
        StringBuilder sb = new StringBuilder();
        if (args == null) return "";
        for (int i = 0; i < args.Length; i++)
        {
            if (i > 0) sb.Append(' ');
            sb.Append(QuoteWindowsArgument(args[i] ?? ""));
        }
        return sb.ToString();
    }

    // CommandLineToArgvW quoting. A trailing backslash must not escape the closing quote.
    public static string QuoteWindowsArgument(string arg)
    {
        if (arg == null) arg = "";
        bool need = arg.Length == 0;
        for (int i = 0; i < arg.Length && !need; i++)
        {
            char c = arg[i];
            if (c == ' ' || c == '\t' || c == '\n' || c == '\v' || c == '"') need = true;
        }
        if (!need) return arg;
        StringBuilder sb = new StringBuilder();
        sb.Append('"');
        int slashes = 0;
        for (int i = 0; i < arg.Length; i++)
        {
            char c = arg[i];
            if (c == '\\') slashes++;
            else if (c == '"')
            {
                for (int k = 0; k < slashes * 2 + 1; k++) sb.Append('\\');
                sb.Append('"');
                slashes = 0;
            }
            else
            {
                for (int k = 0; k < slashes; k++) sb.Append('\\');
                sb.Append(c);
                slashes = 0;
            }
        }
        for (int k = 0; k < slashes * 2; k++) sb.Append('\\');
        sb.Append('"');
        return sb.ToString();
    }

    public static byte[] BuildChimeWav(double firstHz, double secondHz)
    {
        const int rate = 22050;
        int per = rate * 9 / 100;   // 90 ms for each tone
        int count = per * 2;
        MemoryStream stream = new MemoryStream();
        BinaryWriter w = new BinaryWriter(stream);
        w.Write(Encoding.ASCII.GetBytes("RIFF"));
        w.Write(36 + count * 2);
        w.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
        w.Write(16);
        w.Write((short)1);
        w.Write((short)1);
        w.Write(rate);
        w.Write(rate * 2);
        w.Write((short)2);
        w.Write((short)16);
        w.Write(Encoding.ASCII.GetBytes("data"));
        w.Write(count * 2);
        double fade = rate * 0.008;
        for (int i = 0; i < count; i++)
        {
            int j = i % per;
            double hz = i < per ? firstHz : secondHz;
            double envelope = Math.Min(1.0, Math.Min(j, per - 1 - j) / fade);
            w.Write((short)(Math.Sin(2 * Math.PI * hz * j / rate) * envelope * 0.25 * 32767));
        }
        w.Flush();
        return stream.ToArray();
    }
}
