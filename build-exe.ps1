# Compile Calculate325.exe (WinForms, single window, DPI aware) and the console self-test exe.
# Uses only the .NET Framework compiler shipped with Windows: no SDK, no network.
#   powershell -ExecutionPolicy Bypass -File build-exe.ps1
param(
  [string]$Root = $PSScriptRoot
)

$ErrorActionPreference = 'Stop'
$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $csc)) { throw "C# compiler not found: $csc" }

$src = Join-Path $Root 'src\cs'
$wpfCore = (Get-ChildItem 'C:\Windows\Microsoft.NET\assembly' -Filter PresentationCore.dll -Recurse | Select-Object -First 1).FullName
$wpfFramework = (Get-ChildItem 'C:\Windows\Microsoft.NET\assembly' -Filter PresentationFramework.dll -Recurse | Select-Object -First 1).FullName
$wpfBase = (Get-ChildItem 'C:\Windows\Microsoft.NET\assembly' -Filter WindowsBase.dll -Recurse | Select-Object -First 1).FullName
$systemXaml = (Get-ChildItem 'C:\Windows\Microsoft.NET\assembly' -Filter System.Xaml.dll -Recurse | Select-Object -First 1).FullName
$wpfMath = Join-Path $Root 'assets\lib\WpfMath.dll'
$wpfMathShared = Join-Path $Root 'assets\lib\WpfMath.Shared.dll'
$refs = @('/r:System.dll', '/r:System.Core.dll', '/r:System.Drawing.dll', '/r:System.Windows.Forms.dll', '/r:System.Numerics.dll', "/r:$wpfCore", "/r:$wpfFramework", "/r:$wpfBase", "/r:$systemXaml", "/r:$wpfMath", "/r:$wpfMathShared")
$common = @('/nologo', '/optimize+', '/platform:anycpu', '/codepage:65001', '/utf8output', '/warn:3')
$appCommon = $common + @('/define:APP_ONLY')

$exe = Join-Path $Root 'Calculate325.exe'
$tests = Join-Path $Root 'tests\Calculate325-tests.exe'
$icon = Join-Path $Root 'assets\icon.ico'
$manifest = Join-Path $src 'app.manifest'
$axMathFont = Join-Path $Root 'assets\fonts\AxMath.ttf'
$mathItalic = Join-Path $Root 'assets\fonts\lmroman10-italic.otf'
$mathUpright = Join-Path $Root 'assets\fonts\lmroman10-regular.otf'

$resources = @()
if (Test-Path -LiteralPath $icon) { $resources += "/win32icon:$icon" }
if (Test-Path -LiteralPath $manifest) { $resources += "/win32manifest:$manifest" }
if (Test-Path -LiteralPath $wpfMath) { $resources += "/resource:$wpfMath,WpfMathDll" }
if (Test-Path -LiteralPath $wpfMathShared) { $resources += "/resource:$wpfMathShared,WpfMathShared" }
if (Test-Path -LiteralPath $mathUpright) { $resources += "/resource:$mathUpright,MathUpright" }
# AxMath 是商业字体，不随仓库分发；有就内嵌，没有就回退系统字体
if (Test-Path -LiteralPath $axMathFont) { $resources += "/resource:$axMathFont,AxMathFont" }
if (Test-Path -LiteralPath $mathItalic) { $resources += "/resource:$mathItalic,MathItalic" }

# Skin backgrounds (taken from the PPT template in the project), loaded at runtime
# through GetManifestResourceStream.
$skinPairs = @(
  @{ File = 'assets\ppt\bg-jieyuan.jpg';   Name = 'BgJieyuan' },
  @{ File = 'assets\ppt\bg-huaishuli.jpg'; Name = 'BgHuaishuli' },
  @{ File = 'assets\ppt\bg-cisuixing.jpg'; Name = 'BgCisuixing' }
)
foreach ($p in $skinPairs) {
  $full = Join-Path $Root $p.File
  if (Test-Path -LiteralPath $full) {
    $resources += "/resource:$full,$($p.Name)"
  }
}

Write-Host 'Building Calculate325.exe ...'
$argsExe = $appCommon + @('/target:winexe', "/out:$exe") + $resources + $refs + @(
  (Join-Path $src 'AssemblyInfo.cs'),
  (Join-Path $src 'Solver.cs'),
  (Join-Path $src 'SelfTest.cs'),
  (Join-Path $src 'Theme.cs'),
  (Join-Path $src 'Widgets.cs'),
  (Join-Path $src 'MathLayout.cs'),
  (Join-Path $src 'WpfMathBootstrap.cs'),
  (Join-Path $src 'WpfMathRenderer.cs'),
  (Join-Path $src 'MathBox.cs'),
  (Join-Path $src 'FormulaAudit.cs'),
  (Join-Path $src 'ExprAst.cs'),
  (Join-Path $src 'Traps.cs'),
  (Join-Path $src 'RandomExpr.cs'),
  (Join-Path $src 'CustomPatterns.cs'),
  (Join-Path $src 'PatternDialog.cs'),
  (Join-Path $src 'SeedDialog.cs'),
  (Join-Path $src 'HistoryForm.cs'),
  (Join-Path $src 'WindowChrome.cs'),
  (Join-Path $src 'RandomForm.cs'),
  (Join-Path $src 'Program.cs')
)
& $csc $argsExe
if ($LASTEXITCODE -ne 0) { throw "Building Calculate325.exe failed (exit $LASTEXITCODE)" }

Write-Host 'Building tests\Calculate325-tests.exe ...'
$argsTests = $common + @('/target:exe', "/out:$tests") + $refs + @(
  (Join-Path $src 'AssemblyInfo.cs'),
  (Join-Path $src 'Solver.cs'),
  (Join-Path $src 'SelfTest.cs'),
  (Join-Path $src 'FormulaAudit.cs'),
  (Join-Path $src 'ExprAst.cs'),
  (Join-Path $src 'Traps.cs'),
  (Join-Path $src 'RandomExpr.cs'),
  (Join-Path $src 'RandomDump.cs'),
  (Join-Path $src 'AuditSelfTest.cs'),
  (Join-Path $src 'RandomSelfTest.cs'),
  (Join-Path $src 'ConsoleEntry.cs')
)
& $csc $argsTests
if ($LASTEXITCODE -ne 0) { throw "Building test exe failed (exit $LASTEXITCODE)" }

Get-Item -LiteralPath $exe, $tests |
  Select-Object Name, @{n='KB';e={[math]::Round($_.Length/1KB,1)}}, LastWriteTime |
  Format-Table -AutoSize | Out-String
Write-Host 'Done.'
