/*
 * Calculate325 —— 随机窗口专用数学字体
 * ---------------------------------------------------------------------------
 * 内嵌 AxMath 字体 + Latin Modern Roman：
 *   · AxMath.ttf：∫ Σ √ π ∞ 等数学符号
 *   · Latin Modern Roman Italic：变量 x / n / k
 *   · Latin Modern Roman Regular：数字、sin/cos/ln/lim 等函数名
 * 通过 PrivateFontCollection 从 EXE 资源加载，不依赖系统安装字体。
 */
using System;
using System.Drawing;
using System.Drawing.Text;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Calculate325
{
    static class MathFonts
    {
        static PrivateFontCollection pfcItalic, pfcUpright, pfcAx;
        static FontFamily familyItalic, familyUpright, familyAx;
        static byte[] bytesItalic, bytesUpright, bytesAx;
        static GCHandle pinItalic, pinUpright, pinAx;
        static string name = "Cambria Math";

        public static string Name { get { Ensure(); return name; } }
        public static FontFamily Family { get { Ensure(); return familyAx; } }

        static void Ensure()
        {
            if (familyAx != null) return;
            try
            {
                familyAx = Load("AxMathFont", out pfcAx, out bytesAx, out pinAx);
                familyItalic = familyAx;
                familyUpright = Load("MathUpright", out pfcUpright, out bytesUpright, out pinUpright);
                name = familyAx.Name;
            }
            catch
            {
                familyAx = new FontFamily("Cambria Math");
                familyItalic = familyAx;
                familyUpright = familyAx;
                name = "Cambria Math";
            }
        }

        static FontFamily Load(string resource, out PrivateFontCollection pfc, out byte[] bytes, out GCHandle pin)
        {
            pfc = null; bytes = null; pin = default(GCHandle);
            using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource))
            {
                if (s == null) throw new InvalidOperationException(resource + " missing");
                bytes = new byte[s.Length];
                int off = 0;
                while (off < bytes.Length)
                {
                    int n = s.Read(bytes, off, bytes.Length - off);
                    if (n <= 0) break;
                    off += n;
                }
            }
            pin = GCHandle.Alloc(bytes, GCHandleType.Pinned);
            pfc = new PrivateFontCollection();
            pfc.AddMemoryFont(pin.AddrOfPinnedObject(), bytes.Length);
            if (pfc.Families.Length == 0) throw new InvalidOperationException(resource + " has no family");
            return pfc.Families[0];
        }

        public static Font CreateMath(float size, FontStyle style)
        {
            return CreateMath(size, style, GraphicsUnit.Point);
        }

        public static Font CreateMath(float size, FontStyle style, GraphicsUnit unit)
        {
            Ensure();
            try { return new Font(familyAx, size, style, unit); }
            catch { return new Font("Cambria Math", size, style, unit); }
        }

        public static Font CreateMathSymbol(float size)
        {
            Ensure();
            try { return new Font(familyAx, size, FontStyle.Regular, GraphicsUnit.Pixel); }
            catch { return new Font("Cambria Math", size, FontStyle.Regular, GraphicsUnit.Pixel); }
        }

        public static Font CreateAxMath(float size)
        {
            Ensure();
            try { return new Font(familyAx, size, FontStyle.Regular, GraphicsUnit.Pixel); }
            catch { return new Font("Cambria Math", size, FontStyle.Regular, GraphicsUnit.Pixel); }
        }

        public static Font CreateItalic(float size)
        {
            return CreateItalic(size, GraphicsUnit.Point);
        }

        public static Font CreateItalic(float size, GraphicsUnit unit)
        {
            Ensure();
            try { return new Font(familyItalic, size, FontStyle.Italic, unit); }
            catch { return CreateMath(size, FontStyle.Italic, unit); }
        }

        public static Font CreateUpright(float size)
        {
            return CreateUpright(size, GraphicsUnit.Point);
        }

        public static Font CreateUpright(float size, GraphicsUnit unit)
        {
            Ensure();
            try { return new Font(familyUpright, size, FontStyle.Regular, unit); }
            catch { return CreateMath(size, FontStyle.Regular, unit); }
        }
    }
}
