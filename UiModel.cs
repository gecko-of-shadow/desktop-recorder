// UI rules for the note box, the Settings window and the tray menu. No WinForms and no System.Drawing, so the
// unit tests (tests/UiTests.cs) compile this file under Mono too (run-tests.sh). C# 5, like the rest of the app.
using System;
using System.Collections.Generic;
using System.Globalization;

static class UiText
{
    // "0:05", "12:34", "1:02:03". A negative value or a value that is not a number gives "0:00".
    public static string Duration(double seconds)
    {
        if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0) seconds = 0;
        long total = (long)Math.Floor(seconds);
        long hours = total / 3600;
        long minutes = (total / 60) % 60;
        long rest = total % 60;
        string tail = rest.ToString("00", CultureInfo.InvariantCulture);
        if (hours > 0)
            return hours.ToString(CultureInfo.InvariantCulture) + ":" + minutes.ToString("00", CultureInfo.InvariantCulture) + ":" + tail;
        return minutes.ToString(CultureInfo.InvariantCulture) + ":" + tail;
    }
}

// ------------------------------------------------------------------------------------------------ note box

// What started the close of the note box.
enum NoteCloseTrigger
{
    SaveButton,     // "Save note", or Enter in the text box
    NoNoteButton,   // "No note"
    EscapeKey,
    CloseBox,       // the X button, Alt+F4, or a close with no known reason
    QuitApp,        // Exit in the tray menu
    Shutdown        // Windows signs out or shuts down, or Task Manager ends the app: no question is possible
}

enum NoteCloseAction
{
    Save,               // save the note, then close
    CloseWithoutNote,   // close, no note
    Ask,                // show "Save this note?" and keep the box open
    StayOpen            // the answer was Back
}

enum NoteAnswer { Save, Discard, Back }

enum NoteKeyAction { None, Save, NewLine }

// Rules of the note box (Bran, 2026-11-22): the typed text is never lost without a clear answer.
static class NoteRules
{
    public const int MaxLength = 2000;
    public const string WindowTitle = "Note for this recording - Desktop Recorder";
    public const string Title = "Note for this recording";
    public const string Intro = "The note goes into index.md in the ZIP. The AI reads it as context for this recording.";
    // RecordingIndex.Cell writes the note as one line in index.md (line breaks become spaces). note.txt keeps them.
    public const string Hint = "Enter saves the note. Shift+Enter starts a new line. index.md shows the note on one line.";
    public const string SaveText = "Save note";
    public const string NoNoteText = "No note";
    public const string Question = "Save this note?";
    public const string AnswerSave = "Save";
    public const string AnswerDiscard = "Discard";
    public const string AnswerBack = "Back";

    public static string QuestionDetail(NoteCloseTrigger trigger)
    {
        if (trigger == NoteCloseTrigger.QuitApp)
            return "Desktop Recorder closes after this. Save the note in the recording, or discard it? Back keeps the app open.";
        return "You typed a note. Save it in the recording, or discard it? Back returns to the note.";
    }

    public static bool HasText(string text)
    {
        return text != null && text.Trim().Length > 0;
    }

    // The note as it is saved: CR LF line breaks, no white space at the start or the end, at most MaxLength
    // characters. Null when the box has no text.
    public static string Clean(string text)
    {
        if (!HasText(text)) return null;
        string t = text.Replace("\r\n", "\n").Replace('\r', '\n').Trim();
        if (t.Length > MaxLength) t = t.Substring(0, MaxLength).TrimEnd();
        return t.Replace("\n", "\r\n");
    }

    // Save saves. A Windows shutdown saves too, because the app cannot ask then. Every other close asks when the
    // box has text, and closes at once when it has no text.
    public static NoteCloseAction Decide(NoteCloseTrigger trigger, string text)
    {
        if (!HasText(text)) return NoteCloseAction.CloseWithoutNote;
        if (trigger == NoteCloseTrigger.SaveButton || trigger == NoteCloseTrigger.Shutdown) return NoteCloseAction.Save;
        return NoteCloseAction.Ask;
    }

