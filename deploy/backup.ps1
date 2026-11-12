<#
.SYNOPSIS
  Sao lưu cấu hình (SQLite, bằng VACUUM INTO) + thư mục khóa Data Protection, chép ra nơi khác ngoài VPS.
.DESCRIPTION
  Lên lịch bằng Task Scheduler (vd mỗi ngày 23:00). Mất khóa là mất toàn bộ chuỗi kết nối: luôn giữ bản sao ngoài VPS,
  cùng tệp chứng chỉ .pfx bọc khóa (cất riêng, có mật khẩu).
.EXAMPLE
  .\backup.ps1 -Destination \\nas\backup\weberp
#>
param(
    [Parameter(Mandatory = $true)][string]$Destination,
    [string]$Root = 'D:\WebErp',
    [int]$KeepDays = 30
)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$local = Join-Path $Root "backup\$stamp"
& (Join-Path $Root 'tools\admin\WebErp.Admin.exe') backup $local --root $Root
if ($LASTEXITCODE -ne 0) { throw "WebErp.Admin backup lỗi (mã $LASTEXITCODE)." }

robocopy (Join-Path $Root 'backup') $Destination /E /R:3 /W:5 /NFL /NDL /NJH /NJS /NP | Out-Null
if ($LASTEXITCODE -ge 8) { throw "Chép bản sao lưu ra $Destination lỗi (robocopy $LASTEXITCODE)." }
$global:LASTEXITCODE = 0

Get-ChildItem (Join-Path $Root 'backup') -Directory |
    Where-Object { $_.LastWriteTime -lt (Get-Date).AddDays(-$KeepDays) } |
    Remove-Item -Recurse -Force
Write-Host "Đã sao lưu vào $local và chép ra $Destination."
