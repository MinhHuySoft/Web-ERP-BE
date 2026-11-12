# Cài đặt VPS và triển khai tự động

> Áp dụng QĐ-07 (1 BE trên 1 VPS Windows, cấu hình trong SQLite) và QĐ-08 (repo riêng, CD qua runner tự host).
> Làm **một lần** theo thứ tự dưới đây. Sau đó mỗi lần push lên `main`, CI đóng gói và VPS tự cập nhật, tự khởi động lại BE.
>
> Đã dùng trên Windows Server 2022 Datacenter, IIS dùng chung với các site khác, Hosting Bundle 10.0.12.

## 0. Tổng quan

```text
push main ─► CI (runner GitHub, windows-latest): build, test, đóng gói artifact "weberp"
                └─► Deploy (runner tự host trên VPS, nhãn weberp-vps): tải artifact → deploy.ps1
                        app_offline.htm → chép bản mới → bỏ app_offline.htm → chờ /health đúng phiên bản
                        lỗi → quay lui bản trước (releases\previous)
```

Bố cục trên VPS:

```text
C:\WebErp\
  sites\app\      BE + FE (wwwroot)  ← site IIS "WebErp"
  tools\admin\    WebErp.Admin.exe (dòng lệnh quản trị), tools\backup.ps1
  data\           config.db (công ty, domain, chuỗi kết nối mã hóa)
  keys\           khóa Data Protection
  files\  logs\  backup\  releases\
```

**IIS dùng chung với site khác:** không chạy `iisreset` hay `net stop was`, vì sẽ làm gián đoạn mọi site. Biến môi trường cho BE đặt
vào **app pool `WebErp`** (mục 2–4) rồi chỉ khởi động lại pool này: `Restart-WebAppPool WebErp`.

## 1. Phần mềm

1. Bật IIS (Server Manager → Web Server). Không cần ASP.NET 4.x.
2. Cài **ASP.NET Core Hosting Bundle .NET 10** (tải từ trang tải .NET của Microsoft).
   - Bộ cài cần IIS nạp lại module. Máy chỉ có WebErp thì chạy `iisreset`; máy có site khác thì làm vào giờ vắng.
   - Kiểm: `dotnet --list-runtimes` phải có `Microsoft.AspNetCore.App 10.x`.
3. **DB của công ty phát triển:**
   - nằm trên chính VPS: cần SQL Server trên VPS, kết nối bằng Windows Authentication (mục 6.3a);
   - ở máy chủ khác (VPS không có SQL Server): SQL login riêng, `Encrypt=True`, VPN hoặc mở cổng chỉ cho IP của VPS (mục 6.3b).

## 2. Thư mục, tài khoản, app pool, site

Thư mục dữ liệu mặc định là `C:\WebErp`. Muốn ổ khác (vd `E:\WebErp`), đặt `WebErp__Root` ở **hai nơi**:

- cấp máy, cho `WebErp.Admin`, `deploy.ps1`, `backup.ps1`;
- trong biến của app pool, cho Host (mục 4).

```powershell
[Environment]::SetEnvironmentVariable('WebErp__Root', 'E:\WebErp', 'Machine')   # bỏ qua nếu dùng C:\WebErp
```

Chạy PowerShell **với quyền quản trị**:

```powershell
$root = if ($env:WebErp__Root) { $env:WebErp__Root } else { 'C:\WebErp' }
'sites\app','tools\admin','data','keys','files','logs','backup','releases' | ForEach-Object { New-Item -ItemType Directory -Force (Join-Path $root $_) | Out-Null }

# Tài khoản chạy runner (mục 5): KHÔNG thuộc Administrators.
New-LocalUser -Name weberp-deploy -Password (Read-Host -AsSecureString 'Mật khẩu weberp-deploy') -PasswordNeverExpires -UserMayNotChangePassword

Import-Module WebAdministration
New-WebAppPool -Name WebErp
Set-ItemProperty IIS:\AppPools\WebErp -Name managedRuntimeVersion -Value ''
# Site: cổng nội bộ 127.0.0.1:8081 để script triển khai kiểm /health.
New-Website -Name WebErp -PhysicalPath "$root\sites\app" -ApplicationPool WebErp -IPAddress 127.0.0.1 -Port 8081
# Dùng danh tính app pool thay cho IUSR, để quyền NTFS chỉ cần cấp cho pool.
Set-WebConfigurationProperty -PSPath IIS:\ -Location WebErp -Filter system.webServer/security/authentication/anonymousAuthentication -Name userName -Value ''
# Mỗi domain của khách một binding HTTPS (SNI), gắn chứng chỉ TLS của domain đó. Làm khi đã có domain và chứng chỉ.
# New-WebBinding -Name WebErp -Protocol https -Port 443 -HostHeader 'erp.congty.vn' -SslFlags 1
```