    public static NoteCloseAction AfterAnswer(NoteAnswer answer)
    {
        if (answer == NoteAnswer.Save) return NoteCloseAction.Save;
        if (answer == NoteAnswer.Discard) return NoteCloseAction.CloseWithoutNote;
        return NoteCloseAction.StayOpen;
    }

    // Enter saves. Shift+Enter and Ctrl+Enter start a new line. Alt+Enter does nothing.
    public static NoteKeyAction KeyAction(bool enter, bool shift, bool control, bool alt)
    {
        if (!enter || alt) return NoteKeyAction.None;
        return shift || control ? NoteKeyAction.NewLine : NoteKeyAction.Save;
    }

    // "desktop_2026-11-22_14-16-55  ·  Length 0:57". Null when neither part is known.
    public static string RecordingLine(string name, double seconds)
    {
        bool hasName = !string.IsNullOrEmpty(name);
        bool hasLength = !double.IsNaN(seconds) && !double.IsInfinity(seconds) && seconds > 0;
        if (!hasName && !hasLength) return null;
        if (!hasLength) return name;
        string length = "Length " + UiText.Duration(seconds);
        return hasName ? name + "  ·  " + length : length;
    }
}

// ------------------------------------------------------------------------------------------------ Settings window

enum SettingsPage { Recording, Capture, Hotkey, AiAudio, Startup }

sealed class SettingChoice
{
    public readonly string Value;
    public readonly string Text;

    public SettingChoice(string value, string text)
    {
        Value = value;
        Text = text;
    }
}

sealed class SettingInfo
{
    public readonly string Key;          // the key in settings.txt; StartWithWindowsKey = the Run entry in the registry
    public readonly SettingsPage Page;
    public readonly string Title;
    public readonly string Help;         // plain words: what it does, and the default

    public SettingInfo(string key, SettingsPage page, string title, string help)
    {
        Key = key;
        Page = page;
        Title = title;
        Help = help;
    }
}

// The settings that the Settings window shows, in the order of the window. The keys, the defaults and the value
// ranges are the ones of StoredSettings and RecorderCore; tests/UiTests.cs checks the help texts against them.
static class SettingsCatalog
{
    public const string StartWithWindowsKey = "start_with_windows";

    public static readonly SettingsPage[] Pages =
    {
        SettingsPage.Recording, SettingsPage.Capture, SettingsPage.Hotkey, SettingsPage.AiAudio, SettingsPage.Startup
    };

    public static string PageTitle(SettingsPage page)
    {
        switch (page)
        {
            case SettingsPage.Recording: return "Recording";
            case SettingsPage.Capture: return "Capture and quality";
            case SettingsPage.Hotkey: return "Hotkey and selector";
            case SettingsPage.AiAudio: return "AI and audio";
            default: return "Startup and sounds";
        }
    }

    public static string PageIntro(SettingsPage page)
    {
        switch (page)
        {
            case SettingsPage.Recording: return "Where the recordings go, and how often the screen is captured.";
            case SettingsPage.Capture: return "How the screen is captured and encoded.";
            case SettingsPage.Hotkey: return "How you start and stop a recording.";
            case SettingsPage.AiAudio: return "What the app gives to the AI after a recording.";
            default: return "What happens when Windows or Desktop Recorder starts.";
        }
    }

    // The heading and the text above the selector options on the Hotkey page.
    public const string SelectorHeading = "Region selector";
    public const string SelectorIntro = "The keys C, M and P in the selector also turn these on and off.";

    // Settings with no control in the window. Each name is a key in settings.txt (checked by the tests).
    public static readonly string[] FileOnlyKeys =
    {
        "whisper_path", "whisper_model", "whisper_language", "settle_ms", "still_interval_s", "scroll_percent", "size_limit"
    };
    public const string FileOnlyHeading = "More settings";

