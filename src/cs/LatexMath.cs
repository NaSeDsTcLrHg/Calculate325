/*
 * Calculate325 —— 随机窗口的 LaTeX 子集排版
 * ---------------------------------------------------------------------------
 * 只服务随机模式生成的 LaTeX：
 *   分式、根号、上下标、\left...\right、\int/\iint/\iiint、
 *   \sum/\prod（上下限）、\lim、\to、\infty、\pi、\cdot 等。
 * 全部 GDI+ 自绘，不依赖外部库/网络。
 */
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace Calculate325
{
    /// <summary>真正按 fs 字号绘制的文本（变量斜体，函数名正体）。</summary>
    sealed class MathTextNode : MathNode
    {
        public string Text;
        public bool Upright;
        public bool MathSymbol;

        public MathTextNode(string text, bool upright) : this(text, upright, false) { }

        public MathTextNode(string text, bool upright, bool mathSymbol)
        {
            Text = text ?? "";
            Upright = upright;
            MathSymbol = mathSymbol;
        }

        SizeF size;

        Font MakeFont(Font baseFont, float fs)
        {
            float size = Math.Max(5f, fs);
            if (MathSymbol) return MathFonts.CreateMathSymbol(size);
            if (Upright) return MathFonts.CreateUpright(size, GraphicsUnit.Pixel);
            return MathFonts.CreateItalic(size, GraphicsUnit.Pixel);
        }

        public override void Measure(Graphics g, float fs, Font f)
        {
            using (Font ff = MakeFont(f, fs))
            {
                size = g.MeasureString(Text, ff, PointF.Empty, Sf);
                FontFamily fam = ff.FontFamily;
                float em = fam.GetEmHeight(ff.Style);
                if (em <= 0.1f) em = 1f;
                float asc = fam.GetCellAscent(ff.Style);
                float desc = fam.GetCellDescent(ff.Style);
                Base = fs * asc / em;
                H = fs * (asc + desc) / em;
            }
            W = size.Width;
        }

        public override void Draw(Graphics g, float x, float y, float fs, Font f, Color color, float barY)
        {
            using (Font ff = MakeFont(f, fs))
            using (SolidBrush b = new SolidBrush(color))
            {
                g.DrawString(Text, ff, b, x, y, Sf);
            }
        }
    }

    sealed class SpaceNode : MathNode
    {
        public float Em;
        public SpaceNode(float em) { Em = em; }
        public override void Measure(Graphics g, float fs, Font f)
        {
            W = fs * Em;
            H = fs * 1.2f;
            Base = fs * 0.9f;
        }
        public override void Draw(Graphics g, float x, float y, float fs, Font f, Color color, float barY) { }
    }

    /// <summary>普通底数的上标/下标（可同时有）。</summary>
    sealed class ScriptNode : MathNode
    {
        public MathNode BaseNode, Sub, Sup;
        float gap;

        public override void Measure(Graphics g, float fs, Font f)
        {
            BaseNode.Measure(g, fs, f);
            float sw = 0, sh = 0;
            if (Sub != null)
            {
                Sub.Measure(g, fs * 0.62f, f);
                sw = Math.Max(sw, Sub.W);
                sh = Math.Max(sh, Sub.H);
            }
            if (Sup != null)
            {
                Sup.Measure(g, fs * 0.62f, f);
                sw = Math.Max(sw, Sup.W);
                sh = Math.Max(sh, Sup.H);
            }
            gap = fs * 0.05f;
            float supRise = Sup == null ? 0 : Sup.H + fs * 0.34f;
            float subDrop = Sub == null ? 0 : Math.Max(0f, Sub.H - fs * 0.28f);
            Base = Math.Max(BaseNode.Base, supRise);
            H = Math.Max(BaseNode.H, Base + subDrop);
            if (Sup != null && Sup.H > 0) H = Math.Max(H, Base + fs * 0.02f);
            W = BaseNode.W + sw + gap;
        }

        public override void Draw(Graphics g, float x, float y, float fs, Font f, Color color, float barY)
        {
            float baseline = y + Base;
            BaseNode.Draw(g, x, baseline - BaseNode.Base, fs, f, color, barY);
            float sx = x + BaseNode.W + gap;
            if (Sup != null)
                Sup.Draw(g, sx, baseline - fs * 0.34f - Sup.H, fs * 0.62f, f, color, barY);
            if (Sub != null)
                Sub.Draw(g, sx, baseline - fs * 0.28f, fs * 0.62f, f, color, barY);
        }
    }

    /// <summary>大运算符：积分/求和/连乘。积分上下限在右侧，Σ/∏ 在上下方。</summary>
    sealed class BigOpNode : MathNode
    {
        public string Symbol;
        public bool LimitsRight;
        public MathNode Lower, Upper;

        public BigOpNode(string symbol, bool limitsRight)
        {
            Symbol = symbol;
            LimitsRight = limitsRight;
        }

        float symW, symH, gap;
        float lowW, lowH, upW, upH;

        public override void Measure(Graphics g, float fs, Font f)
        {
            gap = fs * 0.08f;
            float size = fs * (Symbol == "Σ" || Symbol == "∏" ? 1.35f : 1.25f);
            using (Font bf = new Font(f.FontFamily, size, FontStyle.Regular, GraphicsUnit.Pixel))
            {
                SizeF sz = g.MeasureString(Symbol, bf, PointF.Empty, Sf);
                symW = sz.Width;
                symH = fs * (Symbol == "Σ" || Symbol == "∏" ? 1.32f : 1.25f);
            }
            lowW = lowH = upW = upH = 0;
            if (Lower != null) { Lower.Measure(g, fs * 0.76f, f); lowW = Lower.W; lowH = Lower.H; }
            if (Upper != null) { Upper.Measure(g, fs * 0.76f, f); upW = Upper.W; upH = Upper.H; }
            if (LimitsRight)
            {
                W = symW + gap + Math.Max(lowW, upW);
                H = Math.Max(symH, Math.Max(upH, symH * 0.68f + lowH));
                Base = symH;
            }
            else
            {
                W = Math.Max(symW, Math.Max(lowW, upW)) + fs * 0.10f;
                H = upH + symH + lowH + fs * 0.06f;
                Base = upH + symH;
            }
        }

        public override void Draw(Graphics g, float x, float y, float fs, Font f, Color color, float barY)
        {
            float size = fs * (Symbol == "Σ" || Symbol == "∏" ? 1.35f : 1.25f);
            using (Font bf = new Font(f.FontFamily, size, FontStyle.Regular, GraphicsUnit.Pixel))
            using (SolidBrush b = new SolidBrush(color))
            {
                if (LimitsRight)
                {
                    g.DrawString(Symbol, bf, b, x, y, Sf);
                    float lx = x + symW + gap;
                    if (Upper != null) Upper.Draw(g, lx, y, fs * 0.76f, f, color, barY);
                    if (Lower != null) Lower.Draw(g, lx, y + Math.Max(upH + fs * 0.02f, symH * 0.68f), fs * 0.76f, f, color, barY);
                }
                else
                {
                    float cx = x + (W - symW) / 2f;
                    g.DrawString(Symbol, bf, b, cx, y + upH, Sf);
                    if (Upper != null) Upper.Draw(g, x + (W - upW) / 2f, y, fs * 0.76f, f, color, barY);
                    if (Lower != null) Lower.Draw(g, x + (W - lowW) / 2f, y + upH + symH, fs * 0.76f, f, color, barY);
                }
            }
        }
    }

    /// <summary>lim 与下方下标。</summary>
    sealed class LimNode : MathNode
    {
        public MathNode Lower;
        float tw, th, lw, lh;

        public override void Measure(Graphics g, float fs, Font f)
        {
            using (Font ff = MathFonts.CreateUpright(fs, GraphicsUnit.Pixel))
            {
                SizeF sz = g.MeasureString("lim", ff, PointF.Empty, Sf);
                tw = sz.Width;
            }
            th = fs * 1.28f;
            lw = lh = 0;
            if (Lower != null) { Lower.Measure(g, fs * 0.70f, f); lw = Lower.W; lh = Lower.H; }
            W = Math.Max(tw, lw) + fs * 0.04f;
            H = th + lh + fs * 0.02f;
            Base = th;
        }

        public override void Draw(Graphics g, float x, float y, float fs, Font f, Color color, float barY)
        {
            using (Font ff = MathFonts.CreateUpright(fs, GraphicsUnit.Pixel))
            using (SolidBrush b = new SolidBrush(color))
            {
                g.DrawString("lim", ff, b, x + (W - tw) / 2f, y, Sf);
            }
            if (Lower != null) Lower.Draw(g, x + (W - lw) / 2f, y + th, fs * 0.70f, f, color, barY);
        }
    }

    /// <summary>可缩放括号/花括号/方括号。</summary>
    sealed class DelimNode : MathNode
    {
        public string Open, Close;
        public MathNode Inner;
        float pad, openW, closeW;

        public DelimNode(string open, string close, MathNode inner)
        {
            Open = open ?? "";
            Close = close ?? "";
            Inner = inner;
        }

        public override void Measure(Graphics g, float fs, Font f)
        {
            Inner.Measure(g, fs, f);
            using (Font ff = new Font(f.FontFamily, Math.Max(5f, fs), FontStyle.Regular, GraphicsUnit.Pixel))
            {
                if (!string.IsNullOrEmpty(Open)) openW = g.MeasureString(Open, ff, PointF.Empty, Sf).Width;
                if (!string.IsNullOrEmpty(Close)) closeW = g.MeasureString(Close, ff, PointF.Empty, Sf).Width;
            }
            pad = Math.Max(fs * 0.20f, Math.Max(openW, closeW) + fs * 0.06f);
            W = Inner.W + pad * 2;
            H = Math.Max(Inner.H + fs * 0.10f, fs * 1.1f);
            Base = Inner.Base + (H - Inner.H) / 2f;
        }

        void Glyph(Graphics g, string glyph, float x, float y, float fs, Font f, Color color, bool right)
        {
            if (string.IsNullOrEmpty(glyph)) return;
            using (GraphicsPath gp = new GraphicsPath())
            {
                gp.AddString(glyph, f.FontFamily, (int)FontStyle.Regular, fs, new PointF(0, 0), StringFormat.GenericTypographic);
                RectangleF bb = gp.GetBounds();
                if (bb.Height <= 0.1f) return;
                float sy = H / bb.Height;
                using (Matrix m = new Matrix())
                {
                    m.Translate(-bb.Left, -bb.Top);
                    m.Scale(0.95f, sy);
                    gp.Transform(m);
                }
                RectangleF b2 = gp.GetBounds();
                using (SolidBrush b = new SolidBrush(color))
                {
                    float dx = right ? (x - b2.Width) : x;
                    using (GraphicsPath p = (GraphicsPath)gp.Clone())
                    using (Matrix m2 = new Matrix())
                    {
                        m2.Translate(dx, y);
                        p.Transform(m2);
                        g.FillPath(b, p);
                    }
                }
            }
        }

        public override void Draw(Graphics g, float x, float y, float fs, Font f, Color color, float barY)
        {
            float cy = y + (H - Inner.H) / 2f;
            Inner.Draw(g, x + pad, cy, fs, f, color, barY);
            Glyph(g, Open, x + fs * 0.02f, y, fs, f, color, false);
            Glyph(g, Close, x + W - fs * 0.02f, y, fs, f, color, true);
        }
    }

    /// <summary>LaTeX 子集解析器。</summary>
    static class LatexMathParser
    {
        sealed class Parser
        {
            readonly string s;
            int p;
            bool tight;

            public Parser(string text) { s = text ?? ""; }

            char Cur { get { return p < s.Length ? s[p] : '\0'; } }

            bool Starts(string t)
            {
                return p + t.Length <= s.Length && string.CompareOrdinal(s, p, t, 0, t.Length) == 0;
            }

            void SkipWs()
            {
                while (p < s.Length && char.IsWhiteSpace(s[p])) p++;
            }

            public MathNode Parse()
            {
                MathNode n = ParseRow(false);
                return n;
            }

            MathNode ParseRow(bool stopRight)
            {
                return ParseRow(stopRight, '\0');
            }

            MathNode ParseRow(bool stopRight, char stopChar)
            {
                List<MathNode> parts = new List<MathNode>();
                while (true)
                {
                    SkipWs();
                    if (p >= s.Length) break;
                    if (Cur == '}') break;
                    if (stopChar != '\0' && Cur == stopChar) break;
                    if (stopRight && Starts("\\right")) break;
                    int before = p;
                    MathNode atom = ParseAtom();
                    if (atom != null) parts.Add(atom);
                    if (p == before) p++;   // 防止死循环
                }
                return Wrap(parts);
            }

            MathNode ParseAtom()
            {
                SkipWs();
                if (p >= s.Length) return null;
                MathNode n = ParsePrimary();
                if (n == null) return null;
                return ParseScripts(n);
            }

            MathNode ParsePrimary()
            {
                SkipWs();
                if (p >= s.Length) return null;
                char c = Cur;
                if (c == '{')
                {
                    p++;
                    MathNode n = ParseRow(false);
                    if (Cur == '}') p++;
                    return n;
                }
                if (c == '\\') return ParseCommand();
                if (char.IsDigit(c)) return ParseNumber();
                if (char.IsLetter(c))
                {
                    p++;
                    return new MathTextNode(c.ToString(), false);
                }
                if (c == '(')
                {
                    p++;
                    MathNode inner = ParseRow(false, ')');
                    if (Cur == ')') p++;
                    return new DelimNode("(", ")", inner);
                }
                p++;
                if (c == '+') return tight ? new MathTextNode("+", true, true) : new MathTextNode("\u2009+\u2009", true, true);
                if (c == '-' || c == '\u2212') return tight ? new MathTextNode("−", true, true) : new MathTextNode("\u2009−\u2009", true, true);
                if (c == '=') return tight ? new MathTextNode("=", true, true) : new MathTextNode("\u2009=\u2009", true, true);
                if (c == '\u00b7') return tight ? new MathTextNode("·", true, true) : new MathTextNode("\u2009·\u2009", true, true);
                if (c == '*' || c == '\u00d7') return tight ? new MathTextNode("×", true, true) : new MathTextNode("\u2009×\u2009", true, true);
                if (c == '/' || c == '\u00f7') return new MathTextNode("\u2009/\u2009", true, true);
                if (c == ',') return new MathTextNode(", ", true);
                if (c == ';') return new MathTextNode("; ", true);
                if (c == '!') return new SpaceNode(-0.06f);
                if (c == '&') return null;
                return new MathTextNode(c.ToString(), true);
            }

            MathNode ParseNumber()
            {
                int st = p;
                while (p < s.Length && (char.IsDigit(s[p]) || s[p] == '.')) p++;
                return new MathTextNode(s.Substring(st, p - st), true);
            }

            MathNode ParseGroup()
            {
                SkipWs();
                if (Cur == '{')
                {
                    p++;
                    MathNode n = ParseRow(false);
                    if (Cur == '}') p++;
                    return n;
                }
                return ParsePrimary();
            }

            MathNode ParseScriptArg()
            {
                bool old = tight;
                tight = true;
                try
                {
                    SkipWs();
                    if (Cur == '{')
                    {
                        p++;
                        MathNode n = ParseRow(false);
                        if (Cur == '}') p++;
                        return n;
                    }
                    MathNode a = ParsePrimary();
                    return a ?? new MathTextNode("", true);
                }
                finally { tight = old; }
            }

            MathNode ParseScripts(MathNode b)
            {
                MathNode sub = null, sup = null;
                while (true)
                {
                    SkipWs();
                    if (Cur == '_')
                    {
                        p++;
                        sub = ParseScriptArg();
                    }
                    else if (Cur == '^')
                    {
                        p++;
                        sup = ParseScriptArg();
                    }
                    else break;
                }
                if (sub == null && sup == null) return b;
                ScriptNode sn = new ScriptNode();
                sn.BaseNode = b;
                sn.Sub = sub;
                sn.Sup = sup;
                return sn;
            }

            string ReadName()
            {
                int st = p;
                while (p < s.Length && char.IsLetter(s[p])) p++;
                return s.Substring(st, p - st);
            }

            string ReadDelimiter()
            {
                SkipWs();
                if (p >= s.Length) return "";
                if (Cur == '\\')
                {
                    p++;
                    if (p < s.Length && (Cur == '{' || Cur == '}' || Cur == '|'))
                    {
                        char ch = Cur;
                        p++;
                        return ch.ToString();
                    }
                    string name = ReadName();
                    if (name == "lvert" || name == "rvert") return "|";
                    if (name == "Vert" || name == "|") return "‖";
                    if (name == "langle") return "⟨";
                    if (name == "rangle") return "⟩";
                    if (name == "{") return "{";
                    if (name == "}") return "}";
                    return "";
                }
                char c = Cur;
                p++;
                if (c == '.') return "";
                return c.ToString();
            }

            MathNode ParseCommand()
            {
                p++; // backslash
                string cmd = ReadName();
                if (cmd.Length == 0)
                {
                    if (p >= s.Length) return null;
                    char ch = s[p];
                    if (ch == ',' || ch == ';' || ch == '!')
                    {
                        p++;
                        float em = ch == ',' ? 0.16f : (ch == ';' ? 0.28f : -0.06f);
                        return new SpaceNode(em);
                    }
                    if (ch == '\\') { p++; return new SpaceNode(0.2f); }
                    p++;
                    return new MathTextNode(ch.ToString(), true);
                }
                if (cmd == "frac")
                {
                    MathNode a = ParseGroup();
                    MathNode b = ParseGroup();
                    FracNode f = new FracNode();
                    f.Num = a;
                    f.Den = b;
                    return f;
                }
                if (cmd == "sqrt")
                {
                    SkipWs();
                    if (Cur == '[')
                    {
                        int depth = 1; p++;
                        while (p < s.Length && depth > 0) { if (s[p] == '[') depth++; else if (s[p] == ']') depth--; p++; }
                    }
                    MathNode inner = ParseGroup();
                    RootNode r = new RootNode(false, true);
                    r.Inner = inner;
                    return r;
                }
                if (cmd == "left")
                {
                    string open = ReadDelimiter();
                    MathNode inner = ParseRow(true);
                    string close = "";
                    if (Starts("\\right"))
                    {
                        p += 6;
                        close = ReadDelimiter();
                    }
                    return new DelimNode(open, close, inner);
                }
                if (cmd == "right")
                {
                    ReadDelimiter();
                    return null;
                }
                if (cmd == "int" || cmd == "iint" || cmd == "iiint" || cmd == "oint")
                {
                    string sym = cmd == "int" ? "∫" : (cmd == "iint" ? "∬" : (cmd == "iiint" ? "∭" : "∮"));
                    BigOpNode op = new BigOpNode(sym, true);
                    ParseBigScripts(op);
                    return op;
                }
                if (cmd == "sum" || cmd == "prod")
                {
                    BigOpNode op = new BigOpNode(cmd == "sum" ? "Σ" : "∏", false);
                    ParseBigScripts(op);
                    return op;
                }
                if (cmd == "lim")
                {
                    LimNode ln = new LimNode();
                    SkipWs();
                    if (Cur == '_')
                    {
                        p++;
                        ln.Lower = ParseScriptArg();
                    }
                    return ln;
                }
                if (cmd == "text" || cmd == "mathrm" || cmd == "operatorname")
                {
                    SkipWs();
                    if (Cur == '{')
                    {
                        int depth = 1; p++;
                        int st = p;
                        while (p < s.Length && depth > 0)
                        {
                            if (s[p] == '{') depth++;
                            else if (s[p] == '}') { depth--; if (depth == 0) break; }
                            p++;
                        }
                        string txt = s.Substring(st, p - st);
                        if (Cur == '}') p++;
                        return new MathTextNode(txt, true);
                    }
                    return null;
                }
                if (cmd == "cdot" || cmd == "times" || cmd == "div" || cmd == "pm")
                {
                    string sym = cmd == "cdot" ? "·" : (cmd == "times" ? "×" : (cmd == "div" ? "÷" : "±"));
                    return tight ? new MathTextNode(sym, true, true) : new MathTextNode("\u2009" + sym + "\u2009", true, true);
                }
                if (cmd == "to") return tight ? new MathTextNode("→", true, true) : new MathTextNode("\u2009→\u2009", true, true);
                if (cmd == "infty") return new MathTextNode("∞", true, true);
                if (cmd == "pi") return new MathTextNode("π", true, true);
                if (cmd == "alpha") return new MathTextNode("α", true, true);
                if (cmd == "beta") return new MathTextNode("β", true, true);
                if (cmd == "gamma") return new MathTextNode("γ", true, true);
                if (cmd == "theta") return new MathTextNode("θ", true, true);
                if (cmd == "lambda") return new MathTextNode("λ", true, true);
                if (cmd == "mu") return new MathTextNode("μ", true, true);
                if (cmd == "sigma") return new MathTextNode("σ", true, true);
                if (cmd == "phi") return new MathTextNode("φ", true, true);
                if (cmd == "omega") return new MathTextNode("ω", true, true);
                if (cmd == "le" || cmd == "leq") return tight ? new MathTextNode("≤", true, true) : new MathTextNode(" ≤ ", true, true);
                if (cmd == "ge" || cmd == "geq") return tight ? new MathTextNode("≥", true, true) : new MathTextNode(" ≥ ", true, true);
                if (cmd == "ne" || cmd == "neq") return tight ? new MathTextNode("≠", true, true) : new MathTextNode(" ≠ ", true, true);
                if (cmd == "approx") return tight ? new MathTextNode("≈", true, true) : new MathTextNode(" ≈ ", true, true);
                if (cmd == "sin" || cmd == "cos" || cmd == "tan" || cmd == "cot" ||
                    cmd == "ln" || cmd == "log" || cmd == "exp" ||
                    cmd == "arcsin" || cmd == "arccos" || cmd == "arctan" ||
                    cmd == "max" || cmd == "min")
                {
                    MathTextNode t = new MathTextNode(cmd, true);
                    return ParseScripts(t);
                }
                if (cmd == "," || cmd == ";" || cmd == "quad" || cmd == "qquad" || cmd == "!")
                {
                    float em = cmd == "," ? 0.16f : (cmd == ";" ? 0.28f : (cmd == "!" ? -0.06f : 0.6f));
                    return new SpaceNode(em);
                }
                if (cmd == "left." || cmd == "right.") return null;
                // 未知命令：按普通文字显示，保证不崩
                return new MathTextNode(cmd, true);
            }

            void ParseBigScripts(BigOpNode op)
            {
                while (true)
                {
                    SkipWs();
                    if (Cur == '_')
                    {
                        p++;
                        op.Lower = ParseScriptArg();
                    }
                    else if (Cur == '^')
                    {
                        p++;
                        op.Upper = ParseScriptArg();
                    }
                    else break;
                }
            }

            static MathNode Wrap(List<MathNode> parts)
            {
                if (parts.Count == 0) return new MathTextNode("", true);
                if (parts.Count == 1) return parts[0];
                RowNode row = new RowNode();
                row.Parts = parts;
                return row;
            }
        }

        public static MathNode Build(string latex)
        {
            try { return new Parser(latex).Parse(); }
            catch (Exception) { return new MathTextNode(latex ?? "", true); }
        }
    }
}
