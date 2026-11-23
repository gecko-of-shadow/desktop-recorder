// Checks for UiModel.cs: the note box rules, the settings catalog and the tray menu model. No windows.
using System;
using System.Collections.Generic;

static class UiTests
{
    static int failures;

    static void Expect(bool condition, string name)
    {
        if (condition) return;
        failures++;
        Console.Error.WriteLine("FAIL " + name);
    }

    static void ExpectEqual(string actual, string expected, string name)
    {
        if (actual == expected) return;
        failures++;
        Console.Error.WriteLine("FAIL " + name);
        Console.Error.WriteLine("  actual:   " + actual);
        Console.Error.WriteLine("  expected: " + expected);
    }

    public static int Run()
    {
        failures = 0;
        TestDuration();
        TestNoteDecide();
        TestNoteKeys();
        TestNoteClean();
        TestRecordingLine();
        TestCatalogKeys();
        TestCatalogDefaults();
        TestTrayPhase();
        TestTrayEntries();
        TestTrayText();
        TestMenuInside();
        TestRecordClick();
        TestZipStatus();
        return failures;
    }

    static void TestDuration()
    {
        ExpectEqual(UiText.Duration(0), "0:00", "duration 0");
        ExpectEqual(UiText.Duration(5.9), "0:05", "duration rounds down");
        ExpectEqual(UiText.Duration(83), "1:23", "duration minutes");
        ExpectEqual(UiText.Duration(3723), "1:02:03", "duration hours");
        ExpectEqual(UiText.Duration(-4), "0:00", "duration negative");
        ExpectEqual(UiText.Duration(double.NaN), "0:00", "duration NaN");
    }

    static void TestNoteDecide()
    {
        NoteCloseTrigger[] all =
        {
            NoteCloseTrigger.SaveButton, NoteCloseTrigger.NoNoteButton, NoteCloseTrigger.EscapeKey,
            NoteCloseTrigger.CloseBox, NoteCloseTrigger.QuitApp, NoteCloseTrigger.Shutdown
        };
        // An empty box (or only white space) always closes with no note and no question.
        foreach (NoteCloseTrigger t in all)
        {
            Expect(NoteRules.Decide(t, "") == NoteCloseAction.CloseWithoutNote, "empty closes: " + t);
            Expect(NoteRules.Decide(t, "  \r\n ") == NoteCloseAction.CloseWithoutNote, "blank closes: " + t);
            Expect(NoteRules.Decide(t, null) == NoteCloseAction.CloseWithoutNote, "null closes: " + t);
        }
        // With text: only Save and a shutdown save at once. Every other close asks. Nothing discards without a question.
        Expect(NoteRules.Decide(NoteCloseTrigger.SaveButton, "x") == NoteCloseAction.Save, "save saves");
        Expect(NoteRules.Decide(NoteCloseTrigger.Shutdown, "x") == NoteCloseAction.Save, "shutdown saves");
        Expect(NoteRules.Decide(NoteCloseTrigger.NoNoteButton, "x") == NoteCloseAction.Ask, "no note asks");
        Expect(NoteRules.Decide(NoteCloseTrigger.EscapeKey, "x") == NoteCloseAction.Ask, "esc asks");
        Expect(NoteRules.Decide(NoteCloseTrigger.CloseBox, "x") == NoteCloseAction.Ask, "close box asks");
        Expect(NoteRules.Decide(NoteCloseTrigger.QuitApp, "x") == NoteCloseAction.Ask, "quit asks");
        foreach (NoteCloseTrigger t in all)
            Expect(NoteRules.Decide(t, "x") != NoteCloseAction.CloseWithoutNote, "text is never dropped: " + t);
        Expect(NoteRules.AfterAnswer(NoteAnswer.Save) == NoteCloseAction.Save, "answer save");
        Expect(NoteRules.AfterAnswer(NoteAnswer.Discard) == NoteCloseAction.CloseWithoutNote, "answer discard");
        Expect(NoteRules.AfterAnswer(NoteAnswer.Back) == NoteCloseAction.StayOpen, "answer back");
        Expect(NoteRules.QuestionDetail(NoteCloseTrigger.QuitApp).Contains("closes"), "quit question says the app closes");
    }

