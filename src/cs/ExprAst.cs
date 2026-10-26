/*
 * Calculate325 —— 表达式 AST（生成 → 配平 → 双渲染）
 * ---------------------------------------------------------------------------
 * 动机：原来 L3/L4/L5 的因子是“模板字符串 + 精确因子拼接”，一类式子要同时维护
 * 两份字符串（Expression 和 LaTeX），容易出现两套写法渐渐不一致。
 *
 * 这里给出一个最小但完整的 AST：
 *
 *     生成器构造节点树  →  FactorBalancer 用精确有理数配平其中一个自由系数
 *     →  Latex() / Text() 两个渲染器分别输出随机窗口和纯文本
 *
 * 节点覆盖：数、分式、乘、加减、幂、定积分、求和、连乘、极限、行列式、函数调用，
 * 以及 Leaf（承载已经算好的片段，便于增量迁移，不必一次改完旧模板）。
 *
 * 目前接入点：
 *   - 陷阱层（有限域 / 概率期望 / Fubini 拆分）全部走 AST；
 *   - L3/L4/L5 的因子链用一个适配器把旧的原子里装成 Leaf，再由 AST 统一合成，
 *     这样“最外层如何拼”只有一处实现。
 * 旧的混合模板（BuildOuter）暂不迁移，避免一次改动过大。
 */
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Text;

namespace Calculate325
{
    public abstract class XNode
    {
        public abstract string Latex { get; }
        public abstract string Text { get; }
        /// <summary>已知精确值（只有常数型节点给），用于配平与自检。</summary>
        public virtual Rat? Exact { get { return null; } }
        public virtual bool DependsOn(string v) { return true; }
        public int Depth { get { return 1; } }
    }

    /// <summary>整数/小数常量。</summary>
    public sealed class XNum : XNode
    {
        public Rat Value;
        public string Dec;          // 需要小数显示时用它（如 0.91）
        public XNum(Rat v) { Value = v; }
        public XNum(long n) { Value = new Rat(n, System.Numerics.BigInteger.One); }
        public static XNum Decimal(double v) { XNum x = new XNum(new Rat(0, 1)); x.Dec = v.ToString("0.00", CultureInfo.InvariantCulture); x._dec = v; x.Value = RatFromDecimal(x.Dec); return x; }
        double _dec = double.NaN;

        static Rat RatFromDecimal(string s)
        {
            int dot = s.IndexOf('.');
            if (dot < 0) return new Rat(System.Numerics.BigInteger.Parse(s, CultureInfo.InvariantCulture), System.Numerics.BigInteger.One);
            string digits = s.Replace(".", "");
            System.Numerics.BigInteger den = System.Numerics.BigInteger.One;
            for (int i = 0; i < s.Length - dot - 1; i++) den *= 10;
            return new Rat(System.Numerics.BigInteger.Parse(digits, CultureInfo.InvariantCulture), den);
        }

        public override string Latex
        {
            get
            {
                if (Dec != null) return Dec;
                return RatLatex(Value);
            }
        }
        public override string Text
        {
            get
            {
                if (Dec != null) return Dec;
                return RandomExpr.FormatRat(Value).Replace('-', '−');
            }
        }
        public override Rat? Exact { get { return Value; } }
        public override bool DependsOn(string v) { return false; }

        public static string RatLatex(Rat r)
        {
            string sign = r.N.Sign < 0 ? "-" : "";
            System.Numerics.BigInteger n = System.Numerics.BigInteger.Abs(r.N);
            if (r.D.IsOne) return sign + n.ToString(CultureInfo.InvariantCulture);
            return sign + "\\frac{" + n.ToString(CultureInfo.InvariantCulture) + "}{" + r.D.ToString(CultureInfo.InvariantCulture) + "}";
        }
    }

    /// <summary>分式 a/b。</summary>
    public sealed class XFrac : XNode
    {
        public XNode N, D;
        public override string Latex { get { return "\\frac{" + N.Latex + "}{" + D.Latex + "}"; } }
        public override string Text { get { return "(" + N.Text + ")/(" + D.Text + ")"; } }
        public override Rat? Exact
        {
            get
            {
                Rat? a = N.Exact, b = D.Exact;
                if (!a.HasValue || !b.HasValue || b.Value.N.IsZero) return null;
                return new Rat(a.Value.N * b.Value.D, a.Value.D * b.Value.N);
            }
        }
        public override bool DependsOn(string v) { return N.DependsOn(v) || D.DependsOn(v); }
    }

