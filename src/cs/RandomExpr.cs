/*
 * Calculate325 —— 随机模式生成器（mhtml 层类型 + 混合嵌套）
 * ---------------------------------------------------------------------------
 * 种子长度 = 8 × level：
 *
 *   L1: 8 位 → 初等函数/常数恒等式（没有类型位、没有 ∫/lim/Σ）
 *   L2: 16 位 → 前 4 位类型位，最多 1 层（旧 L1）
 *   L3: 24 位 → 前 6 位类型位，最多 4 层
 *   L4: 32 位 → 前 8 位类型位；混合最多 4 层，类型位全 0 时 8 层
 *
 * L2 起的布局：前 2×level 位是各层「类型位」（9 = 没有这一层），
 * 后面每 3 位一组：[家族 1 位][参数 2 位]。
 * 第一位 = 最外层类型；第二位 = 第二层类型；可以没有（9）。
 * 层类型：0 单积分、1 二重、2 三重、3 极限、4 级数、5 极限+积分、
 *         6 级数+积分、7 极限+级数、8 等分布极限。
 * 从最内层往外层包，最终得到**一条**混合式子，例如
 *   ∫∫ (w) · [ lim (L) · [ Σ (S) · (19²−6²) ] ] dx dy = 325
 * 325 的核心不用 13·25，而用不显眼的恒等式（见 HiddenCore325）。
 *
 * 全零 32 位：8 个类型 0 + 家族 0/参数 0 → 八个 ∫_0^1 的八重积分。
 *
 * 家族（mhtml 的 FAMILIES；三角家族系数已校正）：
 *   0 幂函数      (p+1)x^p                         [0,1]
 *   1 指数        a e^{-ax}, a=(p+1)/100            [0,∞)
 *   2 高斯        2a/√π e^{-a²x²}                   [0,∞)
 *   3 Cauchy      2a/(π(1+a²x²))                    [0,∞)
 *   4 对数幂      (p+1)² x^p (−ln x)                [0,1]
 *   5 Frullani    (e^{-ax}-e^{-aex})/x              [0,∞)
 *   6 sinc²       2/(πa) sin²(ax)/x²                [0,∞)
 *   7 Beta        (p+1)(p+2) x^p (1−x)              [0,1]
 *   8 三角        (2p+2) sin^{2p+1}x cos x          [0,π/2]  （mhtml 写的是 2p+1，错）
 *   9 arcsin      2/(π√(a²−x²)), a=(p+1)/100         [0,a]
 *
 * 隐蔽系数：325 核心用 19²−6² / 15²+10² / 7³−18 等恒等式，不写 13·25；
 * 家族系数/指数也避开 325 的裸倍数
 * （SafeParam 会跳过 Beta 的 25·26=650、对数幂的 65²=4225 等）。
 * 目标 325 只出现在最后的 = 325。
 */
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Text;

namespace Calculate325
{
    public sealed class RandomExprResult
    {
        public string Expression;
        public string Latex;
        public bool Identity;
        public int Complexity;
        public int Level;
        public string Seed;
        public string Kind;        // nested-2 / nested-4 / nested-6 / nested-8
        public string Family;      // nested
        public int Blocks;         // = 层数（兼容旧字段）
        public int Layers;         // 主层数
        public int Dims;           // 总积分重数（单=1，二重=2，三重=3）
        public string TypeCodes;   // 每层的类型码，如 "0,1,3,4"
        public string TypeNames;   // 每层的中文类型名
        public Rat Value;
        public bool ValueOk;
        public string Note;
        public Func<Rat> ClosedForm;
        public Func<double> NumericValue;
        public string NumericKind;

        // ---- 独立后置校验（FormulaAudit 从最终 LaTeX 重新算出来的结果）----
        public bool AuditOk;
        public bool AuditTight;
        public double AuditValue;
        public double AuditError;
        public double AuditRel;
        public string AuditMode;
        public string AuditDetail;
        public string RequestedSeed;
        public int AuditTries;

        // ---- L5 v2：自然模型管线（种子 → 候选 → 构造性 325 → 评分仲裁）----
        public string NaturalModel;        // M1..M10
        public string NaturalModelName;    // 中文模型名
        public string Topology;            // chain / tree / nested / net / closed
        public string SelectionMode;       // rank / rotation[n]
        public int CandidateId;
        public int CandidateCount;
        public string CandidateModels;
        public string SchemaVersion;       // l5-v2
        public string Fingerprint;         // sha256(canon(expr))
        public string TemplateHash;        // SimHash64（模板指纹）
        public string SubSeeds;            // 派生出的子种子（紧凑审计用）
        public bool RetryUsed;             // 批内指纹冲突后是否重试
        public double T1, T2, T3, T4, T5, T6;
        public int CrossEdges;
        public string L5AuditJson;         // 完整审计链（JSON）
        public string L5VerificationLog;
        public bool Audited;
    }

    public static partial class RandomExpr
    {
        public const int MinLevel = 1;
        public const int MaxLevel = 4;

        public static readonly string[] FamilyNames = new string[]
        {
            "power", "exponential", "gaussian", "cauchy", "logpower",
            "frullani", "sinc2", "beta", "trig", "arcsin"
        };

        public static readonly string[] TypeNames = new string[]
        {
            "单重积分", "二重积分", "三重积分", "极限", "无穷级数",
            "极限+积分", "级数+积分", "极限+级数", "等分布极限", "无"
        };

        static readonly string[] Primes = new string[] { "2", "3", "5", "7", "11", "13", "17", "19", "23", "29" };

        // ===================== 种子 / 分段 =====================

        /// <summary>各等级的种子位数（L1/L2 加长，给四则/三角运算更多自由度）。</summary>
        static readonly int[] SeedBits = new int[] { 0, 12, 20, 24, 32, 64 };

        public static int SeedLength(int level)
        {
            if (level < 1) level = 1;
            if (level > 5) level = 5;
            return SeedBits[level];
        }

        /// <summary>L1 纯初等；L2 1 层；L3 4 层；L4 8 层。</summary>
        public static int MaxLayers(int level)
        {
            level = ClampLevel(level);
            if (level == 1) return 0;
            if (level == 2) return 1;
            if (level == 3) return 4;
            return 8;
        }

        /// <summary>类型槽位数 = 2 × level（L4 即前 8 位都是类型位）。</summary>
        public static int SlotCount(int level)
        {
            return 2 * ClampLevel(level);
        }

        public static string LevelName(int level)
        {
            level = ClampLevel(level);
            if (level == 1) return "L1 8 位 / 初等";
            if (level == 2) return "L2 16 位 / 1 层";
            if (level == 3) return "L3 24 位 / ≤3 层";
            return "L4 32 位 / ≤8 层";
        }

        public static string MakeSeed(int level, Random rnd)
        {
            if (rnd == null) rnd = new Random();
            int len = SeedLength(level);
            StringBuilder sb = new StringBuilder(len);
            for (int i = 0; i < len; i++) sb.Append((char)('0' + rnd.Next(10)));
            return sb.ToString();
        }

        public static string NormalizeSeed(string seed, int level)
        {
            int need = SeedLength(level);
            StringBuilder sb = new StringBuilder(need);
            if (seed != null)
            {
                for (int i = 0; i < seed.Length && sb.Length < need; i++)
                {
                    char c = seed[i];
                    if (c >= '0' && c <= '9') sb.Append(c);
                }
            }
            while (sb.Length < need) sb.Append('0');
            return sb.ToString();
        }

        /// <summary>每 4 位一组，刚好对应一个积分层。</summary>
        /// <summary>
        /// 种子显示格式：每 4 个字符一个空格，每 perLine 个字符换一行。
        /// zc=true 时只保留 Z/C/z/c，否则只保留数字。
        /// </summary>
        public static string FormatSeedWrapped(string seed, int perLine, bool zc)
        {
            if (perLine < 1) perLine = 32;
            StringBuilder clean = new StringBuilder();
            if (seed != null)
            {
                for (int i = 0; i < seed.Length; i++)
                {
                    char c = seed[i];
                    if (zc)
                    {
                        if (c == 'Z' || c == 'C' || c == 'z' || c == 'c') clean.Append(c);
                    }
                    else if (c >= '0' && c <= '9') clean.Append(c);
                }
            }
            StringBuilder sb = new StringBuilder(clean.Length + clean.Length / 4 + clean.Length / perLine * 2 + 2);
            for (int ls = 0; ls < clean.Length; ls += perLine)
            {
                if (ls > 0) sb.Append("\r\n");
                int end = Math.Min(clean.Length, ls + perLine);
                for (int i = ls; i < end; i++)
                {
                    if (i > ls && ((i - ls) % 4) == 0) sb.Append(' ');
                    sb.Append(clean[i]);
                }
            }
            return sb.ToString();
        }

        public static string FormatSeed(string seed)
        {
            StringBuilder clean = new StringBuilder();
            if (seed != null)
            {
                for (int i = 0; i < seed.Length; i++)
                {
                    char c = seed[i];
                    if (c >= '0' && c <= '9') clean.Append(c);
                }
            }
            StringBuilder sb = new StringBuilder(clean.Length + clean.Length / 4 + 1);
            for (int i = 0; i < clean.Length; i++)
            {
                if (i > 0 && (i % 4) == 0) sb.Append(' ');
                sb.Append(clean[i]);
            }
            return sb.ToString();
        }

        static int ClampLevel(int level)
        {
            if (level < MinLevel) return MinLevel;
            if (level > MaxLevel) return MaxLevel;
            return level;
        }

        sealed class Slot
        {
            public int Type;      // 0-9（9 = 无/缺省）
            public int Family;    // 0-9
            public int Param;     // 0-99
            public int Index;     // 第几层（0 起）
        }

        /// <summary>
        /// 种子布局：前 2×level 位是各层的类型位；后面每 3 位一组
        /// [家族 1 位][参数 2 位]。L4 = 8 个类型位 + 8 组 × 3 位 = 32。
        /// </summary>
        static List<Slot> ParseSlots(string seed, int level)
        {
            string s = NormalizeSeed(seed, level);
            int count = SlotCount(level);
            List<Slot> list = new List<Slot>();
            for (int i = 0; i < count; i++)
            {
                Slot slot = new Slot();
                slot.Index = i;
                slot.Type = s[i] - '0';
                int start = count + i * 3;
                slot.Family = s[start] - '0';
                int p;
                if (!int.TryParse(s.Substring(start + 1, 2), NumberStyles.Integer, CultureInfo.InvariantCulture, out p)) p = 0;
                slot.Param = p;
                list.Add(slot);
            }
            return list;
        }

        // ===================== 对外入口 =====================

        /// <summary>L1：只有简单的初等函数/常数恒等式，不出现积分、极限、级数。</summary>
        static RandomExprResult BuildElementary(string seed, Rat target)
        {
            int family = seed[0] - '0';
            int param = 0;
            // L1 现在是 12 位：第 1 位选模板，后面 11 位都参与参数，运算组合更多
            for (int i = 1; i < seed.Length; i++) param = (param * 10 + (seed[i] - '0')) % 1000003;
            int a = param % 99 + 1;
            string asv = a.ToString(CultureInfo.InvariantCulture);

            string coef;
            string coefL;
            if (Is325(target)) HiddenCore325(seed, out coef, out coefL);
            else { coef = Coef(target); coefL = LatexCoef(target); }
            string body;
            string latex;
            // 16 个模板，乘的都是"要算一下才知道等于 1"的初等因子：
            // 对数、三角、反三角、阶乘、四则恒等，避免 L1 全是 K/2·2^{ln e} 一种写法。
            switch (((seed[0] - '0') * 10 + seed[1] - '0') % 16)
            {
                case 0:
                    body = coef;
                    latex = coefL;
                    break;
                case 1:
                    body = "(" + coef + "/2)·(2^{ln e})";
                    latex = "\\frac{" + coefL + "}{2}\\cdot 2^{\\ln e}";
                    break;
                case 2:
                    body = "(" + coef + "/3)·(3^{ln e})";
                    latex = "\\frac{" + coefL + "}{3}\\cdot 3^{\\ln e}";
                    break;
                case 3:
                    body = "(" + coef + "/4)·(4^{ln e})";
                    latex = "\\frac{" + coefL + "}{4}\\cdot 4^{\\ln e}";
                    break;
                case 4:
                    body = coef + "·tan(π/4)";
                    latex = coefL + "\\cdot\\tan\\frac{\\pi}{4}";
                    break;
                case 5:
                    body = coef + "·sin(π/2)";
                    latex = coefL + "\\cdot\\sin\\frac{\\pi}{2}";
                    break;
                case 6:
                    body = coef + "·(2cos(π/3))";
                    latex = coefL + "\\cdot 2\\cos\\frac{\\pi}{3}";
                    break;
                case 7:
                    body = coef + "·(sin(π/6)+cos(π/3))";
                    latex = coefL + "\\cdot\\left(\\sin\\frac{\\pi}{6}+\\cos\\frac{\\pi}{3}\\right)";
                    break;
                case 8:
                    body = coef + "·(arcsin1/(π/2))";
                    latex = coefL + "\\cdot\\frac{\\arcsin 1}{\\pi/2}";
                    break;
                case 9:
                    body = coef + "·(arctan1/(π/4))";
                    latex = coefL + "\\cdot\\frac{\\arctan 1}{\\pi/4}";
                    break;
                case 10:
                    body = coef + "·(arcsin(1/2)+arccos(1/2))·(2/π)";
                    latex = coefL + "\\cdot\\left(\\arcsin\\frac{1}{2}+\\arccos\\frac{1}{2}\\right)\\cdot\\frac{2}{\\pi}";
                    break;
                case 11:
                    body = coef + "·(sin²(π/5)+cos²(π/5))";
                    latex = coefL + "\\cdot\\left(\\sin^{2}\\frac{\\pi}{5}+\\cos^{2}\\frac{\\pi}{5}\\right)";
                    break;
                case 12:
                    body = coef + "·(2³/8)";
                    latex = coefL + "\\cdot\\frac{2^{3}}{8}";
                    break;
                case 13:
                    body = coef + "·e^(ln3)/3";
                    latex = coefL + "\\cdot\\frac{e^{\\ln 3}}{3}";
                    break;
                case 14:
                    body = coef + "·5!/(5·4!)";
                    latex = coefL + "\\cdot\\frac{5!}{5\\cdot 4!}";
                    break;
                default:
                    // 四则恒等：(a+1)² − a² − 2a = 1，a 由种子决定
                    body = coef + "·((a+1)²−a²−2a)".Replace("a", asv);
                    latex = coefL + "\\cdot\\left(\\left(" + asv + "+1\\right)^{2}-" + asv + "^{2}-2\\cdot" + asv + "\\right)";
                    break;
            }

            RandomExprResult r = new RandomExprResult();
            r.Expression = body + " = " + PrettyRat(target);
            r.Latex = latex + " = " + LatexNum(target);
            r.Identity = true;
            r.Level = 1;
            r.Seed = seed;
            r.Family = "elementary";
            r.Layers = 0;
            r.Blocks = 0;
            r.Dims = 0;
            r.TypeCodes = "E";
            r.TypeNames = "初等函数";
            r.Kind = "elementary";
            r.Complexity = 2;
            r.Value = target;
            r.ValueOk = true;
            r.NumericKind = "closed";
            Rat closed = target;
            r.ClosedForm = delegate { return closed; };
            return r;
        }

