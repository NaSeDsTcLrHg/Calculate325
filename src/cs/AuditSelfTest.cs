/*
 * Calculate325 —— 独立校验自检（--audit-test）
 * ---------------------------------------------------------------------------
 * 三件事，缺一不可：
 *   1) 正向：L1–L5 各生成 N 条，全部要通过 FormulaAudit 的独立后置校验，
 *      且算出来的值确实等于目标（不是“生成器说它等于”）。
 *   2) 重试：BuildLevelGated 在审计失败时会换种子重生成，报告最终种子和重试次数。
 *   3) 负向对照：把同一批式子故意改坏（整体乘 2 / 加 1 / 换目标值），
 *      校验器必须拒绝。这证明校验不是恒真。
 */
using System;
using System.Globalization;
using System.Numerics;
using System.Text;

namespace Calculate325
{
    public static class AuditSelfTest
    {
        public static int LastExitCode;

        public static string Run(string targetText, int count, int randSeed)
        {
            if (count < 1) count = 1;
            if (count > 60) count = 60;
            TargetParse tp = Solver.ParseTarget(string.IsNullOrEmpty(targetText) ? "325" : targetText);
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("TARGET=" + (targetText == null ? "" : targetText));
            if (!tp.Ok)
            {
                sb.AppendLine("ERROR=" + tp.Error);
                sb.AppendLine("===== 独立校验自检：通过 0 / 失败 0 =====");
                LastExitCode = 2;
                return sb.ToString();
            }
            Rat target = tp.Value;
            double targetD = Solver.ToDouble(target.N) / Solver.ToDouble(target.D);
            sb.AppendLine("COUNT=" + count.ToString(CultureInfo.InvariantCulture) + "（每级）");
            sb.AppendLine("RAND_SEED=" + randSeed.ToString(CultureInfo.InvariantCulture));

            int pass = 0, fail = 0;
            Random rnd = new Random(randSeed);

            // ---------- 1. 正向 ----------
            sb.AppendLine("LEVEL_BEGIN");
            sb.AppendLine("LEVEL\tSEED\tFINAL_SEED\tKIND\tLAYERS\tDIMS\tVALUE\tAUDIT_OK\tTIGHT\tTRIES\tLATEX\tDETAIL");
            for (int level = 1; level <= 5; level++)
            {
                for (int i = 0; i < count; i++)
                {
                    string seed = level == 5 ? RandomExpr.MakeL5Seed(rnd) : RandomExpr.MakeSeed(level, rnd);
                    RandomExprResult r = null;
                    string error = "";
                    try { r = RandomExpr.BuildLevelGated(level, target, rnd, seed, true); }
                    catch (Exception ex) { error = ex.ToString(); }

                    bool ok = r != null && r.AuditOk
                        && Math.Abs(r.AuditValue - targetD) <= 1e-6 * Math.Max(1.0, Math.Abs(targetD))
                        && !string.IsNullOrEmpty(r.Latex)
                        && (r.Seed != null && r.Seed.Length == RandomExpr.SeedLength(level));
                    if (ok) pass++; else fail++;

                    sb.Append(level.ToString(CultureInfo.InvariantCulture)).Append('\t');
                    sb.Append(seed).Append('\t');
                    sb.Append(r == null ? "" : r.Seed).Append('\t');
                    sb.Append(Clean(r == null ? "" : r.Kind)).Append('\t');
                    sb.Append(r == null ? "0" : r.Layers.ToString(CultureInfo.InvariantCulture)).Append('\t');
                    sb.Append(r == null ? "0" : r.Dims.ToString(CultureInfo.InvariantCulture)).Append('\t');
                    sb.Append(r == null ? "" : r.AuditValue.ToString("R", CultureInfo.InvariantCulture)).Append('\t');
                    sb.Append(ok ? "1" : "0").Append('\t');
                    sb.Append(r != null && r.AuditTight ? "1" : "0").Append('\t');
                    sb.Append(r == null ? "0" : r.AuditTries.ToString(CultureInfo.InvariantCulture)).Append('\t');
                    sb.Append(Clean(r == null ? "" : r.Latex)).Append('\t');
                    sb.Append(Clean(error.Length > 0 ? error : (r == null ? "null" : r.AuditDetail))).AppendLine();
                }
            }
            sb.AppendLine("LEVEL_END");

            // ---------- 2. 负向对照 ----------
            sb.AppendLine("NEGATIVE_BEGIN");
            sb.AppendLine("CASE\tMUTATION\tBASE_VALUE\tGOT_VALUE\tREJECTED\tNOTE");
            string[] levelsToTest = new string[] { "1", "3", "4", "5" };
            for (int i = 0; i < levelsToTest.Length; i++)
            {
                int level = int.Parse(levelsToTest[i], CultureInfo.InvariantCulture);
                string seed = level == 5 ? RandomExpr.MakeL5Seed(rnd) : RandomExpr.MakeSeed(level, rnd);
                RandomExprResult r = RandomExpr.BuildLevelGated(level, target, rnd, seed, true);
                if (r == null || !r.AuditOk)
                {
                    sb.Append("L").Append(level).Append("-base\t基准本身未通过\t-\t-\t0\t").AppendLine();
                    fail++;
                    continue;
                }
                string body = StripClaim(r.Latex);
                Negative(sb, "L" + level + "-x2", "整体乘 2", "2\\cdot\\left(" + body + "\\right)", target, targetD, ref pass, ref fail);
                Negative(sb, "L" + level + "-p1", "整体加 1", "\\left(" + body + "\\right)+1", target, targetD, ref pass, ref fail);
                Negative(sb, "L" + level + "-claim", "只改声称值", body, new Rat(new BigInteger(326), BigInteger.One), targetD, ref pass, ref fail);
            }
            sb.AppendLine("NEGATIVE_END");

            sb.AppendLine("===== 独立校验自检：通过 " + pass.ToString(CultureInfo.InvariantCulture)
                + " / 失败 " + fail.ToString(CultureInfo.InvariantCulture) + " =====");
            LastExitCode = fail == 0 ? 0 : 1;
            return sb.ToString();
        }

        /// <summary>改坏的式子必须被拒绝；拒绝计通过，放行计失败。</summary>
        static void Negative(StringBuilder sb, string name, string mutation, string latex, Rat claim, double baseValue, ref int pass, ref int fail)
        {
            FormulaAuditResult a = FormulaAudit.Audit(latex, claim);
            bool rejected = a == null || !a.Ok;
            if (rejected) pass++; else fail++;
            sb.Append(name).Append('\t');
            sb.Append(mutation).Append('\t');
            sb.Append(baseValue.ToString("R", CultureInfo.InvariantCulture)).Append('\t');
            sb.Append(a == null ? "null" : a.Value.ToString("R", CultureInfo.InvariantCulture)).Append('\t');
            sb.Append(rejected ? "1" : "0").Append('\t');
            sb.Append(Clean(a == null ? "null" : a.Detail)).AppendLine();
        }

        static string StripClaim(string latex)
        {
            string s = latex ?? "";
            int k = s.LastIndexOf(" = ", StringComparison.Ordinal);
            return k > 0 ? s.Substring(0, k) : s;
        }

        static string Clean(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
        }
    }
}
