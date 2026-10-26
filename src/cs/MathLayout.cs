/*
 * Calculate325 —— 表达式排版（LaTeX 风格渲染）
 * ---------------------------------------------------------------------------
 * 把求解器输出的 ASCII 表达式排版成“教科书/Desmos 那样”的样子：
 *   · 除法画成分式（横线与上下两行）
 *   · 乘方画成上标
 *   · 根号带根式符
 *   · 括号按内容高度自动放大
 * 全部用 GDI+ 自绘，不依赖任何外部库或网络。
 */
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace Calculate325
{
    /// <summary>排版节点：可测量、可绘制。</summary>
    abstract class MathNode
    {
        public float W, H, Base;      // 宽、高、基线（从顶部算起）

        public abstract void Measure(Graphics g, float fs, Font f);
        public abstract void Draw(Graphics g, float x, float y, float fs, Font f, Color color, float barY);

        protected static readonly StringFormat Sf = MakeSf();
        static StringFormat MakeSf()
        {
            StringFormat sf = new StringFormat(StringFormatFlags.NoWrap | StringFormatFlags.NoClip);
            sf.Alignment = StringAlignment.Near;
            sf.LineAlignment = StringAlignment.Near;
            return sf;
        }
    }

    /// <summary>一段文字（数字、运算符、字母）。</summary>
    sealed class TextNode : MathNode
    {
        public string Text;
        public bool Slant;    // 斜体（变量用）

        public TextNode(string text) { Text = text; }

        string Txt { get { return Slant ? Text : Text; } }

        SizeF size;

        public override void Measure(Graphics g, float fs, Font f)
        {
            size = g.MeasureString(Txt, f, PointF.Empty, Sf);
            W = size.Width;
            H = fs * 1.25f;
            Base = fs * 0.98f;
        }

        public override void Draw(Graphics g, float x, float y, float fs, Font f, Color color, float barY)
        {
            using (SolidBrush b = new SolidBrush(color))
            {
                g.DrawString(Txt, f, b, x, y + (Base - fs * 0.98f), Sf);
            }
        }
    }

    /// <summary>分式：分子、横线、分母。</summary>
    sealed class FracNode : MathNode
    {
        public MathNode Num, Den;

        public override void Measure(Graphics g, float fs, Font f)
        {
            Num.Measure(g, fs * 0.88f, f);
            Den.Measure(g, fs * 0.88f, f);
            float pad = fs * 0.18f, gap = fs * 0.55f;
            W = Math.Max(Num.W, Den.W) + pad * 2;
            Base = Num.H + gap * 0.55f;
            H = Num.H + gap + Den.H;
        }

        public override void Draw(Graphics g, float x, float y, float fs, Font f, Color color, float barY)
        {
            float cx = x + W / 2f;
            float lineY = y + Num.H + fs * 0.20f;
            float thick = Math.Max(1.2f, fs * 0.055f);
            using (Pen pen = new Pen(color, thick))
            {
                pen.StartCap = LineCap.Round; pen.EndCap = LineCap.Round;
                g.DrawLine(pen, x + fs * 0.07f, lineY, x + W - fs * 0.07f, lineY);
            }
            Num.Draw(g, cx - Num.W / 2f, y, fs * 0.88f, f, color, barY);
            Den.Draw(g, cx - Den.W / 2f, lineY + fs * 0.24f, fs * 0.88f, f, color, barY);
        }
    }

    /// <summary>乘方：底数 + 上标。</summary>
    sealed class PowerNode : MathNode
    {
        public MathNode B, E;
        public float Rise;
        bool paren;

        public PowerNode(bool paren) { this.paren = paren; }

        public override void Measure(Graphics g, float fs, Font f)
        {
            B.Measure(g, fs, f);
            E.Measure(g, fs * 0.62f, f);
            float raise = B.H * 0.45f;
            W = B.W + E.W + fs * 0.04f;
            Base = B.Base;
            H = Math.Max(B.H, raise + E.H);
            Rise = raise;
        }

        public override void Draw(Graphics g, float x, float y, float fs, Font f, Color color, float barY)
        {
            B.Draw(g, x, y + (H - B.H), fs, f, color, barY);
            float ex = x + B.W + fs * 0.04f;
            float ey = y + Rise;
            E.Draw(g, ex, ey, fs * 0.62f, f, color, barY);
        }
    }

    /// <summary>根式：√ 或 ∛ + 内部。</summary>
    sealed class RootNode : MathNode
    {
        public MathNode Inner;
        public bool Cube;
        public bool paren;

        public RootNode(bool cube, bool paren) { Cube = cube; this.paren = paren; }

        public override void Measure(Graphics g, float fs, Font f)
        {
            Inner.Measure(g, fs, f);
            float hook = fs * (Cube ? 0.72f : 0.62f);
            W = hook + Inner.W + fs * 0.12f;
            Base = Inner.Base + fs * 0.14f;
            H = Inner.H + fs * 0.20f;
        }

        public override void Draw(Graphics g, float x, float y, float fs, Font f, Color color, float barY)
        {
            float hook = fs * (Cube ? 0.72f : 0.62f);
            float top = y + fs * 0.05f;
            float bot = y + H - fs * 0.02f;
            using (Pen pen = new Pen(color, Math.Max(1.4f, fs * 0.06f)))
            {
                pen.StartCap = LineCap.Round; pen.EndCap = LineCap.Round;
                PointF[] pts = new PointF[]
                {
                    new PointF(x + fs * 0.02f, y + H * 0.55f),
                    new PointF(x + hook * 0.34f, y + H * 0.72f),
                    new PointF(x + hook * 0.62f, top),
                    new PointF(x + W - fs * 0.02f, top)
                };
                g.DrawLines(pen, pts);
            }
            Inner.Draw(g, x + hook + fs * 0.04f, y + fs * 0.16f, fs * 0.98f, f, color, barY);
        }
    }

    /// <summary>放大括号：把内容夹在缩放过的 ( ) 之间。</summary>
    sealed class ParenNode : MathNode
    {
        public MathNode Inner;

        public override void Measure(Graphics g, float fs, Font f)
        {
            Inner.Measure(g, fs, f);
            float pad = fs * 0.26f;
            W = Inner.W + pad * 2;
            H = Math.Max(Inner.H + fs * 0.10f, fs * 1.1f);
            Base = Inner.Base + (H - Inner.H) / 2f;
        }


        void DrawGlyph(Graphics g, string glyph, float x, float y, float fs, Font f, Color color, bool rightAlign)
        {
            using (GraphicsPath gp = new GraphicsPath())
            {
                gp.AddString(glyph, f.FontFamily, (int)FontStyle.Regular, fs, new PointF(0, 0), StringFormat.GenericTypographic);
                RectangleF bb = gp.GetBounds();
                if (bb.Height <= 0.1f) return;
                float targetH = H;
                float sx = 0.92f;
                float sy = targetH / bb.Height;
                using (Matrix m = new Matrix())
                {
                    m.Translate(-bb.Left, -bb.Top);
                    m.Scale(sx, sy);
                    gp.Transform(m);
                }
                RectangleF b2 = gp.GetBounds();
                using (SolidBrush b = new SolidBrush(color))
                {
                    float dx = rightAlign ? (x - b2.Width) : x;
                    g.FillPath(b, Translate(gp, dx, y));
                }
            }
        }

        static GraphicsPath Translate(GraphicsPath src, float dx, float dy)
        {
            GraphicsPath p = (GraphicsPath)src.Clone();
            using (Matrix m = new Matrix())
            {
                m.Translate(dx, dy);
                p.Transform(m);
            }
            return p;
        }

        public override void Draw(Graphics g, float x, float y, float fs, Font f, Color color, float barY)
        {
            float pad = fs * 0.26f;
            float cy = y + (H - Inner.H) / 2f;
            Inner.Draw(g, x + pad, cy, fs, f, color, barY);

            // 用真实字形 ( ) 纵向拉伸，得到标准的大括号形状
            DrawGlyph(g, "(", x + fs * 0.02f, y, fs, f, color, false);
            DrawGlyph(g, ")", x + W - fs * 0.02f, y, fs, f, color, true);
        }
    }

    /// <summary>把 ASCII 表达式解析成排版树。</summary>
    static class MathParser
    {
        sealed class Parser
        {
            readonly string s;
            int p;
            public Parser(string text) { s = text; }

            char Peek { get { while (p < s.Length && char.IsWhiteSpace(s[p])) p++; return p < s.Length ? s[p] : '\0'; } }

            public MathNode Parse()
            {
                MathNode n = ParseExpr();
                return n;
            }

            MathNode ParseExpr()
            {
                List<MathNode> parts = new List<MathNode>();
                parts.Add(ParseTerm());
                while (Peek == '+' || Peek == '-' || Peek == '\u2212')
                {
                    char op = s[p++];
                    MathNode rhs = ParseTerm();
                    TextNode sign = new TextNode(op == '+' ? "+" : "\u2212");
                    List<MathNode> list = new List<MathNode>();
                    list.Add(parts.Count == 1 ? parts[0] : Wrap(parts));
                    list.Add(sign);
                    list.Add(rhs);
                    parts = list;
                }
                return parts.Count == 1 ? parts[0] : Wrap(parts);
            }

            MathNode ParseTerm()
            {
                MathNode left = ParseFactor();
                while (Peek == '*' || Peek == '/' || Peek == '\u00d7' || Peek == '\u00f7')
                {
                    char op = s[p++];
                    if (op == '\u00d7') op = '*';
                    if (op == '\u00f7') op = '/';
                    MathNode right = ParseFactor();
                    if (op == '/')
                    {
                        FracNode fr = new FracNode();
                        fr.Num = left; fr.Den = right;
                        left = fr;
                    }
                    else
                    {
                        List<MathNode> list = new List<MathNode>();
                        list.Add(left);
                        list.Add(new TextNode("\u00d7"));
                        list.Add(right);
                        left = Wrap(list);
                    }
                }
                return left;
            }

            MathNode ParseFactor()
            {
                MathNode baseNode = ParsePrimary();
                while (true)
                {
                    char c = Peek;
                if (c == '=') { p++; return new TextNode("  =  "); }
                    if (c == '^')
                    {
                        p++;
                        MathNode exp = ParsePrimary();
                        PowerNode pw = new PowerNode(false);
                        pw.B = baseNode; pw.E = exp;
                        baseNode = pw;
                    }
                    else if (c == '!')
                    {
                        p++;
                        List<MathNode> list = new List<MathNode>();
                        list.Add(baseNode);
                        list.Add(new TextNode("!"));
                        baseNode = Wrap(list);
                    }
                    else break;
                }
                return baseNode;
            }

            MathNode ParsePrimary()
            {
                char c = Peek;
                if (c == '\0') return new TextNode("");
                if (c == '(')
                {
                    p++;
                    MathNode inner = ParseExpr();
                    if (Peek == ')') p++;
                    ParenNode pn = new ParenNode();
                    pn.Inner = inner;
                    return pn;
                }
                if (c == '\u221a' || c == '\u221b')   // √ ∛
                {
                    p++;
                    MathNode inner = ParsePrimary();
                    RootNode rn = new RootNode(c == '\u221b', false);
                    rn.Inner = inner;
                    return rn;
                }
                if (c >= '0' && c <= '9' || c == '.')
                {
                    int st = p;
                    while (p < s.Length && (s[p] >= '0' && s[p] <= '9' || s[p] == '.')) p++;
                    return new TextNode(s.Substring(st, p - st));
                }
                // 字母：函数名 + 括号参数，整体当文字
                if (char.IsLetter(c))
                {
                    int st = p;
                    while (p < s.Length && (char.IsLetter(s[p]) || s[p] == '(' || s[p] == ')' || s[p] == ',' || s[p] >= '0' && s[p] <= '9')) p++;
                    return new TextNode(s.Substring(st, p - st));
                }
                p++;
                return new TextNode(c.ToString());
            }

            /// <summary>把若干节点横向拼成一个行内块（用等宽间隙连接）。</summary>
            static MathNode Wrap(List<MathNode> parts)
            {
                RowNode row = new RowNode();
                row.Parts = parts;
                return row;
            }
        }

        public static MathNode Build(string expr)
        {
            try { return new Parser(expr).Parse(); }
            catch (Exception) { return new TextNode(expr ?? ""); }
        }
    }

    /// <summary>横向排列。</summary>
    sealed class RowNode : MathNode
    {
        public List<MathNode> Parts = new List<MathNode>();

        public override void Measure(Graphics g, float fs, Font f)
        {
            float w = 0, h = 0, b = 0;
            for (int i = 0; i < Parts.Count; i++)
            {
                Parts[i].Measure(g, fs, f);
                w += Parts[i].W;
                h = Math.Max(h, Parts[i].H);
                b = Math.Max(b, Parts[i].Base);
            }
            W = w; H = Math.Max(h, fs * 1.2f); Base = b;
        }

        public override void Draw(Graphics g, float x, float y, float fs, Font f, Color color, float barY)
        {
            float cx = x;
            for (int i = 0; i < Parts.Count; i++)
            {
                MathNode n = Parts[i];
                float ny = y + Base - n.Base;
                n.Draw(g, cx, ny, fs, f, color, barY);
                cx += n.W;
            }
        }
    }
}
