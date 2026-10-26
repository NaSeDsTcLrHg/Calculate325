/*
 * Calculate325 —— 求解器核心（C# 版，与 src/solver.js 逐行对应）
 * ---------------------------------------------------------------------------
 * 规则（默认 OrderMode = "fixed"）：
 *   1. 数字串固定为 "325" 的重复，顺序不可改变（325325325…）；
 *   2. 只能在数字之间插入运算符与括号；相邻数字之间不插运算符就拼成多位数；
 *   3. 允许 + − × ÷ 与括号、后缀阶乘 !、小数点（如 3.25 / 32.5）；
 *   4. 自动选择最少的 "325" 块数 k。
 *   5. OrderMode = "any" 时放宽为“数字位置可任意交换”，只要求 3/2/5 出现次数相同。
 *
 * 算法：区间 DP + 按“用数个数”分轮，命中即停（详见 README）。
 * 数值使用 System.Numerics.BigInteger 精确有理数，无浮点误差（浮点只用于剪枝）。
 */
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Text;

namespace Calculate325
{
    // ===================== 精确有理数 =====================
    public struct Rat
    {
        public readonly BigInteger N;
        public readonly BigInteger D;

        public Rat(BigInteger n, BigInteger d)
        {
            if (d.IsZero) throw new DivideByZeroException("division by zero");
            if (d.Sign < 0) { n = -n; d = -d; }
            BigInteger g = BigInteger.GreatestCommonDivisor(n, d);
            if (g.IsZero) g = BigInteger.One;
            N = n / g;
            D = d / g;
        }

        public bool IsInt { get { return D.IsOne; } }

        public override string ToString()
        {
            return D.IsOne ? N.ToString(CultureInfo.InvariantCulture) : N.ToString(CultureInfo.InvariantCulture) + "/" + D.ToString(CultureInfo.InvariantCulture);
        }
    }

    public static class Rats
    {
        public static readonly Rat Zero = new Rat(BigInteger.Zero, BigInteger.One);
        public static readonly Rat One = new Rat(BigInteger.One, BigInteger.One);

        public static Rat FromInt(BigInteger n) { return new Rat(n, BigInteger.One); }

        public static Rat Add(Rat a, Rat b) { return new Rat(a.N * b.D + b.N * a.D, a.D * b.D); }
        public static Rat Sub(Rat a, Rat b) { return new Rat(a.N * b.D - b.N * a.D, a.D * b.D); }
        public static Rat Mul(Rat a, Rat b) { return new Rat(a.N * b.N, a.D * b.D); }

        public static bool TryDiv(Rat a, Rat b, out Rat result)
        {
            if (b.N.IsZero) { result = Zero; return false; }
            result = new Rat(a.N * b.D, a.D * b.N);
            return true;
        }

        public static bool Eq(Rat a, Rat b) { return a.N == b.N && a.D == b.D; }
    }

    // ===================== 参数 =====================
    public class Limits
    {
        public int MaxBlocks = 2;
        public bool AllowFactorial = true;
        public int FactMax = 12;
        public bool AllowPower;   // 允许乘方 a^b
        public bool AllowDet;     // 允许 2x2 行列式
        public bool AllowDecimal = true;
        public double MaxAbsValue = 1e12;
        public double MaxDenominator = 1e9;
        public int[] ClassCaps = new int[] { 0, 200000, 30000, 30000, 12000, 6000, 3000 };
        public int ClassCapTail = 1500;
        public int PowerTry, PowerPut, PowerSkipCap;   // 高级运算诊断计数
        public bool AllowRoot;        // 平方根 √x（仅完全平方）
        public bool AllowDoubleFact;  // 双阶乘 x!!
        public bool AllowComb;        // 组合数 C(n,k)
        public bool AllowAbs;         // abs(x)
        public bool AllowCbrt;        // ∛x（仅完全立方）
        public bool AllowPercent;     // x%（除以 100）
        public bool AllowLcmLog;      // lcm(a,b) / log(b,x)

        public string RequiredOps = "";   // 勾选并要求覆盖的运算（中文名，逗号分隔）
        public bool AllowGcdMod;      // gcd(a,b) / mod(a,b)
        public int TimeBudgetMs = 5000;
        public int FallbackBudgetMs = 4000;
        public int SolutionListLimit = 8;
        public string SearchMode = "blocks";   // blocks | length
        public string OrderMode = "fixed";     // fixed | any
        public string Pattern = "325";         // 数字串（重复单位）：325 / 799 / 325799 / 114514 …

        public Limits Clone()
        {
            Limits c = new Limits();
            c.MaxBlocks = MaxBlocks; c.AllowFactorial = AllowFactorial; c.FactMax = FactMax;
            c.AllowDecimal = AllowDecimal; c.MaxAbsValue = MaxAbsValue; c.MaxDenominator = MaxDenominator;
            c.ClassCaps = (int[])ClassCaps.Clone(); c.ClassCapTail = ClassCapTail;
            c.TimeBudgetMs = TimeBudgetMs; c.FallbackBudgetMs = FallbackBudgetMs;
            c.SolutionListLimit = SolutionListLimit; c.SearchMode = SearchMode; c.OrderMode = OrderMode;
            c.Pattern = Pattern;
            c.AllowPower = AllowPower; c.AllowDet = AllowDet;
            c.AllowRoot = AllowRoot; c.AllowDoubleFact = AllowDoubleFact;
            c.AllowComb = AllowComb; c.AllowGcdMod = AllowGcdMod;
            c.RequiredOps = RequiredOps;
            c.AllowAbs = AllowAbs; c.AllowCbrt = AllowCbrt;
            c.AllowPercent = AllowPercent; c.AllowLcmLog = AllowLcmLog;
            return c;
        }

        public int ClassCapOf(int t)
        {
            if (t < ClassCaps.Length) return ClassCaps[t];
            return ClassCapTail;
        }
    }

    // ===================== 结果对象 =====================
    public class AltWriting
    {
        public string Expression;
        public string Pretty;
        public int Blocks;
        public int Length;
    }

    public class FallbackInfo
    {
        public string Expression;
        public string Pretty;
        public int Blocks;
        public string Kind;
    }

    public class BlockStat
    {
        public int K;
        public int Values;
        public int TokensBuilt;
        public long Ms;
        public bool Pruned;
        public bool Limited;
    }

    public class SolveResult
    {
        public bool Ok;
        public Rat Target;
        public string TargetText;

        /// <summary>本算式用到的运算（中文名，用 + 连接）。</summary>
        public string OpsUsed = "";

        /// <summary>勾选了但没用上的运算。</summary>
        public string OpsMissing = "";
        public string Expression;
        public string ExpressionPretty;
        public string Digits;
        public int Blocks;
        public int TokenCount;
        public int Tokens;
        public bool DigitsOk;
        public bool ValueOk;
        public List<AltWriting> Alternatives = new List<AltWriting>();
        public List<string> Notes = new List<string>();
        public long ElapsedMs;
        public bool Pruned;
        public bool Limited;
        public bool Aborted;
        public List<int> SearchedBlocks = new List<int>();
        public List<BlockStat> Stats = new List<BlockStat>();
        public FallbackInfo FallbackResult;
        public string Error;
        public int PowerTry, PowerPut, PowerSkipCap;
        public string SearchMode;
        public string OrderMode;
        public string Pattern;
        public int Entries;
    }

    public class VerifyResult
    {
        public string Digits;
        public int Blocks;
        public bool DigitsOk;
        public string OrderMode;
        public string Pattern;
        public Rat Value;
        public bool HasValue;
        public bool ValueOk;
        public string Error;
    }

    public class TargetParse
    {
        public bool Ok;
        public Rat Value;
        public string Text;
        public string Error;
    }

    // ===================== 求解器 =====================
    public class Solver
    {
        const int PREC_ATOM = 3, PREC_MUL = 2, PREC_ADD = 1;

        sealed class Entry
        {
            public Rat Val;
            public double Mag;
            public double DenMag;
            public string Text;
            public int Prec;
            public int Tokens;
        }

        sealed class State
        {
            public string Sub;
            public Dictionary<BigInteger, Dictionary<BigInteger, Entry>> Map = new Dictionary<BigInteger, Dictionary<BigInteger, Entry>>();
            public List<Entry>[] ByTokens;
            public int[] Counts;
            public int Built;
            public int MapCount;

            public bool TryGet(Rat r, out Entry e)
            {
                Dictionary<BigInteger, Entry> inner;
                if (Map.TryGetValue(r.D, out inner))
                {
                    return inner.TryGetValue(r.N, out e);
                }
                e = null;
                return false;
            }
        }

