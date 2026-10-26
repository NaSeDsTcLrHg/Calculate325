# -*- coding: utf-8 -*-
"""numengine.py -- an independent numerical evaluation engine for the LaTeX
subset used by the Calculate325 generator.

Everything here is written from mathematics and the Python standard library
only (``math`` / ``fractions``); numpy is deliberately not used, and no other
implementation of this project was consulted.

Numerical toolbox
-----------------
* **tanh-sinh (double exponential) quadrature** -- primary 1-D rule.  It
  clusters nodes at both endpoints and therefore integrates endpoint
  algebraic/logarithmic singularities such as ``x^{-4/5}``,
  ``1/sqrt(x(1-x))`` or ``-ln x`` to close to machine precision.
* **adaptive composite Gauss-Legendre** -- second, independent rule.  It is
  used as a cross-check / fallback for smooth integrands and whenever
  tanh-sinh fails to reach its convergence target.
* **semi-infinite intervals** are mapped with ``x = a + t/(1-t)`` and then
  handled by tanh-sinh, which copes with the exponential decay of the
  transformed integrand.
* **series**: partial sums at N = 64,128,...,4096 followed by Neville
  polynomial extrapolation in ``1/N`` (a standard tail extrapolation, valid
  because the remainder of a convergent series with smooth terms has an
  asymptotic expansion in powers of ``1/N``); geometric series are summed in
  closed form; alternating series use the Euler transformation; p-series
  ``sum n^{-p}`` use Euler-Maclaurin.
* **limits**: evaluate the expression along a geometric sequence approaching
  the limit point and extrapolate with Neville.  ``n -> infinity`` uses
  ``n = 64..16384`` and extrapolates in ``1/n``; ``x -> 0`` uses
  ``x = 2^-3..2^-11`` and extrapolates in ``x``.
* **number theory**: sieve of Eratosthenes, Moebius mu, Euler phi, and the
  Riemann zeta function through Euler-Maclaurin (used for zeta(s), s>1).
* **determinants** are computed exactly with Fraction Gaussian elimination.
* **contour integrals** use the residue theorem for rational integrands.

Analytic identities used where plain quadrature cannot work (each one is
justified in its matcher):

* ``int_0^inf sin(a x)/x dx = pi/2``  (a>0)      -- Dirichlet integral
* ``int_0^inf (cos(a x)-cos(b x))/x dx = ln(b/a)`` -- Frullani integral
* ``int_0^inf sin^2(a x)/x^2 dx = pi a/2``        -- integration by parts
* ``int_0^b x^p dx = b^(p+1)/(p+1)`` (p>-1)      -- fundamental theorem
* ``prod_p (1-p^-s)^-1 = zeta(s)``               -- Euler product
* ``sum_n mu(n)/n^s = 1/zeta(s)`` (s>1)          -- Dirichlet series
* ``lim_{n->inf} (1+a/n)^n = e^a``
* ``lim_{N->inf} (2/N) sum_{k=1}^N {k sqrt(p)} = 1`` -- equidistribution
"""

from __future__ import annotations

import cmath
import math
from fractions import Fraction

from latex_ast import (
    Abs, Add, Ceil, Const, Contour, Deriv, Det, DetProd, Div, Fact, Floor, FracPart, Func,
    Integral, Limit, Mobius, Mod, Mul, Neg, Num, Phi, Pow, Prod, Sum, Var,
    Zeta, add, mul, parse, strip_claim_suffix,
)


class EvalError(Exception):
    """Raised when a formula cannot be evaluated (never silently ignored)."""


# ---------------------------------------------------------------------------
# evaluation context
# ---------------------------------------------------------------------------
class Ctx:
    """Per-formula mutable state: analytic identities used and caches."""

    def __init__(self):
        self.identities = []          # names of the analytic identities used
        self.warnings = []
        self.cache = {}
        self.fv = {}                  # id(node) -> frozenset of free variables
        self.quad_depth = 0           # nesting depth of numerical quadrature
        self.depth = 0

    def identity(self, name):
        self.identities.append(name)

    def warn(self, msg):
        self.warnings.append(msg)


# ---------------------------------------------------------------------------
# small numeric helpers
# ---------------------------------------------------------------------------
HALF_PI = math.pi / 2.0
_ZERO = 0j
_ONE = 1 + 0j


def _finite(z) -> bool:
    z = complex(z)
    return math.isfinite(z.real) and math.isfinite(z.imag)


def _cfsum(vals):
    """Accurate sum of a list of complex numbers."""
    if not vals:
        return 0j
    if all(isinstance(v, float) for v in vals):
        return complex(math.fsum(vals))
    return complex(math.fsum(v.real for v in vals),
                   math.fsum(v.imag for v in vals))


def _real(z, what="表达式"):
    z = complex(z)
    if not math.isfinite(z.real):
        raise EvalError("%s的值不是有限数" % what)
    if abs(z.imag) > 1e-9 * max(1.0, abs(z.real)):
        raise EvalError("%s不是实数（虚部 %r）" % (what, z.imag))
    return z.real


def _as_int(z, what="下标"):
    x = _real(z, what)
    n = round(x)
    if abs(x - n) > 1e-9 * max(1.0, abs(x)):
        raise EvalError("%s必须是整数，实际为 %r" % (what, x))
    return int(n)


def _cint_pow(base, n: int):
    """Integer power by repeated squaring (exact for (-1)^k etc.)."""
    if n == 0:
        return _ONE
    if n < 0:
        return _ONE / _cint_pow(base, -n)
    result = _ONE
    x = base
    while n:
        if n & 1:
            result = result * x
        x = x * x
        n >>= 1
    return result


def _pow_val(base, exp):
    base = complex(base)
    exp = complex(exp)
    if abs(exp.imag) < 1e-15:
        e = exp.real
        if abs(e - round(e)) < 1e-12 and abs(e) < 1e7:
            n = int(round(e))
            if base.imag == 0.0:
                try:
                    return complex(base.real ** n)
                except (OverflowError, ZeroDivisionError):
                    pass
            return _cint_pow(base, n)
    try:
        return base ** exp
    except (OverflowError, ZeroDivisionError) as exc:
        raise EvalError("幂运算失败：%s" % exc)


# ---------------------------------------------------------------------------
# exact rational evaluation (used for floors, factorials, exponents, ...)
# ---------------------------------------------------------------------------
def rat(node):
    """Return the exact Fraction value of *node* if it is rational, else None."""
    if isinstance(node, Num):
        try:
            return Fraction(node.text)
        except (ValueError, ZeroDivisionError):
            return None
    if isinstance(node, Neg):
        r = rat(node.arg)
        return -r if r is not None else None
    if isinstance(node, Add):
        total = Fraction(0)
        for t in node.terms:
            r = rat(t)
            if r is None:
                return None
            total += r
        return total
    if isinstance(node, Mul):
        total = Fraction(1)
        for f in node.factors:
            r = rat(f)
            if r is None:
                return None
            total *= r
        return total
    if isinstance(node, Div):
        a, b = rat(node.num), rat(node.den)
        if a is None or b is None or b == 0:
            return None
        return a / b
    if isinstance(node, Pow):
        b = rat(node.base)
        e = rat(node.exp)
        if b is None or e is None or e.denominator != 1:
            return None
        if abs(e.numerator) > 400:
            return None
        try:
            return b ** int(e.numerator)
        except (OverflowError, ZeroDivisionError):
            return None
    if isinstance(node, Fact):
        r = rat(node.arg)
        if r is None or r.denominator != 1 or r < 0 or r > 50:
            return None
        value = Fraction(1)
        for k in range(2, int(r) + 1):
            value *= k
        return value
    if isinstance(node, Mod):
        a = rat(node.a)
        m = rat(node.m)
        if a is None or m is None or a.denominator != 1 or m.denominator != 1:
            return None
        if m == 0:
            return None
        return Fraction(int(a) % abs(int(m)))
    if isinstance(node, Floor):
        r = rat(node.arg)
        return Fraction(math.floor(r)) if r is not None else None
    if isinstance(node, Ceil):
        r = rat(node.arg)
        return Fraction(math.ceil(r)) if r is not None else None
    if isinstance(node, FracPart):
        r = rat(node.arg)
        if r is None:
            return None
        return r - math.floor(r)
    return None


