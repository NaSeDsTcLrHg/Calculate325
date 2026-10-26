/*
 * Calculate325 —— 独立后置校验（FormulaAudit）
 * ---------------------------------------------------------------------------
 * 目的：不再相信生成器自己记的账（`ClosedForm` 直接返回 target），而是把
 * **最终渲染出去的 LaTeX** 重新解析成 AST，再用与生成器完全无关的数值方法
 * 重新求值，最后和声称的值比较。生成器里任何一个系数、指数、分式、上下限
 * 写错，这里都会算出一个不等于目标的值。
 *
 * 独立性：
 *   - 输入只有 LaTeX 字符串；不读 Fac / ClosedForm / 任何中间状态。
 *   - 积分用 tanh-sinh（双重指数）求积；级数/连乘用部分和 + 1/N 外推；
 *     极限用序列外推；行列式用 Bareiss 无除法消元；φ/μ 用整数分解。
 *   - 少数经典恒等式用定理规则（见 TryOscillatory / TryCesaro / PrimeProduct），
 *     规则参数一律从式子里读出来，不读生成器变量，并在代码里注明数学依据。
 *   - 遇到不认识的构造直接抛 AuditException 判失败，绝不放行。
 *
 * 误差：每个节点带 (Complex 值, 绝对误差界)，逐层传播；最终
 * |算出来 − 声称| 必须 ≤ max(4×误差界, 1e-9×|声称|) 才算通过。
 */
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Text;

namespace Calculate325
{
    public sealed class FormulaAuditResult
    {
        public bool Ok;
        public double Value;
        public double RelError;
        public double ErrorBound;
        public int AtomCount;
        public string Mode;
        public string Detail;
    }

    // ===================== 带误差界的小复数 =====================

    internal struct V
    {
        public Complex C;
        public double E;

        public static V Real(double v) { V r; r.C = new Complex(v, 0.0); r.E = 0.0; return r; }
        public static V RealE(double v, double e) { V r; r.C = new Complex(v, 0.0); r.E = Math.Abs(e); return r; }
        public static V Inf() { V r; r.C = new Complex(double.PositiveInfinity, 0.0); r.E = double.PositiveInfinity; return r; }
        public bool IsInfinite { get { return double.IsInfinity(C.Real) || double.IsInfinity(C.Imaginary); } }
        public bool IsFinite { get { return !double.IsNaN(C.Real) && !double.IsNaN(C.Imaginary) && !IsInfinite; } }
        public double Mag { get { return C.Magnitude; } }
    }

    internal static class Vop
    {
        public static V Add(V a, V b) { V r; r.C = a.C + b.C; r.E = a.E + b.E; return r; }
        public static V Sub(V a, V b) { V r; r.C = a.C - b.C; r.E = a.E + b.E; return r; }
        public static V Neg(V a) { V r; r.C = -a.C; r.E = a.E; return r; }
        public static V Mul(V a, V b)
        {
            V r;
            r.C = a.C * b.C;
            r.E = a.C.Magnitude * b.E + b.C.Magnitude * a.E + a.E * b.E;
            if (double.IsNaN(r.E)) r.E = 1e300;
            return r;
        }
        public static V Div(V a, V b)
        {
            V r;
            double m = b.C.Magnitude;
            // 溢出保护：两边都超出 double 范围时（例如 k! 与 5^k 同时溢出），
            // 真实比值按 0 处理——这类项只出现在已经收敛到 0 的尾部。
            if (double.IsInfinity(m) && (!a.IsFinite)) { r.C = Complex.Zero; r.E = 0.0; return r; }
            r.C = a.C / b.C;
            r.E = (a.E + r.C.Magnitude * b.E) / Math.Max(m, 1e-300);
            if (double.IsNaN(r.E)) r.E = 1e300;
            return r;
        }
        public static V Pow(V a, V b)
        {
            V r;
            double m = a.C.Magnitude;
            r.C = Complex.Pow(a.C, b.C);
            double logm = m > 1e-300 ? Complex.Log(a.C).Magnitude : 0.0;
            r.E = r.C.Magnitude * (Math.Abs(b.C.Magnitude) * a.E / Math.Max(m, 1e-300) + logm * b.E);
            if (double.IsNaN(r.E) || double.IsInfinity(r.E)) r.E = r.C.Magnitude * 1e-9;
            return r;
        }
        public static V Func(V a, Func<Complex, Complex> f)
        {
            V r;
            r.C = f(a.C);
            if (a.E <= 0.0) { r.E = 0.0; return r; }
            double h = a.E;
            if (h < 1e-10) h = 1e-10;
            Complex d = (f(a.C + new Complex(h, 0.0)) - f(a.C - new Complex(h, 0.0))) / (2.0 * h);
            r.E = d.Magnitude * a.E;
            if (double.IsNaN(r.E) || double.IsInfinity(r.E)) r.E = r.C.Magnitude * 1e-9;
            return r;
        }
    }

    // ===================== AST =====================

    internal abstract class Node
    {
        public abstract V Eval(Env env);

        /// <summary>是否依赖某个变量（用于把极限里的常量因子提出来只算一次）。
        /// 默认保守返回 true；各节点按结构覆写，内部绑定同名变量时视为不依赖。</summary>
        public virtual bool DependsOn(string name) { return true; }

        public static bool AnyDepends(List<Node> list, string name)
        {
            for (int i = 0; i < list.Count; i++) if (list[i].DependsOn(name)) return true;
            return false;
        }

        /// <summary>把乘号树展平成因子列表（不改语义）。</summary>
        public static void FlattenMul(Node n, List<Node> outList)
        {
            NMul m = n as NMul;
            if (m == null) { outList.Add(n); return; }
            for (int i = 0; i < m.F.Count; i++) FlattenMul(m.F[i], outList);
        }

        public static Node Num(double v) { NNum n = new NNum(); n.Value = v; return n; }
        public static Node Sym(string name) { NSym s = new NSym(); s.Name = name; return s; }
        public static Node Add(Node a, Node b) { NAdd n = new NAdd(); n.L = a; n.R = b; n.Sign = 1; return n; }
        public static Node Sub(Node a, Node b) { NAdd n = new NAdd(); n.L = a; n.R = b; n.Sign = -1; return n; }
        public static Node Mul(params Node[] items)
        {
            List<Node> list = new List<Node>();
            for (int i = 0; i < items.Length; i++) if (items[i] != null) list.Add(items[i]);
            if (list.Count == 0) return Num(1.0);
            if (list.Count == 1) return list[0];
            NMul m = new NMul(); m.F = list; return m;
        }
        public static Node Div(Node a, Node b) { return Mul(a, Pow(b, Num(-1.0))); }
        public static Node Pow(Node a, Node b) { NPow p = new NPow(); p.Base = a; p.Exp = b; return p; }
    }

    internal sealed class Env
    {
        public Dictionary<string, double> Map = new Dictionary<string, double>(StringComparer.Ordinal);
        public int Depth;
        public AuditBudget Budget;

        public Env Bind(string name, double v)
        {
            Env e = new Env();
            e.Map = new Dictionary<string, double>(Map, StringComparer.Ordinal);
            e.Map[name] = v;
            e.Depth = Depth + 1;
            e.Budget = Budget;
            return e;
        }
    }

    internal sealed class AuditBudget
    {
        public long Ops;
        public long Limit = 150000000L;
        public bool Exceeded;
        public DateTime Deadline = DateTime.MaxValue;
        public DateTime LastClockCheck = DateTime.UtcNow;

        public void Tick()
        {
            Ops++;
            if (Ops > Limit) { Exceeded = true; return; }
            // 每 2^20 次求值看一次墙钟，给 UI 一个硬上限（超时算“未验证”，不放行）
            if ((Ops & 0xFFFFF) == 0)
            {
                DateTime now = DateTime.UtcNow;
                if ((now - LastClockCheck).TotalMilliseconds > 5.0)
                {
                    LastClockCheck = now;
                    if (now > Deadline) Exceeded = true;
                }
            }
        }
    }

    internal sealed class NNum : Node
    {
        public double Value;
        public override V Eval(Env env) { env.Budget.Tick(); return V.Real(Value); }
        public override bool DependsOn(string name) { return false; }
    }

    internal sealed class NInf : Node
    {
        public override V Eval(Env env) { return V.Inf(); }
        public override bool DependsOn(string name) { return false; }
    }

    internal sealed class NSym : Node
    {
        public string Name;
        public override V Eval(Env env)
        {
            env.Budget.Tick();
            // 先查绑定：求和指标 i 必须能遮蔽虚数单位 i（否则 3^{i} 会被当常数）
            double v;
            if (env.Map.TryGetValue(Name, out v)) return V.Real(v);
            if (Name == "pi") return V.Real(Math.PI);
            if (Name == "e") return V.Real(Math.E);
            if (Name == "i") { V r; r.C = Complex.ImaginaryOne; r.E = 0.0; return r; }
            throw new AuditException("未绑定变量 " + Name);
        }
        public override bool DependsOn(string name) { return Name == name; }
    }

    internal sealed class NAdd : Node
    {
        public Node L, R;
        public int Sign;
        public override V Eval(Env env)
        {
            V a = L.Eval(env);
            V b = R.Eval(env);
            return Sign < 0 ? Vop.Sub(a, b) : Vop.Add(a, b);
        }
        public override bool DependsOn(string name) { return L.DependsOn(name) || R.DependsOn(name); }
    }

    internal sealed class NMul : Node
    {
        public List<Node> F;
        public override V Eval(Env env)
        {
            V r = V.Real(1.0);
            for (int i = 0; i < F.Count; i++) r = Vop.Mul(r, F[i].Eval(env));
            return r;
        }
        public override bool DependsOn(string name) { return AnyDepends(F, name); }
    }

    internal sealed class NDiv : Node
    {
        public Node A, B;
        public override V Eval(Env env) { return Vop.Div(A.Eval(env), B.Eval(env)); }
        public override bool DependsOn(string name) { return A.DependsOn(name) || B.DependsOn(name); }
    }

    /// <summary>
    /// 导数算子 d/dx：用 5 点中心差分近似（步长按量级取）。
    /// ODE 陷阱靠它把"y 的导数"真的算出来，而不是把生成器写死的值搬过来。
    /// </summary>
    internal sealed class NDeriv : Node
    {
        public Node Body;
        public string Var = "x";

        public override V Eval(Env env)
        {
            double x0;
            if (!env.Map.TryGetValue(Var, out x0)) throw new AuditException("导数变量未绑定：" + Var);
            double h = 1e-4 * Math.Max(1.0, Math.Abs(x0));
            V m2 = Body.Eval(env.Bind(Var, x0 - 2.0 * h));
            V m1 = Body.Eval(env.Bind(Var, x0 - h));
            V p1 = Body.Eval(env.Bind(Var, x0 + h));
            V p2 = Body.Eval(env.Bind(Var, x0 + 2.0 * h));
            V r;
            r.C = (m2.C - 8.0 * m1.C + 8.0 * p1.C - p2.C) / (12.0 * h);
            r.E = (m2.E + 8.0 * m1.E + 8.0 * p1.E + p2.E) / (12.0 * h) + 1e-10 * (1.0 + r.C.Magnitude);
            return r;
        }

        public override bool DependsOn(string name)
        {
            // 导数在 x 处取值，当然依赖 x（这里曾经错写成 false，导致积分把它当常数）
            if (name == Var) return true;
            return Body.DependsOn(name);
        }
    }

    /// <summary>
    /// 整数取模（有限域陷阱用）。必须精确：a^b mod m 直接走 BigInteger.ModPow，
    /// 不能先算 a^b 再取模（double 在 1e19 以后就丢掉低位比特了，实测会把 1 算成 0）。
    /// </summary>
    internal sealed class NMod : Node
    {
        public Node A, B;

