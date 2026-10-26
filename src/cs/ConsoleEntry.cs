/*
 * Calculate325 —— 控制台自检入口（用于开发验证：node 侧只跑 JS，C# 侧跑这个）
 * 编译：见 build-exe.ps1
 * 运行： Calculate325-tests.exe [expected-js.tsv 路径]
 */
using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace Calculate325
{
    public static class ConsoleEntry
    {
        public static int Main(string[] args)
        {
            if (args.Length > 0 && args[0] == "--audit-test")
            {
                // --audit-test <outfile> [target] [每级条数] [随机种子]
                string outPath = args.Length > 1 ? args[1] : "audit-test.txt";
                string target = args.Length > 2 ? args[2] : "325";
                int count = args.Length > 3 ? int.Parse(args[3], CultureInfo.InvariantCulture) : 8;
                int seed = args.Length > 4 ? int.Parse(args[4], CultureInfo.InvariantCulture) : 20261021;
                string auditReport = AuditSelfTest.Run(target, count, seed);
                File.WriteAllText(outPath, auditReport, new UTF8Encoding(false));
                Console.OutputEncoding = Encoding.UTF8;
                Console.Write(auditReport);
                return AuditSelfTest.LastExitCode;
            }
            if (args.Length > 0 && args[0] == "--audit-one")
            {
                // --audit-one "<latex>"：单独求值一条式子（调试/交叉校验用）
                Console.OutputEncoding = Encoding.UTF8;
                double v;
                string err;
                bool ok = FormulaAudit.TryValue(args.Length > 1 ? args[1] : "", out v, out err);
                Console.WriteLine(ok ? v.ToString("R", CultureInfo.InvariantCulture) : "ERROR " + err);
                return ok ? 0 : 1;
            }
            if (args.Length > 0 && args[0] == "--audit")
            {
                // --audit：独立校验器自检（只测校验器本身，不测生成器）
                Console.OutputEncoding = Encoding.UTF8;
                string auditReport = FormulaAudit.SelfCheck();
                Console.Write(auditReport);
                return auditReport.IndexOf(" 失败 0 ", StringComparison.Ordinal) >= 0 ? 0 : 1;
            }
            if (args.Length > 0 && args[0] == "--dump-random")
            {
                // --dump-random <out.jsonl> [target] [levels] [count] [randSeed]
                string outPath = args.Length > 1 ? args[1] : "random-dump.jsonl";
                string target = args.Length > 2 ? args[2] : "325";
                string levels = args.Length > 3 ? args[3] : "12345";
                int count = args.Length > 4 ? int.Parse(args[4], CultureInfo.InvariantCulture) : 20;
                int seed = args.Length > 5 ? int.Parse(args[5], CultureInfo.InvariantCulture) : 20261021;
                Console.OutputEncoding = Encoding.UTF8;
                Console.Write(RandomDump.Run(outPath, target, levels, count, seed));
                return 0;
            }
            if (args.Length > 0 && args[0] == "--random-test")
            {
                string outPath = args.Length > 1 ? args[1] : "random-test.txt";
                string target = args.Length > 2 ? args[2] : "325";
                int count = args.Length > 3 ? int.Parse(args[3], CultureInfo.InvariantCulture) : 20;
                string randomReport = RandomSelfTest.Run(target, count);
                File.WriteAllText(outPath, randomReport, new UTF8Encoding(false));
                Console.OutputEncoding = Encoding.UTF8;
                Console.Write(randomReport);
                return RandomSelfTest.LastExitCode;
            }
            string tsv = args.Length > 0 ? args[0] : Path.Combine("tests", "expected-js.tsv");
            string report = SelfTest.Run(tsv);
            Console.OutputEncoding = Encoding.UTF8;
            Console.Write(report);
            return SelfTest.LastExitCode;
        }
    }
}
