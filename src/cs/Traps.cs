/*
 * Calculate325 —— 可验证陷阱层（全部用 AST 构造）
 * ---------------------------------------------------------------------------
 * 只收能被 FormulaAudit / 独立 Python 校验器从最终 LaTeX 重新算出来的陷阱：
 *
 *   A. 精确可判的离散数学
 *      - fermat   : a^(p−1) mod p = 1                （Fermat 小定理，模幂精确）
 *      - euler    : r²^((p−1)/2) mod p = 1           （Euler 判别法 / 二次剩余）
 *      - prob-uniform : (2/(n+1))·Σ_{k=1}^{n} k/n = 1（均匀分布均值归一化）
 *      - prob-geom    : (1−p)²·Σ k p^{k−1} = 1       （几何分布均值归一化）
 *      - prob-poisson : (e^{−λ}/λ)·Σ k λ^k/k! = 1    （Poisson 均值归一化）
 *   B. 需要定理但仍可数值复核
 *      - fubini   : 单位正方形沿对角线拆成两个三角形，两个累次积分之和 = 6/π²·π²/6 = 1
 *
 * 不收 ODE：一条 LaTeX 里无法同时表达初值条件，没有嵌入 ODE 求解器就无法独立验证，
 * 按“不加入无法验证的公式”的原则暂缓（见 docs/verification.md）。
 *
 * 所有陷阱的值都恰好是 1，作为中性因子挂到因子链上。
 */
using System;
using System.Globalization;
using System.Numerics;

namespace Calculate325
{
    public sealed class TrapSpec
    {
        public string Name;        // 英文代号
        public string Title;       // 中文说明
        public XNode Node;         // AST（Latex()/Text() 由统一渲染器产出）
        public string Code;        // 归到哪一类层码（写进 TYPE_CODES）
        public int MinLevel;       // 最低等级：4 或 5
    }

    public static class Traps
    {
        static readonly int[] Primes = new int[] { 7, 11, 13, 17, 19, 23, 29, 31, 37, 41, 43, 47, 53, 59, 61, 67, 71, 73, 79, 83, 89, 97 };

        /// <summary>按等级和种子尝试构造一个陷阱；构造后都会先自检一次值是否真的等于 1。</summary>
        public static TrapSpec Build(int level, string seed, int salt)
        {
            int kind = Hash(seed, salt) % 10;
            TrapSpec t = null;
            if (level <= 4)
            {
                // L4 只能用分析/概率，不能出现矩阵和数论
                if (kind < 5) t = UniformMean(seed, salt + 1);
                else if (kind < 8) t = GeometricMean(seed, salt + 2);
            }
            else
            {
                if (kind < 2) t = OdeFtc(seed, salt + 9);
            else if (kind < 3) t = Fermat(seed, salt + 3);
                else if (kind < 5) t = EulerCriterion(seed, salt + 4);
                else if (kind < 7) t = UniformMean(seed, salt + 5);
                else if (kind < 8) t = GeometricMean(seed, salt + 6);
                else if (kind < 9) t = PoissonMean(seed, salt + 7);
                else t = FubiniSplit();
            }
            if (t == null) return null;
            Rat? exact = t.Node.Exact;
            if (exact.HasValue && !Rats.Eq(exact.Value, Rats.One)) return null;   // 自检失败就丢弃
            return t;
        }

        static int Hash(string s, int salt)
        {
            uint st = (uint)(2166136261u ^ (uint)salt);
            if (s != null)
                for (int i = 0; i < s.Length; i++) st = (st ^ s[i]) * 16777619u;
            return (int)(st % 1000u);
        }

        static int Digit(string s, int i) { return s != null && i >= 0 && i < s.Length && s[i] >= '0' && s[i] <= '9' ? s[i] - '0' : 0; }

