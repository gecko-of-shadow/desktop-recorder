// Settled-frame rules for the AI capture folder (AI capture brief, section 6 and Phase 1).
// C# 5. No WinForms and no System.Drawing, so the Mono unit tests can compile this file.
//
// Input: decoded frames (bgr24, top row first) with their times on the time line of the video.
// Output: the frames to write as PNG, with the reason for each frame.
//
// Rules:
// - Changed = more than MinChangedPixels pixels differ by more than PixelTolerance in one color channel.
// - Settled = no change for SettleSeconds. The first frame of that still period is the candidate.
// - A candidate that is the same as the last written frame is not written.
// - A small candidate change waits BlinkSeconds. When the screen goes back to the last written frame
//   in that time (a blinking caret), the area becomes a mask and the change is not written.
//   A small area that changes 3 times in BlinkSeconds (a spinner) also becomes a mask.
//   Masked pixels do not count as changes. A mask ends MaskExpirySeconds after its last change.
// - During a vertical scroll, a frame is written when the scroll since the last written frame is more
//   than ScrollFraction of the frame height (condition A from Bran, 2026-11-19).
// - When the screen does not settle for StillIntervalSeconds, the current frame is written.
// - At the end, the last state is written when it is different from the last written frame.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

sealed class SettledOptions
{
    public double SettleSeconds = 0.4;
    public double StillIntervalSeconds = 5;
    public double ScrollFraction = 0.6;
    // Lossless work files have no encoder noise, so a small value is safe. With 24, a light selection color or a slow fade
    // was "no change" (bug hunt, 2026-11-24).
    public int PixelTolerance = 12;
    public int MinChangedPixels = 3;
    public double ShiftAcceptRatio = 0.75;  // a shift counts when it lowers the pixel error below this part
    // A shift counts only when this part of the edge pixels in the changed columns matches after the shift.
    // Reason: video content (Bran's Twitch recording, 2026-11-20) gave a false "scroll, 573 px up". A lower
    // error alone is not proof: moving video can match a shift a little better than no shift.
    // A real scroll of a lossless capture matches almost every edge pixel (see MatchedEdgeFraction).
    public double ShiftMatchRatio = 0.8;
    public int BlockMinPixels = 3;      // a block of 8 x 8 px counts when this many of its pixels changed
    public double BlinkSeconds = 1.5;
    public int SmallWidth = 48;
    public int SmallHeight = 64;
    public double MaskExpirySeconds = 3;
    // Moving content (task 2, 2026-11-20): index.md says so when the picture changed (not a scroll) during at
    // least this part of a recording of MotionMinSeconds or more. Bran's Twitch recording: 25.9 s, no settled frame.
    public double MotionFraction = 0.7;
    public double MotionMinSeconds = 8;
    // A frame pair counts as moving content only when one group of changed 8 x 8 blocks (blocks that touch) has a
    // short side of this many px or more, or of half the short side of the region when the region is smaller, or
    // when all changed blocks together cover that size squared (see LargeGroups).
    // Reason: a moving mouse pointer gave "the picture changed during 81%" on still pages (Bran's recordings of
    // 2026-11-22, pointer shown). The largest group of most of those pairs had a short side of 16 to 40 px at 125%.
    public int MotionMinSide = 96;
    // Bug hunt, 2026-11-24. A large state (a page, a menu, a dialog) counts as settled when no LARGE change came for
    // SettleSeconds: a moving pointer or a small animation does not matter. A large state of BriefSeconds (2 captures at
    // 6 fps) is written when another large state takes its place. After MaxBrief such frames, at most one each second.
    public double BriefSeconds = 0.3;
    public int MaxBrief = 60;
    // At the end, a blink mask with no change for this time does not hide its area any more (a caret still blinks).
    public double EndMaskQuietSeconds = 1.25;
}

// Kinds of written frames. The index text comes from RecordingIndex.Describe.
static class FrameKind
{
    public const string First = "first";
    public const string Settled = "settled";
    public const string ScrollPart = "scroll-part";
    public const string StillChanging = "still-changing";
    public const string Last = "last";
}

sealed class SettledFrame
{
    public int Number;              // 1, 2, 3 ... in the order of writing
    public double Time;             // seconds, on the time line of the video
    public byte[] Pixels;           // bgr24
    public string Kind;
    public int ScrollPixels;        // vertical scroll since the last written frame. Positive = the content moved up.
    public long ContentOffset;      // total vertical scroll since the start of the video, when this state was on the screen
    public long ChangedPixels;      // pixels that differ from the last written frame
    public double ChangedFraction;  // ChangedPixels / all pixels
    public int BoxX, BoxY, BoxWidth, BoxHeight;   // box around the changed pixels (0 when no box)
}

sealed class SettledFrameFinder
{
    struct Change
    {
        public long Pixels;
        public int X0, Y0, X1, Y1;   // inclusive
        public int Width { get { return Pixels == 0 ? 0 : X1 - X0 + 1; } }
        public int Height { get { return Pixels == 0 ? 0 : Y1 - Y0 + 1; } }
    }

    sealed class Mask
    {
        public int X0, Y0, X1, Y1;
        public double LastActive;
        public bool Spinner;   // made by RegisterSmall (many changes), not by a blink
    }

    struct SmallEvent
    {
        public double Time;
        public int X0, Y0, X1, Y1;
    }

    const int Bands = 8;

    readonly int width, height;
    readonly SettledOptions o;
    readonly List<SettledFrame> written = new List<SettledFrame>();
    readonly List<Mask> masks = new List<Mask>();
    readonly List<SmallEvent> recentSmall = new List<SmallEvent>();

    byte[] prev;
    int[] prevProfile;
    bool started;
    byte[] stateFrame;
    double stateStart;
    bool stateHandled;
    byte[] lastWritten;
    double lastStillOrWrite;
    int scrollSinceWritten;
    SettledFrame pending;
    double pendingDeadline;
    double lastTime;
    int[] blockCounts;
    long[] longA, longB;
    long totalScroll;
    long lastWrittenOffset;   // ContentOffset of the last written frame
    double prevTime;
    bool masksExpired;        // a mask ended since the last check of the still state
    int lastShift;            // the scroll step of the last frame pair (0 = no scroll)
    const int SmallSearch = 96;
    int scrollArea;           // height of the moving area during the scroll since the last written frame
    double firstTime;
    double largeStart;        // the last large change
    bool largeHandled = true;
    int briefFrames;
    readonly List<byte[]> recent = new List<byte[]>();   // pixels of the last written frames (RecentCount), the newest last
    const int RecentCount = 6, RepeatTolerance = 40;
    bool drifting;            // the state comes from a slow fade
    bool finishing;
    int tolerance = -1;       // -1 = PixelTolerance (CompareWith)
    double movingSeconds;     // time with changes that are not a scroll (frame pairs nearer than SettleSeconds)
    int moveX0 = int.MaxValue, moveY0 = int.MaxValue, moveX1 = -1, moveY1 = -1;   // box of those changes

    // Called for each written frame, in order. The receiver can keep or drop Pixels.
    public event Action<SettledFrame> FrameWritten;
    // Test output only. Null in the app.
    public static Action<string> Debug;
    public static readonly System.Diagnostics.Stopwatch TCompare = new System.Diagnostics.Stopwatch(), TShift = new System.Diagnostics.Stopwatch(), TFeed = new System.Diagnostics.Stopwatch();
    public static int NShift;

    public SettledFrameFinder(int width, int height, SettledOptions options)
    {
        if (width < 1 || height < 1) throw new ArgumentException("bad frame size");
        this.width = width;
        this.height = height;
        o = options ?? new SettledOptions();
    }

    public List<SettledFrame> Written { get { return written; } }
    // Seconds with changes that are not a scroll, and the box around them (see MotionReport).
    public double MovingSeconds { get { return movingSeconds; } }

