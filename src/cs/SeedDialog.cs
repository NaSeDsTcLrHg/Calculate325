/*
 * Calculate325 —— 自定义种子窗口（“自定义”按钮弹出）
 * ---------------------------------------------------------------------------
 * 两种等价的输入方式：
 *   数字模式：直接输入十进制数字串（空格忽略），和原来完全一样。
 *   Zc 模式 ：用 Z/C 两个字母交替书写，靠大小写表示 0/1——
 *             第 0 位永远是 Z 开头，之后按 Z、C、Z、C… 交替；
 *             大写 = 1，小写 = 0。
 *             例：ZcZczczC = 10100001，ZczcZCzCzC = 1000110101。
 * 两种模式都只是“换个写法”：Zc 串先按二进制转成大整数，再转成十进制数字串，
 * 之后走与数字模式完全相同的生成/映射路径。
 *
 * 预览窗格按“每行 32 位”换行，高度足够一次看全（L5 的 64 位就是两行）。
 */
using System;
using System.Drawing;
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Windows.Forms;

namespace Calculate325
{
    sealed class SeedDialog : Form
    {
        readonly Skin skin;
        readonly int level;
        readonly RadioButton rbDecimal = new RadioButton();
        readonly RadioButton rbBinary = new RadioButton();
        readonly TextBox input = new TextBox();
        readonly TextBox preview = new TextBox();
        readonly Label hint = new Label();
        readonly ChamferButton btnOk = new ChamferButton();
        readonly ChamferButton btnCancel = new ChamferButton();
        readonly ChamferButton btnRand = new ChamferButton();
        bool switching;

        const int WrapWidth = 32;

        // 记住用户选过的模式（0 = 数字，1 = Zc），落盘到 %APPDATA%\Calculate325\seedmode.txt
        static int modeCache = -1;

        public static int Mode
        {
            get
            {
                // 自检/截图可用环境变量强制指定模式
                string env = Environment.GetEnvironmentVariable("CALCULATE325_SEEDMODE");
                if (!string.IsNullOrEmpty(env)) return env.Trim() == "1" ? 1 : 0;
                if (modeCache < 0) modeCache = LoadMode();
                return modeCache;
            }
            set
            {
                modeCache = value == 1 ? 1 : 0;
                SaveMode(modeCache);
            }
        }

        /// <summary>
        /// 模式文件的候选路径：优先 %APPDATA%\Calculate325\seedmode.txt，
        /// 写不进去（受限环境）就退到 exe 同目录，保证任何情况下都能记住选择。
        /// 测试可用 CALCULATE325_SEEDMODE_FILE 指定唯一路径。
        /// </summary>
        static string[] ModePathList()
        {
            string o = Environment.GetEnvironmentVariable("CALCULATE325_SEEDMODE_FILE");
            if (!string.IsNullOrEmpty(o)) return new string[] { o };
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return new string[]
            {
                System.IO.Path.Combine(appData, "Calculate325", "seedmode.txt"),
                System.IO.Path.Combine(Application.StartupPath, "seedmode.txt")
            };
        }

        static string ModePath { get { return ModePathList()[0]; } }

        /// <summary>给自检用：真正会被读取/写入的那个文件（存在的优先）。</summary>
        public static string ModePathForTest()
        {
            foreach (string p in ModePathList())
            {
                try { if (System.IO.File.Exists(p)) return p; }
                catch (Exception) { }
            }
            return ModePath;
        }

        static int LoadMode()
        {
            foreach (string p in ModePathList())
            {
                try
                {
                    if (System.IO.File.Exists(p))
                    {
                        string s = System.IO.File.ReadAllText(p).Trim();
                        return s == "1" ? 1 : 0;
                    }
                }
                catch (Exception) { }
            }
            return 0;
        }

        /// <summary>写模式；返回是否落盘成功（第一个能写的路径胜出）。</summary>
        static bool SaveMode(int mode)
        {
            foreach (string p in ModePathList())
            {
                try
                {
                    string dir = System.IO.Path.GetDirectoryName(p);
                    if (!string.IsNullOrEmpty(dir) && !System.IO.Directory.Exists(dir))
                        System.IO.Directory.CreateDirectory(dir);
                    System.IO.File.WriteAllText(p, mode.ToString(CultureInfo.InvariantCulture));
                    return true;
                }
                catch (Exception) { }
            }
            return false;
        }

