# 字体说明

程序把数学字体**内嵌**进 exe，最终用户不需要安装任何字体。构建脚本按下面的表查找，找不到就跳过（不会报错）。

| 文件 | 用途 | 是否随仓库分发 | 许可 |
| --- | --- | --- | --- |
| `AxMath.ttf` | 数学符号（∫ Σ √ π ∞） | 否 | 商业字体 |
| `lmroman10-italic.otf` | 变量斜体（x / n / k） | 是 | GUST Font License |
| `lmroman10-regular.otf` | 数字与函数名（sin / ln / lim） | 是 | GUST Font License |

## 没有 AxMath.ttf 会怎样

`MathFonts.Ensure()` 会自动回退到系统字体 **Cambria Math**（Windows 自带），界面与排版照常工作，只是 ∫、Σ 的字形略有差别。

## 想用回 AxMath 字形

把 `AxMath.ttf` 放进本目录（文件名不变），重新运行 `build-exe.ps1` 即可自动内嵌。任何覆盖常见数学符号的字体都可以改名成 `AxMath.ttf` 顶替。
