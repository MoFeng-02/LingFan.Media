# 预览模式（只显示不删除）
# 将下面 $doDelete 改为 $true 即可真正删除
$doDelete = $true

Get-ChildItem -Path . -Include *.csproj,*.vbproj,*.fsproj -Recurse -ErrorAction SilentlyContinue | ForEach-Object {
    $projDir = $_.DirectoryName
    "bin", "obj" | ForEach-Object {
        $target = Join-Path $projDir $_
        if (Test-Path $target) {
            if ($doDelete) {
                Remove-Item -Path $target -Recurse -Force
                Write-Host "已删除: $target" -ForegroundColor Green
            } else {
                Write-Host "将删除: $target" -ForegroundColor Yellow
            }
        }
    }
}