        /// <summary>确定后填出的十进制种子（只含数字）。</summary>
        public string DecimalSeed;

        public SeedDialog(Skin s, int level, string currentSeed)
        {
            skin = s ?? Skins.Plain;
            this.level = level < 1 ? 1 : (level > 5 ? 5 : level);
            int need = RandomExpr.SeedLength(this.level);

            Text = "自定义种子";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(660, 500);
            AppIcon.Apply(this);
            ThemedCaption.Hook(this, skin);
            BackColor = skin.FormBack;
            ForeColor = skin.Ink;

            Label title = new Label();
            title.Text = "自定义种子 · L" + this.level.ToString(CultureInfo.InvariantCulture)
                + " 需要 " + need.ToString(CultureInfo.InvariantCulture) + " 位（不足自动补 0）";
            title.AutoSize = false;
            title.Bounds = new Rectangle(18, 14, 620, 22);
            title.ForeColor = skin.Ink;
            Controls.Add(title);

            rbDecimal.Text = "数字模式（十进制）";
            rbDecimal.Bounds = new Rectangle(20, 46, 300, 24);
            rbDecimal.Checked = Mode == 0;
            rbBinary.Checked = Mode == 1;
            rbDecimal.ForeColor = skin.Ink;
            rbDecimal.BackColor = Color.Transparent;
            rbDecimal.CheckedChanged += delegate { OnModeChanged(); };
            Controls.Add(rbDecimal);

            rbBinary.Text = "Zc 模式（Z/C 交替，大写=1 小写=0）";
            rbBinary.Bounds = new Rectangle(20, 74, 420, 24);
            rbBinary.ForeColor = skin.Ink;
            rbBinary.BackColor = Color.Transparent;
            rbBinary.CheckedChanged += delegate { OnModeChanged(); };
            Controls.Add(rbBinary);

            input.Bounds = new Rectangle(20, 106, 620, 30);
            input.Font = new Font("Consolas", 12f);
            input.BackColor = skin.FieldBack;
            input.ForeColor = skin.FieldFore;
            // 打开时按记住的模式显示现有种子（Zc 模式下是字母串）
            input.Text = Mode == 1 ? DecimalToZc(currentSeed ?? "") : (currentSeed ?? "");
            input.TextChanged += delegate { RefreshPreview(); };
            Controls.Add(input);

            Label pl = new Label();
            pl.Text = "预览（每行 " + WrapWidth.ToString(CultureInfo.InvariantCulture) + " 位）";
            pl.Bounds = new Rectangle(20, 140, 620, 20);
            pl.ForeColor = skin.Muted;
            pl.Font = new Font("Microsoft YaHei UI", 9f);
            Controls.Add(pl);

            preview.Bounds = new Rectangle(20, 166, 620, 226);
            preview.Multiline = true;
            preview.ReadOnly = true;
            preview.ScrollBars = ScrollBars.Vertical;
            preview.WordWrap = false;
            preview.Font = new Font("Consolas", 11f);
            preview.BackColor = skin.FieldBack;
            preview.ForeColor = skin.FieldFore;
            preview.BorderStyle = BorderStyle.FixedSingle;
            Controls.Add(preview);

            hint.Bounds = new Rectangle(20, 402, 620, 22);
            hint.ForeColor = skin.Muted;
            hint.Font = new Font("Microsoft YaHei UI", 9.5f);
            Controls.Add(hint);

            btnRand.Text = "随机种子";
            btnRand.Bounds = new Rectangle(20, 440, 110, 38);
            btnRand.Click += delegate { FillRandom(); };
            Controls.Add(btnRand);

            btnOk.Text = "确定";
            btnOk.Emphasize = true;
            btnOk.Bounds = new Rectangle(436, 440, 96, 38);
            btnOk.Click += delegate { if (Commit()) { DialogResult = DialogResult.OK; Close(); } };
            Controls.Add(btnOk);

            btnCancel.Text = "取消";
            btnCancel.Bounds = new Rectangle(544, 440, 96, 38);
            btnCancel.Click += delegate { DialogResult = DialogResult.Cancel; Close(); };
            Controls.Add(btnCancel);

            AcceptButton = btnOk;
            CancelButton = btnCancel;
            RefreshPreview();
        }