        public override V Eval(Env env)
        {
            V b = B.Eval(env);
            if (Math.Abs(b.C.Imaginary) > 1e-9) throw new AuditException("取模除数必须是整数");
            long m = ToLong(b);
            if (m == 0) throw new AuditException("取模除数为 0");

            NPow pw = A as NPow;
            if (pw != null)
            {
                V bv = pw.Base.Eval(env), ev = pw.Exp.Eval(env);
                if (Math.Abs(bv.C.Imaginary) < 1e-9 && Math.Abs(ev.C.Imaginary) < 1e-9 && ev.C.Real >= 0.0)
                {
                    BigInteger bn = ToBigInt(bv), en = ToBigInt(ev);
                    return V.RealE((double)BigInteger.ModPow(bn, en, m < 0 ? -m : m), 0.0);
                }
            }
            V a = A.Eval(env);
            if (Math.Abs(a.C.Imaginary) > 1e-9) throw new AuditException("取模被除数必须是整数");
            BigInteger x = ToBigInt(a);
            BigInteger mm = m < 0 ? -m : m;
            BigInteger r = x % mm;
            if (r.Sign < 0) r += mm;
            return V.RealE((double)r, 0.0);
        }

        static long ToLong(V v)
        {
            double x = Math.Round(v.C.Real);
            if (Math.Abs(v.C.Real - x) > 1e-6) throw new AuditException("取模除数不是整数");
            return (long)x;
        }

        static BigInteger ToBigInt(V v)
        {
            double x = Math.Round(v.C.Real);
            if (Math.Abs(v.C.Real - x) > 1e-6) throw new AuditException("取模操作数不是整数");
            if (Math.Abs(x) > 9.0e15) throw new AuditException("取模操作数超出精确整数范围");
            return new BigInteger(x);
        }

        public override bool DependsOn(string name) { return A.DependsOn(name) || B.DependsOn(name); }
    }

    internal sealed class NPow : Node
    {
        public Node Base, Exp;
        public override V Eval(Env env) { return Vop.Pow(Base.Eval(env), Exp.Eval(env)); }
        public override bool DependsOn(string name) { return Base.DependsOn(name) || Exp.DependsOn(name); }
    }

    internal sealed class NCall : Node
    {
        public string Fn;
        public Node Arg;
        public override V Eval(Env env)
        {
            V a = Arg.Eval(env);
            switch (Fn)
            {
                case "sin": return Vop.Func(a, Complex.Sin);
                case "cos": return Vop.Func(a, Complex.Cos);
                case "tan": return Vop.Func(a, Complex.Tan);
                case "ln": return Vop.Func(a, Complex.Log);
                case "exp": return Vop.Func(a, Complex.Exp);
                case "gamma":
                    if (Math.Abs(a.C.Imaginary) > 1e-9) throw new AuditException("Gamma 只支持实参数");
                    return V.RealE(FormulaAudit.GammaReal(a.C.Real), a.E);
                case "sqrt": return Vop.Func(a, Complex.Sqrt);
                case "arcsin": return Vop.Func(a, Complex.Asin);
                case "arccos": return Vop.Func(a, Complex.Acos);
                case "arctan": return Vop.Func(a, Complex.Atan);
                case "abs":
                    return V.RealE(Complex.Abs(a.C), a.E);
                case "floor":
                    if (Math.Abs(a.C.Imaginary) > 1e-12) throw new AuditException("floor 参数不是实数");
                    return V.Real(Math.Floor(a.C.Real));
                case "ceil":
                    if (Math.Abs(a.C.Imaginary) > 1e-12) throw new AuditException("ceil 参数不是实数");
                    return V.Real(Math.Ceiling(a.C.Real));
                case "frac":
                    if (Math.Abs(a.C.Imaginary) > 1e-12) throw new AuditException("小数部分参数不是实数");
                    return V.Real(a.C.Real - Math.Floor(a.C.Real));
                case "fact":
                    {
                        if (Math.Abs(a.C.Imaginary) > 1e-12) throw new AuditException("阶乘参数不是实数");
                        double x = a.C.Real;
                        if (x < 0.0) throw new AuditException("阶乘参数为负");
                        int n = (int)Math.Round(x);
                        if (Math.Abs(x - n) > 1e-9) throw new AuditException("阶乘参数不是整数");
                        if (n > 170) return V.Real(double.PositiveInfinity);
                        double r = 1.0;
                        for (int q = 2; q <= n; q++) r *= q;
                        return V.Real(r);
                    }
            }
            throw new AuditException("未知函数 " + Fn);
        }
        public override bool DependsOn(string name) { return Arg.DependsOn(name); }
    }

    internal sealed class NZeta : Node
    {
        public Node S;
        public override V Eval(Env env)
        {
            V s = S.Eval(env);
            double sigma = s.C.Real;
            if (sigma <= 1.0 || Math.Abs(s.C.Imaginary) > 1e-12) throw new AuditException("ζ(s) 需要实数 s>1");
            return V.RealE(Zeta(sigma), 1e-12);
        }
        public override bool DependsOn(string name) { return S.DependsOn(name); }

        /// <summary>直接级数到 N，再对 (N,2N,4N,8N) 做 1/N 外推。</summary>
        internal static double Zeta(double s)
        {
            int n = 2048;
            double[] part = new double[4];
            int idx = 0;
            double acc = 0.0;
            for (int k = 1; k <= n * 8; k++)
            {
                acc += Math.Pow(k, -s);
                if (k == n || k == 2 * n || k == 4 * n || k == 8 * n) { part[idx++] = acc; if (idx >= 4) break; }
            }
            double[] x = new double[] { 1.0 / n, 1.0 / (2.0 * n), 1.0 / (4.0 * n), 1.0 / (8.0 * n) };
            return Interp.ExtrapolateReal(x, part);
        }
    }

    internal sealed class NMu : Node
    {
        public Node A;
        public override V Eval(Env env)
        {
            V a = A.Eval(env);
            long n = (long)Math.Round(a.C.Real);
            if (Math.Abs(a.C.Real - n) > 1e-9 || n < 1) throw new AuditException("μ(n) 需要正整数");
            return V.Real(Mobius(n));
        }
        public override bool DependsOn(string name) { return A.DependsOn(name); }
        internal static int Mobius(long n)
        {
            int primes = 0;
            long m = n;
            for (long p = 2; p * p <= m; p++)
            {
                if (m % p != 0) continue;
                m /= p;
                if (m % p == 0) return 0;
                primes++;
            }
            if (m > 1) primes++;
            return (primes & 1) == 0 ? 1 : -1;
        }
    }

    internal sealed class NPhi : Node
    {
        public Node A;
        public override V Eval(Env env)
        {
            V a = A.Eval(env);
            long n = (long)Math.Round(a.C.Real);
            if (Math.Abs(a.C.Real - n) > 1e-9 || n < 1) throw new AuditException("φ(n) 需要正整数");
            return V.Real(Totient(n));
        }
        public override bool DependsOn(string name) { return A.DependsOn(name); }
        internal static long Totient(long n)
        {
            long r = n, m = n;
            for (long p = 2; p * p <= m; p++)
            {
                if (m % p != 0) continue;
                while (m % p == 0) m /= p;
                r -= r / p;
            }
            if (m > 1) r -= r / m;
            return r;
        }
    }

    // ===================== 积分 =====================

    internal sealed class NInt : Node
    {
        public Node Lo, Hi, Body;
        public string Var;
        public bool Contour;

        public override V Eval(Env env)
        {
            env.Budget.Tick();
            if (Contour) return Residue(env);

            V classic;
            if (Hi != null && Hi.Eval(env).IsInfinite && TryOscillatory(env, out classic)) return classic;

            double a = Lo == null ? 0.0 : RealOf(Lo.Eval(env));
            V hiV = Hi.Eval(env);
            bool inf = hiV.IsInfinite;
            double b = inf ? double.PositiveInfinity : RealOf(hiV);
            // 保留方向：b < a 时交换端点算完再取负，和 Python 侧的有向积分一致
            double dir = 1.0;
            if (!inf && b < a) { double t = a; a = b; b = t; dir = -1.0; }

            V mono;
            if (TryMonomial(env, a, b, out mono)) { V mr = mono; mr.C *= dir; return mr; }

            string var = Var;
            Node body = Body;
            Func<double, V> f = delegate(double t)
            {
                Env e2 = env.Bind(var, t);
                return body.Eval(e2);
            };
            // 嵌套越深，每层允许的 tanh-sinh 折半次数越少：三重积分不会被罚到爆预算
            int levels = env.Depth <= 0 ? 6 : (env.Depth == 1 ? 4 : 3);
            V q = Quad.Integrate(f, a, b, env.Budget, levels);
            if (dir < 0) q.C = -q.C;
            return q;
        }
        public override bool DependsOn(string name)
        {
            if (Var == name) return false;
            if (Lo != null && Lo.DependsOn(name)) return true;
            if (Hi != null && Hi.DependsOn(name)) return true;
            return Body.DependsOn(name);
        }

        /// <summary>
        /// 幂律积分的解析值（∫_0^1 x^p dx = 1/(p+1)，∫_0^b x^p dx = b^{p+1}/(p+1)）。
        /// 只是“把式子里的 p 读出来套微积分基本定理”，不依赖生成器任何变量。
        /// 这条规则同时解决 x^{−4/5}、x^{−12/13} 这类端点奇点让 tanh-sinh 掉精度的问题。
        /// </summary>
        bool TryMonomial(Env env, double a, double b, out V result)
        {
            result = V.Real(0.0);
            if (double.IsInfinity(b) || Math.Abs(a) > 1e-12) return false;
            double coef = 1.0;
            Node pow = Body;
            NMul m = Body as NMul;
            if (m != null)
            {
                pow = null;
                for (int i = 0; i < m.F.Count; i++)
                {
                    NPow pw = m.F[i] as NPow;
                    NSym bs = pw == null ? null : pw.Base as NSym;
                    if (bs != null && bs.Name == Var) { if (pow != null) return false; pow = pw; continue; }
                    if (m.F[i].DependsOn(Var)) return false;
                    V cv = m.F[i].Eval(env);
                    if (!cv.IsFinite) return false;
                    coef *= cv.C.Real;
                }
                if (pow == null) return false;
            }
            NPow p0 = pow as NPow;
            if (p0 == null)
            {
                // 裸变量就是 x^1：∫_0^b x dx = b²/2
                NSym only = pow as NSym;
                if (only == null || only.Name != Var) return false;
                double half = coef * b * b / 2.0;
                result = V.RealE(half, 1e-15 * (1.0 + Math.Abs(half)));
                return true;
            }
            NSym baseSym = p0.Base as NSym;
            if (baseSym == null || baseSym.Name != Var) return false;
            if (p0.Exp.DependsOn(Var)) return false;
            V pv = p0.Exp.Eval(env);
            if (!pv.IsFinite || Math.Abs(pv.C.Imaginary) > 1e-12) return false;
            double p = pv.C.Real;
            if (!(p > -1.0)) return false;                       // 收敛条件
            if (Math.Abs(b) > 1.0 && p > 1e6) return false;
            double val = coef * Math.Pow(b, p + 1.0) / (p + 1.0);
            result = V.RealE(val, 1e-14 * (1.0 + Math.Abs(val)));
            return true;
        }

        static double RealOf(V v)
        {
            if (Math.Abs(v.C.Imaginary) > 1e-9 * (1.0 + Math.Abs(v.C.Real)))
                throw new AuditException("积分限不是实数");
            return v.C.Real;
        }