# ---------------------------------------------------------------------------
# free variables
# ---------------------------------------------------------------------------
def free_vars(node):
    if isinstance(node, Var):
        return {node.key}
    if isinstance(node, (Num, Const)):
        return set()
    if isinstance(node, Add):
        out = set()
        for t in node.terms:
            out |= free_vars(t)
        return out
    if isinstance(node, Mul):
        out = set()
        for f in node.factors:
            out |= free_vars(f)
        return out
    if isinstance(node, Neg):
        return free_vars(node.arg)
    if isinstance(node, Abs):
        return free_vars(node.arg)
    if isinstance(node, DetProd):
        out = set()
        for m in node.mats:
            out |= free_vars(m)
        return out
    if isinstance(node, Deriv):
        return free_vars(node.body)      # 导数在 x 处取值，仍然依赖 x
    if isinstance(node, Div):
        return free_vars(node.num) | free_vars(node.den)
    if isinstance(node, Pow):
        return free_vars(node.base) | free_vars(node.exp)
    if isinstance(node, Fact):
        return free_vars(node.arg)
    if isinstance(node, Mod):
        return free_vars(node.a) | free_vars(node.m)
    if isinstance(node, Func):
        out = free_vars(node.arg)
        if node.power is not None:
            out |= free_vars(node.power)
        return out
    if isinstance(node, Integral):
        out = free_vars(node.body)
        if node.lo is not None:
            out |= free_vars(node.lo)
        if node.hi is not None:
            out |= free_vars(node.hi)
        return out - set(node.dims)
    if isinstance(node, (Sum, Prod)):
        out = free_vars(node.body)
        if node.lo is not None:
            out |= free_vars(node.lo)
        if node.hi is not None:
            out |= free_vars(node.hi)
        return out - {node.var}
    if isinstance(node, Limit):
        return (free_vars(node.body) | free_vars(node.target)) - {node.var}
    if isinstance(node, (Floor, Ceil, FracPart, Zeta, Mobius, Phi)):
        return free_vars(node.arg)
    if isinstance(node, Det):
        out = set()
        for row in node.rows:
            for e in row:
                out |= free_vars(e)
        return out
    if isinstance(node, Contour):
        return free_vars(node.body) - {node.var}
    raise EvalError("未知的语法树节点 %r" % (node,))


def _is_var(node, var) -> bool:
    return isinstance(node, Var) and node.key == var


def _factors(node):
    """Flatten a product into a list of factors, pushing divisions and integer
    powers of products down to the leaves (used for structural matching)."""
    if isinstance(node, Mul):
        out = []
        for f in node.factors:
            out.extend(_factors(f))
        return out
    if isinstance(node, Div):
        out = _factors(node.num)
        for f in _factors(node.den):
            out.append(Pow(f, Num("-1", -1.0)))
        return out
    if isinstance(node, Pow):
        e = rat(node.exp)
        if e is not None and e.denominator == 1 and isinstance(node.base, Mul):
            out = []
            for f in _factors(node.base):
                out.append(Pow(f, node.exp))
            return out
    return [node]


def _as_power_of(node, var, ctx):
    """If node == var**p with p a rational constant, return p (Fraction)."""
    if _is_var(node, var):
        return Fraction(1)
    if isinstance(node, Pow):
        e = rat(node.exp)
        if e is None:
            return None
        inner = _as_power_of(node.base, var, ctx)
        if inner is None:
            return None
        return inner * e
    return None


def _scaled_var(node, var, env, ctx):
    """If node == A*var with a constant complex A, return A, else None."""
    if _is_var(node, var):
        return _ONE
    if isinstance(node, Mul):
        scale = _ONE
        hits = 0
        for f in node.factors:
            if var in free_vars(f):
                s = _scaled_var(f, var, env, ctx)
                if s is None:
                    return None
                scale *= s
                hits += 1
            else:
                scale *= _eval(f, env, ctx)
        return scale if hits == 1 else None
    if isinstance(node, Div):
        if var in free_vars(node.den):
            return None
        s = _scaled_var(node.num, var, env, ctx)
        if s is None:
            return None
        return s / _eval(node.den, env, ctx)
    if isinstance(node, Neg):
        s = _scaled_var(node.arg, var, env, ctx)
        return -s if s is not None else None
    return None


# ---------------------------------------------------------------------------
# elementary functions
# ---------------------------------------------------------------------------
def _apply_func(name, z):
    z = complex(z)
    if name == "gamma" or name == "Gamma":
        if abs(z.imag) > 1e-9:
            raise EvalError("Gamma 只支持实参数")
        return complex(math.gamma(z.real))
    if name == "sqrt":
        if z.imag == 0.0 and z.real >= 0.0:
            return complex(math.sqrt(z.real))
        return cmath.sqrt(z)
    if z.imag == 0.0:
        x = z.real
        try:
            if name == "sin":
                return complex(math.sin(x))
            if name == "cos":
                return complex(math.cos(x))
            if name == "tan":
                return complex(math.tan(x))
            if name == "cot":
                return complex(1.0 / math.tan(x))
            if name in ("ln", "log"):
                if x > 0.0:
                    return complex(math.log(x))
                raise ValueError
            if name == "exp":
                return complex(math.exp(x))
            if name == "arcsin":
                return complex(math.asin(x))
            if name == "arccos":
                return complex(math.acos(x))
            if name == "arctan":
                return complex(math.atan(x))
            if name == "sinh":
                return complex(math.sinh(x))
            if name == "cosh":
                return complex(math.cosh(x))
            if name == "tanh":
                return complex(math.tanh(x))
            if name == "sec":
                return complex(1.0 / math.cos(x))
            if name == "csc":
                return complex(1.0 / math.sin(x))
        except ValueError:
            pass
    fn = {"ln": cmath.log, "log": cmath.log}.get(name, getattr(cmath, name))
    return complex(fn(z))


def _factorial_value(z, times=1):
    for _ in range(times):
        x = _real(z, "阶乘的自变量")
        n = round(x)
        if abs(x - n) > 1e-9 * max(1.0, abs(x)):
            raise EvalError("阶乘的自变量必须是整数，实际为 %r" % x)
        n = int(n)
        if n < 0:
            raise EvalError("负数没有阶乘")
        if n > 170:
            raise EvalError("阶乘 %d! 超出双精度范围" % n)
        z = complex(math.factorial(n))
    return z


def _mod_int(node, modulus, env, ctx):
    """Exact ``a mod m`` for integer expressions.

    Modular exponentiation uses Python's three-argument pow(), i.e. it never
    materialises a**b in floating point (which would overflow): the residue is
    computed with exact big-integer arithmetic.
    """
    if isinstance(node, Pow):
        base = rat(node.base)
        exp = rat(node.exp)
        if base is not None and base.denominator == 1 \
                and exp is not None and exp.denominator == 1:
            e = int(exp)
            if e < 0:
                raise EvalError("模幂的指数不能为负")
            return pow(int(base) % modulus, e, modulus)
    r = rat(node)
    if r is not None and r.denominator == 1:
        return int(r) % modulus
    v = _real(_eval(node, env, ctx), "取模的左操作数")
    n = round(v)
    if abs(v - n) > 1e-9 * max(1.0, abs(v)):
        raise EvalError("取模的左操作数必须是整数")
    return int(n) % modulus


def _eval_mod(node, env, ctx):
    m = _as_int(_eval(node.m, env, ctx), "模数")
    if m == 0:
        raise EvalError("模数不能为零")
    return complex(_mod_int(node.a, abs(m), env, ctx))


# ---------------------------------------------------------------------------
# quadrature
# ---------------------------------------------------------------------------
_GL_CACHE = {}


def _gl_nodes(n):
    """Nodes and weights of the n-point Gauss-Legendre rule on [-1, 1]."""
    if n in _GL_CACHE:
        return _GL_CACHE[n]
    xs, ws = [], []
    for i in range(1, n + 1):
        x = math.cos(math.pi * (i - 0.25) / (n + 0.5))
        dp = 0.0
        for _ in range(100):
            p0, p1 = 1.0, x
            for k in range(2, n + 1):
                p0, p1 = p1, ((2 * k - 1) * x * p1 - (k - 1) * p0) / k
            dp = n * (x * p1 - p0) / (x * x - 1.0)
            dx = -p1 / dp
            x += dx
            if abs(dx) < 1e-15:
                break
        xs.append(x)
        ws.append(2.0 / ((1.0 - x * x) * dp * dp))
    _GL_CACHE[n] = (xs, ws)
    return xs, ws


def _gl_panel(f, a, b, xs, ws):
    c = 0.5 * (a + b)
    d = 0.5 * (b - a)
    vals = []
    for x, w in zip(xs, ws):
        try:
            fv = f(c + d * x)
        except (ValueError, ZeroDivisionError, OverflowError):
            continue
        if _finite(fv):
            vals.append((w * fv))
    return _cfsum(vals) * d


