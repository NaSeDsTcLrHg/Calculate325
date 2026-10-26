/*
 * 程序集信息：会写入 exe 的“属性 → 详细信息”里，让 Windows 能识别出这是哪个程序
 * （没有版本信息的裸 exe 更容易被 SmartScreen 当成“无法识别的应用”）。
 */
using System.Reflection;
using System.Runtime.InteropServices;

[assembly: AssemblyTitle("计算325 · Calculate325")]
[assembly: AssemblyDescription("用连续重复的 325325… 数字串，插入运算符与括号凑出目标数字 n")]
[assembly: AssemblyProduct("Calculate325")]
[assembly: AssemblyCompany("Calculate325")]
[assembly: AssemblyCopyright("Copyright © 2026 Calculate325")]
[assembly: AssemblyConfiguration("Release")]
[assembly: AssemblyVersion("1.1.0.0")]
[assembly: AssemblyFileVersion("1.1.0.0")]
[assembly: AssemblyInformationalVersion("1.1 single-window, DPI-aware")]
[assembly: ComVisible(false)]