        /// <summary>
        /// 收敛较慢 / 振荡的经典积分用定理规则（参数一律从式子里读）：
        ///   Dirichlet:  ∫_0^∞ sin(αx)/x dx = π/2·sign(α)
        ///   Frullani:   ∫_0^∞ (cos(αx)−cos(βx))/x dx = ln(β/α)
        ///   sinc²:      ∫_0^∞ sin²(αx)/x² dx = π|α|/2
        /// 第三条是必需的：tanh-sinh 在 x 很大处节点间距会超过振荡周期，
        /// 会稳定地“收敛”到一个偏小的错值（实测 1.6e-5 相对误差），必须解析处理。
        /// </summary>
        bool TryOscillatory(Env env, out V result)
        {
            result = V.Real(0.0);
            if (Lo == null) return false;
            V loV = Lo.Eval(env);
            if (Math.Abs(loV.C.Real) > 1e-12) return false;

            // 手工拆分子/分母：先展平乘号树，保留分母的指数（x 还是 x²），常数因子提出来
            List<Node> leaves = new List<Node>();
            Node.FlattenMul(Body, leaves);
            List<Node> num = new List<Node>();
            int denPower = 0;
            double coef = 1.0;
            for (int i = 0; i < leaves.Count; i++)
            {
                Node f = leaves[i];
                NPow pw = f as NPow;
                if (pw != null && !pw.Exp.DependsOn(Var))
                {
                    V ev = pw.Exp.Eval(env);
                    if (ev.IsFinite && Math.Abs(ev.C.Imaginary) < 1e-12 && ev.C.Real < 0.0)
                    {
                        int dp = DenPowerOf(pw.Base, Var, env);
                        if (dp > 0)
                        {
                            denPower += (int)Math.Round(dp * -ev.C.Real);
                            continue;
                        }
                    }
                }
                NSym only = f as NSym;
                if (only != null && only.Name == Var) { denPower += 1; continue; }
                if (!f.DependsOn(Var))
                {
                    V cv = f.Eval(env);
                    if (!cv.IsFinite || Math.Abs(cv.C.Imaginary) > 1e-12) return false;
                    coef *= cv.C.Real;
                    continue;
                }
                num.Add(f);
            }
            if (num.Count != 1) return false;
            Node top = num[0];

            // sin(αx) / x
            if (denPower == 1)
            {
                NCall call = top as NCall;
                if (call != null && call.Fn == "sin")
                {
                    double alpha;
                    if (!TryLinearIn(call.Arg, Var, env, out alpha)) return false;
                    result = V.RealE(coef * Math.PI / 2.0 * Math.Sign(alpha), 1e-12 * (1.0 + Math.Abs(coef)));
                    return true;
                }
                NAdd diff = top as NAdd;
                if (diff != null && diff.Sign < 0)
                {
                    NCall ca = diff.L as NCall, cb = diff.R as NCall;
                    if (ca == null || cb == null || ca.Fn != "cos" || cb.Fn != "cos") return false;
                    double aa, bb;
                    if (!TryLinearIn(ca.Arg, Var, env, out aa)) return false;
                    if (!TryLinearIn(cb.Arg, Var, env, out bb)) return false;
                    if (aa <= 0.0 || bb <= 0.0) return false;
                    result = V.RealE(coef * Math.Log(bb / aa), 1e-12 * (1.0 + Math.Abs(coef)));
                    return true;
                }
            }
            // sin²(αx) / x²
            if (denPower == 2)
            {
                NPow sq = top as NPow;
                if (sq == null) return false;
                NNum e2 = sq.Exp as NNum;
                if (e2 == null || Math.Abs(e2.Value - 2.0) > 1e-12) return false;
                NCall sn = sq.Base as NCall;
                if (sn == null || sn.Fn != "sin") return false;
                double alpha;
                if (!TryLinearIn(sn.Arg, Var, env, out alpha)) return false;
                result = V.RealE(coef * Math.PI * Math.Abs(alpha) / 2.0, 1e-12 * (1.0 + Math.Abs(coef)));
                return true;
            }
            return false;
        }

        /// <summary>n 是不是 v^p（p 为正常数）？是就返回 p，用于识别 x、x² 型分母。</summary>
        static int DenPowerOf(Node n, string var, Env env)
        {
            NSym s = n as NSym;
            if (s != null) return s.Name == var ? 1 : 0;
            NPow p = n as NPow;
            if (p == null) return 0;
            if (p.Exp.DependsOn(var)) return 0;
            V ev = p.Exp.Eval(env);
            if (!ev.IsFinite || Math.Abs(ev.C.Imaginary) > 1e-12 || ev.C.Real <= 0.0) return 0;
            NSym b = p.Base as NSym;
            if (b == null || b.Name != var) return 0;
            int q = (int)Math.Round(ev.C.Real);
            return q;
        }

        static bool TryLinearIn(Node arg, string var, Env env, out double slope)
        {
            slope = 0.0;
            V at0 = arg.Eval(env.Bind(var, 0.0));
            if (Math.Abs(at0.C.Real) > 1e-12) return false;
            V at1 = arg.Eval(env.Bind(var, 1.0));
            if (Math.Abs(at1.C.Imaginary) > 1e-12) return false;
            slope = at1.C.Real;
            return Math.Abs(slope) > 1e-12;
        }

        V Residue(Env env)
        {
            List<Node> num = new List<Node>();
            List<Node> den = new List<Node>();
            SplitFrac(Body, num, den);
            if (den.Count != 1) throw new AuditException("留数只支持单极点");
            Node dz = den[0];
            double d0 = RealValue(dz, env.Bind(Var, 0.0));
            double d1 = RealValue(dz, env.Bind(Var, 1.0));
            double slope = d1 - d0;
            if (Math.Abs(slope) < 1e-12) throw new AuditException("留数极点无法解出");
            double pole = -d0 / slope;
            double numAtPole = 1.0;
            for (int i = 0; i < num.Count; i++) numAtPole *= RealValue(num[i], env.Bind(Var, pole));
            double res = numAtPole / slope;
            // ∮_C f dz = 2πi·ΣRes（C 内部只有这一个单极点）
            V outv;
            outv.C = new Complex(0.0, 2.0 * Math.PI * res);
            outv.E = 1e-11 * (1.0 + Math.Abs(res));
            return outv;
        }

        static double RealValue(Node n, Env env)
        {
            V v = n.Eval(env);
            if (Math.Abs(v.C.Imaginary) > 1e-9) throw new AuditException("留数分母不是实值");
            return v.C.Real;
        }

        internal static void SplitFrac(Node n, List<Node> num, List<Node> den)
        {
            NMul m = n as NMul;
            if (m == null) { num.Add(n); return; }
            for (int i = 0; i < m.F.Count; i++)
            {
                NPow p = m.F[i] as NPow;
                if (p != null)
                {
                    NNum e = p.Exp as NNum;
                    if (e != null && e.Value < 0.0) { den.Add(p.Base); continue; }
                }
                num.Add(m.F[i]);
            }
        }
    }

    // ===================== 级数 / 连乘 =====================

    internal sealed class NSeries : Node
    {
        public Node Lo, Hi, Body;
        public string Var;
        public bool IsProd;
        public bool OverPrimes;

        static readonly int[] Cuts = new int[] { 32, 64, 128, 256, 512, 1024, 2048, 4096, 8192 };
        // 嵌套（双重求和）时内层只到 512：外层每个 m 都要付一次内层代价，
        // 512 + 5 阶 1/N 外推足够到 1e-11，再多就会把预算耗光（实测 2048 会超时）。
        static readonly int[] CutsShallow = new int[] { 32, 64, 128, 256, 512 };

        public override bool DependsOn(string name)
        {
            if (Var == name) return false;
            if (Lo != null && Lo.DependsOn(name)) return true;
            if (Hi != null && Hi.DependsOn(name)) return true;
            return Body.DependsOn(name);
        }

        public override V Eval(Env env)
        {
            env.Budget.Tick();
            if (OverPrimes) return PrimeProduct(env);

            // 和式里与求和变量无关的因子提出来只算一次：
            // Σ_k c·g(k) = c·Σ_k g(k)。除了省时间，也避免内层重积分在每个 k 上重复求积。
            if (!IsProd && Body is NMul && Body.DependsOn(Var))
            {
                NMul bm = (NMul)Body;
                List<Node> dep = new List<Node>();
                List<Node> indep = new List<Node>();
                for (int i = 0; i < bm.F.Count; i++)
                {
                    if (bm.F[i].DependsOn(Var)) dep.Add(bm.F[i]); else indep.Add(bm.F[i]);
                }
                if (dep.Count > 0 && indep.Count > 0)
                {
                    V cst = Node.Mul(indep.ToArray()).Eval(env);
                    V inner = SumOnly(Node.Mul(dep.ToArray()), env);
                    return Vop.Mul(cst, inner);
                }
            }
            return SumOnly(Body, env);
        }

        /// <summary>
        /// 定理规则：Σ_{n=1}^∞ μ(n)/n^s = 1/ζ(s)（s&gt;1，Euler 乘积的逆）。
        /// 这一项不能靠部分和 + 1/N 外推：Mertens 函数不规则，外推只能到 1e-7 量级。
        /// s 由式子里 n^{-s} 那个因子在 n=2、n=3 两点的值反解出来，再交叉校验一致。
        /// </summary>
        bool TryMobiusDirichlet(Node body, Env env, out V result)
        {
            result = V.Real(0.0);
            NMul m = body as NMul;
            if (m == null || m.F.Count != 2) return false;
            NMu mu = null;
            Node rest = null;
            for (int i = 0; i < 2; i++)
            {
                NMu cand = m.F[i] as NMu;
                if (cand != null) mu = cand; else rest = m.F[i];
            }
            if (mu == null || rest == null) return false;
            NSym arg = mu.A as NSym;
            if (arg == null || arg.Name != Var) return false;
            if (!rest.DependsOn(Var)) return false;
            double l2 = Math.Log(Math.Abs(rest.Eval(env.Bind(Var, 2.0)).C.Real));
            double l3 = Math.Log(Math.Abs(rest.Eval(env.Bind(Var, 3.0)).C.Real));
            double s2 = -l2 / Math.Log(2.0);
            double s3 = -l3 / Math.Log(3.0);
            if (Math.Abs(s2 - s3) > 1e-9) return false;
            if (!(s2 > 1.0)) return false;
            double v = 1.0 / NZeta.Zeta(s2);
            result = V.RealE(v, 1e-11 * (1.0 + Math.Abs(v)));
            return true;
        }

