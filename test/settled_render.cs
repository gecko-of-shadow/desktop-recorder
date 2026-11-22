// settled_render.cs - makes synthetic test videos for the settled-frame rules (AI capture Phase 1).
// Each video is lossless H.264 4:4:4 at 15 fps, like the work file of the app.
// "changes" adds the app filter of "Changes only" mode (mpdecimate, -fps_mode vfr).
// Build: csc -nologo -out:settled_render.exe -r:System.Drawing.dll test\settled_render.cs
// Run:   settled_render.exe <ffmpeg.exe> <scenario> <out.mp4> [fixed|changes]
// Scenarios:
//   status   60 s chat page. One status line changes 5 times (8, 18, 29.5, 41, 52.3 s). A caret blinks (530 ms).
//   caret    30 s page with a blinking caret and no other change.
//   lookalike  15 one-character changes (x.y x,y O 0 l 1 I rn m a:b a;b a'b a`b - _), holds 700 to 1700 ms.
//   scroll   100 numbered lines under a static header and footer. Smooth scroll at 900 px/s, with pauses.
//   pages    5 settings pages. Each page shows 2 s, scrolls 1 s, shows 2 s.
//   pointer  26 s chat page with a moving mouse pointer (stops 1 s at 6, 12.5 and 19 s). The status changes at 12.8 s.
//            The pointer must not give the motion note (2026-11-22).
//   player   26 s page with a 480 x 270 video area that changes in each frame. It must give the motion note.
using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Runtime.InteropServices;

static class SettledRender
{
    const int W = 1302, H = 960, Fps = 15;

