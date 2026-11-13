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
| **M1** | Vỏ chạy được: cấu hình SQLite + CLI quản trị, nhận diện công ty theo domain, đăng nhập tài khoản ERP, cookie, `session`, `menu`, `home`, màn chưa hỗ trợ, `/health`, contract test, CD qua runner trên VPS | Không | Xong 2026-11-12; chạy trên VPS từ 2026-11-12 (erp / quantri.finora.io.vn) |
| M2 | Màn danh sách chỉ đọc: biên dịch metadata `SY_FrmLstTbl` / `SY_FrmCfg` (GD2, HDE, LYT), lọc, lưới, tra cứu F4. Pilot `AP_PurposeRequestListFrm` (`CS_ReceiptFrm` là form viết tay, không có metadata) | Không | Phần chính xong 2026-11-12 (123/127 form metadata của Medcom); tên thay mã, F4, phân trang xong 2026-11-13; còn ô lọc kiểu khác |
| M3 | Xem chứng từ (`EDIT` `data-mode="view"`), tab chi tiết nạp lười, quyền form (`GetPermission`), ẩn cột tiền | Không | Phần chính xong 2026-11-12; 16 form chứng từ viết tay (2026-11-12) và 30 form viết tay còn lại (2026-11-13) xem được; còn in (M6) |
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

## M2 + M3 — chi tiết (2026-11-12)

**Đã làm:**

- [x] Đọc metadata form lúc chạy, cache 5 phút theo công ty (`Erp/Metadata`). Cột đã kiểm trên MedData.
  - Bảng đọc: `SY_FrmLstTbl`, `SY_FrmCfg`, `SY_FrmFltTbl`, `SY_FmtFldTbl`, `SY_FmatTbl`.
  - Slot `T0…TJ`: TN/TV/PK/SE/EX, join 1–4, SO/SDD/TRS, FTR/FTX/FTXS, MUL/MDL, GD2, HDE/HDE2/HDE3, ACP, SUM, DCP.
  - Form LIST không có T0 lấy bảng từ `SY_FrmLstTbl`.
- [x] Câu SELECT dựng như `TableInfoX` (tài liệu 10 §4.5), nhưng:
  - token `{User}`, `{BranchID}`, `{Module}_Setup`, cột `SY_User`… thành **tham số SQL**, không nối chuỗi (QĐ-03, ngoại lệ 2);
  - đoạn SQL của metadata có lệnh ghi, `;`, `exec`… bị từ chối, màn báo "cấu hình chưa hỗ trợ";
  - mọi câu chạy trong **giao dịch luôn rollback**;
  - không chạy hook (ESL, EBS…) và nút lệnh.
- [x] Lọc:
  - khoảng ngày theo `SP`/`SPA`/`DFP`;
  - ô lọc `SY_FrmFltTbl`: text, ngày, số, combo (nguồn SQL, tối đa 500 dòng), checkbox;
  - tìm nhanh trên khóa + cột GD2.
- [x] Lọc ngầm như WinForms: chứng từ của chính mình nếu không phải Manager/Admin (`SPA2`), chi nhánh của người dùng (`SPA3`), FTR/FTX, chỉ có quyền Thêm thì không thấy gì. Màn xem chứng từ áp cùng lọc nên không mở được chứng từ của người khác bằng URL.
- [x] Quyền form như `GetPermission` (nhóm ∪ riêng, dòng đầu theo MenuID). **Fail-closed:** không có dòng quyền thì chỉ xem, chỉ thấy chứng từ của mình. HDE2/HDE3/ACP (khi ẩn tiền) bị **loại khỏi dữ liệu**, không chỉ ẩn.
- [x] Lưới:
  - danh sách tối đa 1.000 dòng, kèm tổng số dòng và tổng cột (SUM) trên toàn bộ dữ liệu khớp lọc;
  - tiêu đề cột lấy theo F0 → `SY_FmtFldTbl` → tên cột;
  - kiểu và số lẻ lấy theo `SY_FmatTbl`.