        // ===================== L4 高难度：不可分离 / 特殊函数 =====================
        // 仅 UI 生成时启用（allowHard=true），不影响既有随机自检与兼容测试。
        // 统一把 325 写成 a²+b²（a,b 由种子选），避免直接出现 325、650、13·25。
        static RandomExprResult TryBuildHardL4(string seed, Rat target)
        {
            if (seed == null || seed.Length < 32) return null;
            bool allZero = true;
            for (int i = 0; i < 8; i++) if (seed[i] != '0') { allZero = false; break; }
            if (allZero) return null;                 // 全零 32 位仍走八重积分
            int tier = seed[0] - '0';
            if (tier <= 0) tier = 3;                  // L4 一律至少 4 层，不再保留短 D1

            // 325 的三种不显眼写法：立方和比 / 指数对数 / 平方和。
            int kf = SD(seed, 6) % 3;
            string kBody, kTex;
            if (kf == 0)
            {
                kBody = "((20³+15³)/(20+15))";
                kTex = "\\frac{20^{3}+15^{3}}{20+15}";
            }
            else if (kf == 1)
            {
                kBody = "e^{2·ln5+ln13}";
                kTex = "e^{2\\ln5+\\ln13}";
            }
            else
            {
                kBody = "(17²+6²)";
                kTex = "\\left(17^{2}+6^{2}\\right)";
            }

            double rv = 0.8 + (SD(seed, 7) % 12) / 10.0;
            string rs = rv.ToString("0.0", CultureInfo.InvariantCulture);
            double r2 = 1.2 + (SD(seed, 8) % 10) / 10.0;
            string r2s = r2.ToString("0.0", CultureInfo.InvariantCulture);

            string body, tex;
            int dims = 1;
            string codes = "0";
            string tname = "非分离积分";
            int complexity = 8;

            if (tier <= 2)
            {
                // D2（20%）：单层但至少二重/三重，保证 L4 不短于 L3
                int v = SD(seed, 4) % 5;
                if (v == 0)
                {
                    body = "(8·" + rs + "³·" + kBody + "/π^{3/2})·∫_0^{+∞}∫_0^{+∞}∫_0^{+∞} e^{−(" + rs + ")²(x²+y²+z²)} dx dy dz";
                    tex = "\\frac{8\\cdot " + rs + "^{3}\\cdot " + kTex + "}{\\pi^{3/2}}"
                        + "\\int_{0}^{+\\infty}\\int_{0}^{+\\infty}\\int_{0}^{+\\infty}"
                        + "e^{-(" + rs + ")^{2}(x^{2}+y^{2}+z^{2})}\\,dx\\,dy\\,dz";
                    dims = 3;
                    codes = "2";
                    tname = "三重 Gaussian 积分";
                    complexity = 12;
                }
                else if (v == 1)
                {
                    body = "(8·" + rs + "³·" + r2s + "³·" + kBody + "/π²)·(∫_0^{+∞} 1/(x⁴+" + rs + "⁴) dx)·(∫_0^{+∞} 1/(y⁴+" + r2s + "⁴) dy)";
                    tex = "\\frac{8\\cdot " + rs + "^{3}\\cdot " + r2s + "^{3}\\cdot " + kTex + "}{\\pi^{2}}"
                        + "\\left(\\int_{0}^{+\\infty}\\frac{1}{x^{4}+" + rs + "^{4}}\\,dx\\right)"
                        + "\\left(\\int_{0}^{+\\infty}\\frac{1}{y^{4}+" + r2s + "^{4}}\\,dy\\right)";
                    dims = 2;
                    codes = "1";
                    tname = "二重四次积分";
                    complexity = 12;
                }
                else if (v == 2)
                {
                    body = "(" + kBody + "/π²)·(∫_0^1 1/√(x(1−x)) dx)²";
                    tex = "\\frac{" + kTex + "}{\\pi^{2}}\\left(\\int_{0}^{1}\\frac{1}{\\sqrt{x(1-x)}}\\,dx\\right)^{2}";
                    dims = 2;
                    codes = "1";
                    tname = "Beta 积分平方";
                    complexity = 11;
                }
                else if (v == 3)
                {
                    body = "(4·" + kBody + "/π²)·(∫_0^{+∞} sin x/x dx)²";
                    tex = "\\frac{4" + kTex + "}{\\pi^{2}}\\left(\\int_{0}^{+\\infty}\\frac{\\sin x}{x}\\,dx\\right)^{2}";
                    dims = 2;
                    codes = "1";
                    tname = "Dirichlet 积分平方";
                    complexity = 11;
                }
                else
                {
                    body = "∫_0^{+∞} (cos(" + rs + "x) − cos(" + rs + "e^" + kBody + "x))/x dx";
                    tex = "\\int_{0}^{+\\infty}\\frac{\\cos(" + rs + "x)-\\cos\\left(" + rs + "e^{" + kTex + "}x\\right)}{x}\\,dx";
                    complexity = 10;
                }
            }
            else if (tier <= 8)
            {
                // D3（60%）：不可分离二重积分 / 特殊级数 / Euler 乘积，4-6 步
                int v = SD(seed, 5) % 8;
                complexity = 13;
                if (v == 0)
                {
                    body = "(6·" + kBody + "/π²)·∫_0^1∫_0^1 1/(1−xy) dx dy";
                    tex = "\\frac{6" + kTex + "}{\\pi^{2}}"
                        + "\\int_{0}^{1}\\int_{0}^{1}\\frac{1}{1-xy}\\,dx\\,dy";
                    dims = 2;
                    codes = "1";
                    tname = "二重积分";
                }
                else if (v == 1)
                {
                    body = "(12·" + kBody + "/π²)·∫_0^1∫_0^x 1/(1−xy) dy dx";
                    tex = "\\frac{12" + kTex + "}{\\pi^{2}}"
                        + "\\int_{0}^{1}\\int_{0}^{x}\\frac{1}{1-xy}\\,dy\\,dx";
                    dims = 2;
                    codes = "1";
                    tname = "二重积分";
                    complexity = 14;
                }
                else if (v == 2)
                {
                    body = "(" + kBody + "/(π·" + r2s + "²·ln2))·∫_0^{2π}∫_0^" + r2s + " ρ/(1+ρ²/" + r2s + "²) dρ dθ";
                    tex = "\\frac{" + kTex + "}{\\pi " + r2s + "^{2}\\ln2}"
                        + "\\int_{0}^{2\\pi}\\int_{0}^{" + r2s + "}\\frac{\\rho}{1+\\rho^{2}/" + r2s + "^{2}}\\,d\\rho\\,d\\theta";
                    dims = 2;
                    codes = "1";
                    tname = "极坐标二重积分";
                    complexity = 15;
                }
                else if (v == 3)
                {
                    body = "(6·" + kBody + "/π²)·Σ_{n=1}^{∞} 1/n²";
                    tex = "\\frac{6" + kTex + "}{\\pi^{2}}\\sum_{n=1}^{\\infty}\\frac{1}{n^{2}}";
                    dims = 0;
                    codes = "4";
                    tname = "Basel 级数";
                    complexity = 14;
                }
                else if (v == 4)
                {
                    body = "(6·" + kBody + "/π²)·∏_{p}(1−1/p²)^{−1}";
                    tex = "\\frac{6" + kTex + "}{\\pi^{2}}\\prod_{p}\\left(1-\\frac{1}{p^{2}}\\right)^{-1}";
                    dims = 0;
                    codes = "4";
                    tname = "Euler 乘积";
                    complexity = 15;
                }
                else if (v == 5)
                {
                    body = "(36·" + kBody + "/π⁴)·Σ_{m=1}^{∞}Σ_{n=1}^{∞} 1/(m²n²)";
                    tex = "\\frac{36" + kTex + "}{\\pi^{4}}"
                        + "\\sum_{m=1}^{\\infty}\\sum_{n=1}^{\\infty}\\frac{1}{m^{2}n^{2}}";
                    dims = 0;
                    codes = "4";
                    tname = "双重 ζ(2)²";
                    complexity = 16;
                }
                else if (v == 6)
                {
                    body = "(" + kBody + "·π²/6)·Σ_{n=1}^{∞} μ(n)/n²";
                    tex = "\\frac{" + kTex + "\\pi^{2}}{6}"
                        + "\\sum_{n=1}^{\\infty}\\frac{\\mu(n)}{n^{2}}";
                    dims = 0;
                    codes = "4";
                    tname = "Möbius 反演";
                    complexity = 16;
                }
                else
                {
                    body = "(2·" + kBody + "/π)·∏_{n=1}^{∞} (2n)²/((2n−1)(2n+1))";
                    tex = "\\frac{2" + kTex + "}{\\pi}"
                        + "\\prod_{n=1}^{\\infty}\\frac{(2n)^{2}}{(2n-1)(2n+1)}";
                    dims = 0;
                    codes = "4";
                    tname = "Wallis 乘积";
                    complexity = 16;
                }
            }
            else
            {
                // D4（10%）：极限 + 双重 zeta，7+ 步
                body = "lim_{n→∞} (36·" + kBody + "/π⁴)·(Σ_{j=1}^{n} 1/j²)²";
                tex = "\\lim_{n\\to\\infty}\\frac{36" + kTex + "}{\\pi^{4}}"
                    + "\\left(\\sum_{j=1}^{n}\\frac{1}{j^{2}}\\right)^{2}";
                dims = 0;
                codes = "7";
                tname = "极限+级数";
                complexity = 18;
            }

            // 统一套一层“极限 + 双重 Basel”的恒等包络（值=1），保证 L4 至少 4 层。
            string envBody = "lim_{n→∞} (36/π⁴)·(Σ_{j=1}^{n} 1/j²)·(Σ_{m=1}^{∞} 1/m²)·(";
            string envTex = "\\lim_{n\\to\\infty}\\frac{36}{\\pi^{4}}\\left(\\sum_{j=1}^{n}\\frac{1}{j^{2}}\\right)\\left(\\sum_{m=1}^{\\infty}\\frac{1}{m^{2}}\\right)\\cdot\\left(";
            body = envBody + body + ")";
            tex = envTex + tex + "\\right)";
            codes = "3,4,4," + codes;
            tname = "极限 + 双重 Basel + " + tname;
            complexity += 8;

            body += " = " + PrettyRat(target);
            tex += " = " + LatexNum(target);

            RandomExprResult r = new RandomExprResult();
            r.Expression = body;
            r.Latex = tex;
            r.Identity = true;
            r.Complexity = complexity;
            r.Level = 4;
            r.Seed = seed;
            r.Kind = "mixed-4";
            r.Family = "hard";
            r.Blocks = 4;
            r.Layers = 4;
            r.Dims = dims;
            r.TypeCodes = codes;
            r.TypeNames = tname;
            r.Value = target;
            r.ValueOk = true;
            r.Note = "不可分离/特殊函数";
            r.NumericKind = "closed";
            r.NumericValue = null;
            r.ClosedForm = delegate { return target; };
            return r;
        }

        // ===================== L5：多重积分 / 数论 / 连乘 =====================
        public const int L5SeedLength = 64;

        public static string MakeL5Seed(Random rnd)
        {
            if (rnd == null) rnd = new Random();
            StringBuilder sb = new StringBuilder(L5SeedLength);
            for (int i = 0; i < L5SeedLength; i++) sb.Append((char)('0' + rnd.Next(10)));
            return sb.ToString();
        }

        public static string NormalizeL5Seed(string seed)
        {
            StringBuilder sb = new StringBuilder(L5SeedLength);
            if (seed != null)
            {
                for (int i = 0; i < seed.Length && sb.Length < L5SeedLength; i++)
                {
                    char c = seed[i];
                    if (c >= '0' && c <= '9') sb.Append(c);
                }
            }
            while (sb.Length < L5SeedLength) sb.Append('0');
            return sb.ToString();
        }