        V SumOnly(Node body, Env env)
        {
            V mob;
            if (!IsProd && TryMobiusDirichlet(body, env, out mob)) return mob;
            int[] cuts = env.Depth >= 1 ? CutsShallow : Cuts;

            long lo = Lo == null ? 1L : (long)Math.Round(Lo.Eval(env).C.Real);
            V hiV = Hi != null ? Hi.Eval(env) : V.Inf();
            bool toInf = hiV.IsInfinite;

            if (!toInf)
            {
                long hi = (long)Math.Round(hiV.C.Real);
                Complex acc0 = IsProd ? Complex.One : Complex.Zero;
                for (long k = lo; k <= hi; k++)
                {
                    env.Budget.Tick();
                    if (env.Budget.Exceeded) break;
                    V t = body.Eval(env.Bind(Var, k));
                    if (IsProd) acc0 *= t.C; else acc0 += t.C;
                }
                V r0; r0.C = acc0; r0.E = 1e-13 * (1.0 + acc0.Magnitude); return r0;
            }

            long opsAtStart = env.Budget.Ops;
            Complex acc = IsProd ? Complex.One : Complex.Zero;
            double[] xs = new double[cuts.Length];
            Complex[] ys = new Complex[cuts.Length];
            double lastTerm = 0.0;
            int got = 0;
            long kk = lo;
            for (int ci = 0; ci < cuts.Length; ci++)
            {
                long stop = lo + cuts[ci];
                while (kk < stop)
                {
                    env.Budget.Tick();
                    if (env.Budget.Exceeded) break;
                    V t = body.Eval(env.Bind(Var, kk));
                    lastTerm = t.C.Magnitude;
                    if (IsProd) acc *= t.C; else acc += t.C;
                    kk++;
                }
                xs[ci] = 1.0 / (double)cuts[ci];
                ys[ci] = IsProd ? Complex.Log(acc) : acc;
                got = ci + 1;
                if (env.Budget.Exceeded) break;
                if (lastTerm <= 1e-17 * (1.0 + acc.Magnitude)) break;
                // 自适应截断：外层每多一档都要重算一遍内层，双层求和会爆预算。
                // 至少拿到 5 个截断点（外推要 5 个），之后花超过 60 万次求值就停。
                if (got >= 5 && env.Budget.Ops - opsAtStart > 600000) break;
            }

            Complex last = ys[got - 1];
            if (lastTerm <= 1e-17 * (1.0 + acc.Magnitude) || got < 3)
            {
                V r; r.C = IsProd ? Complex.Exp(last) : last;
                r.E = 1e-14 * (1.0 + r.C.Magnitude);
                if (env.Budget.Exceeded) r.E = Math.Max(r.E, 1e-3 * (1.0 + r.C.Magnitude));
                return r;
            }

            // 1/N 外推：对 n^{-p} 型尾部（p>0）都成立
            int use = Math.Min(6, got);
            double[] xs2 = new double[use];
            Complex[] ys2 = new Complex[use];
            for (int i = 0; i < use; i++)
            {
                xs2[i] = xs[got - use + i];
                ys2[i] = ys[got - use + i];
            }
            Complex ext = Interp.ExtrapolateComplex(xs2, ys2, 0.0, use);
            Complex extPrev = Interp.ExtrapolateComplex(xs2, ys2, 0.0, use - 1);
            double d = (ext - extPrev).Magnitude;
            V res;
            res.C = IsProd ? Complex.Exp(ext) : ext;
            res.E = 4.0 * d + 1e-13 * (1.0 + res.C.Magnitude);
            if (env.Budget.Exceeded) res.E = Math.Max(res.E, 1e-3 * (1.0 + res.C.Magnitude));
            return res;
        }

        /// <summary>
        /// ∏_p f(p)：Euler 素数乘积。数值到 P，尾部按 Σ_{p>P}1/p² ≈ 1/(P ln P) 估界。
        /// </summary>
        V PrimeProduct(Env env)
        {
            const int P = 300000;
            bool[] comp = new bool[P + 1];
            Complex acc = Complex.One;
            for (int i = 2; i <= P; i++)
            {
                if (comp[i]) continue;
                env.Budget.Tick();
                V t = Body.Eval(env.Bind(Var, i));
                acc *= t.C;
                if ((long)i * i <= P)
                    for (long j = (long)i * i; j <= P; j += i) comp[j] = true;
            }
            // 尾部修正：p>P 的 log-乘积 ≈ Σ_{p>P} 1/p² ≈ ∫_P^∞ dt/(t² ln t)
            //            ≈ 1/(P ln P) − 1/(P ln²P)
            // 不修正的话结果会系统性偏低 ~2.4e-7（实测），修正后 ~2e-8。
            double pLog = P * Math.Log(P);
            double tailLog = 1.0 / pLog - 1.0 / (pLog * Math.Log(P));
            V r;
            r.C = acc * Math.Exp(tailLog);
            r.E = Math.Abs(r.C.Magnitude) * (1.0 / (pLog * Math.Log(P)) * 2.0) + 1e-12;
            return r;
        }
    }

    // ===================== 极限 =====================

    internal sealed class NLim : Node
    {
        public Node Body;
        public string Var;
        public bool ToInf;
        public double Target;

        public override bool DependsOn(string name)
        {
            if (Var == name) return false;
            return Body.DependsOn(name);
        }

        public override V Eval(Env env)
        {
            env.Budget.Tick();
            // 与极限变量无关的因子只算一次：既省时间，也避免重复求积分的数值噪声
            if (!Body.DependsOn(Var)) return Body.Eval(env);
            // 等分布 Cesàro 型极限先走定理（结构严格匹配，见 TryCesaro），
            // 因为它的收敛是 O(log N/N)，数值外推永远只有 1e-4 量级。
            V ces;
            if (ToInf && TryCesaro(Body, env, out ces)) return ces;
            NMul m = Body as NMul;
            if (m != null)
            {
                List<Node> dep = new List<Node>();
                List<Node> indep = new List<Node>();
                for (int i = 0; i < m.F.Count; i++)
                {
                    if (m.F[i].DependsOn(Var)) dep.Add(m.F[i]); else indep.Add(m.F[i]);
                }
                if (dep.Count > 0 && indep.Count > 0)
                {
                    Node depNode = Node.Mul(dep.ToArray());
                    if (TryCesaro(depNode, env, out ces))    // lim (c/N·Σ) 的 c 可能被拆成独立因子
                    {
                        V cst0 = Node.Mul(indep.ToArray()).Eval(env);
                        return Vop.Mul(cst0, ces);
                    }
                    V cst = Node.Mul(indep.ToArray()).Eval(env);
                    V lim = LimitOf(depNode, env);
                    return Vop.Mul(cst, lim);
                }
            }
            return LimitOf(Body, env);
        }

        V LimitOf(Node body, Env env)
        {
            int n = 6;
            double[] xs = new double[n];
            Complex[] ys = new Complex[n];
            // 基点要够大：e^{a/n} 的 1/n 级数只在 |a/n|<1 才收敛，a 最大约 90
            // 所以从 n=1024 起采样到 n=32768（6 个点），再外推到 1/n→0。
            double baseN = ToInf ? 1024.0 : 1.0 / 512.0;
            for (int i = 0; i < n; i++)
            {
                double t = ToInf ? baseN * Math.Pow(2.0, i) : baseN * Math.Pow(0.5, i);
                xs[i] = ToInf ? 1.0 / t : t;
                V v = body.Eval(env.Bind(Var, t));
                if (!v.IsFinite) throw new AuditException("极限采样出现非有限值（" + Var + "=" + t.ToString("0.###e+0", CultureInfo.InvariantCulture) + "）");
                ys[i] = v.C;
            }
            Complex e5 = Interp.ExtrapolateComplex(xs, ys, 0.0, 5);
            Complex e4 = Interp.ExtrapolateComplex(xs, ys, 0.0, 4);
            Complex e3 = Interp.ExtrapolateComplex(xs, ys, 0.0, 3);
            double spread = (e5 - e4).Magnitude + (e4 - e3).Magnitude;
            double scale = 1.0 + e5.Magnitude;

            if (spread > 1e-5 * scale)
            {
                V ces;
                if (TryCesaro(body, env, out ces)) return ces;
                if (spread > 1e-2 * scale)
                    throw new AuditException("极限外推不收敛（spread=" + spread.ToString("0.###e+0", CultureInfo.InvariantCulture) + "）");
            }
            V res;
            res.C = e5;
            res.E = Math.Max(4.0 * spread, 1e-13 * scale);
            if (env.Budget.Exceeded) res.E = Math.Max(res.E, 1e-3 * scale);
            return res;
        }

        /// <summary>
        /// 定理规则：lim_{N→∞} c/N · Σ_{k=1}^{N} {kα}，α 为无理数。
        /// {kα} 在 [0,1) 上等分布（Weyl 判据），故 Cesàro 平均 → 1/2，整体 → c/2。
        /// c 由把极限变量取 1 时其余因子算出来；Σ 的花括号结构必须成立。
        /// </summary>
        bool TryCesaro(Node body, Env env, out V result)
        {
            result = V.Real(0.0);
            NMul m = body as NMul;
            if (m == null) return false;
            NSeries series = null;
            List<Node> rest = new List<Node>();
            for (int i = 0; i < m.F.Count; i++)
            {
                NSeries s = m.F[i] as NSeries;
                if (s != null) { if (series != null) return false; series = s; continue; }
                rest.Add(m.F[i]);
            }
            if (series == null) return false;
            if (series.Lo != null && Math.Abs(series.Lo.Eval(env).C.Real - 1.0) > 1e-9) return false;
            NCall frac = series.Body as NCall;
            if (frac == null || frac.Fn != "frac") return false;
            // {k·√p}：参数里要有 Σ 变量 k 和 √p
            NMul inner = frac.Arg as NMul;
            bool hasK = false, hasSqrt = false;
            if (inner != null)
            {
                for (int i = 0; i < inner.F.Count; i++)
                {
                    NSym sym = inner.F[i] as NSym;
                    if (sym != null && sym.Name == series.Var) hasK = true;
                    NPow pw = inner.F[i] as NPow;
                    if (pw != null)
                    {
                        NNum ex = pw.Exp as NNum;
                        NSym bs = pw.Base as NSym;
                        if (bs == null)
                        {
                            NNum bn = pw.Base as NNum;
                            if (bn != null && ex != null && Math.Abs(ex.Value - 0.5) < 1e-12 && bn.Value >= 2.0) hasSqrt = true;
                        }
                    }
                    NCall sq = inner.F[i] as NCall;
                    if (sq != null && sq.Fn == "sqrt") hasSqrt = true;
                }
            }
            if (!hasK || !hasSqrt) return false;
            // Σ 上限必须正好是极限变量 N（否则 Cesàro 论证不成立）
            NSym hiSym = series.Hi as NSym;
            if (hiSym == null || hiSym.Name != Var) return false;

            // 其余因子必须正好是 c·N^{-1}：c 为常数（严格，否则 c/2 的结论不成立）
            double c = 1.0;
            bool sawInvN = false;
            for (int i = 0; i < rest.Count; i++)
            {
                List<Node> leaves = new List<Node>();
                Node.FlattenMul(rest[i], leaves);
                for (int j = 0; j < leaves.Count; j++)
                {
                    Node f = leaves[j];
                    NPow pw = f as NPow;
                    NNum ne = pw == null ? null : pw.Exp as NNum;
                    NSym bs = pw == null ? null : pw.Base as NSym;
                    if (bs != null && bs.Name == Var && ne != null && Math.Abs(ne.Value + 1.0) < 1e-12)
                    {
                        sawInvN = true;
                        continue;
                    }
                    if (f.DependsOn(Var)) return false;
                    V cv = f.Eval(env);
                    if (!cv.IsFinite || Math.Abs(cv.C.Imaginary) > 1e-12) return false;
                    c *= cv.C.Real;
                }
            }
            if (!sawInvN) return false;
            result = V.RealE(c * 0.5, 1e-12 * (1.0 + Math.Abs(c)));
            return true;
        }
    }

    // ===================== 行列式 =====================

    /// <summary>矩阵乘积的行列式：det(A·B·…) = det(A)·det(B)·…（逐项用 NDet 精确算）。</summary>
    internal sealed class NDetProd : Node
    {
        public List<Node> Mats = new List<Node>();

        public override bool DependsOn(string name)
        {
            for (int k = 0; k < Mats.Count; k++) if (Mats[k].DependsOn(name)) return true;
            return false;
        }

        public override V Eval(Env env)
        {
            env.Budget.Tick();
            Complex p = Complex.One;
            double e = 0.0;
            for (int k = 0; k < Mats.Count; k++)
            {
                V v = Mats[k].Eval(env);
                p *= v.C;
                e += v.E;
            }
            V r;
            r.C = p;
            r.E = e + 1e-15 * (1.0 + p.Magnitude);
            return r;
        }
    }

    internal sealed class NDet : Node
    {
        public List<Node[]> Rows;