    public static string FileOnlyNote()
    {
        return "These settings are in settings.txt only: " + string.Join(", ", FileOnlyKeys)
            + ". Exit Desktop Recorder before you edit the file. The README tells what each one does.";
    }

    // Choice lists. The first choice of each list is the default (the tests check this).
    public static readonly SettingChoice[] Output =
    {
        new SettingChoice(RecorderCore.OutputFolder, "Folder for AI (recommended)"),
        new SettingChoice(RecorderCore.OutputMp4, "MP4 only")
    };
    public static readonly SettingChoice[] Mode =
    {
        new SettingChoice(RecorderCore.ModeChanges, "Changes only (recommended)"),
        new SettingChoice(RecorderCore.ModeFixed, "Fixed frame rate")
    };
    public static readonly SettingChoice[] Backend =
    {
        new SettingChoice(RecorderCore.BackendDdagrab, "ddagrab: GPU (recommended)"),
        new SettingChoice(RecorderCore.BackendGdigrab, "gdigrab: CPU")
    };
    public static readonly SettingChoice[] Encoder =
    {
        new SettingChoice(RecorderCore.EncoderAuto, "Auto (recommended)"),
        new SettingChoice(RecorderCore.EncoderNvenc, "NVIDIA NVENC"),
        new SettingChoice(RecorderCore.EncoderX264, "x264 on the CPU")
    };
    public static readonly SettingChoice[] Chroma =
    {
        new SettingChoice(RecorderCore.Chroma420, "4:2:0 (plays everywhere)"),
        new SettingChoice(RecorderCore.Chroma444, "4:4:4 (full color)")
    };
    // Quality is stored as 0, 1 or 2. The default (1, Balanced) is the first choice here, like in the other lists.
    public static readonly SettingChoice[] Quality =
    {
        new SettingChoice("1", "Balanced (recommended)"),
        new SettingChoice("0", "Smallest files"),
        new SettingChoice("2", "Sharpest text")
    };

    public static readonly SettingInfo[] All =
    {
        new SettingInfo("folder", SettingsPage.Recording, "Save recordings to",
            "Desktop Recorder makes the folder if it does not exist. Do not use Videos or Documents: Controlled folder access can block them. Default: Recordings in your user folder."),
        new SettingInfo("output_mode", SettingsPage.Recording, "Output",
            "Folder for AI gives one ZIP with the frames, index.md and clip.mp4. MP4 only gives one video file. Default: Folder for AI."),
        new SettingInfo("fps", SettingsPage.Recording, "Frames per second",
            "0.2 to 60. 10 is safe for most scrolling text. A lower value gives smaller files. Default: 10."),
        new SettingInfo("frame_rate_mode", SettingsPage.Recording, "Frame-rate mode",
            "Changes only keeps a frame only when the screen changes, so a still screen costs almost nothing. Default: Changes only."),
        new SettingInfo("split_minutes", SettingsPage.Recording, "Start a new file every (minutes)",
            "MP4 only. 0 gives one file for each recording. Folder for AI never starts a new file. Default: 30."),

        new SettingInfo("quality", SettingsPage.Capture, "Quality",
            "Smaller files or sharper text. In folder mode it changes only clip.mp4: the frames are always lossless. Default: Balanced."),
        new SettingInfo("capture_backend", SettingsPage.Capture, "Capture method",
            "ddagrab uses the GPU and records one monitor. gdigrab uses the CPU and can record across two monitors. Default: ddagrab."),
        new SettingInfo("encoder", SettingsPage.Capture, "Encoder",
            "Auto uses the NVIDIA encoder (NVENC) when it works, and x264 on the CPU when it does not. Default: Auto."),
        new SettingInfo("chroma", SettingsPage.Capture, "Color detail",
            "4:4:4 keeps full color detail for colored text, but some players cannot play it. In folder mode it changes only clip.mp4. Default: 4:2:0."),
        new SettingInfo("show_pointer", SettingsPage.Capture, "Hide the mouse pointer",
            "A moving pointer keeps the screen from settling and adds frames. Default: On (the pointer is hidden)."),

        new SettingInfo("hotkey", SettingsPage.Hotkey, "Hotkey",
            "Opens the region selector, and stops the recording. Use Win, Ctrl or Alt with a key. Shift alone works only with F1 to F12. Default: Win+Shift+D."),
        new SettingInfo("selector_crosshair", SettingsPage.Hotkey, "Crosshair",
            "Lines across the screen at the pointer. Key C in the selector. Default: Off."),
        new SettingInfo("selector_magnifier", SettingsPage.Hotkey, "Magnifier",
            "An enlarged view near the pointer, for exact edges. Key M in the selector. Default: On."),
        new SettingInfo("selector_position", SettingsPage.Hotkey, "Position and size",
            "Shows the position and the size of the selection. Key P in the selector. Default: On."),

        new SettingInfo("copy_to_clipboard", SettingsPage.AiAudio, "Copy to the clipboard when the recording stops",
            "Folder for AI: the ZIP and a message for the AI. Press Ctrl+V in a chat to attach the ZIP. MP4 only: the video file. Default: On."),
        new SettingInfo("record_audio", SettingsPage.AiAudio, "Record the sound of the PC",
            "Records what the PC plays, not the microphone. When Whisper is installed, index.md also gets a transcript. Default: Off."),

        new SettingInfo(StartWithWindowsKey, SettingsPage.Startup, "Start with Windows",
            "Starts Desktop Recorder when you sign in to Windows. It adds one entry for your user account only. Default: Off."),
        new SettingInfo("record_on_launch", SettingsPage.Startup, "Record when the app starts",
            "Records the saved region at once, without the selector. With no saved region, it records the whole desktop. Default: Off."),
        new SettingInfo("play_sounds", SettingsPage.Startup, "Play sounds",
            "Two rising tones when a recording starts, and two falling tones when it stops. Default: On.")
    };

