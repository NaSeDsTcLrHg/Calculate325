/*
 * Calculate325 —— 皮肤（换肤）
 * ---------------------------------------------------------------------------
 * 三套皮肤对应项目里 PT\ 下的三个《明日方舟》模板，各自的视觉语言如下：
 *
 *   jieyuan    「岁的界园志异」  柔粉水彩园林、朱红印章、米白宣纸
 *   huaishuli  「怀黍离」        靛蓝营造图纸、米黄旧纸、极细线框（技术图纸感）
 *   cisuixing  「辞岁行」        灰石庭院与红金缎带、深灰底、朱红 + 鎏金点缀
 *
 * 另外保留一套 plain「默认」（原来的浅灰界面，不加载任何图片）。
 *
 * 共同遵循《明日方舟》界面的设计语言：中性底色 + 单一高饱和强调色、
 * 削角/细描边、编号化的技术感、把中式元素（印章、纹样、篆意字形）符号化使用。
 */
using System;
using System.Drawing;
using System.Drawing.Text;
using System.IO;
using System.Reflection;

namespace Calculate325
{
    enum SkinFace
    {
        Plain,        // 普通界面
        Watercolor,   // 柔粉水彩
        Blueprint,    // 靛蓝图纸
        Diorama       // 灰石庭院 + 红金
    }

    sealed class Skin
    {
        public string Key;
        public string Name;
        public SkinFace Face;

        public Color FormBack;      // 窗口底色（底图透明处可见）
        public Color PanelBack;     // 按钮/面板底色
        public Color FieldBack;     // 输入框底色
        public Color FieldFore;     // 输入框文字
        public Color Border;        // 边框
        public Color Ink;           // 正文
        public Color Muted;         // 次要文字
        public Color Accent;        // 强调色（单一高饱和）
        public Color Accent2;       // 次强调（金/绿等点缀）
        public Color Good;
        public Color Warn;
        public Color OutputBack;
        public Color OutputInk;

        public Color TitleBack;     // 自定义标题栏
        public Color TitleInk;
        public Color TitleLine;     // 标题栏下沿细线
        public string TitleGlyph;   // 标题栏左侧的小图标（单个汉字/符号）
        public Color TitleGlyphInk; // 该图标的颜色
        public string Badge;        // 皮肤代号（标题栏右侧的编号，技术感）

        public Color LotusOuter;    // 荷花按钮外瓣
        public Color LotusInner;    // 荷花按钮内瓣
        public Color LotusInk;      // 荷花按钮文字

        public string BodyFamily;
        public string HeadFamily;
        public string MonoFamily;
        public bool HeadBold;

        public string BackgroundResource;
        public int Corner;          // 削角大小（设计单位，0 = 不削角）

        public bool Dark
        {
            get { return Face == SkinFace.Diorama; }
        }
    }

    static class Skins
    {
        public const string DefaultKey = "plain";

        public static readonly Skin Plain = new Skin
        {
            Key = "plain", Name = "默认", Face = SkinFace.Plain,
            FormBack = Color.FromArgb(244, 246, 251),
            PanelBack = Color.FromArgb(238, 241, 247),
            FieldBack = Color.White,
            FieldFore = Color.FromArgb(27, 36, 48),
            Border = Color.FromArgb(206, 213, 224),
            Ink = Color.FromArgb(27, 36, 48),
            Muted = Color.FromArgb(100, 116, 139),
            Accent = Color.FromArgb(37, 99, 235),
            Accent2 = Color.FromArgb(56, 189, 248),
            Good = Color.FromArgb(21, 128, 61),
            Warn = Color.FromArgb(180, 83, 9),
            OutputBack = Color.White,
            OutputInk = Color.FromArgb(17, 24, 39),
            TitleBack = Color.FromArgb(232, 236, 244),
            TitleInk = Color.FromArgb(27, 36, 48),
            TitleLine = Color.FromArgb(200, 208, 220),
            TitleGlyph = "算", TitleGlyphInk = Color.FromArgb(37, 99, 235), Badge = "CALC",
            LotusOuter = Color.FromArgb(226, 232, 240),
            LotusInner = Color.FromArgb(203, 213, 225),
            LotusInk = Color.FromArgb(27, 36, 48),
            BodyFamily = "Microsoft YaHei UI", HeadFamily = "Microsoft YaHei UI",
            MonoFamily = "Consolas", HeadBold = true,
            BackgroundResource = null, Corner = 0
        };