    // A line for index.md when the picture did not settle for most of the recording, else null.
    // endTime: the end of the video, as for Finish.
    public string MotionReport(double endTime)
    {
        double length = endTime - firstTime;
        if (!started || length < o.MotionMinSeconds || moveX1 < 0) return null;
        double part = movingSeconds / length;
        if (part < o.MotionFraction) return null;
        int percent = (int)Math.Round(Math.Min(1, part) * 100);
        return "the picture changed during " + percent.ToString(CultureInfo.InvariantCulture)
            + "% of the recording (area x " + moveX0.ToString(CultureInfo.InvariantCulture)
            + ", y " + moveY0.ToString(CultureInfo.InvariantCulture)
            + ", " + (moveX1 - moveX0 + 1).ToString(CultureInfo.InvariantCulture)
            + " x " + (moveY1 - moveY0 + 1).ToString(CultureInfo.InvariantCulture)
            + " px), for example a video or an animation. Such content does not settle, so the frames in that time are samples every "
            + o.StillIntervalSeconds.ToString("0.#", CultureInfo.InvariantCulture)
            + " s (\"screen still changing\"). Use clip.mp4 to see the motion.";
    }
    public int MaskCount { get { return masks.Count; } }
    // Total vertical scroll since the first frame (positive = the view moved down the content).
    public long TotalScroll { get { return totalScroll; } }

    public void Feed(byte[] frame, double time)
    {
        if (frame == null || frame.Length < width * height * 3) throw new ArgumentException("short frame");
        lastTime = time;
        if (!started)
        {
            started = true;
            prev = Copy(frame);
            prevProfile = null;
            stateFrame = Copy(frame);
            stateStart = time;
            stateHandled = true;
            lastStillOrWrite = time;
            prevTime = time;
            firstTime = time;
            // The first frame is always written: it is the screen at the start. Without it, a recording that
            // starts during a scroll loses the first screens (checked on Bran's clip of 2026-11-19 14:02).
            Emit(MakeFrame(frame, time, FrameKind.First, new Change()));
            return;
        }
        ExpireMasks(time);
        CheckPending(time);
        // A mask ended. The still state was compared with the mask, so compare it again without it.
        // Reason: a value that changed 3 times and then stayed was hidden by the spinner mask (review 2026-11-19).
        if (masksExpired && stateHandled && lastWritten != null)
        {
            masksExpired = false;
            OnSettled(stateFrame, stateStart, time);
        }
        TCompare.Start();
        Change d = Compare(prev, frame, time, true);
        TCompare.Stop();
        // Moving content needs a large group of changed blocks (MotionMinSide). Found here: the next Compare
        // changes blockCounts. Only frame pairs near in time can count as motion (see below).
        int gx0 = 0, gy0 = 0, gx1 = -1, gy1 = -1;
        int largeSide = Math.Max(8, Math.Min(o.MotionMinSide, Math.Min(width, height) / 2));
        bool large = d.Pixels >= o.MinChangedPixels && LargeGroups(largeSide, out gx0, out gy0, out gx1, out gy1);
        bool largeGroup = large && time - prevTime <= o.SettleSeconds + 1e-9;
        bool stateIsStill = time - stateStart >= o.SettleSeconds - 1e-9;
        if (!stateHandled && stateIsStill)
        {
            stateHandled = true;
            OnSettled(stateFrame, stateStart, time);
        }
        // The screen was still until this frame: the interval for "screen still changing" starts again.
        if (stateIsStill && time > lastStillOrWrite) lastStillOrWrite = time;
        // The same rule for large changes only: a page or a menu under a moving pointer never had a still moment, and it
        // was lost when it went before the 5 s sample. Before the scroll code below, so ContentOffset belongs to prev.
        if (!largeHandled && time - largeStart >= o.SettleSeconds - 1e-9 && BriefAllowed(time))
        {
            largeHandled = true;
            Change lc = Compare(lastWritten, prev, prevTime, false);
            int lx0, ly0, lx1, ly1;
            if (lc.Pixels >= o.MinChangedPixels && LargeGroups(largeSide, out lx0, out ly0, out lx1, out ly1) && !IsRepeat(prev, time))
            {
                briefFrames++;
                WriteFrame(prev, prevTime, FrameKind.StillChanging, lc);
            }
        }
        if (large) { largeStart = time; largeHandled = false; }
        // A slow fade: each step is below the tolerance, but the picture moves away from the state. It is a new state when
        // the fade ends (no step of 2 or more). A state with a normal change is not delayed by a fade.
        if (d.Pixels >= o.MinChangedPixels) drifting = false;
        else if (drifting && !stateHandled ? CompareWith(1, prev, frame, time).Pixels >= o.MinChangedPixels
            : stateHandled && Compare(stateFrame, frame, time, false).Pixels >= o.MinChangedPixels)
        {
            drifting = true;
            Buffer.BlockCopy(frame, 0, stateFrame, 0, width * height * 3);
            stateStart = time;
            stateHandled = false;
            if (time - lastStillOrWrite >= o.StillIntervalSeconds - 1e-9)
            {
                WriteFrame(frame, time, FrameKind.StillChanging, Compare(lastWritten, frame, time, false));
                stateHandled = true;
            }
        }
        if (d.Pixels >= o.MinChangedPixels)
        {
            int dy = 0;
            // A scroll changes many pixels in a tall area. Scattered encoder noise does not: no shift search.
            // Panes down to height / 8 (at least 40 px) are checked, so a scroll in a small pane is found.
            bool tall = d.Height >= Math.Max(40, height / 8) && d.Pixels * 100L >= (long)d.Width * d.Height;
            if (!tall) lastShift = 0;
            if (tall)
            {
                TShift.Start(); NShift++;
                int[] profile = Profile(frame);
                if (prevProfile == null) prevProfile = Profile(prev);
                dy = VerifiedShift(prev, frame, prevProfile, profile, d.Y0, d.Height, d.X0, d.X1);
                prevProfile = profile;
                TShift.Stop();
                // The scroll rule uses the height of the moving area, not of the frame: a static header or
                // footer must not count (checked on Bran's clip: moving area 566 of 762 rows).
                if (dy != 0) scrollArea = scrollArea == 0 ? d.Height : Math.Min(scrollArea, d.Height);
            }
            else prevProfile = null;
            if (dy != 0 && lastWritten != null)
            {
                int area0 = scrollArea > 0 ? scrollArea : height;
                double limit = o.ScrollFraction * area0;
                bool reverses = scrollSinceWritten != 0 && Math.Sign(dy) != Math.Sign(scrollSinceWritten)
                    && Math.Abs(scrollSinceWritten) > 0.1 * area0;
                // Only when this step would leave a gap after the last written frame (more than 95% of the area).
                bool crosses = Math.Abs(scrollSinceWritten + dy) > 0.95 * area0 && scrollSinceWritten != 0;
                // Write the frame before this step when this step goes past the visible area, or when the scroll turns
                // back: that frame shows the furthest content. Without it, a large step or a turn can leave a gap
                // (review 2026-11-19). The written frame is the previous frame, at its own offset.
                if (crosses || reverses) WriteFrame(prev, prevTime, FrameKind.ScrollPart, Compare(lastWritten, prev, prevTime, false));
            }
            // A large state of 2 captures (BriefSeconds) that another large state replaces: a page, a tab or a dialog that
            // shows for 0.33 s at 6 fps. Not in a scroll.
            if (dy == 0 && largeGroup && !stateHandled && lastWritten != null && time - stateStart >= o.BriefSeconds - 1e-9 && BriefAllowed(time))
            {
                Change bc = Compare(lastWritten, stateFrame, time, false);
                int bx0, by0, bx1, by1;
                if (bc.Pixels >= o.MinChangedPixels && LargeGroups(largeSide, out bx0, out by0, out bx1, out by1) && !IsRepeat(stateFrame, time))
                {
                    briefFrames++;
                    double keep = lastStillOrWrite;   // it must not delay the 5 s sample
                    WriteFrame(stateFrame, stateStart, FrameKind.Settled, bc);
                    lastStillOrWrite = keep;
                }
            }
            scrollSinceWritten += dy;
            totalScroll += dy;
            // Moving content: a large change that is not a scroll, close in time to the frame before. In "changes" mode
            // a still screen keeps 1 frame each second, so a gap longer than SettleSeconds is still time, not motion.
            // A moving pointer, a caret or a spinner gives only small groups: no motion (2026-11-22).
            if (dy == 0 && largeGroup)
            {
                movingSeconds += time - prevTime;
                if (Debug != null) Debug("motion t=" + time.ToString("0.000", CultureInfo.InvariantCulture)
                    + " dt=" + (time - prevTime).ToString("0.000", CultureInfo.InvariantCulture)
                    + " px=" + d.Pixels + " area=" + gx0 + "," + gy0 + "," + (gx1 - gx0 + 1) + "x" + (gy1 - gy0 + 1));
                if (gx0 < moveX0) moveX0 = gx0;
                if (gy0 < moveY0) moveY0 = gy0;
                if (gx1 > moveX1) moveX1 = gx1;
                if (gy1 > moveY1) moveY1 = gy1;
            }
            if (IsSmall(d)) RegisterSmall(d, time);
            Buffer.BlockCopy(frame, 0, stateFrame, 0, width * height * 3);
            stateStart = time;
            stateHandled = false;
            int area = scrollArea > 0 ? scrollArea : height;
            if (lastWritten != null && Math.Abs(scrollSinceWritten) > o.ScrollFraction * area)
            {
                WriteFrame(frame, time, FrameKind.ScrollPart, Compare(lastWritten, frame, time, false));
                stateHandled = true;   // this state is written already
            }
            else if (time - lastStillOrWrite >= o.StillIntervalSeconds - 1e-9)
            {
                Change c = lastWritten != null ? Compare(lastWritten, frame, time, false) : new Change();
                WriteFrame(frame, time, lastWritten == null ? FrameKind.First : FrameKind.StillChanging, c);
                stateHandled = true;
            }
        }
        Buffer.BlockCopy(frame, 0, prev, 0, width * height * 3);
        prevTime = time;
    }

