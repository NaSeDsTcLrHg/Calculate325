/*
 * Calculate325 —— 随机窗口的完整 LaTeX 渲染
 * ---------------------------------------------------------------------------
 * WpfMath（OpenType MATH）+ Cambria Math：
 *   · 数字用正体，变量用 \mathit，避免斜体数字高低不齐；
 *   · 积分上下限改成参考图那样的右侧位置。
 */
using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WpfMath;
using WpfMath.Parsers;
using WpfMath.Rendering;

namespace Calculate325
{
    static class WpfMathRenderer
    {
        /// <summary>上一次渲染实际排成了几行（1 = 单行）。</summary>
        public static int LastLines = 1;

        public static Bitmap Render(string latex, double scale, System.Drawing.Color ink, System.Drawing.Color background)
        {
            WpfMathBootstrap.Ensure();

            LastLines = 1;                       // 单行渲染（长式子靠 MathBox 的缩放看）
            string prepared = ItalicVariables(latex);
            TexFormula formula = WpfTeXFormulaParser.Instance.Parse(prepared, "mathrm");
            SideIntegralLimits(formula);

            TexEnvironment env = WpfTeXEnvironment.Create(
                TexStyle.Display, scale, "Cambria Math",
                Brush(ink), Brush(background));

            BitmapSource src = WpfTeXFormulaExtensions.RenderToBitmap(formula, env, scale, 14, 14, 96);
            PngBitmapEncoder enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(src));
            using (MemoryStream ms = new MemoryStream())
            {
                enc.Save(ms);
                ms.Position = 0;
                using (Bitmap bmp = new Bitmap(ms))
                {
                    return new Bitmap(bmp);
                }
            }
        }

        /// <summary>
        /// <summary>数字/函数名正体；只把变量 x/n/k/N 包成 \mathit，保持斜体。</summary>
        static string ItalicVariables(string latex)
        {
            StringBuilder sb = new StringBuilder(latex.Length + 32);
            for (int i = 0; i < latex.Length; i++)
            {
                char c = latex[i];
                if (c == '\\')
                {
                    sb.Append(c);
                    i++;
                    int start = i;
                    while (i < latex.Length && char.IsLetter(latex[i]))
                    {
                        sb.Append(latex[i]);
                        i++;
                    }
                    string cmd = latex.Substring(start, i - start);
                    // \begin{pmatrix} / \end{pmatrix} 的环境名不能做变量替换
                    if (cmd == "begin" || cmd == "end")
                    {
                        while (i < latex.Length && char.IsWhiteSpace(latex[i])) { sb.Append(latex[i]); i++; }
                        if (i < latex.Length && latex[i] == '{')
                        {
                            int depth = 0;
                            while (i < latex.Length)
                            {
                                char cc = latex[i];
                                sb.Append(cc);
                                if (cc == '{') depth++;
                                else if (cc == '}') { depth--; if (depth == 0) { i++; break; } }
                                i++;
                            }
                        }
                    }
                    i--;
                    continue;
                }
                if (c == 'x' || c == 'n' || c == 'k' || c == 'N')
                {
                    sb.Append("\\mathit{").Append(c).Append('}');
                }
                else
                {
                    sb.Append(c);
                }
            }
            return sb.ToString();
        }

        /// <summary>把 \int/\iint/\iiint 的上下限从正上/正下改到右侧。</summary>
        static void SideIntegralLimits(TexFormula formula)
        {
            try
            {
                PropertyInfo root = formula.GetType().GetProperty("RootAtom",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (root == null) return;
                Walk(root.GetValue(formula, null), new HashSet<object>());
            }
            catch (Exception) { }
        }

        static void Walk(object obj, HashSet<object> seen)
        {
            if (obj == null || obj is string) return;
            Type t = obj.GetType();
            if (t.FullName == null || !t.FullName.StartsWith("WpfMath.")) return;
            if (!seen.Add(obj)) return;

            if (t.Name == "BigOperatorAtom")
            {
                PropertyInfo bp = t.GetProperty("BaseAtom");
                object baseAtom = bp == null ? null : bp.GetValue(obj, null);
                string name = null;
                if (baseAtom != null)
                {
                    PropertyInfo np = baseAtom.GetType().GetProperty("Name");
                    if (np != null) name = np.GetValue(baseAtom, null) as string;
                }
                if (name == "int" || name == "iint" || name == "iiint" || name == "oint")
                {
                    PropertyInfo up = t.GetProperty("UseVerticalLimits");
                    if (up != null) up.SetValue(obj, false, null);
                }
            }

            FieldInfo[] fields = t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            for (int i = 0; i < fields.Length; i++)
            {
                object v;
                try { v = fields[i].GetValue(obj); }
                catch (Exception) { continue; }
                if (v == null || v is string) continue;
                IEnumerable seq = v as IEnumerable;
                if (seq != null)
                {
                    foreach (object item in seq) Walk(item, seen);
                }
                else if (v.GetType().FullName != null && v.GetType().FullName.StartsWith("WpfMath."))
                {
                    Walk(v, seen);
                }
            }
        }

        static SolidColorBrush Brush(System.Drawing.Color c)
        {
            SolidColorBrush b = new SolidColorBrush(System.Windows.Media.Color.FromArgb(c.A, c.R, c.G, c.B));
            b.Freeze();
            return b;
        }
    }
}