def adaptive_gauss(f, a, b, tol=1e-12, max_depth=10, n=10):
    """Adaptive composite Gauss-Legendre.  Returns (value, converged)."""
    xs, ws = _gl_nodes(n)
    total = [0j]
    ok = [True]

    def rec(lo, hi, whole, depth):
        mid = 0.5 * (lo + hi)
        left = _gl_panel(f, lo, mid, xs, ws)
        right = _gl_panel(f, mid, hi, xs, ws)
        s = left + right
        if not _finite(s):
            ok[0] = False
            return whole
        if depth <= 0:
            ok[0] = False
            return s
        if abs(s - whole) <= tol * max(1.0, abs(s)):
            return s
        return rec(lo, mid, left, depth - 1) + rec(mid, hi, right, depth - 1)

    whole = _gl_panel(f, a, b, xs, ws)
    total[0] = rec(a, b, whole, max_depth)
    return total[0], ok[0]


def tanh_sinh(f, a, b, max_level=7, eps=1e-13):
    """Double-exponential (tanh-sinh) quadrature on a finite interval.

    x(t) = (a+b)/2 + (b-a)/2 * tanh(pi/2 sinh t)

    The abscissa is computed as ``b - (b-a)*e^{-2u}/(1+e^{-2u})`` (resp.
    ``a + ...``) rather than with ``tanh``: for u below about -19 the tanh
    saturates to -1 in double precision, x collapses onto the endpoint and the
    last 1e-16 of the interval is silently dropped -- which for an integrable
    singularity such as ``x^{-1/2}`` still carries ~1e-8 of the integral.
    The stable form keeps x accurate down to ~1e-300.

    Returns (value, converged).
    """
    halfw = 0.5 * (b - a)
    span = b - a
    prev = None
    prev_diff = None
    last = None
    for level in range(2, max_level + 1):
        h = 2.0 ** (-level)
        kmax = int(6.0 / h)
        parts = []
        for k in range(-kmax, kmax + 1):
            t = k * h
            u = HALF_PI * math.sinh(t)
            if u > 350.0 or u < -350.0:
                continue
            ch = math.cosh(u)
            w = HALF_PI * math.cosh(t) / (ch * ch)
            if u >= 0.0:
                e = math.exp(-2.0 * u)
                x = b - span * e / (1.0 + e)
            else:
                e = math.exp(2.0 * u)
                x = a + span * e / (1.0 + e)
            if x <= a or x >= b:
                continue
            try:
                fv = f(x)
            except (ValueError, ZeroDivisionError, OverflowError):
                continue
            if not _finite(fv):
                continue
            parts.append(w * fv)
        s = _cfsum(parts) * (halfw * h)
        last = s
        if prev is not None:
            tol = eps * max(1.0, abs(s))
            diff = abs(s - prev)
            if diff <= tol and (prev_diff is None or prev_diff <= 10.0 * tol):
                return s, True
            prev_diff = diff
        prev = s
    return (last if last is not None else 0j), False


def _semi_infinite(f, a, max_level=7):
    """integral_a^inf f(x) dx  via  x = a + t/(1-t), t in [0,1)."""
    def g(t):
        omt = 1.0 - t
        if omt <= 0.0:
            return 0j
        return f(a + t / omt) / (omt * omt)

    v, conv = tanh_sinh(g, 0.0, 1.0, max_level=max_level)
    return v, conv


def _tanh_sinh_pair(f, a, b, max_level=7):
    """Integrate over [a,b] by splitting at the midpoint and running tanh-sinh
    on each half, the right half mirrored (x = b - s).

    Why: the stable abscissa of tanh-sinh can resolve points within ~1e-300 of
    a *zero* endpoint, but an endpoint at b != 0 only down to one ulp of b
    (~1e-16 relative).  For an integrable singularity such as ``1/sqrt(1-x)``
    that missing sliver still carries ~1e-8 of the integral.  Mirroring maps
    the right endpoint onto 0, so both endpoint singularities are resolved.
    """
    mid = 0.5 * (a + b)
    span = mid - a
    left, cl = tanh_sinh(f, a, mid, max_level=max_level)
    right, cr = tanh_sinh(lambda s: f(b - s), 0.0, span,
                          max_level=max_level)
    return left + right, (cl and cr)


def integrate_1d(f, lo, hi, ctx, max_level=7, check_gl=True):
    """Numerically integrate f over [lo, hi] (hi may be +inf)."""
    lo = float(lo)
    hi = float(hi)
    if lo == hi:
        return 0j
    if lo > hi:
        return -integrate_1d(f, hi, lo, ctx, max_level=max_level,
                             check_gl=check_gl)
    if math.isinf(hi):
        if math.isinf(lo):
            raise EvalError("不支持 (-inf, +inf) 上的积分")
        val, conv = _semi_infinite(f, lo, max_level=max_level)
        if not _finite(val):
            raise EvalError("无穷区间积分发散")
        if not conv:
            ctx.warn("半无穷积分未达到收敛目标")
        return val
    if math.isinf(lo):
        val, conv = _semi_infinite(lambda s: f(hi - s), 0.0,
                                   max_level=max_level)
        return val

    val, conv = tanh_sinh(f, lo, hi, max_level=max_level)
    if not _finite(val):
        val = None
    if check_gl and (not conv or val is None):
        gl, gl_ok = adaptive_gauss(f, lo, hi, tol=1e-11, max_depth=10)
        if _finite(gl) and gl_ok:
            if val is None or abs(gl - val) <= 1e-6 * max(1.0, abs(gl)):
                return gl
        if val is None:
            raise EvalError("数值积分失败")
    if val is None:
        raise EvalError("数值积分失败")
    return val


# ---------------------------------------------------------------------------
# number theory
# ---------------------------------------------------------------------------
_SIEVE = {}


def primes_upto(n):
    if n in _SIEVE:
        return _SIEVE[n]
    if n < 2:
        return []
    flags = bytearray(b"\x01") * (n + 1)
    flags[0:2] = b"\x00\x00"
    for i in range(2, int(n ** 0.5) + 1):
        if flags[i]:
            start = i * i
            flags[start::i] = bytearray(len(flags[start::i]))
    out = [i for i in range(2, n + 1) if flags[i]]
    _SIEVE[n] = out
    return out


def _factorize(n):
    if n <= 0:
        raise EvalError("数论函数只对正整数有定义")
    f = {}
    d = 2
    while d * d <= n:
        while n % d == 0:
            f[d] = f.get(d, 0) + 1
            n //= d
        d += 1 if d == 2 else 2
    if n > 1:
        f[n] = f.get(n, 0) + 1
    return f


def mobius(n):
    f = _factorize(n)
    if any(e > 1 for e in f.values()):
        return 0
    return -1 if len(f) % 2 else 1


def euler_phi(n):
    if n <= 0:
        raise EvalError("varphi 只对正整数有定义")
    result = n
    for p in _factorize(n):
        result -= result // p
    return result


# Bernoulli numbers B_2j / (2j)!  for the Euler-Maclaurin formula
_B_OVER_FACT = [Fraction(1, 12), Fraction(-1, 720), Fraction(1, 30240),
                Fraction(-1, 1209600), Fraction(1, 47900160)]


def zeta(s):
    """Riemann zeta for real s > 1 via Euler-Maclaurin."""
    if s <= 1.0:
        raise EvalError("zeta(s) 只在 s>1 时数值收敛")
    N = 40
    total = math.fsum(n ** (-s) for n in range(1, N))
    total += N ** (1.0 - s) / (s - 1.0) + 0.5 * N ** (-s)
    deriv = s
    for j, b in enumerate(_B_OVER_FACT, start=1):
        total += float(b) * deriv * N ** (-s - (2 * j - 1))
        deriv *= (s + (2 * j - 1)) * (s + 2 * j)
    return total


def _em_tail(a, p):
    """sum_{k=a}^inf k**p for p < -1, a >= 2 (Euler-Maclaurin tail)."""
    s = -a ** (p + 1.0) / (p + 1.0) + 0.5 * a ** p
    deriv = p
    for j, b in enumerate(_B_OVER_FACT, start=1):
        s -= float(b) * deriv * a ** (p - (2 * j - 1))
        deriv *= (p - (2 * j - 1)) * (p - 2 * j)
    return s


def _harmonic(n):
    if n <= 0:
        return 0.0
    if n < 50:
        return math.fsum(1.0 / k for k in range(1, n + 1))
    inv = 1.0 / n
    return (math.log(n) + 0.5772156649015329 + 0.5 * inv
            - inv ** 2 / 12.0 + inv ** 4 / 120.0)