        static RandomExprResult BuildHardSmall(int level, string seed, Rat target)
        {
            int kf = SD(seed, 2) % 3;
            string kb, kt;
            if (kf == 0) { kb = "((20³+15³)/(20+15))"; kt = "\\frac{20^{3}+15^{3}}{20+15}"; }
            else if (kf == 1) { kb = "e^{2·ln5+ln13}"; kt = "e^{2\\ln5+\\ln13}"; }
            else { kb = "(17²+6²)"; kt = "\\left(17^{2}+6^{2}\\right)"; }

            double av = FixA(0.7 + (SD(seed, 7) % 15) / 10.0);
            string asv = av.ToString("0.0", CultureInfo.InvariantCulture);
            // L3：K 在三重 Gaussian 积分内部，3 重积分 = 3 层；没有矩阵/数论。
            string body = "∫_0^{+∞}∫_0^{+∞}∫_0^{+∞} (8·" + asv + "³·" + kb + "/π^{3/2}) e^{−(" + asv + ")²(x²+y²+z²)} dx dy dz";
            string tex = "\\int_{0}^{+\\infty}\\int_{0}^{+\\infty}\\int_{0}^{+\\infty}"
                + "\\frac{8\\cdot " + asv + "^{3}" + kt + "}{\\pi^{3/2}}"
                + "e^{-(" + asv + ")^{2}(x^{2}+y^{2}+z^{2})}\\,dx\\,dy\\,dz";
            string codes = "0,0,0";
            int dims = 3;
            int layers = 3;

            if (level == 4)
            {
                // L4：三重积分基础上再加 1-2 个恒等于 1 的“非数论”重积分/连乘/极限因子。
                string[] ab = new string[] {
                    "∫_1^{+∞} 1/x² dx",
                    "∫_0^1 x·eˣ dx",
                    "(1/(25π))·∫_0^{2π}∫_0^5 r dr dθ",
                    "8·Σ_{m=1}^{∞}Σ_{j=1}^{∞}Σ_{k=1}^{∞} 1/3^{m+j+k}",
                    "(1/" + asv + ")·∫_0^{+∞} (cos x−cos(e^" + asv + "x))/x dx",
                    "(2·" + asv + "/√π)·∫_0^{+∞} e^{−" + asv + "²x²} dx"
                };
                string[] at = new string[] {
                    "\\int_{1}^{+\\infty}\\frac{1}{x^{2}}\\,dx",
                    "\\int_{0}^{1}x\\cdot {e}^{x}\\,dx",
                    "\\frac{1}{25\\pi}\\int_{0}^{2\\pi}\\int_{0}^{5}r\\,dr\\,d\\theta",
                    "8\\sum_{m=1}^{\\infty}\\sum_{j=1}^{\\infty}\\sum_{k=1}^{\\infty}\\frac{1}{3^{m+j+k}}",
                    "\\frac{1}{" + asv + "}\\int_{0}^{+\\infty}\\frac{\\cos x-\\cos(e^{" + asv + "}x)}{x}\\,dx",
                    "\\frac{2\\cdot " + asv + "}{\\sqrt{\\pi}}\\int_{0}^{+\\infty}e^{-" + asv + "^{2}x^{2}}\\,dx"
                };
                int[] ad = new int[] { 1, 1, 2, 0, 1, 1 };
                int[] ac = new int[] { 0, 0, 1, 4, 0, 0 };
                int count = 1 + SD(seed, 5) % 2;
                for (int i = 0; i < count; i++)
                {
                    int idx = (SD(seed, 8 + i * 2) + i) % 6;
                    body += "·(" + ab[idx] + ")";
                    tex += "\\cdot\\left(" + at[idx] + "\\right)";
                    codes += "," + ac[idx];
                    dims += ad[idx];
                    layers++;
                }
            }

            body += " = " + PrettyRat(target);
            tex += " = " + LatexNum(target);

            RandomExprResult r = new RandomExprResult();
            r.Expression = body;
            r.Latex = tex;
            r.Identity = true;
            r.Complexity = 12 + level * 5 + layers * 2;
            r.Level = level;
            r.Seed = seed;
            r.Kind = "mixed-" + layers.ToString(CultureInfo.InvariantCulture);
            r.Family = level == 3 ? "calculus3" : "calculus4";
            r.Blocks = layers;
            r.Layers = layers;
            r.Dims = dims;
            r.TypeCodes = codes;
            r.TypeNames = "重积分/极限/连乘";
            r.Value = target;
            r.ValueOk = true;
            r.Note = "L" + level + " 纯分析结构，矩阵/数论只属于 L5";
            r.NumericKind = "closed";
            r.NumericValue = null;
            r.ClosedForm = delegate { return target; };
            return r;
        }
        static void BuildMatrixBase(int matrixCount, string seed, out string body, out string tex, out string codes)
        {
            string clean = seed;
            if (matrixCount == 0)
            {
                int kf = SD(clean, 2) % 3;
                string kb, kt;
                if (kf == 0) { kb = "((20³+15³)/(20+15))"; kt = "\\frac{20^{3}+15^{3}}{20+15}"; }
                else if (kf == 1) { kb = "e^{2·ln5+ln13}"; kt = "e^{2\\ln5+\\ln13}"; }
                else { kb = "(17²+6²)"; kt = "\\left(17^{2}+6^{2}\\right)"; }
                body = "(π²/6)·Σ_{n=1}^{∞} " + kb + "·μ(n)/n²";
                tex = "\\frac{\\pi^{2}}{6}\\sum_{n=1}^{\\infty}\\frac{" + kt + "\\mu(n)}{n^{2}}";
                codes = "0";
                return;
            }
            if (matrixCount == 1)
            {
                long[,] a = BuildDetMatrix(4, new int[] { 1, 1, 5, 65 }, clean + "m1");
                SanitizeMatrix(a);
                body = "det" + MatrixBody(a);
                tex = "\\det" + MatrixTex(a);
                codes = "1";
                return;
            }
            int pair = SD(clean, 3) % 4;
            int[] d1 = pair == 0 ? new int[] { 1, 1, 5 } : (pair == 1 ? new int[] { 1, 1, 13 } : (pair == 2 ? new int[] { 1, 5, 5 } : new int[] { 1, 5, 13 }));
            int[] d2 = pair == 0 ? new int[] { 1, 5, 13 } : (pair == 1 ? new int[] { 1, 5, 5 } : (pair == 2 ? new int[] { 1, 1, 13 } : new int[] { 1, 1, 5 }));
            long[,] x = BuildDetMatrix(3, d1, clean + "m2a");
            long[,] y = BuildDetMatrix(3, d2, clean + "m2b");
            SanitizeMatrix(x); SanitizeMatrix(y);
            body = "det" + MatrixBody(x) + "·det" + MatrixBody(y);
            tex = "\\det" + MatrixTex(x) + "\\cdot\\det" + MatrixTex(y);
            codes = "1,1";
        }

        /// <summary>L5 主路径：因子链 + 精确配平（BuildFactorLevel）。</summary>



        public static RandomExprResult BuildL5(string seed, Rat target)
        {
            return BuildFactorLevel(5, NormalizeL5Seed(seed), target);
        }

        struct Fac
        {
            public long N, D;
            public Fac(long n, long d) { N = n; D = d; }
            public double Value { get { return (double)N / D; } }
        }

        static int Prob100(string seed, int salt)
        {
            return (int)(Hash32(seed, salt) % 100u);
        }

        /// <summary>32 位 FNV-1a：种子很长时比按位取 digit 更均匀（L5 是 64 位数字）。</summary>
        static uint Hash32(string seed, int salt)
        {
            uint state = (uint)(2166136261u ^ (uint)salt);
            if (seed != null)
                for (int i = 0; i < seed.Length; i++) state = (state ^ seed[i]) * 16777619u;
            return state;
        }


        static void WrapOuterRandom(RandomExprResult r, string seed)
        {
            if (r == null) return;
            int i = r.Expression.LastIndexOf(" = ", StringComparison.Ordinal);
            int j = r.Latex.LastIndexOf(" = ", StringComparison.Ordinal);
            if (i < 0 || j < 0) return;
            string eb = r.Expression.Substring(0, i);
            string et = r.Latex.Substring(0, j);
            double a = FixA(0.7 + (SD(seed, 42) % 17) / 10.0);
            string asv = a.ToString("0.0", CultureInfo.InvariantCulture);
            int wt = SD(seed, 43) % 3;
            string body, tex, code;
            int dims = 0;
            if (wt == 0)
            {
                body = "lim_{n→∞} (1+" + asv + "/n)^n/e^" + asv + "·(" + eb + ")";
                tex = "\\lim_{n\\to\\infty}\\frac{\\left(1+" + asv + "/n\\right)^{n}}{e^{" + asv + "}}\\left(" + et + "\\right)";
                code = "3";
            }
            else if (wt == 1)
            {
                body = "Σ_{k=0}^{∞} (1/" + asv + ")·((" + asv + "−1)/" + asv + ")^k·(" + eb + ")";
                tex = "\\sum_{k=0}^{\\infty}\\frac{1}{" + asv + "}\\left(\\frac{" + asv + "-1}{" + asv + "}\\right)^{k}\\left(" + et + "\\right)";
                code = "4";
            }
            else
            {
                body = "∫_0^{+∞} " + asv + "e^{−" + asv + "x} dx·(" + eb + ")";
                tex = "\\int_{0}^{+\\infty}" + asv + "e^{-" + asv + "x}\\,dx\\left(" + et + "\\right)";
                code = "0";
                dims = 1;
            }
            r.Expression = body + " = " + PrettyRat(r.Value);
            r.Latex = tex + " = " + LatexNum(r.Value);
            r.TypeCodes = (string.IsNullOrEmpty(r.TypeCodes) ? code : r.TypeCodes + "," + code);
            r.Layers = r.Layers + 1;
            r.Blocks = r.Layers;
            r.Dims += dims;
            r.Kind = "mixed-" + r.Layers.ToString(CultureInfo.InvariantCulture);
            r.Complexity += 4;
        }

