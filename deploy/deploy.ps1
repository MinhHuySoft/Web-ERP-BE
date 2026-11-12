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
    [string]$Root = 'D:\WebErp',
    [string]$HealthUrl = 'http://127.0.0.1:8081/health',
    [int]$HealthTimeoutSeconds = 90
)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8

$site = Join-Path $Root 'sites\app'
$adminDir = Join-Path $Root 'tools\admin'
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

function Wait-Healthy([string]$expected) {
    $deadline = (Get-Date).AddSeconds($HealthTimeoutSeconds)
    do {
        try {
            $r = Invoke-RestMethod -Uri $HealthUrl -UseBasicParsing -TimeoutSec 10
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
New-Item -ItemType Directory -Force $site, $adminDir, $previous | Out-Null

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