        public static readonly Skin Jieyuan = new Skin
        {
            Key = "jieyuan", Name = "界园志异（水彩）", Face = SkinFace.Watercolor,
            FormBack = Color.FromArgb(253, 244, 242),
            PanelBack = Color.FromArgb(255, 250, 247),
            FieldBack = Color.FromArgb(255, 253, 250),
            FieldFore = Color.FromArgb(58, 46, 51),
            Border = Color.FromArgb(231, 203, 203),
            Ink = Color.FromArgb(58, 46, 51),
            Muted = Color.FromArgb(146, 118, 120),
            Accent = Color.FromArgb(200, 50, 76),          // 朱红印章色
            Accent2 = Color.FromArgb(211, 158, 74),        // 金
            Good = Color.FromArgb(58, 140, 106),
            Warn = Color.FromArgb(190, 96, 60),
            OutputBack = Color.FromArgb(251, 239, 240),   // 淡粉纸面（不是纯白）
            OutputInk = Color.FromArgb(48, 38, 43),
            TitleBack = Color.FromArgb(252, 236, 235),
            TitleInk = Color.FromArgb(58, 46, 51),
            TitleLine = Color.FromArgb(200, 50, 76),
            TitleGlyph = "园", TitleGlyphInk = Color.FromArgb(232, 97, 140), Badge = "JIE",
            LotusOuter = Color.FromArgb(247, 200, 208),
            LotusInner = Color.FromArgb(200, 50, 76),
            LotusInk = Color.White,
            BodyFamily = "Microsoft YaHei UI", HeadFamily = "KaiTi",
            MonoFamily = "Consolas", HeadBold = false,
            BackgroundResource = "BgJieyuan", Corner = 6
        };

        public static readonly Skin Huaishuli = new Skin
        {
            Key = "huaishuli", Name = "怀黍离（黍）", Face = SkinFace.Blueprint,
            FormBack = Color.FromArgb(250, 247, 240),      // 白
            PanelBack = Color.FromArgb(255, 252, 245),
            FieldBack = Color.FromArgb(255, 254, 250),
            FieldFore = Color.FromArgb(35, 36, 44),
            Border = Color.FromArgb(206, 190, 152),
            Ink = Color.FromArgb(35, 36, 44),
            Muted = Color.FromArgb(140, 128, 104),
            Accent = Color.FromArgb(176, 138, 42),         // 金
            Accent2 = Color.FromArgb(31, 42, 107),         // 深蓝
            Good = Color.FromArgb(107, 122, 46),           // 橄榄
            Warn = Color.FromArgb(184, 68, 56),            // 朱红
            OutputBack = Color.FromArgb(253, 250, 242),
            OutputInk = Color.FromArgb(32, 33, 41),
            TitleBack = Color.FromArgb(28, 37, 96),        // 深蓝标题栏
            TitleInk = Color.FromArgb(250, 247, 240),      // 白字
            TitleLine = Color.FromArgb(232, 195, 60),      // 黄下划线
            TitleGlyph = "黍", TitleGlyphInk = Color.FromArgb(224, 179, 76), Badge = "SHU",
            LotusOuter = Color.FromArgb(250, 247, 238),    // 白瓣
            LotusInner = Color.FromArgb(176, 138, 42),     // 金心
            LotusInk = Color.FromArgb(255, 253, 246),
            BodyFamily = "Microsoft YaHei UI", HeadFamily = "SimSun",
            MonoFamily = "Consolas", HeadBold = true,
            BackgroundResource = "BgHuaishuli", Corner = 4
        };

        public static readonly Skin Cisuixing = new Skin
        {
            Key = "cisuixing", Name = "辞岁行（庭院）", Face = SkinFace.Diorama,
            FormBack = Color.FromArgb(18, 20, 23),
            PanelBack = Color.FromArgb(30, 33, 38),
            FieldBack = Color.FromArgb(36, 40, 46),
            FieldFore = Color.FromArgb(238, 234, 228),
            Border = Color.FromArgb(92, 84, 74),
            Ink = Color.FromArgb(238, 234, 228),
            Muted = Color.FromArgb(168, 160, 150),
            Accent = Color.FromArgb(198, 51, 46),          // 缎带朱红
            Accent2 = Color.FromArgb(214, 164, 74),        // 鎏金
            Good = Color.FromArgb(128, 206, 168),
            Warn = Color.FromArgb(224, 138, 108),
            OutputBack = Color.FromArgb(14, 16, 19),
            OutputInk = Color.FromArgb(240, 236, 230),
            TitleBack = Color.FromArgb(26, 29, 33),
            TitleInk = Color.FromArgb(238, 234, 228),
            TitleLine = Color.FromArgb(198, 51, 46),
            TitleGlyph = "岁", TitleGlyphInk = Color.FromArgb(214, 164, 74), Badge = "SU",
            LotusOuter = Color.FromArgb(120, 44, 42),
            LotusInner = Color.FromArgb(198, 51, 46),
            LotusInk = Color.FromArgb(255, 244, 226),
            BodyFamily = "Microsoft YaHei UI", HeadFamily = "KaiTi",
            MonoFamily = "Consolas", HeadBold = false,
            BackgroundResource = "BgCisuixing", Corner = 10
        };