        static RandomExprResult BuildFactorLevel(int level, string seed, Rat target, bool mix = true)
        {
            if (!Is325(target)) return null;
            List<Fac> fs;
            if (level == 3)
            {
                int v = SD(seed, 0) % 3;
                if (v == 0) fs = new List<Fac> { new Fac(5,1), new Fac(5,1), new Fac(13,1) };
                else if (v == 1) fs = new List<Fac> { new Fac(25,1), new Fac(13,2), new Fac(2,1) };
                else fs = new List<Fac> { new Fac(65,1), new Fac(5,2), new Fac(2,1) };
            }
            else if (level == 4)
            {
                int n = mix ? 4 + SD(seed, 1) % 2 : 5;
                fs = n == 4
                    ? new List<Fac> { new Fac(5,1), new Fac(13,1), new Fac(5,2), new Fac(2,1) }
                    : new List<Fac> { new Fac(5,1), new Fac(13,1), new Fac(3,1), new Fac(5,6), new Fac(2,1) };
            }
            else
            {
                int n = 6 + SD(seed, 0) % 3;
                if (n == 6) fs = new List<Fac> { new Fac(5,1), new Fac(13,1), new Fac(3,1), new Fac(2,1), new Fac(5,12), new Fac(2,1) };
                else if (n == 7) fs = new List<Fac> { new Fac(5,1), new Fac(13,1), new Fac(3,2), new Fac(2,1), new Fac(2,1), new Fac(5,12), new Fac(2,1) };
                else fs = new List<Fac> { new Fac(5,1), new Fac(13,1), new Fac(3,1), new Fac(1,2), new Fac(2,1), new Fac(2,1), new Fac(5,12), new Fac(2,1) };
            }

            BigInteger productN = 1, productD = 1;
            for (int q = 0; q < fs.Count; q++) { productN *= fs[q].N; productD *= fs[q].D; }
            if (productD.IsZero || productN != target.N * productD) return null;
            Shuffle(fs, seed, level);

            int matrixSlots = level == 5 ? SD(seed, 1) % 3 : 0;
            int numberSlots = level == 5 ? 1 + SD(seed, 2) % 2 : 0;
            List<XNode> chain = new List<XNode>();       // 因子链（AST 叶子），最后统一合成
            StringBuilder codes = new StringBuilder();
            int dims = 0;

            int hideIndex = -1;
            string hideBody = null, hideTex = null;
            long pn = 1, pd = 1;          // 隐藏因子以外的乘积（供 AST 精确配平用）
            if (mix && level <= 4 && fs.Count > 0)
            {
                // 优先隐藏 5/13/25/65 或它们的分数，使 P 不含裸 325。
                List<int> cand = new List<int>();
                for (int i = 0; i < fs.Count; i++)
                {
                    long v = fs[i].N;
                    if (v == 5 || v == 13 || v == 25 || v == 65 || (fs[i].D == 2 && (v == 5 || v == 13))) cand.Add(i);
                }
                if (cand.Count == 0) cand.Add(0);
                hideIndex = cand[SD(seed, 4) % cand.Count];
                for (int j = 0; j < fs.Count; j++)
                {
                    if (j == hideIndex) continue;
                    pn *= fs[j].N;
                    pd *= fs[j].D;
                }
                int kf = SD(seed, 5) % 3;
                string kb, kt;
                if (kf == 0) { kb = "((20³+15³)/(20+15))"; kt = "\\frac{20^{3}+15^{3}}{20+15}"; }
                else if (kf == 1) { kb = "e^{2·ln5+ln13}"; kt = "e^{2\\ln5+\\ln13}"; }
                else { kb = "(17²+6²)"; kt = "\\left(17^{2}+6^{2}\\right)"; }
                hideBody = "(" + kb + ")/(" + RatBody(pn, pd) + ")";
                hideTex = "\\frac{" + kt + "}{" + RatTex(pn, pd) + "}";
            }

            for (int i = 0; i < fs.Count; i++)
            {
                Fac f = fs[i];
                string b, t, code;
                int d;
                if (i == hideIndex)
                {
                    b = hideBody;
                    t = hideTex;
                    code = "0";
                    d = 0;
                    matrixSlots = Math.Max(0, matrixSlots);
                }
                else if (level == 5 && f.D == 1 && f.N >= 2 && matrixSlots > 0)
                {
                    int n = 2 + SD(seed, 3 + i * 2) % 5;
                    int[] diag = new int[n];
                    for (int k = 0; k < n; k++) diag[k] = 1;
                    diag[n - 1] = (int)f.N;
                    long[,] a = BuildDetMatrix(n, diag, seed + "M" + i);
                    SanitizeMatrix(a);
                    if (Determinant(a) != f.N)
                    {
                        a = new long[n, n];
                        for (int k = 0; k < n; k++) a[k, k] = diag[k];
                    }
                // 复合数因子有 40% 概率写成 det(A·B)（det(AB) = det(A)·det(B)）
                long uu = SmallFactor(f.N);
                // 质数因子也允许：A 取幺模矩阵（det = 1），B 的 det 就是因子本身，乘积仍成立
                bool useProd = f.N >= 4 && ((SD(seed, 5 + i * 2) * 7 + i) % 10) < 4;
                if (useProd)
                {
                    long vv = f.N / uu;
                    long[,] ma = MatrixWithDet(uu, seed + "PA" + i, 2 + SD(seed, 6 + i) % 2);
                    long[,] mb = MatrixWithDet(vv, seed + "PB" + i, 2 + SD(seed, 7 + i) % 2);
                    b = "det(" + MatrixBody(ma) + "*" + MatrixBody(mb) + ")";
                    t = "\\det\\left(" + MatrixTex(ma) + "\\cdot" + MatrixTex(mb) + "\\right)";
                }
                else
                {
                    b = "det" + MatrixBody(a);
                    t = "\\det" + MatrixTex(a);
                }
                    code = "1";
                    d = 0;
                    matrixSlots--;
                }
                else if (level == 5 && f.D == 1 && f.N >= 2 && numberSlots > 0)
                {
                    EncodeNumberTheory(f.N, seed, i, out b, out t, out code);
                    d = 0;
                    numberSlots--;
                }
                else
                {
                    EncodeFactor(f, seed, i, out b, out t, out code, out d);
                }

                // 因子链交给 AST：每个因子是一个叶子（带精确有理值），
                // 由 XMul 统一合成，最后用 X.TryBalance 断言精确乘积 == 目标。
                Rat facValue = i == hideIndex
                    ? Rats.Mul(target, new Rat(new BigInteger(pd), new BigInteger(pn)))
                    : new Rat(new BigInteger(f.N), new BigInteger(f.D));
                chain.Add(new XLeaf("\\left(" + t + "\\right)", "(" + b + ")", facValue));
                if (codes.Length > 0) codes.Append(',');
                codes.Append(code);
                dims += d;
            }

            // 经典积分/连乘/极限归一化因子（少量 =1），增加含参与形式复杂度。
            if (mix) {
            double ca = FixA(0.7 + (SD(seed, 15) % 15) / 10.0);
            string cas = ca.ToString("0.0", CultureInfo.InvariantCulture);
            string[] cb = new string[] {
                "(2/(π·ln2))·(∫_0^{+∞} sin x/x dx)·(Σ_{n=1}^{∞} (−1)^{n+1}/n)",
                "∫_0^1 x·eˣ dx",
                "∫_1^{+∞} 1/x² dx",
                "(1/(25π))·∫_0^{2π}∫_0^5 r dr dθ",
                "8·Σ_{m=1}^{∞}Σ_{j=1}^{∞}Σ_{k=1}^{∞} 1/3^{m+j+k}",
                "(5/13)·lim_{x→0} sin(13x)/sin(5x)",
                "(∫_0^1 4t dt)·∫_0^1 x^((∫_0^1 4t dt)−1) dx",
                "(1/26)·∫_0^{2Σ_{n=1}^{∞}(1/2)^n} 13x dx",
                ProdPick(seed, false),
                "(4/(3√π))·Γ(5/2)"
            };
            string[] ct = new string[] {
                "\\frac{2}{\\pi\\ln 2}\\left(\\int_{0}^{+\\infty}\\frac{\\sin x}{x}\\,dx\\right)\\left(\\sum_{n=1}^{\\infty}\\frac{(-1)^{n+1}}{n}\\right)",
                "\\int_{0}^{1}x\\cdot {e}^{x}\\,dx",
                "\\int_{1}^{+\\infty}\\frac{1}{x^{2}}\\,dx",
                "\\frac{1}{25\\pi}\\int_{0}^{2\\pi}\\int_{0}^{5}r\\,dr\\,d\\theta",
                "8\\sum_{m=1}^{\\infty}\\sum_{j=1}^{\\infty}\\sum_{k=1}^{\\infty}\\frac{1}{3^{m+j+k}}",
                "\\frac{5}{13}\\lim_{x\\to0}\\frac{\\sin\\left(13x\\right)}{\\sin\\left(5x\\right)}",
                "\\left(\\int_{0}^{1}4t\\,dt\\right)\\int_{0}^{1}x^{\\left(\\int_{0}^{1}4t\\,dt\\right)-1}\\,dx",
                "\\frac{1}{26}\\int_{0}^{2\\sum_{n=1}^{\\infty}\\left(\\frac{1}{2}\\right)^{n}}13x\\,dx",
                ProdPick(seed, true),
                "\\frac{4}{3\\sqrt{\\pi}}\\cdot\\Gamma\\left(\\frac{5}{2}\\right)"
            };
            int[] cd = new int[] { 1, 1, 1, 2, 0, 0, 1, 1, 0, 0 };
            int[] cc = new int[] { 0, 0, 0, 1, 4, 3, 0, 0, 4, 4 };
            int[] cmin = new int[] { 2, 2, 2, 4, 3, 2, 3, 3, 2, 5 };   // 该模块最低可用等级（极坐标圆域要 2 重积分，只给 L4+）
            int classicCount = level == 3 ? 1 : (level == 4 ? 1 + SD(seed, 16) % 2 : 2 + SD(seed, 16) % 2);
            for (int i = 0; i < classicCount; i++)
            {
                int base0 = (SD(seed, 17 + i * 3) + i) % cd.Length;
                int idx = base0;
                for (int k = 0; k < cd.Length; k++)
                {
                    int c2 = (base0 + k) % cd.Length;
                    if (cmin[c2] <= level) { idx = c2; break; }
                }
                chain.Add(new XLeaf("\\left(" + ct[idx] + "\\right)", "(" + cb[idx] + ")", Rats.One));

                codes.Append(',').Append(cc[idx]);
                dims += cd[idx];
            }

            }
            // L5 嵌套层（30% 概率）：上下限各自是一棵独立的嵌套树，外层模块在 5 种恒等式里随机。
            if (mix && level == 5 && Prob100(seed, 72) < 30)
            {
                int dA = 1 + (int)(Hash32(seed, 73) % 3u);
                int dB = 1 + (int)(Hash32(seed, 76) % 3u);
                double vA, vB;
                XNode A = NestedBound(dA, (int)(Hash32(seed, 74) & 0xFFFFu), out vA);
                XNode B = NestedBound(dB, (int)(Hash32(seed, 77) & 0xFFFFu), out vB);
                // 两棵树可能恰好同值（同形状同底数），那样 (B−A) 会是 0；重抽几次，实在不行改成常数。
                for (int guard = 0; guard < 6 && Math.Abs(vB - vA) < 1e-6; guard++)
                {
                    dB = 1 + (int)(Hash32(seed, 79 + guard) % 3u);
                    B = NestedBound(dB, (int)(Hash32(seed, 85 + guard) & 0xFFFFu), out vB);
                }
                if (Math.Abs(vB - vA) < 1e-6) { B = X.Num(3 + (int)(Hash32(seed, 91) % 3u)); vB = 3; }
                // 只在 B <= A（积分反向）时才加绝对值，约一半情况不带 |·|
                XNode mod = NestedModule(A, B, (int)(Hash32(seed, 78) % 3u), !(vB > vA + 1e-9));
                chain.Add(new XLeaf(mod.Latex, mod.Text, Rats.One));
                codes.Append(',').Append('0');
            }

            // 可验证陷阱层（AST 构造）：有限域 / 概率期望 / Fubini 拆分，值恒等于 1。
            string trapNote = null;
            if (mix && level >= 4 && Prob100(seed, 70) < 65)
            {
                TrapSpec trap = Traps.Build(level, seed, 70);
                if (trap != null)
                {
                    chain.Add(new XLeaf("\\left(" + trap.Node.Latex + "\\right)", "(" + trap.Node.Text + ")", Rats.One));

                    codes.Append(',').Append(trap.Code);
                    if (trap.Name == "fubini") dims += 2;
                    trapNote = trap.Title;
                }
            }

            // 行列式进乘方位置（L5）：10% 底为 =1 分数、指数为 2–6 阶行列式；
            // 5% 行列式的乘方是行列式（det=1 的矩阵 ^ 2–6 阶行列式）。
            // 值恒为 1，可塞到因子链任意位置（位置、阶数均按种子随机）。
            if (mix && level == 5)
            {
                // 记录命中了哪种形态，避免“只命中 B”时误发成 A。
                List<int> kinds = new List<int>();
                if (Prob100(seed, 92) < 10) kinds.Add(0);   // A：底为 =1 分数，指数为 2–6 阶行列式
                if (Prob100(seed, 94) < 5) kinds.Add(1);    // B：行列式的乘方是行列式（det^det）
                if (kinds.Count > 0)
                {
                    List<string> cl = new List<string>(codes.ToString().Split(','));
                    for (int dp = 0; dp < kinds.Count; dp++)
                    {
                        int dpDims;
                        XNode leaf = DetPowFactor(seed, 96 + dp * 3, kinds[dp], out dpDims);
                        int pos = (int)(Hash32(seed, 97 + dp * 3) % (uint)(chain.Count + 1));
                        chain.Insert(pos, leaf);
                        dims += dpDims;
                        if (pos >= cl.Count) cl.Add("1");
                        else cl.Insert(pos, "1");
                    }
                    codes.Clear();
                    for (int c = 0; c < cl.Count; c++) { if (c > 0) codes.Append(','); codes.Append(cl[c]); }
                }
            }

            // ---- 因子链的 AST 合成 + 精确配平 ----
            // 每个因子都是带精确有理值的叶子；XMul 统一渲染出 LaTeX 和纯文本，
            // TryBalance 断言"所有因子精确值之积 == 目标"，把 325 的配平从字符串拼接里独立出来。
            if (chain.Count == 0) return null;
            string balanceError;
            if (!X.TryBalance(chain, target, out balanceError)) return null;
            XNode composed = X.Mul(chain);
            StringBuilder body = new StringBuilder(composed.Text);
            StringBuilder tex = new StringBuilder(composed.Latex);

            // 非平凡恒等对：ln2 的交错级数 × 1/ln2 的积分，不是裸 1。
            if (mix && level >= 5 && Prob100(seed, 60) < 55)
            {
                body.Append("·(Σ_{n=1}^{∞} (−1)^{n+1}/n)·(1/∫_0^1 1/(1+x) dx)");
                tex.Append("\\cdot\\left(\\sum_{n=1}^{\\infty}\\frac{(-1)^{n+1}}{n}\\right)\\cdot\\left(\\frac{1}{\\int_{0}^{1}\\frac{1}{1+x}\\,dx}\\right)");
                codes.Append(",4,0");
                dims += 1;
            }
            // 参数联动：内层积分值 A 直接作为外层幂指参数，外层积分仍为 1。
            if (mix && level >= 5 && Prob100(seed, 61) < 45)
            {
                body.Append("·(∫_0^1 (∫_0^1 t^{−1/5} dt) x^((∫_0^1 t^{−1/5} dt)−1) dx)");
                tex.Append("\\cdot\\left(\\int_{0}^{1}\\left(\\int_{0}^{1}t^{-1/5}\\,dt\\right)x^{\\left(\\int_{0}^{1}t^{-1/5}\\,dt\\right)-1}\\,dx\\right)");
                codes.Append(",0");
                dims += 1;
            }
            // ζ(4) 归一化因子，加入特殊函数。
            if (mix && level >= 5 && Prob100(seed, 63) < 50)
            {
                body.Append("·((90/π⁴)·ζ(4))");
                tex.Append("\\cdot\\left(\\frac{90}{\\pi^{4}}\\zeta(4)\\right)");
                codes.Append(",4");
            }
            // 加减 0 陷阱：两个都等于 ln2 的表达式相减。
            if (mix && level >= 4 && Prob100(seed, 62) < 40)
            {
                body.Append(" + (Σ_{n=1}^{∞} (−1)^{n+1}/n − ∫_0^1 1/(1+x) dx)");
                tex.Append("+\\left(\\sum_{n=1}^{\\infty}\\frac{(-1)^{n+1}}{n}-\\int_{0}^{1}\\frac{1}{1+x}\\,dx\\right)");
            }
            int depth = !mix ? 0 : (level == 3 ? 0 : (level == 4 ? 1 + SD(seed, 18) % 2 : 2 + SD(seed, 18) % 2));
            bool multiInf = level == 5 && ((SD(seed, 19) * 37 + SD(seed, 20) * 11) % 100) < 5;
            if (multiInf) depth += 3;
            for (int i = 0; i < depth; i++)
            {
                double la = FixA(0.7 + (SD(seed, 21 + i * 2) % 17) / 10.0);
                string las = la.ToString("0.0", CultureInfo.InvariantCulture);
                int wt = (SD(seed, 30 + i * 3) + i) % 3;
                if (wt == 0)
                {
                    body.Insert(0, "lim_{n→∞} (1+" + las + "/n)^n/e^" + las + "·(");
                    body.Append(')');
                    tex.Insert(0, "\\lim_{n\\to\\infty}\\frac{\\left(1+" + las + "/n\\right)^{n}}{e^{" + las + "}}\\left(");
                    tex.Append("\\right)");
                    codes.Append(",3");
                }
                else if (wt == 1)
                {
                    body.Insert(0, "Σ_{k=0}^{∞} (1/" + las + ")·((" + las + "−1)/" + las + ")^k·(");
                    body.Append(')');
                    tex.Insert(0, "\\sum_{k=0}^{\\infty}\\frac{1}{" + las + "}\\left(\\frac{" + las + "-1}{" + las + "}\\right)^{k}\\left(");
                    tex.Append("\\right)");
                    codes.Append(",4");
                }
                else
                {
                    body.Insert(0, "∫_0^{+∞} " + las + "e^{−" + las + "x} dx·(");
                    body.Append(')');
                    tex.Insert(0, "\\int_{0}^{+\\infty}" + las + "e^{-" + las + "x}\\,dx\\left(");
                    tex.Append("\\right)");
                    codes.Append(",0");
                    dims++;
                }
            }

            body.Append(" = ").Append(PrettyRat(target));
            tex.Append(" = ").Append(LatexNum(target));

            int layers = codes.ToString().Split(',').Length;
            RandomExprResult r = new RandomExprResult();
            r.Expression = body.ToString();
            r.Latex = tex.ToString();
            r.Identity = true;
            r.Complexity = 30 + level * 10 + layers * 3;
            r.Level = level;
            r.Seed = seed;
            r.Kind = level == 5 ? "hard-5" : "mixed-" + layers.ToString(CultureInfo.InvariantCulture);
            r.Family = level == 5 ? "factor-number" : "factor-calculus";
            r.Blocks = layers;
            r.Layers = layers;
            r.Dims = dims;
            r.TypeCodes = codes.ToString();
            r.TypeNames = level == 5 ? "矩阵 + 数论 + 分析 + 嵌套极限" : "分析因子 + 经典积分 + 嵌套极限";
            r.Value = target;
            r.ValueOk = true;
            r.Note = "因子全部非 1，含隐藏系数、经典积分和嵌套极限"
                + (trapNote == null ? "" : "；陷阱：" + trapNote);
            r.NumericKind = "closed";
            r.NumericValue = null;
            r.ClosedForm = delegate { return target; };
            return r;
        }

        static void Shuffle(List<Fac> fs, string seed, int salt)
        {
            uint state = (uint)(2166136261u ^ (uint)salt);
            for (int i = 0; i < seed.Length; i++) state = (state ^ seed[i]) * 16777619u;
            for (int i = fs.Count - 1; i > 0; i--)
            {
                state = state * 1664525u + 1013904223u;
                int j = (int)(state % (uint)(i + 1));
                Fac t = fs[i]; fs[i] = fs[j]; fs[j] = t;
            }
        }

        static void EncodeFactor(Fac f, string seed, int pos, out string body, out string tex, out string code, out int dims)
        {
            int v = SD(seed, 10 + pos * 3) % 4;
            long en = f.D - f.N, ed = f.N;
            string eb = ExpBody(en, ed);
            string et = ExpTex(en, ed);
            if (v == 0)
            {
                body = "∫_0^1 x^(" + eb + ") dx";
                tex = "\\int_{0}^{1}x^{" + et + "}\\,dx";
                code = "0"; dims = 1;
            }
            else if (v == 1)
            {
                body = "1/∫_0^1 x^(" + ExpBody(f.N - f.D, f.D) + ") dx";
                tex = "\\frac{1}{\\int_{0}^{1}x^{" + ExpTex(f.N - f.D, f.D) + "}\\,dx}";
                code = "0"; dims = 1;
            }
            else if (v == 3)
            {
                List<long> cf = ContinuedFraction(f.N, f.D);
                if (cf.Count >= 2)
                {
                    body = CfBody(cf, 0);
                    tex = CfTex(cf, 0);
                    code = "4"; dims = 0;
                }
                else
                {
                    body = "∫_0^1 x^(" + eb + ") dx";
                    tex = "\\int_{0}^{1}x^{" + et + "}\\,dx";
                    code = "0"; dims = 1;
                }
            }
            else
            {
                if (f.N > f.D)
                {
                    long rn = f.N - f.D, rd = f.N;
                    body = "Σ_{n=0}^{∞} (" + RatBody(rn, rd) + ")^n";
                    tex = "\\sum_{n=0}^{\\infty}\\left(" + RatTex(rn, rd) + "\\right)^{n}";
                    code = "4"; dims = 0;
                }
                else
                {
                    body = "∫_0^1 x^(" + eb + ") dx";
                    tex = "\\int_{0}^{1}x^{" + et + "}\\,dx";
                    code = "0"; dims = 1;
                }
            }
        }

