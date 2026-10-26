# -*- coding: utf-8 -*-
"""latex_ast.py -- tokeniser + recursive-descent parser for the LaTeX subset
produced by the Calculate325 formula generator.

This module is an independent re-implementation: it was written from the
mathematical meaning of the notation only (a separate C# implementation in the
project was deliberately not consulted).  It produces a small AST that
``numengine`` evaluates numerically.

Supported constructs
--------------------
numbers (including decimals such as ``0.91``), ``\\pi``, ``e``, ``i``,
``\\infty``, ``+ - * / \\cdot \\times``, implicit multiplication (``2\\pi``,
``0.91x_{1}``), ``\\frac``, ``^``, ``_`` (subscripted variables such as
``x_{1}``, ``N_{1}``, ``k_{1}``), ``\\sqrt``, the functions
``\\sin \\cos \\tan \\ln \\exp \\arcsin`` (and close relatives), function
powers (``\\sin^{2}x``), ``\\left( \\right)`` / ``\\left[ \\right]`` /
``\\left\\{ \\right\\}`` (fractional part) / ``\\left\\lfloor \\right\\rfloor``
/ ``\\left\\lceil \\right\\rceil``, factorials (including the project's
``!!`` = ``(n!)!``), ``\\int \\iint \\iiint \\oint`` with bounds and
differentials, ``\\sum``, ``\\prod`` (including ``\\prod_{p}`` over primes),
``\\lim``, ``\\zeta``, ``\\mu``, ``\\varphi``, and ``\\det`` of a
``pmatrix``.

Two context-sensitive rules mirror how the generator writes formulas:

* an integral's integrand is *the rest of the product in the current layer*
  and is terminated by the differential that follows it; a nested ``\\int``
  therefore consumes the first differential it meets, so the left-most
  ``\\int`` pairs with the right-most differential -- exactly the ordinary
  mathematical convention for ``\\int_a^b\\int_c^d F(x,y)\\,dy\\,dx``;
* a ``\\sum`` / ``\\prod`` / ``\\lim`` body is likewise *the rest of the
  product in the current layer*; it ends at ``+``, ``-`` or a closing
  delimiter.
"""

from __future__ import annotations

import math
from dataclasses import dataclass
from typing import Any, List, Optional


def _diff_var_of(num, den):
    """\frac{d}{dx} 里的分子是 d、分母是 dx 时，返回变量名 x。"""
    if not isinstance(num, Var) or num.name != "d":
        return None
    if isinstance(den, Var):
        return den.name[1:] if den.name.startswith("d") and len(den.name) > 1 else None
    if isinstance(den, Mul) and len(den.factors) == 2:
        a, b = den.factors
        if isinstance(a, Var) and a.name == "d" and isinstance(b, Var):
            return b.name
    return None


class ParseError(Exception):
    """The input is outside the supported LaTeX subset."""


# ---------------------------------------------------------------------------
# AST
# ---------------------------------------------------------------------------
@dataclass
class Node:
    """Base class of every AST node."""


@dataclass
class Num(Node):
    text: str          # decimal literal exactly as written, e.g. "0.91"
    value: float


@dataclass
class Const(Node):
    name: str          # 'pi' | 'e' | 'i' | 'infty'


@dataclass
class Var(Node):
    name: str
    sub: Optional[str] = None

    @property
    def key(self) -> str:
        return self.name if self.sub is None else "%s_%s" % (self.name, self.sub)


@dataclass
class Add(Node):
    terms: List[Node]


@dataclass
class Mul(Node):
    factors: List[Node]


@dataclass
class Neg(Node):
    arg: Node


@dataclass
class Div(Node):
    num: Node
    den: Node


@dataclass
class Abs(Node):
    """|z| 模长（复数取 modulus）。"""
    arg: Node


@dataclass
class Deriv(Node):
    """d/dx 算子（ODE 陷阱）：对 body 关于 var 求导。"""
    var: str
    body: Node


@dataclass
class Pow(Node):
    base: Node
    exp: Node


@dataclass
class Fact(Node):
    arg: Node
    times: int = 1     # 1 -> n!, 2 -> (n!)!  (the project writes "!!")


@dataclass
class Mod(Node):
    """``a mod m`` (also written ``a \\bmod m`` / ``a \\mathrm{mod} m``)."""
    a: Node
    m: Node


