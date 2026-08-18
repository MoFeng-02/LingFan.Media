# Copy-ToWsl.ps1
# 把 LingFan.Media 的「基本项目 + slnx + 资源」复制到 WSL2 的 Linux 文件系统，
# 忽略 obj/bin 与无关大目录（Bak / 私有文档 / docs / TestInfo / ThirdParty / vk_settings）。
#
# 机制：在 WSL 内用 tar 管道从 /mnt/e 源直接写入 ext4 目标，
#       避免走 \\wsl.localhost Plan9 反向写入（慢且易丢元数据）。
#
# 用法：
#   .\Copy-ToWsl.ps1                      # 默认：Ubuntu-22.04，排除 ThirdParty
#   .\Copy-ToWsl.ps1 -IncludeThirdParty   # 连 ThirdParty 一起拷（仅当你要构建依赖它的 headful 探针时）
#   .\Copy-ToWsl.ps1 -Distro Debian       # 指定其它发行版

param(
    [string]$Distro = "Ubuntu-22.04",
    [string]$WinSrc = "E:\Project\MyProject\AvaloniaUIApp\LingFanEngine.Media",
    [string]$WslSrc = "/mnt/e/Project/MyProject/AvaloniaUIApp/LingFanEngine.Media",
    [string]$WslDst = "/usr/project/LingFan.Media",
    [switch]$IncludeThirdParty
)

$ErrorActionPreference = "Stop"

# 0) 前置检查
if (-not (Test-Path $WinSrc)) { Write-Error "Windows 源目录不存在: $WinSrc"; exit 1 }
$list = & wsl -l -q 2>$null
$listStr = ($list -join "`n") -replace '[\u2013\u2014]', '-'
if ($listStr -notlike "*$Distro*") {
    Write-Warning "未确认到发行版 '$Distro'（wsl -l 输出见下），将仍尝试调用 wsl。`n$listStr"
}

# 1) 构造 WSL 内执行的 bash 脚本（单引号 here-string，PowerShell 不展开 $；
#    用 -f 注入三个动态值，{2} 为空时即不额外排除 ThirdParty）
$thirdPartyExclude = if ($IncludeThirdParty) { "" } else { "--exclude=ThirdParty" }
$bash = @'
set -euo pipefail
export LANG=C.UTF-8 LC_ALL=C.UTF-8
SRC='{0}'
DST='{1}'
if [ ! -d "$SRC" ]; then echo "WSL 内找不到源（检查 E: 是否挂载在 /mnt/e）: $SRC" >&2; exit 1; fi
mkdir -p "$DST"
cd "$SRC"
tar -cf - --exclude=obj --exclude=bin --exclude=.vs --exclude=artifacts --exclude=Bak --exclude=私有文档 --exclude=docs --exclude=TestInfo {2} --exclude=vk_settings --exclude=.git . | ( cd "$DST" && tar -xf - )
echo "=== 复制完成，目标顶层 ==="
ls -la "$DST"
echo "=== 残留 obj/bin 检查（应为空）==="
find "$DST" \( -name obj -o -name bin \) -print | head
echo "=== 目标体积 ==="
du -sh "$DST"
'@ -f $WslSrc, $WslDst, $thirdPartyExclude

# 2) 在 WSL 中以 root 执行（/usr/project 需要写权限）
#    用 base64 传输脚本：避免多行字符串经 wsl 转发时被截断/换行损坏（此前导致 bash 只收到半截脚本而 syntax error）。
#    base64 仅含 [A-Za-z0-9+/=]，在 wsl 端 single-token 安全；UTF-8 字节无损，中文排除项不受影响。
$bytes = [System.Text.Encoding]::UTF8.GetBytes($bash)
$b64 = [Convert]::ToBase64String($bytes)
Write-Host "开始复制 -> WSL [$Distro] : $WslDst"
& wsl -d $Distro -u root bash -c "echo $b64 | base64 -d | bash"
Write-Host "完成。下一步在 WSL 内：cd $WslDst && dotnet build src/Tools/LinuxHeadlessOpenGlProbe/LinuxHeadlessOpenGlProbe.csproj -c Debug -v q"
