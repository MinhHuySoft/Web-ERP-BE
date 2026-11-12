# AGENTS.md — điểm vào cho agent lập trình (Backend Web-ERP)

Backend của **Web-ERP**: dựng lại ERP.NET (WinForms) thành web, chạy song song với WinForms trên cùng DB (QĐ-01).
BE viết **C#, ASP.NET Core .NET 10**, trả **HTML thô theo chuẩn ERP-HTML v1.0**. FE (repo
[`MinhHuySoft/Web-ERP`](https://github.com/MinhHuySoft/Web-ERP)) dựng giao diện từ HTML đó.
Ngôn ngữ tài liệu, chú thích, thông điệp: **tiếng Việt**. Tên trong mã: tiếng Anh, trừ tên bảng / cột ERP.

## Đọc gì trước

| Bạn đang làm | Đọc |
|---|---|
| Bất cứ việc gì | [`docs/cau-truc.md`](docs/cau-truc.md) (project, thư mục, luồng request) → [`docs/ke-hoach.md`](docs/ke-hoach.md) (mốc, giới hạn hiện tại) |
| Sinh HTML cho FE | Repo FE: `docs/huong-dan/be-tra-html-cho-fe.md` → đặc tả `docs/kien-truc/02-chuan-html-erp.md` |
| Đọc / ghi DB ERP | Repo FE: `docs/legacy-analysis/` (bảng `SY_*`, nghiệp vụ) + `D:\Data\Skills\SQL_SERVER_ERP_DATABASE_SAFETY_SKILL.md` |
| Cài VPS, CD | [`deploy/README.md`](deploy/README.md) |
| Vì sao làm thế này | Repo FE: `docs/QUYET-DINH.md` (QĐ-01…QĐ-08) |

## Lệnh

```bash
dotnet build WebErp.slnx
dotnet test --solution WebErp.slnx          # contract test cần repo FE ở ../Web-ERP (hoặc biến WEBERP_FE_WEB)
dotnet run --project src/WebErp.Host        # http://localhost:5190 — cần dữ liệu thử, xem docs/cau-truc.md mục "Chạy thử"
```

## Quy tắc bắt buộc

- **Không** commit: chuỗi kết nối, mật khẩu, hằng mật khẩu kiểu cũ, khóa Data Protection, chứng chỉ, `config.db`,
  `appsettings.*.Local.json`, dữ liệu / tệp thật của khách, mã dịch ngược `Tools.dll` / `ERP.NET`. Repo đang **công khai**.
- **Không** nối DB khách khi phát triển / test. Test chạy trên DB **giả** (`tests/fake-erp/fake-erp.sql`, LocalDB).
- **Không bịa schema ERP.** Bảng / cột phải có trong tài liệu phân tích hệ cũ hoặc đã kiểm trên DB thật. Chưa chắc thì đọc mềm
  theo tên cột và ghi chú "chưa xác minh".
- **Chưa ghi bảng ERP** cho tới M4 (`docs/ke-hoach.md`). Khi ghi: chỉ qua `Erp/Save` / `Erp/Actions`, gọi SP / hook ERP, không
  tự `INSERT` / `UPDATE` bảng ERP.
- Phụ thuộc một chiều: `Host`/`Admin` → `Html`, `Erp`, `Config` → `Core`. `Html` không truy cập DB, `Erp` không sinh HTML.
- HTML chỉ ghi qua `HtmlWriter` (tự encode, cấm `class` / `style` / `on*` / `script`). Trang mới phải có trong
  `WebErp.ContractTests` và qua `validate.js` của FE: **0 lỗi**.
- Đổi cách FE–BE trao đổi: sửa repo FE trước (đặc tả + hướng dẫn BE + validator), rồi cập nhật `fe.ref`.
- Không lộ lỗi nội bộ (SQL, stack) ra trình duyệt: chỉ thông điệp + mã lỗi (`Middleware/ErrorHandling.cs`).