    static void TestNoteKeys()
    {
        Expect(NoteRules.KeyAction(true, false, false, false) == NoteKeyAction.Save, "enter saves");
        Expect(NoteRules.KeyAction(true, true, false, false) == NoteKeyAction.NewLine, "shift enter new line");
        Expect(NoteRules.KeyAction(true, false, true, false) == NoteKeyAction.NewLine, "ctrl enter new line");
        Expect(NoteRules.KeyAction(true, false, false, true) == NoteKeyAction.None, "alt enter nothing");
        Expect(NoteRules.KeyAction(false, false, false, false) == NoteKeyAction.None, "other key nothing");
        Expect(NoteRules.Hint.Contains("Shift+Enter"), "hint names the new line key");
    }

    static void TestNoteClean()
    {
        ExpectEqual(NoteRules.Clean("  first\nsecond \r\n"), "first\r\nsecond", "clean trims and uses CR LF");
        ExpectEqual(NoteRules.Clean("a\rb"), "a\r\nb", "clean CR alone");
        Expect(NoteRules.Clean(" \r\n ") == null, "clean blank is null");
        Expect(NoteRules.Clean(null) == null, "clean null is null");
        string longText = new string('x', NoteRules.MaxLength + 50);
        Expect(NoteRules.Clean(longText).Length == NoteRules.MaxLength, "clean cuts at the limit");
    }

    static void TestRecordingLine()
    {
        ExpectEqual(NoteRules.RecordingLine("desktop_1", 57.4), "desktop_1  ·  Length 0:57", "line name and length");
        ExpectEqual(NoteRules.RecordingLine("desktop_1", 0), "desktop_1", "line name only");
        ExpectEqual(NoteRules.RecordingLine(null, 61), "Length 1:01", "line length only");
        Expect(NoteRules.RecordingLine(null, double.NaN) == null, "line nothing known");
    }

    // The keys that settings.txt has, from the real writer.
    static HashSet<string> FileKeys()
    {
        HashSet<string> keys = new HashSet<string>();
        foreach (string line in RecorderCore.SerializeSettings(new StoredSettings()).Split('\n'))
        {
            int eq = line.IndexOf('=');
            if (eq > 0) keys.Add(line.Substring(0, eq).Trim());
        }
        return keys;
    }

    static void TestCatalogKeys()
    {
        HashSet<string> fileKeys = FileKeys();
        // The 19 settings of the old Settings window (3.0.1): none is lost, except "Record when the app starts"
        // (record_on_launch), which Bran removed on 2026-11-22. That leaves 18.
        string[] oldWindow =
        {
            "folder", "fps", "quality", "split_minutes", "hotkey", "capture_backend", "encoder", "chroma",
            "frame_rate_mode", "output_mode", SettingsCatalog.StartWithWindowsKey, "copy_to_clipboard",
            "play_sounds", "show_pointer", "record_audio", "selector_crosshair", "selector_magnifier", "selector_position"
        };
        HashSet<string> seen = new HashSet<string>();
        foreach (SettingInfo s in SettingsCatalog.All)
        {
            Expect(seen.Add(s.Key), "catalog key once: " + s.Key);
            Expect(s.Key == SettingsCatalog.StartWithWindowsKey || fileKeys.Contains(s.Key), "catalog key is a file key: " + s.Key);
            Expect(s.Help.Contains("Default: "), "help gives the default: " + s.Key);
            Expect(s.Title.Length > 0 && s.Help.Length > 0, "title and help: " + s.Key);
        }
        Expect(SettingsCatalog.All.Length == oldWindow.Length, "catalog has 18 settings");
        Expect(!seen.Contains("record_on_launch") && !fileKeys.Contains("record_on_launch"), "record_on_launch is removed");
        foreach (string key in oldWindow) Expect(seen.Contains(key), "old window setting kept: " + key);
        foreach (SettingsPage page in SettingsCatalog.Pages)
        {
            Expect(SettingsCatalog.OnPage(page).Count >= 2, "page has settings: " + page);
            Expect(SettingsCatalog.PageTitle(page).Length > 0 && SettingsCatalog.PageIntro(page).Length > 0, "page texts: " + page);
        }
        Expect(SettingsCatalog.Pages.Length == 5, "five pages");
        foreach (string key in SettingsCatalog.FileOnlyKeys)
        {
            Expect(fileKeys.Contains(key), "file-only key exists: " + key);
            Expect(!seen.Contains(key), "file-only key has no control: " + key);
            Expect(SettingsCatalog.FileOnlyNote().Contains(key), "file-only note names: " + key);
        }
        bool thrown = false;
        try { SettingsCatalog.Get("no_such_key"); } catch (ArgumentException) { thrown = true; }
        Expect(thrown, "unknown key throws");
    }

