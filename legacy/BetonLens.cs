// BETON LENS — re-draws the live screen in a constructivist / seal-carving language.
//
// Every frame:
//   1. Capture the screen under the lens at full resolution. The lens window is excluded from
//      capture, so it never sees itself.
//   2. Per cell: luminance spread decides content vs. field. Content cells are re-printed per pixel
//      in a three-tone pigment ramp with ordered dither, so text and icons stay readable; flat fields
//      become halftone geometry of their mean tone.
//   3. Write the cells into a premultiplied DIB and push it to a click-through layered window.
//
//   legibility   text, icons and edges keep full resolution: snapped to paper / pigment / ink with
//                4x4 ordered dither, like a screen-printed poster
//   repetition   flat fields sit on a mortar gutter, so empty space becomes coursed blockwork
//   geometry     empty / dot / thin, mid, thick diagonal / solid block by the field's darkness
//   interleave   4x4 cell blocks alternate the diagonal, so the bars weave
//   seal carving red fields turn into white-on-red carved lines (白文印); a 构成 seal sits in the corner
//
// Hotkeys:  Ctrl+Alt+F12  on / off      Ctrl+Alt+F11  opacity 45% / 70% / 100%
//           Ctrl+Alt+F10  cell size 8 / 12 / 18 px     Ctrl+Alt+Shift+F12  quit
// Content stays legible: only flat areas are abstracted; detail is kept at full resolution.
// Flags:    --snapshot    save lens-capture.png and lens-frame.png after a few frames
//
// C# 5 / .NET Framework 4: builds with the csc.exe every Windows ships (needs /unsafe).