**Quyền NTFS.** Thư mục tạo dưới `C:\` kế thừa quyền của `BUILTIN\Users`: mọi tài khoản trên máy, kể cả app pool của site khác,
đọc được khóa và cấu hình, và tạo được tệp con. Vì vậy phải **bỏ kế thừa** trước, rồi cấp đúng từng quyền:

```powershell
# Chỉ giữ Administrators (S-1-5-32-544) và SYSTEM (S-1-5-18).
icacls $root /inheritance:r /grant:r '*S-1-5-32-544:(OI)(CI)F' '*S-1-5-18:(OI)(CI)F'

$p = 'IIS AppPool\WebErp'
icacls "$root\sites\app" /grant "${p}:(OI)(CI)RX" 'weberp-deploy:(OI)(CI)M'
icacls "$root\data"      /grant "${p}:(OI)(CI)R"      # chỉ đọc: chỉ WebErp.Admin (quản trị) được sửa cấu hình
icacls "$root\keys"      /grant "${p}:(OI)(CI)M"
icacls "$root\files"     /grant "${p}:(OI)(CI)M"
icacls "$root\logs"      /grant "${p}:(OI)(CI)M"
icacls "$root\tools"     /grant 'weberp-deploy:(OI)(CI)M'
icacls "$root\releases"  /grant 'weberp-deploy:(OI)(CI)M'
```

`config.db` dùng journal kiểu DELETE (không WAL), nên pool chỉ cần quyền đọc thư mục `data`.

## 3. Chứng chỉ bọc khóa Data Protection (làm TRƯỚC khi thêm công ty)

Chuỗi kết nối của khách được mã hóa bằng khóa trong `C:\WebErp\keys`. Khóa này lại được bọc bằng một chứng chỉ, để bản sao lưu
khôi phục được lên VPS khác.

```powershell
$cert = New-SelfSignedCertificate -Subject 'CN=WebErp Data Protection' -CertStoreLocation Cert:\LocalMachine\My `
  -KeyExportPolicy Exportable -KeySpec KeyExchange -NotAfter (Get-Date).AddYears(10)
# Xuất .pfx có mật khẩu, CẤT NGOÀI VPS (két sắt mật khẩu / ổ khác). Không đưa vào repo.
Export-PfxCertificate -Cert $cert -FilePath C:\temp\weberp-dp.pfx -Password (Read-Host -AsSecureString 'Mật khẩu .pfx')

# Thumbprint (không phải bí mật): cấp máy cho WebErp.Admin; biến của app pool cho Host (mục 4).
[Environment]::SetEnvironmentVariable('WebErp__DataProtectionCertificateThumbprint', $cert.Thumbprint, 'Machine')

# Cấp quyền đọc private key cho app pool (thay cho certlm.msc → Manage Private Keys).
$rsa  = [Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey($cert)
$file = if ($rsa -is [Security.Cryptography.RSACng]) { $rsa.Key.UniqueName } else { $rsa.CspKeyContainerInfo.UniqueKeyContainerName }
$key  = "$env:ProgramData\Microsoft\Crypto\Keys\$file", "$env:ProgramData\Microsoft\Crypto\RSA\MachineKeys\$file" |
        Where-Object { Test-Path $_ } | Select-Object -First 1
icacls $key /grant 'IIS AppPool\WebErp:R'
```

## 4. Biến môi trường của app pool (có bí mật)

BE kiểm mật khẩu `SY_User` bằng thuật toán của ERP. Thuật toán cần một hằng 6 ký tự của hệ cũ (tài liệu 12 §2.5 trong repo FE).
Hằng này **không có trong repo**. Lấy từ đội phát triển ERP, rồi đặt làm biến môi trường **chỉ của app pool**:

