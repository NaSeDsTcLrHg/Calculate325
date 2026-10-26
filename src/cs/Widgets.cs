/*
 * Calculate325 —— 自绘控件
 * ---------------------------------------------------------------------------
 * LotusButton     荷花形按钮（八瓣莲），用于“求解”
 * ChamferButton   削角按钮（《明日方舟》式切角卡片按钮），用于“复制算式”和示例
 * ChamferPanel    削角面板，用作输出区底板（细描边 + 左上角强调短线）
 */
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Calculate325
{
    sealed class LotusButton : Control, IButtonControl
    {
        Skin skin;
        bool hover, pressed;

        public LotusButton()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                     | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            Cursor = Cursors.Hand;
            Font = new Font("Microsoft YaHei UI", 10f);
            Text = "求解";
        }

        public DialogResult DialogResult { get; set; }
        public void NotifyDefault(bool value) { }
        public void PerformClick() { if (Enabled) OnClick(EventArgs.Empty); }

        public void ApplySkin(Skin s)
        {
            skin = s;
            ForeColor = s == null ? SystemColors.ControlText : s.LotusInk;
            Invalidate();
        }

        // 关键：自己把父级背景（窗口底图）对应区域画进来，
        // 否则透明控件会退化成一块浅色方块，看起来像“大方框”。
        protected override void OnPaintBackground(PaintEventArgs e)
        {
            Control p = Parent;
            if (p != null && p.BackgroundImage != null)
            {
                // 底图是“拉伸”铺满父级的，所以源矩形要按拉伸比例换算，
                // 否则会取到错误的区域（表现为一块浅色方块）。
                Image img = p.BackgroundImage;
                float sx = img.Width / (float)Math.Max(1, p.ClientSize.Width);
                float sy = img.Height / (float)Math.Max(1, p.ClientSize.Height);
                RectangleF src = new RectangleF(Left * sx, Top * sy, Width * sx, Height * sy);
                e.Graphics.DrawImage(img, ClientRectangle, src, GraphicsUnit.Pixel);
            }
            else if (p != null)
            {
                using (SolidBrush b = new SolidBrush(p.BackColor)) e.Graphics.FillRectangle(b, ClientRectangle);
            }
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; pressed = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) { pressed = true; Invalidate(); } base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { if (pressed) { pressed = false; Invalidate(); } base.OnMouseUp(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Skin s = skin ?? Skins.Plain;

            float scale = pressed ? 0.94f : 1f;
            float w = Width * scale, h = Height * scale;
            float cx = Width / 2f, cy = Height / 2f;

            if (hover)
            {
                for (int i = 2; i >= 0; i--)
                {
                    using (GraphicsPath glow = LotusPath(cx, cy, w / 2f + i * 2.2f, h / 2f + i * 2.2f))
                    using (SolidBrush b = new SolidBrush(Color.FromArgb(16 + i * 12, s.LotusInner)))
                    {
                        g.FillPath(b, glow);
                    }
                }
            }

            using (GraphicsPath outer = LotusPath(cx, cy, w / 2f, h / 2f))
            using (LinearGradientBrush b = new LinearGradientBrush(new RectangleF(0, 0, Width, Height),
                ChamferButton.Blend(s.LotusOuter, Color.White, 0.50f), s.LotusOuter, LinearGradientMode.Vertical))
            {
                g.FillPath(b, outer);
                // 深色描边：金底/花纹底上也能看清轮廓
                using (Pen pen = new Pen(Color.FromArgb(150, s.Ink), 2f)) g.DrawPath(pen, outer);
            }
            using (GraphicsPath inner = LotusPath(cx, cy, w / 2f * 0.60f, h / 2f * 0.60f))
            using (SolidBrush b = new SolidBrush(ChamferButton.Blend(s.LotusInner, s.Ink, 0.55f)))
            {
                g.FillPath(b, inner);
                using (Pen pen = new Pen(Color.FromArgb(120, s.LotusOuter), 1.2f)) g.DrawPath(pen, inner);
            }

            float fs = Math.Max(8.5f, Height * 0.26f);
            using (Font f = new Font(Font.FontFamily, fs, FontStyle.Bold))
            using (SolidBrush tb = new SolidBrush(s.LotusInk))
            {
                StringFormat sf = new StringFormat();
                sf.Alignment = StringAlignment.Center;
                sf.LineAlignment = StringAlignment.Center;
                g.DrawString(Text, f, tb, new RectangleF(0, 0, Width, Height), sf);
                sf.Dispose();
            }
        }

        /// <summary>八瓣莲花：每片花瓣是一枚旋转椭圆，向外张开。</summary>
        static GraphicsPath LotusPath(float cx, float cy, float rx, float ry)
        {
            GraphicsPath path = new GraphicsPath();
            path.FillMode = FillMode.Winding;
            for (int i = 0; i < 8; i++)
            {
                float ang = i * 45f;
                float k = (i % 2 == 0) ? 1f : 0.78f;
                float petalW = rx * 0.62f * k;
                float petalH = ry * 1.02f * k;
                using (Matrix m = new Matrix())
                {
                    m.RotateAt(ang, new PointF(cx, cy));
                    using (GraphicsPath petal = new GraphicsPath())
                    {
                        petal.AddEllipse(cx - petalW / 2f, cy - petalH * 0.78f, petalW, petalH);
                        petal.Transform(m);
                        path.AddPath(petal, false);
                    }
                }
            }
            return path;
        }
    }

    /// <summary>削角按钮：四角被切掉一小块，细描边，悬停填充强调色。</summary>
    sealed class ChamferButton : Control, IButtonControl
    {
        Skin skin;
        bool hover, pressed;

        public int Cut { get; set; }
        public string Glyph { get; set; }        // 可选：文字前的小图标（如 🎲 / ⚄）
        public Font GlyphFont { get; set; }
        public int GlyphGap { get; set; }
        /// <summary>强调边框：常用按钮用主题色描边，平时也一眼能看到。</summary>
        public bool Emphasize { get; set; }

        public ChamferButton()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                     | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            Cursor = Cursors.Hand;
            Font = new Font("Microsoft YaHei UI", 9f);
            Cut = 6;
            GlyphGap = 4;
            anim.Tick += delegate
            {
                phase += 0.06f;
                if (phase > 1f) phase = 0f;
                Invalidate();
            };
        }

        public DialogResult DialogResult { get; set; }
        public void NotifyDefault(bool value) { }
        public void PerformClick() { if (Enabled) OnClick(EventArgs.Empty); }

        // 悬停扫光动画：0→1 循环
        float phase;
        readonly Timer anim = new Timer { Interval = 40 };

        public void ApplySkin(Skin s) { skin = s; Invalidate(); }

        // 关键：把父级底图对应的一块画进来，切角处才不会露出白色方框
        protected override void OnPaintBackground(PaintEventArgs e)
        {
            Control p = Parent;
            if (p != null && p.BackgroundImage != null)
            {
                Image img = p.BackgroundImage;
                float sx = img.Width / (float)Math.Max(1, p.ClientSize.Width);
                float sy = img.Height / (float)Math.Max(1, p.ClientSize.Height);
                e.Graphics.DrawImage(img, ClientRectangle,
                    new RectangleF(Left * sx, Top * sy, Width * sx, Height * sy), GraphicsUnit.Pixel);
            }
            else if (p != null)
            {
                using (SolidBrush b = new SolidBrush(p.BackColor)) e.Graphics.FillRectangle(b, ClientRectangle);
            }
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            hover = true;
            phase = 0f;
            anim.Start();
            Invalidate();
            base.OnMouseEnter(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            hover = false;
            pressed = false;
            anim.Stop();
            Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnMouseDown(MouseEventArgs e) { pressed = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { pressed = false; Invalidate(); base.OnMouseUp(e); }

        protected override void Dispose(bool disposing)
        {
            if (disposing) anim.Dispose();
            base.Dispose(disposing);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Skin s = skin ?? Skins.Plain;
            Rectangle r = new Rectangle(1, 1, Width - 2, Height - 2);
            int cut = Math.Max(0, Math.Min(Cut, Math.Min(r.Width, r.Height) / 2));

            using (GraphicsPath path = ChamferPanel.ChamferPath(r, cut))
            {
                // 半透明填充：切角以外完全露出底图，切角以内也带一点底图质感（不留白）
                int alpha = hover ? (pressed ? 230 : 200) : 165;
                Color fill = hover ? (pressed ? Blend(s.PanelBack, s.Accent, 0.50f) : Blend(s.PanelBack, s.Accent, 0.24f))
                                   : s.PanelBack;
                using (SolidBrush b = new SolidBrush(Color.FromArgb(alpha, fill))) g.FillPath(b, path);

                // 悬停：沿顶边扫过的一道光带
                if (hover)
                {
                    Region old = g.Clip;
                    g.SetClip(path);
                    float bandW = Math.Max(24f, Width * 0.28f);
                    float x = -bandW + (Width + bandW * 2) * phase;
                    using (System.Drawing.Drawing2D.LinearGradientBrush lb = new System.Drawing.Drawing2D.LinearGradientBrush(
                        new RectangleF(x, 0, bandW, Height), Color.FromArgb(0, Color.White),
                        Color.FromArgb(110, Color.White), System.Drawing.Drawing2D.LinearGradientMode.Horizontal))
                    {
                        g.FillRectangle(lb, x, 0, bandW, Height);
                    }
                    g.Clip = old;
                }
                using (Pen pen = new Pen(hover || Emphasize ? s.Accent : s.Border, hover ? 2.2f : (Emphasize ? 2.0f : 1f))) g.DrawPath(pen, path);
            }

            using (SolidBrush fg = new SolidBrush(hover ? s.Accent : s.Ink))
            using (Font f = new Font(Font.FontFamily, Font.Size, hover ? FontStyle.Bold : FontStyle.Regular))
            {
                StringFormat sf = new StringFormat();
                sf.Alignment = StringAlignment.Center;
                sf.LineAlignment = StringAlignment.Center;
                if (string.IsNullOrEmpty(Glyph))
                {
                    g.DrawString(Text, f, fg, new RectangleF(0, 0, Width, Height), sf);
                }
                else
                {
                    // 文字前带一个独立字体的图标（中文界面字体没有 🎲 字形，需要单独画）
                    Font gf = GlyphFont;
                    bool ownGlyphFont = false;
                    if (gf == null)
                    {
                        gf = new Font(f.FontFamily, f.Size, FontStyle.Regular);
                        ownGlyphFont = true;
                    }
                    try
                    {
                        SizeF gs = g.MeasureString(Glyph, gf);
                        SizeF ts = g.MeasureString(Text, f);
                        float gap = Math.Max(0, GlyphGap);
                        float total = gs.Width + gap + ts.Width;
                        float x = Math.Max(1f, (Width - total) / 2f);
                        StringFormat left = new StringFormat();
                        left.Alignment = StringAlignment.Near;
                        left.LineAlignment = StringAlignment.Center;
                        g.DrawString(Glyph, gf, fg, new RectangleF(x, 0, gs.Width + 2f, Height), left);
                        g.DrawString(Text, f, fg, new RectangleF(x + gs.Width + gap, 0, ts.Width + 2f, Height), left);
                        left.Dispose();
                    }
                    finally
                    {
                        if (ownGlyphFont) gf.Dispose();
                    }
                }
                sf.Dispose();
            }
        }

        internal static Color Blend(Color a, Color b, float t)
        {
            if (t < 0) t = 0; if (t > 1) t = 1;
            return Color.FromArgb(
                (int)(a.R + (b.R - a.R) * t),
                (int)(a.G + (b.G - a.G) * t),
                (int)(a.B + (b.B - a.B) * t));
        }
    }



    /// <summary>
    /// 输出区（自绘）。不用 RichTextBox：字体、字号、行距、折行、滚动全部自己控制，
    /// 从而彻底避免字体内回退/跳字形导致的“一半大一半小”。
    /// </summary>
    sealed class OutputView : Control
    {
        sealed class Line
        {
            public string Text;
            public Font Font;
            public Color Color;
            public MathNode Math;      // 非空表示按 LaTeX 风格排版
            public float MathSize;
            public string Tail;        // 数学行后的文字（如 = 2025）
            public Color TailColor;     // 以磅为单位的基准字号
        }

        readonly List<Line> lines = new List<Line>();
        readonly System.Text.StringBuilder pending = new System.Text.StringBuilder();
        Font pendingFont;
        Color pendingColor;
        int scroll;          // 垂直滚动偏移（像素）
        Skin skin;

        public OutputView()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                     | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Color.White;
        }

        public void ApplySkin(Skin s) { skin = s; BackColor = s == null ? Color.White : s.OutputBack; Invalidate(); }

        public void Clear()
        {
            lines.Clear();
            pending.Length = 0;
            scroll = 0;
            Invalidate();
        }

        /// <summary>追加一行 LaTeX 风格数学排版。</summary>
        public void AppendMath(string expr, float sizePt, Color color, string tail, Color tailColor)
        {
            if (pending.Length > 0) Flush();
            MathNode node = MathParser.Build(expr);
            lines.Add(new Line
            {
                Text = expr,
                Font = Font,
                Color = color,
                Math = node,
                MathSize = sizePt,
                Tail = tail,
                TailColor = tailColor
            });
            Invalidate();
        }

        public void Append(string text, Font font, Color color)
        {
            if (string.IsNullOrEmpty(text)) return;
            int i = 0;
            while (i < text.Length)
            {
                int nl = text.IndexOf('\n', i);
                string part = nl < 0 ? text.Substring(i) : text.Substring(i, nl - i);
                if (part.Length > 0)
                {
                    pending.Append(part);
                    pendingFont = font;
                    pendingColor = color;
                }
                if (nl < 0) break;
                Flush();
                i = nl + 1;
            }
            Invalidate();
        }

        public void ScrollToTop() { scroll = 0; Invalidate(); }

        public override string Text
        {
            get { return GetText(); }
            set { Clear(); if (!string.IsNullOrEmpty(value)) Append(value, Font, ForeColor); }
        }

        public string GetText()
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            for (int i = 0; i < lines.Count; i++) sb.AppendLine(lines[i].Text);
            sb.Append(pending.ToString());
            return sb.ToString();
        }

        void Flush()
        {
            lines.Add(new Line { Text = pending.ToString(), Font = pendingFont ?? Font, Color = pendingColor });
            pending.Length = 0;
        }

        const TextFormatFlags Flags = TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix
                                    | TextFormatFlags.TextBoxControl | TextFormatFlags.NoClipping;

        int InnerWidth { get { return Math.Max(40, ClientSize.Width - Pad * 2 - BarW); } }
        int Pad { get { return 4; } }
        int BarW { get { return 6; } }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Skin s = skin ?? Skins.Plain;
            using (SolidBrush back = new SolidBrush(s.OutputBack)) g.FillRectangle(back, ClientRectangle);

            int y = Pad - scroll;
            int total = 0;
            for (int i = 0; i < lines.Count; i++)
            {
                Line ln = lines[i];
                if (ln.Math != null)
                {
                    float px = ln.MathSize * g.DpiY / 72f;
                    using (Font mf = new Font("Cambria Math", ln.MathSize, FontStyle.Regular))
                    {
                        ln.Math.Measure(g, px, mf);
                        int mh = (int)Math.Ceiling(ln.Math.H + Pad * 2);
                        if (y + mh > 0 && y < ClientSize.Height)
                        {
                            ln.Math.Draw(g, Pad, y + Pad, px, mf, ln.Color, 0);
                            if (!string.IsNullOrEmpty(ln.Tail))
                            {
                                float tx = Pad + ln.Math.W + px * 0.15f;
                                float ty = y + Pad + ln.Math.Base - px * 0.70f;
                                TextRenderer.DrawText(g, ln.Tail, ln.Font, new Point((int)tx, (int)ty), ln.TailColor);
                            }
                        }
                        y += mh;
                        total = y + scroll;
                        continue;
                    }
                }
                Size sz = TextRenderer.MeasureText(g, ln.Text, ln.Font,
                    new Size(InnerWidth, int.MaxValue), Flags);
                int h = Math.Max(ln.Font.Height, sz.Height);
                if (y + h > 0 && y < ClientSize.Height)
                {
                    Rectangle r = new Rectangle(Pad, y, InnerWidth, h);
                    TextRenderer.DrawText(g, ln.Text, ln.Font, r, ln.Color, Flags);
                }
                y += h;
                total = y + scroll;
            }
            // 正在输入的最后一行
            if (pending.Length > 0)
            {
                Font f = pendingFont ?? Font;
                Size sz = TextRenderer.MeasureText(g, pending.ToString(), f, new Size(InnerWidth, int.MaxValue), Flags);
                int h = Math.Max(f.Height, sz.Height);
                TextRenderer.DrawText(g, pending.ToString(), f,
                    new Rectangle(Pad, y, InnerWidth, h), pendingColor, Flags);
                total += h;
            }

            // 自绘细滚动条
            int view = ClientSize.Height;
            if (total > view)
            {
                int barH = Math.Max(24, view * view / total);
                int maxScroll = total - view + Pad;
                int barY = maxScroll <= 0 ? 0 : (int)((long)scroll * (view - barH) / maxScroll);
                using (SolidBrush b = new SolidBrush(Color.FromArgb(120, s.Accent)))
                {
                    g.FillRectangle(b, ClientSize.Width - BarW - 1, barY, BarW, barH);
                }
            }
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            int delta = -e.Delta / 120 * 48;
            scrolldo(delta);
            base.OnMouseWheel(e);
        }

        void scrolldo(int delta)
        {
            scroll += delta;
            if (scroll < 0) scroll = 0;
            Invalidate();
        }
    }

    /// <summary>标题栏上的窗口按钮（最小化/最大化/关闭）：矢量绘制，带悬停与按下态。</summary>
    sealed class TitleButton : Control
    {
        public enum Kind { Minimize, Maximize, Close }

        Skin skin;
        bool hover, pressed;

        public Kind Role { get; set; }
        public bool IsMaximized { get; set; }

        public TitleButton()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                     | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            Cursor = Cursors.Hand;
        }

        public DialogResult DialogResult { get; set; }
        public void NotifyDefault(bool value) { }
        public void PerformClick() { if (Enabled) OnClick(EventArgs.Empty); }

        public void ApplySkin(Skin s) { skin = s; Invalidate(); }

        // 与标题栏同款的竖向渐变，保证按钮区不会有突兀的白块
        protected override void OnPaintBackground(PaintEventArgs e)
        {
            Skin s = skin ?? Skins.Plain;
            Rectangle r = ClientRectangle;
            Color top = ChamferButton.Blend(s.TitleBack, s.Accent, 0.18f);
            Color bottom = ChamferButton.Blend(s.TitleBack, s.FormBack, 0.35f);
            using (LinearGradientBrush b = new LinearGradientBrush(r, top, bottom, LinearGradientMode.Vertical))
            {
                e.Graphics.FillRectangle(b, r);
            }
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; pressed = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { pressed = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { pressed = false; Invalidate(); base.OnMouseUp(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Skin s = skin ?? Skins.Plain;
            bool isClose = Role == Kind.Close;

            // 悬停/按下底色（关闭键用朱红）
            if (hover || pressed)
            {
                Color fill = isClose
                    ? (pressed ? ChamferButton.Blend(s.Warn, Color.Black, 0.22f) : s.Warn)
                    : (pressed ? ChamferButton.Blend(s.TitleBack, s.Accent, 0.55f)
                               : ChamferButton.Blend(s.TitleBack, s.Accent, 0.30f));
                using (SolidBrush b = new SolidBrush(Color.FromArgb(isClose ? 235 : 170, fill)))
                {
                    g.FillRectangle(b, ClientRectangle);
                }
            }

            // 只用实心矩形画图标：不依赖 GraphicsPath/Pen，绝不会出现多余的框线
            Color ink = isClose && (hover || pressed) ? Color.White
                      : ChamferButton.Blend(s.TitleBack, s.TitleInk, 0.88f);
            using (SolidBrush b = new SolidBrush(ink))
            {
                int cx = Width / 2, cy = Height / 2;
                int d = Math.Max(5, Math.Min(Width, Height) / 5);
                int th = Math.Max(2, Math.Min(Width, Height) / 26);   // 线宽
                if (Role == Kind.Minimize)
                {
                    g.FillRectangle(b, cx - d, cy - th / 2, d * 2, th);
                }
                else if (Role == Kind.Maximize)
                {
                    int side = d * 2;
                    int x0 = cx - d, y0 = cy - d;
                    g.FillRectangle(b, x0, y0, side, th);                 // 上
                    g.FillRectangle(b, x0, y0 + side - th, side, th);     // 下
                    g.FillRectangle(b, x0, y0, th, side);                 // 左
                    g.FillRectangle(b, x0 + side - th, y0, th, side);     // 右
                }
                else
                {
                    // 两条 45° 斜杠：用小方块的阶梯逼近对角线，避免任何描边
                    int n = d * 2;
                    for (int k = 0; k < n; k++)
                    {
                        g.FillRectangle(b, cx - d + k, cy - d + k, th, th);
                        g.FillRectangle(b, cx + d - k - th, cy - d + k, th, th);
                    }
                }
            }
        }
    }

    sealed class ChamferPanel : Panel
    {
        public Skin Skin { get; set; }
        public int Corner { get; set; }

        public ChamferPanel()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                     | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
        }

        public static GraphicsPath ChamferPath(Rectangle r, int cut)
        {
            GraphicsPath p = new GraphicsPath();
            if (cut <= 0)
            {
                p.AddRectangle(r);
                return p;
            }
            int c = Math.Min(cut, Math.Min(r.Width, r.Height) / 2);
            p.AddPolygon(new PointF[]
            {
                new PointF(r.Left + c, r.Top),
                new PointF(r.Right - c, r.Top),
                new PointF(r.Right, r.Top + c),
                new PointF(r.Right, r.Bottom - c),
                new PointF(r.Right - c, r.Bottom),
                new PointF(r.Left + c, r.Bottom),
                new PointF(r.Left, r.Bottom - c),
                new PointF(r.Left, r.Top + c)
            });
            p.CloseFigure();
            return p;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Skin s = Skin ?? Skins.Plain;
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
            int cut = Math.Min(Corner, Math.Min(r.Width, r.Height) / 2);
            using (GraphicsPath path = ChamferPath(r, cut))
            {
                using (SolidBrush b = new SolidBrush(Color.FromArgb(246, s.OutputBack))) g.FillPath(b, path);
                using (Pen pen = new Pen(Color.FromArgb(170, s.Accent), 1.2f)) g.DrawPath(pen, path);
            }
            // 左上角一小段强调色短线（编号化装饰）
            using (Pen pen = new Pen(s.Accent2, 2.4f))
            {
                float len = Math.Min(60f, Width * 0.10f);
                g.DrawLine(pen, cut + 3f, 1.4f, cut + 3f + len, 1.4f);
            }
        }
    }
}
