/*
 * Calculate325 —— 随机模式窗口
 * ---------------------------------------------------------------------------
 * 4 个等级、种子、生成、复制 LaTeX、保存 PNG。
 * 结果区用 MathBox（WpfMath 完整 LaTeX 排版），窗口固定 1920×1080 并跟随皮肤。
 */
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace Calculate325
{
    sealed class RandomForm : Form
    {
        readonly Rat target;
        readonly string targetText;
        Skin skin;
        readonly Random rnd;

        RandomExprResult current;
        int generated;
        bool uiBuilt;
        bool seedChanged;
        bool resizing;
        bool generatedNumericOk;
        string generatedNumericError = "";

        Label lblHeader;
        Label lblTargetCaption;
        Label lblTargetValue;
        Label lblLevel;
        Label lblSeed;
        Label lblMeta;
        ComboBox cboLevel;
        TextBox txtSeed;
        ChamferButton btnRandom;
        ChamferButton btnGen;
        ChamferButton btnCopy;
        ChamferButton btnSave;
        ChamferButton btnClose;
        ChamferButton btnZoomIn;
        ChamferButton btnZoomOut;
        ChamferButton btnZoomReset;
        ChamferButton btnHistory;
        ChamferButton btnFavorites;
        ChamferButton btnFavCurrent;
        readonly List<HistoryItem> favorites = new List<HistoryItem>();
        string favoritesPath;
        readonly List<HistoryItem> history = new List<HistoryItem>();
        int historySeq;
        int historyLoaded;
        string historyPath;
        Label lblZoom;
        ChamferPanel resultCard;
        MathBox mathBox;
        float scale = 1f;

        public bool LastSelfTestFailed { get; private set; }

        public RandomForm(Rat target, string targetText, Skin skin)
            : this(target, targetText, skin, new Random())
        {
        }

        internal RandomForm(Rat target, string targetText, Skin skin, Random rnd)
        {
            this.skin = skin ?? Skins.Plain;
            this.target = target;
            this.targetText = string.IsNullOrEmpty(targetText) ? RandomExpr.FormatRat(target) : targetText;
            this.rnd = rnd ?? new Random();

            Text = "随机模式";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = true;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            AutoScaleMode = AutoScaleMode.None;
            BackColor = this.skin.FormBack;
            ForeColor = this.skin.Ink;
            Font = new Font("Microsoft YaHei UI", 10.5f, FontStyle.Regular);
            KeyPreview = true;
            AppIcon.Apply(this);
            ThemedCaption.Hook(this, skin);

            try
            {
                using (Graphics g = CreateGraphics()) scale = g.DpiX / 96f;
            }
            catch (Exception) { }
            if (scale < 0.75f) scale = 0.75f;
            if (scale > 3f) scale = 3f;

            BuildUi();
            Size = new Size(1920, 1080);
            MinimumSize = Size;
            MaximumSize = Size;
            LayoutControls();
            historyPath = HistoryFile.PathFor(Application.StartupPath);
            favoritesPath = HistoryFile.PathForFavorites(Application.StartupPath);
            LoadHistory();
            LoadFavorites();
            NewSeed();

            Shown += delegate { CenterOnOwner(); GenerateOnce(false); };
            KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.Control && e.KeyCode == Keys.Enter) { GenerateOnce(false); e.Handled = true; }
                else if (e.Control && e.KeyCode == Keys.R) { NewSeed(); GenerateOnce(false); e.Handled = true; }
                else if (e.KeyCode == Keys.Escape) { Close(); }
            };
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            CenterOnOwner();
        }

        void CenterOnOwner()
        {
            // 不跟随主窗口位置：主窗口本身在右下角时，随机窗口也会被带过去。
            // 统一居中到主窗口所在显示器的可用工作区。
            Rectangle wa = Owner != null
                ? Screen.FromControl(Owner).WorkingArea
                : Screen.FromControl(this).WorkingArea;
            int x = wa.Left + (wa.Width - Width) / 2;
            int y = wa.Top + (wa.Height - Height) / 2;
            // 窗口比工作区还大时也保持等量居中，不要贴右下角。
            if (Width <= wa.Width) x = Math.Max(wa.Left, Math.Min(x, wa.Right - Width));
            if (Height <= wa.Height) y = Math.Max(wa.Top, Math.Min(y, wa.Bottom - Height));
            Location = new Point(x, y);
        }

        int R(float v) { return (int)Math.Round(v * scale); }

        float RF(float v) { return v * scale; }

        Font UIFont(float pt, bool bold)
        {
            return new Font("Microsoft YaHei UI", pt, bold ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Point);
        }

        ChamferButton MakeButton(string text)
        {
            ChamferButton b = new ChamferButton();
            b.Text = text;
            b.ApplySkin(skin);
            b.Cut = R(6);
            b.Font = UIFont(10.5f, false);
            b.TabStop = true;
            return b;
        }

        Label MakeLabel(string text, float pt, bool bold)
        {
            Label l = new Label();
            l.Text = text;
            l.AutoSize = false;
            l.BackColor = Color.Transparent;
            l.ForeColor = skin.Ink;
            l.Font = UIFont(pt, bold);
            l.TextAlign = ContentAlignment.MiddleLeft;
            return l;
        }

        void BuildUi()
        {
            lblHeader = MakeLabel("随机模式 · 值恰好等于 " + targetText, 17f, true);
            lblHeader.ForeColor = skin.Accent;
            Controls.Add(lblHeader);

            lblTargetCaption = MakeLabel("目标", 11f, false);
            lblTargetCaption.ForeColor = skin.Muted;
            Controls.Add(lblTargetCaption);
            lblTargetValue = MakeLabel(targetText, 16f, true);
            Controls.Add(lblTargetValue);

            lblLevel = MakeLabel("等级", 11f, false);
            lblLevel.ForeColor = skin.Muted;
            Controls.Add(lblLevel);
            cboLevel = new ComboBox();
            cboLevel.DropDownStyle = ComboBoxStyle.DropDownList;
            cboLevel.FlatStyle = FlatStyle.Flat;
            cboLevel.BackColor = skin.FieldBack;
            cboLevel.ForeColor = skin.FieldFore;
            cboLevel.Font = UIFont(10.5f, false);
            // 难度菜单只显示 L1..L5
            for (int lv = 1; lv <= 5; lv++) cboLevel.Items.Add("L" + lv.ToString(CultureInfo.InvariantCulture));
            cboLevel.SelectedIndex = 3;
            cboLevel.SelectedIndexChanged += delegate { if (uiBuilt) NewSeed(); };
            Controls.Add(cboLevel);

            lblSeed = MakeLabel("种子", 11f, false);
            lblSeed.ForeColor = skin.Muted;
            Controls.Add(lblSeed);
            txtSeed = new TextBox();
            txtSeed.BackColor = skin.FieldBack;
            txtSeed.ForeColor = skin.FieldFore;
            txtSeed.BorderStyle = BorderStyle.FixedSingle;
            txtSeed.Font = new Font("Consolas", 12f, FontStyle.Regular, GraphicsUnit.Point);
            txtSeed.Multiline = true;          // 每 32 位换行，L5 的 64 位是两行
            txtSeed.WordWrap = false;
            txtSeed.ScrollBars = ScrollBars.Vertical;
            txtSeed.AcceptsReturn = false;
            txtSeed.TextChanged += delegate { seedChanged = true; };
            txtSeed.KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Enter) { GenerateOnce(false); e.SuppressKeyPress = true; e.Handled = true; }
            };
            Controls.Add(txtSeed);

            // “随机生成”：换一个随机种子并立刻生成
            btnRandom = MakeButton("随机生成");
            btnRandom.Emphasize = true;
            btnRandom.Click += delegate { NewSeed(); GenerateOnce(false); };
            Controls.Add(btnRandom);
            // “自定义”：弹出自定义种子窗口（数字模式 / ZC 二进制模式），确定后按填回的种子生成
            btnGen = MakeButton("自定义");
            btnGen.Click += delegate { ShowSeedDialog(); };
            Controls.Add(btnGen);

            resultCard = new ChamferPanel();
            resultCard.Skin = skin;
            resultCard.Corner = R(8);
            resultCard.BackColor = Color.Transparent;
            Controls.Add(resultCard);

            mathBox = new MathBox();
            mathBox.BackColor = skin.OutputBack;
            mathBox.Ink = skin.OutputInk;
            mathBox.SetLatex("\\text{点击右侧「生成」开始。}");
            resultCard.Controls.Add(mathBox);

            lblMeta = MakeLabel("", 10.5f, false);
            lblMeta.ForeColor = skin.Good;
            Controls.Add(lblMeta);

            btnCopy = MakeButton("复制");
            btnCopy.Emphasize = true;
            btnCopy.Click += delegate { CopyLatex(); };
            Controls.Add(btnCopy);
            btnFavCurrent = MakeButton("收藏");
            btnFavCurrent.Emphasize = true;
            btnFavCurrent.Click += delegate { FavoriteCurrent(); };
            Controls.Add(btnFavCurrent);
            btnHistory = MakeButton("历史");
            btnHistory.Click += delegate { ShowHistory(); };
            Controls.Add(btnHistory);
            btnFavorites = MakeButton("收藏夹");
            btnFavorites.Click += delegate { ShowFavorites(); };
            Controls.Add(btnFavorites);
            btnSave = MakeButton("存图");
            btnSave.Emphasize = true;
            btnSave.Click += delegate { SavePng(); };
            Controls.Add(btnSave);
            btnClose = MakeButton("关闭");
            btnClose.Click += delegate { Close(); };
            Controls.Add(btnClose);

            // 缩放：按钮 + 滚轮（滚轮在 MathBox 里处理），拖动可平移，双击复位
            btnZoomOut = MakeButton("缩小");
            btnZoomOut.Click += delegate { mathBox.ZoomBy(0.8f); };
            Controls.Add(btnZoomOut);
            btnZoomIn = MakeButton("放大");
            btnZoomIn.Click += delegate { mathBox.ZoomBy(1.25f); };
            Controls.Add(btnZoomIn);
            btnZoomReset = MakeButton("复位");
            btnZoomReset.Click += delegate { mathBox.ResetView(); };
            Controls.Add(btnZoomReset);
            lblZoom = MakeLabel("100%", 10.5f, false);
            lblZoom.ForeColor = skin.Muted;
            lblZoom.TextAlign = ContentAlignment.MiddleCenter;
            Controls.Add(lblZoom);
            mathBox.ZoomChanged += delegate { lblZoom.Text = mathBox.ZoomText; };

            uiBuilt = true;
            Resize += delegate
            {
                if (!resizing) LayoutControls();
            };
        }

        void LayoutControls()
        {
            if (!uiBuilt) return;

            int pad = R(24);
            int rowY = R(92);
            int h = R(38);
            lblHeader.Bounds = new Rectangle(pad, R(18), ClientSize.Width - pad * 2, R(42));

            lblTargetCaption.Bounds = new Rectangle(pad, rowY, R(52), h);
            lblTargetValue.Bounds = new Rectangle(pad + R(56), rowY - R(4), R(150), R(46));

            int lx = pad + R(270);
            lblLevel.Bounds = new Rectangle(lx, rowY, R(46), h);
            cboLevel.Bounds = new Rectangle(lx + R(50), rowY, R(210), h);

            int sx = lx + R(282);
            lblSeed.Bounds = new Rectangle(sx, rowY, R(46), h);
            int seedX = sx + R(50);

            btnGen.Bounds = new Rectangle(ClientSize.Width - pad - R(110), rowY, R(110), h);
            btnRandom.Bounds = new Rectangle(btnGen.Left - R(18) - R(100), rowY, R(100), h);
            int seedW = Math.Max(R(180), btnRandom.Left - R(18) - seedX);
            txtSeed.Bounds = new Rectangle(seedX, rowY, seedW, R(52));    // 最多两行，其余靠滚动条

            int cardTop = rowY + R(58) + R(18);
            int bottomArea = R(142);
            resultCard.Bounds = new Rectangle(pad, cardTop, ClientSize.Width - pad * 2,
                Math.Max(R(240), ClientSize.Height - cardTop - bottomArea));
            int m = R(20);
            mathBox.Bounds = new Rectangle(m, m, Math.Max(R(200), resultCard.Width - m * 2),
                Math.Max(R(160), resultCard.Height - m * 2));

            lblMeta.Bounds = new Rectangle(pad, resultCard.Bottom + R(12),
                ClientSize.Width - pad * 2 - R(470), R(30));

            // 底部按钮统一收窄：一行排得下、不占横向空间
            int by = ClientSize.Height - pad - R(38);
            int gap = R(8);
            int wClose = R(76), wSave = R(76), wFavs = R(88), wHist = R(76), wFav = R(76), wCopy = R(76);
            btnClose.Bounds = new Rectangle(ClientSize.Width - pad - wClose, by, wClose, R(38));
            btnSave.Bounds = new Rectangle(btnClose.Left - gap - wSave, by, wSave, R(38));
            btnFavorites.Bounds = new Rectangle(btnSave.Left - gap - wFavs, by, wFavs, R(38));
            btnHistory.Bounds = new Rectangle(btnFavorites.Left - gap - wHist, by, wHist, R(38));
            btnFavCurrent.Bounds = new Rectangle(btnHistory.Left - gap - wFav, by, wFav, R(38));
            btnCopy.Bounds = new Rectangle(btnFavCurrent.Left - gap - wCopy, by, wCopy, R(38));

            // 缩放控件放左下角（同样收窄）
            int zx = pad;
            int wz = R(72);
            btnZoomOut.Bounds = new Rectangle(zx, by, wz, R(38));
            lblZoom.Bounds = new Rectangle(zx + wz + gap, by, R(62), R(38));
            btnZoomIn.Bounds = new Rectangle(lblZoom.Right + gap, by, wz, R(38));
            btnZoomReset.Bounds = new Rectangle(btnZoomIn.Right + gap, by, wz, R(38));

        }
        int SelectedLevel { get { return Math.Max(1, Math.Min(5, cboLevel.SelectedIndex + 1)); } }


        void ShowSeedDialog()
        {
            using (SeedDialog dlg = new SeedDialog(skin, SelectedLevel, CurrentSeedDecimal() ?? ""))
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                if (string.IsNullOrEmpty(dlg.DecimalSeed))
                {
                    // 只切换了模式：现有种子按新编码重显；本来没有种子就按新模式随机一个
                    string cur = CurrentSeedDecimal();
                    if (string.IsNullOrEmpty(cur)) NewSeed();
                    else ShowSeedText(cur);
                    GenerateOnce(false);
                    return;
                }
                ShowSeedText(dlg.DecimalSeed);
                GenerateOnce(false);
            }
        }

        /// <summary>把十进制种子按当前模式（数字 / Zc）格式化后显示到输入框。</summary>
        void ShowSeedText(string dec)
        {
            bool zc = SeedDialog.Mode == 1;
            string body = zc ? SeedDialog.DecimalToZc(dec ?? "") : (dec ?? "");
            txtSeed.Text = RandomExpr.FormatSeedWrapped(body, 32, zc);
        }

        /// <summary>从种子框读出十进制种子；含 Zc 字符时先解码，非法返回 null。</summary>
        string CurrentSeedDecimal()
        {
            string raw = txtSeed.Text ?? "";
            bool hasZc = false;
            for (int i = 0; i < raw.Length; i++)
            {
                char c = raw[i];
                if (c == 'Z' || c == 'C' || c == 'z' || c == 'c') { hasZc = true; break; }
            }
            if (hasZc) return SeedDialog.ConvertText(raw, true);
            StringBuilder sb = new StringBuilder(raw.Length);
            for (int i = 0; i < raw.Length; i++)
                if (raw[i] >= '0' && raw[i] <= '9') sb.Append(raw[i]);
            return sb.Length == 0 ? null : sb.ToString();
        }

        /// <summary>随机一个"位数正好等于该等级要求"的十进制种子（首位非 0），供 Zc 模式用。</summary>
        string MakeFixedDecimalSeed(int level)
        {
            int need = RandomExpr.SeedLength(level);
            StringBuilder sb = new StringBuilder(need);
            sb.Append((char)('0' + rnd.Next(1, 10)));
            for (int i = 1; i < need; i++) sb.Append((char)('0' + rnd.Next(0, 10)));
            return sb.ToString();
        }

        void NewSeed()
        {
            int level = SelectedLevel;
            string dec;
            if (SeedDialog.Mode == 1)
            {
                // Zc 模式：随机十进制（位数 = 要求位数）→ 转成 Zc 显示
                dec = MakeFixedDecimalSeed(level);
            }
            else
            {
                dec = level == 5 ? RandomExpr.MakeL5Seed(rnd) : RandomExpr.MakeSeed(level, rnd);
            }
            ShowSeedText(dec);
            seedChanged = true;
        }

        public void SetLevelForTest(int level)
        {
            if (level < 1) level = 1;
            if (level > 5) level = 5;
            cboLevel.SelectedIndex = level - 1;
        }

        public void SetSeedForTest(string seed)
        {
            txtSeed.Text = seed ?? "";
            seedChanged = false;
        }

        void GenerateOnce(bool newSeed)
        {
            if (newSeed) NewSeed();
            string rawSeed = CurrentSeedDecimal();
            if (rawSeed == null)
            {
                lblMeta.Text = "种子格式不对：Zc 模式要求大小写交替的 Z/C";
                lblMeta.ForeColor = skin.Warn;
                return;
            }
            int level = SelectedLevel;
            string seed = level == 5
                ? RandomExpr.NormalizeL5Seed(rawSeed)
                : RandomExpr.NormalizeSeed(rawSeed, level);
            ShowSeedText(seed);

            try
            {
                // 生成 → 独立后置校验 → 不过就换种子重生成（FormulaAudit 只读最终 LaTeX）
                current = RandomExpr.BuildLevelGated(level, target, rnd, seed, true);
                generated++;
                if (current != null && current.RequestedSeed != seed && !string.IsNullOrEmpty(current.Seed))
                {
                    txtSeed.Text = RandomExpr.FormatSeed(current.Seed);
                }

                Rat value;
                string err;
                bool ok = RandomExpr.Verify(current, target, out value, out err);
                double nv;
                string nerr;
                generatedNumericOk = RandomExpr.NumericCheck(current, out nv, out nerr);
                generatedNumericError = ok ? nerr : err;

                mathBox.SetLatex(current.Latex);
                if (!ok)
                {
                    lblMeta.Text = "校验失败：" + err;
                    lblMeta.ForeColor = skin.Warn;
                }
                else
                {
                    // 状态行只保留三项：第几次 / 形式 / 复杂度
                    lblMeta.Text = "第 " + generated.ToString(CultureInfo.InvariantCulture) + " 次"
                        + " · 形式 " + LevelShort(level) + " "
                        + current.Layers.ToString(CultureInfo.InvariantCulture) + "层/"
                        + current.Dims.ToString(CultureInfo.InvariantCulture) + "重 类型 " + current.TypeCodes
                        + " · 复杂度 " + current.Complexity.ToString(CultureInfo.InvariantCulture);
                    lblMeta.ForeColor = current.AuditOk ? skin.Good : skin.Warn;
                }
                RecordHistory(level, seed);
            }
            catch (Exception ex)
            {
                current = null;
                mathBox.SetLatex("\\text{生成失败}");
                lblMeta.Text = "生成失败：" + ex.Message;
                lblMeta.ForeColor = skin.Warn;
            }
        }

        static string LevelShort(int level)
        {
            return "L" + level.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>把刚生成的一条记进历史（失败不影响生成）。</summary>
        void RecordHistory(int level, string seed)
        {
            if (current == null || string.IsNullOrEmpty(current.Latex)) return;
            try
            {
                historySeq++;
                HistoryItem it = new HistoryItem();
                it.Seq = historySeq;
                it.Time = DateTime.Now;
                it.Level = level;
                it.Seed = seed ?? "";
                it.Latex = current.Latex;
                it.Expression = current.Expression;
                it.Kind = current.Kind;
                it.Codes = current.TypeCodes;
                it.Layers = current.Layers;
                it.Dims = current.Dims;
                it.Complexity = current.Complexity;
                it.Meta = lblMeta.Text;
                HistoryStore.Add(history, it);
                HistoryFile.Save(historyPath, history);      // 立刻落盘，下次启动还在
            }
            catch (Exception) { }
        }

        /// <summary>启动时把上次的历史读回来（新的在最前）。</summary>
        void LoadHistory()
        {
            try
            {
                List<HistoryItem> saved = HistoryFile.Load(historyPath);
                history.Clear();
                for (int i = 0; i < saved.Count; i++) history.Add(saved[i]);
                for (int i = 0; i < history.Count; i++)
                    if (history[i].Seq > historySeq) historySeq = history[i].Seq;
                historyLoaded = history.Count;
            }
            catch (Exception) { }
        }

        /// <summary>启动时把收藏读回来。</summary>
        void LoadFavorites()
        {
            try
            {
                List<HistoryItem> saved = HistoryFile.Load(favoritesPath);
                favorites.Clear();
                for (int i = 0; i < saved.Count; i++) favorites.Add(saved[i]);
            }
            catch (Exception) { }
        }

        void ShowHistory()
        {
            using (HistoryForm dlg = new HistoryForm(skin, history, targetText, delegate
            {
                // 窗口里删/清空/收藏后立刻写回
                HistoryFile.Save(historyPath, history);
                HistoryFile.Save(favoritesPath, favorites);
            }, false, favorites))
            {
                dlg.ShowDialog(this);
            }
        }

        /// <summary>把当前这次生成的结果直接加入收藏（不去历史里点）。</summary>
        void FavoriteCurrent()
        {
            if (current == null || string.IsNullOrEmpty(current.Latex))
            {
                lblMeta.Text = "还没有生成结果可收藏";
                lblMeta.ForeColor = skin.Warn;
                return;
            }
            try
            {
                HistoryItem it = new HistoryItem();
                it.Seq = historySeq;
                it.Time = DateTime.Now;
                it.Level = SelectedLevel;
                it.Seed = CurrentSeedDecimal() ?? "";
                it.Latex = current.Latex;
                it.Expression = current.Expression;
                it.Kind = current.Kind;
                it.Codes = current.TypeCodes;
                it.Layers = current.Layers;
                it.Dims = current.Dims;
                it.Complexity = current.Complexity;
                it.Meta = lblMeta.Text;
                int added = HistoryStore.Favorite(favorites, new List<HistoryItem> { it });
                HistoryFile.Save(favoritesPath, favorites);
                lblMeta.Text = added > 0
                    ? "已收藏第 " + historySeq + " 条（共 " + favorites.Count + " 条收藏）"
                    : "这条已经收藏过了（共 " + favorites.Count + " 条收藏）";
                lblMeta.ForeColor = skin.Good;
            }
            catch (Exception) { }
        }

        void ShowFavorites()
        {
            using (HistoryForm dlg = new HistoryForm(skin, favorites, targetText, delegate
            {
                HistoryFile.Save(favoritesPath, favorites);
            }, true, null))
            {
                dlg.ShowDialog(this);
            }
        }

        void CopyLatex()
        {
            if (current == null) return;
            try { Clipboard.SetText(current.Latex ?? ""); }
            catch (Exception) { }
        }

        void SavePng()
        {
            if (current == null) return;
            using (SaveFileDialog dlg = new SaveFileDialog())
            {
                dlg.Filter = "PNG 图片|*.png";
                dlg.FileName = "random-325.png";
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    // 按"最大尺寸"重新渲染，而不是抓屏幕上被缩小后的位图
                    using (Bitmap img = RenderExport(current.Latex))
                    {
                        if (img == null) return;
                        int pad = Math.Max(40, img.Height / 8);
                        // 用控件自己的底色，而不是写死白底：浅色墨 + 深色底的主题（辞岁行）导出才不会变成白底白字
                        Color exportBg = mathBox.BackColor;
                        using (Bitmap canvas = new Bitmap(img.Width + pad * 2, img.Height + pad * 2))
                        using (Graphics g = Graphics.FromImage(canvas))
                        {
                            g.Clear(exportBg);
                            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                            g.DrawImage(img, pad, pad);
                            canvas.Save(dlg.FileName, ImageFormat.Png);
                        }
                    }
                }
                catch (Exception) { }
            }
        }

        /// <summary>导出用的大尺寸渲染：短式子按目标宽度放大重渲染，长式子原样（已经很宽）。</summary>
        Bitmap RenderExport(string latex)
        {
            return MathExport.RenderAtSize(latex, mathBox.Ink);
        }

        public void SaveShot(string path)
        {
            using (Bitmap bmp = new Bitmap(Width, Height))
            {
                DrawToBitmap(bmp, new Rectangle(0, 0, Width, Height));
                bmp.Save(path, ImageFormat.Png);
            }
        }

        public string RunGenerationSelfTest(int count)
        {
            if (count < 1) count = 1;
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("LEVEL_ITEMS=" + cboLevel.Items.Count.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("SEED_LENGTHS=" + RandomExpr.SeedLength(1) + "," + RandomExpr.SeedLength(2) + ","
                + RandomExpr.SeedLength(3) + "," + RandomExpr.SeedLength(4));
            sb.AppendLine("MAX_LAYERS=0,1,4,8");
            sb.AppendLine("RESULT_BOUNDS=" + mathBox.Left + "," + mathBox.Top + "," + mathBox.Width + "," + mathBox.Height);
            sb.AppendLine("GENERATE_TEXT=" + btnGen.Text);
            sb.AppendLine("COPY_TEXT=" + btnCopy.Text);
            sb.AppendLine("SAVE_TEXT=" + btnSave.Text);
            sb.AppendLine("WINDOW_SIZE=" + Width + "x" + Height);
            sb.AppendLine("FIXED_SIZE=" + (MinimumSize == MaximumSize ? "1" : "0"));
            sb.AppendLine("HISTORY_TEXT=" + btnHistory.Text);
            sb.AppendLine("FAVORITES_TEXT=" + btnFavorites.Text);
            sb.AppendLine("FAVCUR_TEXT=" + btnFavCurrent.Text);
            sb.AppendLine("FAVCUR_SIZE=" + btnFavCurrent.Width + "x" + btnFavCurrent.Height);
            sb.AppendLine("EMPHASIS=" + (btnRandom.Emphasize ? "1" : "0") + (btnCopy.Emphasize ? "1" : "0")
                + (btnFavCurrent.Emphasize ? "1" : "0") + (btnSave.Emphasize ? "1" : "0"));
            sb.AppendLine("BTN_W_COPY=" + btnCopy.Width);
            sb.AppendLine("BTN_W_SAVE=" + btnSave.Width);
            sb.AppendLine("FAVORITES_LOADED=" + favorites.Count.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("FORM_ICON=" + (Icon != null && Icon.Width > 0 ? "1" : "0"));
            sb.AppendLine("SEEDMODE=" + (SeedDialog.Mode == 1 ? "zc" : "dec"));
            sb.AppendLine("SEEDMODE_FILE=" + SeedDialog.ModePathForTest());
            sb.Append(HistoryStore.SelfCheck());

            int pass = 0, fail = 0;
            sb.AppendLine("GENERATION_BEGIN");
            for (int level = 1; level <= 4; level++)
            {
                cboLevel.SelectedIndex = level - 1;
                for (int i = 0; i < count; i++)
                {
                    NewSeed();
                    GenerateOnce(false);
                    RandomExprResult r = current;
                    bool ok = false;
                    bool nok = false;
                    string err = "";
                    Rat value = Rats.Zero;
                    if (r != null)
                    {
                        ok = RandomExpr.Verify(r, target, out value, out err);
                        double nv;
                        string nerr;
                        nok = RandomExpr.NumericCheck(r, out nv, out nerr);
                        if (!nok) err = nerr;
                    }
                    else
                    {
                        err = "null";
                    }
                    bool rendered = mathBox.RenderedLineCount > 0 && mathBox.ContentHeight > 0;
                    bool audit = r != null && r.AuditOk;
                    if (ok && nok && value.Equals(target) && rendered && audit) pass++; else fail++;

                    sb.Append(level).Append('\t')
                      .Append(r == null ? "" : r.Seed).Append('\t')
                      .Append(r == null ? "" : r.Kind).Append('\t')
                      .Append(r == null ? "" : r.TypeCodes).Append('\t')
                      .Append(r == null ? "0" : r.Dims.ToString(CultureInfo.InvariantCulture)).Append('\t')
                      .Append(r == null ? "0" : (r.Identity ? "1" : "0")).Append('\t')
                      .Append(r == null ? "0" : r.Complexity.ToString(CultureInfo.InvariantCulture)).Append('\t')
                      .Append(RandomExpr.PrettyRat(value)).Append('\t')
                      .Append(ok ? "1" : "0").Append('\t')
                      .Append(r == null ? "" : r.NumericKind).Append('\t')
                      .Append(nok ? "1" : "0").Append('\t')
                      .Append(Clean(r == null ? "" : r.Expression)).Append('\t')
                      .Append(Clean(r == null ? "" : r.Latex)).Append('\t')
                      .Append(Clean(err)).Append('\t')
                      .Append(Clean(lblMeta.Text)).Append('\t')
                      .Append(audit ? "1" : "0").Append('\t')
                      .Append(r == null ? "0" : (r.AuditTight ? "1" : "0")).Append('\t')
                      .Append(r == null ? "0" : r.AuditValue.ToString("R", CultureInfo.InvariantCulture)).Append('\t')
                      .Append(r == null ? "0" : r.AuditTries.ToString(CultureInfo.InvariantCulture)).Append('\t')
                      .Append(Clean(r == null ? "" : r.AuditDetail)).AppendLine();
                }
            }

            sb.AppendLine("GENERATION_END");
            string before = mathBox.Left + "," + mathBox.Top + "," + mathBox.Width + "," + mathBox.Height;
            string after = before;
            bool fixedOk = MinimumSize == MaximumSize && Width == 1920 && Height == 1080;
            sb.AppendLine("RESIZE_BEFORE=" + before);
            sb.AppendLine("RESIZE_AFTER=" + after);
            sb.AppendLine("RESIZE_OK=" + (fixedOk ? "1" : "0"));
            sb.AppendLine("MATH_LINES=" + mathBox.RenderedLineCount.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("MATH_CONTENT_HEIGHT=" + mathBox.ContentHeight.ToString("0.###", CultureInfo.InvariantCulture));
            sb.AppendLine("MATH_OK=" + (mathBox.RenderedLineCount > 0 && mathBox.ContentHeight > 0 ? "1" : "0"));
            sb.AppendLine("===== 随机模式窗口自检：通过 " + pass + " / 失败 " + fail + " =====");
            // 生成路径里是否真的把每条都记进了历史（自检生成了 pass+fail 条）
            sb.AppendLine("HISTORY_RECORDED=" + history.Count.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("HISTORY_LOADED=" + historyLoaded.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("HISTORY_PATH=" + historyPath);
            sb.Append(HistoryFile.SelfCheck());
            sb.AppendLine("HISTORY_SEQ=" + historySeq.ToString(CultureInfo.InvariantCulture));
            // 历史窗口本身也要能构造 + 勾选（句柄未建时曾抛 NullReferenceException）
            try
            {
                using (HistoryForm hf = new HistoryForm(skin, history, targetText))
                {
                    sb.Append(hf.RunSelfCheck());
                }
            }
            catch (Exception hex)
            {
                sb.AppendLine("HISTORY_FORM_OK=0");
                sb.AppendLine("HISTORY_FORM_ERR=" + hex.GetType().Name + ": " + hex.Message);
            }
            // 收藏流程自检：批量收藏 → 去重 → 落盘 → 读回
            try
            {
                int favBefore = favorites.Count;
                List<HistoryItem> pick = new List<HistoryItem>();
                for (int i = 0; i < history.Count && i < 3; i++) pick.Add(history[i]);
                int added = HistoryStore.Favorite(favorites, pick);
                int again = HistoryStore.Favorite(favorites, pick);
                bool savedFav = HistoryFile.Save(favoritesPath, favorites);
                List<HistoryItem> backFav = HistoryFile.Load(favoritesPath);
                sb.AppendLine("FAVORITES_ADDED=" + added.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("FAVORITES_DEDUP=" + (again == 0 ? "1" : "0"));
                sb.AppendLine("FAVORITES_SAVED=" + (savedFav ? "1" : "0"));
                sb.AppendLine("FAVORITES_RELOAD=" + backFav.Count.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("FAVORITES_OK=" + (added > 0 && again == 0 && savedFav
                    && backFav.Count == favorites.Count ? "1" : "0"));
                if (favBefore > 0) { }
            }
            catch (Exception fex)
            {
                sb.AppendLine("FAVORITES_OK=0");
                sb.AppendLine("FAVORITES_ERR=" + fex.GetType().Name);
            }
            // 收藏窗口（favorites 模式）也要能构造，标题与按钮语义正确
            try
            {
                using (HistoryForm ff = new HistoryForm(skin, favorites, targetText, null, true, null))
                {
                    string fs = ff.RunSelfCheck();
                    sb.AppendLine("FAVORITES_FORM_TITLE=" + (ff.Text == "我的收藏" ? "1" : "0"));
                    sb.AppendLine("FAVORITES_FORM_MODE=" + (fs.IndexOf("HISTORY_FORM_MODE=favorites", StringComparison.Ordinal) >= 0 ? "1" : "0"));
                    sb.AppendLine("FAVORITES_FORM_OK=" + (fs.IndexOf("HISTORY_FORM_OK=1", StringComparison.Ordinal) >= 0 ? "1" : "0"));
                }
            }
            catch (Exception ffex)
            {
                sb.AppendLine("FAVORITES_FORM_OK=0");
                sb.AppendLine("FAVORITES_FORM_ERR=" + ffex.GetType().Name);
            }
            // 查看窗口的「上一条 / 下一条」自检（按列表顺序翻页）
            try
            {
                if (history.Count >= 3)
                {
                    using (HistoryPreviewForm pv = new HistoryPreviewForm(skin, history, 1))
                    {
                        sb.Append(pv.RunNavSelfCheck());
                    }
                }
            }
            catch (Exception pvex)
            {
                sb.AppendLine("PREVIEW_NAV_OK=0");
                sb.AppendLine("PREVIEW_ERR=" + pvex.GetType().Name);
            }
            LastSelfTestFailed = fail != 0;
            return sb.ToString();
        }

        /// <summary>
        /// WpfMath 兼容性自检：把各等级真实式子 + 一批“危险构造”丢给 MathBox，
        /// 记录哪些渲染成功、哪些抛错。用于确认陷阱层引入的 \bmod、k! 等能显示。
        /// </summary>
        public string RunMathCompatTest(int count)
        {
            if (count < 1) count = 1;
            if (count > 40) count = 40;
            // 自检路径不走 BuildUi，这里补齐底色/墨色，导出对比度才和真实窗口一致
            mathBox.BackColor = skin.OutputBack;
            mathBox.Ink = skin.OutputInk;
            StringBuilder sb = new StringBuilder();
            // 每条探针带一个"预期"：1 = WpfMath 应该能渲染，0 = 已知不支持（用于记录边界）。
            // 判定的是"实际 == 预期"，所以不支持的多行构造不会让自检失败，而是被记录下来。
            string[] probes = new string[]
            {
                "\\left(7^{6}\\mathrm{mod}\\,7\\right)",
                "2^{6}\\mathrm{mod}\\,7",
                "\\left(79^{\\frac{88}{2}}\\mathrm{mod}\\,89\\right)",
                "\\frac{e^{-2.5}}{2.5}\\sum_{k=1}^{\\infty}\\frac{k\\cdot 2.5^{k}}{k!}",
                "\\left\\{k\\sqrt{2}\\right\\}",
                "\\frac{1}{2\\pi i}\\oint_{C}\\frac{z}{z-13}\\,dz",
                "\\prod_{p}\\left(1-\\frac{1}{p^{2}}\\right)^{-1}",
                "\\det\\begin{pmatrix}1&0&0&0\\\\0&1&0&0\\\\0&0&5&0\\\\0&0&0&65\\end{pmatrix}",
                // 大矩阵：2–6 阶都要能渲染
                "\\det\\begin{pmatrix}1&0&0&0&0&0\\\\0&1&0&0&0&0\\\\0&0&1&0&0&0\\\\0&0&0&1&0&0\\\\0&0&0&0&1&0\\\\0&0&0&0&0&65\\end{pmatrix}",
                "\\left\\lfloor 5+\\frac{4}{5}\\right\\rfloor",
                "\\int_{0}^{1}\\int_{0}^{x}\\frac{1}{1-xy}\\,dy\\,dx",
                "\\frac{2}{\\left(5+1\\right)}\\sum_{k=1}^{5}\\frac{k}{5}",
                "\\left(0.80\\right)^{2}\\sum_{k=1}^{\\infty}k\\cdot\\left(0.20\\right)^{k-1}",
                "\\frac{1}{e}\\cdot \\int_{0}^{1}\\frac{d}{dx}\\left(x\\cdot {e}^{x}\\right)\\,dx",
                // 多行：WpfMath 没有 aligned / 顶层 \\，按设计保留单行
                "\\begin{aligned}a&=b\\\\c&=d\\end{aligned}",
                "a+b\\\\c+d",
                "\\begin{matrix}a&b\\\\c&d\\end{matrix}",
                "\\substack{a\\\\b}"
            };
            bool[] expects = new bool[]
            {
                true, true, true, true, true, true, true, true, true,
                true, true, true, true, true,
                // aligned / \begin{matrix} / \substack 都不支持；顶层 \\ 能渲染
                // （折行功能已删除：长式子现在靠 MathBox 的缩放看）
                false, true, false, false
            };
            int pass = 0, fail = 0;
            sb.AppendLine("PROBE_BEGIN");
            sb.AppendLine("LATEX\tOK\tEXPECT\tVERDICT\tWIDTH\tHEIGHT\tERROR");
            for (int i = 0; i < probes.Length; i++)
            {
                mathBox.SetLatex(probes[i]);
                bool ok = mathBox.RenderedLineCount > 0 && mathBox.ContentHeight > 0;
                bool good = ok == expects[i];
                if (good) pass++; else fail++;
                sb.Append(Clean(probes[i])).Append('\t').Append(ok ? "1" : "0").Append('\t')
                  .Append(expects[i] ? "1" : "0").Append('\t').Append(good ? "1" : "0").Append('\t')
                  .Append(mathBox.ImageWidth).Append('\t').Append(mathBox.ImageHeight).Append('\t')
                  .AppendLine(Clean(mathBox.RenderError));
            }
            sb.AppendLine("PROBE_END");

            sb.AppendLine("L5_BEGIN");
            sb.AppendLine("LEVEL\tSEED\tLAYERS\tDIMS\tOK\tWIDTH\tHEIGHT\tERROR\tLATEX");
            for (int i = 0; i < count; i++)
            {
                cboLevel.SelectedIndex = 4;          // L5
                NewSeed();
                GenerateOnce(false);
                RandomExprResult r = current;
                bool ok = r != null && mathBox.RenderedLineCount > 0 && mathBox.ContentHeight > 0;
                if (ok) pass++; else fail++;
                sb.Append("5\t").Append(r == null ? "" : r.Seed).Append('\t')
                  .Append(r == null ? "0" : r.Layers.ToString(CultureInfo.InvariantCulture)).Append('\t')
                  .Append(r == null ? "0" : r.Dims.ToString(CultureInfo.InvariantCulture)).Append('\t')
                  .Append(ok ? "1" : "0").Append('\t')
                  .Append(mathBox.ImageWidth).Append('\t').Append(mathBox.ImageHeight).Append('\t')
                  .Append(Clean(mathBox.RenderError)).Append('\t')
                  .AppendLine(Clean(r == null ? "" : r.Latex));
            }
            sb.AppendLine("L5_END");

            // 缩放自检：按钮走 ZoomBy，滚轮走 ZoomAt，两者都改 Zoom；复位回 1.0
            sb.AppendLine("ZOOM_BEGIN");
            float z0 = mathBox.Zoom;
            mathBox.ZoomBy(1.25f);
            float z1 = mathBox.Zoom;
            mathBox.ZoomBy(1.25f);
            float z2 = mathBox.Zoom;
            mathBox.ZoomBy(0.8f);
            float z3 = mathBox.Zoom;
            mathBox.ResetView();
            float z4 = mathBox.Zoom;
            bool zoomOk = Math.Abs(z0 - 1f) < 1e-4f
                && Math.Abs(z1 - 1.25f) < 0.02f
                && Math.Abs(z2 - 1.5625f) < 0.03f
                && Math.Abs(z3 - 1.25f) < 0.03f
                && Math.Abs(z4 - 1f) < 1e-4f;
            if (zoomOk) pass++; else fail++;
            sb.AppendLine("ZOOM_STEPS=" + z0.ToString("0.###", CultureInfo.InvariantCulture) + ","
                + z1.ToString("0.###", CultureInfo.InvariantCulture) + ","
                + z2.ToString("0.###", CultureInfo.InvariantCulture) + ","
                + z3.ToString("0.###", CultureInfo.InvariantCulture) + ","
                + z4.ToString("0.###", CultureInfo.InvariantCulture));
            sb.AppendLine("ZOOM_TEXT=" + mathBox.ZoomText);
            sb.AppendLine("ZOOM_OK=" + (zoomOk ? "1" : "0"));
            sb.AppendLine("ZOOM_END");

            // 自定义种子窗口的两种模式转换自检
            // 自定义种子窗口：Zc 编解码 + 十进制转换 + 32 位折行
            string zc1 = SeedDialog.BitsToZc("10100001");
            string zc2 = SeedDialog.BitsToZc("1000110101");
            string bits1 = SeedDialog.ZcToBits("ZcZczczC");
            string bits2 = SeedDialog.ZcToBits("ZczcZCzCzC");
            string badZc = SeedDialog.ZcToBits("ZczZ");
            string cvBin = SeedDialog.ConvertText("ZcZczczC", true);
            string cvDec = SeedDialog.ConvertText("1 234 5678", false);
            string wrapped = SeedDialog.Wrap(new string('7', 64), 32);
            bool cvOk = zc1 == "ZcZczczC" && zc2 == "ZczcZCzCzC"
                && bits1 == "10100001" && bits2 == "1000110101" && badZc == null
                && cvBin == "161" && cvDec == "12345678"
                && wrapped == new string('7', 32) + "\r\n" + new string('7', 32);
            if (cvOk) pass++; else fail++;
            sb.AppendLine("SEED_ZC_ENC=" + zc1 + "," + zc2);
            sb.AppendLine("SEED_ZC_DEC=" + (bits1 ?? "null") + "," + (bits2 ?? "null") + ",bad=" + (badZc ?? "null"));
            sb.AppendLine("SEED_CONVERT_BIN=" + (cvBin ?? "null"));
            sb.AppendLine("SEED_CONVERT_DEC=" + (cvDec ?? "null"));
            sb.AppendLine("SEED_CONVERT_OK=" + (cvOk ? "1" : "0"));

            // 导出尺寸自检：保存图片走 RenderExport（按大尺寸重渲染），确认它比窗口位图大
            sb.AppendLine("EXPORT_BEGIN");
            using (Bitmap exp = RenderExport(mathBox.Latex))
            {
                int ew = exp == null ? 0 : exp.Width;
                int eh = exp == null ? 0 : exp.Height;
                // 采样最暗像素：导出的字必须是深色，否则就是"太淡看不清"
                int minLum = 255;
                int stepX = Math.Max(1, ew / 900), stepY = Math.Max(1, eh / 200);
                for (int y = 0; y < eh; y += stepY)
                    for (int x = 0; x < ew; x += stepX)
                    {
                        Color c = exp.GetPixel(x, y);
                        if (c.A < 40) continue;
                        int lum = (c.R * 30 + c.G * 59 + c.B * 11) / 100;
                        if (lum < minLum) minLum = lum;
                    }
                // 墨色 vs 底色的对比度：浅色墨（辞岁行主题）配白底会几乎看不见
                Color bgc = mathBox.BackColor;
                int bgLum = (bgc.R * 30 + bgc.G * 59 + bgc.B * 11) / 100;
                int contrast = Math.Abs(bgLum - minLum);
                bool expOk = ew >= 1200 && eh >= 60 && contrast >= 60;
                sb.AppendLine("EXPORT_BG_LUMA=" + bgLum);
                sb.AppendLine("EXPORT_CONTRAST=" + contrast);
                if (expOk) pass++; else fail++;
                // 落盘一份，供外部检查像素（alpha / 亮度）
                try
                {
                    string probe = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dsh-export-probe.png");
                    using (Bitmap canvas = new Bitmap(ew + 80, eh + 80))
                    using (Graphics g2 = Graphics.FromImage(canvas))
                    {
                        g2.Clear(mathBox.BackColor);
                        g2.DrawImage(exp, 40, 40);
                        canvas.Save(probe, ImageFormat.Png);
                    }
                    sb.AppendLine("EXPORT_PROBE=" + probe);
                }
                catch (Exception) { }
                sb.AppendLine("EXPORT_SIZE=" + ew + "x" + eh);
                sb.AppendLine("EXPORT_MIN_LUMA=" + minLum);
                sb.AppendLine("EXPORT_OK=" + (expOk ? "1" : "0"));
            }
            sb.AppendLine("EXPORT_END");

            sb.AppendLine("===== WpfMath 兼容性自检：通过 " + pass + " / 失败 " + fail + " =====");
            LastSelfTestFailed = fail != 0;
            return sb.ToString();
        }

        static string Clean(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
        }
    }
}
