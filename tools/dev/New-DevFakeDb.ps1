<#
.SYNOPSIS
  Máy dev: tạo DB ERP GIẢ trên LocalDB + công ty "DEV" gắn localhost, để chạy thử BE cùng FE.
.DESCRIPTION
  Chỉ dùng trên máy dev. Không bao giờ chạy trên VPS hay trỏ vào DB khách. Dữ liệu giả lấy từ tests/fake-erp/fake-erp.sql.
  Khóa mật khẩu GIẢ "DEV000" trùng với appsettings.Development.json.
  Tài khoản thử: admin / 123, ketoan / abc.
#>
param(
    [string]$Server = '(localdb)\MSSQLLocalDB',
    [string]$Database = 'WebErpDevFake',
    [string]$Key = 'DEV000'
)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$root = Join-Path $repo '.weberp'
$adminProject = Join-Path $repo 'src\WebErp.Admin'

function Invoke-Sql([string]$query) {
    sqlcmd -S $Server -E -b -f 65001 -Q $query | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "sqlcmd lỗi: $query" }
}

function Get-LegacyHash([string]$user, [string]$pass) {
    # Cùng thuật toán với WebErp.Core.Auth.LegacyPassword (muối và vị trí cố định cho dễ đọc).
    $salt = '3141'; $p = 12
    $md5 = [Security.Cryptography.MD5]::Create()
    $bytes = [Text.Encoding]::UTF8.GetBytes($user.ToUpperInvariant() + $Key + $pass + $salt)
    $h = ([BitConverter]::ToString($md5.ComputeHash($bytes))).Replace('-', '')
    return $h.Insert($p, $salt).Insert(3, ('{0:00}' -f $p))
}

sqllocaldb start MSSQLLocalDB | Out-Null
Invoke-Sql "IF DB_ID('$Database') IS NOT NULL BEGIN ALTER DATABASE [$Database] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [$Database]; END; CREATE DATABASE [$Database];"
sqlcmd -S $Server -E -b -f 65001 -d $Database -i (Join-Path $repo 'tests\fake-erp\fake-erp.sql') | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Chạy fake-erp.sql lỗi.' }

$users = @(
    @{ u = 'admin'; n = 'Quản trị (dữ liệu giả)'; p = '123'; g = 'NULL' },
    @{ u = 'ketoan'; n = 'Kế toán (dữ liệu giả)'; p = 'abc'; g = "'KT'" }
)
foreach ($x in $users) {
    $hash = Get-LegacyHash $x.u $x.p
    sqlcmd -S $Server -E -b -f 65001 -d $Database -Q "INSERT dbo.SY_User (UserName,HoTen,[Password],[Disable],UserGroupID,BranchID) VALUES ('$($x.u)',N'$($x.n)','$hash',0,$($x.g),'HN')" | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Thêm người dùng $($x.u) lỗi." }
}

# Cấu hình công ty DEV → localhost (tạo lại từ đầu).
Remove-Item (Join-Path $root 'data\config.db') -ErrorAction SilentlyContinue
dotnet run --project $adminProject -- init --root $root
if ($LASTEXITCODE -ne 0) { throw 'WebErp.Admin init lỗi.' }
"Server=$Server;Database=$Database;Integrated Security=true;TrustServerCertificate=true" |
    dotnet run --project $adminProject -- tenant add DEV --name 'Công ty dữ liệu giả (DEV)' --host localhost --db-label $Database --root $root
if ($LASTEXITCODE -ne 0) { throw 'WebErp.Admin tenant add lỗi.' }
dotnet run --project $adminProject -- tenant test DEV --root $root
Write-Host "Xong. Chạy: dotnet run --project src/WebErp.Host  rồi mở http://localhost:5190/ (admin / 123)"
