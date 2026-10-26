using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace Calculate325
{
    /// <summary>一条随机生成的历史记录。</summary>
    sealed class HistoryItem
    {
        public int Seq;
        public DateTime Time;
        public int Level;
        public string Seed;
        public string Latex;
        public string Expression;
        public string Kind;
        public string Codes;
        public int Layers;
        public int Dims;
        public int Complexity;
        public string Meta;

        /// <summary>列表里显示的一行摘要（LaTeX 压成单行并截断）。</summary>
        public string Summary(int max)
        {
            string s = Latex ?? "";
            StringBuilder sb = new StringBuilder(s.Length);
            bool space = false;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                bool ws = c == '\r' || c == '\n' || c == '\t' || c == ' ';
                if (ws) { if (!space) { sb.Append(' '); space = true; } continue; }
                space = false;
                sb.Append(c);
            }
            string t = sb.ToString().Trim();
            if (max > 8 && t.Length > max) t = t.Substring(0, max - 1) + "…";
            return t;
        }

        public string LevelText { get { return "L" + Level.ToString(CultureInfo.InvariantCulture); } }

        /// <summary>复制一份（收藏时用，之后历史被删也不影响收藏）。</summary>
        public HistoryItem Clone()
        {
            HistoryItem c = new HistoryItem();
            c.Seq = Seq;
            c.Time = Time;
            c.Level = Level;
            c.Seed = Seed;
            c.Latex = Latex;
            c.Expression = Expression;
            c.Kind = Kind;
            c.Codes = Codes;
            c.Layers = Layers;
            c.Dims = Dims;
            c.Complexity = Complexity;
            c.Meta = Meta;
            return c;
        }

        public string LayerText
        {
            get
            {
                return Layers.ToString(CultureInfo.InvariantCulture) + "层/"
                    + Dims.ToString(CultureInfo.InvariantCulture) + "重";
            }
        }
    }

    /// <summary>
    /// 历史的纯逻辑部分：不依赖窗口，便于自检。
    /// 顺序是**新的在最前**（时间倒序），容量满了从末尾（最旧）丢。
    /// </summary>
    static class HistoryStore
    {
        public const int Capacity = 300;

        public static HistoryItem Add(List<HistoryItem> list, HistoryItem it)
        {
            if (list == null || it == null) return null;
            list.Insert(0, it);                              // 新的放最上
            while (list.Count > Capacity) list.RemoveAt(list.Count - 1);
            return it;
        }

        /// <summary>
        /// 批量收藏：按 LaTeX 去重（已收藏过的跳过），新的放在最前。
        /// 返回真正新增的条数。
        /// </summary>
        public static int Favorite(List<HistoryItem> favs, IList<HistoryItem> sel)
        {
            if (favs == null || sel == null) return 0;
            HashSet<string> have = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < favs.Count; i++) if (favs[i] != null) have.Add(favs[i].Latex ?? "");
            int added = 0;
            for (int i = 0; i < sel.Count; i++)
            {
                HistoryItem it = sel[i];
                if (it == null) continue;
                string key = it.Latex ?? "";
                if (have.Contains(key)) continue;
                have.Add(key);
                HistoryItem c = it.Clone();
                c.Time = DateTime.Now;                       // 收藏时间
                favs.Add(c);
                added++;
            }
            if (added > 0)
            {
                // 按收藏时间倒序（新的在最前）
                favs.Sort(delegate(HistoryItem a, HistoryItem b) { return b.Time.CompareTo(a.Time); });
                while (favs.Count > Capacity) favs.RemoveAt(favs.Count - 1);
            }
            return added;
        }

        /// <summary>按引用删除选中的条目，返回真正删掉的条数。</summary>
        public static int Remove(List<HistoryItem> list, IList<HistoryItem> sel)
        {
            if (list == null || sel == null || sel.Count == 0) return 0;
            HashSet<HistoryItem> set = new HashSet<HistoryItem>();
            for (int i = 0; i < sel.Count; i++) if (sel[i] != null) set.Add(sel[i]);
            int before = list.Count;
            list.RemoveAll(delegate(HistoryItem x) { return set.Contains(x); });
            return before - list.Count;
        }

        public static int RemoveAt(List<HistoryItem> list, IList<int> indices)
        {
            if (list == null || indices == null || indices.Count == 0) return 0;
            List<int> idx = new List<int>(indices);
            idx.Sort();
            int n = 0;
            for (int i = idx.Count - 1; i >= 0; i--)
            {
                int k = idx[i];
                if (k >= 0 && k < list.Count) { list.RemoveAt(k); n++; }
            }
            return n;
        }

        /// <summary>自检：新增（倒序）/ 按引用删除 / 容量淘汰。</summary>
        public static string SelfCheck()
        {
            StringBuilder sb = new StringBuilder();
            List<HistoryItem> list = new List<HistoryItem>();
            for (int i = 0; i < 5; i++)
            {
                HistoryItem it = new HistoryItem();
                it.Seq = i + 1;
                it.Latex = "x" + i.ToString(CultureInfo.InvariantCulture);
                it.Level = (i % 5) + 1;
                Add(list, it);
            }
            // 倒序：最后加的在最前
            bool addOk = list.Count == 5 && list[0].Seq == 5 && list[4].Seq == 1 && list[0].Latex == "x4";

            int removed = Remove(list, new List<HistoryItem> { list[0], list[2] });
            bool rmOk = removed == 2 && list.Count == 3
                && list[0].Latex == "x3" && list[1].Latex == "x1" && list[2].Latex == "x0";

            List<HistoryItem> big = new List<HistoryItem>();
            for (int i = 0; i < Capacity + 25; i++)
            {
                HistoryItem it = new HistoryItem();
                it.Seq = i;
                it.Latex = "y" + i.ToString(CultureInfo.InvariantCulture);
                Add(big, it);
            }
            // 留下最新的 300 条：y324 … y25
            bool capOk = big.Count == Capacity
                && big[0].Latex == "y" + (Capacity + 24).ToString(CultureInfo.InvariantCulture)
                && big[Capacity - 1].Latex == "y25";

            sb.AppendLine("HISTORY_ADD=" + (addOk ? "1" : "0"));
            sb.AppendLine("HISTORY_REMOVE=" + (rmOk ? "1" : "0") + ",REMOVED=" + removed.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("HISTORY_CAPACITY=" + (capOk ? "1" : "0") + ",COUNT=" + big.Count.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("HISTORY_OK=" + (addOk && rmOk && capOk ? "1" : "0"));
            return sb.ToString();
        }
    }

    /// <summary>生成历史窗口：列表 + 查看（新窗口）/ 删除 / 全选 / 反选 / 清空。</summary>
    /// <summary>
    /// 历史落盘：exe 同目录 history.txt，一行一条，文本字段用 Base64（免去 LaTeX 反斜杠转义）。
    /// 文件顺序 = 显示顺序（新的在最前）。可用环境变量 CALCULATE325_HISTORY 指定别的路径（测试用）。
    /// </summary>
    static class HistoryFile
    {
        public const int Capacity = HistoryStore.Capacity;

        public static string PathFor(string dir)
        {
            try
            {
                string o = Environment.GetEnvironmentVariable("CALCULATE325_HISTORY");
                if (!string.IsNullOrEmpty(o)) return o;
            }
            catch (Exception) { }
            return System.IO.Path.Combine(dir ?? Application.StartupPath, "history.txt");
        }

        /// <summary>收藏落盘路径（默认 exe 同目录 favorites.txt，可用 CALCULATE325_FAVORITES 覆盖）。</summary>
        public static string PathForFavorites(string dir)
        {
            try
            {
                string o = Environment.GetEnvironmentVariable("CALCULATE325_FAVORITES");
                if (!string.IsNullOrEmpty(o)) return o;
            }
            catch (Exception) { }
            return System.IO.Path.Combine(dir ?? Application.StartupPath, "favorites.txt");
        }

        static string B64(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(s));
        }

        static string UnB64(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            try { return Encoding.UTF8.GetString(Convert.FromBase64String(s)); }
            catch (Exception) { return ""; }
        }

        static string Line(HistoryItem it)
        {
            return it.Seq.ToString(CultureInfo.InvariantCulture) + "|"
                + it.Time.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture) + "|"
                + it.Level.ToString(CultureInfo.InvariantCulture) + "|"
                + B64(it.Seed) + "|" + B64(it.Latex) + "|" + B64(it.Expression) + "|"
                + B64(it.Kind) + "|" + B64(it.Codes) + "|"
                + it.Layers.ToString(CultureInfo.InvariantCulture) + "|"
                + it.Dims.ToString(CultureInfo.InvariantCulture) + "|"
                + it.Complexity.ToString(CultureInfo.InvariantCulture) + "|"
                + B64(it.Meta);
        }

        static HistoryItem Parse(string line)
        {
            if (string.IsNullOrEmpty(line)) return null;
            string[] a = line.Split('|');
            if (a.Length < 12) return null;
            HistoryItem it = new HistoryItem();
            int seq, lvl, layers, dims, comp;
            if (!int.TryParse(a[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out seq)) return null;
            if (!int.TryParse(a[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out lvl)) return null;
            if (!int.TryParse(a[8], NumberStyles.Integer, CultureInfo.InvariantCulture, out layers)) return null;
            if (!int.TryParse(a[9], NumberStyles.Integer, CultureInfo.InvariantCulture, out dims)) return null;
            if (!int.TryParse(a[10], NumberStyles.Integer, CultureInfo.InvariantCulture, out comp)) return null;
            DateTime t;
            if (!DateTime.TryParseExact(a[1], "yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out t)) t = DateTime.Now;
            it.Seq = seq;
            it.Time = t;
            it.Level = lvl;
            it.Seed = UnB64(a[3]);
            it.Latex = UnB64(a[4]);
            it.Expression = UnB64(a[5]);
            it.Kind = UnB64(a[6]);
            it.Codes = UnB64(a[7]);
            it.Layers = layers;
            it.Dims = dims;
            it.Complexity = comp;
            it.Meta = UnB64(a[11]);
            return it;
        }

        /// <summary>读全部历史（保持文件里的顺序 = 新的在前）。读失败返回空表。</summary>
        public static List<HistoryItem> Load(string path)
        {
            List<HistoryItem> list = new List<HistoryItem>();
            try
            {
                if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path)) return list;
                string[] lines = System.IO.File.ReadAllLines(path, Encoding.UTF8);
                for (int i = 0; i < lines.Length; i++)
                {
                    HistoryItem it = Parse(lines[i]);
                    if (it != null && !string.IsNullOrEmpty(it.Latex)) list.Add(it);
                    if (list.Count >= Capacity) break;
                }
            }
            catch (Exception) { }
            return list;
        }

        /// <summary>整体重写（新增/删除/清空后都调一次）。失败静默。</summary>
        public static bool Save(string path, List<HistoryItem> items)
        {
            try
            {
                if (string.IsNullOrEmpty(path)) return false;
                StringBuilder sb = new StringBuilder(1 << 18);
                int n = items == null ? 0 : Math.Min(items.Count, Capacity);
                for (int i = 0; i < n; i++) sb.Append(Line(items[i])).Append('\n');
                string dir = System.IO.Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !System.IO.Directory.Exists(dir))
                    System.IO.Directory.CreateDirectory(dir);
                System.IO.File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
                return true;
            }
            catch (Exception) { return false; }
        }

        /// <summary>自检：写盘 → 读回 → 字段一致（用临时文件，不碰真实历史）。</summary>
        public static string SelfCheck()
        {
            StringBuilder sb = new StringBuilder();
            string tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "calculate325-history-selftest-" + Guid.NewGuid().ToString("N") + ".txt");
            bool ok = false;
            try
            {
                List<HistoryItem> src = new List<HistoryItem>();
                for (int i = 0; i < 3; i++)
                {
                    HistoryItem it = new HistoryItem();
                    it.Seq = 100 - i;
                    it.Time = new DateTime(2026, 10, 25, 12, 0, i);
                    it.Level = i + 1;
                    it.Seed = "se\t|ed" + i;                        // 故意带分隔符/制表
                    it.Latex = "\\int_{0}^{1}x^{" + i + "}\\,dx";   // 反斜杠
                    it.Expression = "a|b";                            // 竖线
                    it.Kind = "k" + i;
                    it.Codes = "0,1";
                    it.Layers = i + 2;
                    it.Dims = i;
                    it.Complexity = 50 + i;
                    it.Meta = "第 " + i + " 次";
                    src.Add(it);
                }
                Save(tmp, src);
                List<HistoryItem> back = Load(tmp);
                ok = back.Count == 3
                    && back[0].Seq == 100 && back[0].Latex == src[0].Latex
                    && back[0].Seed == src[0].Seed && back[0].Expression == "a|b"
                    && back[0].Time == src[0].Time
                    && back[2].Seq == 98 && back[2].Complexity == 52;
            }
            catch (Exception) { ok = false; }
            finally
            {
                try { if (System.IO.File.Exists(tmp)) System.IO.File.Delete(tmp); }
                catch (Exception) { }
            }
            sb.AppendLine("HISTORY_FILE=" + (ok ? "1" : "0"));
            sb.AppendLine("HISTORY_FILE_PATH=" + PathFor(Application.StartupPath));
            return sb.ToString();
        }
    }

    /// <summary>
    /// 历史 / 收藏列表窗口：左下筛选（等级）、每行「查看」、右侧批量按钮。
    /// favorites=true 时变成「我的收藏」：删除语义换成取消收藏，并多出「收藏选中」的反向操作。
    /// </summary>
    sealed class HistoryForm : Form
    {
        readonly Skin skin;
        readonly List<HistoryItem> items;
        readonly List<HistoryItem> shown = new List<HistoryItem>();
        readonly Action onChanged;
        readonly bool favoritesMode;
        readonly List<HistoryItem> favorites;      // 历史模式下的收藏袋（批量收藏用）
        int sortCol = ColSeq;      // 默认按编号
        bool sortAsc = true;       // 升序 → 1 号在最上
        float scale = 1f;

        DataGridView grid;
        ComboBox cboFilter;
        Label lblFilter, lblLevelCap, lblHead, lblHint, lblCount;
        ChamferButton btnFav, btnDelete, btnAll, btnNone, btnInvert, btnClear, btnClose;

        const int ColCheck = 0, ColSeq = 1, ColTime = 2, ColLevel = 3,
                  ColLayer = 4, ColComplexity = 5, ColSeed = 6, ColSummary = 7, ColView = 8;

        int R(float v) { return (int)Math.Round(v * scale); }

        public HistoryForm(Skin s, List<HistoryItem> items, string targetText)
            : this(s, items, targetText, null)
        {
        }

        /// <summary>onChanged：窗口里删/清空后回调（用于立刻落盘）。</summary>
        public HistoryForm(Skin s, List<HistoryItem> items, string targetText, Action onChanged)
            : this(s, items, targetText, onChanged, false, null)
        {
        }

        /// <summary>收藏列表：favorites=true。</summary>
        public HistoryForm(Skin s, List<HistoryItem> items, string targetText, Action onChanged,
                           bool favorites, List<HistoryItem> favBag)
        {
            this.skin = s ?? Skins.Plain;
            this.items = items ?? new List<HistoryItem>();
            this.onChanged = onChanged;
            this.favoritesMode = favorites;
            this.favorites = favBag;

            Text = favoritesMode ? "我的收藏" : "生成历史";
            FormBorderStyle = FormBorderStyle.Sizable;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = true;
            MinimizeBox = false;
            ShowInTaskbar = false;
            AutoScaleMode = AutoScaleMode.None;
            BackColor = this.skin.FormBack;
            ForeColor = this.skin.Ink;
            Font = new Font("Microsoft YaHei UI", 10.5f, FontStyle.Regular);

            try
            {
                using (Graphics g = CreateGraphics()) scale = g.DpiX / 96f;
            }
            catch (Exception) { }
            if (scale < 0.75f) scale = 0.75f;
            if (scale > 3f) scale = 3f;

            ClientSize = new Size(R(1180), R(780));
            MinimumSize = new Size(R(900), R(520));

            // 标题 / 副标题 / 提示：一律左对齐
            lblHead = new Label();
            lblHead.Text = (favoritesMode ? "我的收藏" : "生成历史") + " · 目标 " + (targetText ?? "325");
            lblHead.AutoSize = false;
            lblHead.TextAlign = ContentAlignment.MiddleLeft;
            lblHead.ForeColor = this.skin.Accent;
            lblHead.Font = new Font("Microsoft YaHei UI", 13f, FontStyle.Bold);
            Controls.Add(lblHead);

            lblHint = new Label();
            lblHint.Text = favoritesMode
                ? "每行右侧「查看」打开独立窗口；勾选后可取消收藏（不影响历史记录）"
                : "每行右侧「查看」打开独立窗口；勾选后可收藏或批量删除";
            lblHint.AutoSize = false;
            lblHint.TextAlign = ContentAlignment.MiddleLeft;
            lblHint.ForeColor = this.skin.Muted;
            lblHint.Font = new Font("Microsoft YaHei UI", 9.5f);
            Controls.Add(lblHint);

            grid = new DataGridView();
            grid.AllowUserToAddRows = false;
            grid.AllowUserToDeleteRows = false;
            grid.AllowUserToResizeRows = false;
            grid.AllowUserToOrderColumns = false;
            grid.RowHeadersVisible = false;
            grid.MultiSelect = true;
            grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
            grid.BorderStyle = BorderStyle.FixedSingle;
            grid.BackgroundColor = this.skin.FieldBack;
            grid.GridColor = this.skin.Border;
            grid.Font = new Font("Microsoft YaHei UI", 10f);
            grid.EnableHeadersVisualStyles = false;
            grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
            grid.ColumnHeadersHeight = R(34);
            grid.ColumnHeadersDefaultCellStyle.BackColor = this.skin.PanelBack;
            grid.ColumnHeadersDefaultCellStyle.ForeColor = this.skin.Ink;
            grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = this.skin.PanelBack;
            grid.ColumnHeadersDefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleLeft;
            grid.ColumnHeadersDefaultCellStyle.Font = new Font("Microsoft YaHei UI", 10f, FontStyle.Bold);
            grid.DefaultCellStyle.BackColor = this.skin.FieldBack;
            grid.DefaultCellStyle.ForeColor = this.skin.FieldFore;
            grid.DefaultCellStyle.SelectionBackColor = Blend(this.skin.FieldBack, this.skin.Accent, 0.22f);
            grid.DefaultCellStyle.SelectionForeColor = this.skin.FieldFore;
            grid.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleLeft;
            grid.RowTemplate.Height = R(38);            // 行间距加大

            DataGridViewCheckBoxColumn cChk = new DataGridViewCheckBoxColumn();
            cChk.HeaderText = "";
            cChk.Name = "chk";
            cChk.Width = R(42);
            cChk.ReadOnly = false;
            grid.Columns.Add(cChk);

            grid.Columns.Add(TextCol("#", R(64), DataGridViewContentAlignment.MiddleLeft));
            grid.Columns.Add(TextCol("时间", R(130), DataGridViewContentAlignment.MiddleLeft));
            grid.Columns.Add(TextCol("等级", R(80), DataGridViewContentAlignment.MiddleLeft));
            grid.Columns.Add(TextCol("层/重", R(120), DataGridViewContentAlignment.MiddleLeft));
            grid.Columns.Add(TextCol("复杂度", R(110), DataGridViewContentAlignment.MiddleLeft));
            DataGridViewTextBoxColumn cSeed = TextCol("种子", R(150), DataGridViewContentAlignment.MiddleLeft);
            cSeed.SortMode = DataGridViewColumnSortMode.Programmatic;
            grid.Columns.Add(cSeed);
            DataGridViewTextBoxColumn cSum = TextCol("摘要", R(560), DataGridViewContentAlignment.MiddleLeft);
            cSum.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
            grid.Columns.Add(cSum);

            // 每行一个独立「查看」按钮
            DataGridViewButtonColumn cView = new DataGridViewButtonColumn();
            cView.HeaderText = "查看";
            cView.Name = "view";
            cView.Text = "查看";
            cView.UseColumnTextForButtonValue = true;
            cView.Width = R(78);
            cView.FlatStyle = FlatStyle.Flat;
            cView.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
            cView.DefaultCellStyle.BackColor = this.skin.PanelBack;
            cView.DefaultCellStyle.ForeColor = this.skin.Accent;
            cView.DefaultCellStyle.SelectionBackColor = this.skin.PanelBack;
            cView.DefaultCellStyle.SelectionForeColor = this.skin.Accent;
            grid.Columns.Add(cView);

            grid.CellContentClick += delegate(object o, DataGridViewCellEventArgs e)
            {
                if (e.RowIndex < 0) return;
                if (e.ColumnIndex == ColView) ViewItem(ItemAt(e.RowIndex));
            };
            grid.CellValueChanged += delegate { UpdateCount(); };
            grid.CurrentCellDirtyStateChanged += delegate
            {
                // 勾选框要立即提交，否则计数要等失焦才更新
                try
                {
                    if (grid.IsCurrentCellDirty && grid.CurrentCell is DataGridViewCheckBoxCell)
                        grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
                }
                catch (Exception) { }
            };
            grid.SelectionChanged += delegate { UpdateCount(); };
            // 点表头切换升/降序（像任务管理器那样）
            grid.ColumnHeaderMouseClick += delegate(object o, DataGridViewCellMouseEventArgs e)
            {
                SortBy(e.ColumnIndex);
            };
            grid.CellMouseDoubleClick += delegate(object o, DataGridViewCellMouseEventArgs e)
            {
                if (e.RowIndex >= 0 && e.ColumnIndex != ColCheck) ViewItem(ItemAt(e.RowIndex));
            };
            grid.CellMouseDown += delegate(object o, DataGridViewCellMouseEventArgs e)
            {
                if (e.Button == MouseButtons.Right && e.RowIndex >= 0)
                {
                    grid.ClearSelection();
                    grid.Rows[e.RowIndex].Selected = true;
                }
            };
            Controls.Add(grid);

            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Items.Add("查看（独立窗口）", null, delegate { ViewItem(CurrentItem()); });
            menu.Items.Add("复制 LaTeX", null, delegate { CopyCurrent(); });
            menu.Items.Add("复制种子", null, delegate { CopySeedOf(CurrentItem()); });
            menu.Items.Add("保存图片 PNG", null, delegate { SaveCurrentPng(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("删除", null, delegate { DeleteSelected(); });
            grid.ContextMenuStrip = menu;

            lblCount = new Label();
            lblCount.ForeColor = this.skin.Muted;
            lblCount.Font = new Font("Microsoft YaHei UI", 9.5f);
            lblCount.TextAlign = ContentAlignment.MiddleLeft;
            Controls.Add(lblCount);

            // 左下的筛选：筛选 / 等级 [全部▾]
            lblFilter = new Label();
            lblFilter.Text = "筛选";
            lblFilter.AutoSize = false;
            lblFilter.TextAlign = ContentAlignment.MiddleLeft;
            lblFilter.ForeColor = this.skin.Muted;
            lblFilter.Font = new Font("Microsoft YaHei UI", 9.5f);
            Controls.Add(lblFilter);

            lblLevelCap = new Label();
            lblLevelCap.Text = "等级";
            lblLevelCap.AutoSize = false;
            lblLevelCap.TextAlign = ContentAlignment.MiddleLeft;
            lblLevelCap.ForeColor = this.skin.Muted;
            lblLevelCap.Font = new Font("Microsoft YaHei UI", 9.5f);
            Controls.Add(lblLevelCap);

            cboFilter = new ComboBox();
            cboFilter.DropDownStyle = ComboBoxStyle.DropDownList;
            cboFilter.FlatStyle = FlatStyle.Flat;
            cboFilter.BackColor = this.skin.FieldBack;
            cboFilter.ForeColor = this.skin.FieldFore;
            cboFilter.Font = new Font("Microsoft YaHei UI", 10f);
            cboFilter.Items.Add("全部");
            for (int lv = 1; lv <= 5; lv++) cboFilter.Items.Add("L" + lv.ToString(CultureInfo.InvariantCulture));
            cboFilter.SelectedIndex = 0;
            cboFilter.SelectedIndexChanged += delegate { RefreshList(); };
            Controls.Add(cboFilter);

            // 右下批量按钮
            btnFav = MakeButton("收藏选中");
            btnFav.Emphasize = !favoritesMode;
            btnFav.Visible = !favoritesMode;          // 收藏列表里没有「再收藏」
            btnFav.Click += delegate { FavoriteSelected(); };
            Controls.Add(btnFav);

            btnDelete = MakeButton(favoritesMode ? "取消收藏" : "删除选中");
            btnDelete.Emphasize = favoritesMode;
            btnDelete.Click += delegate { DeleteSelected(); };
            Controls.Add(btnDelete);

            btnAll = MakeButton("全选");
            btnAll.Click += delegate { SetAllChecked(true); };
            Controls.Add(btnAll);

            btnNone = MakeButton("取消全选");
            btnNone.Click += delegate { SetAllChecked(false); };
            Controls.Add(btnNone);

            btnInvert = MakeButton("反选");
            btnInvert.Click += delegate { InvertChecked(); };
            Controls.Add(btnInvert);

            btnClear = MakeButton(favoritesMode ? "清空收藏" : "清空");
            btnClear.Click += delegate { ClearAll(); };
            Controls.Add(btnClear);

            btnClose = MakeButton("关闭");
            btnClose.Click += delegate { Close(); };
            Controls.Add(btnClose);

            Resize += delegate { LayoutControls(); };
            AppIcon.Apply(this);
            ThemedCaption.Hook(this, skin);
            LayoutControls();
            RefreshList();
        }

        static DataGridViewTextBoxColumn TextCol(string head, int width, DataGridViewContentAlignment align)
        {
            DataGridViewTextBoxColumn c = new DataGridViewTextBoxColumn();
            c.HeaderText = head;
            c.Width = width;
            c.ReadOnly = true;
            c.SortMode = DataGridViewColumnSortMode.Programmatic;
            c.DefaultCellStyle.Alignment = align;
            return c;
        }

        static Color Blend(Color a, Color b, float t)
        {
            if (t < 0) t = 0;
            if (t > 1) t = 1;
            return Color.FromArgb(
                (int)(a.R + (b.R - a.R) * t),
                (int)(a.G + (b.G - a.G) * t),
                (int)(a.B + (b.B - a.B) * t));
        }

        ChamferButton MakeButton(string text)
        {
            ChamferButton b = new ChamferButton();
            b.Text = text;
            b.ApplySkin(skin);
            b.Cut = R(6);
            b.Font = new Font("Microsoft YaHei UI", 10.5f, FontStyle.Regular);
            return b;
        }

        void LayoutControls()
        {
            int pad = R(20);
            int barH = R(40);
            int by = ClientSize.Height - pad - barH;

            lblHead.Bounds = new Rectangle(pad, R(10), Math.Max(R(300), ClientSize.Width - pad * 2), R(28));
            lblHint.Bounds = new Rectangle(pad, R(40), Math.Max(R(300), ClientSize.Width - pad * 2), R(22));

            int gridTop = R(70);
            grid.Bounds = new Rectangle(pad, gridTop, Math.Max(R(320), ClientSize.Width - pad * 2),
                Math.Max(R(160), by - R(64) - gridTop));

            lblCount.Bounds = new Rectangle(pad, grid.Bottom + R(6), Math.Max(R(300), R(420)), R(22));

            // 左下：筛选（上行标签 + 下行「等级」+ 下拉）
            int fy = ClientSize.Height - pad - R(74);
            lblFilter.Bounds = new Rectangle(pad, fy, R(90), R(20));
            lblLevelCap.Bounds = new Rectangle(pad, fy + R(26), R(44), R(34));
            cboFilter.Bounds = new Rectangle(pad + R(50), fy + R(26), R(220), R(34));

            // 右下：收藏选中 / 删除(取消收藏) / 全选 / 取消全选 / 反选 / 清空 / 关闭
            int ry = by - R(34);
            btnClose.Bounds = new Rectangle(ClientSize.Width - pad - R(88), ry, R(88), barH);
            btnClear.Bounds = new Rectangle(btnClose.Left - R(8) - R(96), ry, R(96), barH);
            btnInvert.Bounds = new Rectangle(btnClear.Left - R(8) - R(80), ry, R(80), barH);
            btnNone.Bounds = new Rectangle(btnInvert.Left - R(8) - R(104), ry, R(104), barH);
            btnAll.Bounds = new Rectangle(btnNone.Left - R(8) - R(80), ry, R(80), barH);
            btnDelete.Bounds = new Rectangle(btnAll.Left - R(8) - R(116), ry, R(116), barH);
            btnFav.Bounds = new Rectangle(btnDelete.Left - R(8) - R(116), ry, R(116), barH);
        }

        /// <summary>把勾选的历史批量加入收藏（去重，已收藏过的跳过）。</summary>
        void FavoriteSelected()
        {
            if (favorites == null) return;
            List<HistoryItem> sel = CheckedItems();
            if (sel.Count == 0) sel = SelectedItemsSnapshot();
            if (sel.Count == 0)
            {
                MessageBox.Show(this, "先勾选要收藏的记录。", "我的收藏", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            int added = HistoryStore.Favorite(favorites, sel);
            if (onChanged != null) { try { onChanged(); } catch (Exception) { } }
            lblCount.Text = added > 0
                ? "已收藏 " + added + " 条（共 " + favorites.Count + " 条收藏）"
                : "这 " + sel.Count + " 条已经收藏过了";
        }

        List<HistoryItem> SelectedItemsSnapshot()
        {
            List<HistoryItem> sel = new List<HistoryItem>();
            try
            {
                for (int i = 0; i < grid.SelectedRows.Count; i++)
                {
                    HistoryItem it = ItemAt(grid.SelectedRows[i].Index);
                    if (it != null) sel.Add(it);
                }
            }
            catch (Exception) { }
            return sel;
        }

        HistoryItem ItemAt(int rowIndex)
        {
            if (rowIndex < 0 || rowIndex >= shown.Count) return null;
            return shown[rowIndex];
        }

        /// <summary>点表头切换排序：同一列再点一次反向，换列则从升序开始。</summary>
        internal void SortBy(int col)
        {
            if (col == ColCheck || col == ColView || col < 0) return;
            if (col == sortCol) sortAsc = !sortAsc;
            else { sortCol = col; sortAsc = true; }
            RefreshList();
        }

        /// <summary>按当前 sortCol / sortAsc 排；同值时用编号兜底，保证顺序稳定。</summary>
        void ApplySort()
        {
            shown.Sort(delegate(HistoryItem a, HistoryItem b)
            {
                int c;
                switch (sortCol)
                {
                    case ColTime: c = a.Time.CompareTo(b.Time); break;
                    case ColLevel: c = a.Level.CompareTo(b.Level); break;
                    case ColLayer: c = a.Layers.CompareTo(b.Layers); break;
                    case ColComplexity: c = a.Complexity.CompareTo(b.Complexity); break;
                    case ColSeed: c = string.CompareOrdinal(a.Seed ?? "", b.Seed ?? ""); break;
                    case ColSummary: c = string.CompareOrdinal(a.Latex ?? "", b.Latex ?? ""); break;
                    default: c = a.Seq.CompareTo(b.Seq); break;
                }
                if (c == 0) c = a.Seq.CompareTo(b.Seq);
                return sortAsc ? c : -c;
            });
        }

        void ShowSortGlyph()
        {
            for (int i = 0; i < grid.Columns.Count; i++)
            {
                DataGridViewColumn c = grid.Columns[i];
                if (i == sortCol)
                    c.HeaderCell.SortGlyphDirection = sortAsc ? SortOrder.Ascending : SortOrder.Descending;
                else
                    c.HeaderCell.SortGlyphDirection = SortOrder.None;
            }
        }

        /// <summary>当前排序的可读描述（给查看窗口提示用）。</summary>
        public string SortDescription()
        {
            string name;
            switch (sortCol)
            {
                case ColTime: name = "时间"; break;
                case ColLevel: name = "等级"; break;
                case ColLayer: name = "层/重"; break;
                case ColComplexity: name = "复杂度"; break;
                case ColSeed: name = "种子"; break;
                case ColSummary: name = "摘要"; break;
                default: name = "#（编号）"; break;
            }
            return "按 " + name + (sortAsc ? " 升序" : " 降序");
        }

        void RefreshList()
        {
            string want = cboFilter == null || cboFilter.SelectedIndex <= 0 ? null : cboFilter.Text;
            shown.Clear();
            for (int i = 0; i < items.Count; i++)
            {
                HistoryItem it = items[i];
                if (want != null && it.LevelText != want) continue;
                shown.Add(it);
            }
            ApplySort();

            grid.SuspendLayout();
            try
            {
                grid.Rows.Clear();
                for (int i = 0; i < shown.Count; i++)
                {
                    HistoryItem it = shown[i];
                    bool zcMode = SeedDialog.Mode == 1;
                    string seedFull = it.Seed ?? "";
                    // 列表里也跟随种子模式：Zc 模式显示字母串，否则十进制
                    string seedBody = zcMode ? SeedDialog.DecimalToZc(seedFull) : seedFull;
                    string seedShort = seedBody.Length <= 14 ? seedBody : seedBody.Substring(0, 12) + "…";
                    int rowIdx = grid.Rows.Add(
                        false,
                        it.Seq.ToString(CultureInfo.InvariantCulture),
                        it.Time.ToString("MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                        it.LevelText,
                        it.LayerText,
                        it.Complexity.ToString(CultureInfo.InvariantCulture),
                        seedShort,
                        it.Summary(200),
                        "查看");
                    // 种子太长（L5 是 64 位）：列表里截断，悬停看完整值（按当前模式显示）
                    grid.Rows[rowIdx].Cells[ColSeed].ToolTipText = "种子：" + seedBody;
                }
                grid.ClearSelection();
            }
            finally { grid.ResumeLayout(); }
            ShowSortGlyph();
            UpdateCount();
        }

        int CountChecked()
        {
            int n = 0;
            for (int i = 0; i < grid.Rows.Count; i++)
            {
                object v = grid.Rows[i].Cells[ColCheck].Value;
                if (v is bool && (bool)v) n++;
            }
            return n;
        }

        void UpdateCount()
        {
            try
            {
                if (lblCount == null || grid == null || grid.IsDisposed) return;
                int sel = grid.SelectedRows.Count;
                lblCount.Text = "共 " + items.Count.ToString(CultureInfo.InvariantCulture) + " 条"
                    + (shown.Count == items.Count ? "" : "（当前筛选 " + shown.Count.ToString(CultureInfo.InvariantCulture) + " 条）")
                    + " · 勾选 " + CountChecked().ToString(CultureInfo.InvariantCulture) + " 条"
                    + " · 当前选中 " + sel.ToString(CultureInfo.InvariantCulture) + " 条";
            }
            catch (Exception) { }
        }

        List<HistoryItem> CheckedItems()
        {
            List<HistoryItem> sel = new List<HistoryItem>();
            for (int i = 0; i < grid.Rows.Count; i++)
            {
                object v = grid.Rows[i].Cells[ColCheck].Value;
                if (v is bool && (bool)v)
                {
                    HistoryItem it = ItemAt(i);
                    if (it != null) sel.Add(it);
                }
            }
            return sel;
        }

        HistoryItem CurrentItem()
        {
            try
            {
                if (grid.SelectedRows.Count > 0)
                {
                    int idx = grid.SelectedRows[0].Index;
                    HistoryItem it = ItemAt(idx);
                    if (it != null) return it;
                }
            }
            catch (Exception) { }
            List<HistoryItem> c = CheckedItems();
            return c.Count > 0 ? c[0] : null;
        }

        void SetAllChecked(bool on)
        {
            for (int i = 0; i < grid.Rows.Count; i++) grid.Rows[i].Cells[ColCheck].Value = on;
            UpdateCount();
        }

        void InvertChecked()
        {
            for (int i = 0; i < grid.Rows.Count; i++)
            {
                object v = grid.Rows[i].Cells[ColCheck].Value;
                grid.Rows[i].Cells[ColCheck].Value = !(v is bool && (bool)v);
            }
            UpdateCount();
        }

        void ViewItem(HistoryItem it)
        {
            if (it == null)
            {
                MessageBox.Show(this, "先选中一条记录。", "生成历史", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            // 传整个当前列表，窗口里才能按列表顺序翻上/下一条
            int idx = shown.IndexOf(it);
            using (HistoryPreviewForm dlg = new HistoryPreviewForm(skin, shown, idx < 0 ? 0 : idx, SortDescription(),
                favorites == null ? (Action<HistoryItem>)null : delegate(HistoryItem x)
                {
                    HistoryStore.Favorite(favorites, new List<HistoryItem> { x });
                    if (onChanged != null) onChanged();
                }))
            {
                dlg.ShowDialog(this);
            }
        }

        void CopyCurrent()
        {
            HistoryItem it = CurrentItem();
            if (it == null) return;
            try { Clipboard.SetText(it.Latex ?? ""); }
            catch (Exception) { }
            lblCount.Text = "已复制第 " + it.Seq.ToString(CultureInfo.InvariantCulture) + " 条的 LaTeX";
        }

        /// <summary>复制某条记录的种子（十进制原值，不带空格换行）。</summary>
        void CopySeedOf(HistoryItem it)
        {
            if (it == null || string.IsNullOrEmpty(it.Seed)) return;
            try { Clipboard.SetText(it.Seed); }
            catch (Exception) { }
            lblCount.Text = "已复制第 " + it.Seq.ToString(CultureInfo.InvariantCulture) + " 条的种子（"
                + it.Seed.Length.ToString(CultureInfo.InvariantCulture) + " 位）";
        }

        void SaveCurrentPng()
        {
            HistoryItem it = CurrentItem();
            if (it == null) return;
            using (SaveFileDialog dlg = new SaveFileDialog())
            {
                dlg.Filter = "PNG 图片|*.png";
                dlg.FileName = "history-" + it.Seq.ToString(CultureInfo.InvariantCulture) + ".png";
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                string err;
                if (!MathExport.SavePng(it.Latex, skin.OutputInk, skin.OutputBack, dlg.FileName, out err))
                {
                    MessageBox.Show(this, "保存失败：" + err, "历史记录", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                lblCount.Text = "已保存：" + Path.GetFileName(dlg.FileName);
            }
        }

        void DeleteSelected()
        {
            List<HistoryItem> sel = CheckedItems();
            if (sel.Count == 0)
            {
                try
                {
                    for (int i = 0; i < grid.SelectedRows.Count; i++)
                    {
                        HistoryItem it = ItemAt(grid.SelectedRows[i].Index);
                        if (it != null) sel.Add(it);
                    }
                }
                catch (Exception) { }
            }
            if (sel.Count == 0)
            {
                MessageBox.Show(this, "先勾选或选中要删除的记录。", "生成历史", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            int n = HistoryStore.Remove(items, sel);
            if (onChanged != null) { try { onChanged(); } catch (Exception) { } }
            RefreshList();
            lblCount.Text = (favoritesMode ? "已取消收藏 " : "已删除 ") + n.ToString(CultureInfo.InvariantCulture) + " 条 · 剩余 "
                + items.Count.ToString(CultureInfo.InvariantCulture) + " 条";
        }

        void ClearAll()
        {
            if (items.Count == 0) return;
            if (MessageBox.Show(this, "清空全部 " + items.Count.ToString(CultureInfo.InvariantCulture)
                    + (favoritesMode ? " 条收藏？" : " 条历史？"),
                    favoritesMode ? "我的收藏" : "生成历史",
                    MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;
            items.Clear();
            if (onChanged != null) { try { onChanged(); } catch (Exception) { } }
            RefreshList();
        }

        /// <summary>自检：行数、行高、列宽、勾选/反选、图标。</summary>
        public string RunSelfCheck()
        {
            StringBuilder sb = new StringBuilder();
            try
            {
                int n0 = grid.Rows.Count;
                // 筛选功能自检：切到 L4 只剩 L4，切回「全部」恢复
                int l4Rows = -1;
                int backRows = -1;
                if (cboFilter != null && cboFilter.Items.Count == 6)
                {
                    cboFilter.SelectedIndex = 4;
                    l4Rows = grid.Rows.Count;
                    cboFilter.SelectedIndex = 0;
                    backRows = grid.Rows.Count;
                }
                SetAllChecked(true);
                int c1 = CountChecked();
                InvertChecked();
                int c2 = CountChecked();
                // 默认排序：编号升序 → 1 号在最上
                int firstSeq = shown.Count > 0 ? shown[0].Seq : -1;
                int lastSeq = shown.Count > 0 ? shown[shown.Count - 1].Seq : -1;
                // 点「复杂度」表头：第一次升序，再点一次降序
                SortBy(ColComplexity);
                int cAsc0 = shown.Count > 0 ? shown[0].Complexity : -1;
                int cAsc1 = shown.Count > 0 ? shown[shown.Count - 1].Complexity : -1;
                SortBy(ColComplexity);
                int cDesc0 = shown.Count > 0 ? shown[0].Complexity : -1;
                int cDesc1 = shown.Count > 0 ? shown[shown.Count - 1].Complexity : -1;
                // 非编号排序下翻页也要对：此刻是「复杂度 降序」，预览应从第 1 条翻到第 2 条
                bool navSorted = false;
                if (shown.Count >= 2)
                {
                    using (HistoryPreviewForm pv = new HistoryPreviewForm(skin, shown, 0, SortDescription()))
                    {
                        navSorted = pv.Step(1) && pv.Index == 1;
                    }
                }
                SortBy(ColSeq);        // 复位成默认
                int backFirst = shown.Count > 0 ? shown[0].Seq : -1;
                HistoryItem cur = CurrentItem();
                StringBuilder cols = new StringBuilder();
                int[] want = { ColSeq, ColTime, ColLevel, ColLayer, ColComplexity, ColSummary };
                for (int i = 0; i < want.Length; i++)
                {
                    if (i > 0) cols.Append(',');
                    cols.Append(grid.Columns[want[i]].Width.ToString(CultureInfo.InvariantCulture));
                }
                sb.AppendLine("HISTORY_FORM_SCALE=" + scale.ToString("0.00", CultureInfo.InvariantCulture));
                sb.AppendLine("HISTORY_FORM_ROWHEIGHT=" + grid.RowTemplate.Height.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("HISTORY_FORM_COLS=" + cols.ToString());
                sb.AppendLine("HISTORY_FORM_LIST_BOUNDS=" + grid.Left + "," + grid.Top + "," + grid.Width + "," + grid.Height);
                sb.AppendLine("HISTORY_FORM_ITEMS=" + n0.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("HISTORY_FORM_CHECK_ALL=" + c1.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("HISTORY_FORM_INVERT=" + c2.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("HISTORY_FORM_CURRENT=" + (cur == null ? "" : cur.Seq.ToString(CultureInfo.InvariantCulture)));
                sb.AppendLine("HISTORY_FORM_VIEWCOL=" + (grid.Columns.Count == 9 ? "1" : "0"));
                sb.AppendLine("HISTORY_FORM_SEEDCOL=" + (grid.Columns.Count > ColSeed && grid.Columns[ColSeed].HeaderText == "种子" ? "1" : "0"));
                string seedSample = "";
                if (grid.Rows.Count > 0) seedSample = Convert.ToString(grid.Rows[0].Cells[ColSeed].Value, CultureInfo.InvariantCulture);
                sb.AppendLine("HISTORY_FORM_SEEDSAMPLE=" + seedSample);
                sb.AppendLine("HISTORY_FORM_MODE=" + (favoritesMode ? "favorites" : "history"));
                sb.AppendLine("HISTORY_FORM_NONE=" + (btnNone != null && btnNone.Text.Length > 0 ? "1" : "0"));
                sb.AppendLine("HISTORY_FORM_FAVBTN=" + (btnFav != null && btnFav.Text == "收藏选中" ? "1" : "0"));
                sb.AppendLine("HISTORY_FORM_FILTER=" + (cboFilter != null && cboFilter.Items.Count == 6 ? "1" : "0"));
                sb.AppendLine("HISTORY_FORM_ICON=" + (Icon != null && Icon.Width > 0 ? "1" : "0"));
                sb.AppendLine("HISTORY_FORM_FILTER_L4=" + l4Rows.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("HISTORY_FORM_FILTER_BACK=" + backRows.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("HISTORY_FORM_SORT_FIRST=" + firstSeq.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("HISTORY_FORM_SORT_LAST=" + lastSeq.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("HISTORY_FORM_SORT_ASC=" + cAsc0.ToString(CultureInfo.InvariantCulture) + "," + cAsc1.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("HISTORY_FORM_SORT_DESC=" + cDesc0.ToString(CultureInfo.InvariantCulture) + "," + cDesc1.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("HISTORY_FORM_SORT_BACK=" + backFirst.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("HISTORY_FORM_NAV_SORTED=" + (navSorted ? "1" : "0"));
                sb.AppendLine("HISTORY_FORM_SORT_LABEL=" + SortDescription());
                sb.AppendLine("HISTORY_FORM_OK=" + (n0 > 0 && c1 == n0 && c2 == 0
                    && backRows == n0 && l4Rows >= 0 && l4Rows <= n0 && grid.Columns.Count == 9 && navSorted ? "1" : "0"));
            }
            catch (Exception ex)
            {
                sb.AppendLine("HISTORY_FORM_OK=0");
                sb.AppendLine("HISTORY_FORM_ERR=" + ex.GetType().Name + ": " + ex.Message);
            }
            return sb.ToString();
        }
    }

    /// <summary>把一条历史记录加载到新窗口里查看（可缩放、复制）。</summary>
    /// <summary>
    /// 单条查看窗口：左右两端「上一条 / 下一条」按当前列表顺序翻页（不是按编号跳），
    /// 同时支持 ←/→ 方向键；中间是可缩放的大公式区。
    /// </summary>
    sealed class HistoryPreviewForm : Form
    {
        readonly Skin skin;
        readonly List<HistoryItem> list;
        int index;
        MathBox mathBox;
        ChamferButton btnPrev, btnNext;
        ChamferButton btnZoomIn, btnZoomOut, btnZoomReset, btnCopy, btnCopySeed, btnFav, btnSavePng, btnClose;
        TextBox txtSeed;
        Label lblSeedCap;
        readonly Action<HistoryItem> onFavorite;
        Label lblMeta;
        Label lblPos;
        Label lblSort;
        readonly string sortDesc;
        float scale = 1f;

        public HistoryPreviewForm(Skin s, HistoryItem it)
            : this(s, it == null ? new List<HistoryItem>() : new List<HistoryItem> { it }, 0)
        {
        }

        public HistoryPreviewForm(Skin s, List<HistoryItem> list, int index)
            : this(s, list, index, null)
        {
        }

        /// <summary>sortDesc：当前列表的排序方式，显示成一行小字提醒（翻页按这个顺序走）。</summary>
        public HistoryPreviewForm(Skin s, List<HistoryItem> list, int index, string sortDesc)
            : this(s, list, index, sortDesc, null)
        {
        }

        /// <summary>onFavorite：点「收藏」时回调（为空则按钮禁用）。</summary>
        public HistoryPreviewForm(Skin s, List<HistoryItem> list, int index, string sortDesc,
                                   Action<HistoryItem> onFavorite)
        {
            skin = s ?? Skins.Plain;
            this.sortDesc = sortDesc;
            this.onFavorite = onFavorite;
            this.list = list ?? new List<HistoryItem>();
            this.index = Math.Max(0, Math.Min(index, Math.Max(0, this.list.Count - 1)));

            FormBorderStyle = FormBorderStyle.FixedDialog;   // 固定尺寸，不能拉伸
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = true;
            ShowInTaskbar = false;
            AutoScaleMode = AutoScaleMode.None;
            BackColor = skin.FormBack;
            ForeColor = skin.Ink;
            Font = new Font("Microsoft YaHei UI", 10.5f, FontStyle.Regular);
            KeyPreview = true;
            AppIcon.Apply(this);

            try
            {
                using (Graphics g = CreateGraphics()) scale = g.DpiX / 96f;
            }
            catch (Exception) { }
            if (scale < 0.75f) scale = 0.75f;
            if (scale > 3f) scale = 3f;

            Size = new Size(1600, 900);                  // 整个窗口（含边框）固定 1600×900
            MinimumSize = Size;
            MaximumSize = Size;

            ChamferPanel card = new ChamferPanel();
            card.Skin = skin;
            card.Corner = R(8);
            card.BackColor = Color.Transparent;
            Controls.Add(card);

            mathBox = new MathBox();
            mathBox.BackColor = skin.OutputBack;
            mathBox.Ink = skin.OutputInk;
            card.Controls.Add(mathBox);

            lblMeta = new Label();
            lblMeta.ForeColor = skin.Muted;
            Controls.Add(lblMeta);

            lblSort = new Label();
            lblSort.ForeColor = skin.Muted;
            lblSort.Font = new Font("Microsoft YaHei UI", 9f);
            lblSort.TextAlign = ContentAlignment.MiddleLeft;
            lblSort.Text = string.IsNullOrEmpty(sortDesc)
                ? "上一条 / 下一条按列表当前顺序翻页"
                : "当前列表排序：" + sortDesc + "；上一条 / 下一条按此顺序翻页";
            Controls.Add(lblSort);

            lblPos = new Label();
            lblPos.ForeColor = skin.Accent;
            lblPos.Font = new Font("Microsoft YaHei UI", 10.5f, FontStyle.Bold);
            lblPos.TextAlign = ContentAlignment.MiddleRight;
            Controls.Add(lblPos);

            // 种子：与主窗口同样的显示规则（Zc 模式下用字母串，每 32 位一行）
            lblSeedCap = new Label();
            lblSeedCap.Text = "种子";
            lblSeedCap.TextAlign = ContentAlignment.MiddleLeft;
            lblSeedCap.ForeColor = skin.Muted;
            lblSeedCap.Font = new Font("Microsoft YaHei UI", 9.5f);
            Controls.Add(lblSeedCap);

            txtSeed = new TextBox();
            txtSeed.Multiline = true;
            txtSeed.ReadOnly = true;
            txtSeed.WordWrap = false;
            txtSeed.ScrollBars = ScrollBars.Vertical;
            txtSeed.BorderStyle = BorderStyle.FixedSingle;
            txtSeed.BackColor = skin.FieldBack;
            txtSeed.ForeColor = skin.FieldFore;
            txtSeed.Font = new Font("Consolas", 10f);
            Controls.Add(txtSeed);

            // 最左 / 最右：上一条 / 下一条
            btnPrev = MakeNavButton("上\n一\n条");
            btnPrev.Click += delegate { Step(-1); };
            Controls.Add(btnPrev);
            btnNext = MakeNavButton("下\n一\n条");
            btnNext.Click += delegate { Step(1); };
            Controls.Add(btnNext);

            btnZoomOut = MakeButton("缩小");
            btnZoomOut.Click += delegate { mathBox.ZoomBy(0.8f); };
            Controls.Add(btnZoomOut);
            btnZoomIn = MakeButton("放大");
            btnZoomIn.Click += delegate { mathBox.ZoomBy(1.25f); };
            Controls.Add(btnZoomIn);
            btnZoomReset = MakeButton("复位");
            btnZoomReset.Click += delegate { mathBox.ResetView(); };
            Controls.Add(btnZoomReset);

            // 右下三个常用按钮：复制 / 收藏 / 关闭（主题色描边强调）
            btnCopy = MakeButton("复制");
            btnCopy.Emphasize = true;
            btnCopy.Click += delegate { TryCopy(Current == null ? "" : Current.Latex); };
            Controls.Add(btnCopy);
            btnFav = MakeButton("收藏");
            btnFav.Emphasize = true;
            btnFav.Enabled = onFavorite != null;
            btnFav.Click += delegate { FavCurrent(); };
            Controls.Add(btnFav);
            btnClose = MakeButton("关闭");
            btnClose.Emphasize = true;
            btnClose.Click += delegate { Close(); };
            Controls.Add(btnClose);
            // 保存图片放左侧（次要操作）
            btnSavePng = MakeButton("存图");
            btnSavePng.Click += delegate { SaveItemPng(Current); };
            Controls.Add(btnSavePng);
            btnCopySeed = MakeButton("复制种子");
            btnCopySeed.Click += delegate { CopySeed(); };
            Controls.Add(btnCopySeed);
            KeyDown += delegate(object o, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Escape) { Close(); e.Handled = true; }
                else if (e.KeyCode == Keys.Left) { Step(-1); e.Handled = true; }
                else if (e.KeyCode == Keys.Right) { Step(1); e.Handled = true; }
            };
            Resize += delegate { LayoutControls(); };
            AppIcon.Apply(this);
            ThemedCaption.Hook(this, skin);
            ApplyCurrent();
            LayoutControls();
        }

        // 这个窗口是**固定 1600×900 物理像素**，布局直接按像素算，
        // 不再乘 DPI 系数（乘了会让按钮按 1.5 倍放大，在固定窗宽里挤成一团、右侧被裁掉）。
        int R(float v) { return (int)Math.Round(v); }

        HistoryItem Current
        {
            get { return index >= 0 && index < list.Count ? list[index] : null; }
        }

        public int Index { get { return index; } }
        public int Count { get { return list.Count; } }

        /// <summary>按当前列表顺序前后翻一条（越界不动）。</summary>
        public bool Step(int delta)
        {
            int next = index + delta;
            if (next < 0 || next >= list.Count || next == index) return false;
            index = next;
            ApplyCurrent();
            return true;
        }

        void ApplyCurrent()
        {
            HistoryItem it = Current;
            Text = it == null ? "历史记录" : "历史记录 #" + it.Seq.ToString(CultureInfo.InvariantCulture);
            if (it == null)
            {
                mathBox.SetLatex("\\text{没有可显示的记录}");
                lblMeta.Text = "";
                lblPos.Text = "";
                txtSeed.Text = "";
                btnCopySeed.Enabled = false;
            }
            else
            {
                mathBox.SetLatex(it.Latex);
                lblMeta.Text = "第 " + it.Seq.ToString(CultureInfo.InvariantCulture) + " 次 · "
                    + it.Time.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " · "
                    + it.LevelText + " " + it.LayerText
                    + " · 类型 " + (it.Codes ?? "") + " · 复杂度 "
                    + it.Complexity.ToString(CultureInfo.InvariantCulture);
                lblPos.Text = "第 " + (index + 1).ToString(CultureInfo.InvariantCulture) + " / "
                    + list.Count.ToString(CultureInfo.InvariantCulture) + " 条";
                // 与主窗口同一套规则：Zc 模式下显示字母串，每 32 位一行
                bool zc = SeedDialog.Mode == 1;
                string body = zc ? SeedDialog.DecimalToZc(it.Seed ?? "") : (it.Seed ?? "");
                txtSeed.Text = RandomExpr.FormatSeedWrapped(body, 32, zc);
                txtSeed.SelectionStart = 0;
                btnCopySeed.Enabled = !string.IsNullOrEmpty(it.Seed);
            }
            UpdateNavEnabled();
        }

        /// <summary>复制当前这条的种子（十进制原值，不含空格/换行）。</summary>
        void CopySeed()
        {
            HistoryItem it = Current;
            if (it == null || string.IsNullOrEmpty(it.Seed)) return;
            try { Clipboard.SetText(it.Seed); }
            catch (Exception) { }
            lblMeta.Text = lblMeta.Text + "  ·  种子已复制";
        }

        void UpdateNavEnabled()
        {
            if (btnPrev != null) btnPrev.Enabled = index > 0;
            if (btnNext != null) btnNext.Enabled = index < list.Count - 1;
        }

        ChamferButton MakeButton(string text)
        {
            ChamferButton b = new ChamferButton();
            b.Text = text;
            b.ApplySkin(skin);
            b.Cut = R(6);
            b.Font = new Font("Microsoft YaHei UI", 10.5f, FontStyle.Regular);
            return b;
        }

        /// <summary>左右翻页按钮：竖长条，三个字竖排放正中（ChamferButton 支持多行居中）。</summary>
        ChamferButton MakeNavButton(string text)
        {
            ChamferButton b = new ChamferButton();
            b.Text = text;
            b.ApplySkin(skin);
            b.Cut = R(10);
            b.Font = new Font("Microsoft YaHei UI", 13f, FontStyle.Bold);
            return b;
        }

        void FavCurrent()
        {
            HistoryItem it = Current;
            if (it == null || onFavorite == null) return;
            try
            {
                onFavorite(it);
                lblMeta.Text = "已加入收藏";
            }
            catch (Exception) { }
        }

        void TryCopy(string text)
        {
            try { Clipboard.SetText(text ?? ""); }
            catch (Exception) { }
        }

        void SaveItemPng(HistoryItem it)
        {
            if (it == null) return;
            using (SaveFileDialog dlg = new SaveFileDialog())
            {
                dlg.Filter = "PNG 图片|*.png";
                dlg.FileName = "history-" + it.Seq.ToString(CultureInfo.InvariantCulture) + ".png";
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                string err;
                if (!MathExport.SavePng(it.Latex, skin.OutputInk, skin.OutputBack, dlg.FileName, out err))
                {
                    MessageBox.Show(this, "保存失败：" + err, "历史记录", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                lblMeta.Text = lblMeta.Text + "  ·  已保存 " + Path.GetFileName(dlg.FileName);
            }
        }

        void LayoutControls()
        {
            int pad = R(20);
            int gap = R(10);
            int barH = R(40);
            int by = ClientSize.Height - pad - barH;

            // 左右翻页按钮：贴边、竖直居中
            int navW = R(58);
            int navH = R(240);
            int navY = Math.Max(R(70), (by - pad - navH) / 2);
            btnPrev.Bounds = new Rectangle(pad, navY, navW, navH);
            btnNext.Bounds = new Rectangle(ClientSize.Width - pad - navW, navY, navW, navH);

            // 底部两组的宽度先算出来，保证左边不会撞到右边
            int wZoom = R(84), wSave = R(132), wSeed = R(104);
            int wCopy = R(112), wFav = R(84), wClose = R(88);
            int leftGroupW = wZoom + gap + wZoom + gap + wZoom + gap + wSave + gap + wSeed;
            int rightGroupW = wCopy + gap + wFav + gap + wClose;
            int leftStart = btnPrev.Right + R(12);
            int rightEnd = btnNext.Left - R(12);
            // 窗口太窄时（理论上不会）缩一点保存/复制的宽度，避免重叠
            while (leftStart + leftGroupW + R(24) > rightEnd - rightGroupW && wSave > R(90))
            {
                wSave -= R(8); leftGroupW -= R(8);
            }

            ChamferPanel card = null;
            for (int i = 0; i < Controls.Count; i++) { card = Controls[i] as ChamferPanel; if (card != null) break; }
            if (card != null)
            {
                int left = leftStart;
                int right = rightEnd;
                lblSort.Bounds = new Rectangle(left, R(4), Math.Max(R(200), right - left), R(20));

                // 种子行：标签 + 两行高的只读框（不要占太大地方）
                int seedH = R(46);
                int seedTop = by - gap - seedH;
                lblSeedCap.Bounds = new Rectangle(left, seedTop, R(44), seedH);
                int seedW = Math.Min(R(560), Math.Max(R(220), right - left - R(52)));
                txtSeed.Bounds = new Rectangle(left + R(50), seedTop, seedW, seedH);

                // 公式卡占剩下的空间
                int cardBottom = seedTop - gap;
                card.Bounds = new Rectangle(left, pad + R(22), Math.Max(R(320), right - left),
                    Math.Max(R(200), cardBottom - (pad + R(22)) - R(30)));
                int m = R(18);
                mathBox.Bounds = new Rectangle(m, m, Math.Max(R(200), card.Width - m * 2),
                    Math.Max(R(150), card.Height - m * 2));

                // 元信息左对齐铺满，位置计数右对齐
                lblPos.Bounds = new Rectangle(Math.Max(left, ClientSize.Width - pad - R(200)), card.Bottom + R(4), R(190), R(24));
                lblMeta.Bounds = new Rectangle(left, card.Bottom + R(4),
                    Math.Max(R(200), lblPos.Left - left - R(10)), R(24));
            }

            // 左下：缩小 / 放大 / 复位 / 保存图片 / 复制种子（依序排，绝不叠字）
            int x = leftStart;
            btnZoomOut.Bounds = new Rectangle(x, by, wZoom, barH); x = btnZoomOut.Right + gap;
            btnZoomIn.Bounds = new Rectangle(x, by, wZoom, barH); x = btnZoomIn.Right + gap;
            btnZoomReset.Bounds = new Rectangle(x, by, wZoom, barH); x = btnZoomReset.Right + gap;
            btnSavePng.Bounds = new Rectangle(x, by, wSave, barH); x = btnSavePng.Right + gap;
            btnCopySeed.Bounds = new Rectangle(x, by, wSeed, barH);

            // 右下：复制 LaTeX / 收藏 / 关闭（从右往左排）
            btnClose.Bounds = new Rectangle(rightEnd - wClose, by, wClose, barH);
            btnFav.Bounds = new Rectangle(btnClose.Left - gap - wFav, by, wFav, barH);
            btnCopy.Bounds = new Rectangle(btnFav.Left - gap - wCopy, by, wCopy, barH);
        }
        int leftOfBottomBar()
        {
            return btnPrev != null ? btnPrev.Right + R(12) : R(20);
        }

        /// <summary>自检：按列表顺序前后翻页（不依赖真实点击）。</summary>
        public string RunNavSelfCheck()
        {
            StringBuilder sb = new StringBuilder();
            try
            {
                int start = index;
                bool nextOk = Count < 2 ? true : Step(1);
                int afterNext = index;
                bool prevOk = Count < 2 ? true : Step(-1);
                int afterPrev = index;
                // 跳到末尾再点下一条应无效
                index = Count - 1;
                bool atEndBlocked = !Step(1);
                index = start;
                ApplyCurrent();
                sb.AppendLine("PREVIEW_COUNT=" + Count.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("PREVIEW_NEXT=" + (nextOk && afterNext == Math.Min(start + 1, Count - 1) ? "1" : "0"));
                sb.AppendLine("PREVIEW_PREV=" + (prevOk && afterPrev == afterNext - 1 ? "1" : "0"));
                sb.AppendLine("PREVIEW_END_BLOCKED=" + (atEndBlocked ? "1" : "0"));
                sb.AppendLine("PREVIEW_BIG=" + (Width >= 1200 ? "1" : "0"));
                sb.AppendLine("PREVIEW_SIZE=" + Width.ToString(CultureInfo.InvariantCulture)
                    + "x" + Height.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("PREVIEW_FIXED=" + (MinimumSize == MaximumSize && Width == 1600 && Height == 900 ? "1" : "0"));
                sb.AppendLine("PREVIEW_RIGHT_BTNS=" + (btnCopy.Emphasize && btnFav.Emphasize && btnClose.Emphasize
                    && btnCopy.Left < btnFav.Left && btnFav.Left < btnClose.Left ? "1" : "0"));
                sb.AppendLine("PREVIEW_FAV_BTN=" + (btnFav.Text == "收藏" ? "1" : "0"));
                sb.AppendLine("PREVIEW_COPYSEED=" + (btnCopySeed != null && btnCopySeed.Text == "复制种子" ? "1" : "0"));
                sb.AppendLine("PREVIEW_SHORT_LABELS=" + (btnCopy.Text == "复制" && btnSavePng.Text == "存图" ? "1" : "0"));
                bool noOverlap = btnCopySeed.Right + 4 <= btnCopy.Left
                    && btnClose.Right <= ClientSize.Width && btnPrev.Left >= 0 && btnNext.Right <= ClientSize.Width;
                sb.AppendLine("PREVIEW_NO_OVERLAP=" + (noOverlap ? "1" : "0"));
                sb.AppendLine("PREVIEW_SEEDBOX=" + txtSeed.Width.ToString(CultureInfo.InvariantCulture) + "x"
                    + txtSeed.Height.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("PREVIEW_BTN_X=" + btnCopySeed.Left.ToString(CultureInfo.InvariantCulture) + ","
                    + btnCopySeed.Right.ToString(CultureInfo.InvariantCulture) + ","
                    + btnCopy.Left.ToString(CultureInfo.InvariantCulture) + ","
                    + btnClose.Right.ToString(CultureInfo.InvariantCulture));
                // 与主窗口同一规则：Zc 模式用字母串，每 32 位一行、每 4 位空格
                string seedFlat = (txtSeed.Text ?? "").Replace("\r", "").Replace("\n", "").Replace(" ", "").Trim();
                bool zcMode = SeedDialog.Mode == 1;
                string seedBody = Current == null ? ""
                    : (zcMode ? SeedDialog.DecimalToZc(Current.Seed ?? "") : (Current.Seed ?? ""));
                string seedExpect = RandomExpr.FormatSeedWrapped(seedBody, 32, zcMode)
                    .Replace("\r", "").Replace("\n", "").Replace(" ", "");
                sb.AppendLine("PREVIEW_SEED_LEN=" + seedFlat.Length.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("PREVIEW_SEED_MIN=" + seedExpect.Length.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("PREVIEW_SEED_OK=" + (seedFlat == seedExpect ? "1" : "0"));
                sb.AppendLine("PREVIEW_NAV_SHAPE=" + (btnPrev != null && btnPrev.Width < btnPrev.Height && btnNext.Width < btnNext.Height ? "1" : "0"));
                sb.AppendLine("PREVIEW_NAV_OK=" + (Count == 0
                    || (nextOk && afterNext == Math.Min(start + 1, Count - 1)
                        && prevOk && afterPrev == afterNext - 1 && atEndBlocked) ? "1" : "0"));
            }
            catch (Exception ex)
            {
                sb.AppendLine("PREVIEW_NAV_OK=0");
                sb.AppendLine("PREVIEW_ERR=" + ex.GetType().Name + ": " + ex.Message);
            }
            return sb.ToString();
        }
    }
}