        sealed class Ctx
        {
            public Limits Limits;
            public Dictionary<string, State> Cache = new Dictionary<string, State>();
            public long Deadline;
            public bool Aborted;
            public bool Pruned;
            public bool Limited;
            public long Steps;
            public double MulLimit;
            public double MaxDenNum;
            public BigInteger FactMaxBig;
        }

        static readonly Dictionary<BigInteger, BigInteger> FactCache = new Dictionary<BigInteger, BigInteger>();
        static readonly object FactLock = new object();

        // ---------- 工具 ----------
        public static double ToDouble(BigInteger b)
        {
            if (b.IsZero) return 0.0;
            int bytes = b.ToByteArray().Length;
            if (bytes > 120) return b.Sign > 0 ? double.PositiveInfinity : double.NegativeInfinity;
            try { return (double)b; }
            catch (OverflowException) { return b.Sign > 0 ? double.PositiveInfinity : double.NegativeInfinity; }
        }

        static double MagOf(Rat r) { return ToDouble(r.N) / ToDouble(r.D); }

        static BigInteger BinomialBig(BigInteger n, BigInteger k)
        {
            if (k.Sign < 0 || n.Sign < 0 || k > n) return BigInteger.Zero;
            BigInteger kk = k > n - k ? n - k : k;
            BigInteger r = BigInteger.One;
            for (BigInteger i = 1; i <= kk; i++) r = r * (n - kk + i) / i;
            return r;
        }

        /// <summary>扫描表达式用到了哪些运算，返回中文名的有序列表。</summary>
        public static List<string> OpsOf(string expr)
        {
            List<string> ops = new List<string>();
            if (expr == null) return ops;
            if (expr.IndexOf('^') >= 0) ops.Add("乘方");
            if (expr.IndexOf('√') >= 0) ops.Add("根号");
            if (expr.IndexOf("C(", StringComparison.Ordinal) >= 0) ops.Add("组合数");
            if (expr.IndexOf("gcd(", StringComparison.Ordinal) >= 0) ops.Add("gcd");
            if (expr.IndexOf("mod(", StringComparison.Ordinal) >= 0) ops.Add("mod");
            if (expr.IndexOf("det(", StringComparison.Ordinal) >= 0) ops.Add("行列式");
            if (expr.IndexOf('!') >= 0) ops.Add("阶乘");
            return ops;
        }

        static string JoinOps(List<string> ops)
        {
            string s = "";
            for (int i = 0; i < ops.Count; i++) s += (i == 0 ? "" : "+") + ops[i];
            return s;
        }

        static BigInteger ICbrt(BigInteger n)
        {
            if (n.Sign <= 0) return BigInteger.Zero;
            BigInteger x = new BigInteger(Math.Pow(ToDouble(n), 1.0 / 3.0)) + 2;
            while (x * x * x > n) x--;
            while ((x + 1) * (x + 1) * (x + 1) <= n) x++;
            return x;
        }

        static BigInteger ISqrt(BigInteger n)
        {
            if (n.Sign < 0) return BigInteger.Zero;
            if (n.IsZero) return BigInteger.Zero;
            BigInteger x = n, y = (x + 1) / 2;
            while (y < x) { x = y; y = (x + n / x) / 2; }
            return x;
        }

        static BigInteger DoubleFactorialBig(BigInteger n)
        {
            BigInteger r = BigInteger.One;
            for (BigInteger i = n; i > 1; i -= 2) r *= i;
            return r;
        }

        static BigInteger FactorialBig(BigInteger v)
        {
            lock (FactLock)
            {
                BigInteger hit;
                if (FactCache.TryGetValue(v, out hit)) return hit;
                BigInteger r = BigInteger.One;
                for (BigInteger i = 2; i <= v; i++) r *= i;
                FactCache[v] = r;
                return r;
            }
        }

        static string ParenWrap(string t) { return "(" + t + ")"; }

