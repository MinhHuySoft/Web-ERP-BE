# Form cứng (viết tay) của ERP WinForms chưa có trên web

> **Trạng thái: BẢN NHÁP chờ chủ dự án duyệt** (Phần B, bước 2). Chưa port form nào theo tài liệu này.
> Cột "Menu TinPhong" chờ file xuất `SY_Menu` của DB TinPhong (phiên VPS), hiện chỉ điền chỗ đã biết từ tài liệu 30.

## 0. Sửa mô tả trên trang quản trị (không cần build / deploy)

Từ PR "mô tả form sửa trên trang quản trị", tệp mô tả (cùng định dạng `src/WebErp.Erp/Metadata/Overlays/*.json`) còn có thể lưu trong
`config.db` (bảng `form_overlay`, migration `004`) qua trang quản trị → **Mô tả form cứng**:

- **Thứ tự áp:** mô tả riêng một công ty → mô tả chung trên web → tệp nhúng trong BE. Host đọc lại khi cấu hình đổi (vài giây).
- **Form mới:** có mô tả trên web thì web coi form đó là form viết tay (mô tả che `SY_FrmLstTbl` / `SY_FrmCfg` cùng tên), kể cả khi tên lớp
  chưa có trong `hand-coded-forms.txt` (form của bản ERP mới).
- **Kiểm trước khi lưu:**
  - JSON đọc được, khóa `form` khớp tên.
  - Mọi đoạn SQL qua `SqlTemplate.EnsureReadOnly`.
  - Nút "Kiểm trên DB công ty" dựng form trên DB thật và chạy `TOP (0)` từng slot trong giao dịch luôn rollback: chỉ lấy tên cột, không đọc dòng dữ liệu nào.
- **Lưu / xóa:** cần mã TOTP, ghi nhật ký thao tác (không ghi JSON), giữ lịch sử từng phiên bản để khôi phục.
- **"Form còn thiếu của công ty":** đọc `{Module}_Menu` và cấu hình form của một công ty, liệt kê form web chưa dựng được.
- **Khi chạy**, mô tả trên web đi qua đúng lớp bảo vệ như cấu hình DB: kiểm chỉ đọc, giá trị là tham số, giao dịch luôn rollback.

Tệp nhúng vẫn là nơi lưu mô tả đã ổn định (có test). Mô tả viết trên web nên chép về repo khi đã dùng ổn.

## 1. Nguồn và cách lập

