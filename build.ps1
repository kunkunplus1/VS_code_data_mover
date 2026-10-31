# 编译 VS Code 数据迁移工具（单文件 exe）
# 依赖：Windows 自带的 .NET Framework 4.x（Win10/11 默认已有）
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$csc64 = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$csc32 = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
$csc = if (Test-Path $csc64) { $csc64 } else { $csc32 }
if (-not (Test-Path $csc)) { throw '找不到 .NET Framework 的 csc.exe' }

$sources = @(Get-ChildItem -LiteralPath (Join-Path $root 'src') -Filter '*.cs' | ForEach-Object { $_.FullName })
if ($sources.Count -eq 0) { throw 'src 目录下没有 .cs 文件' }

$out = Join-Path $root 'VSCodeDataMover.exe'
if (Test-Path $out) { Remove-Item -LiteralPath $out -Force }

$arguments = @(
  '/nologo',
  '/target:winexe',
  '/optimize+',
  '/platform:anycpu',
  '/langversion:5',
  '/codepage:65001',
  "/out:$out",
  "/win32manifest:$(Join-Path $root 'src\app.manifest')",
  '/r:System.dll',
  '/r:System.Core.dll',
  '/r:System.Drawing.dll',
  '/r:System.Windows.Forms.dll',
  '/r:System.Web.Extensions.dll'
) + $sources

Write-Host "编译 $($sources.Count) 个源文件……"
& $csc $arguments
if ($LASTEXITCODE -ne 0) { throw "编译失败，退出码 $LASTEXITCODE" }

$info = Get-Item -LiteralPath $out
Write-Host ("编译成功: {0}  ({1:N0} 字节)" -f $info.FullName, $info.Length)