        static List<long> ContinuedFraction(long n, long d)
        {
            List<long> cf = new List<long>();
            while (d != 0)
            {
                long a = n / d;
                cf.Add(a);
                long r = n - a * d;
                n = d;
                d = r;
            }
            return cf;
        }

        static string CfBody(List<long> cf, int i)
        {
            long a = cf[i];
            if (i == cf.Count - 1) return a.ToString(CultureInfo.InvariantCulture);
            return a.ToString(CultureInfo.InvariantCulture) + "+1/(" + CfBody(cf, i + 1) + ")";
        }

        static string CfTex(List<long> cf, int i)
        {
            long a = cf[i];
            if (i == cf.Count - 1) return a.ToString(CultureInfo.InvariantCulture);
            return a.ToString(CultureInfo.InvariantCulture) + "+\\frac{1}{" + CfTex(cf, i + 1) + "}";
        }

        static string RatBody(long n, long d)
        {
            if (d == 1) return n.ToString(CultureInfo.InvariantCulture);
            return n.ToString(CultureInfo.InvariantCulture) + "/" + d.ToString(CultureInfo.InvariantCulture);
        }

        static string RatTex(long n, long d)
        {
            if (d == 1) return n.ToString(CultureInfo.InvariantCulture);
            return "\\frac{" + n.ToString(CultureInfo.InvariantCulture) + "}{" + d.ToString(CultureInfo.InvariantCulture) + "}";
        }

        static string ExpBody(long n, long d)
        {
            if (n == 0) return "0";
            return RatBody(n, d);
        }

        static string ExpTex(long n, long d)
        {
            if (n == 0) return "0";
            return RatTex(n, d);
        }

        static void EncodeNumberTheory(long m, string seed, int pos, out string body, out string tex, out string code)
        {
            int v = SD(seed, 50 + pos * 2) % 7;
            code = "4";
            if (v == 0)
            {
                PhiSum(m, out body, out tex);
                return;
            }
            if (v == 1)
            {
                body = "⌊" + m.ToString(CultureInfo.InvariantCulture) + " + 4/5⌋";
                tex = "\\left\\lfloor " + m.ToString(CultureInfo.InvariantCulture) + "+\\frac{4}{5}\\right\\rfloor";
                return;
            }
            if (v == 2)
            {
                body = "⌈" + m.ToString(CultureInfo.InvariantCulture) + " − 1/5⌉";
                tex = "\\left\\lceil " + m.ToString(CultureInfo.InvariantCulture) + "-\\frac{1}{5}\\right\\rceil";
                return;
            }
            if (v == 5)
            {
                body = "(1/(2πi))·∮_C z/(z−" + m.ToString(CultureInfo.InvariantCulture) + ") dz";
                tex = "\\frac{1}{2\\pi i}\\oint_{C}\\frac{z}{z-" + m.ToString(CultureInfo.InvariantCulture) + "}\\,dz";
                return;
            }
            if (v == 6)
            {
                body = "lim_{x→0} sin(" + m.ToString(CultureInfo.InvariantCulture) + "x)/x";
                tex = "\\lim_{x\\to0}\\frac{\\sin(" + m.ToString(CultureInfo.InvariantCulture) + "x)}{x}";
                return;
            }            long p = m + 3;
            long q = 2 + SD(seed, 51 + pos * 2) % 3;
            long a = m + p * q;
            body = a.ToString(CultureInfo.InvariantCulture) + " − " + p.ToString(CultureInfo.InvariantCulture)
                + "·⌊" + a.ToString(CultureInfo.InvariantCulture) + "/" + p.ToString(CultureInfo.InvariantCulture) + "⌋";
            tex = a.ToString(CultureInfo.InvariantCulture) + "-" + p.ToString(CultureInfo.InvariantCulture)
                + "\\left\\lfloor\\frac{" + a.ToString(CultureInfo.InvariantCulture) + "}{" + p.ToString(CultureInfo.InvariantCulture) + "}\\right\\rfloor";
        }

        static void PhiSum(long m, out string body, out string tex)
        {
            body = "";
            tex = "";
            for (long d = 1; d <= m; d++)
            {
                if (m % d != 0) continue;
                if (body.Length > 0) { body += "+"; tex += "+"; }
                body += "φ(" + d.ToString(CultureInfo.InvariantCulture) + ")";
                tex += "\\varphi(" + d.ToString(CultureInfo.InvariantCulture) + ")";
            }
        }

        static BigInteger Determinant(long[,] a)
        {
            int n = a.GetLength(0);
            if (n == 0) return 0;
            if (n == 1) return a[0, 0];
            BigInteger det = 0;
            for (int c = 0; c < n; c++)
            {
                if (a[0, c] == 0) continue;
                long[,] m = new long[n - 1, n - 1];
                for (int i = 1; i < n; i++)
                {
                    int cc = 0;
                    for (int j = 0; j < n; j++)
                    {
                        if (j == c) continue;
                        m[i - 1, cc++] = a[i, j];
                    }
                }
                BigInteger sub = Determinant(m);
                det += ((c & 1) == 0 ? 1 : -1) * a[0, c] * sub;
            }
            return det;
        }

        static void SanitizeMatrix(long[,] a)
        {
            int n = a.GetLength(0);
            for (int pass = 0; pass < 12; pass++)
            {
                bool bad = false;
                for (int i = 0; i < n && !bad; i++)
                    for (int j = 0; j < n; j++)
                    {
                        long v = a[i, j] < 0 ? -a[i, j] : a[i, j];
                        if (v == 325 || v == 650 || v == 975 || v == 1300 || v == 1950) { bad = true; break; }
                    }
                if (!bad) return;
                if (n >= 2) for (int c = 0; c < n; c++) a[0, c] += a[1, c];
            }
        }

        static long[,] BuildDetMatrix(int n, int[] diag, string seed)
        {
            long[,] a = new long[n, n];
            for (int i = 0; i < n; i++) a[i, i] = diag[i];
            uint state = 2166136261u;
            for (int i = 0; i < seed.Length; i++) state = (state ^ seed[i]) * 16777619u;
            int steps = 16 + SD(seed, 1) % 8;
            for (int s = 0; s < steps; s++)
            {
                state = state * 1664525u + 1013904223u;
                int i = (int)(state % (uint)n);
                state = state * 1664525u + 1013904223u;
                int j = (int)(state % (uint)n);
                if (i == j) j = (j + 1) % n;
                state = state * 1664525u + 1013904223u;
                int k = (int)(state % 5u) - 2;
                if (k == 0) k = 1;
                if ((state & 0x10000u) == 0)
                {
                    for (int c = 0; c < n; c++) a[i, c] += k * a[j, c];
                }
                else
                {
                    for (int rr = 0; rr < n; rr++) a[rr, j] += k * a[rr, i];
                }
            }
            return a;
        }

        /// <summary>最小的非平凡因子（质数返回自身，1 返回 1）。</summary>
        static long SmallFactor(long n)
        {
            if (n < 4) return 1;
            for (long d = 2; d * d <= n; d++) if (n % d == 0) return d;
            return 1;
        }

        /// <summary>造一个行列式恰好等于 det 的 n×n 整数矩阵（对角阵 + 消元扰乱）。</summary>
        static long[,] MatrixWithDet(long det, string seed, int n)
        {
            if (n < 2) n = 2;
            if (n > 3) n = 3;
            int[] diag = new int[] { 1, 1, (int)det };
            long[,] a = BuildDetMatrix(n, diag, seed);
            SanitizeMatrix(a);
            if (Determinant(a) != det)
            {
                a = new long[n, n];
                for (int k = 0; k < n - 1; k++) a[k, k] = 1;
                a[n - 1, n - 1] = det;
            }
            return a;
        }

        /// <summary>2–6 阶整数矩阵，行列式恰好等于 det（对角 + 消元扰乱）。</summary>
        static long[,] RandomDetMatrix(string seed, int order, long det)
        {
            if (order < 2) order = 2;
            if (order > 6) order = 6;
            int[] diag = new int[order];
            for (int k = 0; k < order; k++) diag[k] = 1;
            diag[order - 1] = (int)det;
            long[,] a = BuildDetMatrix(order, diag, seed);
            SanitizeMatrix(a);
            if (Determinant(a) != det)
            {
                a = new long[order, order];
                for (int k = 0; k < order - 1; k++) a[k, k] = 1;
                a[order - 1, order - 1] = det;
            }
            return a;
        }

        /// <summary>
        /// “行列式出现在乘方位置”的 =1 因子：
        ///   kind 0 —— 底是一个"不显眼的 1"（定积分/级数恒等于 1，不是 k/k 那种一眼化简），
        ///             指数是 2–6 阶行列式：(\int_0^1 2x dx)^{\det A}；
        ///   kind 1 —— 行列式的乘方是行列式：底是 det=1 的 2–6 阶矩阵，指数是 2–6 阶行列式：(\det U)^{\det B}。
        /// 值恒等于 1（1 的任意整数次幂 = 1），不改变整体乘积。
        /// </summary>
        static XNode DetPowFactor(string seed, int salt, int kind, out int dims)
        {
            dims = 0;
            int orderE = 2 + (int)(Hash32(seed, salt) % 5u);
            long detE = 2 + (int)(Hash32(seed, salt + 1) % 60u);
            long[,] mE = RandomDetMatrix(seed + salt, orderE, detE);

            string bL, bT;
            if (kind == 0)
            {
                // 底必须"看起来是个真式子"，但值恰好是 1：定积分 ∫₀¹ kx^{k−1} dx = 1 / 几何级数 Σ(1/2)ⁿ = 1。
                // 不用 k/k —— 那会一眼化简成 1^n。
                switch ((int)(Hash32(seed, salt + 2) % 4u))
                {
                    case 0:
                        bL = "\\int_{0}^{1}2x\\,dx"; bT = "(∫_0^1 2x dx)"; dims = 1; break;
                    case 1:
                        bL = "\\int_{0}^{1}3x^{2}\\,dx"; bT = "(∫_0^1 3x² dx)"; dims = 1; break;
                    case 2:
                        bL = "\\int_{0}^{1}4x^{3}\\,dx"; bT = "(∫_0^1 4x³ dx)"; dims = 1; break;
                    default:
                        bL = "\\sum_{n=1}^{\\infty}\\left(\\frac{1}{2}\\right)^{n}";
                        bT = "(Σ_{n=1}^{∞} (1/2)ⁿ)";
                        break;
                }
            }
            else
            {
                int orderB = 2 + (int)(Hash32(seed, salt + 2) % 5u);
                long[,] mB = RandomDetMatrix(seed + salt + 3, orderB, 1);
                bL = "\\det" + MatrixTex(mB);
                bT = "det" + MatrixBody(mB);
            }

            string eL = "\\det" + MatrixTex(mE);
            string eT = "det" + MatrixBody(mE);
            return new XLeaf("\\left(" + bL + "\\right)^{" + eL + "}", "(" + bT + ")^{" + eT + "}", Rats.One);
        }

        static string MatrixBody(long[,] a)
        {
            int n = a.GetLength(0);
            StringBuilder sb = new StringBuilder("[");
            for (int i = 0; i < n; i++)
            {
                if (i > 0) sb.Append(';');
                sb.Append('[');
                for (int j = 0; j < n; j++)
                {
                    if (j > 0) sb.Append(',');
                    sb.Append(a[i, j].ToString(CultureInfo.InvariantCulture));
                }
                sb.Append(']');
            }
            sb.Append(']');
            return sb.ToString();
        }

        static string MatrixTex(long[,] a)
        {
            int n = a.GetLength(0);
            StringBuilder sb = new StringBuilder("\\begin{pmatrix}");
            for (int i = 0; i < n; i++)
            {
                if (i > 0) sb.Append("\\\\");
                for (int j = 0; j < n; j++)
                {
                    if (j > 0) sb.Append("&");
                    sb.Append(a[i, j].ToString(CultureInfo.InvariantCulture));
                }
            }
            sb.Append("\\end{pmatrix}");
            return sb.ToString();
        }
        static double FixA(double a)
        {
            // 避免 a=1，导致 Σ ((a-1)/a)^k 退化成 Σ 0^k。
            if (Math.Abs(a - 1.0) < 0.051) a += 0.3;
            return a;
        }

        static int SD(string s, int i)
        {
            if (s == null || i < 0 || i >= s.Length) return 0;
            char c = s[i];
            return c >= '0' && c <= '9' ? c - '0' : 0;
        }

        public static RandomExprResult BuildLevel(int level, Rat target, Random rnd)
        {
            if (rnd == null) rnd = new Random();
            return BuildLevel(level, target, rnd, MakeSeed(level, rnd));
        }

