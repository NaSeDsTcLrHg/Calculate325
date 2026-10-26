using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Calculate325
{
    /// <summary>
    /// 让子窗口的原生标题栏（含最小化/关闭按钮）跟随皮肤配色。
    /// Win11 用 DWMWA_CAPTION_COLOR/TEXT_COLOR/BORDER_COLOR 直接染色；
    /// Win10 只支持深浅模式，退化成跟随亮/暗。
    /// </summary>
    static class ThemedCaption
    {
        [DllImport("dwmapi.dll", PreserveSig = true)]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        const int DWMWA_BORDER_COLOR = 34;
        const int DWMWA_CAPTION_COLOR = 35;
        const int DWMWA_TEXT_COLOR = 36;

        static int ColorRef(Color c)
        {
            // COLORREF = 0x00BBGGRR
            return (c.B << 16) | (c.G << 8) | c.R;
        }

        static int Luma(Color c)
        {
            return (c.R * 30 + c.G * 59 + c.B * 11) / 100;
        }

        /// <summary>给窗口装上主题标题栏（句柄创建后调用；失败静默）。</summary>
        public static void Apply(Form f, Skin s)
        {
            if (f == null || s == null) return;
            try
            {
                IntPtr h = f.Handle;
                if (h == IntPtr.Zero) return;
                bool dark = s.Dark || Luma(s.TitleBack) < 128;
                int dm = dark ? 1 : 0;
                DwmSetWindowAttribute(h, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dm, sizeof(int));

                int cap = ColorRef(s.TitleBack);
                DwmSetWindowAttribute(h, DWMWA_CAPTION_COLOR, ref cap, sizeof(int));

                int txt = ColorRef(s.TitleInk);
                DwmSetWindowAttribute(h, DWMWA_TEXT_COLOR, ref txt, sizeof(int));

                int bd = ColorRef(s.TitleLine);
                DwmSetWindowAttribute(h, DWMWA_BORDER_COLOR, ref bd, sizeof(int));
            }
            catch (Exception) { }
        }

        /// <summary>在 HandleCreated 时自动应用（订阅一次即可）。</summary>
        public static void Hook(Form f, Skin s)
        {
            if (f == null) return;
            if (f.IsHandleCreated) Apply(f, s);
            f.HandleCreated += delegate { Apply(f, s); };
        }
    }
}
