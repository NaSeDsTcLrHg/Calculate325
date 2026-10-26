# 字体说明

程序把数学字体作为**资源内嵌**到 exe 里，因此最终用户不需要安装任何字体。
构建脚本 `build-exe.ps1` 会按下面的顺序查找资源，找不到就跳过（不会报错）：

| 文件 | 用途 | 是否随仓库分发 | 许可 |
| --- | --- | --- | --- |
| `AxMath.ttf` | 数学符号（∫ Σ √ π ∞ 等） | **否** | 商业字体，不随开源仓库分发 |
| `lmroman10-italic.otf` | 变量斜体（x / n / k） | 是 | GUST Font License（Latin Modern） |
| `lmroman10-regular.otf` | 数字与函数名（sin / cos / ln / lim） | 是 | GUST Font License（Latin Modern） |

## 没有 AxMath.ttf 会怎样

代码里 `MathFonts.Ensure()` 有回退逻辑：内嵌资源缺失时自动退到系统字体 **Cambria Math**
（Windows 自带），界面与排版照常工作，只是∫、Σ 这类符号的字形略有差别。

## 想用回 AxMath 字形

1. 自行取得一份含数学符号的 `AxMath.ttf`（需你拥有相应授权）；
2. 放到本目录，文件名保持 `AxMath.ttf`；
3. 重新运行 `build-exe.ps1`，脚本会自动把它内嵌进 exe。

也可以换成任何等价的数学字体（例如把文件名改成 `AxMath.ttf` 的 Latin Modern Math / STIX Two Math），
只要它覆盖常见的数学符号即可。