using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace BetonLens
{
    static class N
    {
        public const int WS_EX_LAYERED = 0x80000, WS_EX_TRANSPARENT = 0x20, WS_EX_TOPMOST = 0x8,
            WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x8000000;
        public const int ULW_ALPHA = 2, HALFTONE = 4, SRCCOPY = 0x00CC0020, WM_HOTKEY = 0x312;
        public const uint WDA_EXCLUDEFROMCAPTURE = 0x11;
        public const uint MOD_ALT = 1, MOD_CONTROL = 2, MOD_SHIFT = 4, MOD_NOREPEAT = 0x4000;
        public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        public const uint SWP_NOMOVE = 2, SWP_NOSIZE = 1, SWP_NOACTIVATE = 0x10;

        [StructLayout(LayoutKind.Sequential)] public struct POINT { public int x, y; }
        [StructLayout(LayoutKind.Sequential)] public struct SIZE { public int cx, cy; }
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }
        [StructLayout(LayoutKind.Sequential)]
        public struct BITMAPINFOHEADER
        {
            public int biSize, biWidth, biHeight; public short biPlanes, biBitCount;
            public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
        }

        [DllImport("user32.dll")] public static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize,
            IntPtr hdcSrc, ref POINT pprSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);
        [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
        [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr hDC);
        [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr hDC, IntPtr hObject);
        [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr hObject);
        [DllImport("gdi32.dll")] public static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER bmi, uint usage, out IntPtr bits, IntPtr hSection, uint offset);
        [DllImport("gdi32.dll")] public static extern bool StretchBlt(IntPtr dst, int xd, int yd, int wd, int hd, IntPtr src, int xs, int ys, int ws, int hs, int rop);
        [DllImport("gdi32.dll")] public static extern int SetStretchBltMode(IntPtr hdc, int mode);
        [DllImport("gdi32.dll")] public static extern bool SetBrushOrgEx(IntPtr hdc, int x, int y, IntPtr old);
        [DllImport("gdi32.dll")] public static extern bool GdiFlush();
        [DllImport("user32.dll")] public static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);
        [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
        [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr hWnd, int id);
        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] public static extern IntPtr SetProcessDpiAwarenessContext(IntPtr value);
    }

    /// A top-down 32-bit DIB section selected into its own memory DC.
    sealed class Dib : IDisposable
    {
        public readonly int W, H;
        public readonly IntPtr Dc, Bits;
        readonly IntPtr bmp, old;

        public Dib(int w, int h)
        {
            W = w; H = h;
            N.BITMAPINFOHEADER bi = new N.BITMAPINFOHEADER();
            bi.biSize = Marshal.SizeOf(typeof(N.BITMAPINFOHEADER));
            bi.biWidth = w; bi.biHeight = -h; bi.biPlanes = 1; bi.biBitCount = 32;
            IntPtr screen = N.GetDC(IntPtr.Zero);
            Dc = N.CreateCompatibleDC(screen);
            N.ReleaseDC(IntPtr.Zero, screen);
            IntPtr bits;
            bmp = N.CreateDIBSection(Dc, ref bi, 0, out bits, IntPtr.Zero, 0);
            Bits = bits;
            old = N.SelectObject(Dc, bmp);
        }

        public void Save(string path, bool opaque)
        {
            using (Bitmap b = new Bitmap(W, H, PixelFormat.Format32bppArgb))
            {
                BitmapData d = b.LockBits(new Rectangle(0, 0, W, H), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                int[] buf = new int[W * H];
                Marshal.Copy(Bits, buf, 0, buf.Length);
                if (opaque)
                    for (int i = 0; i < buf.Length; i++)
                    {
                        int a = (buf[i] >> 24) & 255;
                        if (a == 0 || a == 255) { buf[i] |= unchecked((int)0xFF000000); continue; }
                        int r = Math.Min(255, ((buf[i] >> 16) & 255) * 255 / a), g = Math.Min(255, ((buf[i] >> 8) & 255) * 255 / a), b2 = Math.Min(255, (buf[i] & 255) * 255 / a);
                        buf[i] = unchecked((int)0xFF000000) | (r << 16) | (g << 8) | b2;   // un-premultiply for a true-colour preview
                    }
                Marshal.Copy(buf, 0, d.Scan0, buf.Length);
                b.UnlockBits(d);
                b.Save(path, ImageFormat.Png);
            }
        }

        public void Dispose()
        {
            N.SelectObject(Dc, old);
            N.DeleteObject(bmp);
            N.DeleteDC(Dc);
        }
    }

    /// The six pigments and the per-cell geometry alphabet.
    static class Style
    {
        public static readonly int[] PAPER = { 220, 216, 207 }, INK = { 23, 24, 26 }, RED = { 163, 39, 28 },
            OCHRE = { 176, 122, 28 }, SLATE = { 47, 84, 102 }, VERD = { 59, 106, 88 };

        // mask ids
        public const int EMPTY = 0, DOT = 1, SLASH1 = 2, SLASH2 = 3, SLASH3 = 4, BACK1 = 5, BACK2 = 6, BACK3 = 7,
            SOLID = 8, STROKE_H = 9, STROKE_V = 10, STROKE_SLASH = 11, STROKE_BACK = 12, COUNT = 13;

        /// masks[id][y * cs + x] — true paints the cell's foreground.
        public static bool[][] Masks(int cs)
        {
            bool[][] m = new bool[COUNT][];
            float[] thick = { 0.20f, 0.36f, 0.56f };
            for (int id = 0; id < COUNT; id++)
            {
                bool[] k = new bool[cs * cs];
                for (int y = 0; y < cs; y++)
                    for (int x = 0; x < cs; x++)
                    {
                        float px = x + 0.5f, py = y + 0.5f, c = cs / 2f;
                        bool gutter = x == cs - 1 || y == cs - 1;              // mortar: halftone shapes keep it
                        float dSlash = Math.Abs(px + py - cs) / 1.4142f;     // "/" through the cell, tiles into long lines
                        float dBack = Math.Abs(px - py) / 1.4142f;           // "\"
                        bool on;
                        switch (id)
                        {
                            case DOT: on = !gutter && Math.Abs(px - c) <= cs * 0.17f && Math.Abs(py - c) <= cs * 0.17f; break;
                            case SLASH1: case SLASH2: case SLASH3: on = !gutter && dSlash <= thick[id - SLASH1] * cs / 2f; break;
                            case BACK1: case BACK2: case BACK3: on = !gutter && dBack <= thick[id - BACK1] * cs / 2f; break;
                            case SOLID: on = !gutter; break;
                            case STROKE_H: on = Math.Abs(py - c) <= cs * 0.22f; break;      // strokes run gutter to gutter
                            case STROKE_V: on = Math.Abs(px - c) <= cs * 0.22f; break;
                            case STROKE_SLASH: on = dSlash <= cs * 0.24f; break;
                            case STROKE_BACK: on = dBack <= cs * 0.24f; break;
                            default: on = false; break;
                        }
                        k[y * cs + x] = on;
                    }
                m[id] = k;
            }
            return m;
        }

        /// Saturated colour -> its pigment; neutral -> null (ink).
        public static int[] Pigment(float r, float g, float b, out bool chroma)
        {
            float max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
            float s = max <= 0 ? 0 : (max - min) / max;
            chroma = s > 0.30f && max > 0.20f;
            if (!chroma) return INK;
            float h;
            float d = max - min;
            if (max == r) h = 60f * (((g - b) / d) % 6f);
            else if (max == g) h = 60f * ((b - r) / d + 2f);
            else h = 60f * ((r - g) / d + 4f);
            if (h < 0) h += 360f;
            if (h < 38f || h >= 320f) return RED;
            if (h < 72f) return OCHRE;
            if (h < 190f) return VERD;
            return SLATE;
        }

        public static int Premul(int[] rgb, int alpha)
        {
            int r = rgb[0] * alpha / 255, g = rgb[1] * alpha / 255, b = rgb[2] * alpha / 255;
            return (alpha << 24) | (r << 16) | (g << 8) | b;
        }
    }

    /// One lens window per monitor.
    ///
    /// Two-layer rule so the screen stays legible:
    ///   detail cells (luminance spread above a threshold: text, icons, edges) are re-printed per pixel at
    ///   full resolution — each pixel snapped to a three-tone pigment ramp with 4x4 ordered dither;
    ///   flat cells (backgrounds, fills) are replaced by the constructivist geometry of their mean tone.
    sealed class Lens : Form
    {
        static readonly int[] BAYER = { 0, 8, 2, 10, 12, 4, 14, 6, 3, 11, 1, 9, 15, 7, 13, 5 };
        // pigment ids used by the per-pixel LUT
        const int P_NEUTRAL = 0, P_RED = 1, P_OCHRE = 2, P_VERD = 3, P_SLATE = 4;
        static readonly int[][] PIG = { Style.INK, Style.RED, Style.OCHRE, Style.VERD, Style.SLATE };
        static readonly int[] GREY = { 128, 124, 116 };
        static int[] lut;   // rgb555 -> lum | pigment << 8

        readonly Rectangle bounds;
        int cs, gw, gh;
        Dib cap, frame;
        bool[][] masks;
        int[] sum, sumSq, sumR, sumG, sumB;
        int[] stamp; int stampSize;
        public int DetailThreshold = 14;   // std-dev of luminance (0..255) that marks a cell as content

        public Lens(Rectangle bounds, int cellSize)
        {
            this.bounds = bounds;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            Bounds = bounds;
            TopMost = true;
            if (lut == null) BuildLut();
            cap = new Dib(bounds.Width, bounds.Height);
            frame = new Dib(bounds.Width, bounds.Height);
            SetCell(cellSize);
            BuildStamp();
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= N.WS_EX_LAYERED | N.WS_EX_TRANSPARENT | N.WS_EX_TOPMOST | N.WS_EX_TOOLWINDOW | N.WS_EX_NOACTIVATE;
                return cp;
            }
        }
        protected override bool ShowWithoutActivation { get { return true; } }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            // Not optional: without it the lens would capture its own output and feed back.
            if (!N.SetWindowDisplayAffinity(Handle, N.WDA_EXCLUDEFROMCAPTURE))
                throw new InvalidOperationException("SetWindowDisplayAffinity(EXCLUDEFROMCAPTURE) failed; Windows 10 2004+ is required.");
        }

        static void BuildLut()
        {
            lut = new int[32768];
            for (int i = 0; i < 32768; i++)
            {
                float r = ((i >> 10) & 31) / 31f, g = ((i >> 5) & 31) / 31f, b = (i & 31) / 31f;
                int l = (int)((0.2126f * r + 0.7152f * g + 0.0722f * b) * 255f + 0.5f);
                bool chroma;
                int[] p = Style.Pigment(r, g, b, out chroma);
                int id = !chroma ? P_NEUTRAL : p == Style.RED ? P_RED : p == Style.OCHRE ? P_OCHRE : p == Style.VERD ? P_VERD : P_SLATE;
                lut[i] = l | (id << 8);
            }
        }

        public void SetCell(int size)
        {
            cs = size;
            gw = (bounds.Width + cs - 1) / cs;
            gh = (bounds.Height + cs - 1) / cs;
            masks = Style.Masks(cs);
            int n = gw * gh;
            sum = new int[n]; sumSq = new int[n]; sumR = new int[n]; sumG = new int[n]; sumB = new int[n];
        }

        /// A seal in the corner: 构成, carved white on red, with a broken border.
        void BuildStamp()
        {
            stampSize = 76;
            using (Bitmap b = new Bitmap(stampSize, stampSize, PixelFormat.Format32bppArgb))
            using (Graphics g = Graphics.FromImage(b))
            {
                g.SmoothingMode = SmoothingMode.None;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                g.Clear(Color.FromArgb(163, 39, 28));
                using (Pen pen = new Pen(Color.FromArgb(220, 216, 207), 4)) g.DrawRectangle(pen, 5, 5, stampSize - 11, stampSize - 11);
                using (SolidBrush red = new SolidBrush(Color.FromArgb(163, 39, 28)))
                {
                    g.FillRectangle(red, stampSize - 30, 2, 12, 8);      // chipped edge, as on a worn seal
                    g.FillRectangle(red, 2, stampSize - 26, 8, 10);
                }
                using (Font f = new Font("Noto Serif SC", 21, FontStyle.Bold, GraphicsUnit.Pixel))
                using (SolidBrush paper = new SolidBrush(Color.FromArgb(220, 216, 207)))
                using (StringFormat sf = new StringFormat())
                {
                    sf.Alignment = StringAlignment.Center; sf.LineAlignment = StringAlignment.Center;
                    g.DrawString("构\n成", f, paper, new RectangleF(0, 1, stampSize, stampSize), sf);
                }
                stamp = new int[stampSize * stampSize];
                BitmapData d = b.LockBits(new Rectangle(0, 0, stampSize, stampSize), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                Marshal.Copy(d.Scan0, stamp, 0, stamp.Length);
                b.UnlockBits(d);
            }
        }

        public void Raise() { N.SetWindowPos(Handle, N.HWND_TOPMOST, 0, 0, 0, 0, N.SWP_NOMOVE | N.SWP_NOSIZE | N.SWP_NOACTIVATE); }
        public void SaveCapture(string path) { cap.Save(path, true); }
        public void SaveFrame(string path) { frame.Save(path, true); }

        public unsafe void Render(int alpha)
        {
            int W = frame.W, H = frame.H;

            // 1 · capture at full resolution (the lens itself is excluded from capture)
            IntPtr screen = N.GetDC(IntPtr.Zero);
            N.StretchBlt(cap.Dc, 0, 0, W, H, screen, bounds.X, bounds.Y, W, H, N.SRCCOPY);
            N.ReleaseDC(IntPtr.Zero, screen);
            N.GdiFlush();
            int* src = (int*)cap.Bits;
            int* o = (int*)frame.Bits;

            // 2 · per-cell statistics
            Array.Clear(sum, 0, sum.Length); Array.Clear(sumSq, 0, sumSq.Length);
            Array.Clear(sumR, 0, sumR.Length); Array.Clear(sumG, 0, sumG.Length); Array.Clear(sumB, 0, sumB.Length);
            for (int y = 0; y < H; y++)
            {
                int rowCell = (y / cs) * gw;
                int* line = src + y * W;
                for (int cx = 0; cx < gw; cx++)
                {
                    int x0 = cx * cs, x1 = Math.Min(W, x0 + cs);
                    int s = 0, sq = 0, sr = 0, sg = 0, sb = 0;
                    for (int x = x0; x < x1; x++)
                    {
                        int c = line[x];
                        int r = (c >> 16) & 255, g = (c >> 8) & 255, b = c & 255;
                        int l = lut[((r >> 3) << 10) | ((g >> 3) << 5) | (b >> 3)] & 255;
                        s += l; sq += l * l; sr += r; sg += g; sb += b;
                    }
                    int i = rowCell + cx;
                    sum[i] += s; sumSq[i] += sq; sumR[i] += sr; sumG[i] += sg; sumB[i] += sb;
                }
            }

            // premultiplied palette
            int paper = Style.Premul(Style.PAPER, alpha), ink = Style.Premul(Style.INK, alpha),
                grey = Style.Premul(GREY, alpha), red = Style.Premul(Style.RED, alpha);
            int[] pig = new int[PIG.Length];
            for (int p = 0; p < PIG.Length; p++) pig[p] = Style.Premul(PIG[p], alpha);
            pig[P_NEUTRAL] = grey;

            // 3 · write each cell
            for (int cy = 0; cy < gh; cy++)
                for (int cx = 0; cx < gw; cx++)
                {
                    int i = cy * gw + cx;
                    int x0 = cx * cs, y0 = cy * cs;
                    int cw = Math.Min(cs, W - x0), ch = Math.Min(cs, H - y0);
                    int n = cw * ch;
                    float mean = (float)sum[i] / n;
                    float var = (float)sumSq[i] / n - mean * mean;

                    if (var > DetailThreshold * DetailThreshold)
                    {
                        // content: per-pixel three-tone print with ordered dither
                        for (int yy = 0; yy < ch; yy++)
                        {
                            int y = y0 + yy;
                            int* line = src + y * W;
                            int* row = o + y * W;
                            int bRow = (y & 3) * 4;
                            for (int xx = 0; xx < cw; xx++)
                            {
                                int x = x0 + xx;
                                int c = line[x];
                                int v = lut[(((c >> 16) & 255) >> 3 << 10) | ((((c >> 8) & 255) >> 3) << 5) | ((c & 255) >> 3)];
                                int t = (v & 255) + (BAYER[bRow + (x & 3)] - 8) * 5;     // ±40 of dither
                                row[x] = t > 168 ? paper : t > 84 ? pig[v >> 8] : ink;
                            }
                        }
                        continue;
                    }

                    // flat: constructivist geometry from the mean tone
                    float r = sumR[i] / (255f * n), g = sumG[i] / (255f * n), b = sumB[i] / (255f * n);
                    bool chroma;
                    int[] pg = Style.Pigment(r, g, b, out chroma);
                    float dark = 1f - mean / 255f;
                    bool weave = (((cx >> 2) + (cy >> 2)) & 1) == 0;
                    int mask, fg, bg = paper;
                    int lvl;
                    if (dark < 0.10f) lvl = chroma ? 1 : 0;
                    else if (dark < 0.28f) lvl = 1;
                    else if (dark < 0.45f) lvl = 2;
                    else if (dark < 0.62f) lvl = 3;
                    else if (dark < 0.80f) lvl = 4;
                    else lvl = 5;
                    if (lvl == 0) mask = Style.EMPTY;
                    else if (lvl == 1) mask = Style.DOT;
                    else if (lvl == 5) mask = Style.SOLID;
                    else mask = (weave ? Style.SLASH1 : Style.BACK1) + (lvl - 2);
                    fg = chroma ? Style.Premul(pg, alpha) : ink;
                    if (chroma && pg == Style.RED && lvl >= 3) { bg = red; fg = paper; mask = weave ? Style.SLASH1 : Style.BACK1; }   // 白文: carved white on red
                    else if (lvl == 5) bg = chroma ? fg : grey;                                                                       // solid fields: grey mortar, not white

                    bool[] k = masks[mask];
                    for (int yy = 0; yy < ch; yy++)
                    {
                        int* row = o + (y0 + yy) * W + x0;
                        int kr = yy * cs;
                        for (int xx = 0; xx < cw; xx++) row[xx] = k[kr + xx] ? fg : bg;
                    }
                }

            // the seal, bottom-right above the taskbar
            int sx = W - stampSize - 28, sy = H - stampSize - 84;
            if (sx > 0 && sy > 0)
                for (int yy = 0; yy < stampSize; yy++)
                    for (int xx = 0; xx < stampSize; xx++)
                    {
                        int c = stamp[yy * stampSize + xx];
                        int[] rgb = { (c >> 16) & 255, (c >> 8) & 255, c & 255 };
                        o[(sy + yy) * W + sx + xx] = Style.Premul(rgb, alpha);
                    }

            // push
            IntPtr dst = N.GetDC(IntPtr.Zero);
            N.POINT pt = new N.POINT { x = bounds.X, y = bounds.Y };
            N.SIZE sz = new N.SIZE { cx = W, cy = H };
            N.POINT sp = new N.POINT();
            N.BLENDFUNCTION bl = new N.BLENDFUNCTION { BlendOp = 0, SourceConstantAlpha = 255, AlphaFormat = 1 };
            N.UpdateLayeredWindow(Handle, dst, ref pt, ref sz, frame.Dc, ref sp, 0, ref bl, N.ULW_ALPHA);
            N.ReleaseDC(IntPtr.Zero, dst);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { cap.Dispose(); frame.Dispose(); }
            base.Dispose(disposing);
        }
    }

    sealed class Controller : Form
    {
        const int HK_TOGGLE = 1, HK_OPACITY = 2, HK_CELL = 3, HK_QUIT = 4;
        static readonly int[] OPACITY = { 255, 179, 115 };
        static readonly int[] CELLS = { 8, 12, 18 };
        readonly NotifyIcon tray = new NotifyIcon();
        readonly System.Windows.Forms.Timer tick = new System.Windows.Forms.Timer();
        readonly bool snapshot;
        Lens[] lenses = new Lens[0];
        int opacityIdx = 0, cellIdx = 1, frames;
        bool on = true, restored;
        readonly Stopwatch clock = new Stopwatch();
        double spent; int counted;

        public Controller(bool snapshot)
        {
            this.snapshot = snapshot;
            ShowInTaskbar = false; FormBorderStyle = FormBorderStyle.None; Opacity = 0;
            Size = new Size(1, 1); StartPosition = FormStartPosition.Manual; Location = new Point(-32000, -32000);

            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Items.Add(new ToolStripMenuItem("开 / 关   Ctrl+Alt+F12", null, delegate { Toggle(); }));
            menu.Items.Add(new ToolStripMenuItem("透明度   Ctrl+Alt+F11", null, delegate { CycleOpacity(); }));
            menu.Items.Add(new ToolStripMenuItem("格子大小   Ctrl+Alt+F10", null, delegate { CycleCell(); }));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("退出并恢复   Ctrl+Alt+Shift+F12", null, delegate { Close(); }));
            tray.ContextMenuStrip = menu;
            tray.Icon = MakeIcon();
            tray.Text = "BETON LENS";
            tray.Visible = true;
            tray.DoubleClick += delegate { Toggle(); };

            tick.Interval = 66;
            tick.Tick += delegate { Frame(); };
        }

        protected override CreateParams CreateParams
        {
            get { CreateParams cp = base.CreateParams; cp.ExStyle |= N.WS_EX_TOOLWINDOW; return cp; }
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            N.RegisterHotKey(Handle, HK_TOGGLE, N.MOD_CONTROL | N.MOD_ALT | N.MOD_NOREPEAT, (uint)Keys.F12);
            N.RegisterHotKey(Handle, HK_OPACITY, N.MOD_CONTROL | N.MOD_ALT | N.MOD_NOREPEAT, (uint)Keys.F11);
            N.RegisterHotKey(Handle, HK_CELL, N.MOD_CONTROL | N.MOD_ALT | N.MOD_NOREPEAT, (uint)Keys.F10);
            N.RegisterHotKey(Handle, HK_QUIT, N.MOD_CONTROL | N.MOD_ALT | N.MOD_SHIFT | N.MOD_NOREPEAT, (uint)Keys.F12);
            Screen[] screens = Screen.AllScreens;
            lenses = new Lens[screens.Length];
            for (int i = 0; i < screens.Length; i++) { lenses[i] = new Lens(screens[i].Bounds, CELLS[cellIdx]); lenses[i].Show(); }
            Log("start: screens=" + screens.Length + " primary=" + Screen.PrimaryScreen.Bounds);
            tick.Start();
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == N.WM_HOTKEY)
            {
                int id = m.WParam.ToInt32();
                if (id == HK_TOGGLE) Toggle();
                else if (id == HK_OPACITY) CycleOpacity();
                else if (id == HK_CELL) CycleCell();
                else if (id == HK_QUIT) Close();
            }
            base.WndProc(ref m);
        }

        void Frame()
        {
            clock.Restart();
            foreach (Lens l in lenses) l.Render(OPACITY[opacityIdx]);
            spent += clock.Elapsed.TotalMilliseconds; counted++;
            frames++;
            if (frames % 30 == 0) foreach (Lens l in lenses) l.Raise();
            if (counted == 75) { Log("avg frame " + (spent / counted).ToString("0.0") + " ms"); spent = 0; counted = 0; }
            if (snapshot && frames == 8)
            {
                string dir = AppDomain.CurrentDomain.BaseDirectory;
                lenses[0].SaveCapture(Path.Combine(dir, "lens-capture.png"));
                lenses[0].SaveFrame(Path.Combine(dir, "lens-frame.png"));
                Log("snapshot saved");
            }
        }

        void Toggle()
        {
            on = !on;
            foreach (Lens l in lenses) { if (on) l.Show(); else l.Hide(); }
            if (on) tick.Start(); else tick.Stop();
        }
        void CycleOpacity() { opacityIdx = (opacityIdx + 1) % OPACITY.Length; }
        void CycleCell() { cellIdx = (cellIdx + 1) % CELLS.Length; foreach (Lens l in lenses) l.SetCell(CELLS[cellIdx]); }

        protected override void OnFormClosed(FormClosedEventArgs e) { Restore(); base.OnFormClosed(e); }

        public void Restore()
        {
            if (restored) return;
            restored = true;
            tick.Stop();
            N.UnregisterHotKey(Handle, HK_TOGGLE); N.UnregisterHotKey(Handle, HK_OPACITY);
            N.UnregisterHotKey(Handle, HK_CELL); N.UnregisterHotKey(Handle, HK_QUIT);
            foreach (Lens l in lenses) { l.Close(); }
            tray.Visible = false;
            tray.Dispose();
        }

        static void Log(string line)
        {
            try { File.AppendAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "BetonLens.log"), DateTime.Now.ToString("HH:mm:ss ") + line + Environment.NewLine); }
            catch { }
        }

        static Icon MakeIcon()
        {
            using (Bitmap b = new Bitmap(32, 32))
            using (Graphics g = Graphics.FromImage(b))
            {
                g.Clear(Color.FromArgb(163, 39, 28));
                using (Pen p = new Pen(Color.FromArgb(220, 216, 207), 3)) { g.DrawRectangle(p, 3, 3, 25, 25); g.DrawLine(p, 6, 26, 26, 6); }
                return Icon.FromHandle(b.GetHicon());
            }
        }
    }

    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            bool created;
            using (Mutex single = new Mutex(true, "BetonLens.SingleInstance", out created))
            {
                if (!created) return;
                try { N.SetProcessDpiAwarenessContext(new IntPtr(-4)); } catch { }
                bool snapshot = false;
                foreach (string a in args) if (a == "--snapshot") snapshot = true;
                Application.EnableVisualStyles();
                Controller c = new Controller(snapshot);
                Application.ThreadException += delegate(object s, ThreadExceptionEventArgs e)
                {
                    try { File.AppendAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "BetonLens.log"), "error: " + e.Exception + Environment.NewLine); } catch { }
                    try { c.Restore(); } catch { }
                    Application.Exit();
                };
                Application.Run(c);
            }
        }
    }
}