    public static SettingInfo Get(string key)
    {
        foreach (SettingInfo s in All) if (s.Key == key) return s;
        throw new ArgumentException("Unknown setting: " + key);
    }

    public static List<SettingInfo> OnPage(SettingsPage page)
    {
        List<SettingInfo> list = new List<SettingInfo>();
        foreach (SettingInfo s in All) if (s.Page == page) list.Add(s);
        return list;
    }

    // The index of the value in the list. A value that is not in the list gives the default (index 0).
    public static int IndexOf(SettingChoice[] choices, string value)
    {
        for (int i = 0; i < choices.Length; i++) if (choices[i].Value == value) return i;
        return 0;
    }

    public static string ValueAt(SettingChoice[] choices, int index)
    {
        return index >= 0 && index < choices.Length ? choices[index].Value : choices[0].Value;
    }
}

// ------------------------------------------------------------------------------------------------ tray menu

enum TrayPhase { Idle, Recording, Stopping, Processing, Exiting }

enum TrayCommand { Record, OpenFolder, ShowLastZip, CopyLastZip, Settings, Exit }

sealed class TrayState
{
    public TrayPhase Phase;
    public double ElapsedSeconds;
    public double Fps;
    public int RegionWidth;          // 0 = the whole desktop
    public int RegionHeight;
    public string HotkeyText;        // for example "Win+Shift+D"
    public bool HotkeyActive;        // false: another program uses the hotkey
    public bool HasLastZip;
    public string LastResult;        // "Last recording: ...", or null
}

sealed class TrayEntry
{
    public readonly TrayCommand Command;
    public readonly string Text;
    public readonly string Shortcut;  // null = none
    public readonly bool Enabled;
    public readonly bool IsDefault;   // bold
    public readonly int Group;        // a line goes between two groups

    public TrayEntry(TrayCommand command, string text, string shortcut, bool enabled, bool isDefault, int group)
    {
        Command = command;
        Text = text;
        Shortcut = shortcut;
        Enabled = enabled;
        IsDefault = isDefault;
        Group = group;
    }
}