def sum_power(a, b, p):
    """sum_{k=a}^{b} k**p, b may be math.inf.  p is a Fraction/float."""
    p = float(p)
    a = int(a)
    finite_upper = not (b is None or (isinstance(b, float) and math.isinf(b)))
    if finite_upper:
        b = int(b)
        if b < a:
            return 0.0
        if b <= 200:
            return math.fsum(k ** p for k in range(a, b + 1))
        if abs(p + 1.0) < 1e-15:
            return _harmonic(b) - _harmonic(a - 1)
        if p >= -1.0:
            # converges slowly or diverges: direct summation is cheap enough
            if b <= 200000:
                return math.fsum(k ** p for k in range(a, b + 1))
            return (b ** (p + 1.0) - (a - 1) ** (p + 1.0)) / (p + 1.0)
        # p < -1: Euler-Maclaurin for the tail beyond b
        if a >= 2:
            return _em_tail(a, p) - _em_tail(b + 1, p)
        return zeta(-p) - _em_tail(b + 1, p)
    # infinite upper limit
    if p >= -1.0:
        raise EvalError("p 级数在 p<=-1 时发散")
    if a >= 2:
        return _em_tail(a, p)
    return zeta(-p)


# ---------------------------------------------------------------------------
# extrapolation
# ---------------------------------------------------------------------------
def neville(hs, vs):
    """Polynomial interpolation at x=0 through (hs[i], vs[i])."""
    n = len(hs)
    tab = [complex(v) for v in vs]
    for k in range(1, n):
        for i in range(n - 1, k - 1, -1):
            x_i, x_imk = hs[i], hs[i - k]
            tab[i] = (tab[i] * (-x_imk) - tab[i - 1] * (-x_i)) / (x_i - x_imk)
    return tab[-1]


def extrapolate(hs, vs, rtol=1e-13):
    """Neville extrapolation, growing the number of nodes until it settles."""
    if len(hs) < 3:
        return complex(vs[-1])
    best = None
    for n in range(3, len(hs) + 1):
        est = neville(hs[:n], vs[:n])
        if best is not None and abs(est - best) <= rtol * max(1.0, abs(est)):
            return est
        best = est
    return best


# ---------------------------------------------------------------------------
# series
# ---------------------------------------------------------------------------
_SERIES_NMAX = 2048
_SERIES_MARKS = (64, 128, 256, 512, 1024, 2048)


def _series_tiny(term_value, S):
    """Break out of a sum when the current term is far below the partial sum."""
    return abs(term_value) < 1e-17 * max(1.0, abs(S))


def _looks_alternating(term, k0, count=10):
    vals = []
    for i in range(count):
        try:
            vals.append(complex(term(k0 + i)))
        except EvalError:
            return False
    if any(abs(v.imag) > 1e-12 * max(1.0, abs(v.real)) for v in vals):
        return False
    mags = [abs(v.real) for v in vals]
    for i in range(count - 1):
        a, b = vals[i].real, vals[i + 1].real
        if a == 0.0 or b == 0.0 or (a > 0) == (b > 0):
            return False
        if mags[i] < mags[i + 1]:
            return False
    return True


def euler_alternating(term, k0, terms=45):
    """Euler transformation of an alternating series.

    sum_{n>=0} (-1)^n a_n = sum_j (-1)^j Delta^j a_0 / 2^(j+1)
    """
    sign0 = 1.0 if term(k0).real > 0 else -1.0
    a = [abs(complex(term(k0 + n)).real) for n in range(terms + 1)]
    total = 0.0
    sign = 1.0
    for j in range(terms + 1):
        total += sign * a[0] * (2.0 ** (-(j + 1)))
        sign = -sign
        for i in range(terms - j):
            a[i] = a[i + 1] - a[i]
    return complex(sign0 * total)


def series_sum(term, k0, ctx):
    """sum_{k=k0}^inf term(k), with tail extrapolation."""
    # alternating series: Euler transformation (handles 1/n, 1/(2n+1), ...)
    if _looks_alternating(term, k0):
        ctx.identity("交错级数 Euler 变换")
        return euler_alternating(term, k0)

    S = 0j
    marks = []
    nxt = 0
    tiny_run = 0
    last_est = None
    for i in range(_SERIES_NMAX):
        try:
            t = complex(term(k0 + i))
        except OverflowError:
            t = 0j
        if not _finite(t):
            raise EvalError("级数项不是有限数")
        S += t
        if nxt < len(_SERIES_MARKS) and i + 1 == _SERIES_MARKS[nxt]:
            marks.append((1.0 / _SERIES_MARKS[nxt], S))
            nxt += 1
            if len(marks) >= 4:
                est = neville([h for h, _ in marks[-4:]],
                              [v for _, v in marks[-4:]])
                if last_est is not None and \
                        abs(est - last_est) <= 1e-12 * max(1.0, abs(est)):
                    return est
                last_est = est
        if _series_tiny(t, S):
            tiny_run += 1
            if tiny_run >= 4:
                return S
        else:
            tiny_run = 0
        if abs(S) > 1e18:
            raise EvalError("级数发散")

    if len(marks) >= 3:
        hs = [h for h, _ in marks]
        vs = [v for _, v in marks]
        return extrapolate(hs, vs)
    raise EvalError("级数不收敛")


def series_prod(term, k0, ctx):
    """prod_{k=k0}^inf term(k)."""
    P = _ONE
    marks = []
    nxt = 0
    tiny_run = 0
    last_est = None
    for i in range(_SERIES_NMAX):
        try:
            t = complex(term(k0 + i))
        except OverflowError:
            t = 1 + 0j
        if not _finite(t):
            raise EvalError("乘积因子不是有限数")
        P *= t
        if nxt < len(_SERIES_MARKS) and i + 1 == _SERIES_MARKS[nxt]:
            marks.append((1.0 / _SERIES_MARKS[nxt], P))
            nxt += 1
            if len(marks) >= 4:
                est = neville([h for h, _ in marks[-4:]],
                              [v for _, v in marks[-4:]])
                if last_est is not None and \
                        abs(est - last_est) <= 1e-12 * max(1.0, abs(est)):
                    return est
                last_est = est
        if abs(t - 1.0) < 1e-17:
            tiny_run += 1
            if tiny_run >= 4:
                return P
        else:
            tiny_run = 0
    if len(marks) >= 3:
        hs = [h for h, _ in marks]
        vs = [v for _, v in marks]
        return extrapolate(hs, vs)
    raise EvalError("无穷乘积不收敛")


# ---------------------------------------------------------------------------
# evaluation of the AST
# ---------------------------------------------------------------------------
_MISS = object()
_EXPENSIVE_NODES = (Limit, Integral, Sum, Prod, Det, Contour)


def _eval(node, env, ctx):
    ctx.depth += 1
    if ctx.depth > 400:
        ctx.depth -= 1
        raise EvalError("表达式嵌套过深")
    try:
        if isinstance(node, _EXPENSIVE_NODES) and not _fv(node, ctx):
            # A closed sub-expression (an integral/sum/limit/determinant with no
            # free variable) has the same value everywhere: evaluate it once.
            # This matters a lot when a limit is sampled at nine points and its
            # body contains an expensive but n-independent sub-expression.
            cached = getattr(node, "_value_cache", _MISS)
            if cached is not _MISS:
                return cached
            value = _eval_inner(node, env, ctx)
            node._value_cache = value
            return value
        return _eval_inner(node, env, ctx)
    finally:
        ctx.depth -= 1