    /// <summary>乘法链。</summary>
    public sealed class XMul : XNode
    {
        public List<XNode> F = new List<XNode>();
        public override string Latex
        {
            get
            {
                StringBuilder sb = new StringBuilder();
                for (int i = 0; i < F.Count; i++)
                {
                    if (i > 0) sb.Append("\\cdot ");
                    sb.Append(F[i].Latex);
                }
                return sb.ToString();
            }
        }
        public override string Text
        {
            get
            {
                StringBuilder sb = new StringBuilder();
                for (int i = 0; i < F.Count; i++)
                {
                    if (i > 0) sb.Append("·");
                    sb.Append(F[i].Text);
                }
                return sb.ToString();
            }
        }
        public override Rat? Exact
        {
            get
            {
                Rat acc = Rats.One;
                for (int i = 0; i < F.Count; i++)
                {
                    Rat? e = F[i].Exact;
                    if (!e.HasValue) return null;
                    acc = Rats.Mul(acc, e.Value);
                }
                return acc;
            }
        }
        public override bool DependsOn(string v)
        {
            for (int i = 0; i < F.Count; i++) if (F[i].DependsOn(v)) return true;
            return false;
        }
    }

    /// <summary>加减（Sign = +1 / −1）。</summary>
    public sealed class XAdd : XNode
    {
        public XNode A, B;
        public int Sign;
        public override string Latex
        {
            get { return "\\left(" + A.Latex + (Sign < 0 ? "-" : "+") + B.Latex + "\\right)"; }
        }
        public override string Text
        {
            get { return "(" + A.Text + (Sign < 0 ? "−" : "+") + B.Text + ")"; }
        }
        public override Rat? Exact
        {
            get
            {
                Rat? a = A.Exact, b = B.Exact;
                if (!a.HasValue || !b.HasValue) return null;
                return Sign < 0 ? Rats.Sub(a.Value, b.Value) : Rats.Add(a.Value, b.Value);
            }
        }
        public override bool DependsOn(string v) { return A.DependsOn(v) || B.DependsOn(v); }
    }

    /// <summary>幂 a^b。</summary>
    public sealed class XPow : XNode
    {
        public XNode B, E;
        public override string Latex { get { return "{" + B.Latex + "}^{" + E.Latex + "}"; } }
        public override string Text { get { return B.Text + "^{" + E.Text + "}"; } }
        public override bool DependsOn(string v) { return B.DependsOn(v) || E.DependsOn(v); }
    }

    /// <summary>函数调用（sin/cos/tan/ln/sqrt/floor/ceil/frac/zeta/mu/phi/ord）。</summary>
    public sealed class XCall : XNode
    {
        public string Fn;
        public XNode Arg;
        public override string Latex
        {
            get
            {
                switch (Fn)
                {
                    case "sqrt": return "\\sqrt{" + Arg.Latex + "}";
                    case "zeta": return "\\zeta\\left(" + Arg.Latex + "\\right)";
                    case "mu": return "\\mu\\left(" + Arg.Latex + "\\right)";
                    case "phi": return "\\varphi\\left(" + Arg.Latex + "\\right)";
                    case "abs": return "\\left|" + Arg.Latex + "\\right|";
                    case "floor": return "\\left\\lfloor " + Arg.Latex + "\\right\\rfloor";
                    case "ceil": return "\\left\\lceil " + Arg.Latex + "\\right\\rceil";
                    case "fracpart": return "\\left\\{" + Arg.Latex + "\\right\\}";
                    case "fact": return "{" + Arg.Latex + "}!";
                }
                return "\\" + Fn + "\\left(" + Arg.Latex + "\\right)";
            }
        }
        public override string Text
        {
            get
            {
                switch (Fn)
                {
                    case "sqrt": return "√(" + Arg.Text + ")";
                    case "zeta": return "ζ(" + Arg.Text + ")";
                    case "mu": return "μ(" + Arg.Text + ")";
                    case "phi": return "φ(" + Arg.Text + ")";
                    case "floor": return "⌊" + Arg.Text + "⌋";
                    case "ceil": return "⌈" + Arg.Text + "⌉";
                    case "fracpart": return "{" + Arg.Text + "}";
                    case "fact": return Arg.Text + "!";
                }
                return Fn + "(" + Arg.Text + ")";
            }
        }
        public override bool DependsOn(string v) { return Arg.DependsOn(v); }
    }