        bool Binary { get { return rbBinary.Checked; } }

        /// <summary>供自检/截图用：切换模式并重新算一次预览。</summary>
        public void SetMode(bool zc)
        {
            rbDecimal.Checked = !zc;
            rbBinary.Checked = zc;
            RefreshPreview();
        }

        /// <summary>供自检用：当前预览文本。</summary>
        public string PreviewText { get { return preview.Text; } }

        /// <summary>随机种子：按当前模式生成一个合法种子（十进制 N 位；Zc 由 N 位十进制转来）。</summary>
        void FillRandom()
        {
            Random rnd = new Random(Guid.NewGuid().GetHashCode());
            int need = RandomExpr.SeedLength(level);
            StringBuilder d = new StringBuilder(need);
            d.Append((char)('0' + rnd.Next(1, 10)));
            for (int i = 1; i < need; i++) d.Append((char)('0' + rnd.Next(0, 10)));
            string dec = d.ToString();
            input.Text = Binary ? DecimalToZc(dec) : dec;
            RefreshPreview();
        }

        /// <summary>供自检/截图用：直接设置输入框内容。</summary>
        public void SetText(string text)
        {
            input.Text = text ?? "";
            RefreshPreview();
        }

        // ---------------- Zc 编解码 ----------------

        /// <summary>位串 → Zc 串：偶数位字母 Z、奇数位字母 C；大写=1，小写=0。</summary>
        public static string BitsToZc(string bits)
        {
            if (string.IsNullOrEmpty(bits)) return "";
            StringBuilder sb = new StringBuilder(bits.Length);
            for (int i = 0; i < bits.Length; i++)
            {
                char letter = (i % 2 == 0) ? 'Z' : 'C';
                bool one = bits[i] == '1';
                sb.Append(one ? letter : char.ToLowerInvariant(letter));
            }
            return sb.ToString();
        }

        /// <summary>Zc 串 → 位串。字母必须按 Z、C 交替（大小写不限），否则返回 null。</summary>
        public static string ZcToBits(string zc)
        {
            if (string.IsNullOrEmpty(zc)) return null;
            StringBuilder sb = new StringBuilder(zc.Length);
            for (int i = 0; i < zc.Length; i++)
            {
                char c = zc[i];
                char want = (i % 2 == 0) ? 'Z' : 'C';
                char upper = char.ToUpperInvariant(c);
                if (upper != want) return null;
                sb.Append(char.IsUpper(c) ? '1' : '0');
            }
            return sb.ToString();
        }

        /// <summary>把输入框内容按当前模式转成十进制数字串；非法返回 null。</summary>
        public static string ConvertText(string text, bool binary)
        {
            string body = Strip(text, binary);
            if (string.IsNullOrEmpty(body)) return null;
            if (!binary) return body;
            string bits = ZcToBits(body);
            if (bits == null) return null;
            BigInteger v = BigInteger.Zero;
            for (int i = 0; i < bits.Length; i++) v = (v << 1) + (bits[i] == '1' ? 1 : 0);
            return v.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>十进制数字串 → Zc 串（用于切换模式时保持内容）。</summary>
        public static string DecimalToZc(string digits)
        {
            if (string.IsNullOrEmpty(digits)) return "";
            BigInteger v;
            if (!BigInteger.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out v)) return "";
            if (v.Sign <= 0) return BitsToZc("0");
            StringBuilder bits = new StringBuilder();
            while (v.Sign > 0)
            {
                bits.Insert(0, (v & 1) == 1 ? '1' : '0');
                v >>= 1;
            }
            return BitsToZc(bits.ToString());
        }

