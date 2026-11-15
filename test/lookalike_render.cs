// Renders the look-alike test states as PNG files (HANDOFF 6.10).
// Each state is the same page with one small text item changed to a look-alike character.
// Build: csc /nologo /target:exe /out:%TEMP%\lookalike_render.exe /r:System.Drawing.dll test\lookalike_render.cs
// Usage: lookalike_render.exe OUTDIR WIDTH HEIGHT FONT_PX THEME(light|dark)
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.IO;

static class LookalikeRender
{
    // The order makes each step a small change: one character, or one mark, changes.
    public static readonly string[] States =
    {
        "x.y", "x,y", "O", "0", "l", "1", "I", "rn", "m", "a:b", "a;b", "a'b", "a`b", "-", "_",
    };

    static int Main(string[] args)
    {
        if (args.Length != 5)
        {
            Console.Error.WriteLine("Usage: lookalike_render.exe OUTDIR WIDTH HEIGHT FONT_PX light|dark");
            return 2;
        }
        string outDir = args[0];
        int width = int.Parse(args[1], CultureInfo.InvariantCulture);
        int height = int.Parse(args[2], CultureInfo.InvariantCulture);
        float fontPx = float.Parse(args[3], CultureInfo.InvariantCulture);
        bool dark = args[4] == "dark";
        Directory.CreateDirectory(outDir);
        Color back = dark ? Color.FromArgb(30, 30, 30) : Color.White;
        Color fore = dark ? Color.FromArgb(212, 212, 212) : Color.Black;
        using (Font font = new Font("Consolas", fontPx, FontStyle.Regular, GraphicsUnit.Pixel))
        using (SolidBrush brush = new SolidBrush(fore))
        {
            for (int i = 0; i < States.Length; i++)
            {
                using (Bitmap bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb))
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.Clear(back);
                    g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
                    // Static text, so the page is not empty. It does not change between states.
                    for (int line = 0; line < 20; line++)
                    {
                        g.DrawString("L" + (line + 1).ToString("000", CultureInfo.InvariantCulture)
                            + "  The quick brown fox jumps over the lazy dog. 0O1lI| {}[]<>", font, brush,
                            20, 20 + line * fontPx * 1.6f);
                    }
                    // The changing item, at a fixed position, between static words.
                    g.DrawString("value = ", font, brush, 20, height / 2f);
                    g.DrawString(States[i], font, brush, 20 + fontPx * 4.4f, height / 2f);
                    g.DrawString(" ; end", font, brush, 20 + fontPx * 7f, height / 2f);
                    bmp.Save(Path.Combine(outDir, "state_" + i.ToString("00", CultureInfo.InvariantCulture) + ".png"),
                        ImageFormat.Png);
                }
            }
        }
        Console.WriteLine(States.Length);
        return 0;
    }
}