def _eval_inner(node, env, ctx):
    if isinstance(node, Num):
        return complex(node.value)
    if isinstance(node, Const):
        if node.name == "pi":
            return complex(math.pi)
        if node.name == "e":
            return complex(math.e)
        if node.name == "i":
            return 1j
        if node.name == "infty":
            return complex(math.inf)
        raise EvalError("未知常量 %s" % node.name)

    if isinstance(node, Var):
        key = node.key
        if key in env:
            return complex(env[key])
        raise EvalError("变量 %s 没有绑定（自由变量）" % key)

    if isinstance(node, Add):
        return _cfsum([_eval(t, env, ctx) for t in node.terms])

    if isinstance(node, Mul):
        product = _ONE
        for f in node.factors:
            product *= _eval(f, env, ctx)
        return product

    if isinstance(node, Neg):
        return -_eval(node.arg, env, ctx)

    if isinstance(node, Abs):
        z = _eval(node.arg, env, ctx)
        return complex(abs(z))

    if isinstance(node, DetProd):
        prod = 1.0 + 0.0j
        for m in node.mats:
            prod *= _eval(m, env, ctx)
        return prod

    if isinstance(node, Deriv):
        key = node.var
        if key not in env:
            raise EvalError("导数变量 %s 没有绑定" % key)
        x0 = float(env[key].real if isinstance(env[key], complex) else env[key])
        h = 1e-4 * max(1.0, abs(x0))

        def _at(t):
            e2 = dict(env)
            e2[key] = t
            return _eval(node.body, e2, ctx)

        return (_at(x0 - 2.0 * h) - 8.0 * _at(x0 - h)
                + 8.0 * _at(x0 + h) - _at(x0 + 2.0 * h)) / (12.0 * h)

    if isinstance(node, Div):
        a = _eval(node.num, env, ctx)
        b = _eval(node.den, env, ctx)
        if b == 0:
            raise EvalError("除以零")
        return a / b

    if isinstance(node, Pow):
        return _pow_val(_eval(node.base, env, ctx), _eval(node.exp, env, ctx))

    if isinstance(node, Fact):
        return _factorial_value(_eval(node.arg, env, ctx), node.times)

    if isinstance(node, Mod):
        return _eval_mod(node, env, ctx)

    if isinstance(node, Func):
        val = _apply_func(node.name, _eval(node.arg, env, ctx))
        if node.power is not None:
            val = _pow_val(val, _eval(node.power, env, ctx))
        return val

    if isinstance(node, Integral):
        return _eval_integral(node, env, ctx)

    if isinstance(node, (Sum, Prod)):
        return _eval_series(node, env, ctx)

    if isinstance(node, Limit):
        return _eval_limit(node, env, ctx)

    if isinstance(node, Floor):
        r = rat(node.arg)
        if r is not None:
            return complex(math.floor(r))
        return complex(math.floor(_real(_eval(node.arg, env, ctx), "floor 的自变量")))

    if isinstance(node, Ceil):
        r = rat(node.arg)
        if r is not None:
            return complex(math.ceil(r))
        return complex(math.ceil(_real(_eval(node.arg, env, ctx), "ceil 的自变量")))

    if isinstance(node, FracPart):
        r = rat(node.arg)
        if r is not None:
            return complex(float(r - math.floor(r)))
        x = _real(_eval(node.arg, env, ctx), "小数部分的自变量")
        return complex(x - math.floor(x))

    if isinstance(node, Zeta):
        return complex(zeta(_real(_eval(node.arg, env, ctx), "zeta 的自变量")))

    if isinstance(node, Mobius):
        return complex(mobius(_as_int(_eval(node.arg, env, ctx), "mu 的自变量")))

    if isinstance(node, Phi):
        return complex(euler_phi(_as_int(_eval(node.arg, env, ctx), "varphi 的自变量")))

    if isinstance(node, Det):
        return _eval_det(node, env, ctx)

    if isinstance(node, Contour):
        return _eval_contour(node, env, ctx)

    raise EvalError("未知的语法树节点 %r" % (node,))


# ---------------------------------------------------------------------------
# compiled evaluator (fast path used inside quadrature and series)
# ---------------------------------------------------------------------------
def _fv(node, ctx):
    """free_vars(node), memoised on the node object itself.

    (Caching by id() would be wrong: the evaluator builds short-lived temporary
    nodes -- ``mul(*factors)``, ``add(*terms)`` -- whose ids are recycled, so an
    id-keyed cache goes stale and would report a bound variable as free.)
    """
    cached = getattr(node, "_fv_cache", None)
    if cached is None:
        cached = free_vars(node)
        try:
            node._fv_cache = cached
        except AttributeError:
            pass
    return cached


def _compile(node, ctx, fold=True):
    """Compile an AST into a closure ``f(env) -> complex``.

    The arithmetic spine becomes nested Python closures instead of going
    through the recursive isinstance interpreter, and every subtree that does
    not depend on a variable is folded to a constant once.  Nodes that need
    the interpreter (sums, integrals, limits, number-theoretic calls, ...)
    fall back to :func:`_eval`.
    """
    if fold and not _fv(node, ctx):
        value = _eval(node, {}, ctx)
        return lambda env: value

    if isinstance(node, Num):
        v = complex(node.value)
        return lambda env: v
    if isinstance(node, Const):
        v = _eval(node, {}, ctx)
        return lambda env: v
    if isinstance(node, Var):
        key = node.key

        def leaf(env, key=key):
            try:
                return complex(env[key])
            except KeyError:
                raise EvalError("变量 %s 没有绑定（自由变量）" % key)
        return leaf
    if isinstance(node, Add):
        fs = tuple(_compile(t, ctx) for t in node.terms)

        def f_add(env, fs=fs):
            return _cfsum([fn(env) for fn in fs])
        return f_add
    if isinstance(node, Mul):
        fs = tuple(_compile(t, ctx) for t in node.factors)

        def f_mul(env, fs=fs):
            product = _ONE
            for fn in fs:
                product *= fn(env)
            return product
        return f_mul
    if isinstance(node, Neg):
        f0 = _compile(node.arg, ctx)
        return lambda env: -f0(env)
    if isinstance(node, Abs):
        return lambda env: _eval(node, env, ctx)
    if isinstance(node, DetProd):
        return lambda env: _eval(node, env, ctx)
    if isinstance(node, Deriv):
        return lambda env: _eval(node, env, ctx)

    if isinstance(node, Div):
        fn = _compile(node.num, ctx)
        fd = _compile(node.den, ctx)

        def f_div(env, fn=fn, fd=fd):
            b = fd(env)
            if b == 0:
                raise EvalError("除以零")
            return fn(env) / b
        return f_div
    if isinstance(node, Pow):
        fb = _compile(node.base, ctx)
        fe = _compile(node.exp, ctx)
        return lambda env: _pow_val(fb(env), fe(env))
    if isinstance(node, Fact):
        fa = _compile(node.arg, ctx)
        times = node.times
        return lambda env: _factorial_value(fa(env), times)
    if isinstance(node, Func):
        fa = _compile(node.arg, ctx)
        name = node.name
        if node.power is None:
            return lambda env: _apply_func(name, fa(env))
        fp = _compile(node.power, ctx)
        return lambda env: _pow_val(_apply_func(name, fa(env)), fp(env))

    return lambda env: _eval(node, env, ctx)


# ---------------------------------------------------------------------------
# determinants (exact, Fraction based)
# ---------------------------------------------------------------------------
def det_fraction(rows):
    n = len(rows)
    a = [[Fraction(x) for x in row] for row in rows]
    det = Fraction(1)
    for k in range(n):
        pivot = None
        for i in range(k, n):
            if a[i][k] != 0:
                pivot = i
                break
        if pivot is None:
            return Fraction(0)
        if pivot != k:
            a[k], a[pivot] = a[pivot], a[k]
            det = -det
        pv = a[k][k]
        det *= pv
        for i in range(k + 1, n):
            if a[i][k] == 0:
                continue
            f = a[i][k] / pv
            for j in range(k, n):
                a[i][j] -= f * a[k][j]
    return det


def _eval_det(node, env, ctx):
    rows = []
    for row in node.rows:
        out = []
        for entry in row:
            r = rat(entry)
            if r is None:
                # fall back to a numeric entry (must still be an integer-like)
                v = _real(_eval(entry, env, ctx), "行列式元素")
                out.append(Fraction(v).limit_denominator(10 ** 9))
            else:
                out.append(r)
        rows.append(out)
    return complex(float(det_fraction(rows)))


# ---------------------------------------------------------------------------
# contour integrals (residue theorem)
# ---------------------------------------------------------------------------
def _poly(node, var):
    """Coefficient list (low -> high) of a polynomial in *var*, or None."""
    if isinstance(node, Num):
        return [complex(node.value)]
    if isinstance(node, Var):
        return [0j, 1 + 0j] if node.key == var else None
    if isinstance(node, Neg):
        p = _poly(node.arg, var)
        return [-c for c in p] if p is not None else None
    if isinstance(node, Add):
        total = [0j]
        for t in node.terms:
            p = _poly(t, var)
            if p is None:
                return None
            if len(p) > len(total):
                total.extend([0j] * (len(p) - len(total)))
            for i, c in enumerate(p):
                total[i] += c
        return total
    if isinstance(node, Mul):
        total = [1 + 0j]
        for f in node.factors:
            p = _poly(f, var)
            if p is None:
                return None
            out = [0j] * (len(total) + len(p) - 1)
            for i, a in enumerate(total):
                for j, b in enumerate(p):
                    out[i + j] += a * b
            total = out
        return total
    if isinstance(node, Div):
        p = _poly(node.num, var)
        q = _poly(node.den, var)
        if p is None or q is None or len(q) != 1:
            return None
        return [c / q[0] for c in p]
    if isinstance(node, Pow):
        e = rat(node.exp)
        if e is None or e.denominator != 1 or e < 0:
            return None
        base = _poly(node.base, var)
        if base is None:
            return None
        out = [1 + 0j]
        for _ in range(int(e)):
            prod = [0j] * (len(out) + len(base) - 1)
            for i, a in enumerate(out):
                for j, b in enumerate(base):
                    prod[i + j] += a * b
            out = prod
        return out
    return None


