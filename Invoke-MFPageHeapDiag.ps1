<#
.SYNOPSIS
  对 MF 测试宿主开启全页堆(PageHeap)，跑一次测试让堆破坏在违规指令当场崩，跑完自动复原。

.DESCRIPTION
  用途：把 LingFan.Media 冷启动 0x80131506 / 0xC0000005 这类"滞后症状"钉死成"精确凶手"。
  原理：全页堆(full page heap)给每次原生分配末尾加不可访问守护页、释放后立即回收页。
        一旦发生 缓冲区越界 或 释放后使用(UAF)，会在**违规那一条指令**当场 AV，报精确地址+调用点，零误报。
  流程：
    1. 自动定位 gflags.exe（标准 SDK 调试工具路径 + PATH + 递归搜索）
    2. 若找不到，打印安装 Debugging Tools for Windows 的方式后退出
    3. 清理 testhost.exe IFEO 下的惰性 DelayFreeSizeMB 残值（避免状态污染）
    4. gflags /p /enable testhost.exe /full
    5. dotnet test（参数可配）
    6. finally 块：gflags /p /disable testhost.exe，并清理空 IFEO 键

.NOTES
  - 必须以**管理员**身份运行（gflags 写 HKLM\IFEO 需要管理员权限）。
  - 全页堆非常慢且吃内存（MF 分配量大），测试耗时可能数倍增长，属正常。
  - 默认不开 LINGFAN_INTEROP_STRICT（让 PageHeap 纯净发声）；可用 -Strict 叠加。
#>

[CmdletBinding()]
param(
    # 测试工程路径（相对仓库根）
    [string]$TestProject = "src\Tests\LingFan.Media.Backends.MediaFoundation.Tests",
    # 测试筛选
    [string]$Filter = "Category!=RequiresAudioDevice&Category!=RequiresGPU",
    # 若已知 gflags.exe 路径可直传，跳过自动定位
    [string]$GflagsPath,
    # 叠加原生互操作严格模式护栏
    [switch]$Strict,
    # 测试结束后保留全页堆（默认关闭=自动复原）
    [switch]$KeepPageHeap
)

$ErrorActionPreference = "Stop"

# ---------- 1) 定位 gflags.exe ----------
function Find-Gflags {
    if ($GflagsPath -and (Test-Path $GflagsPath)) { return $GflagsPath }
    $cmd = Get-Command gflags.exe -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    $candidates = @(
        "E:\Windows Kits\10\Debuggers\x64\gflags.exe",
        "C:\Program Files (x86)\Windows Kits\10\Debuggers\x64\gflags.exe",
        "C:\Program Files\Windows Kits\10\Debuggers\x64\gflags.exe",
        "C:\Program Files (x86)\Windows Kits\10\Debuggers\x86\gflags.exe"
    )
    foreach ($c in $candidates) { if (Test-Path $c) { return $c } }
    $roots = @("C:\Program Files*\Windows Kits", "E:\Windows Kits", "D:\Windows Kits")
    foreach ($root in $roots) {
        $found = Get-ChildItem $root -Recurse -Filter gflags.exe -ErrorAction SilentlyContinue |
            Select-Object -First 1 -ExpandProperty FullName
        if ($found) { return $found }
    }
    return $null
}

$g = Find-Gflags
if (-not $g) {
    Write-Host "❌ 未找到 gflags.exe。请先安装 'Debugging Tools for Windows'：" -ForegroundColor Red
    Write-Host "   方式A (VS 安装器): 修改 Visual Studio → 单个组件 → 勾选 'Debugging Tools for Windows'"
    Write-Host "   方式B (Windows SDK): 运行 winsdksetup.exe → 仅勾选 'Debugging Tools for Windows'(或 'Windows Debuggers')"
    Write-Host "   方式C (winget, 若可用): winget install Microsoft.WindowsSDK.Debuggers"
    Write-Host "   安装后再以管理员身份重跑本脚本。"
    exit 1
}
Write-Host "✅ gflags: $g"

# ---------- 2) 清理惰性 DelayFreeSizeMB 残值 ----------
$ifeo = "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\testhost.exe"
$p = Get-ItemProperty $ifeo -Name DelayFreeSizeMB -ErrorAction SilentlyContinue
if ($p -and ($null -ne $p.DelayFreeSizeMB)) {
    Remove-ItemProperty $ifeo -Name DelayFreeSizeMB -Force
    Write-Host "🧹 已清理惰性 DelayFreeSizeMB 残值"
}

# ---------- 3) 启用全页堆 ----------
Write-Host "🔧 启用全页堆: gflags /p /enable testhost.exe /full"
& $g /p /enable testhost.exe /full
if ($LASTEXITCODE -ne 0) {
    Write-Warning "gflags enable 返回退出码 $LASTEXITCODE（可能权限不足，请确认以管理员运行）"
}

# 可选：叠加严格模式
if ($Strict) { $env:LINGFAN_INTEROP_STRICT = "1"; Write-Host "🔧 已开启 LINGFAN_INTEROP_STRICT=1" }

try {
    Write-Host "🚀 运行测试（全页堆已开，破坏会在违规指令当场崩）..." -ForegroundColor Cyan
    dotnet test $TestProject --filter $Filter
    $testExit = $LASTEXITCODE
    Write-Host "测试退出码: $testExit"
    if ($testExit -ne 0) {
        Write-Host "💥 非零退出 = 大概率已抓到精确违规点。请把上方完整 'at ...' 原生栈贴回分析。" -ForegroundColor Yellow
    } else {
        Write-Host "✅ 全页堆下仍全绿：强烈暗示破坏源不在我们托管堆分配（可能 MF 内部/系统堆）。需换思路。" -ForegroundColor Green
    }
}
finally {
    if (-not $KeepPageHeap) {
        Write-Host "🔧 复原: gflags /p /disable testhost.exe"
        & $g /p /disable testhost.exe
        # 若整个 IFEO 键已空，删掉避免残留
        if (Test-Path $ifeo) {
            $remain = Get-ItemProperty $ifeo -ErrorAction SilentlyContinue
            $vals = $remain.PSObject.Properties | Where-Object {
                $_.Name -notin @('PSPath','PSParentPath','PSChildName','PSDrive','PSProvider')
            }
            if ($vals.Count -eq 0) {
                Remove-Item $ifeo -Force
                Write-Host "🧹 已删除空 IFEO 键 testhost.exe"
            }
        }
        Write-Host "✅ 全页堆已复原（HKLM IFEO 已清理）"
    } else {
        Write-Host "⚠️ 保留全页堆（KeepPageHeap）。手动复原命令: gflags /p /disable testhost.exe" -ForegroundColor Yellow
    }
}
