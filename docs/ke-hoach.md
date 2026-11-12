# Kế hoạch Backend Web-ERP

> Ràng buộc chung: [QUYET-DINH.md](https://github.com/MinhHuySoft/Web-ERP/blob/main/docs/QUYET-DINH.md) (QĐ-01…QĐ-08).
> Cấu trúc mã: [cau-truc.md](cau-truc.md). Chuẩn trao đổi với FE: ERP-HTML v1.0 trong repo FE.
> Mốc nào xong thì đánh dấu ở đây và ghi ngày.

## Nguyên tắc đi từng mốc

- Mỗi mốc phải **chạy được trên VPS** qua CD, không có mốc "chỉ có mã".
- **Đọc trước, ghi sau.** Chừng nào chưa có DB pilot (CQ-01) và bộ test đối chiếu với WinForms, BE không ghi bảng ERP.
- **Không bịa schema.** Mọi bảng / cột ERP được dùng phải có trong tài liệu phân tích hệ cũ, hoặc đã kiểm trên DB thật.
  Chỗ chưa chắc thì đọc mềm (tên cột theo kết quả trả về) và ghi chú "chưa xác minh".

## Các mốc

| Mốc | Nội dung | Ghi DB ERP? | Trạng thái |
|---|---|---|---|
| **M0** | Repo, solution 6 project, CI build + test, quy tắc (`AGENTS.md`, `.gitignore`) | Không | Xong 2026-11-12 |
| **M1** | Vỏ chạy được: cấu hình SQLite + CLI quản trị, nhận diện công ty theo domain, đăng nhập tài khoản ERP, cookie, `session`, `menu`, `home`, màn chưa hỗ trợ, `/health`, contract test, CD qua runner trên VPS | Không | Xong 2026-11-12 (chờ chạy thử trên VPS) |
| M2 | Màn danh sách chỉ đọc: biên dịch metadata `SY_FrmLstTbl` / `SY_FrmCfg` (GD2, HDE, LYT), lọc, lưới, tra cứu F4. Pilot `CS_ReceiptFrm` | Không | Chưa |
| M3 | Xem chứng từ (`EDIT` `data-mode="view"`), tab chi tiết nạp lười, quyền form (`GetPermission`), ẩn cột tiền | Không | Chưa |
| M4 | Ghi: migration schema `web`, `SavePipeline` (journal `__idem`, `isLock`, khóa sổ, `DateUpdate` → 409, số chứng từ, `UserAutoID`, XML hook, `Msg/MsgType`), `SY_UserLog`, mã công ty trong form | **Có** | Chưa; cần CQ-01 |
| M5 | Trang quản trị trên web (TOTP), chế độ bảo trì, đăng xuất cả công ty từ web, hằng mật khẩu ERP đặt trên web | Không | Xong phần chính 2026-11-12; còn thu hồi phiên theo từng user (cần schema `web`, M4) |
| M6 | Báo cáo (`*ReportStp`), xuất Excel, đính kèm có kiểm quyền | Không | Chưa |

## M1 — chi tiết

**Đã làm:**

- [x] Solution `WebErp.slnx`, `global.json` (SDK 10, cho phép SDK mới hơn), `Directory.Build.props`, quản lý phiên bản NuGet tập trung.
- [x] `WebErp.Core`: công ty, người dùng ERP, kiểm mật khẩu kiểu cũ (MD5 + muối, khóa riêng lấy từ cấu hình máy chủ, không
      nằm trong repo), cây menu, thông điệp.
- [x] `WebErp.Html`: `HtmlWriter` (tự encode, cấm `class` / `style` / `on*`), renderer `login`, `session`, `menu`, `screen`,
      `messages`, trang lỗi.
- [x] `WebErp.Config`: SQLite cấu hình, migration, mã hóa chuỗi kết nối bằng Data Protection, nạp lại khi đổi.
- [x] `WebErp.Erp`: mở kết nối theo công ty, đọc `SY_User`, đọc menu qua `<Module>_GetUserMenuFnc`, thử kết nối.
- [x] `WebErp.Host`: domain → công ty, đăng nhập / đăng xuất, cookie tự chứa (hạn trượt + hạn tối đa + mốc đăng xuất công ty
      + kiểm user còn hoạt động), chống đăng nhập sai liên tục, `/erp/session`, `/erp/menu`, `/erp/home`, `/erp/screens/*`
      (chưa hỗ trợ), `/health`, `/config.js`, phục vụ FE cùng origin.
- [x] `WebErp.Admin`: dòng lệnh quản trị cấu hình (khởi tạo, thêm công ty, domain, chuỗi kết nối, thử kết nối, bảo trì,
      đăng xuất cả công ty, sao lưu).
- [x] Test: unit, Host (WebApplicationFactory), contract (HTML qua `validate.js` của FE), integration trên DB giả LocalDB.
- [x] CI trên runner của GitHub; CD trên runner tự host ở VPS; script `package.ps1`, `deploy.ps1` (có quay lui), `backup.ps1`.

**Giới hạn của M1 (có chủ ý):**

- Không ghi bảng ERP nào, kể cả `SY_UserLog` (để M4, khi đã kiểm cột trên DB thật).
- Không gọi `SY_SetupStp` khi đăng nhập (QĐ-01.6: SP này khóa chứng từ cũ).
- Chưa có thu hồi phiên theo từng user (cần schema `web` ở M4). Đã có mốc "đăng xuất cả công ty" trong SQLite.
- Một domain nhiều công ty: cookie giữ một công ty. Đổi công ty là đăng nhập lại. Kiểm mã công ty trong form (chống lưu nhầm
  ở tab cũ) làm ở M4, khi bắt đầu ghi.
- Menu đọc qua hàm `<Module>_GetUserMenuFnc`. Hàm lỗi thì báo lỗi, **không** rơi về đọc cả bảng menu như WinForms (để không
  lộ menu không được phép).

## Việc chủ dự án cần làm để chạy M1 trên VPS

Xem [deploy/README.md](../deploy/README.md). Tóm tắt:

1. Cài IIS + ASP.NET Core Hosting Bundle .NET 10 trên VPS.
2. Tạo thư mục, app pool, site, binding, quyền NTFS.
3. Đặt khóa mật khẩu kiểu cũ vào biến môi trường của app pool (không đưa vào repo).
4. Chạy `WebErp.Admin.exe init`, rồi `tenant add` cho công ty đầu tiên.
5. Cài GitHub runner tự host (nhãn `weberp-vps`) và chỉnh cài đặt Actions an toàn cho repo công khai.
6. Push lên `main` → CI đóng gói → Deploy tự cập nhật và khởi động lại BE.