    // True for a state that the last written frames show two times already (a placeholder that pulses, a blinking label):
    // A, B, A, B, and now A again. It shows nothing new, so it is not written for StillIntervalSeconds. The large
    // tolerance is for a pulse that the capture sees at different brightness. A text change has larger differences.
    bool IsRepeat(byte[] s, double time)
    {
        if (written.Count < 4 || time - written[written.Count - 1].Time >= o.StillIntervalSeconds - 1e-9) return false;
        int same = 0;
        for (int i = 0; i < recent.Count - 1; i++)
            if (CompareWith(RepeatTolerance, recent[i], s, time).Pixels < o.MinChangedPixels) same++;
        return same >= 2;
    }

    // Content that changes each 0.3 s or more slowly for minutes (an animation) must not give thousands of frames.
    bool BriefAllowed(double time)
    {
        return briefFrames < o.MaxBrief || written.Count == 0 || time - written[written.Count - 1].Time >= 1.0;
    }

    // endTime: the end of the video (the time of the last frame plus its length).
    public void Finish(double endTime)
    {
        if (!started) return;
        if (endTime < lastTime) endTime = lastTime;
        finishing = true;
        CheckPending(endTime);
        ExpireMasks(endTime);
        if (masksExpired && stateHandled && lastWritten != null)
        {
            masksExpired = false;
            OnSettled(stateFrame, stateStart, endTime);
        }
        if (!stateHandled)
        {
            stateHandled = true;
            if (endTime - stateStart >= o.SettleSeconds - 1e-9) OnSettled(stateFrame, stateStart, endTime);
            else
            {
                // The screen changed until the end. Keep what the user saw last.
                FlushPending();
                Change c = lastWritten != null ? Compare(lastWritten, stateFrame, endTime, false) : new Change();
                if (lastWritten == null || c.Pixels >= o.MinChangedPixels)
                    WriteFrame(stateFrame, stateStart, lastWritten == null ? FrameKind.First : FrameKind.Last, c);
            }
        }
        FlushPending();
        // The result of a spinner or a counter can be inside a mask. Compare the last frame without the masks (but a
        // caret that still blinks stays masked). prev, not stateFrame: a change inside a mask is not in stateFrame.
        double t = stateStart;
        bool dropped = false;
        for (int i = masks.Count - 1; i >= 0; i--)
        {
            Mask m = masks[i];
            if (!m.Spinner && endTime - m.LastActive < o.EndMaskQuietSeconds) continue;
            if (m.LastActive > t) t = m.LastActive;
            masks.RemoveAt(i);
            dropped = true;
        }
        if (!dropped || lastWritten == null) return;
        Change last = Compare(lastWritten, prev, endTime, false);
        if (last.Pixels >= o.MinChangedPixels)
            WriteFrame(prev, t, endTime - t >= o.SettleSeconds - 1e-9 ? FrameKind.Settled : FrameKind.Last, last);
    }

    void OnSettled(byte[] s, double t, double now)
    {
        lastStillOrWrite = now;
        if (lastWritten == null)
        {
            WriteFrame(s, t, FrameKind.First, new Change());
            return;
        }
        Change d = Compare(lastWritten, s, now, false);
        if (d.Pixels < o.MinChangedPixels)
        {
            // Back to the last written frame. A pending small change was a blink.
            if (pending != null)
            {
                AddMask(pending.BoxX, pending.BoxY, pending.BoxX + pending.BoxWidth - 1, pending.BoxY + pending.BoxHeight - 1, now, false);
                pending = null;
            }
            scrollSinceWritten = (int)(totalScroll - lastWrittenOffset);
            return;
        }
        if (pending != null)
        {
            Change dp = Compare(pending.Pixels, s, now, false);
            if (dp.Pixels < o.MinChangedPixels) return;   // still the pending state
            FlushPending();
            d = Compare(lastWritten, s, now, false);
            if (d.Pixels < o.MinChangedPixels) return;
        }
        // Not at the end (the last state always counts), and not when the state stayed.
        if (!finishing && scrollSinceWritten == 0 && now - t < o.BlinkSeconds - 1e-9 && IsRepeat(s, now)) return;
        // A small state that stayed BlinkSeconds is not a blink: write it now. (The pass gets no equal frames, so "now"
        // can be long after the state. Waiting longer lost the state when the next one went back.)
        if (IsSmall(d) && scrollSinceWritten == 0 && now - t < o.BlinkSeconds - 1e-9)
        {
            pending = MakeFrame(s, t, FrameKind.Settled, d);
            pendingDeadline = now + o.BlinkSeconds;
            return;
        }
        WriteFrame(s, t, FrameKind.Settled, d);
    }

    void CheckPending(double time)
    {
        if (pending != null && time >= pendingDeadline - 1e-9) FlushPending();
    }

    void FlushPending()
    {
        if (pending == null) return;
        SettledFrame p = pending;
        pending = null;
        // The change can be inside a mask that started after it was found (a spinner).
        Change d = Compare(lastWritten, p.Pixels, lastTime, false);
        if (d.Pixels < o.MinChangedPixels) return;
        Emit(p);
    }

    void WriteFrame(byte[] frame, double time, string kind, Change d)
    {
        if (pending != null && pending.Time < time) FlushPending();
        Emit(MakeFrame(frame, time, kind, d));
    }