        public override V Eval(Env env)
        {
            env.Budget.Tick();
            int n = Rows.Count;
            for (int i = 1; i < n; i++) if (Rows[i].Length != n) throw new AuditException("矩阵不是方阵");
            if (n != Rows[0].Length) throw new AuditException("矩阵不是方阵");

            // 全是整数常量 → 走 Bareiss 精确整数消元（旧的矩阵因子都走这条）
            bool allConst = true;
            for (int i = 0; i < n && allConst; i++)
                for (int j = 0; j < n; j++)
                {
                    NNum num = Rows[i][j] as NNum;
                    if (num == null || Math.Abs(num.Value - Math.Round(num.Value)) > 1e-9) { allConst = false; break; }
                }
            if (allConst)
            {
                List<double[]> raw = new List<double[]>();
                for (int i = 0; i < n; i++)
                {
                    double[] row = new double[n];
                    for (int j = 0; j < n; j++) row[j] = Math.Round(((NNum)Rows[i][j]).Value);
                    raw.Add(row);
                }
                return V.RealE(DeterminantLong(raw), 0.0);
            }

            // 含表达式元素（"最外层行列式"外壳）：逐元求值 + 部分主元高斯消元
            Complex[,] a = new Complex[n, n];
            double err = 0.0;
            for (int i = 0; i < n; i++)
                for (int j = 0; j < n; j++)
                {
                    V v = Rows[i][j].Eval(env);
                    a[i, j] = v.C;
                    err += v.E;
                }
            Complex det = Complex.One;
            double scale = 1.0;
            for (int k = 0; k < n; k++)
            {
                int piv = k;
                for (int i = k + 1; i < n; i++) if (a[i, k].Magnitude > a[piv, k].Magnitude) piv = i;
                if (a[piv, k].Magnitude < 1e-300) return V.RealE(0.0, err);
                if (piv != k)
                {
                    for (int j = 0; j < n; j++) { Complex tmp = a[k, j]; a[k, j] = a[piv, j]; a[piv, j] = tmp; }
                    det = -det;
                }
                det *= a[k, k];
                scale *= (1.0 + a[k, k].Magnitude);
                for (int i = k + 1; i < n; i++)
                {
                    Complex f = a[i, k] / a[k, k];
                    for (int j = k; j < n; j++) a[i, j] -= f * a[k, j];
                }
            }
            V res;
            res.C = det;
            res.E = err * scale + 1e-13 * det.Magnitude;
            return res;
        }

        public override bool DependsOn(string name)
        {
            for (int i = 0; i < Rows.Count; i++)
                for (int j = 0; j < Rows[i].Length; j++)
                    if (Rows[i][j].DependsOn(name)) return true;
            return false;
        }

        /// <summary>Bareiss 无除法消元（整数精确），与生成器的展开式算法无关。</summary>
        internal static long DeterminantLong(List<double[]> rows)
        {
            int n = rows.Count;
            if (n == 0) return 0;
            long[][] a = new long[n][];
            for (int i = 0; i < n; i++)
            {
                a[i] = new long[n];
                for (int j = 0; j < n; j++)
                {
                    double v = rows[i][j];
                    double r = Math.Round(v);
                    if (Math.Abs(v - r) > 1e-9) throw new AuditException("行列式元素不是整数");
                    a[i][j] = (long)r;
                }
            }
            long sign = 1, prev = 1;
            for (int k = 0; k + 1 < n; k++)
            {
                if (a[k][k] == 0)
                {
                    int swap = -1;
                    for (int i = k + 1; i < n; i++) if (a[i][k] != 0) { swap = i; break; }
                    if (swap < 0) return 0;
                    long[] t = a[k]; a[k] = a[swap]; a[swap] = t;
                    sign = -sign;
                }
                for (int i = k + 1; i < n; i++)
                    for (int j = k + 1; j < n; j++)
                        a[i][j] = (a[i][j] * a[k][k] - a[i][k] * a[k][j]) / prev;
                prev = a[k][k];
                for (int i = k + 1; i < n; i++) a[i][k] = 0;
            }
            return sign * a[n - 1][n - 1];
        }
    }

    // ===================== 数值引擎 =====================

    internal static class Interp
    {
        public static Complex ExtrapolateComplex(double[] xs, Complex[] ys, double x0, int order)
        {
            int n = Math.Min(order, xs.Length);
            if (n <= 1) return ys[ys.Length - 1];
            int start = xs.Length - n;
            Complex[] p = new Complex[n];
            for (int i = 0; i < n; i++) p[i] = ys[start + i];
            for (int j = 1; j < n; j++)
            {
                for (int i = n - 1; i >= j; i--)
                {
                    double xa = xs[start + i], xb = xs[start + i - j];
                    double denom = xa - xb;
                    if (Math.Abs(denom) < 1e-300) denom = 1e-300;
                    p[i] = (p[i] * (x0 - xb) - p[i - 1] * (x0 - xa)) / denom;
                }
            }
            return p[n - 1];
        }

        public static double ExtrapolateReal(double[] xs, double[] ys)
        {
            int n = xs.Length;
            if (n <= 1) return ys[ys.Length - 1];
            double[] p = new double[n];
            for (int i = 0; i < n; i++) p[i] = ys[i];
            for (int j = 1; j < n; j++)
                for (int i = n - 1; i >= j; i--)
                {
                    double xa = xs[i], xb = xs[i - j];
                    double denom = xa - xb;
                    if (Math.Abs(denom) < 1e-300) denom = 1e-300;
                    p[i] = (p[i] * (0.0 - xb) - p[i - 1] * (0.0 - xa)) / denom;
                }
            return p[n - 1];
        }
    }

    internal static class Quad
    {
        public static V Integrate(Func<double, V> f, double a, double b, AuditBudget budget, int maxLevel)
        {
            if (maxLevel < 3) maxLevel = 3;
            if (double.IsInfinity(b)) return Infinite(f, a, budget, maxLevel);
            return DeFinite(f, a, b, budget, maxLevel);
        }

        static V DeFinite(Func<double, V> f, double a, double b, AuditBudget budget, int maxLevel)
        {
            if (b <= a) return V.Real(0.0);
            double c = 0.5 * (a + b), r = 0.5 * (b - a);
            double prevRe = 0.0, prevIm = 0.0;
            bool havePrev = false;
            double valRe = 0.0, valIm = 0.0, valE = 0.0;
            double h = 0.5;
            for (int level = 0; level < maxLevel; level++)
            {
                h *= 0.5;
                int kmax = (int)Math.Ceiling(4.6 / h);
                Complex sum = Complex.Zero;
                double errSum = 0.0;
                for (int k = -kmax; k <= kmax; k++)
                {
                    budget.Tick();
                    double t = k * h;
                    double z = Math.PI * 0.5 * Math.Sinh(t);
                    // 用 e^{-2|z|} 表达，避免 1−tanh(z) 在端点附近被浮点抵消吃掉
                    double e = Math.Exp(-2.0 * Math.Abs(z));
                    double omu = 2.0 * e / (1.0 + e);            // 到最近端点的相对距离
                    double sech2 = 4.0 * e / ((1.0 + e) * (1.0 + e));
                    if (omu < 1e-17) continue;
                    double w = r * (Math.PI * 0.5) * Math.Cosh(t) * sech2;
                    if (w == 0.0 || double.IsNaN(w) || double.IsInfinity(w)) continue;
                    double x = z >= 0.0 ? b - r * omu : a + r * omu;
                    V fv = f(x);
                    sum += fv.C * w;
                    errSum += Math.Abs(w) * fv.E;
                }
                valRe = sum.Real * h;
                valIm = sum.Imaginary * h;
                valE = errSum * h + 1e-15 * (sum.Magnitude * h);
                if (havePrev && level >= 2)
                {
                    double d = Math.Abs(valRe - prevRe) + Math.Abs(valIm - prevIm);
                    double scale = 1.0 + Math.Abs(valRe) + Math.Abs(valIm);
                    if (d <= 1e-14 * scale) break;
                }
                prevRe = valRe; prevIm = valIm; havePrev = true;
                if (budget.Exceeded) break;
            }
            V res;
            res.C = new Complex(valRe, valIm);
            res.E = Math.Max(valE, (Math.Abs(valRe - prevRe) + Math.Abs(valIm - prevIm)) * 4.0);
            res.E += 1e-14 * res.C.Magnitude;
            return res;
        }

        /// <summary>
        /// 半无穷区间：先做有理映射 u = (x−a)/(1+x−a)，x = a + u/(1−u)，dx = du/(1−u)²，
        /// 把 [a,∞) 变成 [0,1]，再交给同一套 tanh-sinh。
        /// 对 e^{−ax²}、1/(x⁴+a⁴)、1/(x²+a²) 这类被积函数，映射后在 u=1 处是有限且光滑的
        /// （或所有导数都退化），比直接对 x 做双重指数稳定得多，尤其是三重积分嵌套时。
        /// </summary>
        public static V Infinite(Func<double, V> f, double a, AuditBudget budget, int maxLevel)
        {
            Func<double, V> g = delegate(double u)
            {
                double om = 1.0 - u;
                if (om <= 0.0) return V.Real(0.0);
                double x = a + u / om;
                V v = f(x);
                V r;
                r.C = v.C / (om * om);
                r.E = v.E / (om * om);
                return r;
            };
            // 嵌套（三重积分）时求值次数是各维节点数之积：改用高斯–勒让德，
            // 指数收敛、节点少，40³ = 64000 次就够，比双重指数快两个数量级。
            if (maxLevel <= 4) return Gauss(g, 0.0, 1.0, budget);
            return DeFinite(g, 0.0, 1.0, budget, 4);
        }

        static readonly double[] GlX = new double[40];
        static readonly double[] GlW = new double[40];
        static bool glReady;

        static double Legendre(int n, double z, out double dp)
        {
            double p0 = 1.0, p1 = z;
            for (int k = 2; k <= n; k++)
            {
                double p2 = ((2.0 * k - 1.0) * z * p1 - (k - 1.0) * p0) / k;
                p0 = p1; p1 = p2;
            }
            dp = n * (z * p1 - p0) / (z * z - 1.0);
            return p1;
        }

        static void GlInit()
        {
            if (glReady) return;
            int n = 40;
            for (int i = 0; i < n; i++)
            {
                double z = Math.Cos(Math.PI * (i + 0.75) / (n + 0.5));
                double dp;
                for (int it = 0; it < 100; it++)
                {
                    double v = Legendre(n, z, out dp);
                    double dz = v / dp;
                    z -= dz;
                    if (Math.Abs(dz) < 1e-15) break;
                }
                Legendre(n, z, out dp);
                GlX[i] = z;
                GlW[i] = 2.0 / ((1.0 - z * z) * dp * dp);
            }
            glReady = true;
        }

        /// <summary>n=40 点高斯–勒让德（节点/权重在 GlInit 里用牛顿迭代算出）。</summary>
        public static V Gauss(Func<double, V> f, double a, double b, AuditBudget budget)
        {
            GlInit();
            double c = 0.5 * (a + b), r = 0.5 * (b - a);
            Complex sum = Complex.Zero;
            double err = 0.0;
            for (int i = 0; i < 40; i++)
            {
                budget.Tick();
                double x = c + r * GlX[i];
                V v = f(x);
                sum += v.C * GlW[i];
                err += Math.Abs(GlW[i]) * v.E;
            }
            V res;
            res.C = sum * r;
            res.E = err * Math.Abs(r) + 1e-14 * res.C.Magnitude;
            return res;
        }