def _poly_eval(p, z):
    total = 0j
    for c in reversed(p):
        total = total * z + c
    return total


def _poly_deriv(p):
    return [i * p[i] for i in range(1, len(p))]


def _eval_contour(node, env, ctx):
    var = node.var
    body = node.body
    if not isinstance(body, Div):
        raise EvalError("围道积分只支持有理函数被积式")
    p = _poly(body.num, var)
    q = _poly(body.den, var)
    if p is None or q is None:
        raise EvalError("围道积分：无法把被积式写成多项式之比")
    while q and q[-1] == 0:
        q.pop()
    deg = len(q) - 1
    if deg <= 0:
        raise EvalError("围道积分：分母不是非常数多项式")
    if deg == 1:
        roots = [-q[0] / q[1]]
    elif deg == 2:
        a, b, c = q[2], q[1], q[0]
        disc = cmath.sqrt(b * b - 4 * a * c)
        roots = [(-b + disc) / (2 * a), (-b - disc) / (2 * a)]
    else:
        raise EvalError("围道积分：只支持分母次数 <= 2 的有理函数")
    dq = _poly_deriv(q)
    total = 0j
    for root in roots:
        if abs(_poly_eval(dq, root)) < 1e-15:
            raise EvalError("围道积分：遇到高阶极点")
        total += _poly_eval(p, root) / _poly_eval(dq, root)
    ctx.identity("留数定理 ∮ f(z)dz = 2πi Σ Res")
    return 2j * math.pi * total


# ---------------------------------------------------------------------------
# integrals: analytic identities
# ---------------------------------------------------------------------------
def _remove_power(factors, var, want, ctx):
    """Remove one factor equal to var**want from *factors*; return rest/None."""
    for i, f in enumerate(factors):
        if _as_power_of(f, var, ctx) == want:
            return factors[:i] + factors[i + 1:]
    return None


def _signed_cos(node):
    if isinstance(node, Func) and node.name == "cos" and node.power is None:
        return 1, node.arg
    if isinstance(node, Neg):
        inner = node.arg
        if isinstance(inner, Func) and inner.name == "cos" and inner.power is None:
            return -1, inner.arg
    return None, None


def _sin_squared_arg(node):
    if isinstance(node, Func) and node.name == "sin" and node.power is not None:
        e = rat(node.power)
        if e == 2:
            return node.arg
        return None
    if isinstance(node, Pow) and isinstance(node.base, Func) \
            and node.base.name == "sin" and node.base.power is None:
        if rat(node.exp) == 2:
            return node.base.arg
    return None


def _match_integral_identity(node, var):
    """Try the documented closed forms.  Returns a value or None."""
    body = node.body

    # ---- int_a^b x^p dx = (b^(p+1)-a^(p+1))/(p+1)   (p > -1) -------------
    p = _as_power_of(body, var, None)
    if p is not None and node.lo is not None and node.hi is not None:
        lo_v = rat(node.lo)
        hi_v = rat(node.hi)
        if lo_v is not None and hi_v is not None and p > -1:
            return ("power", lo_v, hi_v, p)

    return None


def _bound_value(node, env, ctx, default):
    """Evaluate an integration limit; infinity is allowed."""
    if node is None:
        return default
    v = complex(_eval(node, env, ctx))
    if abs(v.imag) > 1e-9 * max(1.0, abs(v.real)):
        raise EvalError("积分限不是实数")
    r = v.real
    if math.isnan(r):
        raise EvalError("积分限无效")
    return r


def _unwrap(node):
    """Drop unit factors so that matching sees the essential structure."""
    if isinstance(node, Mul):
        fs = [f for f in node.factors if rat(f) != 1]
        if len(fs) == 1:
            return fs[0]
        return Mul(fs)
    return node


def _match_arcsin(vpart, var, lo, hi, env, ctx):
    """int dx/sqrt(b^2-x^2) = asin(x/b)  (elementary calculus)."""
    num = den = None
    if isinstance(vpart, Div):
        num, den = vpart.num, vpart.den
    elif isinstance(vpart, Pow) and rat(vpart.exp) == -1:
        num, den = Num("1", 1.0), vpart.base
    if num is None or rat(num) != 1:
        return None
    if not (isinstance(den, Func) and den.name == "sqrt" and den.power is None):
        return None
    arg = den.arg
    if not isinstance(arg, Add):
        return None
    b2 = None
    saw_x2 = False
    for t in arg.terms:
        if isinstance(t, Pow) and _is_var(t.base, var) and rat(t.exp) == 2:
            saw_x2 = True
            continue
        if isinstance(t, Neg) and isinstance(t.arg, Pow) \
                and _is_var(t.arg.base, var) and rat(t.arg.exp) == 2:
            saw_x2 = True
            continue
        if b2 is not None:
            return None
        b2 = _eval(t, env, ctx)
    if not saw_x2 or b2 is None:
        return None
    root = cmath.sqrt(b2)
    if abs(root.imag) > 1e-12 or root.real <= 0.0:
        return None
    b = root.real

    def asin_ratio(x):
        r = x / b
        if r > 1.0 + 1e-12 or r < -1.0 - 1e-12:
            return None
        return math.asin(max(-1.0, min(1.0, r)))

    a_lo = asin_ratio(lo)
    a_hi = asin_ratio(hi)
    if a_lo is None or a_hi is None:
        return None
    ctx.identity("arcsin 积分 ∫ dx/√(b²-x²) = arcsin(x/b)")
    return complex(a_hi - a_lo)


def _match_classic_infinite(vpart, var, env, ctx):
    """The three conditionally convergent classics on [0, inf)."""
    factors = _factors(vpart)
    rest = _remove_power(factors, var, Fraction(-1), ctx)
    if rest is not None:
        num = mul(*rest) if rest else Num("1", 1.0)
        # int_0^inf sin(a x)/x dx = pi/2
        if isinstance(num, Func) and num.name == "sin" and num.power is None:
            a = _scaled_var(num.arg, var, env, ctx)
            if a is not None and abs(a.imag) < 1e-14 and a.real > 0:
                ctx.identity("Dirichlet 积分 ∫_0^∞ sin(αx)/x dx = π/2")
                return complex(HALF_PI)
        # int_0^inf (cos(a x)-cos(b x))/x dx = ln(b/a)
        if isinstance(num, Add) and len(num.terms) == 2:
            s1, a1 = _signed_cos(num.terms[0])
            s2, a2 = _signed_cos(num.terms[1])
            if s1 is not None and s2 is not None and s1 == -s2:
                A = _scaled_var(a1, var, env, ctx)
                B = _scaled_var(a2, var, env, ctx)
                if A is not None and B is not None:
                    aa = A.real if s1 > 0 else B.real
                    bb = B.real if s1 > 0 else A.real
                    if aa > 0 and bb > 0:
                        ctx.identity("Frullani 积分 ∫_0^∞ (cos αx-cos βx)/x dx = ln(β/α)")
                        return complex(math.log(bb / aa))
    rest2 = _remove_power(factors, var, Fraction(-2), ctx)
    if rest2 is not None:
        num = mul(*rest2) if rest2 else Num("1", 1.0)
        arg = _sin_squared_arg(num)
        if arg is not None:
            a = _scaled_var(arg, var, env, ctx)
            if a is not None and abs(a.imag) < 1e-14 and a.real > 0:
                ctx.identity("∫_0^∞ sin²(αx)/x² dx = πα/2")
                return complex(math.pi * a.real / 2.0)
    return None


def _match_beta_half(vpart, var, lo, hi, env, ctx):
    """int_0^1 dx/sqrt(c*x*(1-x)) = pi/sqrt(c).

    Substituting x = sin^2(theta) turns this into 2 int_0^(pi/2) dtheta = pi.
    (The integrand is singular at *both* ends, one of which is a non-zero
    abscissa, so this elementary closed form is used instead of quadrature.)
    """
    num = den = None
    if isinstance(vpart, Div):
        num, den = vpart.num, vpart.den
    elif isinstance(vpart, Pow) and rat(vpart.exp) == -1:
        num, den = Num("1", 1.0), vpart.base
    if num is None or rat(num) != 1:
        return None
    if not (isinstance(den, Func) and den.name == "sqrt" and den.power is None):
        return None
    p = _poly(den.arg, var)
    if p is None or len(p) != 3:
        return None
    c0, c1, c2 = p
    if abs(c0) > 1e-15 or abs(c1 + c2) > 1e-12:
        return None
    if abs(c1.imag) > 1e-15 or c1.real <= 0.0:
        return None
    if abs(lo) > 1e-15 or abs(hi - 1.0) > 1e-15:
        return None
    ctx.identity("Beta 积分 ∫_0^1 dx/√(x(1-x)) = π")
    return complex(math.pi / math.sqrt(c1.real))