    SettledFrame MakeFrame(byte[] frame, double time, string kind, Change d)
    {
        SettledFrame f = new SettledFrame();
        f.Time = time;
        f.Pixels = Copy(frame);
        f.Kind = kind;
        f.ContentOffset = totalScroll;
        f.ChangedPixels = d.Pixels;
        f.ChangedFraction = d.Pixels / (double)((long)width * height);
        if (d.Pixels > 0)
        {
            f.BoxX = d.X0;
            f.BoxY = d.Y0;
            f.BoxWidth = d.Width;
            f.BoxHeight = d.Height;
        }
        return f;
    }

    void Emit(SettledFrame f)
    {
        // Scroll since the last written frame, from the offsets. A pending frame from before a scroll keeps
        // the scroll that came after it in the count (review 2026-11-19).
        f.ScrollPixels = (int)(f.ContentOffset - lastWrittenOffset);
        lastWrittenOffset = f.ContentOffset;
        scrollSinceWritten = (int)(totalScroll - f.ContentOffset);
        if (scrollSinceWritten == 0) scrollArea = 0;
        f.Number = written.Count + 1;
        if (lastWritten == null) lastWritten = Copy(f.Pixels);
        else Buffer.BlockCopy(f.Pixels, 0, lastWritten, 0, lastWritten.Length);
        if (f.Time > lastStillOrWrite) lastStillOrWrite = f.Time;
        if (f.ChangedFraction > 0.3) masks.Clear();
        written.Add(f);
        recent.Add(f.Pixels);
        if (recent.Count > RecentCount) recent.RemoveAt(0);
        Action<SettledFrame> h = FrameWritten;
        if (h != null) h(f);
    }

    bool IsSmall(Change d)
    {
        return d.Pixels > 0 && d.Width <= o.SmallWidth && d.Height <= o.SmallHeight;
    }

    void RegisterSmall(Change d, double time)
    {
        SmallEvent e = new SmallEvent();
        e.Time = time;
        e.X0 = d.X0; e.Y0 = d.Y0; e.X1 = d.X1; e.Y1 = d.Y1;
        recentSmall.RemoveAll(delegate(SmallEvent x) { return time - x.Time > o.BlinkSeconds; });
        int same = 1;
        int ux0 = e.X0, uy0 = e.Y0, ux1 = e.X1, uy1 = e.Y1;
        foreach (SmallEvent x in recentSmall)
        {
            if (Iou(x.X0, x.Y0, x.X1, x.Y1, e.X0, e.Y0, e.X1, e.Y1) >= 0.5)
            {
                same++;
                ux0 = Math.Min(ux0, x.X0); uy0 = Math.Min(uy0, x.Y0);
                ux1 = Math.Max(ux1, x.X1); uy1 = Math.Max(uy1, x.Y1);
            }
        }
        recentSmall.Add(e);
        if (same >= 3)
        {
            AddMask(ux0, uy0, ux1, uy1, time, true);
            if (pending != null && pending.BoxX >= ux0 - 2 && pending.BoxY >= uy0 - 2
                && pending.BoxX + pending.BoxWidth - 1 <= ux1 + 2 && pending.BoxY + pending.BoxHeight - 1 <= uy1 + 2)
                pending = null;
        }
    }

    static double Iou(int ax0, int ay0, int ax1, int ay1, int bx0, int by0, int bx1, int by1)
    {
        int ix = Math.Min(ax1, bx1) - Math.Max(ax0, bx0) + 1;
        int iy = Math.Min(ay1, by1) - Math.Max(ay0, by0) + 1;
        if (ix <= 0 || iy <= 0) return 0;
        double inter = (double)ix * iy;
        double a = (double)(ax1 - ax0 + 1) * (ay1 - ay0 + 1);
        double b = (double)(bx1 - bx0 + 1) * (by1 - by0 + 1);
        return inter / (a + b - inter);
    }

    void AddMask(int x0, int y0, int x1, int y1, double time, bool spinner)
    {
        Mask m = new Mask();
        m.X0 = Math.Max(0, x0 - 2);
        m.Y0 = Math.Max(0, y0 - 2);
        m.X1 = Math.Min(width - 1, x1 + 2);
        m.Y1 = Math.Min(height - 1, y1 + 2);
        m.LastActive = time;
        m.Spinner = spinner;
        masks.Add(m);
        if (masks.Count > 8) masks.RemoveAt(0);
    }

    void ExpireMasks(double time)
    {
        double last = -1;
        if (masks.RemoveAll(delegate(Mask m)
        {
            if (time - m.LastActive <= o.MaskExpirySeconds) return false;
            if (m.LastActive > last) last = m.LastActive;
            return true;
        }) == 0) return;
        masksExpired = true;
        // A change inside a mask is not in stateFrame. Take the screen as it was in the last frame, with the time of the
        // last change in the mask: the final value of a counter was never written (bug hunt, 2026-11-24).
        Buffer.BlockCopy(prev, 0, stateFrame, 0, width * height * 3);
        if (last > stateStart) stateStart = last;
    }

    // Counts the pixels that differ (outside the masks) and the box around them.
    // Only 8 x 8 blocks with BlockMinPixels or more changed pixels count. Reason: a lossy video has single
    // changed pixels all over a still picture (encoder refinement, HANDOFF 6.6). A real change (a character,
    // a caret) makes a group of pixels in one block.
    // track = true: a change inside a mask keeps that mask active.
    Change Compare(byte[] a, byte[] b, double time, bool track)
    {
        int bw = (width + 7) / 8, bh = (height + 7) / 8;
        if (blockCounts == null || blockCounts.Length != bw * bh) blockCounts = new int[bw * bh];
        else Array.Clear(blockCounts, 0, blockCounts.Length);
        int longs = (a.Length + 7) / 8;
        if (longA == null || longA.Length != longs) { longA = new long[longs]; longB = new long[longs]; }
        Buffer.BlockCopy(a, 0, longA, 0, a.Length);
        Buffer.BlockCopy(b, 0, longB, 0, b.Length);
        Mask[] maskArray = masks.ToArray();
        // Each block row (8 pixel rows) writes only its own counts, so the rows can run in parallel.
        System.Threading.Tasks.Parallel.For(0, bh, delegate(int by)
        {
            CompareBlockRow(a, b, by, bw, maskArray, time, track);
        });
        Change c = new Change();
        c.X0 = int.MaxValue; c.Y0 = int.MaxValue; c.X1 = -1; c.Y1 = -1;
        int minBlock = Math.Max(1, o.BlockMinPixels);
        for (int by = 0; by < bh; by++)
        {
            for (int bx = 0; bx < bw; bx++)
            {
                int n = blockCounts[by * bw + bx];
                if (n < minBlock) continue;
                c.Pixels += n;
                int x0 = bx * 8, y0 = by * 8;
                int x1 = Math.Min(width - 1, x0 + 7), y1 = Math.Min(height - 1, y0 + 7);
                if (x0 < c.X0) c.X0 = x0;
                if (x1 > c.X1) c.X1 = x1;
                if (y0 < c.Y0) c.Y0 = y0;
                if (y1 > c.Y1) c.Y1 = y1;
            }
        }
        if (c.Pixels == 0) { c.X0 = 0; c.Y0 = 0; c.X1 = 0; c.Y1 = 0; }
        return c;
    }

    Change CompareWith(int tol, byte[] a, byte[] b, double time)
    {
        tolerance = tol;
        try { return Compare(a, b, time, false); }
        finally { tolerance = -1; }
    }

