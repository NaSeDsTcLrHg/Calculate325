/*
 * Calculate325 —— 自定义数字串（持久化）
 * ---------------------------------------------------------------------------
 * 用户自己定义的数字串保存在 exe 同目录的 patterns.txt（每行一个）。
 * 规则：只允许数字，长度 1–24，不能与内置四种、也不能与已有的重复。
 */
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace Calculate325
{
    static class CustomPatterns
    {
        /// <summary>内置的四种数字串（与界面顺序一致）。</summary>
        public static readonly string[] BuiltIn = new string[] { "325", "799", "325799", "114514" };

        public static readonly List<string> Items = new List<string>();

        static readonly Regex DigitsOnly = new Regex("^[0-9]{1,8}$");

        public static string FilePath(string dir)
        {
            return Path.Combine(dir, "patterns.txt");
        }

        public static void Load(string dir)
        {
            Items.Clear();
            try
            {
                string p = FilePath(dir);
                if (!File.Exists(p)) return;
                string[] lines = File.ReadAllLines(p, Encoding.UTF8);
                for (int i = 0; i < lines.Length; i++)
                {
                    string s = (lines[i] ?? "").Trim();
                    if (!DigitsOnly.IsMatch(s)) continue;
                    if (IsBuiltIn(s) || Items.Contains(s)) continue;
                    if (Items.Count >= 20) break;
                    Items.Add(s);
                }
            }
            catch (Exception) { }
        }

        public static void Save(string dir)
        {
            try
            {
                List<string> lines = new List<string>(Items);
                File.WriteAllLines(FilePath(dir), lines.ToArray(), new UTF8Encoding(false));
            }
            catch (Exception) { }
        }

        public static bool IsBuiltIn(string p)
        {
            for (int i = 0; i < BuiltIn.Length; i++)
            {
                if (string.Equals(BuiltIn[i], p, StringComparison.Ordinal)) return true;
            }
            return false;
        }

        /// <summary>校验一条新数字串，返回错误信息；通过时 error 为 null。</summary>
        public static string Validate(string raw)
        {
            string p = (raw ?? "").Trim();
            if (p.Length == 0) return "请输入数字串。";
            if (!DigitsOnly.IsMatch(p)) return "只能输入数字（1–8 位；更长的串当前求解器基本无解）。";
            if (p[0] == '0') return "数字串不能以 0 开头（如 0、0325 都不合法）。";
            if (IsBuiltIn(p)) return "这是内置数字串，无需重复添加。";
            if (Items.Contains(p)) return "这个数字串已经在列表里了。";
            if (p.Length >= 7) return null;    // 合法但位数较多，界面上会额外提示
            if (Items.Count >= 20) return "最多添加 20 个自定义数字串。";
            return null;
        }

        public static bool Add(string raw)
        {
            string p = (raw ?? "").Trim();
            if (Validate(p) != null) return false;
            Items.Add(p);
            return true;
        }

        public static bool Remove(string p)
        {
            return Items.Remove(p);
        }

        /// <summary>内置 + 自定义，供“我全都要！”使用。</summary>
        public static string[] All()
        {
            List<string> all = new List<string>(BuiltIn);
            for (int i = 0; i < Items.Count; i++) all.Add(Items[i]);
            return all.ToArray();
        }
    }
}