        static V DeInfinite(Func<double, V> f, double a, AuditBudget budget, int maxLevel)
        {
            double prevRe = 0.0, prevIm = 0.0;
            bool havePrev = false;
            double valRe = 0.0, valIm = 0.0, valE = 0.0;
            double h = 0.5;
            for (int level = 0; level < maxLevel; level++)
            {
                h *= 0.5;
                int kmax = (int)Math.Ceiling(3.6 / h);
                Complex sum = Complex.Zero;
                double errSum = 0.0;
                for (int k = -kmax; k <= kmax; k++)
                {
                    budget.Tick();
                    double t = k * h;
                    double s = Math.PI * 0.5 * Math.Sinh(t);
                    if (s > 700.0) continue;
                    double ex = Math.Exp(s);
                    if (double.IsInfinity(ex) || ex == 0.0) continue;
                    double x = a + ex;
                    double w = ex * (Math.PI * 0.5) * Math.Cosh(t);
                    if (w == 0.0 || double.IsNaN(w) || double.IsInfinity(w)) continue;
                    V fv = f(x);
                    sum += fv.C * w;
                    errSum += Math.Abs(w) * fv.E;
                }
                valRe = sum.Real * h;
                valIm = sum.Imaginary * h;
                valE = errSum * h + 1e-15 * (sum.Magnitude * h);
                if (havePrev && level >= 2)
                {
                    double d = Math.Abs(valRe - prevRe) + Math.Abs(valIm - prevIm);
                    double scale = 1.0 + Math.Abs(valRe) + Math.Abs(valIm);
                    if (d <= 1e-14 * scale) break;
                }
                prevRe = valRe; prevIm = valIm; havePrev = true;
                if (budget.Exceeded) break;
            }
            V res;
            res.C = new Complex(valRe, valIm);
            res.E = Math.Max(valE, (Math.Abs(valRe - prevRe) + Math.Abs(valIm - prevIm)) * 4.0);
            res.E += 1e-14 * res.C.Magnitude;
            return res;
        }
    }

    // ===================== 解析器 =====================

    internal sealed class AuditException : Exception
    {
        public AuditException(string msg) : base(msg) { }
    }

    internal sealed class LatexParser
    {
        readonly string s;
        int i;

        public LatexParser(string latex) { s = latex ?? ""; }

        public Node ParseAll()
        {
            Node n = ParseAdd();
            Ws();
            if (!Eof) throw new AuditException("式子末尾有多余内容（位置 " + i + "：" + s.Substring(i, Math.Min(12, s.Length - i)) + "）");
            return n;
        }

        bool Eof { get { return i >= s.Length; } }

        void Ws()
        {
            while (i < s.Length)
            {
                char c = s[i];
                if (char.IsWhiteSpace(c)) { i++; continue; }
                if (c == '\\' && i + 1 < s.Length)
                {
                    char n = s[i + 1];
                    if (n == ',' || n == ';' || n == '!' || n == ' ' || n == ':' || n == '|') { i += 2; continue; }
                    if (n == 'q' && StartsWith("quad")) { i += 5; continue; }
                    if (n == 'q' && StartsWith("qquad")) { i += 6; continue; }
                    if (n == 'd' && StartsWith("displaystyle")) { i += 12; continue; }
                    if (n == 'l' && StartsWith("limits")) { i += 7; continue; }
                }
                break;
            }
        }

        bool StartsWith(string t)
        {
            if (i + t.Length > s.Length) return false;
            return string.CompareOrdinal(s, i, t, 0, t.Length) == 0;
        }

        string CmdAhead()
        {
            if (Eof || s[i] != '\\') return null;
            int save = i;
            string c = ReadCmd();
            i = save;
            return c;
        }

        string ReadCmd()
        {
            if (Eof || s[i] != '\\') throw new AuditException("期望命令");
            i++;
            if (Eof) return "";
            char c = s[i];
            if (char.IsLetter(c))
            {
                int start = i;
                while (i < s.Length && char.IsLetter(s[i])) i++;
                return s.Substring(start, i - start);
            }
            i++;
            return c.ToString();
        }

        bool Take(char c)
        {
            Ws();
            if (!Eof && s[i] == c) { i++; return true; }
            return false;
        }

        void Expect(char c)
        {
            if (!Take(c)) throw new AuditException("期望 '" + c + "'（位置 " + i + "）");
        }

        bool TakeCmd(string name)
        {
            Ws();
            if (CmdAhead() == name) { ReadCmd(); return true; }
            return false;
        }

        void ExpectCmd(string name)
        {
            if (!TakeCmd(name)) throw new AuditException("期望 \\" + name + "（位置 " + i + "）");
        }

        bool IsTerminator()
        {
            Ws();
            if (Eof) return true;
            char c = s[i];
            if (c == ')' || c == ']' || c == '}' || c == '=' || c == ',' || c == '&') return true;
            if (c == '\\' && i + 1 < s.Length && s[i + 1] == '\\') return true;
            string cmd = CmdAhead();
            if (cmd == "right" || cmd == "end" || cmd == "to") return true;
            return false;
        }

        // -------- 表达式 --------
        public Node ParseAdd()
        {
            Ws();
            bool neg = false;
            while (!Eof && (s[i] == '+' || s[i] == '-' || s[i] == '\u2212'))
            {
                if (s[i] != '+') neg = !neg;
                i++;
                Ws();
            }
            Node a = ParseMul();
            if (neg) a = Node.Sub(Node.Num(0.0), a);
            while (true)
            {
                Ws();
                if (Eof) break;
                char c = s[i];
                if (c == '+') { i++; a = Node.Add(a, ParseMul()); }
                else if (c == '-' || c == '\u2212') { i++; a = Node.Sub(a, ParseMul()); }
                else break;
            }
            return a;
        }

        Node ParseMul()
        {
            Node acc = null;
            bool pendingDiv = false;
            while (true)
            {
                Ws();
                if (Eof) break;
                char c = s[i];
                if (c == '+' || c == '-' || c == '\u2212') break;
                if (c == '/') { i++; pendingDiv = true; continue; }
                if (c == '\\' && (CmdAhead() == "bmod" || CmdAhead() == "mathrm"))
                {
                    // 取模：\bmod 7 或 \mathrm{mod}\,7 都认（后者是 WpfMath 能渲染的写法）
                    int save = i;
                    string cmd = ReadCmd();
                    bool isMod = cmd == "bmod";
                    if (cmd == "mathrm")
                    {
                        if (Take('{'))
                        {
                            int st = i;
                            while (i < s.Length && s[i] != '}') i++;
                            isMod = string.CompareOrdinal(s, st, "mod", 0, 3) == 0 && (i - st) == 3;
                            if (i < s.Length) i++;
                        }
                    }
                    if (!isMod) { i = save; }
                    else
                    {
                        Node modR = ParsePower();
                        NMod md = new NMod(); md.A = acc ?? Node.Num(0.0); md.B = modR;
                        acc = md;
                        pendingDiv = false;
                        continue;
                    }
                }
                if (c == '\\')
                {
                    string cmd = CmdAhead();
                    if (cmd == "cdot" || cmd == "times") { ReadCmd(); continue; }
                }
                if (IsTerminator()) break;
                int before = i;
                Node p = ParsePower();
                if (i == before) throw new AuditException("解析没有进展（位置 " + i + "）");
                if (p == null) break;
                if (acc == null) acc = p;
                else acc = pendingDiv ? Node.Div(acc, p) : Node.Mul(acc, p);
                pendingDiv = false;
            }
            if (acc == null) return Node.Num(1.0);
            return acc;
        }

        Node ParsePower()
        {
            Node b = ParsePrimary();
            while (true)
            {
                Ws();
                if (Eof) break;
                if (s[i] == '^') { i++; b = Node.Pow(b, ParseScriptArg()); }
                else if (s[i] == '!')
                {
                    // `!!` 在本项目里表示两次阶乘（3!! = (3!)! = 720），与阶乘语义一致地叠两次
                    i++;
                    b = Wrap("fact", b);
                    if (!Eof && s[i] == '!') { i++; b = Wrap("fact", b); }
                }
                else break;
            }
            return b;
        }

        Node ParseScriptArg()
        {
            Ws();
            if (Eof) throw new AuditException("指数/上下限缺失");
            if (s[i] == '{')
            {
                i++;
                Node e = ParseAdd();
                Expect('}');
                return e;
            }
            return ParsePrimary();
        }

        Node ParsePrimary()
        {
            Ws();
            if (Eof) return null;
            char c = s[i];
            if (c >= '0' && c <= '9') return ParseNumber();
            if (c == '(') { i++; Node e = ParseAdd(); Expect(')'); return e; }
            if (c == '[') { i++; Node e = ParseAdd(); Expect(']'); return e; }
            if (c == '{') { i++; Node e = ParseAdd(); Expect('}'); return e; }
            if (c == '|') { i++; Node e = ParseAdd(); Expect('|'); NCall ab = new NCall(); ab.Fn = "abs"; ab.Arg = e; return ab; }
            if (c == '\\') return ParseCommand();
            if (char.IsLetter(c)) return ParseSymbol();
            throw new AuditException("无法解析的字符 '" + c + "'（位置 " + i + "）");
        }