    void CompareBlockRow(byte[] a, byte[] b, int by, int bw, Mask[] maskArray, double time, bool track)
    {
        int tol = tolerance >= 0 ? tolerance : o.PixelTolerance;
        int stride = width * 3;
        int maskCount = maskArray.Length;
        int yEnd = Math.Min(height, by * 8 + 8);
        int blockRow = by * bw;
        for (int y = by * 8; y < yEnd; y++)
        {
            int row = y * stride;
            int end = row + stride;
            // Fast path: a row with no different byte is skipped. Most rows of a lossless still screen are equal.
            if (RowEqual(a, b, row, end)) continue;
            for (int i = row; i < end; i += 3)
            {
                int d0 = a[i] - b[i]; if (d0 < 0) d0 = -d0;
                int d1 = a[i + 1] - b[i + 1]; if (d1 < 0) d1 = -d1;
                int d2 = a[i + 2] - b[i + 2]; if (d2 < 0) d2 = -d2;
                if (d0 <= tol && d1 <= tol && d2 <= tol) continue;
                int x = (i - row) / 3;
                if (maskCount > 0)
                {
                    bool inMask = false;
                    for (int k = 0; k < maskCount; k++)
                    {
                        Mask m = maskArray[k];
                        if (x >= m.X0 && x <= m.X1 && y >= m.Y0 && y <= m.Y1)
                        {
                            inMask = true;
                            if (track) m.LastActive = time;
                            break;
                        }
                    }
                    if (inMask) continue;
                }
                blockCounts[blockRow + (x >> 3)]++;
            }
        }
    }

    int[] groupMark, groupQueue;

    // Groups of changed 8 x 8 blocks of the last Compare (the blocks of a group touch at a side or at a corner).
    // True for moving content: a group has a short side of minSide px or more, or all changed blocks together cover
    // minSide x minSide px or more (many small moving parts, for example a dark video; review 2026-11-22). A moving
    // pointer gives 1 to 3 small groups. x0 to y1: the box (px, inclusive) around the large groups, or around all
    // groups when only the total is large. Call it right after Compare: the next Compare changes blockCounts.
    bool LargeGroups(int minSide, out int x0Large, out int y0Large, out int x1Large, out int y1Large)
    {
        x0Large = int.MaxValue; y0Large = int.MaxValue; x1Large = -1; y1Large = -1;
        int ax0 = int.MaxValue, ay0 = int.MaxValue, ax1 = -1, ay1 = -1;
        long blocks = 0;
        int bw = (width + 7) / 8, bh = (height + 7) / 8, n = bw * bh;
        int minBlock = Math.Max(1, o.BlockMinPixels);
        if (groupMark == null || groupMark.Length != n) { groupMark = new int[n]; groupQueue = new int[n]; }
        else Array.Clear(groupMark, 0, n);
        for (int start = 0; start < n; start++)
        {
            if (groupMark[start] != 0 || blockCounts[start] < minBlock) continue;
            int head = 0, tail = 0;
            groupQueue[tail++] = start;
            groupMark[start] = 1;
            int x0 = int.MaxValue, y0 = int.MaxValue, x1 = -1, y1 = -1;
            while (head < tail)
            {
                int c = groupQueue[head++];
                int cx = c % bw, cy = c / bw;
                if (cx < x0) x0 = cx;
                if (cx > x1) x1 = cx;
                if (cy < y0) y0 = cy;
                if (cy > y1) y1 = cy;
                for (int ny = Math.Max(0, cy - 1); ny <= Math.Min(bh - 1, cy + 1); ny++)
                    for (int nx = Math.Max(0, cx - 1); nx <= Math.Min(bw - 1, cx + 1); nx++)
                    {
                        int k = ny * bw + nx;
                        if (groupMark[k] != 0 || blockCounts[k] < minBlock) continue;
                        groupMark[k] = 1;
                        groupQueue[tail++] = k;
                    }
            }
            int px0 = x0 * 8, py0 = y0 * 8, px1 = Math.Min(width, x1 * 8 + 8) - 1, py1 = Math.Min(height, y1 * 8 + 8) - 1;
            blocks += tail;
            if (px0 < ax0) ax0 = px0;
            if (py0 < ay0) ay0 = py0;
            if (px1 > ax1) ax1 = px1;
            if (py1 > ay1) ay1 = py1;
            if (Math.Min(px1 - px0 + 1, py1 - py0 + 1) < minSide) continue;
            if (px0 < x0Large) x0Large = px0;
            if (py0 < y0Large) y0Large = py0;
            if (px1 > x1Large) x1Large = px1;
            if (py1 > y1Large) y1Large = py1;
        }
        if (x1Large >= 0) return true;
        if (blocks * 64 < (long)minSide * minSide) return false;
        x0Large = ax0; y0Large = ay0; x1Large = ax1; y1Large = ay1;
        return true;
    }

    bool RowEqual(byte[] a, byte[] b, int start, int end)
    {
        int i = start;
        while (i < end && (i & 7) != 0) { if (a[i] != b[i]) return false; i++; }
        while (i + 8 <= end) { if (longA[i >> 3] != longB[i >> 3]) return false; i += 8; }
        while (i < end) { if (a[i] != b[i]) return false; i++; }
        return true;
    }

    // Edge strength of each row in each of 8 vertical bands: sum of |gray(x+1) - gray(x)|.
    int[] Profile(byte[] f)
    {
        int[] p = new int[Bands * height];
        int stride = width * 3;
        System.Threading.Tasks.Parallel.For(0, height, delegate(int y)
        {
            int row = y * stride;
            int prevGray = (f[row] + 2 * f[row + 1] + f[row + 2]) >> 2;
            for (int x = 1; x < width; x++)
            {
                int i = row + x * 3;
                int g = (f[i] + 2 * f[i + 1] + f[i + 2]) >> 2;
                int diff = g - prevGray; if (diff < 0) diff = -diff;
                prevGray = g;
                int band = x * Bands / width;
                p[band * height + y] += diff;
            }
        });
        return p;
    }