@dataclass
class Func(Node):
    name: str
    arg: Node
    power: Optional[Node] = None   # \sin^{2}(x)


@dataclass
class Integral(Node):
    kind: str          # 'int' | 'iint' | 'iiint'
    lo: Optional[Node]
    hi: Optional[Node]
    dims: List[str]    # differential variable of each dimension, innermost first
    body: Node


@dataclass
class Sum(Node):
    var: str
    lo: Optional[Node]
    hi: Optional[Node]
    body: Node
    over_primes: bool = False


@dataclass
class Prod(Node):
    var: str
    lo: Optional[Node]
    hi: Optional[Node]
    body: Node
    over_primes: bool = False


@dataclass
class Limit(Node):
    var: str
    target: Node
    body: Node


@dataclass
class Floor(Node):
    arg: Node


@dataclass
class Ceil(Node):
    arg: Node


@dataclass
class FracPart(Node):
    arg: Node


@dataclass
class Zeta(Node):
    arg: Node


@dataclass
class Mobius(Node):
    arg: Node


@dataclass
class Phi(Node):
    arg: Node


@dataclass
class Det(Node):
    rows: List[List[Node]]


@dataclass
class DetProd(Node):
    """det(A·B·…) = det(A)·det(B)·…"""
    mats: List[Node]


@dataclass
class Contour(Node):
    body: Node
    var: str           # contour integration variable, e.g. 'z'


# ---------------------------------------------------------------------------
# convenience constructors that keep sums/products flat
# ---------------------------------------------------------------------------
def add(*terms: Node) -> Node:
    flat: List[Node] = []
    for t in terms:
        if isinstance(t, Add):
            flat.extend(t.terms)
        else:
            flat.append(t)
    if len(flat) == 1:
        return flat[0]
    return Add(flat)


def mul(*factors: Node) -> Node:
    flat: List[Node] = []
    for f in factors:
        if isinstance(f, Mul):
            flat.extend(f.factors)
        else:
            flat.append(f)
    if len(flat) == 1:
        return flat[0]
    return Mul(flat)


# ---------------------------------------------------------------------------
# Tokeniser
# ---------------------------------------------------------------------------
@dataclass
class Tok:
    kind: str
    value: Any
    pos: int

    def __repr__(self) -> str:      # pragma: no cover - debugging aid
        return "Tok(%s,%r)" % (self.kind, self.value)


# tokens that may start a primary -> implicit multiplication
_OP_CMDS = {"cdot": "*", "times": "*", "div": "/"}
_FUNCS = {
    "sin", "cos", "tan", "cot", "sec", "csc",
    "ln", "log", "exp", "arcsin", "arccos", "arctan",
    "sinh", "cosh", "tanh", "Gamma",
}
_SPACING = {",", "!", ";", ":", " ", "quad", "qquad", "thinspace", "enspace"}
_GREEK_VARS = {"rho", "theta", "alpha", "beta", "gamma", "delta", "lambda",
               "omega", "sigma", "tau", "psi"}
_DELIMS = "()[]|."


def _read_delim(src: str, i: int):
    """Read the delimiter that follows \\left or \\right.

    Returns (kind, value, newpos) where kind is 'left'/'right' and value is the
    normalised delimiter, or (None, None, newpos) when the delimiter itself is
    a command that we parse on its own (\\lfloor, \\lceil, ...).
    """
    n = len(src)
    while i < n and src[i].isspace():
        i += 1
    if i >= n:
        raise ParseError("\\left/\\right 缺少分隔符")
    if src[i] == "\\":
        j = i + 1
        while j < n and src[j].isalpha():
            j += 1
        if j > i + 1:
            name, end = src[i + 1:j], j
        else:
            name, end = src[i + 1], i + 2
        if name in ("lfloor", "lceil"):
            return "cmd", name, end
        if name in ("rfloor", "rceil"):
            return "cmd", name, end
        mapping = {"{": "{", "}": "}", "lbrace": "{", "rbrace": "}",
                   "vert": "|", "Vert": "|", "|": "|"}
        if name in mapping:
            return "delim", mapping[name], end
        raise ParseError("不支持的分隔符 \\%s" % name)
    ch = src[i]
    if ch not in _DELIMS:
        raise ParseError("不支持的分隔符 %r" % ch)
    return "delim", ch, i + 1


