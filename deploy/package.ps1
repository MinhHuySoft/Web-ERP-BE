<#
.SYNOPSIS
  Đóng gói WebErp để triển khai: Host (kèm FE trong wwwroot), Admin (dòng lệnh), agent HostSync, script triển khai, version.txt.
.EXAMPLE
  ./deploy/package.ps1 -Version dev -FrontendWeb ../Web-ERP/web -Output artifacts/weberp
#>
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [Parameter(Mandatory = $true)][string]$FrontendWeb,
    [string]$Output = 'artifacts/weberp',
    [string]$Configuration = 'Release'
)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$repo = Split-Path -Parent $PSScriptRoot

function Invoke-Checked([string]$what, [scriptblock]$cmd) {
    & $cmd
    if ($LASTEXITCODE -ne 0) { throw "$what lỗi (mã $LASTEXITCODE)." }
}

if (-not (Test-Path (Join-Path $FrontendWeb 'index.html'))) { throw "Không thấy FE ở '$FrontendWeb' (thiếu index.html)." }
if (Test-Path $Output) { Remove-Item $Output -Recurse -Force }
$app = Join-Path $Output 'app'
$admin = Join-Path $Output 'admin'
$hostsync = Join-Path $Output 'hostsync'

Invoke-Checked 'publish Host' { dotnet publish "$repo/src/WebErp.Host/WebErp.Host.csproj" -c $Configuration -o $app -r win-x64 --self-contained false "-p:InformationalVersion=$Version" --nologo }
Invoke-Checked 'publish Admin' { dotnet publish "$repo/src/WebErp.Admin/WebErp.Admin.csproj" -c $Configuration -o $admin -r win-x64 --self-contained false "-p:InformationalVersion=$Version" --nologo }
Invoke-Checked 'publish HostSync' { dotnet publish "$repo/src/WebErp.HostSync/WebErp.HostSync.csproj" -c $Configuration -o $hostsync -r win-x64 --self-contained false "-p:InformationalVersion=$Version" --nologo }

# Cấu hình máy dev không lên máy thật.
Remove-Item (Join-Path $app 'appsettings.Development.json'), (Join-Path $admin 'appsettings.Development.json') -ErrorAction SilentlyContinue

# FE: bỏ mock, công cụ kiểm tra và config.js (BE tự sinh /config.js trỏ vào chính nó).
# Cả site người dùng và site quản trị đều dùng FE này (mỗi site tự sinh /config.js riêng).
foreach ($www in (Join-Path $app 'wwwroot'), (Join-Path $admin 'wwwroot')) {
    robocopy $FrontendWeb $www /E /XD mock tools /XF config.js *.md /NFL /NDL /NJH /NJS /NP | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "Chép FE lỗi (robocopy $LASTEXITCODE)." }
    $global:LASTEXITCODE = 0
}

$deploy = New-Item -ItemType Directory -Force (Join-Path $Output 'deploy')
Copy-Item (Join-Path $repo 'deploy/deploy.ps1'), (Join-Path $repo 'deploy/backup.ps1'), (Join-Path $repo 'deploy/setup-hostsync.ps1') -Destination $deploy
Set-Content -Path (Join-Path $Output 'version.txt') -Value $Version -Encoding ascii

# Agent HostSync chạy quyền LocalSystem: deploy chỉ chép vào tools\hostsync-staging, quản trị viên tự cài bằng setup-hostsync.ps1.
# Danh sách SHA-256 + mã băm của danh sách (in ra log / tóm tắt CI) để quản trị viên đối chiếu trước khi cài.
# Cách tính phải GIỐNG HỆT Get-Manifest trong setup-hostsync.ps1.
Set-Content -Path (Join-Path $hostsync 'version.txt') -Value $Version -Encoding ascii
# Lấy đường dẫn từ Get-Item rồi liệt kê từ chính chuỗi đó: tránh lệch tên ngắn (8.3) giữa Resolve-Path và FullName.
$dirFull = (Get-Item -LiteralPath $hostsync).FullName.TrimEnd('\')
$base = $dirFull + '\'
$rels = @(Get-ChildItem -LiteralPath $dirFull -Recurse -File | Where-Object { $_.Name -ne 'SHA256SUMS.txt' } | ForEach-Object { $_.FullName.Substring($base.Length) })
[Array]::Sort($rels, [StringComparer]::Ordinal)
$manifest = New-Object Text.StringBuilder
foreach ($r in $rels) {
    $h = (Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $base $r)).Hash.ToLowerInvariant()
    [void]$manifest.Append("$h  $($r.Replace('\', '/'))`n")
}
[IO.File]::WriteAllText((Join-Path $hostsync 'SHA256SUMS.txt'), $manifest.ToString(), (New-Object Text.UTF8Encoding $false))
$sha = [Security.Cryptography.SHA256]::Create()
$manifestHash = ([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($manifest.ToString()))) -replace '-', '').ToLowerInvariant()
$sha.Dispose()
Set-Content -Path (Join-Path $Output 'hostsync-manifest.txt') -Value $manifestHash -Encoding ascii
Write-Host "HostSync manifest SHA256: $manifestHash"
Write-Host "Đã đóng gói phiên bản $Version vào $Output"