    static string DefaultName(SettingChoice[] list)
    {
        return list[0].Text.Replace(" (recommended)", "");
    }

    static void TestCatalogDefaults()
    {
        StoredSettings d = new StoredSettings();
        // The first choice of each list is the default of StoredSettings, and the help text names it.
        ExpectEqual(SettingsCatalog.Output[0].Value, d.OutputMode, "output default first");
        ExpectEqual(SettingsCatalog.Mode[0].Value, d.FrameRateMode, "mode default first");
        ExpectEqual(SettingsCatalog.Backend[0].Value, d.CaptureBackend, "backend default first");
        ExpectEqual(SettingsCatalog.Encoder[0].Value, d.Encoder, "encoder default first");
        ExpectEqual(SettingsCatalog.Chroma[0].Value, d.ChromaFormat, "chroma default first");
        ExpectEqual(SettingsCatalog.Quality[0].Value, d.Quality.ToString(), "quality default first");
        Expect(SettingsCatalog.Get("output_mode").Help.Contains("Default: " + DefaultName(SettingsCatalog.Output)), "output help default");
        Expect(SettingsCatalog.Get("frame_rate_mode").Help.Contains("Default: " + DefaultName(SettingsCatalog.Mode)), "mode help default");
        Expect(SettingsCatalog.Get("encoder").Help.Contains("Default: " + DefaultName(SettingsCatalog.Encoder)), "encoder help default");
        Expect(SettingsCatalog.Get("quality").Help.Contains("Default: Balanced"), "quality help default");
        Expect(SettingsCatalog.Get("capture_backend").Help.Contains("Default: ddagrab"), "backend help default");
        Expect(SettingsCatalog.Get("chroma").Help.Contains("Default: 4:2:0"), "chroma help default");
        Expect(SettingsCatalog.Get("fps").Help.Contains("Default: " + RecorderCore.DefaultFps + "."), "fps help default");
        Expect(SettingsCatalog.Get("fps").Help.Contains(RecorderCore.MinFps.ToString(System.Globalization.CultureInfo.InvariantCulture) + " to " + RecorderCore.MaxFps), "fps help range");
        Expect(SettingsCatalog.Get("split_minutes").Help.Contains("Default: " + d.SplitMinutes + "."), "split help default");
        Expect(SettingsCatalog.Get("hotkey").Help.Contains("Default: " + RecorderCore.HotkeyText(d.HotkeyModifiers, d.HotkeyKey) + "."), "hotkey help default");
        // Switches: "On" or "Off" as StoredSettings has it. "Hide the mouse pointer" is on when ShowPointer is false.
        Expect(SettingsCatalog.Get("show_pointer").Help.Contains("Default: " + (d.ShowPointer ? "Off" : "On")), "pointer help default");
        Expect(SettingsCatalog.Get("copy_to_clipboard").Help.Contains("Default: " + (d.CopyToClipboard ? "On" : "Off")), "copy help default");
        Expect(SettingsCatalog.Get("record_audio").Help.Contains("Default: " + (d.RecordAudio ? "On" : "Off")), "audio help default");
        Expect(SettingsCatalog.Get("play_sounds").Help.Contains("Default: " + (d.PlaySounds ? "On" : "Off")), "sound help default");
        Expect(SettingsCatalog.Get("selector_crosshair").Help.Contains("Default: " + (d.SelectorCrosshair ? "On" : "Off")), "crosshair help default");
        Expect(SettingsCatalog.Get("selector_magnifier").Help.Contains("Default: " + (d.SelectorMagnifier ? "On" : "Off")), "magnifier help default");
        Expect(SettingsCatalog.Get("selector_position").Help.Contains("Default: " + (d.SelectorPosition ? "On" : "Off")), "position help default");
        // Every value of RecorderCore is in a list, and a value that is not in the list gives the default.
        Expect(SettingsCatalog.IndexOf(SettingsCatalog.Mode, RecorderCore.ModeFixed) == 1, "mode fixed index");
        Expect(SettingsCatalog.IndexOf(SettingsCatalog.Encoder, RecorderCore.EncoderX264) == 2, "encoder x264 index");
        Expect(SettingsCatalog.IndexOf(SettingsCatalog.Chroma, "nonsense") == 0, "unknown value gives default");
        ExpectEqual(SettingsCatalog.ValueAt(SettingsCatalog.Quality, 2), "2", "quality value at 2");
        ExpectEqual(SettingsCatalog.ValueAt(SettingsCatalog.Quality, 9), "1", "quality bad index gives default");
    }

