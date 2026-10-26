/*
 * Calculate325 —— 自检（控制台测试程序与 exe 的 --selftest 共用）
 * 1) 与 JS 版（src/solver.js）逐条对齐：读取 tests/expected-js.tsv 比对算式字符串；
 * 2) 内部一致性：校验器、覆盖率、保底构造质量。
 */
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Calculate325
{
    public static class SelfTest
    {
        public static int LastExitCode = 0;

        static int pass = 0, fail = 0;
        static List<string> failures = new List<string>();

        static void Check(string name, bool cond, string extra = null)
        {
            if (cond) pass++;
            else { fail++; failures.Add(name + (string.IsNullOrEmpty(extra) ? "" : "  →  " + extra)); }
        }

        static Limits Lim(int maxBlocks, string orderMode, bool allowDecimal, bool allowFactorial, int factMax)
        {
            Limits l = new Limits();
            l.MaxBlocks = maxBlocks;
            l.OrderMode = orderMode;
            l.AllowDecimal = allowDecimal;
            l.AllowFactorial = allowFactorial;
            l.FactMax = factMax;
            l.TimeBudgetMs = 30000;
            return l;
        }

        static bool Verified(string expr, Rat target, string orderMode, string pattern)
        {
            VerifyResult vr = Solver.Verify(expr, target, true, orderMode, pattern);
            return vr.DigitsOk && vr.ValueOk;
        }

        public static string Run(string tsvPath)
        {
            pass = 0; fail = 0; failures = new List<string>();
            StringBuilder log = new StringBuilder();

            Section(log, "=== 已知答案（与 JS 版一致） ===");
            object[][] known = new object[][] {
                new object[] { "325", 1, "325" },
                new object[] { "1", 1, "3*2-5" },
                new object[] { "0", 1, "3+2-5" },
                new object[] { "-1", 1, "3!-2-5" },
                new object[] { "3.25", 1, "3.25" },
                new object[] { "6", 1, "3-2+5" },
                new object[] { "32.5", 1, "32.5" },
                new object[] { "0.5", 1, "3-2.5" },
                new object[] { "10", 1, "3+2+5" },
                new object[] { "2", 2, "3!*25/3/25" },
                new object[] { "1000", 2, "325*3+25" },
                new object[] { "2025", 2, "3*25*(32-5)" },
                new object[] { "62", 2, "32-5!+3!*25" },
                new object[] { "777", 2, "32+5!*3!+25" },
                new object[] { "105625", 2, "325*325" },
                new object[] { "325325", 2, "325325" }
            };
            foreach (object[] c in known)
            {
                string target = (string)c[0];
                int maxBlocks = (int)c[1];
                string expected = (string)c[2];
                SolveResult r = Solver.SolveOnce(target, Lim(maxBlocks, "fixed", true, true, 12));
                bool ok = r.Ok && r.Expression == expected;
                Check("已知答案 " + target, ok, r.Ok ? ("得到 " + r.Expression + "，期望 " + expected) : "无解");
                if (r.Ok) Check("已知答案 " + target + " 校验", Verified(r.Expression, r.Target, "fixed", r.Pattern), r.Expression);
                log.AppendLine("  " + target.PadLeft(8) + " → " + (r.Ok ? r.Expression : "(无解)") + "   " + r.ElapsedMs + "ms");
            }

            Section(log, "=== 校验器 ===");
            Check("fixed 拒绝乱序数字", !Solver.Verify("5-2-3", Rats.Zero, true, "fixed").DigitsOk);
            Check("any 接受乱序数字", Solver.Verify("5-2-3", Rats.Zero, true, "any").DigitsOk);
            Check("3+2-5 = 0", Solver.Verify("3+2-5", Rats.Zero, true, "fixed").ValueOk);
            Check("3+2-5 ≠ 1", !Solver.Verify("3+2-5", Rats.One, true, "fixed").ValueOk);
            Check("3! = 6", Solver.Verify("3!", Rats.FromInt(6), true, "fixed").ValueOk);
            Check("3.25 = 13/4", Solver.Verify("3.25", new Rat(13, 4), true, "fixed").ValueOk);
            Check("除零报错", Solver.Verify("3/(2-2)", Rats.Zero, true, "fixed").Error != null);
            Check("CountTokens", Solver.CountTokens("325*3+25") == 3, Solver.CountTokens("325*3+25").ToString(CultureInfo.InvariantCulture));
            Check("Pretty", Solver.Pretty("3*2-5/2") == "3×2−5÷2", Solver.Pretty("3*2-5/2"));

            Section(log, "=== 覆盖率（-40..80，最多 2 块，会话复用） ===");
            {
                Solver.Session session = new Solver.Session(Lim(2, "fixed", true, true, 12));
                int solved = 0;
                List<string> bad = new List<string>();
                for (int n = -40; n <= 80; n++)
                {
                    SolveResult r = session.Solve(n.ToString(CultureInfo.InvariantCulture));
                    if (!r.Ok) { bad.Add(n.ToString(CultureInfo.InvariantCulture) + "(无解)"); continue; }
                    if (!Verified(r.Expression, r.Target, "fixed", r.Pattern)) bad.Add(n.ToString(CultureInfo.InvariantCulture) + "(校验失败:" + r.Expression + ")");
                    else solved++;
                }
                log.AppendLine("  直接解出 " + solved + "/121，缓存子串 " + session.CacheSize);
                if (bad.Count > 0) log.AppendLine("  未通过：" + string.Join(", ", bad.ToArray()));
                Check("覆盖率 ≥ 120/121", solved >= 120, "solved=" + solved);
                Check("覆盖率样例全部合法", bad.Count == 0, string.Join(", ", bad.ToArray()));
            }

            Section(log, "=== 保底构造（最多 1 块必然搜不到） ===");
            string[] bigTargets = new string[] { "12345", "99999", "123456", "1000000", "987654321", "-98765", "1234.5", "-3.14159" };
            foreach (string t in bigTargets)
            {
                SolveResult r = Solver.SolveOnce(t, Lim(1, "fixed", true, true, 12));
                if (r.Ok) { log.AppendLine("  " + t + " → 1 块内直接解出 " + r.Expression); continue; }
                FallbackInfo fb = r.FallbackResult;
                Check("保底存在 " + t, fb != null, string.Join(" | ", r.Notes.ToArray()));
                if (fb == null) { log.AppendLine("  " + t + " → 无保底"); continue; }
                Check("保底合法 " + t, Verified(fb.Expression, r.Target, "fixed", r.Pattern), fb.Expression);
                Check("保底不太长 " + t, fb.Expression.Length <= 300, "len=" + fb.Expression.Length);
                log.AppendLine("  " + t + " → 保底 " + fb.Blocks + " 块 / " + fb.Expression.Length + " 字符");
            }

            Section(log, "=== 小数与负数 ===");
            foreach (string t in new string[] { "3.25", "-3.25", "-325", "0.5", "1234.5", "-0.4" })
            {
                SolveResult r = Solver.SolveOnce(t, Lim(2, "fixed", true, true, 12));
                if (r.Ok) Check("小数/负数 " + t, Verified(r.Expression, r.Target, "fixed", r.Pattern), r.Expression);
                log.AppendLine("  " + t + " → " + (r.Ok ? r.Expression : (r.FallbackResult != null ? "(保底) " + r.FallbackResult.Expression : "(无解)")));
            }

            Section(log, "=== 多种数字串（325 / 799 / 325799 / 114514） ===");
            {
                string[] pats = new string[] { "325", "799", "325799", "114514" };
                foreach (string p in pats)
                {
                    foreach (string t in new string[] { "1", "0", "-1", "10", "1000", "3.25" })
                    {
                        Limits l = Lim(2, "fixed", true, true, 12);
                        l.Pattern = p;
                        SolveResult r = Solver.SolveOnce(t, l);
                        string expr = r.Ok ? r.Expression : (r.FallbackResult != null ? r.FallbackResult.Expression : null);
                        Check("数字串 " + p + " 目标 " + t, expr != null, "既无解也无保底");
                        if (expr != null) Check("数字串 " + p + " 目标 " + t + " 合法", Verified(expr, r.Target, "fixed", p), expr);
                    }
                    log.AppendLine("  " + p + " 已完成 6 个目标");
                }
                // 同一算式在不同数字串下必须被拒绝（数字序列检查生效）
                Check("325 的式子不能算作 799 的式子",
                    !Solver.Verify("3*2-5", Rats.One, true, "fixed", "799").DigitsOk);
                Check("799 的式子不能算作 325 的式子",
                    !Solver.Verify("7*(9-9)", Rats.Zero, true, "fixed", "325").DigitsOk);
                Check("114514 的式子判定",
                    Solver.Verify("(1-1)*4514", Rats.Zero, true, "fixed", "114514").DigitsOk);
            }

            Section(log, "=== 多种数字串（325 / 799 / 325799 / 114514） ===");
            {
                string[] pats = new string[] { "325", "799", "325799", "114514" };
                foreach (string p in pats)
                {
                    foreach (string t in new string[] { "1", "0", "-1", "10", "1000", "3.25" })
                    {
                        Limits l = Lim(2, "fixed", true, true, 12);
                        l.Pattern = p;
                        SolveResult r = Solver.SolveOnce(t, l);
                        string expr = r.Ok ? r.Expression : (r.FallbackResult != null ? r.FallbackResult.Expression : null);
                        Check("数字串 " + p + " 目标 " + t, expr != null, "既无解也无保底");
                        if (expr != null) Check("数字串 " + p + " 目标 " + t + " 合法", Verified(expr, r.Target, "fixed", p), expr);
                    }
                    log.AppendLine("  " + p + " 已完成 6 个目标");
                }
                Check("325 的式子不能算作 799 的式子", !Solver.Verify("3*2-5", Rats.One, true, "fixed", "799").DigitsOk);
                Check("799 的式子不能算作 325 的式子", !Solver.Verify("7*(9-9)", Rats.Zero, true, "fixed", "325").DigitsOk);
                Check("114514 的式子判定", Solver.Verify("(1-1)*4514", Rats.Zero, true, "fixed", "114514").DigitsOk);
            }

            Section(log, "=== 与 JS 版逐条对齐 ===");
            if (string.IsNullOrEmpty(tsvPath) || !File.Exists(tsvPath))
            {
                log.AppendLine("  （跳过：未找到 " + (tsvPath == null ? "expected-js.tsv" : tsvPath) + "）");
            }
            else
            {
                string[] lines = File.ReadAllLines(tsvPath, Encoding.UTF8);
                int rows = 0, mismatch = 0;
                for (int i = 1; i < lines.Length; i++)
                {
                    string line = lines[i];
                    if (line.Trim().Length == 0) continue;
                    string[] f = line.Split('\t');
                    if (f.Length < 10) continue;
                    rows++;
                    string target = f[0];
                    Limits lim = Lim(int.Parse(f[1], CultureInfo.InvariantCulture), f[2],
                                     f[3] == "1", f[4] == "1", int.Parse(f[5], CultureInfo.InvariantCulture));
                    if (f.Length > 10 && f[10].Length > 0) lim.Pattern = f[10];
                    bool jsOk = f[6] == "1";
                    string jsExpr = f[7] == "-" ? null : f[7];
                    string jsFallback = f[9] == "-" ? null : f[9];
                    SolveResult r = Solver.SolveOnce(target, lim);
                    if (jsOk)
                    {
                        if (!r.Ok || r.Expression != jsExpr)
                        {
                            mismatch++;
                            Check("对齐 " + target, false, "JS=" + jsExpr + " C#=" + (r.Ok ? r.Expression : "(无解)"));
                        }
                        else Check("对齐 " + target, true, null);
                    }
                    else
                    {
                        string mine = r.FallbackResult != null ? r.FallbackResult.Expression : null;
                        if (mine != jsFallback)
                        {
                            mismatch++;
                            Check("对齐(保底) " + target, false, "JS=" + (jsFallback == null ? "(无)" : jsFallback) + " C#=" + (mine == null ? "(无)" : mine));
                        }
                        else Check("对齐(保底) " + target, true, null);
                    }
                }
                log.AppendLine("  比对 " + rows + " 条，不一致 " + mismatch + " 条");
                Check("与 JS 版完全一致", mismatch == 0, "mismatch=" + mismatch);
            }

            Section(log, "");
            log.AppendLine("===== C# 自检：通过 " + pass + " / 失败 " + fail + " =====");
            if (failures.Count > 0)
            {
                log.AppendLine("失败项：");
                foreach (string f in failures) log.AppendLine("  - " + f);
            }
            LastExitCode = fail == 0 ? 0 : 1;
            return log.ToString();
        }

        static void Section(StringBuilder log, string title)
        {
            if (title.Length > 0) log.AppendLine(title);
        }
    }
}
