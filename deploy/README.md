# Cài đặt VPS và triển khai tự động

> Áp dụng QĐ-07 (1 BE trên 1 VPS Windows, cấu hình trong SQLite) và QĐ-08 (repo riêng, CD qua runner tự host).
> Làm **một lần** theo thứ tự dưới đây. Sau đó mỗi lần push lên `main`, CI đóng gói và VPS tự cập nhật, tự khởi động lại BE.

## 0. Tổng quan

```text
push main ─► CI (runner GitHub, windows-latest): build, test, đóng gói artifact "weberp"
                └─► Deploy (runner tự host trên VPS, nhãn weberp-vps): tải artifact → deploy.ps1
                        app_offline.htm → chép bản mới → bỏ app_offline.htm → chờ /health đúng phiên bản
                        lỗi → quay lui bản trước (releases\previous)
```

Bố cục trên VPS:

```text
D:\WebErp\
  sites\app\      BE + FE (wwwroot)  ← site IIS "WebErp"
  tools\admin\    WebErp.Admin.exe (dòng lệnh quản trị)
  data\           config.db (công ty, domain, chuỗi kết nối mã hóa)
  keys\           khóa Data Protection
  files\  logs\  backup\  releases\
```

## 1. Phần mềm

1. Bật IIS (Server Manager → Web Server). Không cần ASP.NET 4.x.
2. Cài **ASP.NET Core Hosting Bundle .NET 10** (tải từ trang tải .NET của Microsoft), rồi chạy `iisreset`.
3. SQL Server cho DB của chính công ty (đã có trên VPS theo QĐ-07).

## 2. Thư mục, app pool, site

Chạy PowerShell **với quyền quản trị**:

```powershell
$root = 'D:\WebErp'
'sites\app','tools\admin','data','keys','files','logs','backup','releases' | ForEach-Object { New-Item -ItemType Directory -Force (Join-Path $root $_) | Out-Null }

Import-Module WebAdministration
New-WebAppPool -Name WebErp
Set-ItemProperty IIS:\AppPools\WebErp -Name managedRuntimeVersion -Value ''
# Site chính: cổng nội bộ 127.0.0.1:8081 để script triển khai kiểm /health.
New-Website -Name WebErp -PhysicalPath "$root\sites\app" -ApplicationPool WebErp -IPAddress 127.0.0.1 -Port 8081
# Mỗi domain của khách một binding HTTPS (SNI), gắn chứng chỉ TLS của domain đó trong IIS Manager.
New-WebBinding -Name WebErp -Protocol https -Port 443 -HostHeader 'erp.congty.vn' -SslFlags 1
```

Quyền NTFS cho app pool. **`data` chỉ đọc**: chỉ công cụ quản trị được sửa cấu hình.

```powershell
$p = 'IIS AppPool\WebErp'
icacls "$root\sites\app" /grant "${p}:(OI)(CI)RX"
icacls "$root\data"      /grant "${p}:(OI)(CI)R"
icacls "$root\keys"      /grant "${p}:(OI)(CI)M"
icacls "$root\files"     /grant "${p}:(OI)(CI)M"
icacls "$root\logs"      /grant "${p}:(OI)(CI)M"
```

## 3. Chứng chỉ bọc khóa Data Protection (làm TRƯỚC khi thêm công ty)

Chuỗi kết nối của khách được mã hóa bằng khóa trong `D:\WebErp\keys`. Khóa này lại được bọc bằng một chứng chỉ, để bản sao lưu
khôi phục được lên VPS khác.

```powershell
$cert = New-SelfSignedCertificate -Subject 'CN=WebErp Data Protection' -CertStoreLocation Cert:\LocalMachine\My `
  -KeyExportPolicy Exportable -KeySpec KeyExchange -NotAfter (Get-Date).AddYears(10)
$cert.Thumbprint
# Xuất .pfx có mật khẩu, CẤT NGOÀI VPS (két sắt mật khẩu / ổ khác). Không đưa vào repo.
Export-PfxCertificate -Cert $cert -FilePath C:\temp\weberp-dp.pfx -Password (Read-Host -AsSecureString 'Mật khẩu .pfx')
# Biến môi trường cấp máy (không phải bí mật): Host và công cụ quản trị cùng dùng.
[Environment]::SetEnvironmentVariable('WebErp__DataProtectionCertificateThumbprint', $cert.Thumbprint, 'Machine')
```

Cấp quyền đọc private key cho app pool: `certlm.msc` → Personal → Certificates → chứng chỉ trên → All Tasks → Manage Private
Keys → thêm `IIS AppPool\WebErp` quyền Read.

## 4. Khóa mật khẩu kiểu cũ (bí mật)

BE kiểm mật khẩu `SY_User` bằng thuật toán của ERP. Thuật toán cần một hằng 6 ký tự của hệ cũ (tài liệu 12 §2.5 trong repo FE).
Hằng này **không có trong repo**. Lấy từ đội phát triển ERP, rồi đặt làm biến môi trường **chỉ của app pool**:

```powershell
& "$env:windir\system32\inetsrv\appcmd.exe" set config -section:system.applicationHost/applicationPools `
  /+"[name='WebErp'].environmentVariables.[name='WebErp__LegacyPassword__Key',value='<HẰNG 6 KÝ TỰ>']" /commit:apphost
& "$env:windir\system32\inetsrv\appcmd.exe" set config -section:system.applicationHost/applicationPools `
  /+"[name='WebErp'].environmentVariables.[name='ASPNETCORE_ENVIRONMENT',value='Production']" /commit:apphost