    // Shift from the profiles, checked with the pixels: the shifted picture must match much better than the
    // picture without a shift. Only the rows that changed count (y0 to y1), so a static header or footer
    // does not hide the scroll. Each candidate from the band votes is checked; the best one wins.
    // Reason: text lines repeat, so a profile alone can give a wrong shift (seen as +572 then -572 px).
    int VerifiedShift(byte[] a, byte[] b, int[] pa, int[] pb, int y0, int rows, int x0, int x1)
    {
        List<int> candidates = ShiftCandidates(pa, pb, height, y0, rows);
        // Also the step of the frame before (a scroll keeps its speed), and a full search of small steps.
        // Reason: on a page of near-identical lines the band votes can miss the true step (seen on the
        // synthetic "pages" video: 919 px found, 40 px true).
        if (lastShift != 0) candidates.Add(lastShift);
        if (candidates.Count == 0 && SmallSearch == 0) return 0;
        double err0 = PixelError(a, b, 0, y0, rows, x0, x1, 2, 4);
        // Pass 1: a sparse grid for every candidate (each vote +-2 px, and every step up to SmallSearch px).
        List<int> shifts = new List<int>();
        foreach (int s in candidates)
            for (int fine = s - 2; fine <= s + 2; fine++)
                if (fine != 0 && !shifts.Contains(fine)) shifts.Add(fine);
        for (int s = -SmallSearch; s <= SmallSearch; s += 2)
            if (s != 0 && !shifts.Contains(s)) shifts.Add(s);
        double[] coarseErr = new double[shifts.Count];
        System.Threading.Tasks.Parallel.For(0, shifts.Count, delegate(int k)
        {
            coarseErr[k] = PixelError(a, b, shifts[k], y0, rows, x0, x1, 6, 8);
        });
        List<KeyValuePair<int, double>> order = new List<KeyValuePair<int, double>>();
        for (int k = 0; k < shifts.Count; k++) order.Add(new KeyValuePair<int, double>(shifts[k], coarseErr[k]));
        order.Sort(delegate(KeyValuePair<int, double> x, KeyValuePair<int, double> y) { return x.Value.CompareTo(y.Value); });
        // Pass 2: the full grid for the 6 best, +-1 px.
        List<int> fineShifts = new List<int>();
        for (int k = 0; k < order.Count && k < 6; k++)
            for (int fine = order[k].Key - 1; fine <= order[k].Key + 1; fine++)
                if (fine != 0 && !fineShifts.Contains(fine)) fineShifts.Add(fine);
        // The band votes and the step before always get the full grid too. Reason: on a sparse page the coarse
        // grid gives an error of 0 to many shifts, and the sort does not keep an order for ties, so the true step
        // could drop out (found with a synthetic page on 2026-11-20: 4 of 60 steps lost).
        foreach (int s in candidates)
            for (int fine = s - 1; fine <= s + 1; fine++)
                if (fine != 0 && !fineShifts.Contains(fine)) fineShifts.Add(fine);
        double[] fineErr = new double[fineShifts.Count];
        System.Threading.Tasks.Parallel.For(0, fineShifts.Count, delegate(int k)
        {
            fineErr[k] = PixelError(a, b, fineShifts[k], y0, rows, x0, x1, 2, 4);
        });
        Dictionary<int, double> tried = new Dictionary<int, double>();
        int best = 0;
        double bestErr = double.MaxValue;
        for (int k = 0; k < fineShifts.Count; k++)
        {
            tried[fineShifts[k]] = fineErr[k];
            if (fineErr[k] < bestErr) { bestErr = fineErr[k]; best = fineShifts[k]; }
        }
        // Near ties (repeated lines): take the step nearest to the step before.
        int chosen = best;
        foreach (KeyValuePair<int, double> kv in tried)
        {
            if (kv.Value > bestErr * 1.03) continue;
            if (Math.Abs(kv.Key - lastShift) < Math.Abs(chosen - lastShift)) chosen = kv.Key;
        }
        best = chosen;
        bestErr = tried.ContainsKey(best) ? tried[best] : bestErr;
        if (Debug != null) Debug("shift rows=" + y0 + "+" + rows + " candidates=" + string.Join(",", candidates.ConvertAll(delegate(int v) { return v.ToString(); }).ToArray()) + " best=" + best + " err=" + bestErr.ToString("0.0") + " err0=" + err0.ToString("0.0"));
        if (best == 0 || !(bestErr < o.ShiftAcceptRatio * err0)) { lastShift = 0; return 0; }
        // The shifted rows must match well, not only better than no shift (task 2, 2026-11-20).
        double match = MatchedEdgeFraction(a, b, best, y0, rows, x0, x1);
        if (Debug != null) Debug("shift " + best + " match=" + match.ToString("0.000"));
        if (!(match >= o.ShiftMatchRatio)) { lastShift = 0; RejectedShifts++; return 0; }
        lastShift = best;
        return best;
    }

    // Shifts that had a lower error but did not match well (VerifiedShift). For tests and the pass report.
    public int RejectedShifts;

    // The part of the edge pixels of b (rows y0 to y0 + rows, columns x0 to x1) that a matches at the shift s:
    // b[y][x] against a[y + s][x], each color channel within PixelTolerance.
    // Only edge pixels count (gray step to the right or down of 24 or more): a flat area matches every shift,
    // so it proves nothing. Fewer than 64 edge pixels: 0 (a shift cannot be proved).
    internal double MatchedEdgeFraction(byte[] a, byte[] b, int s, int y0, int rows, int x0, int x1)
    {
        int lo = Math.Max(y0, y0 - s), hi = Math.Min(y0 + rows, y0 + rows - s);
        hi = Math.Min(hi, height - 1);   // the row below must exist (vertical step)
        x0 = Math.Max(0, x0);
        x1 = Math.Min(width - 2, x1);    // the pixel to the right must exist
        if (hi <= lo || x1 < x0) return 0;
        int stride = width * 3;
        int tol = o.PixelTolerance;
        long edges = 0, matched = 0;
        for (int y = lo; y < hi; y += 2)
        {
            int rb = y * stride, ra = (y + s) * stride;
            for (int x = x0; x <= x1; x += 2)
            {
                int i = rb + x * 3;
                int g = b[i] + 2 * b[i + 1] + b[i + 2];
                int gr = b[i + 3] + 2 * b[i + 4] + b[i + 5];
                int gd = b[i + stride] + 2 * b[i + stride + 1] + b[i + stride + 2];
                int dx = g - gr; if (dx < 0) dx = -dx;
                int dyv = g - gd; if (dyv < 0) dyv = -dyv;
                if (dx < 96 && dyv < 96) continue;   // 96 = a step of 24 in each channel (gray x 4)
                edges++;
                int j = ra + x * 3;
                int c0 = a[j] - b[i]; if (c0 < 0) c0 = -c0;
                int c1 = a[j + 1] - b[i + 1]; if (c1 < 0) c1 = -c1;
                int c2 = a[j + 2] - b[i + 2]; if (c2 < 0) c2 = -c2;
                if (c0 <= tol && c1 <= tol && c2 <= tol) matched++;
            }
        }
        if (edges < 64) return 0;
        return matched / (double)edges;
    }

    // Mean gray difference between b[y] and a[y + s] for rows y0 to y0 + rows, on a grid
    // (every 2nd row, every 4th pixel). Only the changed columns x0 to x1: a sidebar that stands still hid the scroll.
    double PixelError(byte[] a, byte[] b, int s, int y0, int rows, int x0, int x1, int rowStep, int colStep)
    {
        int lo = Math.Max(y0, y0 - s), hi = Math.Min(y0 + rows, y0 + rows - s);
        if (hi - lo < Math.Max(8, rows / 5)) return double.MaxValue;
        int stride = width * 3;
        long sum = 0, n = 0;
        for (int y = lo; y < hi; y += rowStep)
        {
            int rb = y * stride, ra = (y + s) * stride;
            for (int x = x0; x <= x1; x += colStep)
            {
                int i = x * 3;
                int ga = a[ra + i] + 2 * a[ra + i + 1] + a[ra + i + 2];
                int gb = b[rb + i] + 2 * b[rb + i + 1] + b[rb + i + 2];
                int d = ga - gb;
                sum += d < 0 ? -d : d;
                n++;
            }
        }
        return n == 0 ? double.MaxValue : sum / (double)n;
    }

    // Candidate shifts from the band profiles (rows y0 to y0 + rows), most votes first.
    internal static List<int> ShiftCandidates(int[] a, int[] b, int height, int y0, int rows)
    {
        List<int> votes = new List<int>();
        int minOverlap = Math.Max(8, rows / 5);
        for (int band = 0; band < Bands; band++)
        {
            int off = band * height + y0;
            long texture = 0;
            for (int y = 0; y < rows; y++) texture += a[off + y] + b[off + y];
            if (texture < rows * 8L) continue;
            int s = BandShift(a, b, off, rows, minOverlap);
            if (s != 0) votes.Add(s);
        }
        List<int> result = new List<int>();
        List<int> counts = new List<int>();
        foreach (int v in votes)
        {
            bool merged = false;
            for (int k = 0; k < result.Count; k++)
                if (Math.Abs(result[k] - v) <= 2) { counts[k]++; merged = true; break; }
            if (!merged) { result.Add(v); counts.Add(1); }
        }
        for (int i = 0; i < result.Count; i++)
            for (int j = i + 1; j < result.Count; j++)
                if (counts[j] > counts[i])
                {
                    int t = counts[i]; counts[i] = counts[j]; counts[j] = t;
                    t = result[i]; result[i] = result[j]; result[j] = t;
                }
        if (result.Count > 4) result.RemoveRange(4, result.Count - 4);
        return result;
    }