    static void TestTrayPhase()
    {
        Expect(TrayModel.PhaseOf(false, false, 0, false) == TrayPhase.Idle, "phase idle");
        Expect(TrayModel.PhaseOf(true, false, 0, false) == TrayPhase.Recording, "phase recording");
        Expect(TrayModel.PhaseOf(true, true, 0, false) == TrayPhase.Stopping, "phase stopping wins over recording");
        Expect(TrayModel.PhaseOf(false, false, 2, false) == TrayPhase.Processing, "phase processing");
        Expect(TrayModel.PhaseOf(true, false, 0, false) == TrayPhase.Recording, "recording while a pass runs");
        Expect(TrayModel.PhaseOf(false, false, 1, true) == TrayPhase.Exiting, "phase exiting");
        Expect(TrayModel.PhaseOf(false, false, 0, true) == TrayPhase.Exiting, "phase exiting, nothing runs");
        Expect(TrayModel.PhaseOf(true, false, 0, true) == TrayPhase.Recording, "a running recording keeps Stop while Exit waits");
    }

    static TrayState State(TrayPhase phase)
    {
        TrayState s = new TrayState();
        s.Phase = phase;
        s.HotkeyText = "Win+Shift+D";
        s.HotkeyActive = true;
        s.Fps = 10;
        return s;
    }

    static void TestTrayEntries()
    {
        List<TrayEntry> idle = TrayModel.Entries(State(TrayPhase.Idle));
        Expect(idle.Count == 6, "six commands");
        ExpectEqual(idle[0].Text, "Select region and record", "idle record text");
        ExpectEqual(idle[0].Shortcut, "Win+Shift+D", "hotkey next to the record command");
        Expect(idle[0].IsDefault && idle[0].Enabled, "record is bold and on");
        Expect(!idle[2].Enabled && !idle[3].Enabled, "ZIP commands off with no ZIP");
        int defaults = 0;
        foreach (TrayEntry e in idle) if (e.IsDefault) defaults++;
        Expect(defaults == 1, "one default command");
        HashSet<TrayCommand> commands = new HashSet<TrayCommand>();
        foreach (TrayEntry e in idle) commands.Add(e.Command);
        Expect(commands.Count == 6, "each command once");

        TrayState withZip = State(TrayPhase.Processing);
        withZip.HasLastZip = true;
        List<TrayEntry> processing = TrayModel.Entries(withZip);
        Expect(processing[2].Enabled && processing[3].Enabled, "ZIP commands on with a ZIP");
        Expect(processing[0].Enabled, "a new recording can start while a pass runs");

        ExpectEqual(TrayModel.Entries(State(TrayPhase.Recording))[0].Text, "Stop recording", "recording stop text");
        Expect(!TrayModel.Entries(State(TrayPhase.Stopping))[0].Enabled, "record off while stopping");
        Expect(!TrayModel.Entries(State(TrayPhase.Exiting))[0].Enabled, "record off while closing");

        TrayState taken = State(TrayPhase.Idle);
        taken.HotkeyActive = false;
        Expect(TrayModel.Entries(taken)[0].Shortcut == null, "no hotkey text when the hotkey is off");
        Expect(TrayModel.StatusDetail(taken).Contains("another program"), "status says the hotkey is taken");
    }