        public static RandomExprResult BuildLevel(int level, Rat target, Random rnd, string seed, bool allowHard = false)
        {
            if (rnd == null) rnd = new Random();
            level = ClampLevel(level);
            string clean = NormalizeSeed(seed, level);
            if (level == 1) return BuildElementary(clean, target);
            if (level == 3 && allowHard && Is325(target))
            {
                RandomExprResult h3 = BuildFactorLevel(3, clean, target);
                if (h3 != null) return h3;
            }
            if (level == 4 && allowHard && Is325(target))
            {
                bool allZero = true;
                for (int i = 0; i < 8 && i < clean.Length; i++) if (clean[i] != '0') { allZero = false; break; }
                if (!allZero)
                {
                    int mode = Prob100(clean, 41);
                    if (mode < 30)
                    {
                        RandomExprResult old = TryBuildHardL4(clean, target);
                        if (old != null) { WrapOuterRandom(old, clean); return old; }
                    }
                    RandomExprResult h4 = BuildFactorLevel(4, clean, target, mode >= 40);
                    if (h4 != null) return h4;
                }
            }
            List<Slot> slots = ParseSlots(clean, level);

            int maxLayers = MaxLayers(level);
            if (level == 3) maxLayers = 3;   // L3 轻微降复杂度
            if (level == 4)
            {
                // 只有“8 个类型位全 0”的纯积分种子才允许八重；
                // 其余 L4 混合式最多 4 层，避免一屏塞不下的超长式子。
                bool allTypeZero = true;
                for (int i = 0; i < slots.Count; i++) if (slots[i].Type != 0) { allTypeZero = false; break; }
                if (!allTypeZero) maxLayers = 4;
            }
            int maxDims = maxLayers;
            List<Slot> main = new List<Slot>();
            List<Slot> extra = new List<Slot>();
            int usedDims = 0;
            bool stopped = false;

            for (int i = 0; i < slots.Count; i++)
            {
                Slot s = slots[i];
                if (i == 0 && s.Type == 9) s.Type = 0;    // 第一层必须有
                if (i >= maxLayers || stopped)
                {
                    extra.Add(s);
                    continue;
                }
                if (i > 0 && s.Type == 9)                  // 第二层起 9 = 没有这一层
                {
                    stopped = true;
                    extra.Add(s);
                    continue;
                }
                int type = MapAllowed(level, s.Type);
                int cost = DimCost(type);
                if (cost > 0 && usedDims + cost > maxDims)
                {
                    if (type <= 2 && usedDims + 1 <= maxDims)
                    {
                        type = 0;
                        cost = 1;
                    }
                    else
                    {
                        stopped = true;
                        extra.Add(s);
                        continue;
                    }
                }
                s.Type = type;
                main.Add(s);
                usedDims += cost;
            }
            if (main.Count == 0)
            {
                Slot s = slots[0];
                s.Type = 0;
                main.Add(s);
                usedDims = 1;
            }

            string coef;
            string coefL;
            if (Is325(target)) HiddenCore325(clean, out coef, out coefL);
            else { coef = Coef(target); coefL = LatexCoef(target); }

            StringBuilder core = new StringBuilder(coef);
            StringBuilder coreL = new StringBuilder(coefL);
            int complexity = 0;

            XNode tree = BuildOuterNode(main, 0, new XLeaf(coreL.ToString(), core.ToString(), target));
            string latex = tree.Latex;
            string body = tree.Text;

            for (int i = 0; i < main.Count; i++) complexity += LayerComplexity(main[i].Type);
            complexity += main.Count;

            body += " = " + PrettyRat(target);
            latex += " = " + LatexNum(target);

            string codes = "";
            string names = "";
            for (int i = 0; i < main.Count; i++)
            {
                if (i > 0) { codes += ","; names += " + "; }
                codes += main[i].Type.ToString(CultureInfo.InvariantCulture);
                names += TypeNames[main[i].Type];
            }

            RandomExprResult r = new RandomExprResult();
            r.Expression = body;
            r.Latex = latex;
            r.Identity = true;
            r.Level = level;
            r.Seed = clean;
            r.Family = "mixed";
            r.Layers = main.Count;
            r.Blocks = main.Count;
            r.Dims = usedDims;
            r.TypeCodes = codes;
            r.TypeNames = names;
            r.Kind = "mixed-" + main.Count.ToString(CultureInfo.InvariantCulture);
            r.Complexity = complexity;
            r.Value = target;
            r.ValueOk = true;
            r.NumericKind = "closed";
            r.NumericValue = null;
            Rat closed = target;
            r.ClosedForm = delegate { return closed; };
            return r;
        }

        static int MapAllowed(int level, int type)
        {
            if (type == 9) return 9;
            if (type < 0 || type > 8) return 0;
            if (level == 1)
            {
                if (type == 1 || type == 2) return 0;
                if (type == 5 || type == 7) return 3;
                if (type == 6) return 4;
                return type;
            }
            if (level == 2)
            {
                if (type == 2) return 1;
                if (type == 5 || type == 6) return 0;   // L2 不嵌套极限/级数+积分
                if (type == 7) return 3;
                if (type == 8) return 4;
                return type;
            }
            return type;
        }

        static int DimCost(int type)
        {
            if (type == 0) return 1;
            if (type == 1) return 2;
            if (type == 2) return 3;
            if (type == 5 || type == 6) return 1;   // 极限+积分 / 级数+积分 也含一层积分
            return 0;
        }

        static int LayerComplexity(int type)
        {
            if (type == 0) return 4;
            if (type == 1) return 7;
            if (type == 2) return 10;
            if (type == 3) return 3;
            if (type == 4) return 3;
            if (type == 5) return 6;
            if (type == 6) return 6;
            if (type == 7) return 7;
            if (type == 8) return 7;
            return 2;
        }

        /// <summary>
        /// 从外到内组装（AST 版）：连续积分层折叠成嵌套积分，极限/级数层再包裹。
        /// 返回 XNode，由 ExprAst 统一渲染出 LaTeX 与纯文本，不再维护两份字符串。
        /// </summary>
        static XNode BuildOuterNode(List<Slot> main, int index, XNode core)
        {
            if (index >= main.Count) return core;
            int type = main[index].Type;

            if (type <= 2)
            {
                List<XNode> weights = new List<XNode>();
                List<XNode> los = new List<XNode>();
                List<XNode> his = new List<XNode>();
                List<string> vars = new List<string>();
                int j = index;
                while (j < main.Count && main[j].Type <= 2)
                {
                    Slot s = main[j];
                    int idx = s.Index + 1;
                    int dims = s.Type == 0 ? 1 : (s.Type == 1 ? 2 : 3);
                    for (int d = 0; d < dims; d++)
                    {
                        string v = (d == 0 ? "x" : (d == 1 ? "y" : "z")) + "_" + idx.ToString(CultureInfo.InvariantCulture);
                        LayerSpec w = BuildLayer(s.Family, (s.Param + d) % 100, v);
                        los.Add(new XLeaf(w.LatexLow, w.Low, null));
                        his.Add(new XLeaf(w.LatexHigh, w.High, null));
                        if (!IsOne(w.Body))
                            weights.Add(new XLeaf("\\left(" + w.Latex + "\\right)", "(" + w.Body + ")", Rats.One));
                        vars.Add(v);
                    }
                    j++;
                }

                XNode inner = BuildOuterNode(main, j, core);
                if (j < main.Count) inner = X.TextParen(inner);      // 纯文本保留括号
                weights.Add(inner);
                XNode body = X.Mul(weights);
                for (int k = los.Count - 1; k >= 0; k--) body = X.Int(los[k], his[k], body, vars[k]);
                return body;
            }

            Slot slot = main[index];
            int ix = slot.Index + 1;
            string n = "n_" + ix.ToString(CultureInfo.InvariantCulture);
            string kv = "k_" + ix.ToString(CultureInfo.InvariantCulture);
            string N = "N_" + ix.ToString(CultureInfo.InvariantCulture);
            XNode inner2 = BuildOuterNode(main, index + 1, core);
            XNode varN = new XLeaf(N, N, null);

            if (type == 3)
            {
                XLeaf l = new XLeaf("\\left(" + BuildLimitFactor(slot.Family, slot.Param, n, true) + "\\right)",
                    "(" + BuildLimitFactor(slot.Family, slot.Param, n, false) + ")", Rats.One);
                return X.Lim(X.Inf, X.Mul(l, X.TextParen(inner2)), n);
            }
            if (type == 4)
            {
                XLeaf s = new XLeaf("\\left(" + BuildSeriesFactor(slot.Family, slot.Param, kv, true) + "\\right)",
                    "(" + BuildSeriesFactor(slot.Family, slot.Param, kv, false) + ")", Rats.One);
                return X.Sum(X.Num(1), X.Inf, X.Mul(s, X.TextParen(inner2)), kv);
            }
            if (type == 5 || type == 6)
            {
                string v = "x_" + ix.ToString(CultureInfo.InvariantCulture);
                LayerSpec w = BuildLayer(slot.Family, slot.Param, v);
                List<XNode> fs = new List<XNode>();
                if (!IsOne(w.Body))
                    fs.Add(new XLeaf("\\left(" + w.Latex + "\\right)", "(" + w.Body + ")", Rats.One));
                if (type == 5)
                {
                    fs.Add(new XLeaf("\\left(" + BuildLimitFactor(slot.Family, slot.Param, n, true) + "\\right)",
                        "(" + BuildLimitFactor(slot.Family, slot.Param, n, false) + ")", Rats.One));
                }
                else
                {
                    fs.Add(new XLeaf("\\left(" + BuildSeriesFactor(slot.Family, slot.Param, kv, true) + "\\right)",
                        "(" + BuildSeriesFactor(slot.Family, slot.Param, kv, false) + ")", Rats.One));
                }
                fs.Add(X.TextParen(inner2));
                XNode integ = X.Int(new XLeaf(w.LatexLow, w.Low, null),
                    new XLeaf(w.LatexHigh, w.High, null), X.Mul(fs), v);
                return type == 5 ? X.Lim(X.Inf, integ, n) : X.Sum(X.Num(1), X.Inf, integ, kv);
            }
            if (type == 7)
            {
                XLeaf l = new XLeaf("\\left(" + BuildLimitFactor(slot.Family, slot.Param, N, true) + "\\right)",
                    "(" + BuildLimitFactor(slot.Family, slot.Param, N, false) + ")", Rats.One);
                XLeaf s = new XLeaf("\\left(" + BuildSeriesFactor(slot.Family, slot.Param, kv, true) + "\\right)",
                    "(" + BuildSeriesFactor(slot.Family, slot.Param, kv, false) + ")", Rats.One);
                XNode sum = X.Sum(X.Num(1), varN, X.Mul(s, l), kv);
                XNode group = X.Group("[", "]", sum);
                return X.Lim(X.Inf, X.Mul(group, X.TextParen(inner2)), N);
            }
            if (type == 8)
            {
                string pr = Primes[(slot.Family + slot.Param) % Primes.Length];
                XNode product = X.Mul(new XLeaf(kv, kv, null), new XLeaf("\\sqrt{" + pr + "}", "√" + pr, null));
                XNode fracPart = X.Call("fracpart", product);
                XNode sum = X.Sum(X.Num(1), varN, fracPart, kv);
                XNode group = X.Group("[", "]", sum);
                return X.Lim(X.Inf, X.Mul(X.Frac(X.Num(2), varN), group, X.TextParen(inner2)), N);
            }
            return inner2;
        }

        static void AppendIntegral(StringBuilder prefix, LayerSpec w, bool latex)
        {
            if (latex)
                prefix.Append("\\int_{").Append(w.LatexLow).Append("}^{").Append(w.LatexHigh).Append("} ");
            else
                prefix.Append("∫_").Append(w.Low).Append("^{").Append(w.High).Append("} ");
        }

        static void AppendWeight(StringBuilder weights, LayerSpec w, bool latex)
        {
            if (IsOne(w.Body)) return;
            if (weights.Length > 0) weights.Append(latex ? "\\cdot " : "· ");
            weights.Append(latex ? "\\left(" + w.Latex + "\\right)" : "(" + w.Body + ")");
        }

        static bool IsOne(string s)
        {
            return string.IsNullOrEmpty(s) || s.Trim() == "1";
        }

        /// <summary>极限层：任意 family 都给一个极限 = 1 的因子。</summary>
        /// <summary>
        /// 值已知的常数表达式，用作积分的上下限。深度 1~3：积分 ∫_0^C 2t dt = C²、
        /// 级数 Σ1/2^n = 1、极限 (1+1/n)^n → e、连乘 Wallis = π/2。
        /// </summary>
        static XNode NestedBound(int depth, int k, out double value)
        {
            if (depth <= 0) { value = 2 + k % 3; return X.Num(2 + k % 3); }
            string vn = new string[] { "n", "m", "p", "q" }[depth % 4];
            double cv;
            XNode raw = NestedBound(depth - 1, k / 4, out cv);
            XNode c = (raw is XNum || raw is XLeaf) ? raw : X.Group("{", "}", raw);
            switch (k & 7)
            {
                case 0: value = cv; return X.Int(X.Num(0), c, X.Num(1), "t");
                case 1: value = cv; return X.Int(X.Num(0), X.Num(1), c, "t");
                case 2: value = cv;
                    return X.Sum(X.Num(1), X.Inf, X.Frac(c, X.Pow(X.Num(2), X.Var(vn))), vn);
                case 3:
                    {
                        value = cv;
                        XNode s = X.Sum(X.Num(1), X.Inf, X.Frac(X.Num(1), X.Pow(X.Num(2), X.Var(vn))), vn);
                        return X.Mul(c, s);
                    }
                case 4:
                    {
                        value = cv * Math.E;
                        XNode lm = X.Lim(X.Inf, X.Pow(X.Add(X.Num(1), X.Frac(X.Num(1), X.Var(vn))), X.Var(vn)), vn);
                        return X.Mul(c, lm);
                    }
                case 5:
                    if (depth > 1) { value = cv; return X.Int(X.Num(0), c, X.Num(1), "t"); }
                    value = Math.Exp(cv);
                    return X.Lim(X.Inf, X.Pow(X.Add(X.Num(1), X.Frac(c, X.Var(vn))), X.Var(vn)), vn);
                case 6:
                    {
                        value = cv * Math.PI / 2.0;
                        XNode nn = X.Var(vn);
                        XNode num = X.Pow(X.Mul(X.Num(2), nn), X.Num(2));
                        XNode den = X.Mul(X.Sub(X.Mul(X.Num(2), nn), X.Num(1)), X.Add(X.Mul(X.Num(2), nn), X.Num(1)));
                        return X.Mul(c, X.Prod(X.Num(1), X.Inf, X.Frac(num, den), vn));
                    }
                default:
                    {
                        value = cv / 2.0;
                        XNode nn2 = X.Var(vn);
                        XNode term = X.Sub(X.Num(1), X.Frac(X.Num(1), X.Pow(X.Add(nn2, X.Num(1)), X.Num(2))));
                        return X.Mul(c, X.Prod(X.Num(1), X.Inf, term, vn));
                    }
            }
        }

        /// <summary>经典模块里的"连乘 = 1"，4 种形态按种子选（原来只有 Wallis 一种）。</summary>
        static string ProdPick(string seed, bool latex)
        {
            int v = SD(seed, 26) % 4;
            if (latex)
            {
                if (v == 0) return "\\frac{2}{\\pi}\\prod_{n=1}^{\\infty}\\frac{(2n)^{2}}{(2n-1)(2n+1)}";
                if (v == 1) return "\\frac{1}{2}\\prod_{n=1}^{\\infty}\\frac{\\left(n+1\\right)^{2}}{n\\left(n+2\\right)}";
                if (v == 2) return "2\\prod_{n=1}^{\\infty}\\left(1-\\frac{1}{\\left(n+1\\right)^{2}}\\right)";
                return "\\frac{4}{3}\\prod_{n=1}^{\\infty}\\left(1-\\frac{1}{\\left(n+3\\right)^{2}}\\right)";
            }
            if (v == 0) return "(2/π)·∏_{n=1}^{∞} (2n)²/((2n−1)(2n+1))";
            if (v == 1) return "(1/2)·∏_{n=1}^{∞} (n+1)²/(n(n+2))";
            if (v == 2) return "2·∏_{n=1}^{∞} (1−1/(n+1)²)";
            return "(4/3)·∏_{n=1}^{∞} (1−1/(n+3)²)";
        }