        /// <summary>按固定宽度折行。</summary>
        public static string Wrap(string text, int width)
        {
            if (string.IsNullOrEmpty(text)) return "";
            if (width < 1) width = 1;
            StringBuilder sb = new StringBuilder(text.Length + text.Length / width + 1);
            for (int i = 0; i < text.Length; i++)
            {
                if (i > 0 && i % width == 0) sb.Append("\r\n");
                sb.Append(text[i]);
            }
            return sb.ToString();
        }

        /// <summary>只保留当前模式允许的字符（用于把半成品也显示出来）。</summary>
        public static string Strip(string text, bool binary)
        {
            if (text == null) return "";
            StringBuilder sb = new StringBuilder(text.Length);
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (binary)
                {
                    if (c == 'Z' || c == 'C' || c == 'z' || c == 'c') sb.Append(c);
                }
                else if (c >= '0' && c <= '9') sb.Append(c);
            }
            return sb.ToString();
        }

        // ---------------- 界面联动 ----------------

        void OnModeChanged()
        {
            if (switching) return;
            switching = true;
            try
            {
                // 立刻记住选择（不用等按「确定」）：重启后仍是这个模式
                Mode = rbBinary.Checked ? 1 : 0;
                string dec = ConvertText(input.Text, !Binary);
                if (dec == null) { RefreshPreview(); return; }
                string converted = Binary ? DecimalToZc(dec) : dec;
                // 转换后过长（例如 64 位十进制换二进制有 200 多位）就清空，让用户自己输入，
                // 免得输入框和预览被一个巨长的串占满。
                input.Text = converted.Length <= 66 ? converted : "";
            }
            finally { switching = false; }
            RefreshPreview();
        }

        void RefreshPreview()
        {
            string dec = ConvertText(input.Text, Binary);
            string zcTyped = Binary ? Strip(input.Text, true) : null;

            // 空输入 = 只切换模式，不自定义种子
            if (string.IsNullOrEmpty(Strip(input.Text, Binary)))
            {
                btnOk.Enabled = true;
                hint.ForeColor = skin.Muted;
                hint.Text = Binary ? "未填种子：只使用 Zc 模式" : "未填种子：只使用数字模式";
                preview.Text = "";
                return;
            }

            if (dec == null)
            {
                hint.ForeColor = skin.Warn;
                hint.Text = Binary
                    ? "Zc 模式只接受 Z/C/z/c，且必须 Z、C 交替（第 1 位是 Z）"
                    : "数字模式只接受十进制数字";
                btnOk.Enabled = false;
                preview.Text = Wrap(zcTyped ?? Strip(input.Text, false), WrapWidth);
                return;
            }

            btnOk.Enabled = true;
            hint.ForeColor = skin.Muted;
            string padded = RandomExpr.NormalizeSeed(dec, level);
            hint.Text = "将使用 " + padded.Length.ToString(CultureInfo.InvariantCulture) + " 位十进制种子";
            // 预览：当前模式的内容分行列出，每行 32 位，标题单独一行，避免和数字挤在一起
            StringBuilder sb = new StringBuilder();
            if (Binary)
            {
                string zcs = zcTyped ?? "";
                sb.Append("Zc（").Append(zcs.Length.ToString(CultureInfo.InvariantCulture)).Append(" 位）\r\n");
                sb.Append(Wrap(zcs, WrapWidth)).Append("\r\n");
            }
            sb.Append("十进制（").Append(padded.Length.ToString(CultureInfo.InvariantCulture)).Append(" 位）\r\n");
            sb.Append(Wrap(padded, WrapWidth));
            preview.Text = sb.ToString();
            preview.SelectionStart = 0;
            preview.SelectionLength = 0;
        }

        bool Commit()
        {
            Mode = rbBinary.Checked ? 1 : 0;
            if (string.IsNullOrEmpty(Strip(input.Text, Binary)))
            {
                DecimalSeed = "";      // 空 = 只应用模式，不改种子
                return true;
            }
            string dec = ConvertText(input.Text, Binary);
            if (dec == null) return false;
            DecimalSeed = dec;
            return true;
        }
    }
}