    static void TestZipStatus()
    {
        ZipStep[] busy = { ZipStep.Stopping, ZipStep.Frames, ZipStep.Transcript, ZipStep.Zip };
        foreach (ZipStep z0 in busy)
        {
            Expect(ZipStatus.Busy(z0), "busy: " + z0);
            ExpectEqual(ZipStatus.BusyText(z0), "Making the ZIP... (step: " + ZipStatus.StepName(z0) + ")", "busy text: " + z0);
            ExpectEqual(ZipStatus.NoteText(z0, 0, true, false, null), ZipStatus.BusyText(z0), "note busy: " + z0);
            Expect(ZipStatus.PopupSeconds(z0) == 0, "busy popup stays: " + z0);
            ExpectEqual(ZipStatus.PopupDetail(z0, null), ZipStatus.WaitText, "busy popup detail: " + z0);
        }
        ExpectEqual(ZipStatus.StepName(ZipStep.Frames), "frames", "step frames");
        ExpectEqual(ZipStatus.StepName(ZipStep.Transcript), "transcript", "step transcript");
        ExpectEqual(ZipStatus.StepName(ZipStep.Zip), "ZIP", "step zip");
        Expect(!ZipStatus.Busy(ZipStep.Ready) && !ZipStatus.Busy(ZipStep.Failed), "ready and failed are not busy");
        long bytes = 35337011;   // 33.7 MB, the Twitch ZIP of 2026-11-22
        ExpectEqual(ZipStatus.Megabytes(bytes), "33.7 MB", "megabytes");
        ExpectEqual(ZipStatus.Megabytes(-5), "0.0 MB", "megabytes negative");
        ExpectEqual(ZipStatus.NoteText(ZipStep.Ready, bytes, true, false, null), "ZIP ready. Press Ctrl+V to paste (33.7 MB).", "note ready");
        Expect(ZipStatus.NoteText(ZipStep.Ready, bytes, false, false, null).Contains("clipboard copy is off"), "note ready, no copy");
        Expect(ZipStatus.NoteText(ZipStep.Ready, bytes, true, true, null).StartsWith("ZIP with your note ready"), "note ready after a note");
        Expect(ZipStatus.NoteText(ZipStep.Failed, 0, true, false, "disk full").Contains("disk full"), "note failed has the error");
        ExpectEqual(ZipStatus.PopupText(ZipStep.Ready, bytes, true, false), "ZIP ready: Ctrl+V to paste (33.7 MB)", "popup ready");
        Expect(!ZipStatus.PopupText(ZipStep.Ready, bytes, false, false).Contains("Ctrl+V"), "popup ready, no copy: no Ctrl+V");
        ExpectEqual(ZipStatus.PopupText(ZipStep.Failed, 0, true, false), "Could not make the ZIP", "popup failed");
        Expect(ZipStatus.PopupDetail(ZipStep.Failed, "disk full").Contains("disk full"), "popup failed detail");
        Expect(ZipStatus.PopupDetail(ZipStep.Ready, null) == null, "popup ready has no detail");
        Expect(ZipStatus.PopupSeconds(ZipStep.Ready) == 4 && ZipStatus.PopupSeconds(ZipStep.Failed) > 4, "popup times");
        Expect(ZipStatus.ClipboardPlaceholder.Contains("ZIP ready"), "placeholder says what to wait for");
        // A ready ZIP that is not on the clipboard says why, and where to get it.
        string ne = ZipStatus.NoteText(ZipStep.Ready, bytes, false, false, null, "the clipboard is in use.");
        Expect(ne.Contains("not on the clipboard") && ne.Contains("Copy ZIP of last recording"), "note copy error");
        Expect(!ne.Contains("off in Settings"), "note copy error does not blame Settings");
        Expect(ZipStatus.PopupText(ZipStep.Ready, bytes, false, false, "x").Contains("not on the clipboard"), "popup copy error");
        Expect(ZipStatus.PopupDetail(ZipStep.Ready, null, "x").Contains("Copy ZIP of last recording"), "popup copy error detail");
        // Tray: the ZIP work shows when the app does not record and does not close.
        TrayState s = State(TrayPhase.Processing);
        s.ZipBusy = true;
        s.ZipStep = ZipStep.Transcript;
        ExpectEqual(TrayModel.StatusTitle(s), "Making the ZIP... (step: transcript)", "tray title zip");
        ExpectEqual(TrayModel.StatusDetail(s), ZipStatus.WaitText, "tray detail zip");
        ExpectEqual(TrayModel.Tooltip(s), "Desktop Recorder: making the ZIP (transcript)", "tray tooltip zip");
        s.Phase = TrayPhase.Stopping;
        s.ZipStep = ZipStep.Stopping;
        ExpectEqual(TrayModel.StatusTitle(s), "Making the ZIP... (step: stopping)", "tray title stopping");
        s.Phase = TrayPhase.Recording;
        Expect(TrayModel.StatusTitle(s).StartsWith("Recording"), "a new recording wins over the ZIP");
        s.Phase = TrayPhase.Exiting;
        Expect(TrayModel.StatusTitle(s).StartsWith("Closing"), "closing wins over the ZIP");
        foreach (ZipStep z in busy)
        {
            TrayState t = State(TrayPhase.Processing);
            t.ZipBusy = true;
            t.ZipStep = z;
            Expect(TrayModel.Tooltip(t).Length <= TrayModel.TooltipMax, "zip tooltip fits: " + z);
        }
    }