    /// <summary>定积分。</summary>
    public sealed class XInt : XNode
    {
        public XNode Lo, Hi, Body;
        public string Var = "x";

        /// <summary>微分里的变量写法：x_1 → x_{1}，和非微分部分的写法保持一致。</summary>
        public string VarTex
        {
            get
            {
                int k = Var.IndexOf('_');
                if (k < 0) return Var;
                return Var.Substring(0, k) + "_{" + Var.Substring(k + 1) + "}";
            }
        }

        public override string Latex
        {
            get { return "\\int_{" + Lo.Latex + "}^{" + Hi.Latex + "}" + Body.Latex + "\\,d" + VarTex; }
        }
        public override string Text
        {
            get { return "∫_" + Lo.Text + "^{" + Hi.Text + "} " + Body.Text + " d" + Var; }
        }
        public override bool DependsOn(string v)
        {
            if (v == Var) return false;
            if (Lo.DependsOn(v) || Hi.DependsOn(v)) return true;
            return Body.DependsOn(v);
        }
    }

    /// <summary>求和 / 连乘。</summary>
    public sealed class XSeries : XNode
    {
        public XNode Lo, Hi, Body;
        public string Var = "k";
        public bool IsProd;
        public override string Latex
        {
            get
            {
                return (IsProd ? "\\prod" : "\\sum") + "_{" + Var + "=" + Lo.Latex + "}^{" + Hi.Latex + "}"
                    + Body.Latex;
            }
        }
        public override string Text
        {
            get { return (IsProd ? "∏" : "Σ") + "_{" + Var + "=" + Lo.Text + "}^{" + Hi.Text + "} " + Body.Text; }
        }
        public override bool DependsOn(string v)
        {
            if (v == Var) return false;
            if (Lo.DependsOn(v) || Hi.DependsOn(v)) return true;
            return Body.DependsOn(v);
        }
    }

    /// <summary>极限。</summary>
    public sealed class XLim : XNode
    {
        public XNode Body, To;
        public string Var = "n";
        public override string Latex
        {
            get { return "\\lim_{" + Var + "\\to" + To.Latex + "}" + Body.Latex; }
        }
        public override string Text
        {
            get { return "lim_{" + Var + "→" + To.Text + "} " + Body.Text; }
        }
        public override bool DependsOn(string v)
        {
            if (v == Var) return false;
            return Body.DependsOn(v);
        }
    }

    /// <summary>整数矩阵行列式（2–6 阶）。</summary>
    public sealed class XDet : XNode
    {
        public long[,] M;
        public override string Latex
        {
            get
            {
                int n = M.GetLength(0);
                StringBuilder sb = new StringBuilder("\\det\\begin{pmatrix}");
                for (int i = 0; i < n; i++)
                {
                    if (i > 0) sb.Append("\\\\");
                    for (int j = 0; j < n; j++)
                    {
                        if (j > 0) sb.Append("&");
                        sb.Append(M[i, j].ToString(CultureInfo.InvariantCulture));
                    }
                }
                sb.Append("\\end{pmatrix}");
                return sb.ToString();
            }
        }
        public override string Text
        {
            get
            {
                int n = M.GetLength(0);
                StringBuilder sb = new StringBuilder("det[");
                for (int i = 0; i < n; i++)
                {
                    if (i > 0) sb.Append(';');
                    sb.Append('[');
                    for (int j = 0; j < n; j++)
                    {
                        if (j > 0) sb.Append(',');
                        sb.Append(M[i, j].ToString(CultureInfo.InvariantCulture));
                    }
                    sb.Append(']');
                }
                sb.Append(']');
                return sb.ToString();
            }
        }
        public override Rat? Exact { get { return new Rat(Determinant(), System.Numerics.BigInteger.One); } }
        public override bool DependsOn(string v) { return false; }