        Node ParseNumber()
        {
            int start = i;
            while (i < s.Length && s[i] >= '0' && s[i] <= '9') i++;
            if (i < s.Length && s[i] == '.')
            {
                i++;
                while (i < s.Length && s[i] >= '0' && s[i] <= '9') i++;
            }
            double v;
            if (!double.TryParse(s.Substring(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture, out v))
                throw new AuditException("数字解析失败：" + s.Substring(start, i - start));
            return Node.Num(v);
        }

        Node ParseSymbol()
        {
            char c = s[i];
            i++;
            if (c == 'e') return Node.Sym("e");
            if (c == 'i') return Node.Sym("i");
            string name = c.ToString();
            if (!Eof && s[i] == '_')
            {
                int save = i;
                i++;
                Ws();
                if (!Eof && s[i] == '{')
                {
                    i++;
                    int st = i;
                    while (i < s.Length && s[i] != '}') i++;
                    name = c + "_" + s.Substring(st, i - st);
                    if (i < s.Length) i++;
                }
                else if (!Eof && char.IsLetterOrDigit(s[i]))
                {
                    int st = i;
                    while (i < s.Length && char.IsLetterOrDigit(s[i])) i++;
                    name = c + "_" + s.Substring(st, i - st);
                }
                else i = save;
            }
            return Node.Sym(name);
        }

        Node ParseCommand()
        {
            string cmd = ReadCmd();
            switch (cmd)
            {
                case "frac":
                    {
                        Node a = ParseGroupArg();
                        Node b = ParseGroupArg();
                        // \frac{d}{dx}<operand> 认成导数算子（ODE 陷阱用）
                        string dv = DiffVarOf(a, b);
                        if (dv != null)
                        {
                            Node operand = ParsePower();
                            NDeriv nd = new NDeriv();
                            nd.Var = dv;
                            nd.Body = operand;
                            return nd;
                        }
                        return Node.Div(a, b);
                    }
                case "sqrt":
                    {
                        Ws();
                        if (!Eof && s[i] == '[') { i++; ParseAdd(); Expect(']'); }
                        Node a = ParseGroupArg();
                        NCall c = new NCall(); c.Fn = "sqrt"; c.Arg = a; return c;
                    }
                case "left": return ParseLeftRight();
                case "right": throw new AuditException("多余的 \\right");
                case "int": return ParseIntegral(false);
                case "iint": case "iiint": return ParseIntegral(false);
                case "oint": return ParseIntegral(true);
                case "sum": return ParseSeries(false);
                case "prod": return ParseSeries(true);
                case "lim": return ParseLimit();
                case "det": return ParseDet();
                case "zeta": { NZeta z = new NZeta(); z.S = ParseGroupArg(); return z; }
                case "mu": { NMu m = new NMu(); m.A = ParseGroupArg(); return m; }
                case "varphi": case "phi": { NPhi p = new NPhi(); p.A = ParseGroupArg(); return p; }
                case "sin": case "cos": case "tan": case "ln": case "exp": case "arcsin": case "arccos": case "arctan": case "Gamma":
                    return ParseFunction(cmd == "log" ? "ln" : (cmd == "Gamma" ? "gamma" : cmd));
                case "pi": return Node.Sym("pi");
                case "rho": case "theta": case "tau": return Node.Sym(cmd);
                case "infty": return new NInf();
                case "text": SkipBraced(); return Node.Num(1.0);
                case "{": { Node e = ParseAdd(); ExpectCmd("}"); return e; }
                case "}": throw new AuditException("多余的 \\}");
                case "lfloor": case "lceil": return ParseFloorNoLeft(cmd);
                case "lbrace": { Node e = ParseAdd(); ExpectCmd("rbrace"); return e; }
                case "begin": SkipBracedName(); return ParseDetBody();
            }
            throw new AuditException("不支持的 LaTeX 命令 \\" + cmd);
        }

        static string DiffVarOf(Node num, Node den)
        {
            NSym n = num as NSym;
            if (n == null || n.Name != "d") return null;
            NSym sym = den as NSym;
            if (sym != null) return sym.Name.StartsWith("d") && sym.Name.Length > 1 ? sym.Name.Substring(1) : null;
            NMul m = den as NMul;
            if (m == null || m.F.Count != 2) return null;
            NSym d0 = m.F[0] as NSym, d1 = m.F[1] as NSym;
            if (d0 == null || d1 == null || d0.Name != "d") return null;
            return d1.Name;
        }

        void SkipBraced()
        {
            Ws();
            if (Eof || s[i] != '{') return;
            int d = 0;
            while (i < s.Length)
            {
                if (s[i] == '{') d++;
                else if (s[i] == '}') { d--; if (d == 0) { i++; return; } }
                i++;
            }
        }

        void SkipBracedName()
        {
            Ws();
            if (Eof || s[i] != '{') return;
            i++;
            while (i < s.Length && s[i] != '}') i++;
            if (i < s.Length) i++;
        }

        Node ParseGroupArg()
        {
            Ws();
            if (Eof) throw new AuditException("参数缺失");
            if (s[i] == '{') { i++; Node e = ParseAdd(); Expect('}'); return e; }
            return ParsePower();
        }

        Node ParseFunction(string fn)
        {
            Ws();
            double power = 1.0;
            bool hasPower = false;
            if (!Eof && s[i] == '^')
            {
                i++;
                Node e = ParseScriptArg();
                NNum nn = e as NNum;
                if (nn == null) throw new AuditException("函数幂次必须是常数");
                power = nn.Value;
                hasPower = true;
            }
            Node arg = ParseGroupArg();
            Node val = Wrap(fn, arg);
            if (hasPower && power != 1.0) val = Node.Pow(val, Node.Num(power));
            return val;
        }

        static Node Wrap(string fn, Node arg) { NCall c = new NCall(); c.Fn = fn; c.Arg = arg; return c; }

        Node ParseLeftRight()
        {
            string delim = ReadDelimiter();
            Node inner = ParseAdd();
            ExpectCmd("right");
            string close = ReadDelimiter();
            if (delim == "lfloor" || close == "rfloor") return Wrap("floor", inner);
            if (delim == "lceil" || close == "rceil") return Wrap("ceil", inner);
            if (delim == "{" || close == "}") return Wrap("frac", inner);
            if (delim == "|" || close == "|") { NCall ab = new NCall(); ab.Fn = "abs"; ab.Arg = inner; return ab; }
            return inner;
        }

        Node ParseFloorNoLeft(string open)
        {
            Node inner = ParseAdd();
            ExpectCmd(open == "lfloor" ? "rfloor" : "rceil");
            return Wrap(open == "lfloor" ? "floor" : "ceil", inner);
        }

        string ReadDelimiter()
        {
            Ws();
            if (Eof) throw new AuditException("定界符缺失");
            if (s[i] == '\\') return ReadCmd();
            char c = s[i];
            i++;
            return c.ToString();
        }

        // ---- 大运算符 ----
        Node ParseIntegral(bool contour)
        {
            Node lo = null, hi = null;
            Ws();
            if (!Eof && s[i] == '_') { i++; lo = ParseScriptArg(); }
            Ws();
            if (!Eof && s[i] == '^') { i++; hi = ParseScriptArg(); }

            List<Node> fs = new List<Node>();
            string var = null;
            while (true)
            {
                Ws();
                if (Eof) break;
                if (TryDifferential(out var)) break;
                if (IsTerminator()) break;
                if (s[i] == '+' || s[i] == '-' || s[i] == '\u2212') break;
                if (s[i] == '\\')
                {
                    string cmd = CmdAhead();
                    if (cmd == "cdot" || cmd == "times") { ReadCmd(); continue; }
                }
                int before = i;
                Node p = ParsePower();
                if (i == before) break;
                fs.Add(p);
            }
            if (var == null) throw new AuditException("积分缺少微分 d<变量>");
            NInt ni = new NInt();
            ni.Lo = lo;
            ni.Hi = hi;
            ni.Body = Node.Mul(fs.ToArray());
            ni.Var = var;
            ni.Contour = contour;
            return ni;
        }

        bool TryDifferential(out string var)
        {
            var = null;
            Ws();
            int save = i;
            if (Eof || s[i] != 'd') return false;
            i++;
            if (Eof) { i = save; return false; }
            if (char.IsLetter(s[i]))
            {
                char c = s[i];
                i++;
                string name = c.ToString();
                if (!Eof && s[i] == '_')
                {
                    i++;
                    Ws();
                    if (!Eof && s[i] == '{')
                    {
                        i++;
                        int st = i;
                        while (i < s.Length && s[i] != '}') i++;
                        name = c + "_" + s.Substring(st, i - st);
                        if (i < s.Length) i++;
                    }
                }
                var = name;
                return true;
            }
            if (s[i] == '\\')
            {
                string cmd = ReadCmd();
                if (cmd == "rho" || cmd == "theta" || cmd == "tau" || cmd == "varphi")
                {
                    var = cmd;
                    return true;
                }
            }
            i = save;
            return false;
        }

        string ReadVarName()
        {
            Ws();
            if (Eof) throw new AuditException("变量名缺失");
            string name;
            if (s[i] == '\\') name = ReadCmd();
            else { name = s[i].ToString(); i++; }
            if (!Eof && s[i] == '_')
            {
                int save = i;
                i++;
                Ws();
                if (!Eof && s[i] == '{')
                {
                    i++;
                    int st = i;
                    while (i < s.Length && s[i] != '}') i++;
                    name = name + "_" + s.Substring(st, i - st);
                    if (i < s.Length) i++;
                }
                else if (!Eof && (char.IsLetterOrDigit(s[i])))
                {
                    int st = i;
                    while (i < s.Length && char.IsLetterOrDigit(s[i])) i++;
                    name = name + "_" + s.Substring(st, i - st);
                }
                else i = save;
            }
            return name;
        }

        Node ParseSeries(bool isProd)
        {
            Node lo = null, hi = null;
            string var = null;
            Ws();
            if (!Eof && s[i] == '_')
            {
                i++;
                Ws();
                if (!Eof && s[i] == '{')
                {
                    i++;
                    var = ReadVarName();
                    Ws();
                    if (!Eof && s[i] == '=') { i++; lo = ParseAdd(); }
                    Expect('}');
                }
            }
            Ws();
            if (!Eof && s[i] == '^') { i++; hi = ParseScriptArg(); }
            Node body = ParseMul();
            NSeries ns = new NSeries();
            ns.Lo = lo;
            ns.Hi = hi;
            ns.Body = body;
            ns.Var = var ?? "k";
            ns.IsProd = isProd;
            ns.OverPrimes = isProd && var == "p" && lo == null;
            return ns;
        }

        Node ParseLimit()
        {
            string var = "n";
            bool toInf = true;
            Ws();
            if (!Eof && s[i] == '_')
            {
                i++;
                Ws();
                if (!Eof && s[i] == '{')
                {
                    i++;
                    var = ReadVarName();
                    ExpectCmd("to");
                    Ws();
                    if (CmdAhead() == "infty") { ReadCmd(); toInf = true; }
                    else
                    {
                        Node t = ParseAdd();
                        NNum tn = t as NNum;
                        if (tn == null) throw new AuditException("极限目标必须是常数");
                        toInf = false;
                    }
                    Expect('}');
                }
            }
            Node body = ParseMul();
            NLim l = new NLim();
            l.Body = body;
            l.Var = var;
            l.ToInf = toInf;
            return l;
        }

        Node ParseDet()
        {
            Ws();
            if (CmdAhead() == "begin")
            {
                ReadCmd();
                SkipBracedName();
                return ParseDetBody();
            }
            // \det\left( A \cdot B \right)：矩阵乘积的行列式（det(AB) = det(A)·det(B)）
            if (CmdAhead() == "left")
            {
                ReadCmd();
                ReadDelimiter();
                NDetProd dp = new NDetProd();
                dp.Mats.Add(ParseDetMatrix());
                while (true)
                {
                    Ws();
                    if (TakeCmd("cdot")) { dp.Mats.Add(ParseDetMatrix()); continue; }
                    break;
                }
                if (!TakeCmd("right")) throw new AuditException("\\det 的矩阵乘积缺少 \\right");
                ReadDelimiter();
                if (dp.Mats.Count < 2) return dp.Mats[0];
                return dp;
            }
            throw new AuditException("\\det 只支持矩阵字面量");
        }

        Node ParseDetMatrix()
        {
            Ws();
            if (CmdAhead() != "begin") throw new AuditException("\\det 里缺少 pmatrix");
            ReadCmd();
            SkipBracedName();
            return ParseDetBody();
        }

        Node ParseDetBody()
        {
            List<Node[]> rows = new List<Node[]>();
            while (true)
            {
                Ws();
                if (Eof) break;
                if (CmdAhead() == "end") { ReadCmd(); SkipBracedName(); break; }
                List<Node> row = new List<Node>();
                while (true)
                {
                    // 矩阵元素允许是任意表达式（"最外层行列式"外壳会塞进整条式子）
                    Node cell = ParseAdd();
                    row.Add(cell);
                    Ws();
                    if (!Eof && s[i] == '&') { i++; continue; }
                    break;
                }
                rows.Add(row.ToArray());
                Ws();
                if (!Eof && s[i] == '\\' && i + 1 < s.Length && s[i + 1] == '\\') { i += 2; continue; }
                break;
            }
            Ws();
            if (CmdAhead() == "end") { ReadCmd(); SkipBracedName(); }
            if (rows.Count == 0) throw new AuditException("空矩阵");
            NDet d = new NDet();
            d.Rows = rows;
            return d;
        }
    }

    // ===================== 对外入口 =====================

    public static class FormulaAudit
    {
        public const double RelTol = 1e-9;

        /// <summary>Lanczos 近似的实数 Gamma（g=7, n=9），用于 \Gamma(1/2)、\Gamma(5/2) 这类陷阱因子。</summary>
        internal static double GammaReal(double x)
        {
            if (x < 0.5) return Math.PI / (Math.Sin(Math.PI * x) * GammaReal(1.0 - x));
            double[] g =
            {
                0.99999999999980993, 676.5203681218851, -1259.1392167224028,
                771.32342877765313, -176.61502916214059, 12.507343278686905,
                -0.13857109526572012, 9.9843695780195716e-6, 1.5056327351493116e-7
            };
            double z = x - 1.0, sum = g[0];
            for (int i = 1; i < 9; i++) sum += g[i] / (z + i);
            double tt = z + 7.5;
            return Math.Sqrt(2.0 * Math.PI) * Math.Pow(tt, z + 0.5) * Math.Exp(-tt) * sum;
        }

        public static FormulaAuditResult Audit(string latex, Rat claim)
        {
            return Audit(latex, claim, 6000, 500000000L);
        }