static class TrayModel
{
    // NotifyIcon.Text of .NET Framework accepts at most 63 characters.
    public const int TooltipMax = 63;

    public static TrayPhase PhaseOf(bool recording, bool stopping, int passesRunning, bool quitRequested)
    {
        if (quitRequested) return TrayPhase.Exiting;
        // While FFmpeg finishes the file, the user already pressed Stop: not "recording" any more.
        if (stopping) return TrayPhase.Stopping;
        if (recording) return TrayPhase.Recording;
        if (passesRunning > 0) return TrayPhase.Processing;
        return TrayPhase.Idle;
    }

    public static string StatusTitle(TrayState s)
    {
        switch (s.Phase)
        {
            case TrayPhase.Recording: return "Recording  " + UiText.Duration(s.ElapsedSeconds);
            case TrayPhase.Stopping: return "Stopping the recording...";
            case TrayPhase.Processing: return "Processing the last recording...";
            case TrayPhase.Exiting: return "Closing Desktop Recorder...";
            default: return "Not recording";
        }
    }

    public static string StatusDetail(TrayState s)
    {
        switch (s.Phase)
        {
            case TrayPhase.Recording:
                string fps = s.Fps.ToString("0.###", CultureInfo.InvariantCulture) + " fps";
                if (s.RegionWidth <= 0 || s.RegionHeight <= 0) return fps + "  ·  whole desktop";
                return fps + "  ·  " + s.RegionWidth.ToString(CultureInfo.InvariantCulture) + " x "
                    + s.RegionHeight.ToString(CultureInfo.InvariantCulture) + " px";
            case TrayPhase.Stopping: return "FFmpeg is finishing the file.";
            case TrayPhase.Processing: return "Making the frames, index.md and the ZIP.";
            case TrayPhase.Exiting: return "The app closes when the last recording is ready.";
            default:
                if (string.IsNullOrEmpty(s.HotkeyText)) return "Use the menu to select a region.";
                return s.HotkeyActive ? "Press " + s.HotkeyText + " to select a region."
                    : s.HotkeyText + " is used by another program. Change it in Settings.";
        }
    }

    public static string Tooltip(TrayState s)
    {
        string text;
        switch (s.Phase)
        {
            case TrayPhase.Recording: text = "Desktop Recorder: recording"; break;
            case TrayPhase.Stopping:
            case TrayPhase.Processing: text = "Desktop Recorder: processing"; break;
            case TrayPhase.Exiting: text = "Desktop Recorder: closing"; break;
            default: text = "Desktop Recorder: not recording"; break;
        }
        return text.Length > TooltipMax ? text.Substring(0, TooltipMax) : text;
    }

    // The menu below the status: the record command first (bold, the hotkey next to it), then the files, then the app.
    public static List<TrayEntry> Entries(TrayState s)
    {
        bool recording = s.Phase == TrayPhase.Recording;
        bool canRecord = s.Phase != TrayPhase.Stopping && s.Phase != TrayPhase.Exiting;
        string shortcut = s.HotkeyActive && !string.IsNullOrEmpty(s.HotkeyText) ? s.HotkeyText : null;
        List<TrayEntry> list = new List<TrayEntry>();
        list.Add(new TrayEntry(TrayCommand.Record, recording ? "Stop recording" : "Select region and record",
            shortcut, canRecord, true, 1));
        list.Add(new TrayEntry(TrayCommand.OpenFolder, "Open recordings folder", null, true, false, 2));
        list.Add(new TrayEntry(TrayCommand.ShowLastZip, "Show last ZIP in Explorer", null, s.HasLastZip, false, 2));
        list.Add(new TrayEntry(TrayCommand.CopyLastZip, "Copy ZIP of last recording", null, s.HasLastZip, false, 2));
        list.Add(new TrayEntry(TrayCommand.Settings, "Settings...", null, true, false, 3));
        list.Add(new TrayEntry(TrayCommand.Exit, "Exit", null, true, false, 3));
        return list;
    }
}