        public static Skin[] All()
        {
            return new Skin[] { Plain, Jieyuan, Huaishuli, Cisuixing };
        }

        public static Skin ByKey(string key)
        {
            if (!string.IsNullOrEmpty(key))
            {
                Skin[] all = All();
                for (int i = 0; i < all.Length; i++)
                {
                    if (string.Equals(all[i].Key, key.Trim(), StringComparison.OrdinalIgnoreCase)) return all[i];
                }
            }
            return Plain;
        }

        public static int IndexOf(string key)
        {
            Skin[] all = All();
            for (int i = 0; i < all.Length; i++)
            {
                if (string.Equals(all[i].Key, key, StringComparison.OrdinalIgnoreCase)) return i;
            }
            return 0;
        }
    }

    /// <summary>底图与模板字体。字体优先用 PT\ 里模板自带的思源宋体，缺失则退回系统字体。</summary>
    static class SkinAssets
    {
        static PrivateFontCollection loaded;

        public static Image LoadBackground(Skin s)
        {
            if (s == null || string.IsNullOrEmpty(s.BackgroundResource)) return null;
            try
            {
                Assembly asm = Assembly.GetExecutingAssembly();
                using (Stream st = asm.GetManifestResourceStream(s.BackgroundResource))
                {
                    if (st == null) return null;
                    using (Image img = Image.FromStream(st)) return new Bitmap(img);
                }
            }
            catch (Exception) { return null; }
        }

        /// <summary>尝试加载模板自带字体；成功则把字体族加进私有集合，可被 new Font(name) 使用。</summary>
        public static void TryLoadTemplateFonts(string baseDir)
        {
            if (loaded != null) return;
            loaded = new PrivateFontCollection();
            string[] candidates = new string[]
            {
                @"PT\extract\岁的界园志异PPT模板\PPT所用字体(打开PPT前先全选右键安装)\SourceHanSerifCN-Medium-6.otf",
                @"PT\extract\岁的界园志异PPT模板\PPT所用字体(打开PPT前先全选右键安装)\SourceHanSerifCN-Bold-2.otf",
                @"PT\辞岁行PPT模板\辞岁行PPT模板\PPT所用字体(打开PPT前请全选右键安装)\汇文明朝体.otf",
                @"PT\辞岁行PPT模板\辞岁行PPT模板\PPT所用字体(打开PPT前请全选右键安装)\字语文篆体_mianfeiziti.com.ttf"
            };
            for (int i = 0; i < candidates.Length; i++)
            {
                try
                {
                    string p = Path.Combine(baseDir, candidates[i]);
                    if (File.Exists(p)) loaded.AddFontFile(p);
                }
                catch (Exception) { }
            }
        }

        public static FontFamily FindFamily(string wanted)
        {
            if (string.IsNullOrEmpty(wanted)) return null;
            try
            {
                if (loaded != null)
                {
                    for (int i = 0; i < loaded.Families.Length; i++)
                    {
                        if (string.Equals(loaded.Families[i].Name, wanted, StringComparison.OrdinalIgnoreCase))
                            return loaded.Families[i];
                    }
                }
            }
            catch (Exception) { }
            return null;
        }

        /// <summary>模板字体是否可用（用于界面上的“字体来自模板”判断）。</summary>
        public static string LoadedFontNames()
        {
            if (loaded == null) return "";
            try
            {
                string s = "";
                for (int i = 0; i < loaded.Families.Length; i++) s += (i == 0 ? "" : "/") + loaded.Families[i].Name;
                return s;
            }
            catch (Exception) { return ""; }
        }
    }
}