        // ---------- A0. ODE / 微积分基本定理 ----------
        // y 满足一阶方程 y' = f(x,y) ⟺ ∫ y' dx = y(b) − y(a)。
        // 这里把 y' 写成 \frac{d}{dx}(...) 让校验器必须**真的求导**再积分，
        // 而不是把生成器算好的值搬过来。
        //   变体 0：y = x e^x，y' = (1+x)e^x，∫_0^1 y' = e      → (1/e)∫ = 1
        //   变体 1：y = e^{x²}，y' = 2x e^{x²}，∫_0^1 y' = e−1   → (1/(e−1))∫ = 1
        static TrapSpec OdeFtc(string seed, int salt)
        {
            int which = (Digit(seed, 27) * 7 + Digit(seed, 28) * 3 + salt) % 2;
            TrapSpec t = new TrapSpec();
            t.Name = "ode-ftc";
            t.Title = "ODE 解的导数再积分 = 端点差（FTC）";
            t.MinLevel = 5;
            t.Code = "0";
            XNode x = X.Var("x");
            XNode dydx, rhs;
            if (which == 0)
            {
                // y = x e^x：分子是 y′ 的积分（校验器必须真的求导），分母是 ODE 右端的积分
                dydx = X.Deriv("x", X.Mul(x, X.Pow(X.E, x)));
                rhs = X.Mul(X.Add(X.Num(1), x), X.Pow(X.E, x));
            }
            else
            {
                // y = e^{x²}：y′ = 2x e^{x²}
                dydx = X.Deriv("x", X.Pow(X.E, X.Pow(x, X.Num(2))));
                rhs = X.Mul(X.Num(2), x, X.Pow(X.E, X.Pow(x, X.Num(2))));
            }
            // 自归一化：∫y′ / ∫(ODE 右端)，两边都应该等于 y(1) − y(0)，
            // 所以比值恒为 1，而且式子里没有任何"刻意凑出来的系数"。
            XNode num = X.Int(X.Num(0), X.Num(1), dydx, "x");
            XNode den = X.Int(X.Num(0), X.Num(1), rhs, "x");
            t.Node = X.Frac(num, den);
            return t;
        }

        // ---------- A1. Fermat 小定理 ----------
        static TrapSpec Fermat(string seed, int salt)
        {
            int p = Primes[(Digit(seed, 11) * 7 + Digit(seed, 12) * 3 + salt) % Primes.Length];
            int a = 2 + (Digit(seed, 13) * 7 + Digit(seed, 14) * 5 + salt) % (p - 2);
            if (ModPow(a, p - 1, p) != 1) return null;
            TrapSpec t = new TrapSpec();
            t.Name = "fermat";
            t.Title = "有限域 Fermat 小定理";
            t.MinLevel = 5;
            t.Code = "5";
            t.Node = new XMod();
            ((XMod)t.Node).A = X.Pow(X.Num(a), X.Num(p - 1));
            ((XMod)t.Node).M = X.Num(p);
            return t;
        }

        // ---------- A2. Euler 判别法（二次剩余） ----------
        static TrapSpec EulerCriterion(string seed, int salt)
        {
            int p = Primes[(Digit(seed, 15) * 5 + Digit(seed, 16) * 11 + salt) % Primes.Length];
            int r = 2 + (Digit(seed, 17) * 7 + Digit(seed, 18) * 3 + salt) % (p - 2);
            int qr = (r * r) % p;
            if (qr == 0) return null;
            int e = (p - 1) / 2;
            if (ModPow(qr, e, p) != 1) return null;
            TrapSpec t = new TrapSpec();
            t.Name = "euler";
            t.Title = "有限域 Euler 判别法";
            t.MinLevel = 5;
            t.Code = "5";
            XMod m = new XMod();
            m.A = X.Pow(X.Num(qr), X.Frac(X.Num(p - 1), X.Num(2)));
            m.M = X.Num(p);
            t.Node = m;
            return t;
        }

        static long ModPow(long b, long e, long m)
        {
            long r = 1 % m;
            b %= m;
            while (e > 0)
            {
                if ((e & 1) == 1) r = (r * b) % m;
                b = (b * b) % m;
                e >>= 1;
            }
            return r;
        }