- [x] Xem chứng từ (form EDIT):
  - ô theo `LYT` các tab 1/2/3/A/B/C (bỏ nút, ô ẩn, ô chỉ Manager/Admin);
  - tab chi tiết T1… nạp lười (`/tabs/T1`), theo TPV.
- [x] 49 form viết tay (tài liệu 30) và báo cáo: vẫn là màn "chưa có trên web".
- [x] Test:
  - unit: `SqlTemplate`, `MetadataCompiler`;
  - Host: route, khóa có `/`;
  - contract: 3 kiểu trang mới;
  - integration: LocalDB với metadata rút gọn từ MedData (lọc ngầm, ẩn tiền, form ghi DB bị chặn, bảng canary không đổi).
- [x] Chạy thử trên bản sao MedData (máy dev, chỉ đọc), cả 127 form metadata trong menu:
  - 123 mở được, 4 là form viết tay;
  - 28 chứng từ và 53 tab chi tiết mở được;
  - validator FE: 0 lỗi, 0 cảnh báo.
  
  Test `RealTenantSmokeTests` chỉ chạy khi đặt biến `WEBERP_REAL_*`.

**Còn lại:**

- ~~Tra cứu F4 và hiện **tên thay mã** (trạng thái, khách hàng…) theo `SY_FrmDrdwTbl`.~~ Xong 2026-11-13, xem dưới.
- ~~Phân trang trên server.~~ Xong 2026-11-13. Lưu bố cục lưới (`web.GridLayout`, M4).
- Ô lọc kiểu khác (kỳ 9, quý 14, UserFilterCombo 18…), `CostCenterID` của người dùng, ủy quyền `SY_UserAuthority` (MedData không có bảng).
- ~~Form viết tay (Phiếu thu/chi, Mua hàng…): viết riêng từng form.~~ Xong phần xem (2026-11-12) bằng tệp mô tả, xem dưới.

## Form viết tay — tệp mô tả (2026-11-12)

- `src/WebErp.Erp/Metadata/Overlays/<FormName>.json` mô tả lại cách form viết tay nạp dữ liệu bằng **đúng mô hình slot của
  `SY_FrmCfg`** (T0 bảng chính, T1… tab: bảng, khóa, cột nối MDL, join tên, ẩn, tổng, ACP, DCP). Engine metadata dựng màn như mọi
  form khác; không viết giao diện riêng.
- Key riêng của web:
  - `FTW`: điều kiện luôn áp, vd `isBank = 1` theo Para; không bị cấu hình DB đè;
  - `HHA` = 1: ẩn cả tab khi bị ẩn tiền (lưới Định khoản);
  - `OPT` = 1: bỏ tab nếu bảng không có trong DB của khách (lịch sử trạng thái…).
- `useDbConfig`: form viết tay có đọc `SY_FrmCfg` (T0/T1…) ⇒ cấu hình DB của khách đứng trước, tệp bù phần thiếu.
  3 form luôn viết cứng (`AP_PurchaseAllocateFrm`, `AR_StockInputFrm`, `AR_StockOutFrm`) chỉ dùng tệp.
- Đã có 16 form chứng từ:
  - quỹ: Phiếu thu/chi (+ ngân hàng);
  - mua bán: Mua hàng (+ nhập khẩu), Trả NCC, Mua/Bán dịch vụ, Hóa đơn bán (+ xuất khẩu), Hàng bán trả lại;
  - kế toán và kho: Phiếu kế toán, Phân rã, Nhập/Xuất/Chuyển kho, Phân bổ chi phí, Theo dõi nhập/xuất.
  
  Nguồn: mã dịch ngược (số dòng ghi ở đầu mỗi tệp) + cột thật MedData. Sửa trực tiếp tệp JSON khi cần.
