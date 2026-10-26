/*
 * Calculate325 —— 随机式子导出（供独立校验器使用）
 * ---------------------------------------------------------------------------
 * 只负责“把生成结果原样写成机器可读的 JSONL”，不做任何校验：
 * 校验必须由独立实现（tests/verify 的 Python 校验器 / EXE 内的 FormulaAudit）
 * 从 Latex 或 Expression 重新算一遍，才有意义。
 *
 * 用法：
 *   Calculate325-tests.exe --dump-random out.jsonl [target] [levels] [count] [randSeed]
 *     target   : 默认 325
 *     levels   : 默认 "12345"，只写要导出的等级，如 "45"
 *     count    : 每级条数，默认 20
 *     randSeed : 默认 20261021，决定种子序列
 */
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Calculate325
{
    public static class RandomDump
    {
        public static string Run(string outPath, string targetText, string levels, int count, int randSeed)
        {
            if (count < 1) count = 1;
            if (count > 500) count = 500;
            TargetParse tp = Solver.ParseTarget(string.IsNullOrEmpty(targetText) ? "325" : targetText);
            if (!tp.Ok) return "ERROR=" + tp.Error;

            if (string.IsNullOrEmpty(levels)) levels = "12345";
            bool want1 = levels.IndexOf('1') >= 0;
            bool want2 = levels.IndexOf('2') >= 0;
            bool want3 = levels.IndexOf('3') >= 0;
            bool want4 = levels.IndexOf('4') >= 0;
            bool want5 = levels.IndexOf('5') >= 0;

            StringBuilder sb = new StringBuilder(1 << 20);
            Random rnd = new Random(randSeed);
            int total = 0, auditFail = 0;
            for (int level = 1; level <= 5; level++)
            {
                bool want = (level == 1 && want1) || (level == 2 && want2) || (level == 3 && want3)
                    || (level == 4 && want4) || (level == 5 && want5);
                if (!want) continue;
                for (int i = 0; i < count; i++)
                {
                    string seed = level == 5
                        ? RandomExpr.MakeL5Seed(rnd)
                        : RandomExpr.MakeSeed(level, rnd);
                    RandomExprResult r = null;
                    string error = null;
                    try
                    {
                        r = level == 5
                            ? RandomExpr.BuildL5(seed, tp.Value)
                            : RandomExpr.BuildLevel(level, tp.Value, rnd, seed, true);
                    }
                    catch (Exception ex) { error = ex.Message; }
                    if (r == null)
                    {
                        sb.Append("{\"level\":").Append(level.ToString(CultureInfo.InvariantCulture))
                          .Append(",\"seed\":\"").Append(Json(seed)).Append('"')
                          .Append(",\"error\":\"").Append(Json(error ?? "null result")).Append("\"}\n");
                        auditFail++;
                        continue;
                    }
                    total++;
                    FormulaAuditResult a;
                    double ms = 0.0;
                    if (r.Audited)
                    {
                        a = new FormulaAuditResult();
                        a.Ok = r.AuditOk;
                        a.Value = r.AuditValue;
                        a.ErrorBound = r.AuditError;
                        a.RelError = r.AuditRel;
                        a.Mode = r.AuditMode;
                        a.Detail = r.AuditDetail;
                    }
                    else
                    {
                        DateTime t0 = DateTime.UtcNow;
                        a = FormulaAudit.Audit(r.Latex, r.Value);
                        ms = (DateTime.UtcNow - t0).TotalMilliseconds;
                    }
                    if (a == null || !a.Ok) auditFail++;
                    sb.Append("{\"level\":").Append(level.ToString(CultureInfo.InvariantCulture))
                      .Append(",\"seed\":\"").Append(Json(r.Seed)).Append('"')
                      .Append(",\"kind\":\"").Append(Json(r.Kind)).Append('"')
                      .Append(",\"codes\":\"").Append(Json(r.TypeCodes)).Append('"')
                      .Append(",\"dims\":").Append(r.Dims.ToString(CultureInfo.InvariantCulture))
                      .Append(",\"layers\":").Append(r.Layers.ToString(CultureInfo.InvariantCulture));
                    if (level == 5 && !string.IsNullOrEmpty(r.NaturalModel))
                    {
                        sb.Append(",\"natural_model\":\"").Append(Json(r.NaturalModel)).Append('"')
                          .Append(",\"model_name\":\"").Append(Json(r.NaturalModelName)).Append('"')
                          .Append(",\"topology\":\"").Append(Json(r.Topology)).Append('"')
                          .Append(",\"selection_mode\":\"").Append(Json(r.SelectionMode)).Append('"')
                          .Append(",\"candidate_id\":").Append(r.CandidateId.ToString(CultureInfo.InvariantCulture))
                          .Append(",\"candidate_count\":").Append(r.CandidateCount.ToString(CultureInfo.InvariantCulture))
                          .Append(",\"candidate_models\":\"").Append(Json(r.CandidateModels)).Append('"')
                          .Append(",\"schema_version\":\"").Append(Json(r.SchemaVersion)).Append('"')
                          .Append(",\"fingerprint\":\"").Append(Json(r.Fingerprint)).Append('"')
                          .Append(",\"template_hash\":\"").Append(Json(r.TemplateHash)).Append('"')
                          .Append(",\"retry_used\":").Append(r.RetryUsed ? "true" : "false")
                          .Append(",\"cross_edges\":").Append(r.CrossEdges.ToString(CultureInfo.InvariantCulture))
                          .Append(",\"t1\":").Append(Num(r.T1))
                          .Append(",\"t2\":").Append(Num(r.T2))
                          .Append(",\"t3\":").Append(Num(r.T3))
                          .Append(",\"t4\":").Append(Num(r.T4))
                          .Append(",\"t5\":").Append(Num(r.T5))
                          .Append(",\"t6\":").Append(Num(r.T6))
                          .Append(",\"l5_chain\":").Append(r.L5AuditJson);
                    }
                    sb.Append(",\"claim\":\"").Append(Json(RandomExpr.FormatRat(r.Value))).Append('"')
                      .Append(",\"latex\":\"").Append(Json(r.Latex)).Append('"')
                      .Append(",\"expr\":\"").Append(Json(r.Expression)).Append('"')
                      .Append(",\"audit\":{")
                      .Append("\"ok\":").Append(a != null && a.Ok ? "true" : "false")
                      .Append(",\"value\":").Append(a == null ? "null" : Num(a.Value))
                      .Append(",\"err\":").Append(a == null ? "null" : Num(a.ErrorBound))
                      .Append(",\"rel\":").Append(a == null ? "null" : Num(a.RelError))
                      .Append(",\"mode\":\"").Append(Json(a == null ? "null" : a.Mode)).Append('"')
                      .Append(",\"atoms\":").Append(a == null ? "0" : a.AtomCount.ToString(CultureInfo.InvariantCulture))
                      .Append(",\"why\":\"").Append(Json(a == null ? "null" : (a.Detail ?? ""))).Append('"')
                      .Append(",\"ms\":").Append(ms.ToString("0.0", CultureInfo.InvariantCulture))
                      .Append("}}\n");
                }
            }
            File.WriteAllText(outPath, sb.ToString(), new UTF8Encoding(false));
            return "WROTE=" + outPath + "\nCOUNT=" + total.ToString(CultureInfo.InvariantCulture)
                + "\nAUDIT_FAIL=" + auditFail.ToString(CultureInfo.InvariantCulture) + "\n";
        }

        static string Num(double v)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) return "null";
            return v.ToString("R", CultureInfo.InvariantCulture);
        }

        static string Json(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            StringBuilder sb = new StringBuilder(s.Length + 16);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '"') sb.Append("\\\"");
                else if (c == '\\') sb.Append("\\\\");
                else if (c == '\n') sb.Append("\\n");
                else if (c == '\r') sb.Append("\\r");
                else if (c == '\t') sb.Append("\\t");
                else if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                else sb.Append(c);
            }
            return sb.ToString();
        }
    }
}
