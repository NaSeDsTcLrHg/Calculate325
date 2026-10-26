/*
 * Calculate325 —— 随机窗口数学显示控件
 * ---------------------------------------------------------------------------
 * 由 WpfMath 的 OpenType MATH 排版生成位图，再按控件大小等比居中绘制。
 * 大运算符、积分上下限、分式、根号、上下标都由 WpfMath 排版。
 *
 * 缩放：滚轮以光标为中心缩放，左键拖动平移，双击复位；
 * 随机窗口上另有 ＋ / − / 复位 三个按钮（走 ZoomBy / ResetView）。
 */
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Windows.Forms;

namespace Calculate325
{
    sealed class MathBox : Control
    {
        string latex = "";
        Bitmap image;
        string error = "";
        float fontSize = 44f;
        Color ink = Color.Black;
        const float Pad = 18f;

        // ---- 视图状态 ----
        float zoom = 1f;        // 相对"适配窗口"的倍率
        float panX, panY;       // 屏幕像素偏移
        bool dragging;
        Point dragStart;
        float dragPanX, dragPanY;

        public MathBox()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            TabStop = false;
            BackColor = Color.White;
            ForeColor = Color.Black;
        }

        public string Latex { get { return latex; } }
        public string PlainText { get; set; }
        public int RenderedLineCount { get { return image == null ? 0 : Math.Max(1, WpfMathRenderer.LastLines); } }
        public string RenderError { get { return error ?? ""; } }
        public int ImageWidth { get { return image == null ? 0 : image.Width; } }
        public int ImageHeight { get { return image == null ? 0 : image.Height; } }
        public float ContentHeight { get { return image == null ? 0f : image.Height; } }

        /// <summary>当前缩放倍率（1 = 刚好适配）。</summary>
        public event EventHandler ZoomChanged;

        void RaiseZoom() { if (ZoomChanged != null) ZoomChanged(this, EventArgs.Empty); }

        public float Zoom { get { return zoom; } }

        public string ZoomText
        {
            get { return ((int)Math.Round(zoom * 100f)).ToString() + "%"; }
        }

        public float FontSize
        {
            get { return fontSize; }
            set
            {
                if (value < 12f) value = 12f;
                if (value > 96f) value = 96f;
                if (Math.Abs(fontSize - value) > 0.01f)
                {
                    fontSize = value;
                    Render();
                }
            }
        }

        public Color Ink
        {
            get { return ink; }
            set
            {
                if (ink.ToArgb() == value.ToArgb()) return;
                ink = value;
                Render();
            }
        }

        public void SetLatex(string text)
        {
            latex = text ?? "";
            ResetView(false);
            Render();
        }

        public void ResetView() { ResetView(true); }

        void ResetView(bool redraw)
        {
            zoom = 1f;
            panX = panY = 0f;
            if (redraw) { Invalidate(); RaiseZoom(); }
        }

        /// <summary>按倍率缩放（以控件中心为中心）。</summary>
        public void ZoomBy(float factor)
        {
            ZoomAt(new Point(ClientSize.Width / 2, ClientSize.Height / 2), factor);
        }

        void ZoomAt(Point anchor, float factor)
        {
            if (image == null) return;
            float oldZoom = zoom;
            float next = Math.Max(0.5f, Math.Min(8f, oldZoom * factor));
            if (Math.Abs(next - oldZoom) < 1e-4f) return;

            float fit = FitScale();
            float s0 = fit * oldZoom, s1 = fit * next;
            float w0 = image.Width * s0, h0 = image.Height * s0;
            float ox0 = (ClientSize.Width - w0) / 2f - panX;
            float oy0 = (ClientSize.Height - h0) / 2f - panY;
            float u = (anchor.X - ox0) / s0;
            float v = (anchor.Y - oy0) / s0;

            float w1 = image.Width * s1, h1 = image.Height * s1;
            float ox1 = anchor.X - u * s1;
            float oy1 = anchor.Y - v * s1;
            zoom = next;
            panX = (ClientSize.Width - w1) / 2f - ox1;
            panY = (ClientSize.Height - h1) / 2f - oy1;
            ClampPan();
            Invalidate();
            RaiseZoom();
        }

        float FitScale()
        {
            if (image == null || image.Width <= 0 || image.Height <= 0) return 1f;
            float availW = Math.Max(1f, ClientSize.Width - Pad * 2f);
            float availH = Math.Max(1f, ClientSize.Height - Pad * 2f);
            return Math.Min(1f, Math.Min(availW / image.Width, availH / image.Height));
        }

        void ClampPan()
        {
            if (image == null) { panX = panY = 0f; return; }
            float s = FitScale() * zoom;
            float w = image.Width * s, h = image.Height * s;
            float maxX = Math.Max(0f, (w - ClientSize.Width) / 2f + Pad);
            float maxY = Math.Max(0f, (h - ClientSize.Height) / 2f + Pad);
            if (panX > maxX) panX = maxX;
            if (panX < -maxX) panX = -maxX;
            if (panY > maxY) panY = maxY;
            if (panY < -maxY) panY = -maxY;
        }