- Chạy thử trên bản sao MedData: 19 biến thể theo Para đều mở được danh sách, chứng từ và tab; validator 0 lỗi.
- Chủ dự án chốt 2026-11-13: trang quản trị chỉ là cấu hình riêng của BE; form nào của ERP thì **giữ trên web ERP như WinForms**,
  không bỏ, không dời sang trang quản trị (khách đang dùng). Xong 2026-11-13: cả 30 form viết tay còn lại trong menu Medcom, xem dưới.

## 30 form viết tay hệ thống / kế toán / tài sản (2026-11-13)

- Tệp mô tả cho: người dùng, nhóm, phân quyền (+ Web), lịch tài chính, thiết lập thông số, ngôn ngữ, user log, sao lưu, phục hồi,
  đổi mật khẩu, khóa sổ, số dư đầu (hàng, công nợ, tài khoản), duyệt/khóa chứng từ, định nghĩa và phân bổ chi phí, thiết lập 3 báo
  cáo tài chính, lý lịch / thanh lý / phân bổ CCDC, danh mục / thanh lý / khấu hao tài sản, kiểm kê, tính giá xuất kho, kết nối
  Viettel Post. Nguồn: phân tích mã dịch ngược từng form (số dòng ghi ở đầu mỗi tệp) + cột thật MedData.
- Key web mới của tệp mô tả:
  - `formType: "DIALOG"`: hộp thoại thực hiện (sao lưu, phục hồi, đổi mật khẩu, tính giá): mô tả `WNOTE` (đoạn cách `|`), lưới T0 nếu có;
  - `WACT`: nút thực hiện riêng của form, hiện như ERP nhưng **khóa** (web chưa ghi, M4);
  - `IND` = 1: tab không nối chứng từ, hiện dưới lưới danh sách (vd mọi quý / mọi kỳ của lịch tài chính);
  - `GCA`: cột nhóm mặc định (`data-group-by`, key cũ `TableInfoX.GroupColumnArr`); `FRM`: FROM thay thế (bảng dẫn xuất gộp nhóm);
  - `flt`: ô lọc viết cứng trong form (cột như `SY_FrmFltTbl`), vd ô "Mẫu" của thiết lập báo cáo tài chính;
  - `F0…F9`, `C` trong tệp mô tả = tiêu đề cột / nhãn ô; `{Mod}` trong giá trị = mã module của công ty (thay bằng chữ đã kiểm).
- Bảo vệ dữ liệu: BE **không bao giờ** trả cột mật khẩu / token / khóa API / chuỗi kết nối / nội dung tệp đính kèm (`ErpScreens.IsSecret`),
  dù cấu hình nào. User log che `Note` của thao tác trên form người dùng (WinForms ghi cả hash mật khẩu vào đó). Thiết lập thông số
  che giá trị liên kết DB / máy chủ. Web không chạy các lệnh ghi mà WinForms chạy lúc MỞ form (ALTER, INSERT mặc định, sửa dữ liệu).
- Quyền mở màn như WinForms: `MainFrm.MenuClick` không kiểm `Permiss.View`, cửa chặn là menu của người dùng (Host chỉ mở màn có
  trong `{Module}_GetUserMenuFnc`; user `Admin` thấy mọi menu). Cờ quyền chỉ quyết định phạm vi dữ liệu.
- Lọc như form viết tay của WinForms: không lọc người tạo ở form số dư, phiếu do SP sinh (SPA2); lọc người tạo viết cứng ở tài sản,
  kiểm kê, định nghĩa phân bổ (FTW, kể cả khi DB có SPA2).
- Lọc chi nhánh (chủ dự án chốt 2026-11-13: **giống ERP**): chỉ form động tự lọc theo chi nhánh của người dùng
  (`FrmFormList`/`FrmFormListEdit`/`FrmMasterDetail` gọi `GetBranchFilter`, trừ SPA3 = 1). Form viết tay không lọc, SPA3 của DB không
  áp; 5 form tự lọc chi nhánh trong mã đặt key web `WBR` = 1 (theo dõi phiếu nhập / xuất kho; phân bổ chi phí, phân bổ CCDC, trích
  khấu hao có ô chi nhánh khóa theo người dùng).