def _match_closed_form(vpart, var, lo, hi, env, ctx):
    vpart = _unwrap(vpart)

    # ---- int_a^b x^p dx = (b^(p+1)-a^(p+1))/(p+1) ------------------------
    p = _as_power_of(vpart, var, ctx)
    if p is not None and not math.isinf(lo) and not math.isinf(hi):
        pf = float(p)
        if pf > -1.0 or lo > 0.0:
            ctx.identity("幂律积分 ∫x^p dx = (b^(p+1)-a^(p+1))/(p+1)")
            pa = pf + 1.0
            if lo == 0.0 and hi == 1.0:
                return complex(1.0 / pa)
            bv = hi ** pa
            av = lo ** pa if lo != 0.0 else 0.0
            return complex((bv - av) / pa)

    v = _match_arcsin(vpart, var, lo, hi, env, ctx)
    if v is not None:
        return v

    v = _match_beta_half(vpart, var, lo, hi, env, ctx)
    if v is not None:
        return v

    if math.isinf(hi) and lo == 0.0:
        v = _match_classic_infinite(vpart, var, env, ctx)
        if v is not None:
            return v
    return None


def _eval_integral(node, env, ctx):
    var_list = node.dims
    lo = _bound_value(node.lo, env, ctx, 0.0)
    hi = _bound_value(node.hi, env, ctx, math.inf)

    if len(var_list) > 1:
        return _integrate_multi(node.body, var_list, lo, hi, env, ctx)

    var = var_list[0]

    # integrand independent of the integration variable
    if var not in free_vars(node.body):
        val = _eval(node.body, env, ctx)
        if math.isinf(hi) or math.isinf(lo):
            if abs(val) < 1e-300:
                return 0j
            raise EvalError("无穷区间上的常数被积函数发散")
        return val * (hi - lo)

    const, vpart = _split_const_factors(node.body, var, env, ctx)
    closed = _match_closed_form(vpart, var, lo, hi, env, ctx)
    if closed is not None:
        return const * closed

    # Fast integrand: compile the arithmetic spine to closures and fold the
    # var-independent parts to constants.
    compiled = _compile(node.body, ctx)
    local = dict(env)

    def f(t):
        local[var] = t
        return compiled(local)

    # nested quadrature gets fewer nodes so that 2-D integrals stay affordable
    depth = ctx.quad_depth
    max_level = 7 if depth == 0 else (5 if depth == 1 else 4)
    ctx.quad_depth += 1
    try:
        return integrate_1d(f, lo, hi, ctx, max_level=max_level)
    finally:
        ctx.quad_depth -= 1


def _integrate_multi(body, dims, lo, hi, env, ctx):
    compiled = _compile(body, ctx)
    base_depth = ctx.quad_depth

    def rec(k, env2):
        if k == len(dims):
            return compiled(env2)
        var = dims[k]
        local = dict(env2)

        def f(t, local=local, var=var, k=k):
            local[var] = float(t)
            return rec(k + 1, local)

        ctx.quad_depth = base_depth + k + 1
        return integrate_1d(f, lo, hi, ctx, max_level=4, check_gl=False)

    ctx.quad_depth = base_depth + len(dims)
    try:
        return rec(0, dict(env))
    finally:
        ctx.quad_depth = base_depth


# ---------------------------------------------------------------------------
# sums and products
# ---------------------------------------------------------------------------
def _split_const_factors(body, var, env, ctx):
    """Split body = (constant part) * (part depending on *var*)."""
    factors = _factors(body)
    const, dep = [], []
    for f in factors:
        if var in free_vars(f):
            dep.append(f)
        else:
            const.append(f)
    c = _eval(mul(*const), env, ctx) if const else _ONE
    vpart = mul(*dep) if dep else Num("1", 1.0)
    return c, vpart


def _eval_series(node, env, ctx):
    is_sum = isinstance(node, Sum)
    var = node.var

    if node.over_primes:
        return _eval_prime_product(node, env, ctx) if not is_sum else \
            _eval_prime_sum(node, env, ctx)

    lo_node = node.lo
    lo = _as_int(_eval(lo_node, env, ctx), "求和/乘积下限") if lo_node is not None else 1
    hi_node = node.hi
    if hi_node is None:
        hi = math.inf
    else:
        h = _eval(hi_node, env, ctx)
        if not _finite(h):
            hi = math.inf
        else:
            hr = _real(h, "求和/乘积上限")
            hi = int(round(hr))
            if abs(hr - hi) > 1e-9 * max(1.0, abs(hr)):
                raise EvalError("求和/乘积上限必须是整数，实际为 %r" % hr)

    # -- closed forms -------------------------------------------------------
    hi_inf = math.isinf(hi)
    if is_sum:
        closed = _match_mobius_sum(node, var, lo, env, ctx)
        if closed is not None:
            return closed
        geom = _match_geometric(body=node.body, var=var, lo=lo, hi=hi,
                                env=env, ctx=ctx)
        if geom is not None:
            return geom

    const, vpart = _split_const_factors(node.body, var, env, ctx)

    # p-series fast path: sum_{k=a}^{b} C * k^p
    p = _as_power_of(vpart, var, ctx)
    if is_sum and p is not None and var not in free_vars(vpart):
        if hi_inf and p >= -1:
            raise EvalError("p 级数发散")
        val = sum_power(lo, hi, p)
        ctx.identity("p 级数 Euler-Maclaurin")
        return const * complex(val)

    compiled = _compile(vpart, ctx)
    local = dict(env)

    def term(k):
        local[var] = float(k)
        return const * compiled(local)

    if not hi_inf:
        if hi < lo:
            return 0j if is_sum else _ONE
        total = _ONE if not is_sum else 0j
        for k in range(lo, hi + 1):
            t = term(k)
            if _finite(t):
                total = total * t if not is_sum else total + t
            if not is_sum and abs(total) > 1e300:
                raise EvalError("乘积溢出")
        return total

    if const == 0 and is_sum:
        return 0j
    if is_sum:
        return series_sum(term, lo, ctx)
    return series_prod(term, lo, ctx)


def _match_geometric(body, var, lo, hi, env, ctx):
    """sum_{k=lo}^{hi} c * r^k  (|r|<1) = c r^lo/(1-r)."""
    factors = _factors(body)
    coeff = []
    ratio = None
    for f in factors:
        if isinstance(f, Pow) and _is_var(f.exp, var):
            base = f.base
            if var in free_vars(base):
                return None
            if ratio is not None:
                return None
            ratio = _eval(base, env, ctx)
        elif var in free_vars(f):
            return None
        else:
            coeff.append(f)
    if ratio is None or abs(ratio) >= 1 or not math.isinf(hi):
        return None
    c = _eval(mul(*coeff), env, ctx) if coeff else _ONE
    ctx.identity("等比级数闭式 Σ r^k = r^a/(1-r)")
    return c * _pow_val(ratio, lo) / (1 - ratio)


def _match_mobius_sum(node, var, lo, env, ctx):
    """sum_{n>=1} mu(n)/n^s = 1/zeta(s)."""
    if lo != 1:
        return None
    body = node.body
    factors = _factors(body)
    mu_arg = None
    exponent = None
    for f in factors:
        if isinstance(f, Mobius):
            mu_arg = f.arg
            continue
        p = _as_power_of(f, var, ctx)
        if p is not None:
            if exponent is not None:
                return None
            exponent = p
            continue
        return None
    if mu_arg is None or not _is_var(mu_arg, var) or exponent is None:
        return None
    s = -float(exponent)
    if s <= 1:
        return None
    ctx.identity("Σ μ(n)/n^s = 1/ζ(s)")
    return complex(1.0 / zeta(s))


def _eval_prime_sum(node, env, ctx):
    raise EvalError("不支持对素数求和")


def _match_euler_product(body, var, env, ctx):
    """prod_p (1-p^-s)^-1 = zeta(s)."""
    if not (isinstance(body, Pow) and rat(body.exp) == -1):
        return None
    base = body.base
    if not isinstance(base, Add) or len(base.terms) != 2:
        return None
    t0, t1 = base.terms
    minus = None
    if isinstance(t1, Neg) and rat(t0) == 1:
        minus = t1.arg
    elif isinstance(t0, Neg) and rat(t1) == 1:
        minus = t0.arg
    if minus is None:
        return None
    if not isinstance(minus, Div) or rat(minus.num) != 1:
        return None
    den = minus.den
    if not (isinstance(den, Pow) and _is_var(den.base, var)):
        return None
    s = rat(den.exp)
    if s is None or float(s) <= 1:
        return None
    ctx.identity("Euler 乘积 ∏_p (1-p^-s)^-1 = ζ(s)")
    return complex(zeta(float(s)))


