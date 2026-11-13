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
    public int HotkeyModifiers = 4 | 8;   // 1 = Alt, 2 = Ctrl, 4 = Shift, 8 = Win
    public int HotkeyKey = (int)'D';
    // Lines this program does not understand. Written back so another OS can keep its keys.
    public List<string> ExtraLines = new List<string>();
}

static class RecorderCore
{
    public const double DefaultFps = 2;
    public const double MinFps = 0.2;
    public const double MaxFps = 60;
    public static readonly int[] Crf = { 32, 27, 21 };

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
        return false;
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
        Append(sb, "hotkey", HotkeyText(s.HotkeyModifiers, s.HotkeyKey));
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
        s.HotkeyModifiers = defaults.HotkeyModifiers;
        s.HotkeyKey = defaults.HotkeyKey;
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

    public static string[] BuildWindowsArguments(StoredSettings s, DateTime now)
    {
        if (s == null) s = new StoredSettings();
        double fps = ClampFps(s.Fps);
        int quality = ClampQuality(s.Quality);
        int split = ClampSplitMinutes(s.SplitMinutes);
        string fpsText = fps.ToString("0.###", CultureInfo.InvariantCulture);
        int gop = Math.Max(2, (int)Math.Round(fps * 30));
        int lookahead = Math.Max(0, Math.Min(40, (int)Math.Round(fps * 2)));
        int threads = Math.Max(1, Math.Min(16, (int)Math.Ceiling(fps)));
        string stamp = now.ToString("yyyy-MM-dd_HH-mm-ss_fff", CultureInfo.InvariantCulture);
        string folder = s.Folder ?? "";
        List<string> a = new List<string>();
        a.Add("-hide_banner");
        a.Add("-loglevel");
        a.Add("error");
        a.Add("-nostats");
        a.Add("-y");
        a.Add("-f");
        a.Add("gdigrab");
        a.Add("-framerate");
        a.Add(fpsText);
        a.Add("-draw_mouse");
        a.Add("1");
        a.Add("-i");
        a.Add("desktop");
        a.Add("-vf");
        a.Add("crop=trunc(iw/2)*2:trunc(ih/2)*2:0:0");
        a.Add("-c:v");
        a.Add("libx264");
        a.Add("-preset");
        a.Add("medium");
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
        a.Add("-pix_fmt");
        a.Add("yuv420p");
        if (split > 0)
        {
            // The segment muxer only cuts on a keyframe. Force one at each boundary
            // so "every N minutes" is the real cut, not "N minutes plus up to one GOP".
            int seconds = split * 60;
            a.Add("-force_key_frames");
            a.Add("expr:gte(t,n_forced*" + seconds.ToString(CultureInfo.InvariantCulture) + ")");
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
            a.Add(CombinePath(folder, "desktop_" + stamp + ".mp4", true));
        }
        return a.ToArray();
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