- **Bản ERP:** bản cài TinPhong (`ERP.NET.exe` + `Tools.dll`, 2026-11), do chủ dự án gửi.
- **Dịch ngược và giải mã chuỗi:**
  - Dịch ngược bằng `ilspycmd` 9.1. Chuỗi bị .NET Reactor mã hóa, đã giải mã bằng hàm giải mã có sẵn trong chính assembly.
  - Toàn bộ mã nằm **ngoài repo** (`D:\Projects\erp-winforms-src\`) và không commit (AGENTS.md). Tài liệu này chỉ ghi tên form, bảng, cột, SP, không chép mã.
- **Danh sách form:** các lớp kế thừa `Form` trùng với form người dùng nêu, thêm các form TinPhong mà bước 1 chuyển sang "chưa có trên web" (PR #29).
  - WinForms tìm **lớp trùng tên trước** khi đọc `SY_FrmLstTbl` (tài liệu 30 §2.2). Vì vậy một lớp viết tay luôn che dòng cấu hình cùng tên.
- **Đối chiếu:** tài liệu `30-metadata-menu-form-db.md` (repo FE, bản Medcom) và 46 tệp mô tả `src/WebErp.Erp/Metadata/Overlays/*.json` đang dùng.

Chú thích các cột:

- **Ghi:** form có `INSERT` / `UPDATE` / `DELETE` / SP ghi, hoặc lệnh cấp máy chủ.
- **Nguy hiểm:** thuộc nhóm phải hỏi chủ dự án trước khi đưa lên web, như sao lưu / phục hồi, khóa sổ, đánh lại số chứng từ, tính giá, phát hành chứng từ điện tử, xóa hàng loạt, sửa cấu hình hệ thống.
- **Web hiện tại:**
  - "overlay": đã có tệp mô tả, web hiện được, thường là chỉ đọc.
  - "chưa có": màn "chưa có trên web".
- **Ràng buộc chung:** mọi việc ghi bảng ERP đều phải chờ M4 (`docs/ke-hoach.md`: SavePipeline, kiểm khóa sổ, số chứng từ, gọi SP / hook như WinForms). Trước M4 chỉ port phần **xem**.

## 2. Bảng tổng hợp

| # | Form | Menu TinPhong | Chức năng | Bảng / SP chính | Ghi | Nguy hiểm | Độ khó | Web hiện tại | Đề xuất |
|---|---|---|---|---|---|---|---|---|---|
| 1 | `AR_Invoice2Frm` | 0607 (tài liệu 30) | Hóa đơn bán hàng "bản 2": **kế thừa nguyên** `AR_InvoiceFrm`, chỉ khác khóa cấu hình / quyền | `AR_InvoiceTbl`, `AR_InvoiceDetailTbl`, `AR_VATInvoiceTbl`, `AR_InvoiceAcc2View` | Có | Có (phát hành HĐĐT) | Thấp | chưa có | **Đợt 1** (xem) |
| 2 | `NguoiDungFrm` | 010101? | Danh mục người dùng | `SY_User`, `SY_UserGroup`, `CF_BranchTbl` | Có (mật khẩu) | Có | Cao | overlay (lỗi 207 ở TinPhong) | **Đợt 1** sửa phần xem; ghi ở đợt 3 |
| 3 | `NguoiDungNhomFrm` | 010100? | Nhóm người dùng + thông tin chung của nhóm | `SY_UserGroup`, `SY_UserGroupDetail` | Có | Thấp | Thấp | overlay | **Đợt 1** (xem), đợt 2 (ghi) |
| 4 | `UserLogFrm` | 0101081 | Nhật ký đăng nhập + thao tác | `{Mod}_UserLog`, `{Mod}_UserLogDetail`, `{Mod}_Menu` | Hiếm (xóa log) | Thấp | Thấp | overlay | **Đợt 1** (thêm tab Daily log) |
| 5 | `PeriodLockDataFrm` | 0108? | Khóa dữ liệu theo kỳ: **form cấu hình EDIT**, không phải lớp viết tay | `SY_Period.isLockData` | Có | Có (khóa dữ liệu) | Thấp nếu có `SY_FrmCfg` | dựng từ cấu hình | Kiểm cấu hình TinPhong; ghi ở M4 |
| 6 | `GJ_DocBatchSetupfrm` | ? | Lô chứng từ ghi sổ (master / chi tiết) | `GJ_DocBatchSetupTbl`, `GJ_DocBatchSetupDetailTbl` | Có | Không | Thấp | chưa có | **Đợt 1** (xem, tệp mô tả EDIT) |
| 7 | `SY_LockDocumentFrm` | ? | Duyệt / khóa chứng từ hàng loạt theo kỳ, phân hệ | `vDocumentForLock`, `vGeneralAcc`, bảng chi tiết theo `SourceID` | Có (`UPDATE … IsLock`) | Vừa | Trung bình | overlay (thiếu tab chi tiết) | Đợt 1 (bổ sung tab chi tiết), ghi ở M4 |
| 8 | `FiscalFrm` | ? | Năm tài chính: tạo / sửa / xóa năm, quý, kỳ | `SY_Year`, `SY_Quarter`, `SY_Period` | Có | Có (xóa năm xóa cả kỳ đã khóa) | Trung bình | overlay | Đợt 2 |
| 9 | `ChangePasswordFrm` | ? | Tự đổi mật khẩu / admin đặt lại | `SY_User` (Password, HoTen, StartupForm) | Có (mật khẩu) | Có | Trung bình | overlay (chỉ xem) | Đợt 2 (API riêng, TOTP cho đặt lại) |
| 10 | `SystemLangSetupFrm` | ? | Nhãn đa ngôn ngữ hệ thống, trường, control, menu | `SY_StTbl`, `SY_FmtFldTbl`, `SY_FrmCtrTbl`, `{Mod}_Menu` | Có | Vừa | Trung bình | overlay | Đợt 2 |
| 11 | `NguoiDungPhanQuyenFrm` | 010102 | Phân quyền nhóm theo cây menu, gán hàng loạt, chép quyền | `{Mod}_UserGroupPermisstion`, `{Mod}_Menu`, `SY_String2TableFnc` | Có | Có | Cao | overlay (gần đúng) | Đợt 3 |
| 12 | `NguoiDungPhanQuyenWAFrm` | ? | Như trên cho menu web app (`WA_`) | `WA_UserGroupPermisstion`, `WA_Menu` | Có | Có | Trung bình | overlay | Đợt 3 (dùng chung với #11) |
| 13 | `AR_InvoiceTinPhongMNFrm` | ? | Hóa đơn "TP Miền Nam": coupon, điểm, khuyến mãi, HĐĐT, đẩy DMS | `AR_InvoiceTbl` + `AR_InvoiceCouponTbl`, `AR_VATInvoiceTbl`, `AR_PhieuGiamGiaTbl`, SP `AR_KiemTra_CouponSeri_Stp`, `AR_Invoice_eInvoiceData_Stp` | Có | Có | Cao | chưa có | Đợt 3 (xem trước), ghi / phát hành: hỏi |
| 14 | `IV_StockOutputVDFrm` | ? | Phiếu xuất **chuyển kho điện tử** (VNPT), dữ liệu `IV_TranferTbl` | `IV_TranferTbl`, `IV_TranferDetailTbl`, `EL_Invoice2ListTbl`, `EI_Pattern2Tbl` | Có (**đổi DocumentID** sau phát hành) | Có | Cao | chưa có | Đợt 3 (xem), phát hành: hỏi |
| 15 | `IV_StockOutputVD2Frm` | ? | Phiếu xuất **gửi đại lý điện tử**, như #14 có giá | như #14, `EL_Invoice3ListTbl`, `EI_Pattern3Tbl` | Có | Có | Trung bình (sau #14) | chưa có | Cùng #14 (một form có tham số) |
| 16 | `eInvoiceSettingFrm` | ? | Thiết lập kết nối hóa đơn điện tử theo chi nhánh, tải mẫu | `EI_SettingTbl`, `EI_PatternTbl`, `EI_XMLTypeTbl` | Có (mật khẩu mã hóa giải ngược được) | Có | Trung bình | chưa có | Đợt 3 (xem, ẩn mật khẩu) |
| 17 | `ePostSettingViettelFrm` | ? | 6 tài khoản Viettel Post, điểm lấy hàng, dịch vụ | `EP_SettingTbl`, `EP_SenderListTbl`, `EP_Order_ServiceTbl` | Có | Có | Trung bình | overlay (chỉ xem) | Giữ chỉ xem; nút kiểm tra kết nối: hỏi |
| 18 | `SetupFrm` | 010108 | Thông số hệ thống: công ty, kỳ bắt đầu, phương pháp giá, tài khoản mặc định, định dạng | `SY_Setup`, `SY_InitSetup`, `SY_DocSetup`, `SY_FmatTbl`, `SY_StupAcTbl`, `CF_ChartAccTbl` | Có (có nút **xóa hệ tài khoản**) | Có | Cao | overlay (chỉ xem) | Giữ chỉ xem; ghi: **hỏi** |
| 19 | `PeriodSetupFrm` | 0107 | **Khóa / mở sổ**, khấu hao, phân bổ, giá vốn, kết chuyển | `SY_Period`, SP `SY_CloseAllBookPeriodStp`, `SY_Calculate_UnitCostStp`, `SY_Create_*Stp` | Có | **Rất cao** | Cao | overlay (chỉ xem) | Giữ chỉ xem; xử lý: **hỏi** |
| 20 | `IV_UnitCostProcessFrm` | ? (Medcom 0710) | **Tính giá xuất kho** theo kỳ / quý / năm | SP `SY_Calculate_UnitCostStp`, `IV_Cost_AVGD_Stp`, `IV_Cost_LOT_Stp`, `SY_BalanceFIFOTbl` | Có | **Rất cao** | Trung bình | overlay (chỉ xem) | Giữ chỉ xem; tính giá: **hỏi** |
| 21 | `SY_MakeDocumentIDFrm` | ? (Medcom 1604, tắt) | **Đánh lại số chứng từ** trên nhiều bảng | `vDocumentForRebuiltID`, `UPDATE <bảng> SET DocumentID`, cột `RefDoc*` | Có (không giao dịch) | **Rất cao** | Cao | chưa có | **Hỏi**; nếu làm thì thành SP có giao dịch |
| 22 | `BackupDataFrm` | ? | **Sao lưu DB**, đổi cấu hình máy chủ, thu gọn | `BACKUP DATABASE`, `sp_configure`, `KILL` | Có (cấp máy chủ) | **Rất cao** | Cao | overlay (chỉ thông tin) | **Không port** |
| 23 | `RestoreDataFrm` | ? | **Phục hồi DB**, ghi đè toàn bộ | `RESTORE DATABASE … REPLACE`, `SINGLE_USER` | Có (cấp máy chủ) | **Cao nhất** | Cao | overlay (chỉ thông tin) | **Không port** |
| 24 | `SystemMenuManagerFrm` | (mở từ form khác) | Quản trị cây menu: đổi MenuID, xóa nhánh, chép quyền | `{Mod}_Menu`, `{Mod}_UserGroupPermisstion` | Có | Cao | Cao | chưa có | Không port giai đoạn này |
| 25 | `FormControlerSetupFrm` | (mở từ form khác, Admin) | Công cụ cấu hình form + tiện ích DB (xóa dữ liệu, giấy phép, GRANT…) | `SY_Frm*`, `SY_FmtFldTbl`, `SY_UsLc` | Có | **Cực cao** | Cao | chưa có | **Không port** |
| 26 | `FrmReportParaSetup` | (mở từ báo cáo) | Cấu hình tham số báo cáo (có SQL nguồn) | `SY_RpDTbl`, `SY_RpTbl` | Có | Vừa | Trung bình | chưa có | Để sau (cùng M6 báo cáo) |
| 27 | `ViewDataFrm` | (hộp chọn dùng chung) | Popup bảng / chọn dòng do form khác truyền dữ liệu | (không tự đọc DB) | Không | Không | Thấp | chưa có | Thành phần dùng chung khi cần, không phải màn |

Không phải form cứng, không cần port riêng:

- **`HV_*` (Haravan):** không có lớp hay chuỗi nào. `HaravanAPI` / `HaravanButtonCtl` thực chất là mã Zalo ZNS chép lại.
- **`BeeC_*` (gồm `BeeC_SyncCenter2Frm`):** form **cấu hình** trong DB TinPhong. Web dựng từ `SY_FrmLstTbl` / `SY_FrmCfg` như các form động khác; nếu lỗi thì sửa ở engine, không port tay.
- **`Logoff` / `Exit`:** web tự xử lý (đăng xuất).

## 3. Đề xuất thứ tự

1. **Đợt 1: chỉ đọc, rẻ, dùng hằng ngày.**
   - `AR_Invoice2Frm`: dùng tệp mô tả `AR_InvoiceFrm` cho khóa `AR_Invoice2Frm`; kiểm `SY_FrmCfg` của TinPhong.
   - `NguoiDungFrm` (phần xem): làm như WinForms, không có cấu hình thì đọc `SY_User` theo cột có thật, không cố định danh sách cột Medcom; luôn chặn cột mật khẩu.
   - `NguoiDungNhomFrm`, `UserLogFrm` (thêm tab Daily log), `GJ_DocBatchSetupfrm`, tab chi tiết của `SY_LockDocumentFrm`.
   - Kiểm `PeriodLockDataFrm` dựng được từ cấu hình TinPhong.
2. **Đợt 2: ghi dữ liệu mức vừa (sau M4).**
   - `NguoiDungNhomFrm` (ghi), `FiscalFrm`, `SystemLangSetupFrm`.
   - `ChangePasswordFrm`: API riêng ở BE. Tự đổi phải kiểm mật khẩu cũ; admin đặt lại cần quyền Admin trên `NguoiDungFrm` + TOTP. Mật khẩu băm theo đúng thuật toán WinForms đang dùng.
3. **Đợt 3: phân quyền, chứng từ riêng TinPhong, thiết lập tích hợp.**
   - `NguoiDungFrm` (ghi), `NguoiDungPhanQuyenFrm` + `WAFrm` (một thành phần có tham số module).
   - `AR_InvoiceTinPhongMNFrm`, `IV_StockOutputVD*Frm` (một form có tham số), `eInvoiceSettingFrm` (phần xem).
4. **Phải hỏi chủ dự án trước** (mặc định giữ chỉ xem hoặc không port):
   - khóa / mở sổ `PeriodSetupFrm`;
   - tính giá `IV_UnitCostProcessFrm`;
   - đánh lại số `SY_MakeDocumentIDFrm`;
   - sửa thông số `SetupFrm`;
   - phát hành HĐĐT / phiếu kho điện tử;
   - nút kiểm tra kết nối Viettel Post.
5. **Không port:** `BackupDataFrm`, `RestoreDataFrm` (để quản trị viên làm trên máy chủ), `FormControlerSetupFrm`, `SystemMenuManagerFrm`.

## 4. Chi tiết từng form

Số dòng nguồn là của bản dịch ngược ngoài repo (`readable\`), chỉ để tra lại.

### 4.1 Người dùng, phân quyền, hệ thống

- **`NguoiDungFrm`** (`Tools.SysForm/NguoiDungFrm.cs` 762–869).
  - Có `LoadLayout`. Không có cấu hình thì `SetData("SY_User", "UserName", "*", …, "Disable, UserName")`, tức là đọc **mọi cột**.
  - Tự kiểm `SY_User.BranchID` và bảng `CF_BranchTbl` trước khi dùng. Đây là lý do TinPhong lỗi 207 trên web: tệp mô tả liệt kê cột Medcom (`TenNgan`, `CeoID`…).
  - Lọc theo nhóm, ẩn người ngừng dùng.
  - Ghi:
    - lưới qua `SaveData`;
    - chế độ mật khẩu phức tạp: `INSERT` / `UPDATE SY_User` nối chuỗi;
    - mỗi lần mở / đóng, tự ghi hash cho user có mật khẩu rỗng.
  - Quyền: `GetPermission("NguoiDungFrm")`. Nút Phân quyền chỉ cho Admin.
  - Mở ba form con: đặt lại mật khẩu, phân quyền riêng user, chuyển dữ liệu user.
- **`NguoiDungNhomFrm`** (`NguoiDungNhomFrm.cs` 646–697).
  - Master `SY_UserGroup` (+ đếm user), chi tiết `SY_UserGroupDetail` (chỉ sửa `isDisable`, `Value`).
  - Khi mở chạy `CREATE INDEX` và SP `{Mod}_Update_Permission`. Web không lặp lại việc này.
  - Có cột chi nhánh nếu `SY_UserGroup.BranchID` tồn tại.
- **`NguoiDungPhanQuyenFrm` / `NguoiDungPhanQuyenWAFrm`.**
  - Cây menu + lưới cờ quyền (Run / Add / Edit / Delete / Manager / Admin / AutoLock / HideAmount / Excel / LockDoc).
  - Gán hàng loạt cho nhiều nhóm: `UPDATE … WHERE UserGroupID IN (SY_String2TableFnc(…))`.
  - Chép quyền nhóm (`DELETE` + `INSERT`) làm mất các cờ mới: isHideAmount, isLockDoc, isUnLockDoc, isExportExcel.
  - Bản WA chạy `ALTER TABLE` và `UPDATE` khi mở.
- **`ChangePasswordFrm`.** Đổi mật khẩu bằng `UPDATE SY_User SET Password=…, HoTen=…` nối chuỗi; mật khẩu băm MD5.
  - Chấp nhận khi mật khẩu cũ nhập vào trùng nguyên chuỗi hash.
  - Mật khẩu mới để trống là xóa mật khẩu.
  - Không kiểm quyền.
- **`UserLogFrm`.** Đọc `{Mod}_UserLog` / `{Mod}_UserLogDetail`, phiên đang online qua `master..sysprocesses` (cần quyền máy chủ). Xóa log chỉ khi Admin mode. Cột `Note` có thể chứa giá trị nhạy cảm, nên tệp mô tả hiện đang che.
- **`SystemLangSetupFrm`.** 4 lưới sửa nhãn (VN / EN / CH) lưu một lần; khi mở chạy 8 lệnh `ALTER TABLE` + 3 lệnh `INSERT SY_StTbl`.
- **`SystemMenuManagerFrm`.** Đổi MenuID cả nhánh (cập nhật cả quyền), xóa / tắt nhánh, thêm menu. Cột `NotifyIconSQLCmd` (WA) chứa SQL để chạy. Không port.
- **`FiscalFrm`.** Tạo năm (sinh quý / kỳ theo tháng bắt đầu) hoặc chép từ năm khác, sửa, xóa. Xóa năm xóa luôn quý / kỳ, kể cả kỳ đã khóa, và không chung giao dịch. Không kiểm quyền.

### 4.2 Thiết lập, kỳ, vận hành

- **`SetupFrm`.**
  - Khoảng 30 khóa `SY_Setup` rời rạc, `SY_InitSetup` (kỳ bắt đầu, phương pháp giá), 5 lưới định dạng, logo (`SY_Res`).
  - Nút QĐ15 / QĐ48 `DELETE FROM CF_ChartAccTbl` rồi nạp lại hệ tài khoản.
  - Đổi ràng buộc mặc định của `CF_ObjectTbl`. Không kiểm quyền.
- **`PeriodSetupFrm`.**
  - Khóa sổ: chuỗi SP `SY_CloseAllBookPeriodStp` → khấu hao → phân bổ → `SY_Calculate_UnitCostStp` → kết chuyển, chạy lâu (tới 15 phút).
  - Sau khóa: sửa `SY_BalanceAccountTbl` và xóa bút toán rỗng. Có "bảo trì" `UPDATE` hàng loạt trên mọi bảng.
- **`IV_UnitCostProcessFrm`.**
  - Tính giá theo kỳ (SP lặp N vòng). Với FIFO thì xóa `SY_BalanceFIFO*` trước.
  - Lỗi ở bản gốc: tính quý / năm luôn gọi `IV_Cost_LOT_Stp`, kể cả khi phương pháp là AVG. Không sao chép hành vi này.
- **`SY_MakeDocumentIDFrm`.**
  - `UPDATE <bảng> SET DocumentID=mới WHERE DocumentID=cũ` + cột `RefDoc*` ở khoảng 10 bảng, không giao dịch, lỗi bị bỏ qua.
  - Khi người dùng ADMIN mở form, `GetDocPreFix` ghi sai `SY_DocSetup`; đã thấy dấu vết trên DB thật.
- **`SY_LockDocumentFrm`.** `UPDATE <bảng> SET IsLock=1|0 WHERE DocumentID=…`, tên bảng lấy từ view, cần quyền Manager. Không xét cờ `isLockDoc` / `isUnLockDoc`.
- **`GJ_DocBatchSetupfrm`.** Master / chi tiết chuẩn qua `SaveData`. Người không phải Admin / Manager chỉ thấy bản ghi mình tạo.
- **`BackupDataFrm` / `RestoreDataFrm`.**
  - `BACKUP` / `RESTORE DATABASE`, `sp_configure`, `KILL` mọi phiên, `SINGLE_USER`.
  - Quyền chỉ dựa vào tên đăng nhập bắt đầu bằng "ADMIN".
- **`FormControlerSetupFrm`, `FrmReportParaSetup`, `ViewDataFrm`:** công cụ cho quản trị / lập trình viên và thành phần dùng chung (xem bảng §2).
- **`PeriodLockDataFrm`:** không có lớp. Menu "Khóa dữ liệu" trỏ tới form cấu hình EDIT trên cột `SY_Period.isLockData`.

### 4.3 Chứng từ riêng và tích hợp

- **`AR_Invoice2Frm`.**
  - Lớp con rỗng của `AR_InvoiceFrm`. MainFrm đặt `form.Name = "AR_Invoice2Frm"`, nên cấu hình và quyền theo khóa này.
  - Menu có Para thì thành `AR_InvoiceFrm_EXPORT`.
- **`AR_InvoiceTinPhongMNFrm`.**
  - Chép lại từ `AR_Invoice2OldFrm`, không kế thừa. Thêm coupon / điểm / khuyến mãi / DMS (`DMSService.Push_Invoice`).
  - Hủy hóa đơn đưa mọi số lượng / tiền về 0, và "bỏ hủy" **không khôi phục**.
  - Mỗi lần mở chạy `UPDATE AR_InvoiceTbl SET isExport=0 …`.
- **`IV_StockOutputVDFrm` / `VD2Frm`.**
  - Dữ liệu `IV_TranferTbl` (`isInvoiceOut=1`), engine `eInvoice2Engine` / `eInvoice3Engine` (VNPT SOAP).
  - Sau phát hành, **đổi `DocumentID`** thành ký hiệu + số. Hủy thì đưa chi tiết về 0.
  - Khi mở chạy `ALTER TABLE IV_TranferTbl`.
- **`eInvoiceSettingFrm` / `ePostSettingViettelFrm`.**
  - Lưu bằng `DELETE` rồi `INSERT` thiết lập. Mật khẩu chỉ "mã hóa" bằng `EncodeString`, giải ngược được.
  - Gọi API nhà cung cấp (tải mẫu HĐĐT; Viettel Post `partner.viettelpost.vn`). Không kiểm quyền trong form.

## 5. Phát hiện chung, áp dụng khi port

1. **SQL nối chuỗi.** Hầu hết lệnh ghi ngoài `SaveData` nối chuỗi từ ô nhập, không giao dịch, bỏ qua lỗi. Web phải đi qua SP / hành động BE có tham số và giao dịch, không chép nguyên câu lệnh.
2. **DDL lúc mở form.** Nhiều form tự sửa schema khi mở (`ALTER TABLE`, `INSERT` mặc định, `CREATE INDEX`, SP cập nhật quyền). Web không làm vậy. Nếu cần thì đưa vào bước nâng cấp DB riêng, có duyệt.
3. **Quyền yếu.** `SetupFrm`, `PeriodSetupFrm`, `FiscalFrm`, `ChangePasswordFrm`, `IV_UnitCostProcessFrm`, `SY_MakeDocumentIDFrm`, `RestoreDataFrm` không gọi `GetPermission`. Có các lối tắt: tên đăng nhập bắt đầu bằng "ADMIN", phím Ctrl bỏ kiểm mật khẩu. Web đặt quyền rõ cho từng hành động, giữ quyền theo menu / phân quyền ERP, và thao tác nguy hiểm phải xác nhận lại.
4. **Mật khẩu.** WinForms băm MD5 và tải hash về máy người dùng (`SY_User` `*`). Web không bao giờ gửi cột mật khẩu (đã có `IsSecret`). Đổi / đặt lại mật khẩu là API riêng ở BE.
5. **Không có cấu hình thì đọc mềm như WinForms.** Tệp mô tả viết theo DB Medcom không khớp DB khác (bước 1 đã chặn bằng kiểm `sys.columns`). Khi port, ưu tiên đọc mềm theo cột có thật.
6. **Lỗi sẵn có ở bản gốc không sao chép.** Gồm: tính giá quý / năm luôn dùng LOT; `GetDocPreFix` sửa `SY_DocSetup`; chép quyền làm mất cờ; bỏ hủy hóa đơn không khôi phục chi tiết.

## 6. Cần chủ dự án duyệt

- Thứ tự đợt 1–3 ở §3.
- Danh sách "phải hỏi" và "không port" ở §3 mục 4–5.
- Có port phần **ghi** của chứng từ riêng TinPhong (`AR_InvoiceTinPhongMNFrm`, `IV_StockOutputVD*`) và phát hành điện tử hay không, hay chỉ xem.