        void Render()
        {
            if (image != null) { image.Dispose(); image = null; }
            error = "";
            if (string.IsNullOrEmpty(latex)) { Invalidate(); return; }
            try
            {
                WpfMathBootstrap.Ensure();
                image = WpfMathRenderer.Render(latex, fontSize, ink, Color.Transparent);
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }
            ClampPan();
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            using (SolidBrush bg = new SolidBrush(BackColor)) g.FillRectangle(bg, ClientRectangle);

            if (image != null)
            {
                float s = FitScale() * zoom;
                int w = Math.Max(1, (int)Math.Round(image.Width * s));
                int h = Math.Max(1, (int)Math.Round(image.Height * s));
                float x = (ClientSize.Width - w) / 2f - panX;
                float y = (ClientSize.Height - h) / 2f - panY;
                g.InterpolationMode = s >= 1f
                    ? System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor
                    : System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                if (s >= 1f) g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
                g.DrawImage(image, x, y, w, h);
                return;
            }

            if (!string.IsNullOrEmpty(error))
            {
                using (Font f = new Font("Microsoft YaHei UI", 10f, FontStyle.Regular))
                using (SolidBrush b = new SolidBrush(Color.FromArgb(180, 40, 40)))
                {
                    g.DrawString("LaTeX 渲染失败：" + error, f, b,
                        new RectangleF(Pad, Pad, Math.Max(20, Width - Pad * 2), Math.Max(20, Height - Pad * 2)));
                }
            }
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (image == null) return;
            float factor = e.Delta > 0 ? 1.15f : 1f / 1.15f;
            ZoomAt(e.Location, factor);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left || image == null) return;
            dragging = true;
            dragStart = e.Location;
            dragPanX = panX;
            dragPanY = panY;
            Cursor = Cursors.SizeAll;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!dragging) return;
            panX = dragPanX - (e.X - dragStart.X);
            panY = dragPanY - (e.Y - dragStart.Y);
            ClampPan();
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (!dragging) return;
            dragging = false;
            Cursor = Cursors.Default;
        }

        protected override void OnDoubleClick(EventArgs e)
        {
            base.OnDoubleClick(e);
            ResetView();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && image != null) { image.Dispose(); image = null; }
            base.Dispose(disposing);
        }
    }

    /// <summary>公式导出成 PNG（按大尺寸重渲染，不抓屏）。随机窗口与历史预览共用。</summary>
    static class MathExport
    {
        /// <summary>短式子按目标宽度放大重渲染；长式子原样（本身已经很宽）。</summary>
        public static Bitmap RenderAtSize(string latex, Color ink)
        {
            float fs = 44f;
            Bitmap first = null;
            try { first = WpfMathRenderer.Render(latex, fs, ink, Color.Transparent); }
            catch (Exception) { return null; }
            if (first == null) return null;
            const float TargetW = 3600f;
            if (first.Width >= TargetW) return first;
            float f2 = fs * Math.Max(2f, Math.Min(8f, TargetW / Math.Max(1f, first.Width)));
            try
            {
                Bitmap second = WpfMathRenderer.Render(latex, f2, ink, Color.Transparent);
                if (second != null) { first.Dispose(); return second; }
            }
            catch (Exception) { }
            return first;
        }

        /// <summary>把式子画到带内边距的底色画布上并存盘；返回是否成功。</summary>
        public static bool SavePng(string latex, Color ink, Color bg, string path, out string error)
        {
            error = "";
            try
            {
                using (Bitmap img = RenderAtSize(latex ?? "", ink))
                {
                    if (img == null) { error = "渲染失败"; return false; }
                    int pad = Math.Max(40, img.Height / 8);
                    using (Bitmap canvas = new Bitmap(img.Width + pad * 2, img.Height + pad * 2))
                    using (Graphics g = Graphics.FromImage(canvas))
                    {
                        g.Clear(bg);
                        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                        g.DrawImage(img, pad, pad);
                        canvas.Save(path, System.Drawing.Imaging.ImageFormat.Png);
                    }
                }
                return true;
            }
            catch (Exception ex) { error = ex.Message; return false; }
        }
    }

    /// <summary>窗口标题栏图标：统一用 exe 自带的图标（否则子窗口会显示 .NET 默认图标）。</summary>
    static class AppIcon
    {
        static Icon cached;

        public static Icon Get()
        {
            if (cached != null) return cached;
            try
            {
                Icon ico = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
                if (ico != null) cached = ico;
            }
            catch (Exception) { }
            return cached;
        }

        /// <summary>给窗口装上标题栏图标（失败不影响功能）。</summary>
        public static void Apply(Form f)
        {
            if (f == null) return;
            try
            {
                Icon ico = Get();
                if (ico != null) f.Icon = ico;
            }
            catch (Exception) { }
        }
    }
}