- Sửa lỗi khi phiên có chi nhánh: ô lọc trên toolbar (`SY_FrmFltTbl`, mặc định `{BranchID}`) ghép cột trần, slot có join tới bảng
  cùng tên cột báo "Ambiguous column name" (hóa đơn bán, xuất kho, chuyển kho). Cột của bảng nguồn giờ ghi kèm tên bảng, kể cả trong
  mẫu `WhereString`. Chạy thử cả menu với phiên chi nhánh MB và không chi nhánh: 0 màn lỗi.
- Chạy thử trên bản sao MedData: 170 danh sách, 74 chứng từ, 156 tab chi tiết, 13 tab độc lập, 484 lần F4; validator 0 lỗi.
- Gần đúng so với WinForms (ghi ở đầu từng tệp): cây menu hiện dạng danh sách (phân quyền, ngôn ngữ), phân bổ / khấu hao hiện danh
  sách phiếu của kỳ thay cho ô chọn kỳ, duyệt chứng từ chưa có tab chi tiết theo phân hệ, tab "Daily log" của user log chưa làm.

## Tên thay mã, tra cứu F4, phân trang (2026-11-13)

- Đọc `SY_FrmDrdwTbl` của form (dòng của tên form chạy trước, bỏ `IsDisable`), cache cùng metadata form.
- Lưới: cột có danh sách chọn mà `DisplayColumn` khác `ValueColumn` hiện **tên**, giữ mã làm giá trị máy (`td[data-value]`), như
  GridEX hiện DisplayMember. Tên lưới khớp như WinForms: T0 `grdList`/`grdListDoc`, T1 `grdChitiet`, T2… `grdChitiet2`…;
  form viết tay đặt key web `GN` trong tệp mô tả. `Caption` đổi tiêu đề cột, `isInvisible` ẩn cột.
- Ô chứng từ (`GridName` rỗng, `ColumnID` = `Fld<Cột>` hoặc `<Cột>`): thành ô `lookup` (mã + `lookup-text` = tên thay mã, hoặc
  `DisplayMember2` của ComboX). `Caption` của dòng đè nhãn LYT.
- Tra cứu F4: `GET /erp/screens/{Form}/lookups/{Cột}?q=&p0=…` → `table[data-erp=lookup-result]`, tối đa 50 dòng + tổng, tìm LIKE
  trên các cột chữ của `ColumnArr` (như `SearchTopRecord`), sắp theo cột giá trị. `p0…` = tham số `{0}`… của `ParaArr` lấy từ chứng từ.
- Nguồn: chỉ câu `SELECT` (bỏ `ORDER BY` cuối), qua kiểm chỉ đọc, token và `{0}` thành tham số, trong giao dịch rollback. Thủ tục
  (`…Stp`), `WITH`, SQL lỗi: giữ nguyên mã, màn vẫn mở. Danh sách tĩnh `ValueList`/`ValueLstCb`/`LangList` tách theo `ColumnArr`.
  Tên tra theo lô (`IN`, 500 mã/câu). Tham số theo từng dòng lưới thì bỏ tên cột đó (giữ mã).
- Phân trang: danh sách 1.000 dòng/trang, `OFFSET … FETCH` với thứ tự SO + khóa chính (trang không trùng/sót dòng);
  `nav[data-erp=pager]` sau lưới, link giữ bộ lọc đang áp. Slot có `TRS` không phân trang (WinForms chỉ lấy top N).
- Chạy thử trên bản sao MedData: 145 màn, 59 chứng từ: 249 ô tra cứu (87 có tên kèm), 438 lần F4 đều trả đúng; validator 0 lỗi.