```powershell
$appcmd = "$env:windir\system32\inetsrv\appcmd.exe"
function Set-PoolEnv($name, $value) {
  & $appcmd set config -section:system.applicationHost/applicationPools /-"[name='WebErp'].environmentVariables.[name='$name']" /commit:apphost 2>$null | Out-Null
  & $appcmd set config -section:system.applicationHost/applicationPools /+"[name='WebErp'].environmentVariables.[name='$name',value='$value']" /commit:apphost
}
Set-PoolEnv 'ASPNETCORE_ENVIRONMENT' 'Production'
Set-PoolEnv 'WebErp__DataProtectionCertificateThumbprint' $cert.Thumbprint
Set-PoolEnv 'WebErp__LegacyPassword__Key' '<HẰNG 6 KÝ TỰ>'
# Set-PoolEnv 'WebErp__Root' 'E:\WebErp'        # chỉ khi không dùng C:\WebErp
Restart-WebAppPool WebErp                        # không cần iisreset
```

## 5. Runner tự host (CD)

GitHub khuyến cáo **không dùng runner tự host cho repo công khai**, vì mã lạ có thể chạy trên máy của bạn. Repo BE đang để công
khai đến khi chạy thật (QĐ-08). Vì vậy BẮT BUỘC làm đủ các bước sau.

1. **Chọn nơi đăng ký runner** (chỉ chọn một):

   | Cách | Đăng ký ở | Giới hạn workflow được dùng runner | Khi nào |
   |---|---|---|---|
   | A. Cấp repo | Repo → Settings → Actions → Runners → New self-hosted runner | Không có. Bảo vệ dựa vào bước 3–4 | `MinhHuySoft` là tài khoản cá nhân, hoặc muốn đơn giản |
   | B. Cấp organization (khuyến nghị) | Org → Settings → Actions → Runner groups → New group: chỉ repo `Web-ERP-BE`, **Selected workflows** = `MinhHuySoft/Web-ERP-BE/.github/workflows/deploy.yml@refs/heads/main`. Rồi New runner trong group đó (URL `https://github.com/MinhHuySoft`, token của org) | Chỉ workflow Deploy trên `main` | `MinhHuySoft` là organization |

2. **Cài runner** vào `C:\actions-runner` theo lệnh GitHub hiện ra. Khi chạy `config.cmd`:
   - thêm `--labels weberp-vps`;
   - chọn chạy như service, đăng nhập bằng `weberp-deploy` (tạo ở mục 2).

   Khóa quyền thư mục runner: `icacls C:\actions-runner /inheritance:r /grant:r '*S-1-5-32-544:(OI)(CI)F' '*S-1-5-18:(OI)(CI)F' 'weberp-deploy:(OI)(CI)M'`.
3. **Repo → Settings → Actions → General:**
   - "Fork pull request workflows from outside collaborators" chọn **Require approval for all external contributors**;
   - "Workflow permissions" chọn **Read repository contents**.
4. **Repo → Settings → Environments → New environment `production`:** Deployment branches chọn **Selected branches** → `main`.
5. Khi chuyển repo sang riêng tư (QĐ-08), giữ nguyên các bước trên.

## 6. Lần triển khai đầu tiên

1. Push lên `main` (hoặc tab Actions → CI → Run workflow trên `main`). Chờ CI xong, Deploy tự chạy trên runner.
   - Lúc này `/health` báo `"status":"degraded"` vì chưa có cấu hình. Đó là bình thường.
2. Khởi tạo cấu hình, thêm công ty đầu tiên (PowerShell của tài khoản quản trị):

   ```powershell
   cd C:\WebErp\tools\admin
   .\WebErp.Admin.exe init
   .\WebErp.Admin.exe tenant add MINHHUY --name "Tên công ty" --host erp.congty.vn --db-label "Tên DB"
   # Nhập chuỗi kết nối khi được hỏi (không hiện ra màn hình), xem 6.3.
   .\WebErp.Admin.exe tenant test MINHHUY
   .\WebErp.Admin.exe tenant list
   ```

