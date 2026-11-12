<#
.SYNOPSIS
  Cài gói WebErp lên VPS rồi khởi động lại BE. Có quay lui về bản trước nếu bản mới không khỏe.
.DESCRIPTION
  Chạy được bằng Windows PowerShell 5.1. Không cần quyền quản trị IIS: app_offline.htm làm ASP.NET Core Module tắt ứng dụng
  (nhả khóa DLL), chép bản mới, bỏ app_offline.htm, request đầu tiên (kiểm /health) khởi động bản mới.
.EXAMPLE
  .\deploy.ps1 -Package C:\actions-runner\_work\_temp\weberp
#>
param(
    [Parameter(Mandatory = $true)][string]$Package,
    # Thư mục dữ liệu: biến môi trường cấp máy WebErp__Root (cùng biến Host và WebErp.Admin đọc), mặc định C:\WebErp.
    [string]$Root = $(if ($env:WebErp__Root) { $env:WebErp__Root } else { 'C:\WebErp' }),
    [string]$HealthUrl = $(if ($env:WEBERP_HEALTH_URL) { $env:WEBERP_HEALTH_URL } else { 'http://127.0.0.1:8081/health' }),
    # Site quản trị (README mục 9). Chưa tạo C:\WebErp\sites\admin thì bỏ qua, chỉ cập nhật dòng lệnh ở tools\admin.
    [string]$AdminHealthUrl = $(if ($env:WEBERP_ADMIN_HEALTH_URL) { $env:WEBERP_ADMIN_HEALTH_URL } else { 'http://127.0.0.1:8082/health' }),
    [int]$HealthTimeoutSeconds = 90
)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8

$site = Join-Path $Root 'sites\app'
$adminDir = Join-Path $Root 'tools\admin'
$adminSite = Join-Path $Root 'sites\admin'
$previous = Join-Path $Root 'releases\previous'

function Copy-Mirror([string]$from, [string]$to, [string[]]$extra) {
    robocopy $from $to /MIR /R:5 /W:2 /NFL /NDL /NJH /NJS /NP @extra | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "Chép $from -> $to lỗi (robocopy $LASTEXITCODE)." }
    $global:LASTEXITCODE = 0
}

function Set-Offline([string]$dir) {
    # FE nhận trang này (503) như một thông điệp ERP-HTML.
    $html = '<!doctype html><html lang="vi"><head><meta charset="utf-8"><title>Đang cập nhật</title></head><body>' +
            '<div data-erp="messages"><p data-erp="message" data-msg-type="warning">Hệ thống đang cập nhật. Vui lòng thử lại sau ít phút.</p></div>' +
            '</body></html>'
    [IO.File]::WriteAllText((Join-Path $dir 'app_offline.htm'), $html, (New-Object Text.UTF8Encoding $false))
}

function Set-Online([string]$dir) {
    Remove-Item (Join-Path $dir 'app_offline.htm') -ErrorAction SilentlyContinue
}

function Wait-Healthy([string]$expected, [string]$url = $HealthUrl) {
    $deadline = (Get-Date).AddSeconds($HealthTimeoutSeconds)
    do {
        try {
            $r = Invoke-RestMethod -Uri $url -UseBasicParsing -TimeoutSec 10
            if (-not $expected -or $r.version -eq $expected) { return $true }
            Write-Host "  /health trả phiên bản $($r.version), đang chờ $expected..."
        } catch {
            Write-Host "  chưa trả lời: $($_.Exception.Message)"
        }
        Start-Sleep -Seconds 3
    } while ((Get-Date) -lt $deadline)
    return $false
}

$versionFile = Join-Path $Package 'version.txt'
if (-not (Test-Path $versionFile) -or -not (Test-Path (Join-Path $Package 'app\WebErp.Host.dll'))) {
    throw "Gói '$Package' không hợp lệ (thiếu version.txt hoặc app\WebErp.Host.dll)."
}
$version = (Get-Content $versionFile -Raw).Trim()
Write-Host "Triển khai phiên bản $version vào $site"
# Chỉ tạo khi chưa có: New-Item -Force kiểm cả thư mục cha (vd C:\WebErp\sites), mà tài khoản runner không có quyền ở đó.
foreach ($dir in $site, $adminDir, $previous) {
    if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir | Out-Null }
}

$hasCurrent = Test-Path (Join-Path $site 'WebErp.Host.dll')
if ($hasCurrent) { Copy-Mirror $site $previous @('/XF', 'app_offline.htm', '/XD', 'logs') }

Set-Offline $site
Start-Sleep -Seconds 3
try {
    Copy-Mirror (Join-Path $Package 'app') $site @('/XF', 'app_offline.htm', '/XD', 'logs')
    Copy-Mirror (Join-Path $Package 'admin') $adminDir @()
    Copy-Item (Join-Path $Package 'deploy\backup.ps1') (Join-Path $Root 'tools\backup.ps1') -Force
} catch {
    Write-Warning "Chép bản mới lỗi: $($_.Exception.Message)"
    if ($hasCurrent) { Copy-Mirror $previous $site @('/XF', 'app_offline.htm', '/XD', 'logs') }
    Set-Online $site
    throw
}
Set-Online $site

if (Wait-Healthy $version) {
    Write-Host "Đã chạy phiên bản $version."
    # Site quản trị: cập nhật sau site người dùng. Lỗi ở đây chỉ cảnh báo, không quay lui site người dùng.
    if (Test-Path $adminSite) {
        Set-Offline $adminSite
        Start-Sleep -Seconds 3
        try {
            Copy-Mirror (Join-Path $Package 'admin') $adminSite @('/XF', 'app_offline.htm', '/XD', 'logs')
        } finally {
            Set-Online $adminSite
        }
        if (Wait-Healthy $version $AdminHealthUrl) { Write-Host "Trang quản trị chạy phiên bản $version." }
        else { Write-Warning "Trang quản trị chưa trả lời $AdminHealthUrl. Kiểm tra site WebErpAdmin (README mục 9)." }
    } else {
        Write-Host "Chưa có site quản trị ($adminSite): chỉ cập nhật dòng lệnh ở $adminDir."
    }
    exit 0
}

Write-Warning "Bản $version không khỏe sau $HealthTimeoutSeconds giây. Quay lui bản trước."
if ($hasCurrent) {
    Set-Offline $site
    Start-Sleep -Seconds 3
    Copy-Mirror $previous $site @('/XF', 'app_offline.htm', '/XD', 'logs')
    Set-Online $site
    if (Wait-Healthy $null) { Write-Warning 'Đã quay lui, bản trước đang chạy.' } else { Write-Warning 'Quay lui xong nhưng /health vẫn chưa trả lời. Kiểm tra Event Viewer.' }
}
exit 1