```

## 5. Runner tự host (CD)

GitHub khuyến cáo **không dùng runner tự host cho repo công khai**, vì mã lạ có thể chạy trên máy của bạn. Repo BE đang để công
khai đến khi chạy thật (QĐ-08). Vì vậy BẮT BUỘC làm đủ các bước sau:

1. **Tài khoản chạy runner:** tạo user Windows riêng, ví dụ `weberp-deploy`. Không cho vào nhóm Administrators. Cấp quyền
   Modify trên `D:\WebErp\sites\app`, `D:\WebErp\tools`, `D:\WebErp\releases`.
2. **Cài runner:** repo → Settings → Actions → Runners → New self-hosted runner → Windows x64. Làm theo lệnh GitHub hiện ra
   trong `C:\actions-runner`. Khi chạy `config.cmd`:
   - thêm `--labels weberp-vps`;
   - chọn chạy như service, đăng nhập bằng `weberp-deploy`.
3. **Settings → Actions → General:**
   - "Fork pull request workflows from outside collaborators" chọn **Require approval for all external contributors**;
   - "Workflow permissions" chọn **Read repository contents**.
4. **Settings → Environments → New environment `production`:** Deployment branches chọn **Selected branches** → `main`.
5. Nếu `MinhHuySoft` là **organization**: tạo runner group chỉ cho repo này, giới hạn "Selected workflows" là
   `MinhHuySoft/Web-ERP-BE/.github/workflows/deploy.yml@refs/heads/main`.
6. Khi chuyển repo sang riêng tư (QĐ-08), giữ nguyên các bước trên.

## 6. Lần triển khai đầu tiên

1. Push lên `main` (hoặc vào tab Actions → CI → Run workflow trên `main`). Chờ CI xong, Deploy tự chạy.
   - Lúc này `/health` báo `"status":"degraded"` vì chưa có cấu hình. Đó là bình thường.
2. Khởi tạo cấu hình, thêm công ty đầu tiên (PowerShell của tài khoản quản trị):

   ```powershell
   cd D:\WebErp\tools\admin
   .\WebErp.Admin.exe init
   .\WebErp.Admin.exe tenant add MINHHUY --name "Tên công ty" --host erp.congty.vn --db-label "Tên DB"
   # Nhập chuỗi kết nối khi được hỏi (không hiện ra màn hình), ví dụ DB trên cùng VPS dùng Windows Authentication:
   #   Server=localhost;Database=<DB>;Integrated Security=true;TrustServerCertificate=true
   .\WebErp.Admin.exe tenant test MINHHUY
   .\WebErp.Admin.exe tenant list
   ```

3. Cấp quyền SQL cho app pool (DB trên cùng VPS, M1 chỉ đọc):

   ```sql
   CREATE LOGIN [IIS APPPOOL\WebErp] FROM WINDOWS;
   USE [<DB>];
   CREATE USER [IIS APPPOOL\WebErp] FOR LOGIN [IIS APPPOOL\WebErp];
   ALTER ROLE db_datareader ADD MEMBER [IIS APPPOOL\WebErp];
   GRANT SELECT ON dbo.SY_GetUserMenuFnc TO [IIS APPPOOL\WebErp];
   ```

   Lưu ý: `tenant test` chạy bằng tài khoản Windows của người gõ lệnh, không phải app pool. Với Windows Authentication, kiểm
   thật bằng cách đăng nhập trên web.
4. Mở `https://erp.congty.vn/` và đăng nhập bằng tài khoản ERP.

DB của khách ở máy chủ khác: dùng SQL login riêng (không `db_owner`), đi qua VPN hoặc cổng chỉ mở cho IP của VPS, có
`Encrypt=True` (QĐ-07, bổ sung 2026-11-11, mục 3).

## 7. Sao lưu

Task Scheduler → Create Task, chạy hằng ngày bằng tài khoản quản trị:

```text
powershell.exe -NoProfile -ExecutionPolicy Bypass -File D:\WebErp\tools\backup.ps1 -Destination \\<nơi-ngoài-VPS>\weberp
```

`backup.ps1` được mỗi lần triển khai chép sẵn vào `D:\WebErp\tools\`. Bản sao lưu gồm `config-*.db` và thư mục khóa. Cùng với
tệp `.pfx` ở bước 3, như vậy là đủ để dựng lại trên VPS mới.

## 8. Kiểm tra và xử lý sự cố

| Việc | Lệnh / nơi xem |
|---|---|
| BE đang chạy bản nào | `Invoke-RestMethod http://127.0.0.1:8081/health` |
| Nhật ký lỗi | Event Viewer → Windows Logs → Application (nguồn `WebErp.Host` / `.NET Runtime` / `IIS AspNetCore Module V2`) |
| Bảo trì một công ty (BE trả 503) | `WebErp.Admin.exe tenant maintenance MINHHUY 30` … `tenant maintenance MINHHUY off` |
| Đăng xuất mọi người của một công ty | `WebErp.Admin.exe tenant logout-all MINHHUY` |
| Cài lại một bản cũ | Actions → Deploy → Run workflow, nhập ID lần chạy CI của bản đó |
| Quay lui thủ công | Bản trước nằm ở `D:\WebErp\releases\previous` (deploy.ps1 tự quay lui khi bản mới không khỏe) |