        /// <summary>
        /// budgetMs / opLimit 只给 L5 融合版用：嵌套积分壳让求值次数变多，
        /// 2 s 预算在机器繁忙时会把本来正确的式子判成“未验证”。
        /// </summary>
        public static FormulaAuditResult Audit(string latex, Rat claim, int budgetMs, long opLimit)
        {
            FormulaAuditResult r = new FormulaAuditResult();
            r.Mode = "numeric";
            try
            {
                string body = StripClaim(latex);
                if (body.Length == 0) { r.Detail = "空式子"; return r; }
                LatexParser p = new LatexParser(body);
                Node ast = p.ParseAll();
                AuditBudget budget = new AuditBudget();
                budget.Limit = opLimit;
                budget.Deadline = DateTime.UtcNow.AddMilliseconds(budgetMs);
                Env env = new Env();
                env.Budget = budget;
                V v = ast.Eval(env);
                if (!v.IsFinite) { r.Detail = "求值不是有限数"; return r; }
                if (Math.Abs(v.C.Imaginary) > 1e-7 * (1.0 + Math.Abs(v.C.Real)))
                {
                    r.Detail = "结果不是实数：" + v.C.ToString();
                    return r;
                }
                double got = v.C.Real;
                double claimed = Solver.ToDouble(claim.N) / Solver.ToDouble(claim.D);
                r.Value = got;
                r.ErrorBound = v.E;
                r.RelError = Math.Abs(got - claimed) / Math.Max(1.0, Math.Abs(claimed));
                double diff = Math.Abs(got - claimed);
                double scale = Math.Max(1.0, Math.Abs(claimed));
                double tight = Math.Max(4.0 * v.E, RelTol * scale);
                // 松界只用于“容差以外的数值噪声”（端点奇点、慢收敛级数）：
                // 结构性错误至少是 1% 量级，1e-6 相对误差远不足以掩盖它。
                double loose = 1e-6 * scale;
                if (v.E > 1e-3 * scale || budget.Exceeded)
                {
                    // 误差界大到无法下结论（或预算用尽）时判“未验证”，不能算通过，
                    // 否则一个算歪的式子会因为误差界很大而被放行。
                    r.Ok = false;
                    r.Detail += budget.Exceeded ? " 预算用尽（未验证）" : " 误差界过大（未验证）";
                    return r;
                }
                r.Ok = diff <= Math.Max(tight, loose);
                r.Mode = diff <= tight ? "tight" : "approx";
                r.Detail = "got=" + got.ToString("0.000000000000", CultureInfo.InvariantCulture)
                    + " err=" + v.E.ToString("0.###e+0", CultureInfo.InvariantCulture)
                    + (budget.Exceeded ? " BUDGET" : "");
                return r;
            }
            catch (AuditException ex) { r.Detail = ex.Message; return r; }
            catch (Exception ex) { r.Detail = ex.GetType().Name + ": " + ex.Message; return r; }
        }

        public static bool TryValue(string latex, out double value, out string error)
        {
            value = 0.0;
            error = null;
            try
            {
                string body = StripClaim(latex);
                LatexParser p = new LatexParser(body);
                Node ast = p.ParseAll();
                AuditBudget budget = new AuditBudget();
                budget.Deadline = DateTime.UtcNow.AddMilliseconds(6000);
                Env env = new Env();
                env.Budget = budget;
                V v = ast.Eval(env);
                if (!v.IsFinite) { error = "非有限"; return false; }
                value = v.C.Real;
                return true;
            }
            catch (Exception ex) { error = ex.Message; return false; }
        }

        /// <summary>只剥掉最外层、深度 0 的 " = 数字" 后缀。</summary>
        static string StripClaim(string latex)
        {
            string s = latex ?? "";
            int depth = 0, cut = -1;
            for (int k = 0; k < s.Length; k++)
            {
                char c = s[k];
                if (c == '{' || c == '(' || c == '[') depth++;
                else if (c == '}' || c == ')' || c == ']') depth--;
                else if (c == '=' && depth == 0)
                {
                    bool left = k > 0 && (s[k - 1] == ' ');
                    bool right = k + 1 < s.Length && s[k + 1] == ' ';
                    if (left && right) cut = k;
                }
            }
            if (cut > 0) s = s.Substring(0, cut);
            return s.Trim();
        }

        public static string SelfCheck()
        {
            StringBuilder sb = new StringBuilder();
            int pass = 0, fail = 0;
            string[] tex = new string[]
            {
                "\\int_{0}^{1}x^{2}\\,dx",
                "\\int_{0}^{1}3x^{2}\\,dx",
                "\\frac{20^{3}+15^{3}}{20+15}",
                "\\left(17^{2}+6^{2}\\right)",
                "e^{2\\ln5+\\ln13}",
                "\\frac{10^{3}-5^{2}}{3}",
                "25^{2}-20\\cdot15",
                "\\det\\begin{pmatrix}1&0&0&0\\\\0&1&0&0\\\\0&0&5&0\\\\0&0&0&65\\end{pmatrix}",
                "\\sum_{d=1}^{5}\\varphi(d)",
                "\\left\\lfloor 5+\\frac{4}{5}\\right\\rfloor",
                "\\zeta(4)",
                "\\frac{6}{\\pi^{2}}\\sum_{n=1}^{\\infty}\\frac{1}{n^{2}}",
                "\\varphi(1)+\\varphi(5)",
                "\\sum_{k=1}^{\\infty}\\frac{1}{2^{k}}",
                "\\sum_{k=1}^{\\infty}\\frac{1}{k(k+1)}",
                "\\sum_{n=1}^{\\infty}\\frac{1}{n^{2}}",
                "\\lim_{n\\to\\infty}\\frac{n+3}{n}",
                "\\lim_{n\\to\\infty}\\left(1+\\frac{2}{n}\\right)^{n}/e^{2}",
                "\\int_{0}^{+\\infty}2e^{-2x}\\,dx",
                "\\frac{2}{\\pi}\\int_{0}^{+\\infty}\\frac{\\sin x}{x}\\,dx",
                "\\frac{6}{\\pi^{2}}\\int_{0}^{1}\\int_{0}^{1}\\frac{1}{1-xy}\\,dx\\,dy",
                "\\frac{12}{\\pi^{2}}\\int_{0}^{1}\\int_{0}^{x}\\frac{1}{1-xy}\\,dy\\,dx",
                "\\frac{2}{\\pi}\\prod_{n=1}^{\\infty}\\frac{(2n)^{2}}{(2n-1)(2n+1)}",
                "\\int_{0}^{1}\\frac{1}{\\sqrt{x(1-x)}}\\,dx",
                "\\int_{0}^{1}x^{-1/5}\\,dx",
                "\\frac{1}{2\\pi i}\\oint_{C}\\frac{z}{z-13}\\,dz",
                "\\frac{1}{3}\\int_{0}^{+\\infty}\\frac{\\cos x-\\cos(e^{3}x)}{x}\\,dx",
                "\\int_{0}^{+\\infty}\\frac{1}{x^{4}+2^{4}}\\,dx",
                "\\lim_{N\\to\\infty}\\frac{2}{N}\\left[\\sum_{k=1}^{N}\\left\\{k\\sqrt{2}\\right\\}\\right]",
                "\\frac{6}{\\pi^{2}}\\prod_{p}\\left(1-\\frac{1}{p^{2}}\\right)^{-1}",
                // ---- 陷阱层 ----
                "2^{6}\\bmod 7",
                "2^{6}\\mathrm{mod}\\, 7",
                "9^{3}\\bmod 7",
                "9^{3}\\mathrm{mod}\\,7",
                "\\frac{2}{\\left(5+1\\right)}\\sum_{k=1}^{5}\\frac{k}{5}",
                "\\left(0.80\\right)^{2}\\sum_{k=1}^{\\infty}k\\cdot\\left(0.20\\right)^{k-1}",
                "\\frac{e^{-2.5}}{2.5}\\sum_{k=1}^{\\infty}\\frac{k\\cdot 2.5^{k}}{k!}",
                "\\frac{6}{\\pi^{2}}\\left(\\int_{0}^{1}\\int_{0}^{x}\\frac{1}{1-xy}\\,dy\\,dx+\\int_{0}^{1}\\int_{x}^{1}\\frac{1}{1-xy}\\,dy\\,dx\\right)",
                // ---- ODE / 导数 ----
                "\\frac{1}{e}\\cdot \\int_{0}^{1}\\frac{d}{dx}\\left(x\\cdot {e}^{x}\\right)\\,dx",
                "\\frac{1}{\\left(e-1\\right)}\\cdot \\int_{0}^{1}\\frac{d}{dx}\\left({e}^{{x}^{2}}\\right)\\,dx",
                "\\int_{0}^{1}\\frac{d}{dx}\\left(x^{3}\\right)\\,dx"
            };
            double[] want = new double[]
            {
                1.0/3.0,                                    // 0  ∫_0^1 x²
                1.0,                                        // 1  ∫_0^1 3x²
                325.0,                                      // 2  (20³+15³)/(20+15)
                325.0,                                      // 3  17²+6²
                325.0,                                      // 4  e^{2ln5+ln13}
                325.0,                                      // 5  (10³−5²)/3
                325.0,                                      // 6  25²−20·15
                325.0,                                      // 7  det 4×4
                10.0,                                       // 8  Σ_{d=1..5} φ(d) = 10
                5.0,                                        // 9  ⌊5+4/5⌋
                Math.Pow(Math.PI, 4) / 90.0,                // 10 ζ(4)
                1.0,                                        // 11 (6/π²)ζ(2)
                5.0,                                        // 12 φ(1)+φ(5)
                1.0,                                        // 13 Σ 1/2^k
                1.0,                                        // 14 Σ 1/(k(k+1))
                Math.PI * Math.PI / 6.0,                    // 15 ζ(2)
                1.0,                                        // 16 lim (n+3)/n
                1.0,                                        // 17 lim (1+2/n)^n/e²
                1.0,                                        // 18 ∫ 2e^{-2x}
                1.0,                                        // 19 (2/π)∫ sin x/x
                1.0,                                        // 20 (6/π²)∬1/(1−xy)
                1.0,                                        // 21 (12/π²)∫_0^1∫_0^x
                1.0,                                        // 22 (2/π) Wallis
                Math.PI,                                    // 23 ∫_0^1 dx/√(x(1−x))
                1.25,                                       // 24 ∫_0^1 x^{-1/5}
                13.0,                                       // 25 (1/2πi)∮ z/(z−13)
                1.0,                                        // 26 (1/3) Frullani
                Math.PI / (2.0 * Math.Sqrt(2.0) * 8.0),     // 27 ∫_0^∞ dx/(x⁴+2⁴)
                1.0,                                        // 28 lim (2/N)Σ{k√2}
                1.0,                                        // 29 (6/π²)∏_p(1−p^{-2})^{-1}
                1.0,                                        // 30 2^6 mod 7（Fermat）
                1.0,                                        // 31 9^3 mod 7（二次剩余）
                1.0,                                        // 32 均匀分布均值
                1.0,                                        // 33 几何分布均值
                1.0,                                        // 34 Poisson 均值
                1.0,                                        // 35 Fubini 拆分
                1.0,                                        // 36 2^6 \mathrm{mod} 7
                1.0,                                        // 37 9^3 \mathrm{mod} 7
                1.0,                                        // 38 ODE-FTC：y=xe^x
                1.0,                                        // 39 ODE-FTC：y=e^{x²}
                1.0                                         // 40 d/dx(x³) 的积分
            };
            for (int i = 0; i < tex.Length; i++)
            {
                double v;
                string err;
                bool ok = TryValue(tex[i], out v, out err);
                bool good = ok && Math.Abs(v - want[i]) <= 1e-6 * Math.Max(1.0, Math.Abs(want[i]));
                if (good) pass++; else fail++;
                if (!good)
                {
                    sb.Append("FAIL #").Append(i).Append(' ').Append(tex[i]).Append("  →  ");
                    sb.Append(ok ? v.ToString("R", CultureInfo.InvariantCulture) : err).Append('\n');
                }
            }
            sb.Append("===== 审计器自检：通过 ").Append(pass.ToString(CultureInfo.InvariantCulture))
              .Append(" / 失败 ").Append(fail.ToString(CultureInfo.InvariantCulture)).Append(" =====\n");
            return sb.ToString();
        }
    }
}
