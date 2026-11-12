# Cấu trúc Backend (theo mã hiện tại)

> Bản thiết kế gốc: `docs/huong-dan/be-cau-truc-va-to-chuc.md` của repo FE (QĐ-08). Tài liệu này mô tả **mã đang có**.
> Thêm / đổi thư mục thì sửa ở đây.

## 1. Project và chiều phụ thuộc

```text
WebErp.Host ─┬─► WebErp.Web ──┬─► WebErp.Html ───┐
WebErp.Admin ┘                ├─► WebErp.Config ─┼─► WebErp.Core
              └──────────────► WebErp.Erp ───────┘
```

`WebErp.Web` (thêm khi làm trang quản trị) gom phần web dùng chung cho hai site: trả ERP-HTML, lỗi → mã HTTP, header bảo mật,
`/config.js` + tệp tĩnh FE. Không có nghiệp vụ.

| Project | Có gì (M1) |
|---|---|
| `src/WebErp.Core` | `Tenancy/` (công ty, kiểm tên, `ITenantStore`), `Auth/` (`LegacyPassword` — hash kiểu cũ, `IErpUsers`), `Menu/` (`MenuTree`, `IErpMenu`), `Messages/`, `Errors/` |
| `src/WebErp.Html` | `HtmlWriter` (tự encode, cấm `class`/`style`/`on*`), `HtmlPage`, `Roles/` (mỗi vai trò `data-erp` một renderer), `Screens/` (trang chủ, màn chưa có, trang lỗi) |
| `src/WebErp.Erp` | `Connection/` (mở kết nối theo công ty, timeout), `Security/ErpUsers` (`SY_User`), `Menu/ErpMenu` (`<Module>_GetUserMenuFnc`), `Diagnostics/ErpProbe` |
| `src/WebErp.Config` | `ConfigDatabase` (SQLite, Host chỉ đọc), `ConfigMigrator` (chạy `db/config/*.sql`), `SqliteTenantStore` (bản chụp + nạp lại theo `config_meta.version`), `ConfigWriter` (chỉ Admin), `SecretProtector`, `DataProtectionSetup` |
| `src/WebErp.Host` | `Program.cs`, `Middleware/` (công ty theo host, lỗi → ERP-HTML, `/config.js`, header bảo mật), `Auth/` (cookie tự chứa, kiểm cookie, chống dò mật khẩu, menu cache), `Endpoints/` (`/erp/login`, `/erp/logout`, `/erp/session`, `/erp/menu`, `/erp/home`, `/erp/screens/*`, `/erp/reports/*`, `/health`) |
| `src/WebErp.Web` | `HtmlResults`, `ErrorHandling`, `SecurityHeaders`, `FrontendConfig` (`/config.js` + FE cùng origin) |
| `src/WebErp.Admin` | Trang quản trị trên web (`Web/`: cổng host + HTTPS, đăng nhập mật khẩu + TOTP, thiết lập lần đầu có QR, công ty, cài đặt chung, nhật ký) và dòng lệnh (`Cli.cs`: `init`, `tenant …`, `admin …`, `backup`) |
| `tests/WebErp.Tests` | Unit + Host qua WebApplicationFactory (ERP giả trong bộ nhớ) |
| `tests/WebErp.ContractTests` | HTML của renderer chạy qua `validate.js` của FE trong Edge không giao diện: 0 lỗi, 0 cảnh báo |
| `tests/WebErp.IntegrationTests` | Đọc `SY_User`, menu trên DB **giả** tạo tạm ở LocalDB, xóa sau khi chạy |
| `db/config/` | Migration SQLite cấu hình |
| `deploy/` | `package.ps1`, `deploy.ps1`, `backup.ps1`, hướng dẫn VPS |
| `fe.ref` | Commit của repo FE mà BE đóng gói kèm và dùng để kiểm hợp đồng |

## 2. Luồng một request

```text
/config.js          → FrontendConfig: 4 điểm vào trỏ vào /erp/*
/ , /css, /js …     → tệp tĩnh FE (wwwroot; máy dev: ../Web-ERP/web)
/erp/*              → TenantResolution: host → công ty (lạ: 404; bảo trì: 503)
                    → Cookie: công ty thuộc host, chưa quá hạn tối đa, sau mốc "đăng xuất cả công ty", user còn hoạt động
                    → Endpoint: lấy phiên → Erp (đọc) → Html (sinh) → 200 / 303 / 401 / 403 / 404 / 429 / 503
/health             → phiên bản đang chạy + trạng thái cấu hình (deploy.ps1 dùng)
```

## 3. Chạy thử trên máy dev

Cần: .NET SDK 10 (hoặc mới hơn), LocalDB, repo FE ở `../Web-ERP`.

```powershell
# 1. DB ERP GIẢ trên LocalDB + công ty "DEV" gắn với localhost (khóa mật khẩu giả DEV000 trong appsettings.Development.json)
./tools/dev/New-DevFakeDb.ps1
# 2. Chạy BE (FE phục vụ cùng origin)
dotnet run --project src/WebErp.Host
# 3. Mở http://localhost:5190/  — đăng nhập admin / 123 hoặc ketoan / abc
```

Dữ liệu chạy thử nằm ở `.weberp/` (đã chặn trong `.gitignore`).

## 4. Quy ước

| Thứ | Quy ước |
|---|---|
| Namespace | Trùng đường dẫn thư mục |
| Renderer | `<Vai trò data-erp>Renderer` trong `Html/Roles` |
| Endpoint | `<Nhóm URL>Endpoints` trong `Host/Endpoints`, dùng `MapGroup` |
| SQL | Tham số hóa (Dapper). Chuỗi dùng `DbString { IsAnsi = true, Length = n }` khi cột là `varchar(n)` |
| Migration | `NNN_mô_tả.sql`, chỉ thêm, không sửa tệp đã chạy |
| Thông điệp lỗi | Tiếng Việt, không lộ chi tiết nội bộ |