def tokenize(src: str) -> List[Tok]:
    toks: List[Tok] = []
    i, n = 0, len(src)
    while i < n:
        ch = src[i]
        if ch.isspace():
            i += 1
            continue

        # ---- commands -----------------------------------------------------
        if ch == "\\":
            if i + 1 < n and src[i + 1] == "\\":
                toks.append(Tok("row", None, i))
                i += 2
                continue
            if i + 1 >= n:
                raise ParseError("输入以孤立的反斜杠结尾")
            if src[i + 1].isalpha():
                j = i + 1
                while j < n and src[j].isalpha():
                    j += 1
                name = src[i + 1:j]
                i = j
            else:
                name = src[i + 1]
                i += 2

            if name in ("left", "right"):
                kind, val, i = _read_delim(src, i)
                if kind == "cmd":
                    toks.append(Tok("cmd", val, i))
                else:
                    toks.append(Tok("left" if name == "left" else "right", val, i))
                continue
            if name in ("begin", "end"):
                j = i
                while j < n and src[j].isspace():
                    j += 1
                if j >= n or src[j] != "{":
                    raise ParseError("\\%s 后面缺少环境名" % name)
                k = src.find("}", j + 1)
                if k < 0:
                    raise ParseError("\\%s 环境名缺少右花括号" % name)
                env = src[j + 1:k].strip()
                i = k + 1
                toks.append(Tok("env" if name == "begin" else "endenv", env, j))
                continue
            if name in ("mathrm", "text", "operatorname", "mathit", "mathbf"):
                # keep the rendered text but drop the wrapper; \mathrm{mod} is
                # the modular-arithmetic operator
                j = i
                while j < n and src[j].isspace():
                    j += 1
                if j < n and src[j] == "{":
                    k = src.find("}", j + 1)
                    if k < 0:
                        raise ParseError("\\%s 缺少右花括号" % name)
                    body = src[j + 1:k]
                    i = k + 1
                    word = body.strip()
                    if word.lower() in ("mod", "bmod"):
                        toks.append(Tok("op", "mod", j))
                        continue
                    for t in tokenize(body):
                        if t.kind != "eof":
                            toks.append(t)
                continue
            if name in ("bmod", "mod"):
                toks.append(Tok("op", "mod", i))
                continue
            if name in _OP_CMDS:
                toks.append(Tok("op", _OP_CMDS[name], i))
                continue
            if name in _SPACING:
                continue
            if name == "pi":
                toks.append(Tok("cmd", "pi", i))
                continue
            if name == "infty":
                toks.append(Tok("cmd", "infty", i))
                continue
            if name in _FUNCS:
                toks.append(Tok("cmd", name, i))
                continue
            if name in _GREEK_VARS:
                toks.append(Tok("name", name, i))
                continue
            if name in ("varphi", "phi"):
                toks.append(Tok("cmd", "varphi", i))
                continue
            if name == "mu":
                toks.append(Tok("cmd", "mu", i))
                continue
            if name == "zeta":
                toks.append(Tok("cmd", "zeta", i))
                continue
            if name in ("frac", "dfrac", "tfrac"):
                toks.append(Tok("cmd", "frac", i))
                continue
            if name == "sqrt":
                toks.append(Tok("cmd", "sqrt", i))
                continue
            if name in ("sum", "prod", "lim"):
                toks.append(Tok("cmd", name, i))
                continue
            if name == "int":
                toks.append(Tok("cmd", "int", i))
                continue
            if name == "iint":
                toks.append(Tok("cmd", "iint", i))
                continue
            if name == "iiint":
                toks.append(Tok("cmd", "iiint", i))
                continue
            if name == "oint":
                toks.append(Tok("cmd", "oint", i))
                continue
            if name == "det":
                toks.append(Tok("cmd", "det", i))
                continue
            if name in ("lfloor", "rfloor", "lceil", "rceil"):
                toks.append(Tok("cmd", name, i))
                continue
            if name == "to":
                toks.append(Tok("cmd", "to", i))
                continue
            if name in ("{", "}"):
                toks.append(Tok("cmd", name, i))
                continue
            raise ParseError("不支持的 LaTeX 命令 \\%s" % name)

        # ---- numbers ------------------------------------------------------
        if ch.isdigit() or (ch == "." and i + 1 < n and src[i + 1].isdigit()):
            j = i
            while j < n and src[j].isdigit():
                j += 1
            if j < n and src[j] == "." and j + 1 < n and src[j + 1].isdigit():
                j += 1
                while j < n and src[j].isdigit():
                    j += 1
            text = src[i:j]
            toks.append(Tok("num", text, i))
            i = j
            continue

        # ---- identifiers / differentials ----------------------------------
        if ch.isalpha():
            if ch == "d" and i + 1 < n:
                nxt = src[i + 1]
                if nxt.isalpha():
                    toks.append(Tok("dif", nxt, i))
                    i += 2
                    continue
                if nxt == "\\":
                    j = i + 2
                    while j < n and src[j].isalpha():
                        j += 1
                    cname = src[i + 2:j]
                    if cname in _GREEK_VARS:
                        toks.append(Tok("dif", cname, i))
                        i = j
                        continue
            toks.append(Tok("name", ch, i))
            i += 1
            continue

        # ---- punctuation --------------------------------------------------
        if ch == "{":
            toks.append(Tok("lbr", None, i))
            i += 1
            continue
        if ch == "}":
            toks.append(Tok("rbr", None, i))
            i += 1
            continue
        if ch == "(":
            toks.append(Tok("lpar", None, i))
            i += 1
            continue
        if ch == ")":
            toks.append(Tok("rpar", None, i))
            i += 1
            continue
        if ch == "[":
            toks.append(Tok("lbrack", None, i))
            i += 1
            continue
        if ch == "]":
            toks.append(Tok("rbrack", None, i))
            i += 1
            continue
        if ch == "&":
            toks.append(Tok("amp", None, i))
            i += 1
            continue
        if ch == "!":
            toks.append(Tok("bang", None, i))
            i += 1
            continue
        if ch == "=":
            toks.append(Tok("eq", None, i))
            i += 1
            continue
        if ch in "+-*/^_":
            toks.append(Tok("op", ch, i))
            i += 1
            continue
        if ch == "|":
            toks.append(Tok("bar", None, i))
            i += 1
            continue
        if ch in ",.<>":
            # stray punctuation: harmless spacing in this subset
            i += 1
            continue
        raise ParseError("无法识别的字符 %r" % ch)

    toks.append(Tok("eof", None, n))
    return toks


