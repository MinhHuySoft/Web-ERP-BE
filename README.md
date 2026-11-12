# Web-ERP — Backend

Backend của Web-ERP: đọc metadata và dữ liệu ERP.NET (SQL Server, chạy song song với WinForms), trả **HTML theo chuẩn
ERP-HTML** cho [Frontend](https://github.com/MinhHuySoft/Web-ERP).

- C# / ASP.NET Core **.NET 10**, IIS trên **1 VPS Windows** (QĐ-07). Mỗi công ty một domain hoặc nhiều công ty chung domain.
- Cấu hình công ty (domain, chuỗi kết nối đã mã hóa) trong **SQLite**. Đăng nhập bằng tài khoản ERP (`SY_User`).
- Push lên `main` → CI build + test + đóng gói → **CD qua runner tự host** trên VPS tự cập nhật và khởi động lại BE.

| Tài liệu | Nội dung |
|---|---|
| [AGENTS.md](AGENTS.md) | Quy tắc bắt buộc, đọc gì trước |
| [docs/ke-hoach.md](docs/ke-hoach.md) | Các mốc M0–M6, đang ở đâu |
| [docs/cau-truc.md](docs/cau-truc.md) | Project, thư mục, luồng request, chạy thử trên máy dev |
| [deploy/README.md](deploy/README.md) | Cài VPS, IIS, runner, sao lưu |

```bash
dotnet build WebErp.slnx
dotnet test --solution WebErp.slnx
```