        static string BuildLimitFactor(int family, int param, string n, bool latex)
        {
            int a = param % 99 + 1;
            string asv = a.ToString(CultureInfo.InvariantCulture);
            // slot.Family 只有 0..9，这里用 (family,param) 混成 0..13，
            // 让新增的三角/反三角极限因子也有机会出现。
            family = ((family * 5 + param) % 14 + 14) % 14;
            // 三角/反三角的"极限 = 1"因子（评审要求多加三角运算）
            if (family == 10) return latex ? "\\frac{\\tan\\frac{" + asv + "}{" + n + "}}{\\frac{" + asv + "}{" + n + "}}" : "tan(" + asv + "/" + n + ")/(" + asv + "/" + n + ")";
            if (family == 11) return latex ? "\\left(\\left(\\cos\\frac{" + asv + "}{" + n + "}\\right)^{2}+\\left(\\sin\\frac{" + asv + "}{" + n + "}\\right)^{2}\\right)" : "(cos²(" + asv + "/" + n + ")+sin²(" + asv + "/" + n + "))";
            if (family == 12) return latex ? "\\frac{\\sin\\frac{" + asv + "}{" + n + "}}{\\frac{" + asv + "}{" + n + "}}" : "sin(" + asv + "/" + n + ")/(" + asv + "/" + n + ")";
            if (family == 13) return latex ? "\\frac{\\arcsin\\frac{" + asv + "}{" + n + "}}{\\frac{" + asv + "}{" + n + "}}" : "arcsin(" + asv + "/" + n + ")/(" + asv + "/" + n + ")";
            if (family == 0) return latex ? "\\frac{" + n + "+" + asv + "}{" + n + "}" : "(" + n + "+" + asv + ")/" + n;
            if (family == 1) return latex ? "1+\\frac{" + asv + "}{" + n + "}" : "1+" + asv + "/" + n;
            if (family == 2) return latex ? "\\frac{" + n + "}{" + n + "+" + asv + "}" : n + "/(" + n + "+" + asv + ")";
            if (family == 3) return latex ? "\\left(1+\\frac{" + asv + "}{" + n + "}\\right)^{2}" : "(1+" + asv + "/" + n + ")²";
            if (family == 4) return latex ? "\\cos\\frac{" + asv + "}{" + n + "}" : "cos(" + asv + "/" + n + ")";
            if (family == 5) return latex ? "e^{\\frac{" + asv + "}{" + n + "}}" : "e^{" + asv + "/" + n + "}";
            if (family == 6) return latex ? "\\frac{" + n + "^{2}+" + asv + "}{" + n + "^{2}+" + (a + 1) + "}" : "(" + n + "²+" + asv + ")/(" + n + "²+" + (a + 1) + ")";
            if (family == 7) return latex ? "\\frac{" + n + "+" + asv + "}{" + n + "+" + (a + 1) + "}" : "(" + n + "+" + asv + ")/(" + n + "+" + (a + 1) + ")";
            if (family == 8) return latex ? "\\frac{" + n + "^{2}+1}{" + n + "^{2}+2}" : "(" + n + "²+1)/(" + n + "²+2)";
            return latex ? "1+\\frac{" + asv + "}{" + n + "^{2}}" : "1+" + asv + "/" + n + "²";
        }

        /// <summary>级数层：任意 family 都给一个和 = 1 的项。</summary>
        static string BuildSeriesFactor(int family, int param, string k, bool latex)
        {
            int a = param % 9 + 1;
            int q = 2 + param % 9;
            double alpha = (param % 9 + 1) / 10.0;
            string asv = a.ToString(CultureInfo.InvariantCulture);
            string qs = q.ToString(CultureInfo.InvariantCulture);
            string al = alpha.ToString("0.0", CultureInfo.InvariantCulture);
            family = ((family % 10) + 10) % 10;
            if (family == 0) return latex ? "\\frac{1}{2^{" + k + "}}" : "1/2^{" + k + "}";
            if (family == 1) return latex ? "\\frac{1}{" + k + "(" + k + "+1)}" : "1/(" + k + "(" + k + "+1))";
            if (family == 2) return latex ? "\\frac{" + (q - 1) + "}{" + qs + "^{" + k + "}}" : (q - 1) + "/" + qs + "^{" + k + "}";
            if (family == 3) return latex ? al + "\\cdot\\left(" + (1.0 - alpha).ToString("0.0", CultureInfo.InvariantCulture) + "\\right)^{" + k + "-1}"
                                          : al + "·(" + (1.0 - alpha).ToString("0.0", CultureInfo.InvariantCulture) + ")^{" + k + "−1}";
            if (family == 4) return latex ? "\\frac{" + (a + 1) + "}{(" + k + "+" + asv + ")(" + k + "+" + (a + 1) + ")}"
                                          : (a + 1) + "/((" + k + "+" + asv + ")(" + k + "+" + (a + 1) + "))";
            if (family == 5) return latex ? "\\frac{1}{2" + k + "-1}-\\frac{1}{2" + k + "+1}"
                                          : "1/(2" + k + "−1) − 1/(2" + k + "+1)";
            if (family == 6) return latex ? "\\frac{" + (a + 1) + "}{(" + k + "+" + asv + ")(" + k + "+" + (a + 1) + ")}"
                                          : (a + 1) + "/((" + k + "+" + asv + ")(" + k + "+" + (a + 1) + "))";
            if (family == 7) return latex ? "\\frac{" + (q - 1) + "}{" + qs + "^{" + k + "}}" : (q - 1) + "/" + qs + "^{" + k + "}";
            if (family == 8) return latex ? "\\frac{1}{" + k + "(" + k + "+1)}" : "1/(" + k + "(" + k + "+1))";
            return latex ? "\\frac{1}{2^{" + k + "}}" : "1/2^{" + k + "}";
        }

        /// <summary>额外的 0 维装饰因子，值恒等于 1。</summary>
        static string BuildDecoration(Slot slot, bool latex)
        {
            if (slot.Type == 9) return "";
            int type = (slot.Type + slot.Index) % 9;
            int a = (slot.Param + slot.Index * 7) % 99 + 1;
            string asv = a.ToString(CultureInfo.InvariantCulture);
            if (type == 0) return latex ? "\\sin^{2}" + asv + "+\\cos^{2}" + asv : "sin²(" + asv + ")+cos²(" + asv + ")";
            if (type == 1) return latex ? "\\cos^{2}" + asv + "+\\sin^{2}" + asv : "cos²(" + asv + ")+sin²(" + asv + ")";
            if (type == 2) return latex ? "e^{" + asv + "}e^{-" + asv + "}" : "e^{" + asv + "}·e^{−" + asv + "}";
            if (type == 3) return latex ? "\\lim_{n\\to\\infty}\\left(1+\\frac{" + asv + "}{n}\\right)" : "lim_{n→∞}(1+" + asv + "/n)";
            if (type == 4) return latex ? "\\sum_{k=1}^{\\infty}\\frac{1}{2^{k}}" : "Σ_{k=1}^{∞} 1/2^{k}";
            if (type == 5) return latex ? "\\lim_{n\\to\\infty}\\frac{n}{n+" + asv + "}" : "lim_{n→∞} n/(n+" + asv + ")";
            if (type == 6) return latex ? "\\sum_{k=1}^{\\infty}\\frac{1}{k(k+1)}" : "Σ_{k=1}^{∞} 1/(k(k+1))";
            if (type == 7) return latex ? "\\lim_{N\\to\\infty}\\frac{1}{N}\\sum_{k=1}^{N}1" : "lim_{N→∞} (1/N)Σ_{k=1}^{N} 1";
            if (type == 8)
            {
                string pr = Primes[(slot.Family + slot.Param) % Primes.Length];
                return latex ? "\\lim_{N\\to\\infty}\\frac{2}{N}\\sum_{k=1}^{N}\\left\\{k\\sqrt{" + pr + "}\\right\\}"
                             : "lim_{N→∞} (2/N)Σ_{k=1}^{N} {k√" + pr + "}";
            }
            return "";
        }

        // ===================== 10 个归一化家族 =====================

        sealed class LayerSpec
        {
            public string FamilyName;
            public string Body;
            public string Latex;
            public string Low;
            public string High;
            public string LatexLow;
            public string LatexHigh;
            public int Complexity;
            public Func<double> Numeric;
            public string NumericKind;
        }

        static LayerSpec BuildLayer(int family, int param, string v)
        {
            family = ((family % 10) + 10) % 10;
            param = SafeParam(family, ((param % 1000) + 1000) % 1000);
            string lv = v.Replace("_", "_{") + "}";

            if (family == 0)
            {
                int a = param + 1;
                string body = (a == 1) ? "1" : (a.ToString(CultureInfo.InvariantCulture) + " " + v + "^{" + param.ToString(CultureInfo.InvariantCulture) + "}");
                string latex = (a == 1) ? "1" : (a.ToString(CultureInfo.InvariantCulture) + lv + "^{" + param.ToString(CultureInfo.InvariantCulture) + "}");
                return Layer("power", body, latex, "0", "1", 2,
                    delegate { return Simpson(delegate(double x) { return a * Math.Pow(x, param); }, 0.0, 1.0, 60000); }, "simpson");
            }
            if (family == 1)
            {
                double a = (param + 1) / 100.0;
                string s = Dec(a);
                return Layer("exponential", s + " e^{-" + s + " " + v + "}",
                    s + "e^{-" + s + lv + "}", "0", "+∞", 3,
                    delegate { return Simpson(delegate(double t) { return Math.Exp(-t); }, 0.0, 100.0, 60000); }, "simpson");
            }
            if (family == 2)
            {
                double a = (param + 1) / 100.0;
                string s = Dec(a);
                string body = "2·" + s + "/√π e^{-(" + s + ")² " + v + "²}";
                string latex = "\\frac{2\\cdot " + s + "}{\\sqrt{\\pi}}e^{-\\left(" + s + "\\right)^{2}" + lv + "^{2}}";
                return Layer("gaussian", body, latex, "0", "+∞", 4,
                    delegate { return Simpson(delegate(double t) { return 2.0 / Math.Sqrt(Math.PI) * Math.Exp(-t * t); }, 0.0, 10.0, 60000); }, "simpson");
            }
            if (family == 3)
            {
                double a = (param + 1) / 100.0;
                string s = Dec(a);
                string body = "(2·" + s + "/π)/(1+(" + s + ")² " + v + "²)";
                string latex = "\\frac{2\\cdot " + s + "}{\\pi\\left(1+\\left(" + s + "\\right)^{2}" + lv + "^{2}\\right)}";
                return Layer("cauchy", body, latex, "0", "+∞", 4,
                    delegate { return Simpson(delegate(double th) { return 2.0 / Math.PI; }, 0.0, Math.PI / 2.0 - 1e-12, 30000); }, "simpson");
            }
            if (family == 4)
            {
                int a = param + 1;
                long coef = (long)a * a;
                string body = param == 0
                    ? "(−ln " + v + ")"
                    : coef.ToString(CultureInfo.InvariantCulture) + " " + v + "^{" + param.ToString(CultureInfo.InvariantCulture) + "}(−ln " + v + ")";
                string latex = param == 0
                    ? "(-\\ln " + lv + ")"
                    : coef.ToString(CultureInfo.InvariantCulture) + lv + "^{" + param.ToString(CultureInfo.InvariantCulture) + "}(-\\ln " + lv + ")";
                return Layer("logpower", body, latex, "0", "1", 3,
                    delegate
                    {
                        // x = e^{-t}：∫_0^1 x^p(-ln x) dx = ∫_0^∞ t e^{-(p+1)t} dt = 1/(p+1)²
                        double upper = Math.Max(20.0, 100.0 / a);
                        return Simpson(delegate(double tt) { return coef * tt * Math.Exp(-a * tt); },
                            0.0, upper, 60000);
                    }, "simpson");
            }
            if (family == 5)
            {
                double a = (param + 1) / 100.0;
                string s = Dec(a);
                string body = "(e^{-" + s + " " + v + "} − e^{-" + s + "·e " + v + "})/" + v;
                string latex = "\\frac{e^{-" + s + lv + "}-e^{-" + s + "\\cdot e" + lv + "}}{" + lv + "}";
                return Layer("frullani", body, latex, "0", "+∞", 5, null, "closed");
            }
            if (family == 6)
            {
                double a = (param + 1) / 100.0;
                string s = Dec(a);
                string coef = Dec(2.0 / (Math.PI * a));
                string body = coef + " sin²(" + s + " " + v + ")/" + v + "²";
                string latex = "\\frac{2}{\\pi\\cdot " + s + "}\\cdot\\frac{\\sin^{2}(" + s + lv + ")}{" + lv + "^{2}}";
                return Layer("sinc2", body, latex, "0", "+∞", 5, null, "closed");
            }
            if (family == 7)
            {
                int a = param + 1;
                int coef = a * (a + 1);
                string body = param == 0
                    ? "2 (1−" + v + ")"
                    : coef.ToString(CultureInfo.InvariantCulture) + " " + v + "^{" + param.ToString(CultureInfo.InvariantCulture) + "}(1−" + v + ")";
                string latex = param == 0
                    ? "2\\left(1-" + lv + "\\right)"
                    : coef.ToString(CultureInfo.InvariantCulture) + lv + "^{" + param.ToString(CultureInfo.InvariantCulture) + "}(1-" + lv + ")";
                return Layer("beta", body, latex, "0", "1", 3,
                    delegate
                    {
                        return Simpson(delegate(double x)
                        {
                            return (param == 0 ? 2.0 : (double)coef) * Math.Pow(x, param) * (1.0 - x);
                        }, 0.0, 1.0, 60000);
                    }, "simpson");
            }
            if (family == 8)
            {
                int n = param;
                int power = 2 * n + 1;
                int coef = 2 * n + 2;
                string body = coef.ToString(CultureInfo.InvariantCulture) + " sin^{" + power.ToString(CultureInfo.InvariantCulture) + "}" + v + " cos " + v;
                string latex = coef.ToString(CultureInfo.InvariantCulture) + "\\sin^{" + power.ToString(CultureInfo.InvariantCulture) + "}" + lv + "\\cos " + lv;
                return Layer("trig", body, latex, "0", "π/2", 3,
                    delegate
                    {
                        return Simpson(delegate(double x)
                        {
                            return coef * Math.Pow(Math.Sin(x), power) * Math.Cos(x);
                        }, 0.0, Math.PI / 2.0, 60000);
                    }, "simpson");
            }

            double aa = (param + 1) / 100.0;
            string asv = Dec(aa);
            string arcBody = "2/(π√((" + asv + ")² − " + v + "²))";
            string arcLatex = "\\frac{2}{\\pi\\sqrt{\\left(" + asv + "\\right)^{2}-" + lv + "^{2}}}";
            return Layer("arcsin", arcBody, arcLatex, "0", asv, 4,
                delegate { return Simpson(delegate(double th) { return 2.0 / Math.PI; }, 0.0, Math.PI / 2.0 - 1e-12, 30000); }, "simpson");
        }