    // Best shift for one band (profile rows off to off + height). 0 when no shift is clearly better.
    static int BandShift(int[] a, int[] b, int off, int height, int minOverlap)
    {
        int h4 = height / 4;
        if (h4 < 4) return 0;
        long[] a4 = new long[h4], b4 = new long[h4];
        for (int i = 0; i < h4; i++)
        {
            for (int k = 0; k < 4; k++) { a4[i] += a[off + i * 4 + k]; b4[i] += b[off + i * 4 + k]; }
        }
        int min4 = Math.Max(2, minOverlap / 4);
        double err0 = Err(a, b, off, height, 0);
        int bestS4 = 0;
        double best4 = double.MaxValue;
        for (int s = -(h4 - min4); s <= h4 - min4; s++)
        {
            double e = Err4(a4, b4, h4, s);
            if (e < best4) { best4 = e; bestS4 = s; }
        }
        int bestS = 0;
        double best = double.MaxValue;
        for (int s = bestS4 * 4 - 4; s <= bestS4 * 4 + 4; s++)
        {
            if (Math.Abs(s) > height - minOverlap) continue;
            double e = Err(a, b, off, height, s);
            if (e < best || (e == best && Math.Abs(s) < Math.Abs(bestS))) { best = e; bestS = s; }
        }
        if (bestS == 0) return 0;
        if (!(best < 0.6 * err0)) return 0;
        return bestS;
    }

    static double Err(int[] a, int[] b, int off, int height, int s)
    {
        int y0 = Math.Max(0, -s), y1 = Math.Min(height, height - s);
        if (y1 <= y0) return double.MaxValue;
        long sum = 0;
        for (int y = y0; y < y1; y++)
        {
            int d = a[off + y + s] - b[off + y];
            sum += d < 0 ? -d : d;
        }
        return sum / (double)(y1 - y0);
    }

    static double Err4(long[] a, long[] b, int n, int s)
    {
        int y0 = Math.Max(0, -s), y1 = Math.Min(n, n - s);
        if (y1 <= y0) return double.MaxValue;
        long sum = 0;
        for (int y = y0; y < y1; y++)
        {
            long d = a[y + s] - b[y];
            sum += d < 0 ? -d : d;
        }
        return sum / (double)(y1 - y0);
    }

    static byte[] Copy(byte[] f)
    {
        byte[] c = new byte[f.Length];
        Buffer.BlockCopy(f, 0, c, 0, f.Length);
        return c;
    }
}

// Window titles sampled during the recording, on the time line of the video.
sealed class TitleTimeline
{
    readonly List<double> times = new List<double>();
    readonly List<string> titles = new List<string>();
    readonly object gate = new object();

    public void Add(double time, string title)
    {
        lock (gate)
        {
            string t = title ?? "";
            if (titles.Count > 0 && titles[titles.Count - 1] == t) return;
            times.Add(time);
            titles.Add(t);
        }
    }

    // The last title at or before the time. Before the first sample: the first title.
    public string At(double time)
    {
        lock (gate)
        {
            if (titles.Count == 0) return "";
            string result = titles[0];
            for (int i = 0; i < times.Count; i++)
            {
                if (times[i] <= time + 1e-9) result = titles[i];
                else break;
            }
            return result;
        }
    }

    public int Count { get { lock (gate) return titles.Count; } }
}

// Text of index.md (brief section 6).
static class RecordingIndex
{
    public const int FileWarningLimit = 20;

    // The instruction at the top of index.md, for an AI that gets the files without a text message.
    public const string AiNote =
        "For the AI that reads this file: this file is in a ZIP file. The ZIP is one screen recording that Bran made. "
        + "It holds this file, the PNG images in the frames folder, and clip.mp4. "
        + "They are context for Bran's message. Read this file first. Then look at each image: image 001.png is frame 001 in the table, and so on. "
        + "Read the text on the screen in each image. Then do what Bran's message asks. "
        + "If Bran's message has no question, tell him in a few lines what the recording shows, and ask what he wants.\n";

    // True when the recording folder may be deleted (Bran, 2026-11-22). Folder mode gives one ZIP.
    // The folder stays when the pass failed (it holds the lossless work file), when a ZIP is not made or not done
    // (a note makes the ZIP again), when the last ZIP failed, and while the note box is open.
    public static bool CanDeleteFolder(bool passDone, bool passOk, bool zipExists, int zipPending, bool zipFailed, bool noteOpen)
    {
        return CanDeleteFolder(passDone, passOk, zipExists, zipPending, zipFailed, noteOpen, false);
    }

    // transcriptPending: Whisper still reads and writes files in the folder.
    public static bool CanDeleteFolder(bool passDone, bool passOk, bool zipExists, int zipPending, bool zipFailed, bool noteOpen, bool transcriptPending)
    {
        return CanDeleteFolder(passDone, passOk, zipExists, zipPending, zipFailed, noteOpen, transcriptPending, false);
    }

    // soundOnlyInWav: the sound is not in clip.mp4, and the ZIP has no WAV file. The WAV in the folder is the only copy.
    public static bool CanDeleteFolder(bool passDone, bool passOk, bool zipExists, int zipPending, bool zipFailed, bool noteOpen, bool transcriptPending, bool soundOnlyInWav)
    {
        return passDone && passOk && zipExists && zipPending == 0 && !zipFailed && !noteOpen && !transcriptPending && !soundOnlyInWav;
    }

    // A recording folder that the app did not finish (the app or the PC stopped): the work file is there, and no ZIP.
    // marker: the pass ran to its end on this folder (a failed pass keeps its work file), so do not try again.
    // ageSeconds: since the last write. A young file can belong to a running recording.
    public static bool NeedsRecovery(string folderName, bool workFileHasVideo, bool zipExists, bool marker, double ageSeconds)
    {
        DateTime start;
        return workFileHasVideo && !zipExists && !marker && ageSeconds >= 60 && TryFolderTime(folderName, out start);
    }

