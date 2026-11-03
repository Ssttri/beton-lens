// BETON VEIL — a whole-screen "concrete" layer for Windows.
//
// Two independent effects, both removed when the process exits:
//   1. Grade: a 5x5 colour matrix applied by the compositor to the entire desktop
//      through the Magnification API (desaturate, cool, lift blacks, compress whites).
//      No capture loop, no latency, nothing is drawn by us.
//   2. Veil: one click-through, always-on-top, per-pixel-alpha window per virtual
//      screen carrying static concrete grain and a vignette. It never takes focus
//      or input and is excluded from screenshots / screen recording.
//
// Hotkeys:  Ctrl+Alt+F12        toggle on / off
//           Ctrl+Alt+F11        cycle intensity (轻 / 标准 / 重)
//           Ctrl+Alt+Shift+F12  quit
// Flags:    --capturable        let screenshots see the veil (for checking it)
//           --intensity=soft|standard|heavy   --no-grade   --no-veil
//
// C# 5 / .NET Framework 4 on purpose: builds with the csc.exe every Windows ships.

using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace BetonVeil
{
    static class Native
    {
        public const int WS_EX_LAYERED = 0x80000, WS_EX_TRANSPARENT = 0x20, WS_EX_TOPMOST = 0x8,
            WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x8000000;
        public const int ULW_ALPHA = 2;
        public const byte AC_SRC_OVER = 0, AC_SRC_ALPHA = 1;
        public const uint WDA_NONE = 0, WDA_EXCLUDEFROMCAPTURE = 0x11;
        public const int WM_HOTKEY = 0x312;
        public const uint MOD_ALT = 1, MOD_CONTROL = 2, MOD_SHIFT = 4, MOD_NOREPEAT = 0x4000;
        public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        public const uint SWP_NOMOVE = 2, SWP_NOSIZE = 1, SWP_NOACTIVATE = 0x10, SWP_SHOWWINDOW = 0x40;

        [StructLayout(LayoutKind.Sequential)] public struct POINT { public int x, y; }
        [StructLayout(LayoutKind.Sequential)] public struct SIZE { public int cx, cy; }
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize,
            IntPtr hdcSrc, ref POINT pprSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);
        [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
        [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr hDC);
        [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr hDC, IntPtr hObject);
        [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr hObject);
        [DllImport("user32.dll")] public static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);
        [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
        [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr hWnd, int id);
        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] public static extern IntPtr SetProcessDpiAwarenessContext(IntPtr value);
        [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
        [DllImport("user32.dll")] public static extern int GetSystemMetrics(int index);

        [DllImport("Magnification.dll")] public static extern bool MagInitialize();
        [DllImport("Magnification.dll")] public static extern bool MagUninitialize();
        [DllImport("Magnification.dll", SetLastError = true)] public static extern bool MagSetFullscreenColorEffect(float[] effect);
    }

    enum Level { Soft, Standard, Heavy }

    /// The colour grade: a row-vector 5x5 matrix, out = [r g b a 1] · M.
    static class Grade
    {
        public static float[] Matrix(Level level)
        {
            // saturation kept, contrast, black lift, cool tint per channel
            float s, c, lift; float[] tint;
            if (level == Level.Soft) { s = 0.78f; c = 0.96f; lift = 0.015f; tint = new float[] { 0.985f, 0.995f, 1.015f }; }
            else if (level == Level.Heavy) { s = 0.38f; c = 0.86f; lift = 0.05f; tint = new float[] { 0.96f, 0.985f, 1.04f }; }
            else { s = 0.58f; c = 0.91f; lift = 0.032f; tint = new float[] { 0.972f, 0.99f, 1.028f }; }

            float[] lum = { 0.2126f, 0.7152f, 0.0722f };
            float[] m = new float[25];
            for (int i = 0; i < 3; i++)          // input channel (row)
                for (int j = 0; j < 3; j++)      // output channel (column)
                    m[i * 5 + j] = ((1 - s) * lum[i] + (i == j ? s : 0)) * c * tint[j];
            m[3 * 5 + 3] = 1;                    // alpha passes through
            for (int j = 0; j < 3; j++) m[4 * 5 + j] = lift * tint[j];   // offset row: lifted, slightly cold blacks
            m[4 * 5 + 4] = 1;
            return m;
        }

        public static readonly float[] Identity = {
            1,0,0,0,0, 0,1,0,0,0, 0,0,1,0,0, 0,0,0,1,0, 0,0,0,0,1 };
    }

    /// One click-through layered window covering a single monitor.
    sealed class VeilWindow : Form
    {
        readonly Rectangle bounds;
        readonly bool capturable;

        public VeilWindow(Rectangle bounds, bool capturable)
        {
            this.bounds = bounds;
            this.capturable = capturable;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            Bounds = bounds;
            TopMost = true;
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= Native.WS_EX_LAYERED | Native.WS_EX_TRANSPARENT | Native.WS_EX_TOPMOST |
                              Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE;
                return cp;
            }
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Native.SetWindowDisplayAffinity(Handle, capturable ? Native.WDA_NONE : Native.WDA_EXCLUDEFROMCAPTURE);
        }

        public void Render(Level level)
        {
            using (Bitmap bmp = Texture.Build(bounds.Width, bounds.Height, level))
                Push(bmp);
        }

        public void Raise()
        {
            Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        }

        void Push(Bitmap bmp)
        {
            IntPtr screen = Native.GetDC(IntPtr.Zero);
            IntPtr mem = Native.CreateCompatibleDC(screen);
            IntPtr hbmp = bmp.GetHbitmap(Color.FromArgb(0));
            IntPtr old = Native.SelectObject(mem, hbmp);
            try
            {
                Native.POINT dst = new Native.POINT { x = bounds.X, y = bounds.Y };
                Native.SIZE size = new Native.SIZE { cx = bmp.Width, cy = bmp.Height };
                Native.POINT src = new Native.POINT();
                Native.BLENDFUNCTION blend = new Native.BLENDFUNCTION
                {
                    BlendOp = Native.AC_SRC_OVER, SourceConstantAlpha = 255, AlphaFormat = Native.AC_SRC_ALPHA
                };
                Native.UpdateLayeredWindow(Handle, screen, ref dst, ref size, mem, ref src, 0, ref blend, Native.ULW_ALPHA);
            }
            finally
            {
                Native.SelectObject(mem, old);
                Native.DeleteObject(hbmp);
                Native.DeleteDC(mem);
                Native.ReleaseDC(IntPtr.Zero, screen);
            }
        }
    }

    /// Static concrete: fractal-ish grain (two octaves of value noise + fine speckle) and a soft vignette.
    static class Texture
    {
        public static Bitmap Build(int w, int h, Level level)
        {
            float grain = level == Level.Soft ? 0.035f : level == Level.Heavy ? 0.085f : 0.055f;   // peak grain alpha
            float vig = level == Level.Soft ? 0.10f : level == Level.Heavy ? 0.26f : 0.17f;        // corner darkening

            Random rnd = new Random(1937);
            int cell = 6;
            int gw = w / cell + 2, gh = h / cell + 2;
            float[] coarse = new float[gw * gh];
            for (int i = 0; i < coarse.Length; i++) coarse[i] = (float)rnd.NextDouble();

            Bitmap bmp = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
            BitmapData data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
            int stride = data.Stride;
            byte[] row = new byte[stride];
            float cx = w / 2f, cy = h / 2f;
            float maxR = (float)Math.Sqrt(cx * cx + cy * cy);
            for (int y = 0; y < h; y++)
            {
                float fy = (float)y / cell; int iy = (int)fy; float ty = fy - iy;
                for (int x = 0; x < w; x++)
                {
                    float fx = (float)x / cell; int ix = (int)fx; float tx = fx - ix;
                    float a = coarse[iy * gw + ix], b = coarse[iy * gw + ix + 1];
                    float c = coarse[(iy + 1) * gw + ix], d = coarse[(iy + 1) * gw + ix + 1];
                    float smooth = (a + (b - a) * tx) * (1 - ty) + (c + (d - c) * tx) * ty;   // pits and patches
                    float fine = (float)rnd.NextDouble();                                       // aggregate speckle
                    float n = smooth * 0.55f + fine * 0.45f;                                     // 0..1, 0.5 = neutral

                    // grain: below 0.5 darkens (pores), above lightens (cement dust)
                    float dev = n - 0.5f;
                    float ga = Math.Abs(dev) * 2f * grain;                                       // 0..grain
                    float gv = dev < 0 ? 20f : 236f;                                             // dark pore or pale dust

                    // vignette: black, eased so the centre stays clean
                    float dx = x - cx, dy2 = y - cy;
                    float r = (float)Math.Sqrt(dx * dx + dy2 * dy2) / maxR;
                    float va = r < 0.55f ? 0 : (r - 0.55f) / 0.45f;
                    va = va * va * vig;

                    // grain over vignette, premultiplied
                    float A = ga + va * (1 - ga);
                    float C = gv * ga;                                                           // vignette contributes black
                    byte pa = (byte)Math.Min(255, A * 255f + 0.5f);
                    byte pc = (byte)Math.Min(pa, C + 0.5f);
                    int o = x * 4;
                    row[o] = pc; row[o + 1] = pc; row[o + 2] = pc; row[o + 3] = pa;
                }
                Marshal.Copy(row, 0, data.Scan0 + y * stride, stride);
            }
            bmp.UnlockBits(data);
            return bmp;
        }
    }

    /// Owns the tray icon, hotkeys, grade and veil windows.
    sealed class Controller : Form
    {
        const int HK_TOGGLE = 1, HK_CYCLE = 2, HK_QUIT = 3;
        readonly bool capturable, useGrade, useVeil;
        readonly NotifyIcon tray = new NotifyIcon();
        readonly System.Windows.Forms.Timer raiseTimer = new System.Windows.Forms.Timer();
        VeilWindow[] veils = new VeilWindow[0];
        Level level;
        bool on = true, magReady;
        ToolStripMenuItem toggleItem;
        ToolStripMenuItem[] levelItems;

        public Controller(Level level, bool capturable, bool useGrade, bool useVeil)
        {
            this.level = level; this.capturable = capturable; this.useGrade = useGrade; this.useVeil = useVeil;
            ShowInTaskbar = false;
            FormBorderStyle = FormBorderStyle.None;
            Opacity = 0;
            Size = new Size(1, 1);
            StartPosition = FormStartPosition.Manual;
            Location = new Point(-32000, -32000);

            ContextMenuStrip menu = new ContextMenuStrip();
            toggleItem = new ToolStripMenuItem("关闭效果  Ctrl+Alt+F12", null, delegate { Toggle(); });
            menu.Items.Add(toggleItem);
            menu.Items.Add(new ToolStripSeparator());
            string[] names = { "轻", "标准", "重" };
            levelItems = new ToolStripMenuItem[3];
            for (int i = 0; i < 3; i++)
            {
                Level l = (Level)i;
                levelItems[i] = new ToolStripMenuItem("强度：" + names[i], null, delegate { SetLevel(l); });
                menu.Items.Add(levelItems[i]);
            }
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("退出并恢复  Ctrl+Alt+Shift+F12", null, delegate { Close(); }));
            tray.ContextMenuStrip = menu;
            tray.Icon = MakeIcon();
            tray.Text = "BETON VEIL";
            tray.Visible = true;
            tray.DoubleClick += delegate { Toggle(); };

            raiseTimer.Interval = 1500;
            raiseTimer.Tick += delegate { foreach (VeilWindow v in veils) v.Raise(); };
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged += delegate { BeginInvoke((MethodInvoker)Rebuild); };
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            Native.RegisterHotKey(Handle, HK_TOGGLE, Native.MOD_CONTROL | Native.MOD_ALT | Native.MOD_NOREPEAT, (uint)Keys.F12);
            Native.RegisterHotKey(Handle, HK_CYCLE, Native.MOD_CONTROL | Native.MOD_ALT | Native.MOD_NOREPEAT, (uint)Keys.F11);
            Native.RegisterHotKey(Handle, HK_QUIT, Native.MOD_CONTROL | Native.MOD_ALT | Native.MOD_SHIFT | Native.MOD_NOREPEAT, (uint)Keys.F12);
            if (useGrade) magReady = Native.MagInitialize();
            Log("start: MagInitialize=" + magReady + " screens=" + Screen.AllScreens.Length + " primary=" + Screen.PrimaryScreen.Bounds);
            Rebuild();
            raiseTimer.Start();
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_HOTKEY)
            {
                int id = m.WParam.ToInt32();
                if (id == HK_TOGGLE) Toggle();
                else if (id == HK_CYCLE) SetLevel((Level)(((int)level + 1) % 3));
                else if (id == HK_QUIT) Close();
            }
            base.WndProc(ref m);
        }

        void Toggle() { on = !on; Rebuild(); }
        void SetLevel(Level l) { level = l; on = true; Rebuild(); }

        void Rebuild()
        {
            toggleItem.Text = (on ? "关闭效果" : "开启效果") + "  Ctrl+Alt+F12";
            for (int i = 0; i < levelItems.Length; i++) levelItems[i].Checked = (int)level == i;

            if (magReady)
            {
                bool ok = Native.MagSetFullscreenColorEffect(on ? Grade.Matrix(level) : Grade.Identity);
                Log("grade " + (on ? level.ToString() : "off") + " -> " + ok + " (err " + Marshal.GetLastWin32Error() + ")");
            }

            foreach (VeilWindow v in veils) { v.Close(); v.Dispose(); }
            veils = new VeilWindow[0];
            if (!on || !useVeil) return;

            Screen[] screens = Screen.AllScreens;
            veils = new VeilWindow[screens.Length];
            for (int i = 0; i < screens.Length; i++)
            {
                VeilWindow v = new VeilWindow(screens[i].Bounds, capturable);
                v.Show();
                v.Render(level);
                veils[i] = v;
            }
            Log("veil " + level + " on " + screens.Length + " screen(s)");
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            Restore();
            base.OnFormClosed(e);
        }

        bool restored;

        protected override CreateParams CreateParams
        {
            get { CreateParams cp = base.CreateParams; cp.ExStyle |= Native.WS_EX_TOOLWINDOW; return cp; }
        }

        public void Restore()
        {
            if (restored) return;
            restored = true;
            raiseTimer.Stop();
            Native.UnregisterHotKey(Handle, HK_TOGGLE);
            Native.UnregisterHotKey(Handle, HK_CYCLE);
            Native.UnregisterHotKey(Handle, HK_QUIT);
            foreach (VeilWindow v in veils) v.Close();
            if (magReady)
            {
                Native.MagSetFullscreenColorEffect(Grade.Identity);
                Native.MagUninitialize();
                magReady = false;
            }
            tray.Visible = false;
            tray.Dispose();
        }

        static void Log(string line)
        {
            try
            {
                string path = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "BetonVeil.log");
                System.IO.File.AppendAllText(path, DateTime.Now.ToString("HH:mm:ss ") + line + Environment.NewLine);
            }
            catch { }
        }

        static Icon MakeIcon()
        {
            using (Bitmap b = new Bitmap(32, 32))
            using (Graphics g = Graphics.FromImage(b))
            {
                g.Clear(Color.FromArgb(38, 40, 42));
                using (SolidBrush red = new SolidBrush(Color.FromArgb(163, 39, 28))) g.FillRectangle(red, 0, 0, 32, 6);
                using (SolidBrush ink = new SolidBrush(Color.FromArgb(220, 216, 207)))
                {
                    g.FillRectangle(ink, 6, 11, 5, 16);
                    g.FillRectangle(ink, 21, 11, 5, 16);
                    g.FillRectangle(ink, 11, 11, 10, 4);
                }
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
            using (Mutex single = new Mutex(true, "BetonVeil.SingleInstance", out created))
            {
                if (!created) return;

                // Physical pixels on every monitor (per-monitor v2), falling back to system-aware.
                try { Native.SetProcessDpiAwarenessContext(new IntPtr(-4)); } catch { Native.SetProcessDPIAware(); }

                Level level = Level.Standard;
                bool capturable = false, grade = true, veil = true;
                foreach (string a in args)
                {
                    if (a == "--capturable") capturable = true;
                    else if (a == "--no-grade") grade = false;
                    else if (a == "--no-veil") veil = false;
                    else if (a == "--intensity=soft") level = Level.Soft;
                    else if (a == "--intensity=heavy") level = Level.Heavy;
                }

                Application.EnableVisualStyles();
                Controller controller = new Controller(level, capturable, grade, veil);
                // Restore the screen on any exit path we get to see.
                AppDomain.CurrentDomain.ProcessExit += delegate { try { controller.Restore(); } catch { } };
                Application.ThreadException += delegate { try { controller.Restore(); } catch { } Application.Exit(); };
                Application.Run(controller);
            }
        }
    }
}
