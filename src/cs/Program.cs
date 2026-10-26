/*
 * Calculate325 —— Windows 桌面版（WinForms）
 *
 * 三个要点：
 *  1) 高分屏适配：进程清单声明 PerMonitorV2 感知，窗口内部按“实际 DPI / 96”手动缩放布局
 *     （字号用点，GDI+ 会随 DPI 渲染），窗口跨屏移动时通过 DpiChanged 重新缩放。
 *  2) 单一输出区：结果、明细、保底构造、提示全部显示在同一个输出框；搜不到时直接输出保底算式。
 *  3) 结果渲染完成后把视图滚回最上方（长算式先看到开头）。
 */
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace Calculate325
{
    public class MainForm : Form
    {
        // ---------- DPI 相关 ----------
        [DllImport("user32.dll")]
        static extern uint GetDpiForWindow(IntPtr hWnd);
        [DllImport("user32.dll")]
        static extern uint GetDpiForSystem();
        [DllImport("shcore.dll")]
        static extern int GetProcessDpiAwareness(IntPtr hProcess, out int awareness);
        [DllImport("user32.dll")]
        static extern IntPtr GetThreadDpiAwarenessContext();
        [DllImport("user32.dll")]
        static extern bool AreDpiAwarenessContextsEqual(IntPtr a, IntPtr b);
        [DllImport("user32.dll")]
        static extern bool PrintWindow(IntPtr hWnd, IntPtr hdc, uint flags);

        static readonly IntPtr CTX_UNAWARE = new IntPtr(-1);
        static readonly IntPtr CTX_SYSTEM_AWARE = new IntPtr(-2);
        static readonly IntPtr CTX_PER_MONITOR = new IntPtr(-3);
        static readonly IntPtr CTX_PER_MONITOR_V2 = new IntPtr(-4);

        float scale = 1f;      // 布局缩放：Dpi / 96
        int dpiUsed = 96;
        bool sizeInitialized = false;

        // ---------- 控件 ----------
        readonly Label lblTarget = new Label();
        readonly Label lblBlocks = new Label();
        readonly Label lblOrder = new Label();
        readonly Label lblFactMax = new Label();
        readonly Label lblPattern = new Label();
        readonly ComboBox cboPattern = new ComboBox();
        readonly ChamferButton btnCustom = new ChamferButton();
        readonly ChamferButton btnRandom = new ChamferButton();
        readonly Random rnd = new Random();
        int randomPick = -1;      // -1 = 最优解；>=0 = 第 N 条其它写法
        static readonly string[] Patterns = new string[] { "325", "799", "325799", "114514" };
        static readonly string[] PatternItems = new string[] { "325", "799", "325799", "114514", "我全都要！" };
        readonly TextBox txtTarget = new TextBox();
        readonly LotusButton btnSolve = new LotusButton();
        readonly ChamferButton btnCopy = new ChamferButton();
        readonly ComboBox cboBlocks = new ComboBox();
        readonly ComboBox cboOrder = new ComboBox();
        readonly NumericUpDown numFactMax = new NumericUpDown();
        readonly CheckBox chkFact = new CheckBox();
        readonly CheckBox chkDecimal = new CheckBox();
        // ---- 高级运算：总开关 + 展开面板 ----
        readonly CheckBox chkAdvanced = new CheckBox();
        readonly FlowLayoutPanel panelAdv = new FlowLayoutPanel();
        readonly CheckBox chkPower = new CheckBox();
        readonly CheckBox chkDet = new CheckBox();
        readonly CheckBox chkLim = new CheckBox();
        readonly CheckBox chkSin = new CheckBox();
        readonly CheckBox chkRoot = new CheckBox();
        readonly CheckBox chkDfact = new CheckBox();
        readonly CheckBox chkComb = new CheckBox();
        readonly CheckBox chkAbs = new CheckBox();
        readonly CheckBox chkCbrt = new CheckBox();
        readonly CheckBox chkPercent = new CheckBox();
        readonly CheckBox chkLcmLog = new CheckBox();
        readonly CheckBox chkGcdMod = new CheckBox();
        readonly FlowLayoutPanel examples = new FlowLayoutPanel();
        readonly OutputView rtb = new OutputView();
        readonly ChamferPanel outputCard = new ChamferPanel();
        // ---- 换肤 ----
        // ---- 自定义标题栏（无边框窗口的拖拽栏） ----
        const int TITLE_H = 38;                 // 设计单位
        readonly Panel titleBar = new Panel();
        readonly Label lblGlyph = new Label();
        readonly Label lblTitle = new Label();
        readonly Label lblBadge = new Label();
        readonly TitleButton btnMin = new TitleButton();
        readonly TitleButton btnMax = new TitleButton();
        readonly TitleButton btnClose = new TitleButton();
        readonly Label lblSkin = new Label();
        readonly ComboBox cboSkin = new ComboBox();
        Skin skin = Skins.Plain;

        readonly ToolTip tips = new ToolTip();

        Font fontSmall, fontBody, fontBig, fontMono, fontTarget, fontHead;



        readonly Dictionary<string, Solver.Session> sessions = new Dictionary<string, Solver.Session>();
        List<object[]> lastResults;
        string lastCopy = "";
        string pendingTarget = "";

        // 配色全部来自当前皮肤（skin），这样换肤时输出区也会跟着变
        Color Ink { get { return skin.Ink; } }
        Color Muted { get { return skin.Muted; } }
        Color Warn { get { return skin.Warn; } }
        Color Good { get { return skin.Good; } }
        Color Brand { get { return skin.Accent; } }

        public MainForm()
        {
            Text = "计算325 · Calculate325";
            FormBorderStyle = FormBorderStyle.None;     // 自绘标题栏
            MaximizeBox = false;
            MinimizeBox = true;
            MaximizedBounds = Screen.PrimaryScreen.WorkingArea;
            try
            {
                Icon exeIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
                if (exeIcon != null) Icon = exeIcon;
            }
            catch (Exception) { }
            SkinAssets.TryLoadTemplateFonts(Application.StartupPath);
            CustomPatterns.Load(Application.StartupPath);
            BackColor = Color.FromArgb(244, 246, 251);
            StartPosition = FormStartPosition.CenterScreen;
            // 自己按 DPI 缩放布局，关闭 WinForms 的自动缩放，避免与手动缩放叠加
            AutoScaleMode = AutoScaleMode.None;
            KeyPreview = false;

            RecreateFonts();
            BuildUi();
            BuildTitleBar();
            // 默认皮肤先建好控件，再套用上次记住的皮肤（首次为“默认”）
            ApplySkin(Skins.ByKey(LoadSkinKey()), false);
            if (ForceAdvanced) { chkAdvanced.Checked = true; chkPower.Checked = true; }
            AcceptButton = btnSolve;
            Shown += delegate
            {
                CenterMainScreen();
                if (!string.IsNullOrEmpty(shotPath)) PrepareShot();
                else btnSolve.PerformClick();
            };
        }

        // ---------- 自截图（开发/文档用，--shot） ----------
        internal string shotPath;
        internal string shotTarget;
        internal int shotBlocks;
        internal string shotPatterns;
        internal string shotSkin;
        internal static bool ForcePower;
        internal static bool ForceAdvanced;

        void PrepareShot()
        {
            if (!string.IsNullOrEmpty(shotTarget)) txtTarget.Text = shotTarget;
            if (!string.IsNullOrEmpty(shotPatterns))
            {
                SelectPatternsFromCsv(shotPatterns.Split(','));
            }
            if (!string.IsNullOrEmpty(shotSkin))
            {
                cboSkin.SelectedIndex = Skins.IndexOf(shotSkin);
            }
            if (!string.IsNullOrEmpty(shotSkin))
            {
                cboSkin.SelectedIndex = Skins.IndexOf(shotSkin);
            }
            btnSolve.PerformClick();
            Timer t = new Timer();
            t.Interval = 6500;   // 等求解完成（多数字串更慢）
            t.Tick += delegate
            {
                t.Stop();
                try
                {
                    using (Bitmap bmp = new Bitmap(Width, Height))
                    {
                        using (Graphics g = Graphics.FromImage(bmp))
                        {
                            IntPtr hdc = g.GetHdc();
                            PrintWindow(Handle, hdc, 2);
                            g.ReleaseHdc(hdc);
                        }
                        bmp.Save(shotPath, ImageFormat.Png);
                    }
                }
                catch (Exception ex)
                {
                    try { File.WriteAllText(shotPath + ".error.txt", ex.ToString(), new UTF8Encoding(false)); }
                    catch (Exception) { }
                }
                Close();
            };
            t.Start();
        }

        // ===================== DPI =====================
        int R(int v) { return (int)Math.Round(v * scale); }
        Point P(int x, int y) { return new Point(R(x), R(y)); }
        Size S(int w, int h) { return new Size(R(w), R(h)); }
        Rectangle Rect(int x, int y, int w, int h) { return new Rectangle(R(x), R(y) + R(TITLE_H), R(w), R(h)); }

        // 标题栏：外观 + 拖动 + 双击最大化 + 三个窗口按钮
        void BuildTitleBar()
        {
            titleBar.Paint += delegate(object s, PaintEventArgs e) { PaintTitleBar(e.Graphics); };
            Controls.Add(titleBar);

            lblGlyph.Text = "园";
            lblGlyph.TextAlign = ContentAlignment.MiddleCenter;
            lblGlyph.AutoSize = false;
            lblGlyph.BackColor = Color.Transparent;
            lblGlyph.Visible = false;

            lblTitle.Text = "计算325 · Calculate325";
            lblTitle.AutoSize = false;
            lblTitle.TextAlign = ContentAlignment.MiddleLeft;
            lblTitle.BackColor = Color.Transparent;
            lblTitle.Visible = false;

            lblBadge.Text = "CALC-325";
            lblBadge.AutoSize = false;
            lblBadge.TextAlign = ContentAlignment.MiddleRight;
            lblBadge.BackColor = Color.Transparent;
            lblBadge.Visible = false;

            btnMin.Role = TitleButton.Kind.Minimize;
            btnMax.Role = TitleButton.Kind.Maximize;
            btnMax.Enabled = false;
            btnMax.Visible = false;
            btnClose.Role = TitleButton.Kind.Close;
            btnMin.Click += delegate { WindowState = FormWindowState.Minimized; };
            btnMax.Click += delegate { ToggleMaximize(); };
            btnClose.Click += delegate { Close(); };
            titleBar.Controls.Add(btnMin);
            titleBar.Controls.Add(btnMax);
            titleBar.Controls.Add(btnClose);

            // 拖动窗口：交给系统（ReleaseCapture + WM_NCLBUTTONDOWN/HTCAPTION）。
            // 手写“按位移移动”在快速拖动时会掉帧、而且标题栏被 Label 盖住时收不到事件。
            AttachDrag(titleBar);
            titleBar.DoubleClick += delegate(object s, EventArgs e) { ToggleMaximize(); };
        }

        // 让任意控件（含其子控件）按住左键即可拖动整个窗口
        void AttachDrag(Control c)
        {
            if (c is TitleButton) return;      // 三个窗口按钮绝对不能被“拖窗口”抢走点击
            c.MouseDown += delegate(object s, MouseEventArgs e)
            {
                if (e.Button != MouseButtons.Left) return;
                ReleaseCapture();
                SendMessage(Handle, 0x00A1 /*WM_NCLBUTTONDOWN*/, (IntPtr)2 /*HTCAPTION*/, IntPtr.Zero);
            };
            foreach (Control child in c.Controls) AttachDrag(child);
        }

        [DllImport("user32.dll")]
        static extern bool ReleaseCapture();

        [DllImport("user32.dll")]
        static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        void ToggleMaximize()
        {
            // 固定窗口，最大化按钮不生效
        }

        void PaintTitleBar(Graphics g)
        {
            Skin s = skin;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            Rectangle r = titleBar.ClientRectangle;
            Color top = s.Face == SkinFace.Plain ? s.TitleBack : Blend(s.TitleBack, s.Accent, 0.18f);
            Color bottom = Blend(s.TitleBack, s.FormBack, 0.35f);
            using (System.Drawing.Drawing2D.LinearGradientBrush b = new System.Drawing.Drawing2D.LinearGradientBrush(
                r, top, bottom, System.Drawing.Drawing2D.LinearGradientMode.Vertical))
            {
                g.FillRectangle(b, r);
            }
            // 下沿细线（强调色）+ 左侧一小段金线，做出“编号化”的技术感
            using (Pen p = new Pen(s.TitleLine, 1.4f)) g.DrawLine(p, 0, r.Height - 1, r.Width, r.Height - 1);
            using (Pen p = new Pen(s.Accent2, 2f)) g.DrawLine(p, R(10), r.Height - 1, R(10) + R(54), r.Height - 1);

            // 图标 + 标题 + 代号：全部在这里画，保证一定可见
            StringFormat left = new StringFormat();
            left.Alignment = StringAlignment.Near;
            left.LineAlignment = StringAlignment.Center;
            using (Font gf = new Font(PickFamily(s.HeadFamily, UiFamily()), 12f, FontStyle.Bold))
            using (SolidBrush gb = new SolidBrush(s.TitleGlyphInk))
            {
                g.DrawString(s.TitleGlyph, gf, gb, new RectangleF(R(10), 0, R(34), r.Height), left);
            }
            using (Font tf = new Font(PickFamily(s.HeadFamily, UiFamily()), 10.5f,
                       s.HeadBold ? FontStyle.Bold : FontStyle.Regular))
            using (SolidBrush tb = new SolidBrush(s.TitleInk))
            {
                g.DrawString(Text, tf, tb, new RectangleF(R(46), 0, Math.Max(R(80), r.Width - R(46) - R(360)), r.Height), left);
            }
            StringFormat right = new StringFormat();
            right.Alignment = StringAlignment.Far;
            right.LineAlignment = StringAlignment.Center;
            using (Font bf = new Font(PickFamily(s.MonoFamily, MonoFamily()), 9.5f, FontStyle.Regular))
            using (SolidBrush bb = new SolidBrush(ChamferButton.Blend(s.TitleBack, s.TitleInk, 0.62f)))
            {
                g.DrawString(s.Badge, bf, bb, new RectangleF(r.Width - R(300), 0, R(160), r.Height), right);
            }
            left.Dispose();
            right.Dispose();
        }


        static Color Blend(Color a, Color b, float t)
        {
            if (t < 0) t = 0; if (t > 1) t = 1;
            return Color.FromArgb(
                (int)(a.R + (b.R - a.R) * t),
                (int)(a.G + (b.G - a.G) * t),
                (int)(a.B + (b.B - a.B) * t));
        }

        void LayoutTitleBar()
        {
            int h = R(TITLE_H);
            titleBar.Bounds = new Rectangle(0, 0, ClientSize.Width, h);
            titleBar.BackColor = skin.TitleBack;
            titleBar.BringToFront();
            int bw = R(42), gap = R(2), bh = h - R(2), buttons = btnMax.Visible ? 3 : 2;
            int right = ClientSize.Width - R(6);
            btnClose.Bounds = new Rectangle(right - bw, R(1), bw, bh);
            btnMax.Bounds = new Rectangle(right - bw * 2 - gap, R(1), bw, bh);
            btnMin.Bounds = new Rectangle(right - bw * buttons - gap * (buttons - 1), R(1), bw, bh);
            lblGlyph.Bounds = new Rectangle(R(10), R(4), R(26), h - R(8));
            lblTitle.Bounds = new Rectangle(R(42), R(2), Math.Max(R(120), ClientSize.Width - R(42) - bw * buttons - R(150)), h - R(4));
            lblGlyph.BringToFront(); lblTitle.BringToFront(); lblBadge.BringToFront();
            btnMin.BringToFront(); btnMax.BringToFront(); btnClose.BringToFront();
            lblBadge.Bounds = new Rectangle(ClientSize.Width - bw * buttons - R(120), R(2), R(110), h - R(4));
        }

        static int SafeGetDpiForSystem()
        {
            try { return (int)GetDpiForSystem(); }
            catch (Exception) { return 0; }
        }

        internal static int SafeDpiOfWindow(IntPtr hwnd)
        {
            try
            {
                uint v = GetDpiForWindow(hwnd);
                if (v >= 48) return (int)v;
            }
            catch (Exception) { }
            return 96;
        }

        int SafeGetDpiForWindow()
        {
            try
            {
                uint v = GetDpiForWindow(Handle);
                if (v >= 48) return (int)v;
            }
            catch (Exception) { }
            try
            {
                using (Graphics g = CreateGraphics())
                {
                    int d = (int)Math.Round(g.DpiX);
                    if (d >= 48) return d;
                }
            }
            catch (Exception) { }
            return 96;
        }

        static string ThreadAwarenessName()
        {
            try
            {
                IntPtr ctx = GetThreadDpiAwarenessContext();
                if (AreDpiAwarenessContextsEqual(ctx, CTX_PER_MONITOR_V2)) return "PerMonitorV2";
                if (AreDpiAwarenessContextsEqual(ctx, CTX_PER_MONITOR)) return "PerMonitor";
                if (AreDpiAwarenessContextsEqual(ctx, CTX_SYSTEM_AWARE)) return "System";
                if (AreDpiAwarenessContextsEqual(ctx, CTX_UNAWARE)) return "Unaware";
            }
            catch (Exception) { }
            return "Unknown";
        }

        static string ProcessAwarenessName()
        {
            try
            {
                int a;
                if (GetProcessDpiAwareness(IntPtr.Zero, out a) == 0)
                {
                    if (a == 2) return "per-monitor";
                    if (a == 1) return "system";
                    return "unaware";
                }
            }
            catch (Exception) { }
            return "unknown";
        }

        void ApplyDpi(int newDpi, Rectangle suggested)
        {
            if (newDpi < 48) newDpi = 96;
            if (forcedDpi > 0) newDpi = forcedDpi;      // 诊断模式：按指定 DPI 演练布局
            dpiUsed = newDpi;
            scale = newDpi / 96f;
            RecreateFonts();
            SuspendLayout();
            Screen screen = suggested.Width > 0 ? Screen.FromRectangle(suggested) : Screen.PrimaryScreen;
            ApplyMinimumSize(screen);
            if (suggested.Width > 0 && suggested.Height > 0)
            {
                Bounds = suggested;                     // 系统建议的新窗口大小（已是新 DPI 的物理像素）
            }
            else
            {
                ClientSize = ClampTo(screen);
            }
            ApplyLayout();
            ResumeLayout(true);
            if (lastResults != null) RenderAll(lastResults);  // 重新排版输出文本
        }

        int forcedDpi = 0;                              // >0 时忽略真实 DPI（仅 --diag 使用）

        [StructLayout(LayoutKind.Sequential)]
        struct RECT
        {
            public int Left, Top, Right, Bottom;
        }

        const int WM_DPICHANGED = 0x02E0;
        const int WM_NCHITTEST = 0x0084;

        // 自己接管 DPI 变化通知：WinForms（.NET Framework）在没有 app.config 指定
        // DpiAwareness 时会用错误的 96/实际DPI 比例再次缩放窗口，把已经算好的尺寸缩小
        // （实测 1132×1046 被缩成 755×697 = 2/3）。这里直接处理，不再交给基类。
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_DPICHANGED)
            {
                int newDpi = (int)(m.WParam.ToInt64() & 0xFFFF);
                if (newDpi < 48) newDpi = 96;
                if (newDpi == dpiUsed)
                {
                    // 窗口刚创建时的重复通知：保持已算好的尺寸，忽略
                    return;
                }
                Rectangle suggested = Rectangle.Empty;
                try
                {
                    if (m.LParam != IntPtr.Zero)
                    {
                        RECT r = (RECT)Marshal.PtrToStructure(m.LParam, typeof(RECT));
                        suggested = Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
                    }
                }
                catch (Exception) { }
                ApplyDpi(newDpi, suggested);
                return;
            }
            base.WndProc(ref m);
            // 固定 1920×1080，不再做边缘拖拽缩放
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            if (forcedDpi > 0)
            {
                dpiUsed = forcedDpi;
            }
            else
            {
                dpiUsed = SafeGetDpiForWindow();
                if (dpiUsed < 48) dpiUsed = SafeGetDpiForSystem();
                if (dpiUsed < 48) dpiUsed = 96;
            }
            scale = dpiUsed / 96f;
            RecreateFonts();
            Screen screen = Screen.PrimaryScreen;
            ApplyMinimumSize(screen);
            if (!sizeInitialized)
            {
                ClientSize = ClampTo(screen);
                sizeInitialized = true;
            }
            ApplyLayout();
            CenterMainScreen();
            if (lastResults != null) RenderAll(lastResults);
        }

        void CenterMainScreen()
        {
            Rectangle wa = Screen.FromControl(this).WorkingArea;
            int x = wa.Left + (wa.Width - Width) / 2;
            int y = wa.Top + (wa.Height - Height) / 2;
            if (Width <= wa.Width) x = Math.Max(wa.Left, Math.Min(x, wa.Right - Width));
            if (Height <= wa.Height) y = Math.Max(wa.Top, Math.Min(y, wa.Bottom - Height));
            Location = new Point(x, y);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            LayoutOutput();
        }

        // ===================== 字体 =====================
        static bool HasFamily(string name)
        {
            foreach (FontFamily f in FontFamily.Families)
            {
                if (string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        static string UiFamily()
        {
            string[] names = new string[] { "Microsoft YaHei UI", "Microsoft YaHei", "Segoe UI", "SimSun" };
            foreach (string n in names) if (HasFamily(n)) return n;
            return FontFamily.GenericSansSerif.Name;
        }

        static string MonoFamily()
        {
            string[] names = new string[] { "Cascadia Mono", "Consolas", "Courier New" };
            foreach (string n in names) if (HasFamily(n)) return n;
            return FontFamily.GenericMonospace.Name;
        }

        void RecreateFonts()
        {
            // 字号用“点”：GDI+ 会按当前 DPI 渲染，所以字体本身不需要乘以缩放系数。
            // 注意：这里刻意不 Dispose 旧字体 —— Control.Font 对“等值字体”会跳过赋值
            // （实测 ReferenceEquals 仍指向旧对象），释放后控件就会持有已释放的字体，
            // 之后任何测量都会抛 “参数无效”。字号对象很少，交给 GC 即可。
            string ui = PickFamily(skin.BodyFamily, UiFamily());
            string mono = PickFamily(skin.MonoFamily, MonoFamily());
            // 输出区必须用“中西文同族”的等宽字体：Consolas 没有汉字/【】，
            // 长算式里混排时汉字会回退成别的字体，看起来就是“字体忽大忽小”。
            string outMono = mono;   // 全 ASCII 的算式用 Consolas 才均匀（NSimSun 是点阵字体，会跳字形）
            // 输出区统一用等宽字体：标题行、算式、说明行同族，只靠字号和颜色分层，
            // 避免“表达式前后字体/字号不一样”的观感。
            fontSmall = new Font(ui, 9.5f, FontStyle.Regular);      // 说明行（含汉字，用中西文都齐的界面字体）
            fontBody = new Font(ui, 10f, FontStyle.Regular);       // 界面正文（控件）
            fontBig = new Font(outMono, 14f, FontStyle.Bold);       // 算式（粗体）
            fontMono = new Font(ui, 9.5f, FontStyle.Regular);       // 其它写法（同上）
            fontTarget = new Font(mono, 14f, FontStyle.Regular);   // 目标数字输入框
            fontHead = new Font(ui, 12f, FontStyle.Bold);           // 【数字串 xxx】（含汉字与全角括号）

            lblTarget.Font = fontSmall;
            lblBlocks.Font = fontSmall;
            lblOrder.Font = fontSmall;
            lblFactMax.Font = fontSmall;
            txtTarget.Font = fontTarget;
            btnSolve.Font = fontBody;
            btnCopy.Font = fontBody;
            cboBlocks.Font = fontBody;
            cboOrder.Font = fontBody;
            numFactMax.Font = fontBody;
            chkFact.Font = fontBody;
            chkDecimal.Font = fontBody;
            chkPower.Font = fontSmall;
            chkRoot.Font = fontSmall;
            chkDfact.Font = fontSmall;
            chkComb.Font = fontSmall;
            chkGcdMod.Font = fontSmall;
            chkDet.Font = fontSmall;
            chkAdvanced.Font = fontBody;
            if (lblSkin != null) lblSkin.Font = fontSmall;
            if (cboSkin != null) cboSkin.Font = fontBody;
            foreach (Control c in examples.Controls) c.Font = fontSmall;
        }

        static string PickFamily(string wanted, string fallback)
        {
            if (!string.IsNullOrEmpty(wanted) && HasFamily(wanted)) return wanted;
            return fallback;
        }

        // ---------- 换肤 ----------
        string SkinFilePath()
        {
            try { return Path.Combine(Application.StartupPath, "skin.txt"); }
            catch (Exception) { return null; }
        }

        string LoadSkinKey()
        {
            try
            {
                string p = SkinFilePath();
                if (p != null && File.Exists(p)) return File.ReadAllText(p, Encoding.UTF8).Trim();
            }
            catch (Exception) { }
            return Skins.DefaultKey;
        }

        void SaveSkinKey(string key)
        {
            try
            {
                string p = SkinFilePath();
                if (p != null) File.WriteAllText(p, key, new UTF8Encoding(false));
            }
            catch (Exception) { }
        }

        void ApplySkin(Skin s, bool persist)
        {
            if (s == null) s = Skins.Plain;
            skin = s;

            BackgroundImageLayout = ImageLayout.Stretch;
            BackgroundImage = SkinAssets.LoadBackground(s);
            BackColor = s.FormBack;
            ForeColor = s.Ink;

            RecreateFonts();
            ApplySkinColors(this, s);

            // 标题栏：配色 + 小图标 + 代号
            if (titleBar != null)
            {
                lblGlyph.Text = s.TitleGlyph;
                lblGlyph.ForeColor = s.TitleInk;
                lblBadge.Text = s.Badge;
                lblBadge.ForeColor = ChamferButton.Blend(s.TitleBack, s.TitleInk, 0.62f);
                lblTitle.ForeColor = s.TitleInk;
                lblTitle.Font = new Font(PickFamily(s.HeadFamily, UiFamily()), 10.5f,
                    s.HeadBold ? FontStyle.Bold : FontStyle.Regular);
                titleBar.Invalidate();
                btnMin.ApplySkin(s); btnMax.ApplySkin(s); btnClose.ApplySkin(s);
                btnMax.IsMaximized = WindowState == FormWindowState.Maximized;
                btnMin.Invalidate(); btnMax.Invalidate(); btnClose.Invalidate();
            }
            // 荷花按钮
            btnSolve.ApplySkin(s);
            rtb.ApplySkin(s);
            btnCopy.ApplySkin(s);
            btnCustom.ApplySkin(s);
            btnRandom.ApplySkin(s);
            outputCard.Skin = s;
            outputCard.Corner = R(s.Corner + 2);
            outputCard.Invalidate();
            foreach (Control c in examples.Controls)
            {
                ChamferButton cb = c as ChamferButton;
                if (cb != null) cb.ApplySkin(s);
            }


            int idx = Skins.IndexOf(s.Key);
            if (idx >= 0 && idx < cboSkin.Items.Count && cboSkin.SelectedIndex != idx)
            {
                cboSkin.SelectedIndex = idx;          // 会再次进入 ApplySkin，但 ReferenceEquals 会挡住
            }
            if (persist) SaveSkinKey(s.Key);

            // 已经算出的结果要按新配色重画一遍
            if (lastResults != null) RenderAll(lastResults);
        }

        void ApplySkinColors(Control parent, Skin s)
        {
            foreach (Control c in parent.Controls)
            {
                try
                {
                    if (c == rtb)
                    {
                        rtb.BackColor = s.OutputBack;
                        rtb.ForeColor = s.OutputInk;
                        }
                    else if (c is TextBox || c is ComboBox || c is NumericUpDown)
                    {
                        c.BackColor = s.FieldBack;
                        c.ForeColor = s.FieldFore;
                        if (c is ComboBox) ((ComboBox)c).FlatStyle = FlatStyle.Flat;   // 否则深色皮肤仍按系统主题画
                        else if (c is NumericUpDown) ((NumericUpDown)c).BorderStyle = BorderStyle.FixedSingle;
                        else ((TextBox)c).BorderStyle = BorderStyle.FixedSingle;
                    }
                    else if (c is Button)
                    {
                        Button b = (Button)c;
                        b.FlatStyle = FlatStyle.Flat;
                        b.BackColor = s.PanelBack;
                        b.ForeColor = s.Ink;
                        b.FlatAppearance.BorderColor = s.Border;
                        b.FlatAppearance.BorderSize = 1;
                        b.UseVisualStyleBackColor = false;
                    }
                    else if (c is Label || c is CheckBox)
                    {
                        // 这两个支持透明，能露出底图
                        c.BackColor = Color.Transparent;
                        c.ForeColor = s.Ink;
                        if (c is CheckBox) ((CheckBox)c).FlatStyle = FlatStyle.Standard;
                    }
                    else if (c is FlowLayoutPanel || c is Panel)
                    {
                        c.BackColor = Color.Transparent;
                        c.ForeColor = s.Ink;
                    }
                    else
                    {
                        // 其它控件（以及 NumericUpDown 内部的子控件）不支持透明，给实色
                        c.BackColor = s.PanelBack;
                        c.ForeColor = s.Ink;
                    }
                }
                catch (Exception) { }
                if (c.HasChildren) ApplySkinColors(c, s);
            }
        }

        // ===================== 界面 =====================
        void BuildUi()
        {
            lblTarget.Text = "目标数字 n";
            lblTarget.ForeColor = Muted;
            lblTarget.AutoSize = false;
            Controls.Add(lblTarget);

            txtTarget.Text = "325";
            Controls.Add(txtTarget);

            btnSolve.Click += delegate { StartSolve(); };
            Controls.Add(btnSolve);

            btnCopy.Text = "复制算式";
            btnCopy.Click += delegate { ShowCopyMenu(); };
            Controls.Add(btnCopy);

            lblBlocks.Text = "最多用几个 325 块";
            lblBlocks.ForeColor = Muted;
            lblBlocks.AutoSize = false;
            lblBlocks.Visible = false;

            cboBlocks.DropDownStyle = ComboBoxStyle.DropDownList;
            cboBlocks.Items.AddRange(new object[] {
                "1 块（3 位数字）", "2 块（6 位数字）", "3 块（9 位数字，较慢）", "4 块（12 位数字，很慢）"
            });
            cboBlocks.SelectedIndex = 1;
            cboBlocks.Visible = false;

            lblOrder.Text = "数字顺序";
            lblOrder.ForeColor = Muted;
            lblOrder.AutoSize = false;
            Controls.Add(lblOrder);

            cboOrder.DropDownStyle = ComboBoxStyle.DropDownList;
            cboOrder.Items.AddRange(new object[] { "固定：325325…（默认）", "允许交换 3 / 2 / 5" });
            cboOrder.SelectedIndex = 0;
            Controls.Add(cboOrder);

            lblFactMax.Text = "阶乘上限";
            lblFactMax.ForeColor = Muted;
            lblFactMax.AutoSize = false;
            Controls.Add(lblFactMax);

            numFactMax.Minimum = 0;
            numFactMax.Maximum = 20;
            numFactMax.Value = 12;
            Controls.Add(numFactMax);

            chkFact.Text = "允许后缀阶乘 !";
            chkFact.AutoSize = false;
            chkFact.Checked = true;
            Controls.Add(chkFact);

            chkDecimal.Text = "允许小数点";
            chkDecimal.AutoSize = false;
            chkDecimal.Checked = true;
            Controls.Add(chkDecimal);

            // 高级运算总开关（“数字串”行最右侧）
            chkAdvanced.Text = "高级运算";
            chkAdvanced.AutoSize = false;
            chkAdvanced.Bounds = Rect(518, 158, 116, 22);
            chkAdvanced.Font = fontBody;

            // 高级运算已取消：chkAdvanced 不再加入界面

            // 展开后的可选运算（默认全部不勾）
            panelAdv.WrapContents = true;
            panelAdv.AutoScroll = false;
            panelAdv.Visible = false;
            panelAdv.BackColor = Color.Transparent;
            // 高级运算已取消：panelAdv 不再加入界面

            // ---- 数字串（可多选：选中的每一个都会各自算一遍并全部输出） ----
            lblPattern.Text = "数字串";
            lblPattern.ForeColor = Muted;
            lblPattern.AutoSize = false;
            Controls.Add(lblPattern);
            cboPattern.DropDownStyle = ComboBoxStyle.DropDownList;
            RebuildPatternCombo(null);
            Controls.Add(cboPattern);

            // 自定义数字串：打开新窗口增删
            btnCustom.Text = "自定义…";
            btnCustom.Cut = 5;
            btnCustom.Click += delegate
            {
                string before = cboPattern.SelectedItem == null ? null : cboPattern.SelectedItem.ToString();
                using (PatternDialog dlg = new PatternDialog(skin))
                {
                    dlg.ShowDialog(this);
                    CustomPatterns.Save(Application.StartupPath);
                }
                RebuildPatternCombo(before);
            };
            Controls.Add(btnCustom);

            btnRandom.Text = "随机模式";
            btnRandom.Cut = 5;
            btnRandom.Click += delegate { OpenRandomMode(); };
            Controls.Add(btnRandom);

            // ---- 换肤：放在“数字串”这一行的右侧，避免改动其它控件的位置 ----
            lblSkin.Text = "皮肤";
            lblSkin.AutoSize = false;
            Controls.Add(lblSkin);
            cboSkin.DropDownStyle = ComboBoxStyle.DropDownList;
            Skin[] allSkins = Skins.All();
            for (int i = 0; i < allSkins.Length; i++) cboSkin.Items.Add(allSkins[i].Name);
            cboSkin.SelectedIndex = 0;
            cboSkin.SelectedIndexChanged += delegate
            {
                Skin target = Skins.All()[cboSkin.SelectedIndex];
                if (!ReferenceEquals(target, skin)) ApplySkin(target, true);
            };
            Controls.Add(cboSkin);

            examples.WrapContents = false;
            examples.AutoScroll = false;
            Controls.Add(examples);
            ApplyLayout();

            outputCard.Skin = skin;
            outputCard.Corner = R(6);
            Controls.Add(outputCard);
            outputCard.SendToBack();

            Controls.Add(rtb);
            outputCard.SendToBack();   // 卡框必须在最底层，否则会盖住输出文本

            tips.SetToolTip(cboBlocks, "最多用几个完整的 325 数字块；3 块以上为深度搜索，可能较慢。");
            tips.SetToolTip(cboOrder, "固定：数字必须按 325325… 原顺序出现；允许交换：3/2/5 位置可任意排列。");
            tips.SetToolTip(btnCopy, "复制纯 ASCII 版本（* / -），方便贴到计算器或脚本里。");
        }

        /// <summary>高级运算面板里的单个勾选项。</summary>
        void MakeAdvBox(CheckBox box, string text, bool enabled)
        {
            box.Text = text;
            box.AutoSize = true;
            box.Enabled = enabled;
            box.Font = fontSmall;
            box.ForeColor = enabled ? skin.Ink : skin.Muted;
            if (box.Parent == null) panelAdv.Controls.Add(box);
            if (!enabled) tips.SetToolTip(box, "暂未实现，先占位");
        }

        /// <summary>总开关切换时：显示/隐藏面板，并把输出区让出来。</summary>
        void UpdateAdvancedPanel()
        {
            bool on = chkAdvanced.Checked;
            panelAdv.Visible = on;
            foreach (Control c in panelAdv.Controls) c.Visible = on;
            ApplyLayout();
        }

        // ===================== 布局 =====================
        void ApplyLayout()
        {
            SuspendLayout();

            lblTarget.Bounds = Rect(14, 12, 140, 18);
            txtTarget.Bounds = Rect(14, 32, 240, 30);
            btnSolve.Bounds = Rect(256, 22, 112, 46);
            btnCopy.Bounds = Rect(386, 31, 104, 30);

            lblBlocks.Bounds = Rect(14, 72, 170, 18);
            cboBlocks.Bounds = Rect(14, 92, 170, 26);
            lblOrder.Bounds = Rect(14, 72, 170, 18);
            cboOrder.Bounds = Rect(14, 92, 200, 26);
            lblFactMax.Bounds = Rect(230, 72, 90, 18);
            numFactMax.Bounds = Rect(230, 92, 62, 26);

            chkFact.Bounds = Rect(14, 126, 150, 24);
            chkDecimal.Bounds = Rect(178, 126, 130, 24);

            lblPattern.Bounds = Rect(14, 158, 48, 20);
            cboPattern.Bounds = Rect(66, 155, 150, 26);
            btnCustom.Bounds = Rect(222, 155, 84, 26);
            btnRandom.Bounds = Rect(510, 31, 96, 30);
            lblSkin.Bounds = Rect(318, 158, 44, 20);
            cboSkin.Bounds = Rect(364, 155, 140, 26);
            chkAdvanced.Bounds = Rect(518, 158, 116, 22);
            panelAdv.Bounds = Rect(14, 190, 712, 30);

            LayoutTitleBar();
            LayoutOutput();
            ResumeLayout(true);
        }

        void LayoutOutput()
        {
            int left = R(14);
            int top = R(200) + R(TITLE_H);   // 展开高级运算时下移
            int w = ClientSize.Width - R(28);
            int h = ClientSize.Height - top - R(14);
            if (w < R(200)) w = R(200);
            if (h < R(90)) h = R(90);
            LayoutTitleBar();
            // 输出区：底下一层削角卡片（比文本框大一圈，露出卡框）
            int pad = R(7);
            outputCard.Bounds = new Rectangle(left - pad, top - pad, w + pad * 2, h + pad * 2);
            rtb.Bounds = new Rectangle(left, top, w, h);
        }

        // 固定客户区 1920×1080
        Size DesiredClientSize()
        {
            return new Size(1920, 1080);
        }

        Size ClampTo(Screen screen)
        {
            return new Size(1920, 1080);
        }

        void ApplyMinimumSize(Screen screen)
        {
            Size fixedSize = new Size(1920, 1080);
            MinimumSize = fixedSize;
            MaximumSize = fixedSize;
        }

        // ===================== 数字串选择 =====================
        // 供测试/截图模式使用：把 "325,799" 这类参数映射成菜单选项
        /// <summary>「随机模式」：打开独立窗口，按种子 + 等级生成值恰好等于目标的式子。</summary>
        void OpenRandomMode()
        {
            TargetParse tp = Solver.ParseTarget(txtTarget.Text);
            if (!tp.Ok)
            {
                ShowMessage("输入无效", tp.Error);
                return;
            }
            using (RandomForm dlg = new RandomForm(Rats.FromInt(325), "325", skin))
            {
                dlg.ShowDialog(this);
            }
        }

        /// <summary>重建“数字串”菜单：内置 4 项 + 自定义项 + 我全都要！（自定义在“我全都要”上方）。</summary>
        /// <summary>从当前结果的“其它写法”里随机挑一条显示（值必然等于目标，已校验）。</summary>
        void PickRandomSolution()
        {
            if (lastResults == null || lastResults.Count == 0)
            {
                ShowMessage("还没有可随机的内容", "先点“求解”，再用“随机式”换一种写法。");
                return;
            }
            int bestCount = 0;
            for (int i = 0; i < lastResults.Count; i++)
            {
                SolveResult r = ResultOf(lastResults[i]);
                if (r != null && r.Alternatives.Count > bestCount) bestCount = r.Alternatives.Count;
            }
            int span = Math.Min(bestCount, 6);
            randomPick = span <= 1 ? 0 : rnd.Next(0, span);
            RenderAll(lastResults);
        }

        /// <summary>lastResults 里可能是 SolveResult，也可能是 (pattern, SolveResult) 二元组。</summary>
        static SolveResult ResultOf(object item)
        {
            SolveResult r = item as SolveResult;
            if (r != null) return r;
            object[] pair = item as object[];
            if (pair != null && pair.Length > 1) return pair[1] as SolveResult;
            return null;
        }

        void RebuildPatternCombo(string keep)
        {
            string all = PatternItems[4];
            cboPattern.BeginUpdate();
            cboPattern.Items.Clear();
            for (int i = 0; i < CustomPatterns.BuiltIn.Length; i++) cboPattern.Items.Add(CustomPatterns.BuiltIn[i]);
            for (int i = 0; i < CustomPatterns.Items.Count; i++) cboPattern.Items.Add(CustomPatterns.Items[i]);
            cboPattern.Items.Add(all);
            cboPattern.EndUpdate();

            int idx = string.IsNullOrEmpty(keep) ? 0 : cboPattern.Items.IndexOf(keep);
            if (idx < 0) idx = 0;
            cboPattern.SelectedIndex = idx;
        }

        void SelectPatternsFromCsv(string[] want)
        {
            string[] all = CustomPatterns.All();
            int count = 0, firstIdx = 0;
            for (int i = 0; i < all.Length; i++)
            {
                for (int j = 0; j < want.Length; j++)
                {
                    if (want[j].Trim() == all[i]) { count++; if (firstIdx == 0) firstIdx = i; }
                }
            }
            if (count >= all.Length) cboPattern.SelectedIndex = cboPattern.Items.Count - 1;
            else cboPattern.SelectedIndex = Math.Min(firstIdx, cboPattern.Items.Count - 1);
        }

        string[] SelectedPatterns()
        {
            int idx = cboPattern.SelectedIndex;
            if (idx < 0) return new string[] { CustomPatterns.BuiltIn[0] };
            if (idx == cboPattern.Items.Count - 1) return CustomPatterns.All();   // 我全都要！
            return new string[] { cboPattern.Items[idx].ToString() };
        }

        // ===================== 求解 =====================
        Limits CurrentLimits()
        {
            Limits l = new Limits();
            l.MaxBlocks = 2;   // 固定 2 块（原“最多用几个 325 块”已移除）
            l.OrderMode = cboOrder.SelectedIndex == 1 ? "any" : "fixed";
            l.AllowFactorial = chkFact.Checked;
            l.AllowDecimal = chkDecimal.Checked;
            l.AllowPower = false;   // 高级运算已取消
            l.AllowRoot = false;
            l.AllowDoubleFact = false;
            l.AllowComb = false;
            l.AllowAbs = false;
            l.AllowCbrt = false;
            l.AllowPercent = false;
            l.AllowLcmLog = false;
            l.AllowGcdMod = false;
            // 记账用：把已勾选的运算告诉求解器
            System.Collections.Generic.List<string> want = new System.Collections.Generic.List<string>();
            if (chkAdvanced.Checked)
            {
                if (chkPower.Checked) want.Add("乘方");
                if (chkRoot.Checked) want.Add("根号");
                if (chkComb.Checked) want.Add("组合数");
                if (chkGcdMod.Checked) { want.Add("gcd"); want.Add("mod"); }
                if (chkDet.Checked) want.Add("行列式");
            }
            l.RequiredOps = string.Join(",", want.ToArray());

            l.AllowDet = false;
            l.FactMax = (int)numFactMax.Value;
            l.TimeBudgetMs = l.MaxBlocks >= 3 ? 6000 : 3000;
            l.SolutionListLimit = 8;
            return l;
        }

        static string KeyOf(Limits l)
        {
            return l.Pattern + "|" + l.MaxBlocks + "|" + l.OrderMode + "|" + l.AllowDecimal + "|" + l.AllowFactorial + "|" + l.FactMax + "|" + l.TimeBudgetMs + "|" + l.AllowPower + "|" + l.AllowDet;
        }

        Solver.Session SessionFor(Limits lim)
        {
            string key = KeyOf(lim);
            Solver.Session s;
            if (!sessions.TryGetValue(key, out s))
            {
                s = new Solver.Session(lim);
                sessions[key] = s;
            }
            return s;
        }

        void StartSolve()
        {
            string target = txtTarget.Text;
            TargetParse tp = Solver.ParseTarget(target);
            if (!tp.Ok)
            {
                ShowMessage("输入无效", tp.Error);
                return;
            }
            string[] selected = SelectedPatterns();
            if (selected.Length == 0)
            {
                ShowMessage("请至少选择一个数字串", "在“数字串”那一行勾选 325 / 799 / 325799 / 114514 中至少一个。");
                return;
            }
            Rat value = tp.Value;
            string text = tp.Text;
            List<Solver.Session> useSessions = new List<Solver.Session>();
            List<string> usePatterns = new List<string>();
            foreach (string p in selected)
            {
                Limits lim = CurrentLimits();
                lim.Pattern = p;
                useSessions.Add(SessionFor(lim));
                usePatterns.Add(p);
            }

            btnSolve.Enabled = false;
            rtb.Clear();
            lastResults = null;
            AppendLine("计算中… 正在搜索 " + string.Join(" / ", usePatterns.ToArray()) + " 的切分与运算组合。", fontBody, Muted);
            ScrollOutputToTop();
            pendingTarget = text;

            BackgroundWorker bw = new BackgroundWorker();
            bw.DoWork += delegate(object s, DoWorkEventArgs e)
            {
                List<object[]> outList = new List<object[]>();
                for (int i = 0; i < useSessions.Count; i++)
                {
                    outList.Add(new object[] { usePatterns[i], useSessions[i].Solve(value, text) });
                }
                e.Result = outList;
            };
            bw.RunWorkerCompleted += delegate(object s, RunWorkerCompletedEventArgs e)
            {
                btnSolve.Enabled = true;
                if (e.Error != null)
                {
                    ShowMessage("求解异常", e.Error.Message);
                    return;
                }
                RenderAll((List<object[]>)e.Result);
            };
            bw.RunWorkerAsync();
        }

        // 显示用：在运算符两侧加空格——既便于阅读，也让自动换行发生在运算符处而不是数字中间。
        // 注意：复制按钮复制的始终是没有空格的 ASCII 版本，不受影响。
        internal static string DisplayFormula(string ascii)
        {
            if (string.IsNullOrEmpty(ascii)) return "";
            StringBuilder sb = new StringBuilder(ascii.Length + 16);
            for (int i = 0; i < ascii.Length; i++)
            {
                char c = ascii[i];
                if (c == '+' || c == '-' || c == '*' || c == '/')
                {
                    while (sb.Length > 0 && sb[sb.Length - 1] == ' ') sb.Length--;
                    if (sb.Length > 0) sb.Append(' ');
                    sb.Append(c).Append(' ');
                }
                else sb.Append(c);
            }
            return Solver.Pretty(sb.ToString().Trim());
        }

        // ===================== 输出（唯一输出区） =====================
        void Append(string text, Font font, Color color)
        {
            rtb.Append(text, font, color);
        }

        void AppendLine(string text, Font font, Color color)
        {
            Append(text + Environment.NewLine, font, color);
        }

        // 长文本先看到最上方
        void ScrollOutputToTop()
        {
            rtb.ScrollToTop();
        }

        void ShowMessage(string title, string body)
        {
            rtb.Clear();
            lastCopy = "";
            AppendLine(title, fontBody, Warn);
            AppendLine(body == null ? "" : body, fontBody, Ink);
            ScrollOutputToTop();
        }

        // 多个数字串的结果全部输出在这一个输出区里，每个一段，尽量精简
        void RenderAll(List<object[]> results)
        {
            lastResults = results;
            rtb.Clear();
            List<string> copies = new List<string>();
            for (int i = 0; i < results.Count; i++)
            {
                string pat = (string)results[i][0];
                SolveResult r = (SolveResult)results[i][1];
                if (i > 0) AppendLine("", fontSmall, Muted);
                RenderOne(pat, r);
                if (r.Ok) copies.Add(r.Expression);
                else if (r.FallbackResult != null) copies.Add(r.FallbackResult.Expression);
            }
            lastCopy = string.Join("\n", copies.ToArray());
            ScrollOutputToTop();
        }

        /// <summary>把异常写到 exe 同目录的 error.log，并弹窗提示（避免“闪退”无痕迹）。</summary>
        internal static void ReportCrash(Exception ex)
        {
            string msg = ex == null ? "未知错误" : ex.ToString();
            try
            {
                File.AppendAllText(Path.Combine(Application.StartupPath, "error.log"),
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + Environment.NewLine + msg + Environment.NewLine + Environment.NewLine,
                    new UTF8Encoding(false));
            }
            catch (Exception) { }

            // 防"弹窗套弹窗"：MessageBox 会跑消息循环，同一条异常（比如每次重绘都抛）
            // 会立刻再次进来，最后堆成关不完的窗口。这里只让第一条弹窗，其余静默记账。
            if (crashDialogOpen) return;
            if ((DateTime.UtcNow - lastCrashAt).TotalMilliseconds < 1500) return;
            crashDialogOpen = true;
            lastCrashAt = DateTime.UtcNow;
            try
            {
                MessageBox.Show("程序遇到一个错误（已写入 error.log）：\n\n" +
                    (ex == null ? "未知错误" : ex.Message),
                    "计算325", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            catch (Exception) { }
            finally
            {
                crashDialogOpen = false;
                lastCrashAt = DateTime.UtcNow;
            }
        }

        static bool crashDialogOpen;
        static DateTime lastCrashAt = DateTime.MinValue;

        string FirstOutputLine()
        {
            string all = rtb.GetText();
            int nl = all.IndexOf('\n');
            string first = nl < 0 ? all : all.Substring(0, nl);
            return first.TrimEnd('\r');
        }

        void RenderOne(string pat, SolveResult r)
        {
            AppendLine("【数字串 " + pat + "】", fontHead, Brand);

            bool hasFallback = r.FallbackResult != null;
            string expression = r.Ok ? r.Expression : (hasFallback ? r.FallbackResult.Expression : null);
            int blocks = r.Ok ? r.Blocks : (hasFallback ? r.FallbackResult.Blocks : 0);
            // “随机式”：在当前结果的多条合法写法里切换到第 randomPick 条
            if (randomPick > 0 && r.Alternatives.Count > randomPick)
            {
                expression = r.Alternatives[randomPick].Expression;
                blocks = r.Alternatives[randomPick].Blocks;
            }

            if (expression == null)
            {
                AppendLine("未能在 " + (cboBlocks.SelectedIndex + 1) + " 块内找到算式，也没有可用的保底构造。"
                    + (r.Error != null ? "（" + r.Error + "）" : ""), fontBody, Warn);
                return;
            }

            Append(DisplayFormula(expression), fontBig, Ink);   // LaTeX 渲染离线已可用，界面接入待调字号/尾随对齐
            if (!string.IsNullOrEmpty(r.TargetText))
            {
                Append("  =  " + Solver.Pretty(r.TargetText), fontBig, Muted);
            }
            Append(Environment.NewLine, fontBig, Ink);

            VerifyResult vr = Solver.Verify(expression, r.Target, true, r.OrderMode, r.Pattern);
            string opsNote = "";
            Append(Solver.DigitsOf(expression) + "  ·  " + blocks + " 块  ·  " + Solver.CountTokens(expression) + " 个数  ·  " + r.ElapsedMs + " ms"
                + (opsNote.Length > 0 ? "  ·  用到：" + opsNote : ""),
                fontSmall, Muted);
            Append(Environment.NewLine, fontSmall, Muted);

            if (r.Ok && r.Alternatives.Count > 1)
            {


                System.Collections.Generic.HashSet<string> shown = new System.Collections.Generic.HashSet<string>();
                shown.Add(r.Alternatives[1].Pretty);
                Append("其它写法：" + r.Alternatives[1].Pretty, fontMono, skin.Accent2);

                for (int i = 2; i < r.Alternatives.Count && i <= 3; i++)
                {

                    if (!shown.Add(r.Alternatives[i].Pretty)) continue;
                    Append("   ｜   " + r.Alternatives[i].Pretty, fontMono, skin.Accent2);
                }
                Append(Environment.NewLine, fontMono, Muted);
            }
        }

        /// <summary>把引用式算式转 LaTeX（\\\\times 等）。</summary>
        static string ToLatex(string expr)
        {
            string s = expr.Replace("×", " \\times ").Replace("÷", " \\div ").Replace("−", "-");
            s = s.Replace("(", "\\left(").Replace(")", "\\right)");
            return s;
        }

        /// <summary>转纯 ASCII（* / -），方便贴计算器。</summary>
        static string ToAscii(string expr)
        {
            return expr.Replace("×", "*").Replace("÷", "/").Replace("−", "-") + "";
        }

        void ShowCopyMenu()
        {
            if (lastCopy.Length == 0)
            {
                ShowMessage("还没有可复制的内容", "先点“求解”得到算式。");
                return;
            }
            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Font = new Font(PickFamily(skin.BodyFamily, UiFamily()), 10f);
            ToolStripItem a = menu.Items.Add("复制为纯 ASCII（* / -）");
            a.Click += delegate { CopyText(ToAscii(lastCopy), "已复制 ASCII"); };
            ToolStripItem l = menu.Items.Add("复制为 LaTeX（\\times / \\div）");
            l.Click += delegate { CopyText(ToLatex(lastCopy), "已复制 LaTeX"); };
            menu.Items.Add(new ToolStripSeparator());
            ToolStripItem p = menu.Items.Add("导出为 PNG 图片…");
            p.Click += delegate { ExportPng(); };
            menu.Show(btnCopy, new Point(0, btnCopy.Height));
        }

        void CopyText(string text, string what)
        {
            try
            {
                Clipboard.SetText(text);
                btnCopy.Text = what;
                Timer t = new Timer();
                t.Interval = 1300;
                t.Tick += delegate { btnCopy.Text = "复制算式"; t.Stop(); t.Dispose(); };
                t.Start();
            }
            catch (Exception ex) { ShowMessage("复制失败", ex.Message); }
        }

        void ExportPng()
        {
            try
            {
                using (SaveFileDialog dlg = new SaveFileDialog())
                {
                    dlg.Title = "导出输出区为图片";
                    dlg.Filter = "PNG 图片|*.png";
                    dlg.FileName = "calculate325-" + (pendingTarget == null ? "result" : pendingTarget) + ".png";
                    if (dlg.ShowDialog(this) != DialogResult.OK) return;
                    using (Bitmap bmp = new Bitmap(Math.Max(1, rtb.Width), Math.Max(1, rtb.Height)))
                    {
                        rtb.DrawToBitmap(bmp, new Rectangle(0, 0, bmp.Width, bmp.Height));
                        bmp.Save(dlg.FileName, System.Drawing.Imaging.ImageFormat.Png);
                    }
                    btnCopy.Text = "已导出";
                    Timer t = new Timer();
                    t.Interval = 1500;
                    t.Tick += delegate { btnCopy.Text = "复制算式"; t.Stop(); t.Dispose(); };
                    t.Start();
                }
            }
            catch (Exception ex) { ShowMessage("导出失败", ex.Message); }
        }

        void DoCopy()
        {
            if (lastCopy.Length == 0)
            {
                ShowMessage("还没有可复制的内容", "先点“求解”得到算式。");
                return;
            }
            try
            {
                Clipboard.SetText(lastCopy);
                btnCopy.Text = "已复制";
                Timer t = new Timer();
                t.Interval = 1200;
                t.Tick += delegate
                {
                    btnCopy.Text = "复制算式";
                    t.Stop();
                    t.Dispose();
                };
                t.Start();
            }
            catch (Exception ex)
            {
                ShowMessage("复制失败", ex.Message);
            }
        }

        static string BoundsOf(Control c)
        {
            return string.Format(CultureInfo.InvariantCulture, "{0},{1},{2},{3}", c.Left, c.Top, c.Width, c.Height);
        }

        // ---------- 无窗口模式（供测试校验） ----------
        internal string RunHeadlessRender(string target, int maxBlocks, string patternsCsv)
        {
            CreateControl();
            txtTarget.Text = target;
            if (maxBlocks >= 1 && maxBlocks <= 4) cboBlocks.SelectedIndex = maxBlocks - 1;
            // 无头渲染（测试用）：按传入的 CSV 精确求解这几条，不受"我全都要"下拉项影响。
            // 之前走 SelectPatternsFromCsv：一旦本地 patterns.txt 里多了自定义数字串，
            // "4 条内置"就不再等于"全部"，会静默退化成只算 1 条。
            string[] headlessPatterns = null;
            if (!string.IsNullOrEmpty(patternsCsv))
            {
                List<string> want = new List<string>();
                foreach (string s in patternsCsv.Split(','))
                {
                    string t2 = s.Trim();
                    if (t2.Length > 0 && !want.Contains(t2)) want.Add(t2);
                }
                if (want.Count > 0) headlessPatterns = want.ToArray();
            }
            TargetParse tp = Solver.ParseTarget(target);
            if (!tp.Ok)
            {
                ShowMessage("输入无效", tp.Error);
            }
            else
            {
                List<object[]> list = new List<object[]>();
                foreach (string p in (headlessPatterns ?? SelectedPatterns()))
                {
                    Limits lim = CurrentLimits();
                    lim.Pattern = p;
                    list.Add(new object[] { p, SessionFor(lim).Solve(tp.Value, tp.Text) });
                }
                RenderAll(list);
            }
            int firstVisible = 0;
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("=====OUTPUT=====");
            sb.Append(rtb.Text);
            sb.AppendLine();
            sb.AppendLine("=====LASTCOPY=====");
            sb.AppendLine(lastCopy);
            sb.AppendLine("=====VIEW=====");
            sb.AppendLine("FIRST_VISIBLE_LINE=" + firstVisible.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("SELECTION_START=0");
            sb.AppendLine("LINE_COUNT=" + rtb.GetText().Split(new string[] { Environment.NewLine }, StringSplitOptions.None).Length.ToString(CultureInfo.InvariantCulture));
            try
            {
                Point p0 = Point.Empty;
                sb.AppendLine("FIRST_CHAR_POS=" + p0.X + "," + p0.Y);
                sb.AppendLine("FIRST_LINE_TEXT=" + FirstOutputLine());
            }
            catch (Exception) { }
            return sb.ToString();
        }

        internal string RunDiagnostics(int forcedDpiValue)
        {
            forcedDpi = forcedDpiValue >= 48 ? forcedDpiValue : 0;
            dpiUsed = forcedDpi > 0 ? forcedDpi : dpiUsed;
            scale = dpiUsed / 96f;
            CreateControl();                    // 触发 OnHandleCreated（会按 forcedDpi 处理）
            RecreateFonts();
            ApplyLayout();
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("DPI_AWARENESS_THREAD=" + ThreadAwarenessName());
            sb.AppendLine("DPI_AWARENESS_PROCESS=" + ProcessAwarenessName());
            sb.AppendLine("DPI_SYSTEM=" + SafeGetDpiForSystem().ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("DPI_WINDOW=" + SafeGetDpiForWindow().ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("FORCED_DPI=" + forcedDpi.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("DPI_USED=" + dpiUsed.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("LAYOUT_SCALE=" + scale.ToString("0.###", CultureInfo.InvariantCulture));
            sb.AppendLine("CLIENT_SIZE=" + ClientSize.Width + "x" + ClientSize.Height);
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            sb.AppendLine("WORKAREA=" + wa.Width + "x" + wa.Height);
            sb.AppendLine("MIN_SIZE=" + MinimumSize.Width + "x" + MinimumSize.Height);
            sb.AppendLine("BOUNDS_TXT=" + BoundsOf(txtTarget));
            sb.AppendLine("BOUNDS_SOLVE=" + BoundsOf(btnSolve));
            sb.AppendLine("BOUNDS_CHK_DECIMAL=" + BoundsOf(chkDecimal));
            sb.AppendLine("BOUNDS_RTB=" + BoundsOf(rtb));
            sb.AppendLine("BOUNDS_SKIN=" + BoundsOf(cboSkin));
            sb.AppendLine("SKIN_COUNT=" + cboSkin.Items.Count.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("SKIN_CURRENT=" + skin.Key);
            sb.AppendLine("PATTERN_COUNT=" + cboPattern.Items.Count.ToString(CultureInfo.InvariantCulture));
            string items = "";
            foreach (object o in cboPattern.Items) items += (items.Length == 0 ? "" : ",") + o.ToString();
            sb.AppendLine("PATTERN_ITEMS=" + items);
            sb.AppendLine("SKIN_HAS_BG=" + (BackgroundImage != null ? "1" : "0"));
            // 三个窗口按钮必须是 TitleButton（不是 Label）——否则“拖窗口”会抢走点击，导致关不掉
            sb.AppendLine("TITLE_BUTTONS=" + ((btnMin is TitleButton && btnMax is TitleButton && btnClose is TitleButton) ? "3" : "0"));
            sb.AppendLine("FORM_BORDER=" + FormBorderStyle.ToString());
            sb.AppendLine("--- RECTS ---");
            foreach (Control c in Controls)
            {
                if (c == titleBar || c == examples) continue;
                sb.AppendLine("RECT=" + c.GetType().Name + "|" + c.Left + "," + c.Top + "," + c.Width + "," + c.Height);
            }
            foreach (Control c in titleBar.Controls)
            {
                sb.AppendLine("RECT=" + c.GetType().Name + "|" + c.Left + "," + c.Top + "," + c.Width + "," + c.Height);
            }
            foreach (Control c in examples.Controls)
            {
                sb.AppendLine("RECT=ExampleButton|" + c.Left + "," + c.Top + "," + c.Width + "," + c.Height);
            }
            sb.AppendLine("FONT_BODY_HEIGHT=" + fontBody.Height.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("FONT_BIG_HEIGHT=" + fontBig.Height.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("AUTO_SCALE_MODE=" + AutoScaleMode.ToString());
            sb.AppendLine("FORM_WINDOW=" + Width + "x" + Height);
            sb.AppendLine("FORM_CLIENT_NOW=" + ClientSize.Width + "x" + ClientSize.Height);
            Rectangle wc = Screen.FromControl(this).WorkingArea;
            sb.AppendLine("WORKAREA_FROMCONTROL=" + wc.Width + "x" + wc.Height);
            sb.AppendLine("DESIRED_CLIENT=" + DesiredClientSize().Width + "x" + DesiredClientSize().Height);
            return sb.ToString();
        }
    }

    public static class Program
    {
        [STAThread]
        public static void Main(string[] args)
        {
            if (args.Length > 0 && args[0] == "--selftest")
            {
                string outPath = args.Length > 1 ? args[1] : "selftest-report.txt";
                string tsv = args.Length > 2 ? args[2] : Path.Combine("tests", "expected-js.tsv");
                string report = SelfTest.Run(tsv);
                File.WriteAllText(outPath, report, new UTF8Encoding(false));
                Environment.Exit(SelfTest.LastExitCode);
                return;
            }
            if (args.Length > 0 && args[0] == "--render-test")
            {
                // --render-test <outfile> <target> <maxBlocks> [pattern1,pattern2]
                string outPath = args[1];
                string target = args[2];
                int maxBlocks = args.Length > 3 ? int.Parse(args[3], CultureInfo.InvariantCulture) : 2;
                MainForm f = new MainForm();
                string pats = args.Length > 4 ? args[4] : null;
                string dump = f.RunHeadlessRender(target, maxBlocks, pats);
                File.WriteAllText(outPath, dump, new UTF8Encoding(false));
                Environment.Exit(0);
                return;
            }
            if (args.Length > 0 && args[0] == "--diag")
            {
                // --diag <outfile> [强制 DPI，用于验证不同缩放下的布局]
                string outPath = args[1];
                int forced = args.Length > 2 ? int.Parse(args[2], CultureInfo.InvariantCulture) : 0;
                MainForm f = new MainForm();
                string dump = f.RunDiagnostics(forced);
                File.WriteAllText(outPath, dump, new UTF8Encoding(false));
                Environment.Exit(0);
                return;
            }
            if (args.Length > 0 && args[0] == "--diag-window")
            {
                // --diag-window <outfile> [毫秒]：真的开一个窗口，显示后把实际状态写文件再自动关闭
                string outPath = args[1];
                int delay = args.Length > 2 ? int.Parse(args[2], CultureInfo.InvariantCulture) : 2500;
                MainForm f = new MainForm();
                Timer t = new Timer();
                t.Interval = delay;
                t.Tick += delegate
                {
                    t.Stop();
                    try
                    {
                        string dump = f.RunDiagnostics(0)
                            + "SHOWN_WINDOW=" + f.Width + "x" + f.Height + "\n"
                            + "SHOWN_CLIENT=" + f.ClientSize.Width + "x" + f.ClientSize.Height + "\n"
                            + "SHOWN_LOCATION=" + f.Left + "," + f.Top + "\n"
                            + "SHOWN_BOUNDS=" + f.Bounds + "\n"
                            + "SHOWN_DPI_REAL=" + MainForm.SafeDpiOfWindow(f.Handle) + "\n"
                            + "DEVICE_DPI_PROPERTY=" + f.DeviceDpi + "\n";
                        File.WriteAllText(outPath, dump, new UTF8Encoding(false));
                    }
                    catch (Exception ex)
                    {
                        File.WriteAllText(outPath, "ERROR " + ex.Message, new UTF8Encoding(false));
                    }
                    f.Close();
                };
                t.Start();
                Application.Run(f);
                return;
            }
            for (int ai = 0; ai < args.Length; ai++)
            {
                if (args[ai] == "--power") MainForm.ForcePower = true;
                if (args[ai] == "--adv") MainForm.ForceAdvanced = true;
            }
            if (args.Length > 0 && args[0] == "--random-form-test")
            {
                // --random-form-test <outfile> [target] [每级次数]：不显示窗口，模拟连点生成
                string outPath = args.Length > 1 ? args[1] : "random-form-test.txt";
                string targetArg = args.Length > 2 ? args[2] : "325";
                int count = args.Length > 3 ? int.Parse(args[3], CultureInfo.InvariantCulture) : 20;
                TargetParse tp = Solver.ParseTarget(targetArg);
                int code;
                if (!tp.Ok)
                {
                    File.WriteAllText(outPath, "ERROR " + tp.Error, new UTF8Encoding(false));
                    code = 2;
                }
                else
                {
                    try
                    {
                        using (RandomForm f = new RandomForm(tp.Value, tp.Text, Skins.Plain, new Random(20261018)))
                        {
                            f.CreateControl();
                            string dump = f.RunGenerationSelfTest(count);
                            File.WriteAllText(outPath, dump, new UTF8Encoding(false));
                            code = f.LastSelfTestFailed ? 1 : 0;
                        }
                    }
                    catch (Exception ex)
                    {
                        File.WriteAllText(outPath, "ERROR " + ex, new UTF8Encoding(false));
                        code = 3;
                    }
                }
                Environment.Exit(code);
                return;
            }
            if (args.Length > 0 && args[0] == "--set-seedmode")
            {
                // --set-seedmode <0|1>：写种子模式（0=十进制，1=Zc），等价于在窗口里切换模式
                SeedDialog.Mode = (args.Length > 1 && args[1] == "1") ? 1 : 0;
                Console.WriteLine("SEEDMODE=" + (SeedDialog.Mode == 1 ? "zc" : "dec"));
                Console.WriteLine("SEEDMODE_FILE=" + SeedDialog.ModePathForTest());
                Environment.Exit(0);
                return;
            }
            if (args.Length > 0 && args[0] == "--seed-dialog-shot")
            {
                // --seed-dialog-shot <out.png> [level] [seed] [zc]
                string outPath = args[1];
                int lvl = args.Length > 2 ? int.Parse(args[2], CultureInfo.InvariantCulture) : 5;
                string seedArg = args.Length > 3 ? args[3] : "";
                bool zc = args.Length > 4 && string.Equals(args[4], "zc", StringComparison.OrdinalIgnoreCase);
                using (SeedDialog d = new SeedDialog(Skins.Plain, lvl, seedArg))
                {
                    d.StartPosition = FormStartPosition.Manual;
                    d.Location = new System.Drawing.Point(-4000, -4000);
                    d.ShowInTaskbar = false;
                    d.Show();
                    d.SetMode(zc);
                    if (args.Length > 5) d.SetText(args[5]);
                    Application.DoEvents();
                    System.Threading.Thread.Sleep(200);
                    Application.DoEvents();
                    using (System.Drawing.Bitmap bmp = new System.Drawing.Bitmap(d.Width, d.Height))
                    {
                        d.DrawToBitmap(bmp, new System.Drawing.Rectangle(0, 0, bmp.Width, bmp.Height));
                        bmp.Save(outPath, System.Drawing.Imaging.ImageFormat.Png);
                    }
                    d.Close();
                }
                return;
            }

            if (args.Length > 0 && args[0] == "--math-one")
            {
                // --math-one <out.txt> <latex>：单条 WpfMath 渲染探针（调试 L5 结构用）
                string outPath = args.Length > 1 ? args[1] : "math-one.txt";
                string latex = args.Length > 2 ? args[2] : "";
                if (!string.IsNullOrEmpty(latex) && latex[0] == '@')
                {
                    string lf = latex.Substring(1);
                    if (File.Exists(lf)) latex = File.ReadAllText(lf, Encoding.UTF8);
                }
                try
                {
                    WpfMathBootstrap.Ensure();
                    using (Bitmap bmp = WpfMathRenderer.Render(latex, 44f, Color.Black, Color.Transparent))
                    {
                        File.WriteAllText(outPath, "OK " + bmp.Width + "x" + bmp.Height, new UTF8Encoding(false));
                    }
                    Environment.Exit(0);
                }
                catch (Exception ex)
                {
                    File.WriteAllText(outPath, "ERROR " + ex.Message, new UTF8Encoding(false));
                    Environment.Exit(3);
                }
                return;
            }
            if (args.Length > 0 && args[0] == "--math-test")
            {
                // --math-test <outfile> [L5 条数]：WpfMath 兼容性自检（陷阱构造 + L5 真实式子）
                string outPath = args.Length > 1 ? args[1] : "math-test.txt";
                int count = args.Length > 2 ? int.Parse(args[2], CultureInfo.InvariantCulture) : 8;
                int code = 0;
                try
                {
                    Skin mtSkin = args.Length > 3 ? Skins.ByKey(args[3]) : Skins.Plain;
                    using (RandomForm f = new RandomForm(new Rat(new System.Numerics.BigInteger(325), System.Numerics.BigInteger.One), "325", mtSkin, new Random(20261018)))
                    {
                        f.CreateControl();
                        string dump = f.RunMathCompatTest(count);
                        File.WriteAllText(outPath, dump, new UTF8Encoding(false));
                        code = f.LastSelfTestFailed ? 1 : 0;
                    }
                }
                catch (Exception ex)
                {
                    File.WriteAllText(outPath, "ERROR " + ex, new UTF8Encoding(false));
                    code = 3;
                }
                Environment.Exit(code);
                return;
            }
            if (args.Length > 0 && args[0] == "--screen-shot")
            {
                // --screen-shot <out.png> [which] [skin] [count]
                // which = history | random | seed。抓**屏幕**（含原生标题栏），用于核对标题栏配色。
                string outPath = args[1];
                string which = args.Length > 2 ? args[2] : "history";
                string skinKey = args.Length > 3 ? args[3] : "cisuixing";
                int count = args.Length > 4 ? int.Parse(args[4], CultureInfo.InvariantCulture) : 6;
                Skin sk = Skins.ByKey(skinKey);
                Form target = null;
                if (which == "random")
                {
                    target = new RandomForm(new Rat(325, 1), "325", sk);
                }
                else if (which == "seed")
                {
                    target = new SeedDialog(sk, 4, "12345678901234567890123456789012");
                }
                else
                {
                    List<HistoryItem> items = new List<HistoryItem>();
                    Random rnd = new Random(20261025);
                    for (int i = 0; i < count; i++)
                    {
                        int level = 1 + rnd.Next(5);
                        string seed = level == 5 ? RandomExpr.MakeL5Seed(rnd) : RandomExpr.MakeSeed(level, rnd);
                        RandomExprResult r = null;
                        try { r = RandomExpr.BuildLevel(level, new Rat(325, 1), rnd, seed, true); }
                        catch (Exception) { }
                        if (r == null) continue;
                        HistoryItem it = new HistoryItem();
                        it.Seq = items.Count + 1;
                        it.Time = DateTime.Now.AddMinutes(-i * 2);
                        it.Level = level;
                        it.Seed = seed;
                        it.Latex = r.Latex;
                        it.Expression = r.Expression;
                        it.Kind = r.Kind;
                        it.Codes = r.TypeCodes;
                        it.Layers = r.Layers;
                        it.Dims = r.Dims;
                        it.Complexity = r.Complexity;
                        items.Add(it);
                    }
                    target = new HistoryForm(sk, items, "325");
                }
                target.StartPosition = FormStartPosition.Manual;
                target.Location = new Point(40, 40);
                target.Show();
                target.Activate();
                Application.DoEvents();
                Timer t = new Timer();
                t.Interval = 700;
                t.Tick += delegate
                {
                    t.Stop();
                    try
                    {
                        Rectangle b = target.Bounds;
                        using (Bitmap bmp = new Bitmap(b.Width, b.Height))
                        using (Graphics g = Graphics.FromImage(bmp))
                        {
                            g.CopyFromScreen(b.Left, b.Top, 0, 0, b.Size);
                            bmp.Save(outPath, System.Drawing.Imaging.ImageFormat.Png);
                        }
                    }
                    catch (Exception ex)
                    {
                        try { File.WriteAllText(outPath + ".error.txt", ex.ToString(), new UTF8Encoding(false)); }
                        catch (Exception) { }
                    }
                    target.Close();
                };
                t.Start();
                Application.Run(target);
                return;
            }
            if (args.Length > 0 && args[0] == "--history-shot")
            {
                // --history-shot <out.png> [target] [count] [skin]：构造若干历史记录并给历史窗口截图
                string outPath = args[1];
                string targetArg = args.Length > 2 ? args[2] : "325";
                int count = args.Length > 3 ? int.Parse(args[3], CultureInfo.InvariantCulture) : 8;
                string skinKey = args.Length > 4 ? args[4] : null;
                TargetParse tp = Solver.ParseTarget(targetArg);
                if (!tp.Ok)
                {
                    File.WriteAllText(outPath + ".error.txt", tp.Error, new UTF8Encoding(false));
                    Environment.Exit(2);
                    return;
                }
                if (count < 1) count = 1;
                if (count > 60) count = 60;
                bool preview = args.Length > 5 && args[5] == "preview";
                List<HistoryItem> items = new List<HistoryItem>();
                Random rnd = new Random(20261024);
                for (int i = 0; i < count; i++)
                {
                    int level = 1 + rnd.Next(5);
                    string seed = level == 5 ? RandomExpr.MakeL5Seed(rnd) : RandomExpr.MakeSeed(level, rnd);
                    RandomExprResult r = null;
                    try { r = RandomExpr.BuildLevel(level, tp.Value, rnd, seed, true); }
                    catch (Exception) { }
                    if (r == null) continue;
                    HistoryItem it = new HistoryItem();
                    it.Seq = items.Count + 1;
                    it.Time = DateTime.Now.AddMinutes(-i * 3);
                    it.Level = level;
                    it.Seed = seed;
                    it.Latex = r.Latex;
                    it.Expression = r.Expression;
                    it.Kind = r.Kind;
                    it.Codes = r.TypeCodes;
                    it.Layers = r.Layers;
                    it.Dims = r.Dims;
                    it.Complexity = r.Complexity;
                    items.Add(it);
                }
                HistoryForm hf = new HistoryForm(Skins.ByKey(skinKey), items, tp.Text);
                Form shotForm = hf;
                HistoryPreviewForm pv = null;
                if (preview && items.Count > 0)
                {
                    pv = new HistoryPreviewForm(Skins.ByKey(skinKey), items, 0);
                    shotForm = pv;
                }
                shotForm.Show();
                Application.DoEvents();
                try
                {
                    using (Bitmap bmp = new Bitmap(shotForm.Width, shotForm.Height))
                    {
                        shotForm.DrawToBitmap(bmp, new Rectangle(0, 0, shotForm.Width, shotForm.Height));
                        bmp.Save(outPath, System.Drawing.Imaging.ImageFormat.Png);
                    }
                }
                catch (Exception ex)
                {
                    try { File.WriteAllText(outPath + ".error.txt", ex.ToString(), new UTF8Encoding(false)); }
                    catch (Exception) { }
                }
                if (pv != null) pv.Close();
                hf.Close();
                return;
            }
            if (args.Length > 0 && args[0] == "--shot-random")
            {
                // --shot-random <out.png> [target] [level] [skin] [seed]：打开随机模式窗口并截图
                string outPath = args[1];
                string targetArg = args.Length > 2 ? args[2] : "325";
                int level = args.Length > 3 ? int.Parse(args[3], CultureInfo.InvariantCulture) : 3;
                string skinKey = args.Length > 4 ? args[4] : null;
                string seedArg = args.Length > 5 ? args[5] : null;
                TargetParse tp = Solver.ParseTarget(targetArg);
                if (!tp.Ok)
                {
                    File.WriteAllText(outPath + ".error.txt", tp.Error, new UTF8Encoding(false));
                    Environment.Exit(2);
                    return;
                }
                RandomForm rf = new RandomForm(tp.Value, tp.Text, Skins.ByKey(skinKey));
                rf.SetLevelForTest(level);
                if (!string.IsNullOrEmpty(seedArg)) rf.SetSeedForTest(seedArg);
                rf.Shown += delegate
                {
                    Timer st = new Timer();
                    st.Interval = 900;
                    st.Tick += delegate
                    {
                        st.Stop();
                        try { rf.SaveShot(outPath); }
                        catch (Exception ex)
                        {
                            try { File.WriteAllText(outPath + ".error.txt", ex.ToString(), new UTF8Encoding(false)); }
                            catch (Exception) { }
                        }
                        rf.Close();
                    };
                    st.Start();
                };
                Application.Run(rf);
                return;
            }
            if (args.Length > 0 && args[0] == "--shot")
            {
                // --shot <out.png> [target] [maxBlocks]：真的开窗口、求解、截图后自动关闭
                MainForm f = new MainForm();
                f.shotPath = args[1];
                f.shotTarget = args.Length > 2 ? args[2] : "325";
                f.shotBlocks = args.Length > 3 ? int.Parse(args[3], CultureInfo.InvariantCulture) : 2;
                f.shotPatterns = args.Length > 4 ? args[4] : null;
                f.shotSkin = args.Length > 5 ? args[5] : null;
                f.shotSkin = args.Length > 5 ? args[5] : null;
                Application.Run(f);
                return;
            }
            // 兜底：任何未捕获异常都写进 error.log 并弹窗提示，而不是直接闪退
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += delegate(object s2, System.Threading.ThreadExceptionEventArgs e2)
            {
                MainForm.ReportCrash(e2.Exception);
            };
            AppDomain.CurrentDomain.UnhandledException += delegate(object s2, UnhandledExceptionEventArgs e2)
            {
                MainForm.ReportCrash(e2.ExceptionObject as Exception);
            };

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }
}
