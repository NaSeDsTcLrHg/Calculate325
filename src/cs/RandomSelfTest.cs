/*
 * Calculate325 —— 随机生成器自检（tests\Calculate325-tests.exe --random-test）
 * ---------------------------------------------------------------------------
 * 类型位 + 家族/参数：
 *   前 2×level 位 = 各层类型（9 = 没有这一层）
 *   后面每 3 位 = [家族 1 位][参数 2 位]
 *
 * 检查：
 *   1) 4 个等级各生成 N 条混合式子，逐条精确校验 = 目标
 *   2) 10 个归一化积分家族逐个 = 1
 *   3) 全零 32 位 = 八重积分；首段指数家族；混合类型/单层/L1/L2 形态
 */
using System;
using System.Globalization;
using System.Text;

namespace Calculate325
{
    public static class RandomSelfTest
    {
        public static int LastExitCode;

        public static string Run(string targetText, int count)
        {
            if (count < 1) count = 1;
            if (count > 200) count = 200;

            TargetParse tp = Solver.ParseTarget(targetText);
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("TARGET=" + (targetText == null ? "" : targetText));
            if (!tp.Ok)
            {
                sb.AppendLine("ERROR=" + tp.Error);
                sb.AppendLine("===== 随机生成自检：通过 0 / 失败 0 =====");
                LastExitCode = 2;
                return sb.ToString();
            }

            sb.AppendLine("TARGET_VALUE=" + RandomExpr.FormatRat(tp.Value));
            sb.AppendLine("COUNT=" + count.ToString(CultureInfo.InvariantCulture));
            string lengths = "";
            for (int level = 1; level <= RandomExpr.MaxLevel; level++)
            {
                lengths += (lengths.Length == 0 ? "" : ",")
                    + RandomExpr.SeedLength(level).ToString(CultureInfo.InvariantCulture);
            }
            sb.AppendLine("SEED_LENGTHS=" + lengths);
            sb.AppendLine("MAX_LAYERS=" + RandomExpr.MaxLayers(1) + ","
                + RandomExpr.MaxLayers(2) + "," + RandomExpr.MaxLayers(3) + "," + RandomExpr.MaxLayers(4));

            int pass = 0, fail = 0;
            Random rnd = new Random(20261018 + count);

            sb.AppendLine("GENERATION_BEGIN");
            sb.AppendLine("LEVEL\tSEED\tKIND\tTYPE_CODES\tDIMS\tIDENTITY\tCOMPLEXITY\tVALUE\tOK\tNUMERIC_KIND\tNUMERIC_OK\tEXPR\tLATEX\tERROR");
            for (int level = 1; level <= RandomExpr.MaxLevel; level++)
            {
                for (int i = 0; i < count; i++)
                {
                    string seed = RandomExpr.MakeSeed(level, rnd);
                    RandomExprResult r = null;
                    string error = "";
                    try { r = RandomExpr.BuildLevel(level, tp.Value, rnd, seed); }
                    catch (Exception ex) { error = ex.Message; }

                    Rat v = Rats.Zero;
                    string verr = "";
                    bool ok = r != null && RandomExpr.Verify(r, tp.Value, out v, out verr);
                    if (ok && level == 1 &&
                        (r.Expression.IndexOf("∫", StringComparison.Ordinal) >= 0
                         || r.Expression.IndexOf("lim", StringComparison.Ordinal) >= 0
                         || r.Expression.IndexOf("Σ", StringComparison.Ordinal) >= 0))
                    {
                        ok = false;
                        verr = "L1 不是初等函数：" + r.Expression;
                    }
                    double nv;
                    string nerr = "";
                    bool num = ok && RandomExpr.NumericCheck(r, out nv, out nerr);
                    bool good = ok && num;
                    if (good) pass++; else fail++;
                    if (r != null) { r.Value = v; r.ValueOk = ok; }

                    sb.Append(level.ToString(CultureInfo.InvariantCulture)).Append('\t');
                    sb.Append(Clean(seed)).Append('\t');
                    sb.Append(r == null ? "" : Clean(r.Kind)).Append('\t');
                    sb.Append(r == null ? "" : Clean(r.TypeCodes)).Append('\t');
                    sb.Append(r == null ? "0" : r.Dims.ToString(CultureInfo.InvariantCulture)).Append('\t');
                    sb.Append(r != null && r.Identity ? "1" : "0").Append('\t');
                    sb.Append(r == null ? "0" : r.Complexity.ToString(CultureInfo.InvariantCulture)).Append('\t');
                    sb.Append(r == null ? "" : Clean(RandomExpr.FormatRat(v))).Append('\t');
                    sb.Append(good ? "1" : "0").Append('\t');
                    sb.Append(r == null ? "" : Clean(r.NumericKind)).Append('\t');
                    sb.Append(num ? "1" : "0").Append('\t');
                    sb.Append(r == null ? "" : Clean(r.Expression)).Append('\t');
                    sb.Append(r == null ? "" : Clean(r.Latex)).Append('\t');
                    sb.Append(Clean(error.Length > 0 ? error : verr));
                    if (!num) sb.Append(' ').Append(Clean(nerr));
                    sb.AppendLine();
                }
            }
            sb.AppendLine("GENERATION_END");

            // 10 个归一化积分家族逐个构造单层积分，值必须 = 1
            sb.AppendLine("LAYER_BEGIN");
            sb.AppendLine("FAMILY\tPARAM\tKIND\tBUILT\tOK\tNUMERIC_KIND\tNUMERIC_OK\tVALUE\tEXPR\tLATEX\tERROR");
            int[] paramsToTest = new int[] { 0, 1, 2, 5, 9, 24, 64 };
            for (int family = 0; family < RandomExpr.FamilyNames.Length; family++)
            {
                for (int pi = 0; pi < paramsToTest.Length; pi++)
                {
                    int param = paramsToTest[pi];
                    RandomExprResult r = null;
                    string error = "";
                    try { r = RandomExpr.BuildLayerTest(family, param); }
                    catch (Exception ex) { error = ex.Message; }

                    Rat v = Rats.Zero;
                    string verr = "";
                    bool ok = r != null && RandomExpr.Verify(r, Rats.One, out v, out verr);
                    double nv;
                    string nerr = "";
                    bool num = ok && RandomExpr.NumericCheck(r, out nv, out nerr);
                    bool good = ok && num;
                    if (good) pass++; else fail++;

                    sb.Append(Clean(RandomExpr.FamilyNames[family])).Append('\t');
                    sb.Append(param.ToString(CultureInfo.InvariantCulture)).Append('\t');
                    sb.Append(r == null ? "" : Clean(r.Kind)).Append('\t');
                    sb.Append(r == null ? "0" : "1").Append('\t');
                    sb.Append(good ? "1" : "0").Append('\t');
                    sb.Append(r == null ? "" : Clean(r.NumericKind)).Append('\t');
                    sb.Append(num ? "1" : "0").Append('\t');
                    sb.Append(r == null ? "" : Clean(RandomExpr.FormatRat(v))).Append('\t');
                    sb.Append(r == null ? "" : Clean(r.Expression)).Append('\t');
                    sb.Append(r == null ? "" : Clean(r.Latex)).Append('\t');
                    sb.Append(Clean(error.Length > 0 ? error : verr));
                    if (!num) sb.Append(' ').Append(Clean(nerr));
                    sb.AppendLine();
                }
            }
            sb.AppendLine("LAYER_END");

            // 全零 32 位：必须正好八重积分
            string zero = new string('0', 32);
            RandomExprResult z = RandomExpr.BuildLevel(4, tp.Value, rnd, zero);
            Rat zv;
            string zerr = "";
            bool zok = RandomExpr.Verify(z, tp.Value, out zv, out zerr);
            int zintegrals = CountChar(z.Expression, '∫');
            sb.AppendLine("ZERO_SEED=" + zero);
            sb.AppendLine("ZERO_SEED_INTEGRALS=" + zintegrals.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("ZERO_SEED_DIMS=" + z.Dims.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("ZERO_SEED_LAYERS=" + z.Layers.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("ZERO_SEED_TYPE_CODES=" + z.TypeCodes);
            sb.AppendLine("ZERO_SEED_VALUE=" + RandomExpr.FormatRat(zv));
            sb.AppendLine("ZERO_SEED_OK=" + ((zok && zintegrals == 8 && z.Dims == 8 && z.Layers == 8) ? "1" : "0"));
            if (zok && zintegrals == 8 && z.Dims == 8 && z.Layers == 8) pass++; else fail++;

            // 首层用指数家族：type0 + family1 + param00，其余类型 9 表示没有
            string firstSeed = "09999999" + "100" + new string('0', 21);
            RandomExprResult f = RandomExpr.BuildLevel(4, tp.Value, rnd, firstSeed);
            Rat fv;
            string ferr = "";
            bool fok = RandomExpr.Verify(f, tp.Value, out fv, out ferr);
            bool firstExp = f.Expression.IndexOf("e^{-", StringComparison.Ordinal) >= 0;
            sb.AppendLine("FIRST_SEED=" + firstSeed);
            sb.AppendLine("FIRST_LAYER_EXPONENTIAL=" + (firstExp ? "1" : "0"));
            sb.AppendLine("FIRST_LAYER_LAYERS=" + f.Layers.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("FIRST_LAYER_VALUE=" + RandomExpr.FormatRat(fv));
            sb.AppendLine("FIRST_LAYER_OK=" + ((fok && firstExp && f.Layers == 1) ? "1" : "0"));
            if (fok && firstExp && f.Layers == 1) pass++; else fail++;

            // 混合类型/单层/低等级形态
            sb.AppendLine("MIXED_BEGIN");
            sb.AppendLine("CASE\tLAYERS\tDIMS\tTYPE_CODES\tINTEGRALS\tHAS_LIM\tHAS_SERIES\tOK");
            AppendMixed(sb, "L4-mixed", 4, MixedSeed("0134", 4), 4, 3, "0,1,3,4", tp.Value, true, true, ref pass, ref fail);
            AppendMixed(sb, "L4-one", 4, MixedSeed("09", 4), 1, 1, "0", tp.Value, false, false, ref pass, ref fail);
            AppendMixed(sb, "L4-limit-only", 4, MixedSeed("3478", 4), 4, 0, "3,4,7,8", tp.Value, true, true, ref pass, ref fail);
            AppendMixed(sb, "L2-zero", 2, new string('0', 16), 1, 1, "0", tp.Value, false, false, ref pass, ref fail);
            AppendMixed(sb, "L1-zero", 1, new string('0', 8), 0, 0, "E", tp.Value, false, false, ref pass, ref fail);
            sb.AppendLine("MIXED_END");

            sb.AppendLine("===== 随机生成自检：通过 " + pass.ToString(CultureInfo.InvariantCulture)
                + " / 失败 " + fail.ToString(CultureInfo.InvariantCulture) + " =====");
            LastExitCode = fail == 0 ? 0 : 1;
            return sb.ToString();
        }

        static string MixedSeed(string types, int level)
        {
            while (types.Length < 2 * level) types += "9";
            return types + new string('0', 6 * level);
        }

        static void AppendMixed(StringBuilder sb, string name, int level, string seed,
            int expectedLayers, int expectedDims, string expectedCodes, Rat target,
            bool expectLim, bool expectSeries, ref int pass, ref int fail)
        {
            RandomExprResult r = null;
            string error = "";
            try { r = RandomExpr.BuildLevel(level, target, new Random(1), seed); }
            catch (Exception ex) { error = ex.Message; }
            Rat v;
            string verr = "";
            bool ok = r != null && RandomExpr.Verify(r, target, out v, out verr);
            int integrals = r == null ? 0 : CountChar(r.Expression, '∫');
            bool hasLim = r != null && r.Expression.IndexOf("lim", StringComparison.Ordinal) >= 0;
            bool hasSeries = r != null && r.Expression.IndexOf("Σ", StringComparison.Ordinal) >= 0;
            int layers = r == null ? 0 : r.Layers;
            int dims = r == null ? 0 : r.Dims;
            string codes = r == null ? "" : r.TypeCodes;
            bool good = ok
                && layers == expectedLayers
                && dims == expectedDims
                && codes == expectedCodes
                && hasLim == expectLim
                && hasSeries == expectSeries;
            if (good) pass++; else fail++;

            sb.Append(name).Append('\t');
            sb.Append(layers.ToString(CultureInfo.InvariantCulture)).Append('\t');
            sb.Append(dims.ToString(CultureInfo.InvariantCulture)).Append('\t');
            sb.Append(Clean(codes)).Append('\t');
            sb.Append(integrals.ToString(CultureInfo.InvariantCulture)).Append('\t');
            sb.Append(hasLim ? "1" : "0").Append('\t');
            sb.Append(hasSeries ? "1" : "0").Append('\t');
            sb.Append(good ? "1" : "0").Append('\t');
            if (!good) sb.Append(Clean(error.Length > 0 ? error : verr));
            sb.AppendLine();
        }

        static int CountChar(string s, char c)
        {
            int n = 0;
            for (int i = 0; i < s.Length; i++) if (s[i] == c) n++;
            return n;
        }

        static string Clean(string s)
        {
            if (s == null) return "";
            return s.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
        }
    }
}