    // The start time in a folder name such as desktop_2026-11-24_12-30-05 or desktop_2026-11-24_12-30-05_2.
    public static bool TryFolderTime(string folderName, out DateTime start)
    {
        start = DateTime.MinValue;
        if (folderName == null || folderName.Length < 27 || !folderName.StartsWith("desktop_", StringComparison.OrdinalIgnoreCase)) return false;
        if (folderName.Length > 27 && folderName[27] != '_') return false;
        return DateTime.TryParseExact(folderName.Substring(8, 19), "yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out start);
    }

    // Text for the clipboard: a message for the AI and the path of the ZIP (Bran, 2026-11-22).
    // Folder mode gives one ZIP. The ZIP has one folder, and that folder has index.md, frames and clip.mp4.
    public static string AiMessage(string zip, bool passOk)
    {
        return AiMessage(zip, passOk, false, false);
    }

    // audio: clip.mp4 has the system audio. transcript: index.md has a transcript section.
    public static string AiMessage(string zip, bool passOk, bool audio, bool transcript)
    {
        string nl = "\r\n";
        string head = "Read my screen recording in this ZIP file:" + nl + zip + nl + nl
            + "Unpack the ZIP first if you cannot read inside it. It has one folder." + nl;
        string sound = audio
            ? "clip.mp4 also has the sound of the PC. If you can hear the audio of a video, listen to it." + nl : "";
        if (passOk)
            return head
                + "1. Read index.md first. It gives the start time, my note, and one row for each frame: the time, the window title and what changed." + nl
                + "2. Look at the PNG files in the frames folder of the ZIP. Each file is one settled frame at full resolution. The file names match the rows in index.md." + nl
                + "3. Use clip.mp4 only for the motion between the frames. Its keyframes are at the times in index.md." + nl
                + (transcript ? "4. Read the section \"Audio and transcript\" at the end of index.md. It has the words that were spoken, with times on the time line of clip.mp4." + nl : "")
                + sound;
        return head
            + "The frame pass failed, so the ZIP has no frames and no index.md." + nl
            + "1. Watch clip.mp4." + nl
            + "2. If note.txt is in the ZIP, read it. It has my note." + nl
            + sound;
    }

    public static string Describe(SettledFrame f, string title, string previousTitle)
    {
        return Describe(f, title, previousTitle, 0);
    }

    // A scroll below this part of the moving area is not named in a row of a changing screen.
    public const double SmallScrollFraction = 0.02;

    // regionHeight: the height of the frame. It is the moving area when the frame has no change box.
    public static string Describe(SettledFrame f, string title, string previousTitle, int regionHeight)
    {
        string kind = f.Kind;
        if (kind == FrameKind.First) return "first frame";
        string scroll = "";
        // Video content can move the picture a few pixels up or down (a camera tilt). The shift check cannot
        // tell this from a slow scroll, so a row of a changing screen does not name a scroll below 2% of the
        // moving area. Bran's YouTube test recording of 2026-11-20 gave "scroll, 3 px up" on such rows.
        // The frames do not change: this rule is for the text only.
        int area = f.BoxHeight > 0 ? f.BoxHeight : regionHeight;
        bool changing = kind == FrameKind.StillChanging || kind == FrameKind.Last;
        bool small = changing && area > 0 && Math.Abs(f.ScrollPixels) < SmallScrollFraction * area;
        if (f.ScrollPixels != 0 && !small)
            scroll = "scroll, " + Math.Abs(f.ScrollPixels).ToString(CultureInfo.InvariantCulture)
                + " px " + (f.ScrollPixels > 0 ? "down" : "up");
        if (kind == FrameKind.ScrollPart) return scroll + " (still scrolling)";
        if (kind == FrameKind.StillChanging) return "screen still changing" + (scroll.Length > 0 ? ", " + scroll : "");
        string what;
        if (previousTitle != null && title != previousTitle) what = "new window content";
        else if (scroll.Length > 0) what = scroll;
        else if (f.ChangedFraction >= 0.3) what = "large change";
        else what = "small change at x " + f.BoxX.ToString(CultureInfo.InvariantCulture)
            + ", y " + f.BoxY.ToString(CultureInfo.InvariantCulture)
            + " (" + f.BoxWidth.ToString(CultureInfo.InvariantCulture)
            + " x " + f.BoxHeight.ToString(CultureInfo.InvariantCulture) + " px)";
        if (kind == FrameKind.Last) what += ", last frame (screen was changing)";
        return what;
    }

    // Numeric UTC offset, for example "UTC-03:00".
    public static string UtcOffsetText(TimeSpan offset)
    {
        string sign = offset < TimeSpan.Zero ? "-" : "+";
        TimeSpan a = offset.Duration();
        return "UTC" + sign + a.Hours.ToString("00", CultureInfo.InvariantCulture) + ":" + a.Minutes.ToString("00", CultureInfo.InvariantCulture);
    }

    public static string FrameFileName(int number, int total)
    {
        int digits = Math.Max(3, total.ToString(CultureInfo.InvariantCulture).Length);
        return number.ToString(new string('0', digits), CultureInfo.InvariantCulture);
    }

    // One line for a table cell or a note line: each line break (CR LF, CR or LF) becomes one space (live test,
    // HANDOFF 7.22: a CR LF gave two spaces), and a pipe is escaped.
    internal static string Cell(string s)
    {
        if (s == null) return "";
        return s.Replace("\r\n", " ").Replace("\r", " ").Replace("\n", " ").Replace("|", "\\|");
    }

    public static string Build(DateTime start, TimeSpan utcOffset, string note, double lengthSeconds,
        int width, int height, IList<SettledFrame> frames, TitleTimeline titles, int sizeLimit, IList<string> extraNotes)
    {
        return Build(start, utcOffset, note, lengthSeconds, width, height, frames, titles, sizeLimit, extraNotes, 0);
    }

    // titleShift: the time of the first frame. The titles are on the FFmpeg stats clock, and the stats time=
    // starts at 0 at the first frame (checked on Bran's PC: first packet 0.2 s and 0.27 s, first time= 0.0 s).
    public static string Build(DateTime start, TimeSpan utcOffset, string note, double lengthSeconds,
        int width, int height, IList<SettledFrame> frames, TitleTimeline titles, int sizeLimit, IList<string> extraNotes,
        double titleShift)
    {
        StringBuilder sb = new StringBuilder();
        sb.Append("# Recording ").Append(start.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture))
          .Append(" (").Append(UtcOffsetText(utcOffset)).Append(")\n");
        // A chat app takes the files from the clipboard and not the text message (Bran, 2026-11-20).
        // Thus the instruction for the AI must be in this file.
        sb.Append(AiNote).Append('\n');
        sb.Append("The time above is the start of the recording.\n");
        if (note != null && note.Trim().Length > 0) sb.Append("Note from Bran: ").Append(Cell(note.Trim())).Append('\n');
        sb.Append("Length: ").Append(lengthSeconds.ToString("0.0", CultureInfo.InvariantCulture)).Append(" s\n");
        sb.Append("Region: ").Append(width.ToString(CultureInfo.InvariantCulture)).Append(" x ")
          .Append(height.ToString(CultureInfo.InvariantCulture)).Append('\n');
        sb.Append("Frames: ").Append(frames.Count.ToString(CultureInfo.InvariantCulture)).Append('\n');
        sb.Append("Times are seconds on the time line of clip.mp4. Each frame in the table is a keyframe of clip.mp4.\n");
        // Folder mode gives one ZIP (Bran, 2026-11-22). A chat app that unpacks it gets index.md and every frame:
        // frames + 1 files. The limit stays at 20 files (the Claude chat limit in one message, HANDOFF 7.9).
        if (frames.Count + 1 > FileWarningLimit)
            sb.Append("Warning: the ZIP has ").Append(frames.Count.ToString(CultureInfo.InvariantCulture))
              .Append(" frame images. With index.md, that is ").Append((frames.Count + 1).ToString(CultureInfo.InvariantCulture))
              .Append(" files. A chat app that unpacks the ZIP can limit the number of images that it reads. Some chat apps accept only ")
              .Append(FileWarningLimit.ToString(CultureInfo.InvariantCulture)).Append(" files in one message.\n");
        if (sizeLimit > 0 && Math.Max(width, height) > sizeLimit)
            sb.Append("Warning: the long side of each frame is more than ").Append(sizeLimit.ToString(CultureInfo.InvariantCulture))
              .Append(" px. Some AI apps make such images smaller before they read them.\n");
        else if ((long)width * height > RecorderCore.DefaultPixelLimit)
            sb.Append("Warning: each frame has more than 3.75 million pixels. Some AI apps make such images smaller before they read them.\n");
        if (extraNotes != null) foreach (string n in extraNotes) sb.Append("Warning: ").Append(Cell(n)).Append('\n');
        sb.Append('\n');
        sb.Append("| Frame | Time | Window title | What changed |\n");
        sb.Append("|---|---|---|---|\n");
        string previousTitle = null;
        for (int i = 0; i < frames.Count; i++)
        {
            SettledFrame f = frames[i];
            string title = titles != null ? titles.At(f.Time - titleShift) : "";
            sb.Append("| ").Append(FrameFileName(f.Number, frames.Count))
              .Append(" | ").Append(f.Time.ToString("0.0", CultureInfo.InvariantCulture)).Append(" s")
              .Append(" | ").Append(Cell(title))
              .Append(" | ").Append(Cell(Describe(f, title, previousTitle, height))).Append(" |\n");
            previousTitle = title;
        }
        return sb.ToString();
    }
}