        public System.Numerics.BigInteger Determinant()
        {
            int n = M.GetLength(0);
            if (n == 0) return 0;
            System.Numerics.BigInteger det = 0;
            for (int c = 0; c < n; c++)
            {
                if (M[0, c] == 0) continue;
                long[,] m = new long[n - 1, n - 1];
                for (int i = 1; i < n; i++)
                {
                    int cc = 0;
                    for (int j = 0; j < n; j++)
                    {
                        if (j == c) continue;
                        m[i - 1, cc++] = M[i, j];
                    }
                }
                XDet sub = new XDet(); sub.M = m;
                det += ((c & 1) == 0 ? 1 : -1) * M[0, c] * sub.Determinant();
            }
            return det;
        }
    }

    /// <summary>
    /// 导数算子 \frac{d}{d<var>}。用于 ODE 陷阱：验证 y 的导数再积分 = y 的端点差（FTC），
    /// 即"y 确实满足那条一阶方程"的残差为 0。
    /// </summary>
    public sealed class XDeriv : XNode
    {
        public XNode Body;
        public string Var = "x";
        public override string Latex
        {
            get { return "\\frac{d}{d" + Var + "}\\left(" + Body.Latex + "\\right)"; }
        }
        public override string Text
        {
            get { return "d/d" + Var + "(" + Body.Text + ")"; }
        }
        public override bool DependsOn(string v)
        {
            if (v == Var) return true;      // 导数在 x 处求值，依赖 x
            return Body.DependsOn(v);
        }
    }

    /// <summary>只在纯文本里加括号（LaTeX 那侧保持原样，便于按顶层 \cdot 折行）。</summary>
    public sealed class XTextParen : XNode
    {
        public XNode Inner;
        public override string Latex { get { return Inner.Latex; } }
        public override string Text { get { return "(" + Inner.Text + ")"; } }
        public override Rat? Exact { get { return Inner.Exact; } }
        public override bool DependsOn(string v) { return Inner.DependsOn(v); }
    }

    /// <summary>带定界符的组：\left[ ... \right] 之类。</summary>
    public sealed class XGroup : XNode
    {
        public XNode Inner;
        public string Open = "(", Close = ")";
        public override string Latex
        {
            get
            {
                // 花括号是"分组"而不是"定界符"：\left{ ... \right} 是非法写法，
                // 直接输出 { ... }（WpfMath 与解析器都按不可见分组处理）
                if (Open == "{" && Close == "}") return "{" + Inner.Latex + "}";
                return "\\left" + Open + Inner.Latex + "\\right" + Close;
            }
        }
        public override string Text
        {
            get { return Open.Replace("\\", "") + Inner.Text + Close.Replace("\\", ""); }
        }
        public override Rat? Exact { get { return Inner.Exact; } }
        public override bool DependsOn(string v) { return Inner.DependsOn(v); }
    }

    /// <summary>整数取模（有限域陷阱用）：a mod m。</summary>
    public sealed class XMod : XNode
    {
        public XNode A, M;
        public override string Latex { get { return A.Latex + "\\mathrm{mod}\\," + M.Latex; } }
        public override string Text { get { return "(" + A.Text + ") mod " + M.Text; } }
        public override Rat? Exact
        {
            get
            {
                Rat? a = A.Exact, m = M.Exact;
                if (!a.HasValue || !m.HasValue) return null;
                if (!a.Value.D.IsOne || !m.Value.D.IsOne || m.Value.N.IsZero) return null;
                BigInteger mm = m.Value.N;
                BigInteger r = BigInteger.ModPow(1, 1, mm);
                // A 可能是幂：用模幂算，避免大整数溢出
                XPow pw = A as XPow;
                if (pw != null)
                {
                    Rat? b = pw.B.Exact, e = pw.E.Exact;
                    if (b.HasValue && e.HasValue && b.Value.D.IsOne && e.Value.D.IsOne && e.Value.N.Sign >= 0)
                    {
                        r = BigInteger.ModPow(b.Value.N, e.Value.N, mm);
                        return new Rat(r, BigInteger.One);
                    }
                }
                if (a.Value.N.Sign < 0) return null;
                return new Rat(a.Value.N % mm, BigInteger.One);
            }
        }
        public override bool DependsOn(string v) { return A.DependsOn(v) || M.DependsOn(v); }
    }