        static LayerSpec Layer(string name, string body, string latex, string low, string high,
            int complexity, Func<double> numeric, string numericKind)
        {
            LayerSpec l = new LayerSpec();
            l.Body = body;
            l.Latex = latex;
            l.Low = low;
            l.High = high;
            l.LatexLow = low;
            l.LatexHigh = high == "+∞" ? "+\\infty" : (high == "π/2" ? "\\pi/2" : high);
            l.Complexity = complexity;
            l.Numeric = numeric;
            l.NumericKind = numericKind;
            l.FamilyName = name;
            return l;
        }

        // ===================== 自检 / 校验 =====================

        /// <summary>供家族自检：构造一条单层积分，值 = 1。</summary>
        public static RandomExprResult BuildLayerTest(int family, int param)
        {
            LayerSpec l = BuildLayer(family, param, "x_1");
            string body = "∫_" + l.Low + "^{" + l.High + "} (" + l.Body + ") dx_1 = 1";
            string latex = "\\int_{" + l.LatexLow + "}^{" + l.LatexHigh + "} \\left(" + l.Latex + "\\right)\\,dx_{1} = 1";
            RandomExprResult r = new RandomExprResult();
            r.Expression = body;
            r.Latex = latex;
            r.Identity = true;
            r.Level = 1;
            r.Seed = "";
            r.Family = l.FamilyName;
            r.Kind = l.FamilyName;
            r.Blocks = 1;
            r.Layers = 1;
            r.Complexity = l.Complexity;
            r.Value = Rats.One;
            r.ValueOk = true;
            r.NumericValue = l.Numeric;
            r.NumericKind = l.NumericKind;
            Rat one = Rats.One;
            r.ClosedForm = delegate { return one; };
            return r;
        }

        public static bool Verify(RandomExprResult r, Rat target, out Rat value, out string error)
        {
            value = Rats.Zero;
            error = null;
            if (r == null) { error = "结果为空"; return false; }
            if (r.ClosedForm == null) { error = "缺少闭式值"; return false; }
            try { value = r.ClosedForm(); }
            catch (Exception ex) { error = ex.Message; return false; }
            if (!Rats.Eq(value, target))
            {
                error = "值 " + FormatRat(value) + " ≠ 目标 " + FormatRat(target);
                return false;
            }
            return true;
        }

        public static bool NumericCheck(RandomExprResult r, out double value, out string error)
        {
            value = 0.0;
            error = null;
            if (r == null) { error = "结果为空"; return false; }
            if (r.NumericKind == "closed" || r.NumericValue == null)
            {
                value = D(r.Value);
                return true;
            }
            try { value = r.NumericValue(); }
            catch (Exception ex) { error = ex.Message; return false; }
            double target = D(r.Value);
            double tol = 1e-8 * Math.Max(1.0, Math.Abs(target));
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                error = "数值为 " + value.ToString("R", CultureInfo.InvariantCulture);
                return false;
            }
            if (Math.Abs(value - target) > tol)
            {
                error = "数值 " + value.ToString("R", CultureInfo.InvariantCulture)
                    + " 与目标 " + target.ToString("R", CultureInfo.InvariantCulture) + " 相差过大";
                return false;
            }
            return true;
        }

        static double Simpson(Func<double, double> f, double a, double b, int n)
        {
            if (n < 2) n = 2;
            if ((n & 1) != 0) n++;
            double h = (b - a) / n;
            double sum = f(a) + f(b);
            for (int i = 1; i < n; i++)
            {
                double x = a + i * h;
                sum += f(x) * ((i & 1) == 1 ? 4.0 : 2.0);
            }
            return sum * h / 3.0;
        }

        // ===================== 隐蔽系数 =====================

        /// <summary>跳过会让 p+1 / (p+1)(p+2) / 2p+2 成为 325 裸倍数的参数。</summary>
        static int SafeParam(int family, int p)
        {
            for (int i = 0; i < 1000; i++)
            {
                int q = (p + i) % 1000;
                if (!Exposed(family, q)) return q;
            }
            return p;
        }

        static bool Exposed(int family, int p)
        {
            if (family == 5) return false;
            if (family == 0)
            {
                return ((p + 1) % 325 == 0) || (p != 0 && (p % 325) == 0);
            }
            if (family == 4)
            {
                long a = p + 1;
                return (p != 0 && (p % 325) == 0) || (a % 325 == 0) || ((a * a) % 325 == 0);
            }
            if (family == 7)
            {
                long a = p + 1;
                return (p != 0 && (p % 325) == 0)
                    || (a % 325 == 0)
                    || ((a + 1) % 325 == 0)
                    || ((a * (a + 1)) % 325 == 0);
            }
            if (family == 8)
            {
                return ((2 * p + 2) % 325 == 0) || ((2 * p + 1) % 325 == 0);
            }
            return ((p + 1) % 325 == 0);
        }

        // ===================== 独立后置校验 / 失败丢弃重生成 =====================

        /// <summary>
        /// 用 FormulaAudit 从最终 LaTeX 重新求值，结果写回 r（不改表达式本身）。
        /// 这一步与生成器内部记账完全无关：只吃 LaTeX 字符串。
        /// </summary>
        public static void AuditResult(RandomExprResult r, Rat target)
        {
            if (r == null) return;
            FormulaAuditResult a = FormulaAudit.Audit(r.Latex, target);
            r.AuditOk = a != null && a.Ok;
            r.AuditTight = a != null && a.Ok && a.Mode == "tight";
            if (a != null)
            {
                r.AuditValue = a.Value;
                r.AuditError = a.ErrorBound;
                r.AuditRel = a.RelError;
                r.AuditMode = a.Mode;
                r.AuditDetail = a.Detail;
            }
            else r.AuditDetail = "审计返回空";
            r.Audited = true;
        }

        /// <summary>同等级的另一种子（重试用）：保留长度，只换数字。</summary>
        static string RetrySeed(int level, string seed, Random rnd)
        {
            if (level == 5) return MakeL5Seed(rnd);
            return MakeSeed(level, rnd);
        }

        /// <summary>
        /// 带独立校验的生成：算出 LaTeX → 后置审计 → 不过就换种子重新生成。
        /// 最多 4 次；仍不过就返回最后一次（AuditOk=false，UI 会显示警告）。
        /// </summary>
        public static RandomExprResult BuildLevelGated(int level, Rat target, Random rnd, string seed, bool allowHard)
        {
            if (rnd == null) rnd = new Random();
            // 注意：这里不能用 ClampLevel（MaxLevel=4）。L5 是独立路径，
            // 夹成 4 会让“L5”悄悄生成 L4 式子（随机窗口曾因此显示 L4 形态）。
            if (level < 1) level = 1;
            if (level > 5) level = 5;
            string use = level == 5 ? NormalizeL5Seed(seed) : NormalizeSeed(seed, level);
            RandomExprResult last = null;
            for (int attempt = 0; attempt < 4; attempt++)
            {
                RandomExprResult r = level == 5
                    ? BuildL5(use, target)
                    : BuildLevel(level, target, rnd, use, allowHard);
                if (r == null) return null;
                r.RequestedSeed = seed;
                r.AuditTries = attempt + 1;
                if (!r.Audited) AuditResult(r, target);
                if (r.AuditOk) return r;
                last = r;
                use = RetrySeed(level, use, rnd);
            }
            if (last != null) last.Note = (last.Note == null ? "" : last.Note + "；") + "独立校验未通过（已重试 4 次）";
            return last;
        }

        // ===================== 格式 =====================

        static bool Is325(Rat t)
        {
            return t.D.IsOne && t.N == new BigInteger(325);
        }

        /// <summary>325 的“不显眼”恒等式；全零种子也给一个带括号的原子块。</summary>
        static void HiddenCore325(string seed, out string body, out string latex)
        {
            int h = 0;
            if (seed != null)
            {
                for (int i = 0; i < seed.Length; i++)
                {
                    char c = seed[i];
                    if (c >= '0' && c <= '9') h = (h * 7 + (c - '0')) % 1000;
                }
            }
            // 参数化生成：每次从几族构造里挑，参数也跟着种子走，
            // 不再总是那几个固定数字（评审：系数类型太频繁、要多样）。
            int fam = h % 6;
            int pick = (h / 6) % 977;
            switch (fam)
            {
                case 0:
                    {
                        // 平方差：325 的因数对 (u,v) → (a−b)(a+b)，a=(u+v)/2, b=(v−u)/2
                        int[] pairs = new int[] { 1, 325, 5, 65, 13, 25 };
                        int pi = (pick % 3) * 2;
                        int u = pairs[pi], v = pairs[pi + 1];
                        int a2 = (u + v) / 2, b2 = (v - u) / 2;
                        if (a2 * a2 - b2 * b2 == 325)
                        {
                            body = "(" + a2 + "−" + b2 + ")(" + a2 + "+" + b2 + ")";
                            latex = "\\left(" + a2 + "-" + b2 + "\\right)\\left(" + a2 + "+" + b2 + "\\right)";
                            break;
                        }
                        goto case 1;
                    }
                case 1:
                    {
                        // 立方和商：(a³+b³)/(a+b) = a²−ab+b²
                        int ba = 20, bb = 15;
                        for (int a3 = 6; a3 <= 30 && ba == 20; a3++)
                            for (int b3 = 2; b3 < a3; b3++)
                                if (a3 * a3 - a3 * b3 + b3 * b3 == 325) { ba = a3; bb = b3; break; }
                        body = "((" + ba + "³+" + bb + "³)/(" + ba + "+" + bb + "))";
                        latex = "\\frac{" + ba + "^{3}+" + bb + "^{3}}{" + ba + "+" + bb + "}";
                        break;
                    }
                case 2:
                    {
                        // 对数：e^{ln u + ln v}
                        // 只用 (5,65)/(13,25)：(1,325) 会写出裸 325，违反约束
                        int[] pairs = new int[] { 5, 65, 13, 25 };
                        int pi = (pick % 2) * 2;
                        int u = pairs[pi], v = pairs[pi + 1];
                        body = "e^(ln" + u + "+ln" + v + ")";
                        latex = "e^{\\ln" + u + "+\\ln" + v + "}";
                        break;
                    }
                case 3:
                    {
                        // 复数模长：(a²+b²)·k
                        int[] aa = new int[] { 3, 4, 1, 8, 4, 7, 6, 17, 10, 15, 1, 18 };
                        int[] bbv = new int[] { 4, 3, 8, 1, 7, 4, 17, 6, 15, 10, 18, 1 };
                        int[] kk = new int[] { 13, 13, 5, 5, 5, 5, 1, 1, 1, 1, 1, 1 };
                        int i2 = pick % 12;
                        if (aa[i2] * aa[i2] + bbv[i2] * bbv[i2] != 325 / kk[i2]) { i2 = 0; }
                        body = "|" + aa[i2] + "+" + bbv[i2] + "i|²·" + kk[i2];
                        latex = "\\left|" + aa[i2] + "+" + bbv[i2] + "i\\right|^{2}\\cdot " + kk[i2];
                        break;
                    }
                case 4:
                    {
                        // 平方和：a²+b²（325 = 1²+18² = 6²+17² = 10²+15² = 15²+10² = 17²+6² = 18²+1²）
                        int[] aa = new int[] { 1, 18, 6, 17, 10, 15, 15, 10, 17, 6, 18, 1 };
                        int i2 = (pick % 6) * 2;
                        int p2 = aa[i2], q2 = aa[i2 + 1];
                        body = "(" + p2 + "²+" + q2 + "²)";
                        latex = "\\left(" + p2 + "^{2}+" + q2 + "^{2}\\right)";
                        break;
                    }
                default:
                    {
                        // 两根之积（Vieta）：(a−√d)(a+√d) = a²−d
                        int[] aa = new int[] { 19, 35, 163 };
                        int[] cc = new int[] { 6, 30, 162 };
                        int i2 = pick % 3;
                        body = "(" + aa[i2] + "−√" + (cc[i2] * cc[i2]) + ")(" + aa[i2] + "+√" + (cc[i2] * cc[i2]) + ")";
                        latex = "\\left(" + aa[i2] + "-\\sqrt{" + (cc[i2] * cc[i2]) + "}\\right)\\left(" + aa[i2] + "+\\sqrt{" + (cc[i2] * cc[i2]) + "}\\right)";
                        break;
                    }
            }
            body = "(" + body + ")";
            latex = "\\left(" + latex + "\\right)";
        }
        static string Coef(Rat r)
        {
            string s = PrettyRat(r);
            if (r.N.Sign < 0 || !r.D.IsOne) return "(" + s + ")";
            return s;
        }

        static string LatexNum(Rat r)
        {
            string sign = r.N.Sign < 0 ? "-" : "";
            BigInteger n = BigInteger.Abs(r.N);
            if (r.D.IsOne) return sign + n.ToString(CultureInfo.InvariantCulture);
            return sign + "\\frac{" + n.ToString(CultureInfo.InvariantCulture) + "}{" + r.D.ToString(CultureInfo.InvariantCulture) + "}";
        }

        static string LatexCoef(Rat r)
        {
            string s = LatexNum(r);
            if (r.N.Sign < 0 || !r.D.IsOne) return "\\left(" + s + "\\right)";
            return s;
        }

        public static string FormatRat(Rat r)
        {
            if (r.D.IsOne) return r.N.ToString(CultureInfo.InvariantCulture);
            return r.N.ToString(CultureInfo.InvariantCulture) + "/" + r.D.ToString(CultureInfo.InvariantCulture);
        }

        public static string PrettyRat(Rat r)
        {
            return FormatRat(r).Replace('-', '−');
        }

        static double D(Rat r)
        {
            return Solver.ToDouble(r.N) / Solver.ToDouble(r.D);
        }

        static string Dec(double v)
        {
            return v.ToString("0.00", CultureInfo.InvariantCulture);
        }
        /// <summary>
        /// 外层模块：把两棵独立的嵌套树塞进积分上下限，用 5 种经典恒等式把值拉回 1。
        /// A、B 由不同哈希生成，数值上必定不同（都不小于 2），所以分母非零。
        /// </summary>
        static XNode NestedModule(XNode A, XNode B, int which, bool needAbs)
        {
            XNode x = X.Var("x_1");
            XNode diff = X.Sub(B, A);
            XNode adiff = X.Call("abs", diff);
            if (which == 0) return X.Call("abs", X.Int(A, B, X.Frac(X.Num(1), diff), "x_1"));
            if (which == 1) return X.Mul(X.Frac(X.Num(1), diff), X.Int(A, B, X.Num(1), "x_1"));
            return X.Mul(X.Frac(X.Num(2), X.Sub(X.Pow(B, X.Num(2)), X.Pow(A, X.Num(2)))), X.Int(A, B, x, "x_1"));
        }
    }
}