# ---------------------------------------------------------------------------
# Parser
# ---------------------------------------------------------------------------
_STARTER_CMDS = {
    "frac", "sqrt", "pi", "infty", "sin", "cos", "tan", "cot", "sec", "csc",
    "ln", "log", "exp", "arcsin", "arccos", "arctan", "sinh", "cosh", "tanh",
    "zeta", "mu", "varphi", "det", "int", "iint", "iiint", "oint", "sum",
    "prod", "lim", "lfloor", "lceil",
}


def _stop_key(tok: Tok):
    if tok.kind == "right":
        return ("right", tok.value)
    if tok.kind == "endenv":
        return "endenv"
    if tok.kind == "cmd":
        return ("cmd", tok.value)
    return tok.kind


class Parser:
    def __init__(self, toks: List[Tok]):
        self.toks = toks
        self.i = 0

    # -- token helpers ------------------------------------------------------
    def peek(self, k: int = 0) -> Tok:
        j = self.i + k
        if j >= len(self.toks):
            return self.toks[-1]
        return self.toks[j]

    def advance(self) -> Tok:
        t = self.toks[self.i]
        self.i += 1
        return t

    def at(self, kind: str, value: Any = None) -> bool:
        t = self.peek()
        return t.kind == kind and (value is None or t.value == value)

    def expect(self, kind: str, value: Any = None) -> Tok:
        t = self.peek()
        if t.kind != kind or (value is not None and t.value != value):
            raise ParseError("第 %d 个字符处期待 %s，实际得到 %s"
                             % (t.pos, kind, t.kind))
        return self.advance()

    def stopped(self, stops) -> bool:
        return _stop_key(self.peek()) in stops

    # -- entry point --------------------------------------------------------
    def parse_all(self) -> Node:
        node = self.parse_add(frozenset({"eof"}))
        if self.peek().kind != "eof":
            t = self.peek()
            raise ParseError("第 %d 个字符处有多余记号 %s" % (t.pos, t.kind))
        return node

    # -- expression grammar -------------------------------------------------
    def parse_add(self, stops) -> Node:
        terms = [self.parse_mul(stops)]
        while True:
            t = self.peek()
            if t.kind == "op" and t.value in "+-" and not self.stopped(stops):
                self.advance()
                if t.value == "-":
                    terms.append(Neg(self.parse_mul(stops)))
                else:
                    terms.append(self.parse_mul(stops))
            else:
                break
        return terms[0] if len(terms) == 1 else Add(terms)

    def parse_mul(self, stops) -> Node:
        cur = self.parse_unary(stops)
        while not self.stopped(stops):
            t = self.peek()
            if t.kind == "op" and t.value in "*/":
                self.advance()
                rhs = self.parse_unary(stops)
                cur = mul(cur, rhs) if t.value == "*" else Div(cur, rhs)
            elif t.kind == "op" and t.value == "mod":
                # a mod m binds like multiplication
                self.advance()
                rhs = self.parse_unary(stops)
                cur = Mod(cur, rhs)
            elif self._starts_primary(t):
                rhs = self.parse_unary(stops)
                cur = mul(cur, rhs)
            else:
                break
        return cur

    @staticmethod
    def _starts_primary(t: Tok) -> bool:
        if t.kind in ("num", "name", "lpar", "left", "lbr"):
            return True
        if t.kind == "cmd" and t.value in _STARTER_CMDS:
            return True
        return False

    def parse_unary(self, stops) -> Node:
        t = self.peek()
        if t.kind == "op" and t.value == "-":
            self.advance()
            return Neg(self.parse_unary(stops))
        if t.kind == "op" and t.value == "+":
            self.advance()
            return self.parse_unary(stops)
        return self.parse_postfix(stops)

    def parse_postfix(self, stops) -> Node:
        base = self.parse_primary(stops)
        while True:
            t = self.peek()
            if t.kind == "op" and t.value == "^":
                self.advance()
                base = Pow(base, self.parse_exponent(stops))
            elif t.kind == "bang":
                self.advance()
                base = Fact(base, 1)
            else:
                break
        return base

    def parse_exponent(self, stops) -> Node:
        if self.at("lbr"):
            self.advance()
            e = self.parse_add(frozenset({"rbr"}))
            self.expect("rbr")
            return e
        return self.parse_unary(stops)

    # -- primaries ----------------------------------------------------------
    def parse_primary(self, stops) -> Node:
        t = self.peek()

        if t.kind == "num":
            self.advance()
            return Num(t.value, float(t.value))

        if t.kind == "name":
            self.advance()
            sub = None
            if self.at("op", "_"):
                self.advance()
                sub = self.parse_simple_label()
            if sub is None and t.value == "e":
                return Const("e")
            if sub is None and t.value == "i":
                return Const("i")
            return Var(t.value, sub)

        if t.kind == "lpar":
            self.advance()
            e = self.parse_add(frozenset({"rpar"}))
            self.expect("rpar")
            return e

        if t.kind == "bar":
            self.advance()
            e = self.parse_add(frozenset({"bar"}))
            self.expect("bar")
            return Abs(e)

        if t.kind == "lbr":
            self.advance()
            e = self.parse_add(frozenset({"rbr"}))
            self.expect("rbr")
            return e

        if t.kind == "left":
            delim = self.advance().value
            closing = {"(": ")", "[": "]", "{": "}", "|": "|", ".": "."}[delim]
            close = ("right", closing)
            e = self.parse_add(frozenset({close}))
            if self.peek().kind != "right":
                raise ParseError("\\left 缺少配对的 \\right")
            got = self.advance().value
            if got != closing:
                raise ParseError("\\left%s 与 \\right%s 不匹配" % (delim, got))
            if delim == "{":
                return FracPart(e)
            if delim == "|":
                return Abs(e)
            return e

        if t.kind == "cmd":
            return self.parse_command(stops)

        if t.kind == "dif":
            raise ParseError("第 %d 个字符处出现了多余的微分 d%s" % (t.pos, t.value))

        raise ParseError("第 %d 个字符处无法开始一个表达式（%s）" % (t.pos, t.kind))

    # -- labels / small token groups ---------------------------------------
    def parse_simple_label(self) -> str:
        if self.at("lbr"):
            toks = self.read_braced_raw()
            parts = []
            for tk in toks:
                if tk.kind == "num":
                    parts.append(tk.value)
                elif tk.kind == "name":
                    parts.append(tk.value)
                else:
                    parts.append(str(tk.value))
            return "".join(parts)
        t = self.peek()
        if t.kind in ("num", "name"):
            self.advance()
            return str(t.value)
        raise ParseError("第 %d 个字符处的下标无法识别" % t.pos)

    def read_braced_raw(self) -> List[Tok]:
        """Consume a ``{ ... }`` group and return the tokens inside it."""
        self.expect("lbr")
        depth = 1
        out: List[Tok] = []
        while True:
            t = self.peek()
            if t.kind == "eof":
                raise ParseError("缺少右花括号")
            if t.kind == "lbr":
                depth += 1
            elif t.kind == "rbr":
                depth -= 1
                if depth == 0:
                    self.advance()
                    return out
            out.append(t)
            self.advance()

    def read_script_tokens(self) -> List[Tok]:
        """Read the argument of ``_`` or ``^`` as a token list."""
        if self.at("lbr"):
            return self.read_braced_raw()
        out: List[Tok] = []
        t = self.peek()
        if t.kind == "op" and t.value in "+-":
            out.append(self.advance())
            t = self.peek()
        if t.kind in ("num", "name", "cmd", "dif"):
            out.append(self.advance())
        return out

    @staticmethod
    def tokens_to_expr(toks: List[Tok]) -> Node:
        p = Parser(list(toks) + [Tok("eof", None, toks[-1].pos if toks else 0)])
        return p.parse_all()

    @staticmethod
    def parse_varname(toks: List[Tok]):
        """Split ``k_1`` / ``p`` into (key, remaining tokens)."""
        if not toks or toks[0].kind != "name":
            raise ParseError("求和/极限指标不是变量名")
        name = toks[0].value
        sub = None
        rest = toks[1:]
        if len(rest) >= 2 and rest[0].kind == "op" and rest[0].value == "_":
            lab = rest[1]
            if lab.kind in ("num", "name"):
                sub = str(lab.value)
            else:
                sub = str(lab.value)
            rest = rest[2:]
        return (name if sub is None else "%s_%s" % (name, sub)), rest

    # -- commands -----------------------------------------------------------
    def parse_command(self, stops) -> Node:
        t = self.advance()
        name = t.value

        if name in ("pi", "infty"):
            return Const(name)

        if name == "frac":
            num = self.parse_frac_arg(stops)
            # \frac{d}{dx}(...)：词法层已经把 dx 标成 dif 记号（可能在 { } 里），这里直接吃掉
            if isinstance(num, Var) and num.name == "d":
                dvar = self._take_diff_token()
                if dvar is not None:
                    operand = self.parse_postfix(stops)
                    return Deriv(dvar, operand)
            den = self.parse_frac_arg(stops)
            dvar = _diff_var_of(num, den)
            if dvar is not None:
                operand = self.parse_postfix(stops)
                return Deriv(dvar, operand)
            return Div(num, den)

        if name == "sqrt":
            power = None
            if self.at("lbrack"):
                self.advance()
                idx = self.parse_add(frozenset({"rbrack"}))
                self.expect("rbrack")
                power = Div(Num("1", 1.0), idx)
            arg = self.parse_frac_arg(stops)
            return Func("sqrt", arg, power)

        if name in _FUNCS:
            power = None
            if self.at("op", "^"):
                self.advance()
                power = self.parse_exponent(stops)
            arg = self.parse_func_arg(stops)
            return Func(name, arg, power)

        if name == "zeta":
            return Zeta(self.parse_func_arg(stops))
        if name == "mu":
            return Mobius(self.parse_func_arg(stops))
        if name == "varphi":
            return Phi(self.parse_func_arg(stops))

        if name == "det":
            # det(A·B·…)：矩阵乘积的行列式
            if self.at("left"):
                self.advance()          # \left 的值就是定界符本身
                mats = [Det(self.parse_matrix())]
                while True:
                    t2 = self.peek()
                    # \cdot 在词法里被归一成 op '*'
                    if (t2.kind == "cmd" and t2.value == "cdot") or (t2.kind == "op" and t2.value == "*"):
                        self.advance()
                        mats.append(Det(self.parse_matrix()))
                        continue
                    break
                if not self.at("right"):
                    raise ParseError("\\det 的矩阵乘积缺少 \\right")
                self.advance()
                if len(mats) == 1:
                    return mats[0]
                return DetProd(mats)
            return Det(self.parse_matrix())

        if name in ("int", "iint", "iiint"):
            return self.parse_integral(stops, name)

        if name == "oint":
            return self.parse_contour(stops)

        if name in ("sum", "prod"):
            return self.parse_bigop(stops, name)

        if name == "lim":
            return self.parse_limit(stops)

        if name == "lfloor":
            e = self.parse_add(stops | frozenset({("cmd", "rfloor")}))
            self._expect_cmd("rfloor")
            return Floor(e)

        if name == "lceil":
            e = self.parse_add(stops | frozenset({("cmd", "rceil")}))
            self._expect_cmd("rceil")
            return Ceil(e)

        raise ParseError("命令 \\%s 不能出现在这里" % name)

    def _expect_cmd(self, which: str):
        t = self.peek()
        if t.kind == "cmd" and t.value == which:
            self.advance()
            return
        raise ParseError("期待 \\%s，实际得到 %s" % (which, t.kind))

    def _take_diff_token(self):
        """吃掉一个 dx / {dx} 记号，返回变量名；不是微分就返回 None。"""
        if self.peek().kind == "dif":
            dvar = self.advance().value
        elif self.peek().kind == "lbr" and self.peek(1).kind == "dif" and self.peek(2).kind == "rbr":
            self.advance()
            dvar = self.advance().value
            self.advance()
        else:
            return None
        if self.at("op", "_"):
            self.advance()
            dvar = "%s_%s" % (dvar, self.parse_simple_label())
        return dvar

    def parse_frac_arg(self, stops) -> Node:
        if self.at("lbr"):
            self.advance()
            e = self.parse_add(frozenset({"rbr"}))
            self.expect("rbr")
            return e
        return self.parse_unary(stops)

    def parse_func_arg(self, stops) -> Node:
        if self.at("lpar"):
            self.advance()
            e = self.parse_add(frozenset({"rpar"}))
            self.expect("rpar")
            return e
        if self.at("left"):
            return self.parse_primary(stops)
        if self.at("lbr"):
            self.advance()
            e = self.parse_add(frozenset({"rbr"}))
            self.expect("rbr")
            return e
        return self.parse_postfix(stops)

    # -- matrices -----------------------------------------------------------
    def parse_matrix(self) -> List[List[Node]]:
        t = self.peek()
        if t.kind != "env":
            raise ParseError("\\det 后面缺少 pmatrix 环境")
        env = self.advance().value
        if env.strip() not in ("pmatrix", "bmatrix", "matrix"):
            raise ParseError("不支持的矩阵环境 %s" % env)
        rows: List[List[Node]] = []
        cur: List[Node] = []
        while True:
            e = self.parse_add(frozenset({"amp", "row", "endenv"}))
            cur.append(e)
            t = self.peek()
            if t.kind == "amp":
                self.advance()
                continue
            if t.kind == "row":
                self.advance()
                rows.append(cur)
                cur = []
                continue
            if t.kind == "endenv":
                self.advance()
                rows.append(cur)
                break
            raise ParseError("矩阵项没有正确结束")
        if not rows:
            raise ParseError("空矩阵")
        width = len(rows[0])
        for r in rows:
            if len(r) != width:
                raise ParseError("矩阵各行长度不一致")
        return rows

    # -- big operators ------------------------------------------------------
    def _range_spec(self, toks: List[Tok]):
        """Interpret the tokens of a subscript such as ``k=1`` or ``n\\to\\infty``.

        Returns (kind, var, payload):
          kind='range'  -> (var, lower_node)
          kind='limit'  -> (var, target_node)
          kind='prime'  -> (var, None)
        """
        eq_i = next((i for i, tk in enumerate(toks) if tk.kind == "eq"), None)
        if eq_i is not None:
            var, rest = self.parse_varname(toks[:eq_i])
            if rest:
                raise ParseError("求和下限格式无法识别")
            lo = self.tokens_to_expr(toks[eq_i + 1:])
            return "range", var, lo
        to_i = next((i for i, tk in enumerate(toks)
                     if tk.kind == "cmd" and tk.value == "to"), None)
        if to_i is not None:
            var, rest = self.parse_varname(toks[:to_i])
            target = self.tokens_to_expr(toks[to_i + 1:])
            return "limit", var, target
        var, rest = self.parse_varname(toks)
        if rest:
            raise ParseError("指标格式无法识别")
        return "prime", var, None

    def parse_integral(self, stops, kind: str) -> Integral:
        lo = hi = None
        while True:
            if self.at("op", "_"):
                self.advance()
                toks = self.read_script_tokens()
                lo = self.tokens_to_expr(toks)
            elif self.at("op", "^"):
                self.advance()
                toks = self.read_script_tokens()
                hi = self.tokens_to_expr(toks)
            else:
                break
        body = self.parse_mul(stops | frozenset({"dif"}))
        # A single \int consumes exactly one differential; \iint / \iiint
        # consume one per dimension.  This is what makes
        #   \int_a^b\int_c^d F(x,y)\,dy\,dx
        # come out right: the inner (right-most written) \int takes the first
        # differential, the outer one takes the next, i.e. the left-most
        # integral pairs with the right-most differential.
        need = {"int": 1, "iint": 2, "iiint": 3}[kind]
        dims: List[str] = []
        while self.peek().kind == "dif" and len(dims) < need:
            tok = self.advance()
            var = tok.value
            if self.at("op", "_"):
                self.advance()
                var = "%s_%s" % (var, self.parse_simple_label())
            dims.append(var)
        if not dims:
            raise ParseError("积分缺少微分符号 dx")
        return Integral(kind, lo, hi, dims, body)

    def parse_contour(self, stops) -> Contour:
        contour_label = None
        if self.at("op", "_"):
            self.advance()
            toks = self.read_script_tokens()
            if len(toks) == 1 and toks[0].kind == "name":
                contour_label = toks[0].value
        if self.at("op", "^"):
            self.advance()
            self.read_script_tokens()
        body = self.parse_mul(stops | frozenset({"dif"}))
        var = None
        if self.peek().kind == "dif":
            tok = self.advance()
            var = tok.value
            if self.at("op", "_"):
                self.advance()
                var = "%s_%s" % (var, self.parse_simple_label())
        if var is None:
            raise ParseError("围道积分缺少微分符号 dz")
        return Contour(body, var)

    def parse_bigop(self, stops, kind: str):
        spec = None
        if self.at("op", "_"):
            self.advance()
            spec = self.read_script_tokens()
        hi = None
        if self.at("op", "^"):
            self.advance()
            hi = self.tokens_to_expr(self.read_script_tokens())
        body = self.parse_mul(stops)

        over_primes = False
        lo = None
        if spec is None:
            raise ParseError("\\%s 缺少下标" % kind)
        skind, var, payload = self._range_spec(spec)
        if skind == "range":
            lo = payload
        elif skind == "prime":
            over_primes = True
            lo = Num("2", 2.0)
        else:
            raise ParseError("\\%s 的下标不能是极限形式" % kind)
        if hi is None:
            if over_primes:
                hi = Const("infty")
            else:
                raise ParseError("\\%s 缺少上限" % kind)
        if kind == "sum":
            return Sum(var, lo, hi, body, over_primes)
        return Prod(var, lo, hi, body, over_primes)

    def parse_limit(self, stops) -> Limit:
        if not self.at("op", "_"):
            raise ParseError("\\lim 缺少下标")
        self.advance()
        spec = self.read_script_tokens()
        skind, var, target = self._range_spec(spec)
        if skind != "limit":
            raise ParseError("\\lim 的下标必须是 n\\to\\infty 形式")
        body = self.parse_mul(stops)
        return Limit(var, target, body)


# ---------------------------------------------------------------------------
# public helpers
# ---------------------------------------------------------------------------
def parse(latex: str) -> Node:
    """Parse one LaTeX formula into an AST."""
    return Parser(tokenize(latex)).parse_all()


def strip_claim_suffix(latex: str) -> str:
    """Remove the trailing `` = 325`` claim, if present."""
    s = latex.rstrip()
    eq = s.rfind("=")
    if eq <= 0:
        return s
    rhs = s[eq + 1:].strip()
    ok = rhs != ""
    seen_dot = False
    for idx, ch in enumerate(rhs):
        if ch.isdigit():
            continue
        if ch == "." and not seen_dot and idx > 0:
            seen_dot = True
            continue
        if ch in "+-" and idx == 0:
            continue
        ok = False
        break
    if ok:
        return s[:eq].rstrip()
    return s