        public static string Pretty(string text)
        {
            if (text == null) return "";
            StringBuilder sb = new StringBuilder(text.Length + 8);
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '*') sb.Append('×');
                else if (c == '/') sb.Append('÷');
                else if (c == '-') sb.Append('−');
                else sb.Append(c);
            }
            return sb.ToString();
        }

        public static string NormalizeExpr(string text)
        {
            if (text == null) return "";
            StringBuilder sb = new StringBuilder(text.Length);
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '×' || c == '✕' || c == '✖') sb.Append('*');
                else if (c == '÷') sb.Append('/');
                else if (c == '−' || c == '–' || c == '—') sb.Append('-');
                else if (c == '（') sb.Append('(');
                else if (c == '）') sb.Append(')');
                else if (c == '！') sb.Append('!');
                else if (char.IsWhiteSpace(c)) { }
                else sb.Append(c);
            }
            return sb.ToString();
        }

        public static string DigitsOf(string text)
        {
            string s = NormalizeExpr(text);
            StringBuilder sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++) if (s[i] >= '0' && s[i] <= '9') sb.Append(s[i]);
            return sb.ToString();
        }

        public static int CountTokens(string expression)
        {
            string s = NormalizeExpr(expression);
            int count = 0, i = 0;
            while (i < s.Length)
            {
                if (s[i] >= '0' && s[i] <= '9')
                {
                    count++;
                    while (i < s.Length && s[i] >= '0' && s[i] <= '9') i++;
                    if (i < s.Length && s[i] == '.')
                    {
                        int j = i + 1;
                        while (j < s.Length && s[j] >= '0' && s[j] <= '9') j++;
                        if (j > i + 1) i = j;
                    }
                }
                else i++;
            }
            return count;
        }

        // 数字串（重复单位）：1~8 位数字，不能以 0 开头
        public static string NormalizePattern(string p)
        {
            string s = (p == null || p.Trim().Length == 0) ? "325" : p.Trim();
            if (s.Length < 1 || s.Length > 32) return null;   // 与界面允许的位数一致（1–32）
            for (int i = 0; i < s.Length; i++) if (s[i] < '0' || s[i] > '9') return null;
            if (s[0] == '0') return null;
            return s;
        }

        public static string RepeatPattern(string pat, int k)
        {
            StringBuilder sb = new StringBuilder(pat.Length * k);
            for (int i = 0; i < k; i++) sb.Append(pat);
            return sb.ToString();
        }

        public static bool CheckDigits(string digits, string orderMode, string pattern)
        {
            string pat = NormalizePattern(pattern);
            if (pat == null) pat = "325";
            int L = pat.Length;
            if (digits == null || digits.Length == 0 || digits.Length % L != 0) return false;
            int k = digits.Length / L;
            if (orderMode == "any")
            {
                int[] need = new int[10];
                for (int i = 0; i < L; i++) need[pat[i] - '0'] += k;
                for (int j = 0; j < digits.Length; j++)
                {
                    int d = digits[j] - '0';
                    if (d < 0 || d > 9 || need[d] == 0) return false;
                    need[d]--;
                }
                return true;
            }
            for (int m = 0; m < digits.Length; m++)
            {
                if (digits[m] != pat[m % L]) return false;
            }
            return true;
        }

        // ---------- 目标解析 ----------
        public static TargetParse ParseTarget(string input)
        {
            TargetParse tp = new TargetParse();
            if (input == null) { tp.Error = "请输入目标数字 n"; return tp; }
            string s = input.Trim();
            if (s.Length == 0) { tp.Error = "请输入目标数字 n"; return tp; }
            StringBuilder filter = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '，' || c == ',') filter.Append('.');
                else if (c == '−' || c == '–' || c == '—') filter.Append('-');
                else if (char.IsWhiteSpace(c)) { }
                else filter.Append(c);
            }
            s = filter.ToString();
            if (!System.Text.RegularExpressions.Regex.IsMatch(s, @"^[+-]?\d+(\.\d+)?$", System.Text.RegularExpressions.RegexOptions.ECMAScript))
            {
                tp.Error = "只能输入整数或小数（可带负号），例如 325、1000、3.25、-7";
                return tp;
            }
            bool neg = s[0] == '-';
            if (s[0] == '+' || s[0] == '-') s = s.Substring(1);
            int dot = s.IndexOf('.');
            string intPart = dot >= 0 ? s.Substring(0, dot) : s;
            string fracPart = dot >= 0 ? s.Substring(dot + 1) : "";
            BigInteger den = BigInteger.Pow(10, fracPart.Length);
            BigInteger num = BigInteger.Parse(intPart + fracPart, CultureInfo.InvariantCulture);
            if (neg) num = -num;
            tp.Ok = true;
            tp.Value = new Rat(num, den);
            tp.Text = (neg ? "-" : "") + intPart + (fracPart.Length > 0 ? "." + fracPart : "");
            return tp;
        }

        // ---------- 独立校验：递归下降求值 ----------
        sealed class EvalParser
        {
            string s;
            int pos;
            int factLimit;

            public EvalParser(string text, int verifyFactMax)
            {
                s = NormalizeExpr(text);
                pos = 0;
                factLimit = verifyFactMax;
            }

            char Peek() { return pos < s.Length ? s[pos] : '\0'; }

            public Rat Parse()
            {
                Rat v = ParseExpr();
                if (pos != s.Length) throw new FormatException("表达式结尾有多余字符");
                return v;
            }

            Rat ParseExpr()
            {
                Rat v = ParseTerm();
                while (Peek() == '+' || Peek() == '-')
                {
                    char op = s[pos++];
                    Rat r = ParseTerm();
                    v = op == '+' ? Rats.Add(v, r) : Rats.Sub(v, r);
                }
                return v;
            }

            Rat ParseTerm()
            {
                Rat v = ParseFactor();
                while (Peek() == '*' || Peek() == '/')
                {
                    char op = s[pos++];
                    Rat r = ParseFactor();
                    if (op == '*') v = Rats.Mul(v, r);
                    else
                    {
                        Rat q;
                        if (!Rats.TryDiv(v, r, out q)) throw new DivideByZeroException("出现除以零");
                        v = q;
                    }
                }
                return v;
            }

            Rat ParseFactor()
            {
                // 乘方：右结合，形如 a^b（b 必须是 2..12 的整数）
                Rat v = ParsePrimary();
                if (Peek() == '^')
                {
                    pos++;
                    Rat e = ParseFactor();
                    if (!e.IsInt || e.N.Sign <= 0 || e.N > 12) throw new FormatException("乘方指数必须是 1..12 的整数");
                    int k = (int)e.N;
                    BigInteger num = BigInteger.Pow(v.N, k);
                    BigInteger den = BigInteger.Pow(v.D, k);
                    v = new Rat(num, den);
                }
                while (Peek() == '%')
                {
                    pos++;
                    v = new Rat(v.N, v.D * 100);
                }
                while (Peek() == '!')
                {
                    pos++;
                    if (!v.IsInt || v.N.Sign < 0) throw new FormatException("阶乘只能用于非负整数");
                    if (v.N > factLimit) throw new FormatException("阶乘参数过大，无法校验");
                    v = Rats.FromInt(FactorialBig(v.N));
                }
                return v;
            }

            Rat ParsePrimary()
            {
                char c = Peek();
                if (c == '∛')
                {
                    pos++;
                    Rat inner2 = ParsePrimary();
                    if (!inner2.IsInt || inner2.N.Sign < 0) throw new FormatException("立方根内必须是非负整数");
                    BigInteger cb = ICbrt(inner2.N);
                    if (cb * cb * cb != inner2.N) throw new FormatException("不是完全立方数");
                    return Rats.FromInt(cb);
                }
                if (c == '√')

                {
                    pos++;
                    Rat inner = ParsePrimary();
                    if (!inner.IsInt || inner.N.Sign < 0) throw new FormatException("根号内必须是非负整数");
                    BigInteger r = ISqrt(inner.N);
                    if (r * r != inner.N) throw new FormatException("根号内不是完全平方数");
                    return Rats.FromInt(r);
                }
                if (char.IsLetter(c))
                {
                    int st = pos;
                    while (pos < s.Length && char.IsLetter(s[pos])) pos++;
                    string fn = s.Substring(st, pos - st);
                    if (Peek() != '(') throw new FormatException("函数缺少左括号");
                    pos++;
                    System.Collections.Generic.List<Rat> args = new System.Collections.Generic.List<Rat>();
                    args.Add(ParseExpr());
                    while (Peek() == ',')
                    {
                        pos++;
                        args.Add(ParseExpr());
                        if (args.Count > 4) throw new FormatException("函数参数过多");
                    }
                    if (Peek() != ')') throw new FormatException("缺少右括号");
                    pos++;
                    if (fn == "abs")
                    {
                        if (args.Count != 1) throw new FormatException("abs 需要一个参数");
                        return args[0].N.Sign < 0 ? Rats.FromInt(-args[0].N) : args[0];
                    }
                    if (fn == "lcm")
                    {
                        if (args.Count != 2) throw new FormatException("lcm 需要两个参数");
                        if (!args[0].IsInt || !args[1].IsInt) throw new FormatException("lcm 参数必须是整数");
                        BigInteger A = BigInteger.Abs(args[0].N), Bv = BigInteger.Abs(args[1].N);
                        if (A.IsZero || Bv.IsZero) return Rats.FromInt(BigInteger.Zero);
                        BigInteger g = BigInteger.GreatestCommonDivisor(A, Bv);
                        return Rats.FromInt(A / g * Bv);
                    }
                    if (fn == "log")
                    {
                        if (args.Count != 2) throw new FormatException("log 需要两个参数");
                        if (!args[0].IsInt || !args[1].IsInt) throw new FormatException("log 参数必须是整数");
                        BigInteger bs = args[0].N, xv = args[1].N;
                        if (bs <= BigInteger.One || xv <= BigInteger.Zero) throw new FormatException("log 参数越界");
                        BigInteger t = BigInteger.One; int e2 = 0;
                        while (t < xv && e2 < 64) { t *= bs; e2++; }
                        if (t != xv) throw new FormatException("对数不是整数，无法精确表示");
                        return Rats.FromInt(new BigInteger(e2));
                    }
                    if (fn == "det")

                    {
                        if (args.Count != 4) throw new FormatException("det 需要 4 个参数");
                        for (int q2 = 0; q2 < 4; q2++) if (!args[q2].IsInt) throw new FormatException("det 参数必须是整数");
                        Rat dv = Rats.Sub(Rats.Mul(args[0], args[3]), Rats.Mul(args[1], args[2]));
                        return dv;
                    }
                    if (args.Count != 2) throw new FormatException("该函数需要两个参数");
                    Rat a = args[0];
                    Rat b2 = args[1];
                    if (!a.IsInt || !b2.IsInt) throw new FormatException("函数参数必须是整数");
                    if (fn == "C")
                    {
                        BigInteger n2 = a.N, k2 = b2.N;
                        if (n2.Sign < 0 || k2.Sign < 0 || k2 > n2 || n2 > 200) throw new FormatException("组合数参数越界");
                        return Rats.FromInt(BinomialBig(n2, k2));
                    }
                    if (fn == "gcd")
                    {
                        BigInteger g = BigInteger.GreatestCommonDivisor(BigInteger.Abs(a.N), BigInteger.Abs(b2.N));
                        return Rats.FromInt(g);
                    }
                    if (fn == "mod")
                    {
                        if (b2.N.Sign <= 0) throw new FormatException("mod 的除数必须为正");
                        BigInteger r2 = a.N % b2.N;
                        if (r2.Sign < 0) r2 += b2.N;
                        return Rats.FromInt(r2);
                    }
                    throw new FormatException("未知函数：" + fn);
                }
                if (c == '(')
                {
                    pos++;
                    Rat v = ParseExpr();
                    if (Peek() != ')') throw new FormatException("缺少右括号");
                    pos++;
                    return v;
                }
                if (c >= '0' && c <= '9')
                {
                    int st = pos;
                    while (pos < s.Length && s[pos] >= '0' && s[pos] <= '9') pos++;
                    if (pos < s.Length && s[pos] == '.')
                    {
                        pos++;
                        while (pos < s.Length && s[pos] >= '0' && s[pos] <= '9') pos++;
                    }
                    return ParseDecimalToken(s.Substring(st, pos - st));
                }
                throw new FormatException("无法识别的字符：" + (pos >= s.Length ? "(式尾)" : c.ToString()));
            }

            static Rat ParseDecimalToken(string tok)
            {
                int dot = tok.IndexOf('.');
                if (dot >= 0)
                {
                    string a = tok.Substring(0, dot);
                    string b = tok.Substring(dot + 1);
                    BigInteger den = BigInteger.Pow(10, b.Length);
                    BigInteger num = BigInteger.Parse(a, CultureInfo.InvariantCulture) * den + (b.Length > 0 ? BigInteger.Parse(b, CultureInfo.InvariantCulture) : BigInteger.Zero);
                    return new Rat(num, den);
                }
                return Rats.FromInt(BigInteger.Parse(tok, CultureInfo.InvariantCulture));
            }
        }

        /// <summary>
        /// 只按表达式本身求值（不要求数字串顺序）。随机模式用它做普通式的独立校验。
        /// 复用求解器自己的递归下降解析器，不改变任何求解逻辑。
        /// </summary>
        public static bool TryEvalExact(string expression, out Rat value, out string error)
        {
            value = Rats.Zero;
            error = null;
            try
            {
                EvalParser p = new EvalParser(expression, 5000);
                value = p.Parse();
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        // 兼容旧签名：默认数字串 325
        public static VerifyResult Verify(string expression, Rat target, bool hasTarget, string orderMode)
        {
            return Verify(expression, target, hasTarget, orderMode, "325");
        }

        public static VerifyResult Verify(string expression, Rat target, bool hasTarget, string orderMode, string pattern)
        {
            VerifyResult vr = new VerifyResult();
            vr.OrderMode = orderMode;
            string pat = NormalizePattern(pattern);
            if (pat == null) pat = "325";
            vr.Pattern = pat;
            vr.Digits = DigitsOf(expression);
            vr.Blocks = vr.Digits.Length / pat.Length;
            vr.DigitsOk = CheckDigits(vr.Digits, orderMode, pat);
            try
            {
                EvalParser p = new EvalParser(expression, 5000);
                vr.Value = p.Parse();
                vr.HasValue = true;
            }
            catch (Exception ex)
            {
                vr.Error = ex.Message;
                return vr;
            }
            if (hasTarget) vr.ValueOk = Rats.Eq(vr.Value, target);
            return vr;
        }

        // ---------- 区间 DP ----------
        static State GetState(string sub, Ctx ctx)
        {
            State st;
            if (ctx.Cache.TryGetValue(sub, out st)) return st;
            st = new State();
            st.Sub = sub;
            st.ByTokens = new List<Entry>[sub.Length + 1];
            st.Counts = new int[sub.Length + 1];
            for (int i = 0; i <= sub.Length; i++) st.ByTokens[i] = new List<Entry>();
            ctx.Cache[sub] = st;
            return st;
        }

        static bool NeedText(State st, Rat val, int est)
        {
            Entry e;
            if (!st.TryGet(val, out e)) return true;
            return e.Text.Length > est;
        }

        static bool PutEntry(State st, int t, Rat val, string text, int prec, Ctx ctx)
        {
            Entry e;
            if (st.TryGet(val, out e))
            {
                if (text.Length < e.Text.Length) { e.Text = text; e.Prec = prec; }
                return false;
            }
            double mag = MagOf(val);
            double denMag = ToDouble(val.D);
            if (!(mag <= ctx.Limits.MaxAbsValue) || !(denMag <= ctx.MaxDenNum))
            {
                ctx.Limited = true;
                return false;
            }
            if (st.Counts[t] >= ctx.Limits.ClassCapOf(t))
            {
                ctx.Pruned = true;
                return false;
            }
            Entry entry = new Entry();
            entry.Val = val; entry.Mag = mag; entry.DenMag = denMag;
            entry.Text = text; entry.Prec = prec; entry.Tokens = t;
            Dictionary<BigInteger, Entry> inner;
            if (!st.Map.TryGetValue(val.D, out inner))
            {
                inner = new Dictionary<BigInteger, Entry>();
                st.Map[val.D] = inner;
            }
            inner[val.N] = entry;
            st.ByTokens[t].Add(entry);
            st.Counts[t]++;
            st.MapCount++;
            return true;
        }

        static void AddPair(State st, int t, Entry A, Entry B, Ctx ctx)
        {
            bool any = ctx.Limits.OrderMode == "any";
            double lim = ctx.MulLimit;
            string Badd = B.Prec <= PREC_ADD ? ParenWrap(B.Text) : B.Text;
            string Aadd = A.Prec <= PREC_ADD ? ParenWrap(A.Text) : A.Text;

            if (A.Mag + B.Mag <= lim)
            {
                Rat v1 = Rats.Add(A.Val, B.Val);
                if (NeedText(st, v1, A.Text.Length + 1 + Badd.Length)) PutEntry(st, t, v1, A.Text + "+" + Badd, PREC_ADD, ctx);
            }
            if (A.Mag + B.Mag <= lim)
            {
                Rat v2 = Rats.Sub(A.Val, B.Val);
                if (NeedText(st, v2, A.Text.Length + 1 + Badd.Length)) PutEntry(st, t, v2, A.Text + "-" + Badd, PREC_ADD, ctx);
            }
            if (A.Mag * B.Mag <= lim)
            {
                string Amul = A.Prec < PREC_MUL ? ParenWrap(A.Text) : A.Text;
                string Bmul = B.Prec < PREC_MUL ? ParenWrap(B.Text) : B.Text;
                Rat v3 = Rats.Mul(A.Val, B.Val);
                if (NeedText(st, v3, Amul.Length + 1 + Bmul.Length)) PutEntry(st, t, v3, Amul + "*" + Bmul, PREC_MUL, ctx);
            }
            if (!B.Val.N.IsZero && A.Mag / B.Mag <= lim && A.DenMag * Math.Abs(ToDouble(B.Val.N)) <= ctx.MaxDenNum)
            {
                string Amul2 = A.Prec < PREC_MUL ? ParenWrap(A.Text) : A.Text;
                string Bmul2 = B.Prec <= PREC_MUL ? ParenWrap(B.Text) : B.Text;
                Rat v4;
                if (Rats.TryDiv(A.Val, B.Val, out v4))
                {
                    if (NeedText(st, v4, Amul2.Length + 1 + Bmul2.Length)) PutEntry(st, t, v4, Amul2 + "/" + Bmul2, PREC_MUL, ctx);
                }
            }
            // ---- 高级运算：乘方（指数 2..4，底数绝对值 <= 40，结果受 lim 约束） ----
            if (ctx.Limits.AllowPower)
            {
                ctx.Limits.PowerTry++;
                bool bPlain = B.Text.Length == 1 && B.Text[0] >= '2' && B.Text[0] <= '4' && B.Val.D.IsOne;
                if (bPlain && B.Val.N >= 2 && B.Val.N <= 4 && A.Mag <= 40)
                {
                    int k = (int)B.Val.N;
                    double est = Math.Pow(A.Mag, k);
                    if (est <= lim)
                    {
                        Rat vp = new Rat(BigInteger.Pow(A.Val.N, k), BigInteger.Pow(A.Val.D, k));
                        string baseText = A.Prec < PREC_ATOM ? ParenWrap(A.Text) : A.Text;
                        string text = baseText + "^" + B.Text;
                        if (NeedText(st, vp, text.Length))
                        {
                            if (PutEntry(st, t, vp, text, PREC_ATOM, ctx)) ctx.Limits.PowerPut++; else ctx.Limits.PowerSkipCap++;
                        }
                    }
                }
                bool aPlain = A.Text.Length == 1 && A.Text[0] >= '2' && A.Text[0] <= '4' && A.Val.D.IsOne;
                if (any && aPlain && A.Val.N >= 2 && A.Val.N <= 4 && B.Mag <= 40)
                {
                    int k = (int)A.Val.N;
                    double est = Math.Pow(B.Mag, k);
                    if (est <= lim)
                    {
                        Rat vp2 = new Rat(BigInteger.Pow(B.Val.N, k), BigInteger.Pow(B.Val.D, k));
                        string baseText2 = B.Prec < PREC_ATOM ? ParenWrap(B.Text) : B.Text;
                        string text2 = baseText2 + "^" + A.Text;
                        if (NeedText(st, vp2, text2.Length)) PutEntry(st, t, vp2, text2, PREC_ATOM, ctx);
                    }
                }
            }

            if (!any) return;

            if (A.Mag + B.Mag <= lim)
            {
                Rat v5 = Rats.Sub(B.Val, A.Val);
                if (NeedText(st, v5, B.Text.Length + 1 + Aadd.Length)) PutEntry(st, t, v5, B.Text + "-" + Aadd, PREC_ADD, ctx);
            }
            if (!A.Val.N.IsZero && B.Mag / A.Mag <= lim && B.DenMag * Math.Abs(ToDouble(A.Val.N)) <= ctx.MaxDenNum)
            {
                string Bmul3 = B.Prec < PREC_MUL ? ParenWrap(B.Text) : B.Text;
                string Amul3 = A.Prec <= PREC_MUL ? ParenWrap(A.Text) : A.Text;
                Rat v6;
                if (Rats.TryDiv(B.Val, A.Val, out v6))
                {
                    if (NeedText(st, v6, Bmul3.Length + 1 + Amul3.Length)) PutEntry(st, t, v6, Bmul3 + "/" + Amul3, PREC_MUL, ctx);
                }
            }
        }

        static void ApplyFactorials(State st, int t, Ctx ctx)
        {
            if (!ctx.Limits.AllowFactorial) return;
            int i = 0, rounds = 0;
            while (rounds++ < 8 && !ctx.Aborted)
            {
                List<Entry> bucket = st.ByTokens[t];
                bool added = false;
                for (; i < bucket.Count && !ctx.Aborted; i++)
                {
                    Entry e = bucket[i];
                    if (!e.Val.D.IsOne || e.Val.N.Sign < 0 || e.Val.N > ctx.FactMaxBig) continue;
                    Rat f = Rats.FromInt(FactorialBig(e.Val.N));
                    string text = e.Prec < PREC_ATOM ? ParenWrap(e.Text) + "!" : e.Text + "!";
                    if (PutEntry(st, t, f, text, PREC_ATOM, ctx)) added = true;
                }
                if (!added && i >= bucket.Count) break;
            }
        }

        static List<Entry> TokenValues(string sub, Limits limits)
        {
            List<Entry> outp = new List<Entry>();
            Entry e0 = new Entry();
            e0.Val = Rats.FromInt(BigInteger.Parse(sub, CultureInfo.InvariantCulture));
            e0.Text = sub; e0.Prec = PREC_ATOM;
            outp.Add(e0);
            if (limits.AllowDecimal)
            {
                for (int m = 1; m < sub.Length; m++)
                {
                    string a = sub.Substring(0, m);
                    string b = sub.Substring(m);
                    BigInteger den = BigInteger.Pow(10, b.Length);
                    Rat val = new Rat(BigInteger.Parse(a, CultureInfo.InvariantCulture) * den + BigInteger.Parse(b, CultureInfo.InvariantCulture), den);
                    Entry e = new Entry();
                    e.Val = val; e.Text = a + "." + b; e.Prec = PREC_ATOM;
                    outp.Add(e);
                }
            }
            return outp;
        }

        static State Ensure(string sub, int need, Ctx ctx)
        {
            State st = GetState(sub, ctx);
            int len = sub.Length;
            if (need > len) need = len;
            if (st.Built >= need) return st;

            if (st.Built == 0)
            {
                List<Entry> toks = TokenValues(sub, ctx.Limits);
                for (int i = 0; i < toks.Count; i++)
                {
                    PutEntry(st, 1, toks[i].Val, toks[i].Text, toks[i].Prec, ctx);
                }
                st.Built = 1;
                ApplyFactorials(st, 1, ctx);
                if (need <= 1 || ctx.Aborted) return st;
            }

            for (int t = st.Built + 1; t <= need; t++)
            {
                for (int cut = 1; cut < len; cut++)
                {
                    State lt = Ensure(sub.Substring(0, cut), t - 1, ctx);
                    State rt = Ensure(sub.Substring(cut), t - 1, ctx);
                    if (ctx.Aborted) break;
                    for (int i = 1; i < t; i++)
                    {
                        int j = t - i;
                        if (i >= lt.ByTokens.Length || j >= rt.ByTokens.Length) continue;
                        List<Entry> La = lt.ByTokens[i];
                        List<Entry> Ra = rt.ByTokens[j];
                        if (La.Count == 0 || Ra.Count == 0) continue;
                        for (int x = 0; x < La.Count; x++)
                        {
                            Entry A = La[x];
                            for (int y = 0; y < Ra.Count; y++)
                            {
                                if ((++ctx.Steps & 1023) == 0 && (long)Environment.TickCount > ctx.Deadline) { ctx.Aborted = true; break; }
                                AddPair(st, t, A, Ra[y], ctx);
                            }
                            if (ctx.Aborted) break;
                        }
                        if (ctx.Aborted) break;
                    }
                    if (ctx.Aborted) break;
                }
                st.Built = t;
                if (ctx.Aborted) break;
                ApplyFactorials(st, t, ctx);
            }
            return st;
        }

        static void AddWriting(List<string> outp, HashSet<string> seen, string text, int limit)
        {
            if (outp.Count >= limit) return;
            if (seen.Contains(text)) return;
            seen.Add(text);
            outp.Add(text);
        }

        static void CollectWritings(string sub, Rat target, Ctx ctx, int limit, List<string> outp, HashSet<string> seen)
        {
            if (outp.Count >= limit) return;
            State st;
            if (!ctx.Cache.TryGetValue(sub, out st)) return;

            Entry direct;
            if (st.TryGet(target, out direct)) AddWriting(outp, seen, direct.Text, limit);
            if (outp.Count >= limit) return;

            if (ctx.Limits.AllowFactorial)
            {
                bool done = false;
                for (int t = 1; t < st.ByTokens.Length && !done; t++)
                {
                    List<Entry> bucket = st.ByTokens[t];
                    for (int i = 0; i < bucket.Count; i++)
                    {
                        Entry e = bucket[i];
                        if (outp.Count >= limit) { done = true; break; }
                        if (!e.Val.D.IsOne || e.Val.N.Sign < 0 || e.Val.N > ctx.FactMaxBig) continue;
                        if (!Rats.Eq(Rats.FromInt(FactorialBig(e.Val.N)), target)) continue;
                        AddWriting(outp, seen, e.Prec < PREC_ATOM ? ParenWrap(e.Text) + "!" : e.Text + "!", limit);
                    }
                }
                if (outp.Count >= limit) return;
            }

            bool any = ctx.Limits.OrderMode == "any";
            for (int cut = 1; cut < sub.Length && outp.Count < limit; cut++)
            {
                State L, Rm;
                if (!ctx.Cache.TryGetValue(sub.Substring(0, cut), out L)) continue;
                if (!ctx.Cache.TryGetValue(sub.Substring(cut), out Rm)) continue;
                for (int ti = 1; ti < L.ByTokens.Length && outp.Count < limit; ti++)
                {
                    List<Entry> La = L.ByTokens[ti];
                    if (La.Count == 0) continue;
                    for (int tj = 1; tj < Rm.ByTokens.Length && outp.Count < limit; tj++)
                    {
                        List<Entry> Ra = Rm.ByTokens[tj];
                        if (Ra.Count == 0) continue;
                        for (int x = 0; x < La.Count && outp.Count < limit; x++)
                        {
                            Entry A = La[x];
                            for (int y = 0; y < Ra.Count && outp.Count < limit; y++)
                            {
                                Entry B = Ra[y];
                                string bAdd = B.Prec <= PREC_ADD ? ParenWrap(B.Text) : B.Text;
                                if (Rats.Eq(Rats.Add(A.Val, B.Val), target)) AddWriting(outp, seen, A.Text + "+" + bAdd, limit);
                                if (outp.Count >= limit) break;
                                if (Rats.Eq(Rats.Sub(A.Val, B.Val), target)) AddWriting(outp, seen, A.Text + "-" + bAdd, limit);
                                if (outp.Count >= limit) break;
                                if (Rats.Eq(Rats.Mul(A.Val, B.Val), target)) AddWriting(outp, seen, (A.Prec < PREC_MUL ? ParenWrap(A.Text) : A.Text) + "*" + (B.Prec < PREC_MUL ? ParenWrap(B.Text) : B.Text), limit);
                                if (outp.Count >= limit) break;
                                Rat d1;
                                if (Rats.TryDiv(A.Val, B.Val, out d1) && Rats.Eq(d1, target)) AddWriting(outp, seen, (A.Prec < PREC_MUL ? ParenWrap(A.Text) : A.Text) + "/" + (B.Prec <= PREC_MUL ? ParenWrap(B.Text) : B.Text), limit);
                                if (!any || outp.Count >= limit) continue;
                                if (Rats.Eq(Rats.Sub(B.Val, A.Val), target)) AddWriting(outp, seen, B.Text + "-" + (A.Prec <= PREC_ADD ? ParenWrap(A.Text) : A.Text), limit);
                                if (outp.Count >= limit) break;
                                Rat d2;
                                if (Rats.TryDiv(B.Val, A.Val, out d2) && Rats.Eq(d2, target)) AddWriting(outp, seen, (B.Prec < PREC_MUL ? ParenWrap(B.Text) : B.Text) + "/" + (A.Prec <= PREC_MUL ? ParenWrap(A.Text) : A.Text), limit);
                            }
                        }
                    }
                }
            }
        }

        // ---------- 保底构造（与具体数字串无关） ----------
        // 记 P = 数字串本身组成的整数；1 块 = 数字串写一遍。
        //   n ≥ P      →  P×(q 的式子) + (r 的式子)，q = n÷P，r = n mod P，递归
        //   0 ≤ n < P  →  先完整搜 1~2 块；搜不到则十进制拆分 Σ dᵢ×10ⁱ
        //                （系数与 10 的幂都先查 DP 巧解，例如 325 的 100 = 325/3.25）
        //   n < 0      →  (0 的写法) - (|n| 的式子)
        //   小数 n/d   →  (n 的式子) ÷ (d 的式子)
        static string RepeatChar(string pat, int k)
        {
            StringBuilder sb = new StringBuilder(pat.Length * k);
            for (int i = 0; i < k; i++) sb.Append(pat);
            return sb.ToString();
        }

        static FallbackInfo LookupValue(string pat, Rat value, int maxBlocks, int maxTokens, Ctx ctx)
        {
            for (int k = 1; k <= maxBlocks; k++)
            {
                if (k > 1 && pat.Length * k > Math.Max(12, pat.Length * 2)) break;
                int tok = Math.Min(maxTokens, (pat.Length * k <= 6) ? 6 : 4);
                tok = Math.Min(tok, pat.Length * k);
                State st = Ensure(RepeatChar(pat, k), tok, ctx);
                Entry e;
                if (st.TryGet(value, out e))
                {
                    FallbackInfo fi = new FallbackInfo();
                    fi.Expression = e.Text; fi.Pretty = Pretty(e.Text); fi.Blocks = k; fi.Kind = "structured";
                    return fi;
                }
                if (ctx.Aborted) break;
            }
            return null;
        }

        sealed class FallbackBuilder
        {
            readonly Ctx ctx;
            readonly string pat;
            readonly BigInteger P;
            readonly Dictionary<string, FallbackInfo> cache = new Dictionary<string, FallbackInfo>();

            public FallbackBuilder(Ctx context, string pattern)
            {
                ctx = context;
                pat = pattern;
                P = BigInteger.Parse(pat, CultureInfo.InvariantCulture);
            }

            public BigInteger PatternValue { get { return P; } }

            FallbackInfo Cached(string key)
            {
                FallbackInfo v;
                if (cache.TryGetValue(key, out v)) return v;
                return null;
            }

            FallbackInfo Unit(string name, Rat value, int maxBlocks, int maxTokens)
            {
                string ck = "u" + name;
                if (cache.ContainsKey(ck)) return cache[ck];
                FallbackInfo r = LookupValue(pat, value, maxBlocks, maxTokens, ctx);
                cache[ck] = r;
                return r;
            }

            public FallbackInfo One() { return Unit("one", Rats.One, 3, 3); }
            FallbackInfo Ten() { return Unit("ten", Rats.FromInt(10), 3, 4); }
            public FallbackInfo Zero() { return Unit("zero", Rats.Zero, 2, 3); }

            // d 个 unit 相加
            static FallbackInfo SumCopies(FallbackInfo u, int count)
            {
                FallbackInfo fi = new FallbackInfo();
                fi.Kind = "structured";
                if (count <= 1) { fi.Expression = "(" + u.Expression + ")"; fi.Blocks = u.Blocks; fi.Pretty = Pretty(fi.Expression); return fi; }
                StringBuilder sb = new StringBuilder();
                for (int i = 0; i < count; i++)
                {
                    if (i > 0) sb.Append('+');
                    sb.Append('(').Append(u.Expression).Append(')');
                }
                fi.Expression = sb.ToString();
                fi.Blocks = count * u.Blocks;
                fi.Pretty = Pretty(fi.Expression);
                return fi;
            }

            static FallbackInfo MulCopies(FallbackInfo u, int count)
            {
                FallbackInfo fi = new FallbackInfo();
                StringBuilder sb = new StringBuilder();
                for (int i = 0; i < count; i++)
                {
                    if (i > 0) sb.Append('*');
                    sb.Append('(').Append(u.Expression).Append(')');
                }
                fi.Expression = sb.ToString();
                fi.Blocks = count * u.Blocks;
                fi.Pretty = Pretty(fi.Expression);
                fi.Kind = "structured";
                return fi;
            }

            // 1..9：先查 DP 巧解，否则用 1 累加
            FallbackInfo SmallInt(int d)
            {
                string ck = "si" + d;
                FallbackInfo hit = Cached(ck);
                if (hit != null || cache.ContainsKey(ck)) return hit;
                FallbackInfo r = LookupValue(pat, Rats.FromInt(d), 3, 4, ctx);
                if (r == null)
                {
                    FallbackInfo u = One();
                    if (u != null && d <= 9) r = SumCopies(u, d);
                }
                cache[ck] = r;
                return r;
            }

            // 10^p：先查 DP（如 325 的 100 = 325/3.25），否则用 10 连乘
            FallbackInfo PowerUnit(int p)
            {
                string ck = "p" + p;
                FallbackInfo hit = Cached(ck);
                if (hit != null || cache.ContainsKey(ck)) return hit;
                BigInteger target = BigInteger.Parse("1" + new string('0', p), CultureInfo.InvariantCulture);
                FallbackInfo r = LookupValue(pat, Rats.FromInt(target), 3, 4, ctx);
                if (r == null)
                {
                    FallbackInfo t = Ten();
                    if (t != null) r = MulCopies(t, p);
                }
                cache[ck] = r;
                return r;
            }

            public FallbackInfo Base(int m)
            {
                string ck = "b" + m;
                FallbackInfo hit = Cached(ck);
                if (hit != null || cache.ContainsKey(ck)) return hit;
                FallbackInfo res = LookupValue(pat, Rats.FromInt(m), 2, 6, ctx);
                if (res == null && m == 0) res = Zero();
                if (res == null && m > 0)
                {
                    string ds = m.ToString(CultureInfo.InvariantCulture);
                    List<string> terms = new List<string>();
                    int blocks = 0;
                    bool ok = true;
                    for (int i = 0; i < ds.Length; i++)
                    {
                        int d = ds[i] - '0';
                        if (d == 0) continue;
                        int power = ds.Length - 1 - i;
                        FallbackInfo coef = SmallInt(d);
                        if (coef == null) { ok = false; break; }
                        if (power == 0)
                        {
                            terms.Add("(" + coef.Expression + ")");
                            blocks += coef.Blocks;
                        }
                        else
                        {
                            FallbackInfo pw = PowerUnit(power);
                            if (pw == null) { ok = false; break; }
                            terms.Add("(" + coef.Expression + ")*(" + pw.Expression + ")");
                            blocks += coef.Blocks + pw.Blocks;
                        }
                    }
                    if (ok && terms.Count > 0)
                    {
                        res = new FallbackInfo();
                        res.Expression = string.Join("+", terms.ToArray());
                        res.Blocks = blocks;
                        res.Pretty = Pretty(res.Expression);
                        res.Kind = "structured";
                    }
                }
                cache[ck] = res;
                return res;
            }

            public FallbackInfo Build(BigInteger n)
            {
                string ck = "n" + n.ToString(CultureInfo.InvariantCulture);
                FallbackInfo hit = Cached(ck);
                if (hit != null || cache.ContainsKey(ck)) return hit;
                FallbackInfo res = null;
                if (n < P || P <= BigInteger.One)
                {
                    // P == 1 时 n/P == n，走下面的分支会无限递归（栈溢出闪退）
                    if (n <= int.MaxValue) res = Base((int)n);
                }
                else
                {
                    BigInteger q = n / P, r = n % P;
                    FallbackInfo qe = Build(q);
                    if (qe != null)
                    {
                        string text = pat + "*(" + qe.Expression + ")";
                        int blocks = 1 + qe.Blocks;
                        bool ok = true;
                        if (r > 0)
                        {
                            FallbackInfo re = Base((int)r);
                            if (re != null) { text += "+(" + re.Expression + ")"; blocks += re.Blocks; }
                            else ok = false;
                        }
                        if (ok)
                        {
                            res = new FallbackInfo();
                            res.Expression = text;
                            res.Blocks = blocks;
                            res.Pretty = Pretty(text);
                            res.Kind = "structured";
                        }
                    }
                }
                cache[ck] = res;
                return res;
            }
        }

        /// <summary>直接用 a^b 命中目标（a 来自本数字串可写出的整数，b=2..4）。</summary>
        static FallbackInfo PowerDirect(Rat target, Ctx ctx)
        {
            if (!ctx.Limits.AllowPower || !target.IsInt) return null;
            string pat = NormalizePattern(ctx.Limits.Pattern);
            if (pat == null) return null;
            for (int k = 1; k <= ctx.Limits.MaxBlocks; k++)
            {
                string digits = RepeatPattern(pat, k);
                List<Entry> toks = TokenValues(digits, ctx.Limits);
                for (int i = 0; i < toks.Count; i++)
                {
                    Entry e = toks[i];
                    if (!e.Val.D.IsOne) continue;
                    if (e.Val.N.Sign == 0) continue;
                    double mag = Math.Abs(ToDouble(e.Val.N));
                    if (mag > 40) continue;
                    // 平方根：仅完全平方可写成 √x
                    if (ctx.Limits.AllowRoot && e.Val.D.IsOne && e.Val.N.Sign > 0)
                    {
                        BigInteger root = ISqrt(e.Val.N);
                        if (root * root == e.Val.N && root == target.N)
                        {
                            string text = "√" + e.Text;
                            VerifyResult vr2 = Verify(text, target, true, ctx.Limits.OrderMode, pat);
                            if (vr2.DigitsOk && vr2.ValueOk)
                            {
                                FallbackInfo fi2 = new FallbackInfo();
                                fi2.Expression = text; fi2.Pretty = Pretty(text);
                                fi2.Blocks = vr2.Blocks; fi2.Kind = "root";
                                return fi2;
                            }
                        }
                    }
                    // 组合数 / gcd / mod：与另一个候选值配对试
                    if (ctx.Limits.AllowComb || ctx.Limits.AllowGcdMod)
                    {
                        for (int j2 = 0; j2 < toks.Count; j2++)
                        {
                            Entry f2 = toks[j2];
                            if (f2 == e || !f2.Val.D.IsOne || !e.Val.D.IsOne) continue;
                            if (e.Val.N.Sign < 0 || f2.Val.N.Sign < 0) continue;
                            if (e.Val.N > 200 || f2.Val.N > 200) continue;
                            string[] fns = ctx.Limits.AllowComb
                                ? (ctx.Limits.AllowGcdMod ? new string[] { "C", "gcd", "mod" } : new string[] { "C" })
                                : new string[] { "gcd", "mod" };
                            for (int fi2 = 0; fi2 < fns.Length; fi2++)
                            {
                                if (fns[fi2] == "mod" && f2.Val.N.IsZero) continue;
                                string txt = fns[fi2] + "(" + e.Text + "," + f2.Text + ")";
                                VerifyResult vr4 = Verify(txt, target, true, ctx.Limits.OrderMode, pat);
                                if (!(vr4.DigitsOk && vr4.ValueOk)) continue;
                                FallbackInfo fi4 = new FallbackInfo();
                                fi4.Expression = txt; fi4.Pretty = Pretty(txt);
                                fi4.Blocks = vr4.Blocks; fi4.Kind = "adv";
                                return fi4;
                            }
                        }
                    }
                    // 2x2 行列式：det(a,b,c,d) = a*d - b*c
                    if (ctx.Limits.AllowDet)
                    {
                        for (int j2 = 0; j2 < toks.Count; j2++)
                        {
                            Entry t2 = toks[j2];
                            if (!t2.Val.D.IsOne || t2.Val.N.Sign < 0 || t2.Val.N > 40) continue;
                            for (int j3 = 0; j3 < toks.Count; j3++)
                            {
                                Entry t3 = toks[j3];
                                if (!t3.Val.D.IsOne || t3.Val.N.Sign < 0 || t3.Val.N > 40) continue;
                                for (int j4 = 0; j4 < toks.Count; j4++)
                                {
                                    Entry t4 = toks[j4];
                                    if (!t4.Val.D.IsOne || t4.Val.N.Sign < 0 || t4.Val.N > 40) continue;
                                    BigInteger dv = e.Val.N * t4.Val.N - t2.Val.N * t3.Val.N;
                                    if (dv != target.N) continue;
                                    string txt = "det(" + e.Text + "," + t2.Text + "," + t3.Text + "," + t4.Text + ")";
                                    VerifyResult vrd = Verify(txt, target, true, ctx.Limits.OrderMode, pat);
                                    if (!(vrd.DigitsOk && vrd.ValueOk)) continue;
                                    FallbackInfo fid = new FallbackInfo();
                                    fid.Expression = txt; fid.Pretty = Pretty(txt);
                                    fid.Blocks = vrd.Blocks; fid.Kind = "det";
                                    return fid;
                                }
                            }
                        }
                    }
                    for (int b = 2; b <= 4; b++)

                    {
                        BigInteger pow = BigInteger.Pow(e.Val.N, b);
                        if (pow != target.N) continue;
                        string text = e.Text + "^" + b.ToString(CultureInfo.InvariantCulture);
                        VerifyResult vr = Verify(text, target, true, ctx.Limits.OrderMode, pat);
                        if (!(vr.DigitsOk && vr.ValueOk)) continue;
                        FallbackInfo fi = new FallbackInfo();
                        fi.Expression = text;
                        fi.Pretty = Pretty(text);
                        fi.Blocks = vr.Blocks;
                        fi.Kind = "power";
                        return fi;
                    }
                }
            }
            return null;
        }

        static FallbackInfo StructuredFallback(Rat target, Ctx ctx)
        {
            ctx.Aborted = false;
            ctx.Deadline = (long)Environment.TickCount + ctx.Limits.FallbackBudgetMs;
            string pat = NormalizePattern(ctx.Limits.Pattern);
            if (pat == null) pat = "325";
            FallbackBuilder b = new FallbackBuilder(ctx, pat);
            FallbackInfo zero = b.Zero();
            string zeroText = zero != null ? zero.Expression : null;

            if (target.IsInt)
            {
                if (target.N.IsZero)
                {
                    if (zeroText == null) return null;
                    FallbackInfo z = new FallbackInfo();
                    z.Expression = zeroText; z.Pretty = Pretty(zeroText); z.Blocks = 1; z.Kind = "structured";
                    return z;
                }
                FallbackInfo bn = b.Build(target.N.Sign < 0 ? -target.N : target.N);
                if (bn == null) return null;
                if (target.N.Sign > 0) return bn;
                if (zeroText == null) return null;
                FallbackInfo neg = new FallbackInfo();
                neg.Expression = zeroText + "-(" + bn.Expression + ")";
                neg.Pretty = Pretty(neg.Expression);
                neg.Blocks = 1 + bn.Blocks;
                neg.Kind = "structured";
                return neg;
            }

            BigInteger pos = target.N.Sign < 0 ? -target.N : target.N;
            string numExpr;
            if (pos.IsZero)
            {
                if (zeroText == null) return null;
                numExpr = zeroText;
            }
            else
            {
                FallbackInfo nb = b.Build(pos);
                if (nb == null) return null;
                numExpr = nb.Expression;
            }
            FallbackInfo db = b.Build(target.D);
            if (db == null) return null;
            string frac = "(" + numExpr + ")÷(" + db.Expression + ")";
            if (target.N.Sign < 0)
            {
                if (zeroText == null) return null;
                frac = zeroText + "-(" + frac + ")";
            }
            FallbackInfo fi = new FallbackInfo();
            fi.Expression = frac.Replace('÷', '/');
            fi.Pretty = Pretty(fi.Expression);
            fi.Blocks = 0;
            fi.Kind = "structured";
            return fi;
        }

        // 次选保底：用「1 的写法」累加（u / z 来自同一数字串的搜索）
        static FallbackInfo SimpleOnesFallback(Rat target, FallbackBuilder b, int cap)
        {
            if (!target.IsInt) return null;
            FallbackInfo u = b.One();
            if (u == null) return null;
            BigInteger count = target.N.Sign < 0 ? -target.N : target.N;
            if (count > cap) return null;
            if (count > 24) return null;                       // 累加项太多就是“满屏重复”，不如不给
            if (count * (u.Expression.Length + 3) > 260) return null;
            int n = (int)count;
            FallbackInfo fi = new FallbackInfo();
            fi.Kind = "simple";
            if (target.N.Sign > 0)
            {
                if (n == 0) return null;
                StringBuilder sb = new StringBuilder();
                for (int i = 0; i < n; i++)
                {
                    if (i > 0) sb.Append('+');
                    if (n > 1) sb.Append('(').Append(u.Expression).Append(')');
                    else sb.Append(u.Expression);
                }
                fi.Expression = sb.ToString();
                fi.Blocks = n * u.Blocks;
            }
            else
            {
                FallbackInfo z = b.Zero();
                if (z == null) return null;
                StringBuilder sb = new StringBuilder(z.Expression);
                for (int i = 0; i < n; i++) sb.Append("-(").Append(u.Expression).Append(')');
                fi.Expression = sb.ToString();
                fi.Blocks = 0;
            }
            fi.Pretty = Pretty(fi.Expression);
            return fi;
        }

        // ===================== 会话 =====================
        public class Session
        {
            readonly Limits limits;
            readonly Ctx ctx;
            readonly string pat;

            public Session(Limits options)
            {
                limits = options == null ? new Limits() : options.Clone();
                limits.MaxBlocks = Math.Max(1, Math.Min(6, limits.MaxBlocks));
                pat = NormalizePattern(limits.Pattern);
                if (pat == null)
                {
                    // 非法数字串（如 "0"、"0325"）：不要偷偷换成 325，交给上层如实报错
                    pat = "325";
                    patternInvalid = true;
                }
                limits.Pattern = pat;
                ctx = new Ctx();
                ctx.Limits = limits;
                ctx.MulLimit = limits.MaxAbsValue * 4;
                ctx.MaxDenNum = limits.MaxDenominator;
                ctx.FactMaxBig = new BigInteger(limits.FactMax);
            }

            public bool patternInvalid;

            public Limits LimitsUsed { get { return limits; } }
            public int CacheSize { get { return ctx.Cache.Count; } }
            public void Reset() { ctx.Cache.Clear(); }

            public SolveResult Solve(string targetInput)
            {
                if (patternInvalid)
                {
                    SolveResult bad0 = new SolveResult();
                    bad0.Error = "数字串无效（不能以 0 开头）";
                    bad0.SearchMode = limits.SearchMode;
                    bad0.OrderMode = limits.OrderMode;
                    bad0.TargetText = targetInput;
                    return bad0;
                }
                TargetParse tp = ParseTarget(targetInput);
                if (!tp.Ok)
                {
                    SolveResult bad = new SolveResult();
                    bad.Error = tp.Error;
                    bad.SearchMode = limits.SearchMode;
                    bad.OrderMode = limits.OrderMode;
                    return bad;
                }
                return Solve(tp.Value, tp.Text);
            }

            public SolveResult Solve(Rat target, string targetText)
            {
                if (patternInvalid)
                {
                    SolveResult bad1 = new SolveResult();
                    bad1.Error = "数字串无效（不能以 0 开头）";
                    bad1.SearchMode = limits.SearchMode;
                    bad1.OrderMode = limits.OrderMode;
                    bad1.Target = target;
                    bad1.TargetText = targetText;
                    return bad1;
                }
                SolveResult result = new SolveResult();
                result.Target = target;
                result.TargetText = targetText;
                result.SearchMode = limits.SearchMode;
                result.OrderMode = limits.OrderMode;
                result.Pattern = limits.Pattern;

                Stopwatch sw = Stopwatch.StartNew();
                ctx.Deadline = (long)Environment.TickCount + limits.TimeBudgetMs;
                ctx.Aborted = false;
                ctx.Pruned = false;
                ctx.Limited = false;

                Entry hitEntry = null;
                int hitBlocks = 0, hitTokens = 0;
                string hitDigits = null;

                for (int kk = 1; kk <= limits.MaxBlocks; kk++)
                {
                    if (ctx.Aborted) break;
                    string digits = RepeatPattern(pat, kk);
                    long t0 = Environment.TickCount;
                    int lastTok = 0, lastSize = 0;
                    for (int t = 1; t <= digits.Length; t++)
                    {
                        State st = Ensure(digits, t, ctx);
                        lastTok = t;
                        lastSize = st.MapCount;
                        Entry entry;
                        if (st.TryGet(target, out entry))
                        {
                            if (hitEntry == null || limits.SearchMode != "length" || entry.Text.Length < hitEntry.Text.Length)
                            {
                                hitEntry = entry; hitBlocks = kk; hitTokens = entry.Tokens; hitDigits = digits;
                            }
                            if (limits.SearchMode != "length") break;
                        }
                        if (ctx.Aborted) break;
                    }
                    result.SearchedBlocks.Add(kk);
                    BlockStat bs = new BlockStat();
                    bs.K = kk; bs.Values = lastSize; bs.TokensBuilt = lastTok;
                    bs.Ms = Environment.TickCount - t0; bs.Pruned = ctx.Pruned; bs.Limited = ctx.Limited;
                    result.Stats.Add(bs);
                    if (hitEntry != null && limits.SearchMode != "length") break;
                    if (ctx.Aborted) break;
                }

                result.ElapsedMs = sw.ElapsedMilliseconds;
                result.Pruned = ctx.Pruned;
                result.Limited = ctx.Limited;
                result.Aborted = ctx.Aborted;
                int entries = 0;
                foreach (KeyValuePair<string, State> kv in ctx.Cache) entries += kv.Value.MapCount;
                result.Entries = entries;

                if (hitEntry != null)
                {
                    result.Ok = true;
                    result.Blocks = hitBlocks;
                    result.Tokens = hitTokens;
                    result.Digits = hitDigits;
                    result.Expression = hitEntry.Text;
                    result.ExpressionPretty = Pretty(hitEntry.Text);
                    result.TokenCount = CountTokens(hitEntry.Text);

                    List<string> outp = new List<string>();
                    outp.Add(hitEntry.Text);
                    HashSet<string> seen = new HashSet<string>();
                    seen.Add(hitEntry.Text);
                    CollectWritings(hitDigits, target, ctx, limits.SolutionListLimit, outp, seen);
                    outp.Sort(delegate (string a, string b)
                    {
                        int c = a.Length.CompareTo(b.Length);
                        if (c != 0) return c;
                        return string.CompareOrdinal(a, b);
                    });
                    for (int i = 0; i < outp.Count; i++)
                    {
                        VerifyResult vr = Verify(outp[i], target, true, limits.OrderMode, pat);
                        if (!(vr.DigitsOk && vr.ValueOk)) continue;
                        AltWriting alt = new AltWriting();
                        alt.Expression = outp[i]; alt.Pretty = Pretty(outp[i]); alt.Blocks = hitBlocks; alt.Length = outp[i].Length;
                        result.Alternatives.Add(alt);
                    }
                    if (result.Alternatives.Count == 0)
                    {
                        AltWriting alt = new AltWriting();
                        alt.Expression = hitEntry.Text; alt.Pretty = Pretty(hitEntry.Text); alt.Blocks = hitBlocks; alt.Length = hitEntry.Text.Length;
                        result.Alternatives.Add(alt);
                    }
                    VerifyResult chk = Verify(hitEntry.Text, target, true, limits.OrderMode, pat);
                    result.DigitsOk = chk.DigitsOk;
                    result.ValueOk = chk.ValueOk;
                    if (!result.DigitsOk || !result.ValueOk)
                    {
                        result.Ok = false;
                        result.Error = "内部校验未通过：" + (chk.Error != null ? chk.Error : "数字顺序或求值不符");
                    }
                }
                else
                {
                    FallbackInfo fb = null;
                    FallbackBuilder fbTools = null;
                    try
                    {
                        fb = StructuredFallback(target, ctx);
                        if (fb == null)
                        {
                            // 次选：用本数字串能表达的「1」累加
                            fbTools = new FallbackBuilder(ctx, pat);
                            fb = SimpleOnesFallback(target, fbTools, 3000);
                        }
                    }
                    catch (Exception) { fb = null; }
                    if (fb != null)
                    {
                        VerifyResult fchk = Verify(fb.Expression, target, true, limits.OrderMode, pat);
                        if (fchk.DigitsOk && fchk.ValueOk) fb.Blocks = fchk.Blocks;
                        else fb = null;
                    }
                    if (fb != null)
                    {
                        fb.Pretty = Pretty(fb.Expression);
                        result.FallbackResult = fb;
                    }
                }

                // 只在“超时”时提示（按设计的剪枝属于内部细节）
                if (result.Aborted)
                {
                    result.Notes.Add("搜索超时，结果可能不完整。");
                }
                return result;
            }
        }

        public static SolveResult SolveOnce(string target, Limits limits)
        {
            Session s = new Session(limits);
            SolveResult r = s.Solve(target);
            r.PowerTry = s.LimitsUsed.PowerTry;
            r.PowerPut = s.LimitsUsed.PowerPut;
            r.PowerSkipCap = s.LimitsUsed.PowerSkipCap;
            if (s.patternInvalid)
            {
                r.Ok = false;
                r.Expression = null;
                r.FallbackResult = null;
                r.Error = "数字串无效（不能以 0 开头）";
            }
            return r;
        }
    }
}
