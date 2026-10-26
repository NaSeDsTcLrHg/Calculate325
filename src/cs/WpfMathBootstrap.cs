/*
 * Calculate325 —— WpfMath 运行时加载
 * ---------------------------------------------------------------------------
 * WpfMath.dll / WpfMath.Shared.dll 以资源形式嵌在 EXE 里。
 * 必须在任何 WpfMath 类型被 JIT 之前调用 Ensure()。
 */
using System;
using System.IO;
using System.Reflection;

namespace Calculate325
{
    static class WpfMathBootstrap
    {
        static bool ready;
        static Assembly shared;
        static Assembly math;

        public static void Ensure()
        {
            if (ready) return;
            ready = true;

            AppDomain.CurrentDomain.AssemblyResolve += delegate(object sender, ResolveEventArgs e)
            {
                string name = new AssemblyName(e.Name).Name;
                if (name == "WpfMath") return math;
                if (name == "WpfMath.Shared") return shared;
                return null;
            };

            shared = Load("WpfMathShared");
            math = Load("WpfMathDll");
        }

        static Assembly Load(string resource)
        {
            using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource))
            {
                if (s == null) throw new InvalidOperationException(resource + " missing");
                byte[] bytes = new byte[s.Length];
                int off = 0;
                while (off < bytes.Length)
                {
                    int n = s.Read(bytes, off, bytes.Length - off);
                    if (n <= 0) break;
                    off += n;
                }
                return Assembly.Load(bytes);
            }
        }
    }
}