3. Quyền SQL cho BE. M1 chỉ đọc.

   **a) DB trên chính VPS** (Windows Authentication, không lưu mật khẩu). Chuỗi kết nối:
   `Server=localhost;Database=<DB>;Integrated Security=true;TrustServerCertificate=true`

   ```sql
   CREATE LOGIN [IIS APPPOOL\WebErp] FROM WINDOWS;
   USE [<DB>];
   CREATE USER [IIS APPPOOL\WebErp] FOR LOGIN [IIS APPPOOL\WebErp];
   ALTER ROLE db_datareader ADD MEMBER [IIS APPPOOL\WebErp];
   GRANT SELECT ON dbo.SY_GetUserMenuFnc TO [IIS APPPOOL\WebErp];
   ```

   `tenant test` chạy bằng tài khoản Windows của người gõ lệnh, không phải app pool. Kiểm thật bằng cách đăng nhập trên web.

   **b) DB ở máy chủ khác** (DB khách hàng, hoặc VPS không có SQL Server). Tạo trên SQL Server chứa DB:

   ```sql
   CREATE LOGIN weberp_minhhuy WITH PASSWORD = '<mật khẩu mạnh>', CHECK_POLICY = ON;
   USE [<DB>];
   CREATE USER weberp_minhhuy FOR LOGIN weberp_minhhuy;
   ALTER ROLE db_datareader ADD MEMBER weberp_minhhuy;
   GRANT SELECT ON dbo.SY_GetUserMenuFnc TO weberp_minhhuy;
   ```

   Chuỗi kết nối: `Server=<máy chủ>,<cổng>;Database=<DB>;User Id=weberp_minhhuy;Password=<…>;Encrypt=True;TrustServerCertificate=False`.
   - Không dùng `sa`, không cấp `db_owner`.
   - Đường kết nối: VPN, hoặc cổng SQL chỉ mở cho IP của VPS (QĐ-07, bổ sung 2026-11-11, mục 3).
   - SQL Server phải có chứng chỉ TLS hợp lệ thì `TrustServerCertificate=False` mới kết nối được.
4. Gắn binding HTTPS cho domain (mục 2), rồi mở `https://erp.congty.vn/` và đăng nhập bằng tài khoản ERP.

## 7. Sao lưu

Task Scheduler → Create Task, chạy hằng ngày bằng tài khoản quản trị:

```text
powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\WebErp\tools\backup.ps1 -Destination \\<nơi-ngoài-VPS>\weberp
```

`backup.ps1` được mỗi lần triển khai chép sẵn vào `C:\WebErp\tools\`. Bản sao lưu gồm `config-*.db` và thư mục khóa. Cùng với
tệp `.pfx` ở mục 3, như vậy là đủ để dựng lại trên VPS mới.

## 8. Kiểm tra và xử lý sự cố

| Việc | Lệnh / nơi xem |
|---|---|
| BE đang chạy bản nào | `Invoke-RestMethod http://127.0.0.1:8081/health` |
| `/health` báo "chưa khởi tạo cấu hình" | Chạy `WebErp.Admin.exe init` (mục 6.2) |
| `/health` báo "không đọc được cấu hình" | Pool thiếu quyền đọc `data` (mục 2), hoặc `WebErp__Root` của pool khác của máy |
| Đăng nhập báo "chưa cấu hình xác thực" | Thiếu `WebErp__LegacyPassword__Key` trong biến của pool (mục 4) |
| Nhật ký lỗi | Event Viewer → Windows Logs → Application (nguồn `WebErp.Host` / `.NET Runtime` / `IIS AspNetCore Module V2`) |
| Đổi biến môi trường của pool | `Set-PoolEnv` (mục 4) rồi `Restart-WebAppPool WebErp`. Không cần `iisreset` |
| Bảo trì một công ty (BE trả 503) | `WebErp.Admin.exe tenant maintenance MINHHUY 30` … `tenant maintenance MINHHUY off` |
| Đăng xuất mọi người của một công ty | `WebErp.Admin.exe tenant logout-all MINHHUY` |
| Cài lại một bản cũ | Actions → Deploy → Run workflow, nhập ID lần chạy CI của bản đó |
| Quay lui thủ công | Bản trước nằm ở `C:\WebErp\releases\previous` (deploy.ps1 tự quay lui khi bản mới không khỏe) |