    /// <summary>已经渲染好的片段（增量迁移用）。</summary>
    public sealed class XLeaf : XNode
    {
        public string L, T;
        public Rat? V;
        public bool Constant = true;
        public XLeaf(string latex, string text, Rat? value)
        {
            L = latex; T = text; V = value;
        }
        public override string Latex { get { return L; } }
        public override string Text { get { return T; } }
        public override Rat? Exact { get { return V; } }
        public override bool DependsOn(string v) { return !Constant; }
    }

    // ===================== 组合器 + 配平 =====================

    public static class X
    {
        public static XNode Num(long n) { return new XNum(n); }
        public static XNode Num(Rat r) { return new XNum(r); }
        public static XNode Dec(double v) { return XNum.Decimal(v); }
        public static XNode Var(string name) { return new XLeaf(name, name, null) { Constant = false }; }
        public static XNode Inf { get { return new XLeaf("\\infty", "∞", null); } }
        public static XNode Pi { get { return new XLeaf("\\pi", "π", null); } }
        public static XNode E { get { return new XLeaf("e", "e", null); } }

        public static XNode Frac(XNode a, XNode b)
        {
            XFrac f = new XFrac(); f.N = a; f.D = b; return f;
        }

        public static XNode Mul(params XNode[] items)
        {
            XMul m = new XMul();
            for (int i = 0; i < items.Length; i++) if (items[i] != null) m.F.Add(items[i]);
            if (m.F.Count == 1) return m.F[0];
            return m;
        }

        public static XNode Mul(List<XNode> items)
        {
            XMul m = new XMul();
            for (int i = 0; i < items.Count; i++) if (items[i] != null) m.F.Add(items[i]);
            if (m.F.Count == 1) return m.F[0];
            return m;
        }

        public static XNode Add(XNode a, XNode b) { XAdd n = new XAdd(); n.A = a; n.B = b; n.Sign = 1; return n; }
        public static XNode Sub(XNode a, XNode b) { XAdd n = new XAdd(); n.A = a; n.B = b; n.Sign = -1; return n; }
        public static XNode Pow(XNode a, XNode b) { XPow p = new XPow(); p.B = a; p.E = b; return p; }
        public static XNode Call(string fn, XNode a) { XCall c = new XCall(); c.Fn = fn; c.Arg = a; return c; }

        /// <summary>给子式加一对括号（渲染成 \left(...\right)），只用于消歧义，不改变值。</summary>
        public static XNode Paren(XNode a) { return Group("(", ")", a); }

        public static XNode TextParen(XNode a) { XTextParen p = new XTextParen(); p.Inner = a; return p; }

        public static XNode Deriv(string var, XNode body) { XDeriv d = new XDeriv(); d.Var = var; d.Body = body; return d; }

        public static XNode Group(string open, string close, XNode a) { XGroup g = new XGroup(); g.Open = open; g.Close = close; g.Inner = a; return g; }

        public static XNode Int(XNode lo, XNode hi, XNode body, string var)
        {
            XInt i = new XInt(); i.Lo = lo; i.Hi = hi; i.Body = body; i.Var = var; return i;
        }

        public static XNode Sum(XNode lo, XNode hi, XNode body, string var)
        {
            XSeries s = new XSeries(); s.Lo = lo; s.Hi = hi; s.Body = body; s.Var = var; return s;
        }

        public static XNode Prod(XNode lo, XNode hi, XNode body, string var)
        {
            XSeries s = new XSeries(); s.Lo = lo; s.Hi = hi; s.Body = body; s.Var = var; s.IsProd = true; return s;
        }

        public static XNode Lim(XNode to, XNode body, string var)
        {
            XLim l = new XLim(); l.To = to; l.Body = body; l.Var = var; return l;
        }

        /// <summary>把一个因子链配平成 AST，并检查精确值的乘积。</summary>
        public static bool TryBalance(List<XNode> factors, Rat target, out string error)
        {
            error = null;
            Rat acc = Rats.One;
            for (int i = 0; i < factors.Count; i++)
            {
                Rat? e = factors[i].Exact;
                if (!e.HasValue) continue;                 // 非常数因子不参与配平
                acc = Rats.Mul(acc, e.Value);
            }
            if (!Rats.Eq(acc, target))
            {
                error = "AST 因子精确值乘积 " + RandomExpr.FormatRat(acc) + " ≠ 目标 " + RandomExpr.FormatRat(target);
                return false;
            }
            return true;
        }
    }
}
