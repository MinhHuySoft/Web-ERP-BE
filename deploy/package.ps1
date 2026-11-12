<#
.SYNOPSIS
  Đóng gói WebErp để triển khai: Host (kèm FE trong wwwroot), Admin (dòng lệnh), script triển khai, version.txt.
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

Invoke-Checked 'publish Host' { dotnet publish "$repo/src/WebErp.Host/WebErp.Host.csproj" -c $Configuration -o $app -r win-x64 --self-contained false "-p:InformationalVersion=$Version" --nologo }
Invoke-Checked 'publish Admin' { dotnet publish "$repo/src/WebErp.Admin/WebErp.Admin.csproj" -c $Configuration -o $admin -r win-x64 --self-contained false "-p:InformationalVersion=$Version" --nologo }

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
Copy-Item (Join-Path $repo 'deploy/deploy.ps1'), (Join-Path $repo 'deploy/backup.ps1') -Destination $deploy
Set-Content -Path (Join-Path $Output 'version.txt') -Value $Version -Encoding ascii
Write-Host "Đã đóng gói phiên bản $Version vào $Output"