    static int Main(string[] args)
    {
        string ffmpeg = args[0], scenario = args[1], output = args[2];
        bool changes = args.Length > 3 && args[3] == "changes";
        string vf = changes ? "-vf format=yuv444p,mpdecimate=hi=256:lo=128:frac=0.33:max=14 -fps_mode vfr " : "-pix_fmt yuv444p ";
        string a = "-hide_banner -loglevel error -y -f rawvideo -pix_fmt bgr24 -s " + W + "x" + H + " -r " + Fps
            + " -i - " + vf + "-c:v h264_nvenc -preset p4 -tune lossless -profile:v high444p " + '"' + output + '"';
        Process p = new Process();
        p.StartInfo = new ProcessStartInfo(ffmpeg, a);
        p.StartInfo.UseShellExecute = false;
        p.StartInfo.RedirectStandardInput = true;
        p.Start();
        Stream stdin = p.StandardInput.BaseStream;
        double seconds = scenario == "status" ? 60 : scenario == "caret" ? 30 : scenario == "lookalike" ? 22 : scenario == "scroll" ? 20 : 26;
        int frames = (int)(seconds * Fps);
        byte[] buffer = new byte[W * H * 3];
        using (Bitmap bmp = new Bitmap(W, H, PixelFormat.Format24bppRgb))
        using (Graphics g = Graphics.FromImage(bmp))
        using (Font font = new Font("Consolas", 10f))
        using (Font big = new Font("Segoe UI", 11f))
        {
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            for (int i = 0; i < frames; i++)
            {
                double t = i / (double)Fps;
                g.Clear(Color.White);
                if (scenario == "status") Status(g, font, big, t);
                else if (scenario == "caret") Caret(g, font, t, 60, 300);
                else if (scenario == "lookalike") Lookalike(g, font, t);
                else if (scenario == "scroll") Scroll(g, font, big, t);
                else if (scenario == "pointer") PointerPage(g, font, big, t);
                else if (scenario == "player") Player(g, font, t);
                else Pages(g, font, big, t);
                BitmapData d = bmp.LockBits(new Rectangle(0, 0, W, H), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
                for (int y = 0; y < H; y++) Marshal.Copy(new IntPtr(d.Scan0.ToInt64() + (long)y * d.Stride), buffer, y * W * 3, W * 3);
                bmp.UnlockBits(d);
                stdin.Write(buffer, 0, buffer.Length);
            }
        }
        stdin.Close();
        p.WaitForExit();
        return p.ExitCode;
    }

    static void Caret(Graphics g, Font font, double t, int x, int y)
    {
        g.DrawString("Type a message here", font, Brushes.Gray, x - 4, y - 2);
        g.DrawRectangle(Pens.LightGray, x - 10, y - 8, 600, 32);
        if (((int)(t * 1000) / 530) % 2 == 0) g.FillRectangle(Brushes.Black, x + 150, y, 2, 18);
    }

    static void Status(Graphics g, Font font, Font big, double t)
    {
        for (int k = 0; k < 30; k++) g.DrawString("Chat line " + (k + 1) + ": the bot answers with some text to read, value " + (k * 37 % 101), font, Brushes.Black, 40, 40 + k * 22);
        string[] states = { "Reading the files", "Copying the folders", "Messaging the bots", "Checking the results", "Writing the report", "Done" };
        double[] at = { 0, 8, 18, 29.5, 41, 52.3 };
        int s = 0;
        for (int k = 0; k < at.Length; k++) if (t >= at[k]) s = k;
        g.DrawString("Status: " + states[s], big, Brushes.DarkBlue, 40, 720);
        Caret(g, font, t, 60, 860);
    }

    static void Lookalike(Graphics g, Font font, double t)
    {
        string[] items = { "x.y", "x,y", "O", "0", "l", "1", "I", "rn", "m", "a:b", "a;b", "a'b", "a`b", "-", "_" };
        double[] hold = { 0.7, 1.3, 0.9, 1.7, 1.1, 0.8, 1.5, 1.2, 0.7, 1.6, 0.9, 1.3, 1.1, 1.7, 3 };
        double start = 0; int s = 0;
        for (int k = 0; k < items.Length; k++) { if (t >= start) s = k; start += hold[k]; }
        for (int k = 0; k < 20; k++) g.DrawString("Static text line " + (k + 1) + " with some words", font, Brushes.Black, 40, 40 + k * 22);
        g.DrawString("Item: " + items[s], font, Brushes.Black, 40, 600);
    }

    static void Scroll(Graphics g, Font font, Font big, double t)
    {
        // 0-2 s still, 2-6 s scroll at 900 px/s, 6-8 s still, 8-11 s scroll, then still.
        double pos;
        if (t < 2) pos = 0;
        else if (t < 6) pos = (t - 2) * 900;
        else if (t < 8) pos = 3600;
        else if (t < 11) pos = 3600 - (t - 8) * 300;
        else pos = 2700;
        int lineH = 40;
        int top = 80, bottom = H - 80;
        g.SetClip(new Rectangle(0, top, W, bottom - top));
        for (int k = 0; k < 120; k++)
        {
            int y = top + k * lineH - (int)Math.Round(pos);
            if (y < top - lineH || y > bottom) continue;
            g.DrawString("Line " + (k + 1).ToString("000") + "  the quick brown fox jumps over the lazy dog " + (k * 7919 % 1000), font, Brushes.Black, 40, y);
        }
        g.ResetClip();
        g.FillRectangle(Brushes.LightSteelBlue, 0, 0, W, top);
        g.DrawString("Static header", big, Brushes.Black, 40, 20);
        g.FillRectangle(Brushes.LightGray, 0, bottom, W, H - bottom);
        g.DrawString("Static footer", big, Brushes.Black, 40, bottom + 20);
    }

    static void PointerPage(Graphics g, Font font, Font big, double t)
    {
        for (int k = 0; k < 30; k++) g.DrawString("Chat line " + (k + 1) + ": the bot answers with some text to read, value " + (k * 37 % 101), font, Brushes.Black, 40, 40 + k * 22);
        g.DrawString("Status: " + (t < 12.8 ? "Reading the files" : "Writing the report"), big, Brushes.DarkBlue, 40, 720);
        // The pointer stops for 1 s at 6, 12.5 and 19 s. Else it moves on a curve at 300 to 1500 px/s.
        double m = t;
        foreach (double stop in new double[] { 6, 12.5, 19 }) if (t > stop) m -= Math.Min(1, t - stop);
        int x = (int)(650 + 500 * Math.Sin(m * 1.9)), y = (int)(480 + 380 * Math.Sin(m * 1.3 + 0.7));
        // Arrow pointer, 15 x 24 px (the Windows arrow at 125 %): white with a black edge.
        Point[] arrow = { new Point(x, y), new Point(x, y + 21), new Point(x + 5, y + 16), new Point(x + 9, y + 24),
            new Point(x + 12, y + 23), new Point(x + 8, y + 15), new Point(x + 15, y + 15) };
        g.FillPolygon(Brushes.White, arrow);
        g.DrawPolygon(Pens.Black, arrow);
    }

    static void Player(Graphics g, Font font, double t)
    {
        for (int k = 0; k < 10; k++) g.DrawString("Video page line " + (k + 1) + ": a title, a channel name and some comments", font, Brushes.Black, 40, 40 + k * 22);
        Rectangle area = new Rectangle(400, 300, 480, 270);
        int r = (int)(128 + 120 * Math.Sin(t * 5)), gr = (int)(128 + 120 * Math.Sin(t * 4 + 2)), b = (int)(128 + 120 * Math.Sin(t * 3 + 4));
        using (SolidBrush back = new SolidBrush(Color.FromArgb(r, gr, b))) g.FillRectangle(back, area);
        g.SetClip(area);
        for (int k = 0; k < 6; k++)
        {
            int cx = area.X + (int)((Math.Sin(t * (1.1 + k * 0.3) + k) * 0.5 + 0.5) * area.Width);
            int cy = area.Y + (int)((Math.Cos(t * (0.9 + k * 0.4) + k) * 0.5 + 0.5) * area.Height);
            g.FillEllipse(k % 2 == 0 ? Brushes.White : Brushes.Black, cx - 30, cy - 30, 60, 60);
        }
        g.ResetClip();
        g.DrawString("Comments below the video", font, Brushes.Black, 40, 640);
    }

    static void Pages(Graphics g, Font font, Font big, double t)
    {
        int page = Math.Min(4, (int)(t / 5.2));
        double local = t - page * 5.2;
        double pos = local < 2 ? 0 : local < 3 ? (local - 2) * 600 : 600;
        g.FillRectangle(Brushes.WhiteSmoke, 0, 0, 260, H);
        for (int k = 0; k < 5; k++) g.DrawString("Settings page " + (k + 1), big, k == page ? Brushes.Blue : Brushes.Black, 20, 40 + k * 40);
        g.SetClip(new Rectangle(260, 0, W - 260, H));
        for (int k = 0; k < 50; k++)
        {
            int y = 40 + k * 30 - (int)pos;
            g.DrawString("Page " + (page + 1) + " option " + (k + 1) + ": value " + ((page + 1) * 100 + k), font, Brushes.Black, 300, y);
        }
        g.ResetClip();
    }
}