    static void TestTrayText()
    {
        TrayState rec = State(TrayPhase.Recording);
        rec.ElapsedSeconds = 83;
        rec.RegionWidth = 1880;
        rec.RegionHeight = 860;
        ExpectEqual(TrayModel.StatusTitle(rec), "Recording  1:23", "recording title has the time");
        ExpectEqual(TrayModel.StatusDetail(rec), "10 fps  ·  1880 x 860 px", "recording detail");
        rec.RegionWidth = 0;
        Expect(TrayModel.StatusDetail(rec).Contains("whole desktop"), "whole desktop detail");
        ExpectEqual(TrayModel.StatusTitle(State(TrayPhase.Idle)), "Not recording", "idle title");
        Expect(TrayModel.StatusTitle(State(TrayPhase.Processing)).StartsWith("Processing"), "processing title");
        foreach (TrayPhase p in new[] { TrayPhase.Idle, TrayPhase.Recording, TrayPhase.Stopping, TrayPhase.Processing, TrayPhase.Exiting })
        {
            TrayState s = State(p);
            Expect(TrayModel.Tooltip(s).Length <= TrayModel.TooltipMax, "tooltip fits: " + p);
            Expect(TrayModel.StatusTitle(s).Length > 0 && TrayModel.StatusDetail(s).Length > 0, "status texts: " + p);
        }
    }

    static void TestRecordClick()
    {
        Expect(TrayModel.RecordClickActs(TrayPhase.Recording, TrayPhase.Recording), "stop while recording acts");
        Expect(TrayModel.RecordClickActs(TrayPhase.Idle, TrayPhase.Idle), "record while idle acts");
        Expect(TrayModel.RecordClickActs(TrayPhase.Processing, TrayPhase.Idle), "record while the ZIP is made, then idle, acts");
        Expect(!TrayModel.RecordClickActs(TrayPhase.Recording, TrayPhase.Stopping), "a stale 'Stop recording' does nothing (stopping)");
        Expect(!TrayModel.RecordClickActs(TrayPhase.Recording, TrayPhase.Processing), "a stale 'Stop recording' does nothing (processing)");
        Expect(!TrayModel.RecordClickActs(TrayPhase.Idle, TrayPhase.Recording), "a stale 'Select region and record' does not stop the new recording");
    }

    // NotifyIcon puts the bottom right corner of the menu at the pointer. The live test (HANDOFF 7.22) found the
    // lower 30 px of the menu on the taskbar: the menu must move into the work area.
    static void TestMenuInside()
    {
        int x, y;
        TrayModel.MenuInside(798, 699, 440, 351, 0, 0, 1920, 1020, out x, out y);
        Expect(x == 798 && y == 669, "menu above a bottom taskbar: " + x + "," + y);
        TrayModel.MenuInside(798, 300, 440, 351, 0, 0, 1920, 1020, out x, out y);
        Expect(x == 798 && y == 300, "menu inside the work area does not move: " + x + "," + y);
        TrayModel.MenuInside(798, 20, 440, 351, 0, 48, 1920, 1080, out x, out y);
        Expect(x == 798 && y == 48, "menu below a top taskbar: " + x + "," + y);
        TrayModel.MenuInside(1700, 600, 440, 351, 0, 0, 1872, 1080, out x, out y);
        Expect(x == 1432 && y == 600, "menu left of a right taskbar: " + x + "," + y);
        TrayModel.MenuInside(-10, 600, 440, 351, 48, 0, 1920, 1080, out x, out y);
        Expect(x == 48 && y == 600, "menu right of a left taskbar: " + x + "," + y);
        TrayModel.MenuInside(0, 0, 440, 2000, 0, 0, 1920, 1020, out x, out y);
        Expect(x == 0 && y == 0, "a menu taller than the work area keeps its top in the area: " + x + "," + y);
    }
}