        // ---------- A3. 均匀分布均值 ----------
        static TrapSpec UniformMean(string seed, int salt)
        {
            int n = 5 + (Digit(seed, 21) * 7 + Digit(seed, 22) * 3 + salt) % 36;   // 5..40
            TrapSpec t = new TrapSpec();
            t.Name = "prob-uniform";
            t.Title = "概率期望：均匀分布均值";
            t.MinLevel = 4;
            t.Code = "4";
            XNode coef = X.Frac(X.Num(2), X.Add(X.Num(n), X.Num(1)));
            XNode term = X.Frac(X.Var("k"), X.Num(n));
            XNode sum = X.Sum(X.Num(1), X.Num(n), term, "k");
            t.Node = X.Mul(coef, sum);
            return t;
        }

        // ---------- A4. 几何分布均值 ----------
        static TrapSpec GeometricMean(string seed, int salt)
        {
            int q = 2 + (Digit(seed, 23) * 7 + Digit(seed, 24) + salt) % 7;   // 2..8
            string ps = "0." + q.ToString(CultureInfo.InvariantCulture);
            int complementary = 10 - q;
            TrapSpec t = new TrapSpec();
            t.Name = "prob-geom";
            t.Title = "概率期望：几何分布均值";
            t.MinLevel = 4;
            t.Code = "4";
            XNode pNode = new XLeaf(ps, ps, new Rat(new BigInteger(q), new BigInteger(10)));
            XNode qNode = new XLeaf("0." + complementary.ToString(CultureInfo.InvariantCulture),
                "0." + complementary.ToString(CultureInfo.InvariantCulture),
                new Rat(new BigInteger(complementary), new BigInteger(10)));
            XNode pow = X.Pow(pNode, X.Sub(X.Var("k"), X.Num(1)));
            XNode sum = X.Sum(X.Num(1), X.Inf, X.Mul(X.Var("k"), pow), "k");
            t.Node = X.Mul(X.Pow(qNode, X.Num(2)), sum);
            return t;
        }

        // ---------- A5. Poisson 均值 ----------
        static TrapSpec PoissonMean(string seed, int salt)
        {
            int w = 1 + (Digit(seed, 25) * 7 + Digit(seed, 26) * 3 + salt) % 8;   // 1..8
            string lam = (w / 2.0).ToString("0.0", CultureInfo.InvariantCulture);
            TrapSpec t = new TrapSpec();
            t.Name = "prob-poisson";
            t.Title = "概率期望：Poisson 均值";
            t.MinLevel = 5;
            t.Code = "4";
            XNode lamNode = new XLeaf(lam, lam, null);
            XNode negLam = new XLeaf("-" + lam, "−" + lam, null);
            XNode expPart = X.Frac(X.Pow(X.E, negLam), lamNode);
            XNode term = X.Frac(X.Mul(X.Var("k"), X.Pow(lamNode, X.Var("k"))), X.Call("fact", X.Var("k")));
            XNode sum = X.Sum(X.Num(1), X.Inf, term, "k");
            t.Node = X.Mul(expPart, sum);
            return t;
        }

        // ---------- B1. Fubini 拆分 ----------
        static TrapSpec FubiniSplit()
        {
            TrapSpec t = new TrapSpec();
            t.Name = "fubini";
            t.Title = "Fubini 拆分（对角线两半）";
            t.MinLevel = 5;
            t.Code = "1";
            XNode one = X.Num(1);
            XNode denom = X.Sub(one, X.Mul(X.Var("x"), X.Var("y")));
            XNode integrand = X.Frac(one, denom);
            XNode lower = X.Int(X.Num(0), X.Var("x"), integrand, "y");
            XNode upper = X.Int(X.Var("x"), X.Num(1), integrand, "y");
            XNode left = X.Int(X.Num(0), one, lower, "x");
            XNode right = X.Int(X.Num(0), one, upper, "x");
            XNode coef = X.Frac(X.Num(6), X.Pow(X.Pi, X.Num(2)));
            t.Node = X.Mul(coef, X.Add(left, right));
            return t;
        }
    }
}