def _eval_prime_product(node, env, ctx):
    var = node.var
    closed = _match_euler_product(node.body, var, env, ctx)
    if closed is not None:
        return closed

    # numeric Euler product over primes up to 2e6 with a tail estimate
    P = 2000000
    primes = primes_upto(P)
    total = 0j
    for p in primes:
        val = _eval(node.body, dict(env, **{var: float(p)}), ctx)
        if val == 0:
            return 0j
        total += cmath.log(val)
    # tail bound: sum_{p>P} 1/p^2 ~ E1(ln P) ~ e^-lnP/(lnP) = 1/(P ln P)
    tail = 1.0 / (P * math.log(P))
    total += tail
    ctx.identity("Euler 乘积数值（素数筛 + 尾部估界）")
    return cmath.exp(total)


# ---------------------------------------------------------------------------
# limits
# ---------------------------------------------------------------------------
def _limit_pattern(node, var, target_inf, env, ctx):
    """Recognise the documented closed-form limits.  Returns (value, name)."""
    # (1 + a/n)^n -> e^a   for n -> inf
    if target_inf:
        if isinstance(node, Pow) and _is_var(node.exp, var):
            base = node.base
            if isinstance(base, Add):
                one = None
                over = None
                for t in base.terms:
                    if rat(t) == 1:
                        one = t
                    else:
                        over = t
                if one is not None and over is not None:
                    a = _a_over(node=over, var=var, env=env, ctx=ctx)
                    if a is not None:
                        return complex(cmath.exp(a)), "lim (1+a/n)^n = e^a"
        # n/(n+c) and (n+c)/n
        if isinstance(node, Div):
            num, den = node.num, node.den
            if _is_var(num, var) and isinstance(den, Add):
                others = [t for t in den.terms if not _is_var(t, var)]
                if len(others) == len(den.terms) - 1 and \
                        all(var not in free_vars(o) for o in others):
                    return _ONE, "lim n/(n+c) = 1"
            if _is_var(den, var) and isinstance(num, Add):
                others = [t for t in num.terms if not _is_var(t, var)]
                if len(others) == len(num.terms) - 1 and \
                        all(var not in free_vars(o) for o in others):
                    return _ONE, "lim (n+c)/n = 1"
        # exp(a/n) -> 1
        if isinstance(node, Func) and node.name == "exp" and node.power is None:
            a = _a_over(node.arg, var, env, ctx)
            if a is not None:
                return _ONE, "lim exp(a/n) = 1"
    else:
        # sin(a x)/x -> a
        if isinstance(node, Div) and _is_var(node.den, var):
            num = node.num
            if isinstance(num, Func) and num.name == "sin" and num.power is None:
                a = _scaled_var(num.arg, var, env, ctx)
                if a is not None:
                    return a, "lim sin(ax)/x = a"
        # (cos(a x)-1)/x^2 -> -a^2/2 : handled numerically
        # sqrt / exp etc. fall through

    # (2/N) sum_{k=1}^N {k sqrt(p)} -> 1   (equidistribution, Weyl)
    if target_inf and isinstance(node, Mul):
        recip = None
        the_sum = None
        rest_const = True
        for f in node.factors:
            if isinstance(f, Div) and rat(f.num) == 2 and _is_var(f.den, var):
                recip = f
            elif isinstance(f, Sum) and f.var not in (var,) and f.hi is not None \
                    and _is_var(f.hi, var):
                the_sum = f
            elif var in free_vars(f):
                rest_const = False
        if recip is not None and the_sum is not None and rest_const:
            if isinstance(the_sum.body, FracPart):
                arg = the_sum.body.arg
                k = None
                for f in _factors(arg):
                    if _is_var(f, the_sum.var):
                        continue
                    k = f
                if k is not None and isinstance(k, Func) and k.name == "sqrt" \
                        and rat(k.arg) is not None:
                    q = rat(k.arg)
                    root = math.isqrt(int(q))
                    if root * root != int(q):
                        return _ONE, "等分布 lim (2/N)Σ{k√p} = 1"
    return None


def _a_over(node, var, env, ctx):
    """Return a if node == a/var with constant a, else None."""
    if isinstance(node, Div) and _is_var(node.den, var):
        if var not in free_vars(node.num):
            return _eval(node.num, env, ctx)
    if isinstance(node, Mul):
        rest = []
        hits = 0
        for f in node.factors:
            if isinstance(f, Pow) and _is_var(f.base, var) and rat(f.exp) == -1:
                hits += 1
            elif var in free_vars(f):
                return None
            else:
                rest.append(f)
        if hits == 1:
            return _eval(mul(*rest), env, ctx) if rest else _ONE
    return None


def _rewrite_limit(node, var, target_inf, env, ctx):
    """Replace closed-form sub-expressions by their limits (returns new node)."""
    if isinstance(node, (Sum, Prod)):
        if node.var == var:
            return node
        body = _rewrite_limit(node.body, var, target_inf, env, ctx)
        lo = _rewrite_limit(node.lo, var, target_inf, env, ctx) if node.lo else None
        hi = _rewrite_limit(node.hi, var, target_inf, env, ctx) if node.hi else None
        return type(node)(node.var, lo, hi, body, node.over_primes)
    if isinstance(node, Integral):
        if var in node.dims:
            return node
        body = _rewrite_limit(node.body, var, target_inf, env, ctx)
        lo = _rewrite_limit(node.lo, var, target_inf, env, ctx) if node.lo else None
        hi = _rewrite_limit(node.hi, var, target_inf, env, ctx) if node.hi else None
        return Integral(node.kind, lo, hi, node.dims, body)
    if isinstance(node, Limit):
        return node
    if isinstance(node, Contour):
        return node

    hit = _limit_pattern(node, var, target_inf, env, ctx)
    if hit is not None:
        value, name = hit
        ctx.identity(name)
        return Num("%.17g" % value.real, value.real)

    if isinstance(node, Mul):
        return mul(*[_rewrite_limit(f, var, target_inf, env, ctx)
                     for f in node.factors])
    if isinstance(node, Div):
        return Div(_rewrite_limit(node.num, var, target_inf, env, ctx),
                   _rewrite_limit(node.den, var, target_inf, env, ctx))
    if isinstance(node, Add):
        return add(*[_rewrite_limit(t, var, target_inf, env, ctx)
                     for t in node.terms])
    if isinstance(node, Neg):
        return Neg(_rewrite_limit(node.arg, var, target_inf, env, ctx))
    if isinstance(node, Pow):
        return Pow(_rewrite_limit(node.base, var, target_inf, env, ctx),
                   _rewrite_limit(node.exp, var, target_inf, env, ctx))
    if isinstance(node, Func):
        return Func(node.name,
                    _rewrite_limit(node.arg, var, target_inf, env, ctx),
                    node.power)
    return node


def _eval_limit(node, env, ctx):
    var = node.var
    target = _eval(node.target, env, ctx)
    target_inf = not _finite(target)
    body = _rewrite_limit(node.body, var, target_inf, env, ctx)

    if var not in free_vars(body):
        return _eval(body, env, ctx)

    if target_inf:
        points = [64 * (2 ** j) for j in range(9)]      # 64 .. 16384
        hs = [1.0 / n for n in points]
        vs = [_eval(body, dict(env, **{var: float(n)}), ctx) for n in points]
    else:
        t = _real(target, "极限点")
        hs = [2.0 ** (-j) for j in range(3, 12)]
        vs = [_eval(body, dict(env, **{var: t + h}), ctx) for h in hs]
        if any(not _finite(v) for v in vs):
            raise EvalError("极限过程出现非有限值")
    return extrapolate(hs, vs, rtol=1e-12)


# ---------------------------------------------------------------------------
# public API
# ---------------------------------------------------------------------------
def evaluate_latex(latex):
    """Parse and evaluate one LaTeX formula (the trailing ``= claim`` is
    removed first).  Returns (value: complex, ctx: Ctx)."""
    src = strip_claim_suffix(latex)
    ast = parse(src)
    ctx = Ctx()
    value = _eval(ast, {}, ctx)
    return value, ctx


def evaluate_ast(ast):
    ctx = Ctx()
    return _eval(ast, {}, ctx), ctx
