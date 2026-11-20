// capture_args.cs - prints the FFmpeg arguments of the app for a folder-mode capture (test helper).
// Usage: capture_args.exe <mode fixed|changes> <fps> <x> <y> <w> <h> <work file> [gdigrab]
using System;
using System.Collections.Generic;
using System.Globalization;

static class CaptureArgs
{
    static int Main(string[] a)
    {
        StoredSettings s = new StoredSettings();
        s.FrameRateMode = a[0];
        s.Fps = double.Parse(a[1], CultureInfo.InvariantCulture);
        s.ShowPointer = false;
        if (a.Length > 7 && a[7] == "gdigrab") s.CaptureBackend = RecorderCore.BackendGdigrab;
        CaptureRegion r = new CaptureRegion(int.Parse(a[2]), int.Parse(a[3]), int.Parse(a[4]), int.Parse(a[5]));
        List<MonitorBounds> m = new List<MonitorBounds>();
        m.Add(new MonitorBounds(0, 0, 0, 1920, 1080));
        Console.WriteLine(RecorderCore.JoinWindowsArguments(RecorderCore.BuildCaptureArguments(s, r, m, DateTime.Now, true, a[6])));
        return 0;
    }
}
