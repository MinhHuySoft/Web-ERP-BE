-- DB ERP GIẢ cho test và chạy thử trên máy dev. KHÔNG phải mã hay dữ liệu của ERP thật.
-- Chỉ gồm các bảng/cột BE đang đọc, lấy đúng tên và kiểu theo tài liệu phân tích hệ cũ (repo FE, docs/legacy-analysis/12 §4.2, §5.1).
-- Hàm SY_GetUserMenuFnc ở đây là bản VIẾT LẠI GIẢN LƯỢC theo mô tả ở §5.2 để có dữ liệu thử; BE chạy thật gọi hàm có sẵn trong DB khách.
SET NOCOUNT ON;

CREATE TABLE dbo.SY_UserGroup (
  UserGroupID varchar(20) NOT NULL PRIMARY KEY, UserGroupName nvarchar(100) NOT NULL, IsDisable bit NOT NULL DEFAULT 0, BranchID varchar(50) NULL);

CREATE TABLE dbo.SY_User (
  UserName varchar(100) NOT NULL PRIMARY KEY, HoTen nvarchar(150) NOT NULL, TenNgan nvarchar(100) NOT NULL DEFAULT N'',
  [Password] varchar(200) NULL, [Disable] bit NOT NULL DEFAULT 0, UserGroupID varchar(50) NULL, UserAuthority varchar(100) NULL,
  BranchID varchar(50) NULL);

CREATE TABLE dbo.SY_Menu (
  MenuID varchar(50) NOT NULL PRIMARY KEY, VN nvarchar(200) NULL, EN nvarchar(200) NULL, CH nvarchar(200) NULL, MenuType int NOT NULL DEFAULT 0,
  IconIndex int NULL, FormName varchar(100) NULL, Parent varchar(50) NULL, isBeginGroup bit NULL, isDisable bit NULL, isBold bit NULL,
  isNotCheckPermission bit NULL, Para varchar(50) NULL);

CREATE TABLE dbo.SY_UserGroupPermisstion (
  ID varchar(50) NOT NULL PRIMARY KEY DEFAULT CONVERT(varchar(50), NEWID()), UserGroupID varchar(20) NOT NULL, MenuID varchar(50) NOT NULL,
  IsRun bit NOT NULL DEFAULT 0, IsAdd bit NOT NULL DEFAULT 0, IsUpdate bit NOT NULL DEFAULT 0, IsDelete bit NOT NULL DEFAULT 0,
  isManager bit NULL, isAdmin bit NOT NULL DEFAULT 0, isHideAmount bit NULL, isExportExcel bit NULL);

CREATE TABLE dbo.SY_UserPermisstion (
  ID varchar(50) NOT NULL PRIMARY KEY DEFAULT CONVERT(varchar(50), NEWID()), UserName varchar(100) NOT NULL, MenuID varchar(50) NOT NULL,
  IsRun bit NOT NULL DEFAULT 0, IsAdd bit NOT NULL DEFAULT 0, IsUpdate bit NOT NULL DEFAULT 0, IsDelete bit NOT NULL DEFAULT 0,
  isManager bit NULL, isAdmin bit NOT NULL DEFAULT 0, isHideAmount bit NULL, isExportExcel bit NULL);
GO

-- Giản lược §5.2: Admin thấy mọi menu bật; người khác thấy mục IsRun=1 (nhóm, riêng, của người ủy quyền) + mục không cần quyền,
-- rồi thêm các menu cha.
CREATE FUNCTION dbo.SY_GetUserMenuFnc(@Username varchar(100))
RETURNS TABLE
AS RETURN
WITH Granted AS (
  SELECT M.MenuID FROM dbo.SY_Menu M WHERE ISNULL(M.isDisable,0)=0 AND @Username='Admin'
  UNION SELECT P.MenuID FROM dbo.SY_User U JOIN dbo.SY_UserGroupPermisstion P ON P.UserGroupID=U.UserGroupID
    WHERE U.UserName=@Username AND P.IsRun=1
  UNION SELECT P.MenuID FROM dbo.SY_UserPermisstion P WHERE P.UserName=@Username AND P.IsRun=1
  UNION SELECT P.MenuID FROM dbo.SY_User A JOIN dbo.SY_UserGroupPermisstion P ON P.UserGroupID=A.UserGroupID
    WHERE A.UserAuthority=@Username AND P.IsRun=1
  UNION SELECT M.MenuID FROM dbo.SY_Menu M WHERE ISNULL(M.isNotCheckPermission,0)=1
), Tree AS (
  SELECT M.MenuID,M.Parent,0 AS Lvl FROM dbo.SY_Menu M JOIN Granted G ON G.MenuID=M.MenuID WHERE ISNULL(M.isDisable,0)=0
  UNION ALL
  SELECT P.MenuID,P.Parent,T.Lvl+1 FROM Tree T JOIN dbo.SY_Menu P ON P.MenuID=T.Parent WHERE T.Lvl<4 AND ISNULL(P.isDisable,0)=0
)
SELECT M.* FROM dbo.SY_Menu M WHERE M.MenuID IN (SELECT MenuID FROM Tree);
GO

INSERT dbo.SY_UserGroup (UserGroupID,UserGroupName) VALUES ('KT',N'Kế toán'),('KHO',N'Kho');

INSERT dbo.SY_Menu (MenuID,VN,EN,MenuType,FormName,Parent,isBeginGroup,isDisable,isBold,isNotCheckPermission,Para) VALUES
 ('01',N'Hệ thống','System',1,NULL,NULL,0,0,0,0,NULL),
 ('0101',N'Danh sách người dùng','Users',0,'NguoiDungFrm','01',0,0,0,0,NULL),
 ('0199',N'Đăng xuất','Log off',0,'LogOff','01',1,0,0,1,NULL),
 ('09',N'Tiền mặt, ngân hàng','Cash',1,NULL,NULL,0,0,0,0,NULL),
 ('0901',N'Phiếu thu','Receipt',0,'CS_ReceiptFrm','09',0,0,0,0,NULL),
 ('0902',N'Phiếu thu ngân hàng','Bank receipt',0,'CS_ReceiptFrm','09',0,0,0,0,'Bank'),
 ('0930',N'Sổ quỹ tiền mặt','Cash book',0,'CS_CashBookReport','09',1,0,0,0,NULL),
 ('0990',N'Chức năng đã tắt','Disabled',0,'CS_OldFrm','09',0,1,0,0,NULL),
 ('16',N'Tổng hợp','Ledger',1,NULL,NULL,0,0,0,0,NULL),
 ('1601',N'Sổ cái','General ledger',0,'GJ_GeneralLedgerReport','16',0,0,0,0,NULL);

INSERT dbo.SY_UserGroupPermisstion (UserGroupID,MenuID,IsRun) VALUES ('KT','0901',1),('KT','0902',1),('KT','0930',1),('KT','1601',0),('KHO','1601',1);
INSERT dbo.SY_UserPermisstion (UserName,MenuID,IsRun) VALUES ('ketoan','0101',1);

-- ---------------------------------------------------------------------------------------------------------------------
-- Metadata form (M2/M3): cột lấy đúng tên/kiểu như MedData (đã kiểm 2026-11-12), chỉ phần BE đọc. Cấu hình form
-- "Đề nghị mua hàng" rút gọn từ cấu hình thật; dữ liệu nghiệp vụ là dữ liệu GIẢ.
CREATE TABLE dbo.SY_FrmLstTbl (
  FormID varchar(100) NOT NULL PRIMARY KEY, FormType varchar(50) NULL, CaptionVN nvarchar(100) NULL, CaptionEN nvarchar(100) NULL,
  TableName varchar(100) NULL, PrimaryKey varchar(100) NULL, HideColumnArr varchar(max) NULL, SummaryColumnArr varchar(200) NULL,
  FilterEx nvarchar(50) NULL, TableDetailLeftJoinField varchar(100) NULL, TableDetailSelectFrom varchar(500) NULL);

CREATE TABLE dbo.SY_FrmCfg (
  UserAutoID varchar(50) NOT NULL PRIMARY KEY, FID varchar(250) NULL, KeyID varchar(50) NULL, SubID varchar(50) NULL,
  KeyValue nvarchar(max) NULL, SubValue nvarchar(max) NULL, VDate datetime NULL, PFID varchar(250) NULL);

CREATE TABLE dbo.SY_FrmFltTbl (
  UserAutoID varchar(40) NOT NULL PRIMARY KEY, FormID varchar(250) NULL, KeyID varchar(50) NULL, ColumnID varchar(50) NULL,
  Caption nvarchar(150) NULL, Type int NULL, Source nvarchar(max) NULL, ValueColumn varchar(50) NULL, DisplayColumn varchar(50) NULL,
  Operator int NULL, WhereString nvarchar(250) NULL, DefaultValue nvarchar(100) NULL, IsDisable bit NULL);

CREATE TABLE dbo.SY_FmatTbl (
  FormatID varchar(2) NOT NULL PRIMARY KEY, FormatName nvarchar(50) NULL, NumberDecimal int NULL, FormatString nvarchar(100) NULL, Type char(1) NULL);

CREATE TABLE dbo.SY_FmtFldTbl (
  AutoID numeric(18,0) IDENTITY PRIMARY KEY, FormatID varchar(2) NULL, FieldName varchar(50) NULL, FormName varchar(100) NULL,
  CaptionVN nvarchar(200) NULL);

CREATE TABLE dbo.CF_BranchTbl (BranchID varchar(20) NOT NULL PRIMARY KEY, BranchName nvarchar(100) NULL);
CREATE TABLE dbo.CF_ItemTbl (ItemID varchar(30) NOT NULL PRIMARY KEY, ItemName nvarchar(200) NULL, Unit nvarchar(20) NULL);
CREATE TABLE dbo.AP_PurchaseRequestTbl (
  PurchaseRequestID varchar(30) NOT NULL PRIMARY KEY, PurchaseDate datetime NULL, StatusID int NULL, BranchID varchar(20) NULL,
  Notes nvarchar(500) NULL, Price decimal(18,2) NULL, RateExchange decimal(18,4) NULL, isLock bit NULL, UserCreate varchar(100) NULL);
CREATE TABLE dbo.AP_PurchaseRequestDetailTbl (
  UserAutoID varchar(50) NOT NULL PRIMARY KEY, PurchaseRequestID varchar(30) NULL, ItemID varchar(30) NULL, Quantity decimal(18,2) NULL,
  UnitPrice decimal(18,2) NULL);
-- Kiểm lớp bảo vệ chỉ đọc: nếu một đoạn SQL metadata ghi được thì số dòng ở đây đổi.
CREATE TABLE dbo.WEB_Canary (ID int NOT NULL PRIMARY KEY);
GO

INSERT dbo.SY_Menu (MenuID,VN,EN,MenuType,FormName,Parent,isBeginGroup,isDisable,isBold,isNotCheckPermission,Para) VALUES
 ('05',N'Mua hàng','Purchase',1,NULL,NULL,0,0,0,0,NULL),
 ('05011',N'Đề nghị mua hàng','Purchase request',0,'AP_PurposeRequestListFrm','05',0,0,0,0,NULL),
 ('05090',N'Chi nhánh','Branches',0,'CF_BranchListFrm','05',0,0,0,0,NULL),
 ('05099',N'Form cấu hình xấu','Bad form',0,'BAD_ConfigFrm','05',0,0,0,0,NULL);
INSERT dbo.SY_UserGroupPermisstion (UserGroupID,MenuID,IsRun,IsAdd) VALUES ('KT','05011',1,1),('KT','05090',1,0),('KT','05099',1,0);
INSERT dbo.SY_UserPermisstion (UserName,MenuID,IsRun,isAdmin) VALUES ('Admin','05011',1,1);
INSERT dbo.SY_UserPermisstion (UserName,MenuID,IsRun,isHideAmount) VALUES ('ketoan','05011',1,1);

INSERT dbo.SY_FrmLstTbl (FormID,FormType,CaptionVN,TableName,PrimaryKey) VALUES
 ('AP_PurposeRequestListFrm','EDIT',N'Đề nghị mua hàng','AP_PurchaseRequestTbl','PurchaseRequestID'),
 ('CF_BranchListFrm','LIST',N'Chi nhánh','CF_BranchTbl','BranchID'),
 ('BAD_ConfigFrm','LIST',N'Form cấu hình xấu','AP_PurchaseRequestTbl','PurchaseRequestID');

INSERT dbo.SY_FrmCfg (UserAutoID,FID,KeyID,SubID,KeyValue,SubValue) VALUES
 ('c01','AP_PurposeRequestListFrm','SP','','13',NULL), ('c02','AP_PurposeRequestListFrm','SPA','','30',NULL),
 ('c03','AP_PurposeRequestListFrm','DFP','','PurchaseDate',NULL),
 ('c10','AP_PurposeRequestListFrm','T0','TN','AP_PurchaseRequestTbl',NULL), ('c11','AP_PurposeRequestListFrm','T0','PK','PurchaseRequestID',NULL),
 ('c12','AP_PurposeRequestListFrm','T0','GD2','PurchaseRequestID;PurchaseDate;StatusID',NULL),
 ('c13','AP_PurposeRequestListFrm','T0','HDE','RateExchange',NULL), ('c14','AP_PurposeRequestListFrm','T0','SO','PurchaseRequestID Desc',NULL),
 ('c15','AP_PurposeRequestListFrm','T0','SUM','Price',NULL), ('c16','AP_PurposeRequestListFrm','T0','ACP','Price',NULL),
 ('c20','AP_PurposeRequestListFrm','T1','TN','AP_PurchaseRequestDetailTbl',NULL), ('c21','AP_PurposeRequestListFrm','T1','PK','UserAutoID',NULL),
 ('c22','AP_PurposeRequestListFrm','T1','TN1','CF_ItemTbl',NULL), ('c23','AP_PurposeRequestListFrm','T1','FKA1','ItemID',NULL),
 ('c24','AP_PurposeRequestListFrm','T1','FKB1','ItemID',NULL), ('c25','AP_PurposeRequestListFrm','T1','SE1','A.ItemName, A.Unit',NULL),
 ('c26','AP_PurposeRequestListFrm','T1','GD2','ItemID;ItemName;Unit;Quantity',NULL), ('c27','AP_PurposeRequestListFrm','T1','SUM','Quantity',NULL),
 ('c28','AP_PurposeRequestListFrm','T1','DCP',N'Hàng hóa đề nghị',NULL),
 ('c30','AP_PurposeRequestListFrm','F0','PurchaseDate',N'Ngày đề nghị','CV'),
 ('c40','AP_PurposeRequestListFrm','LYT1','PurchaseRequestID','1;4;1;1;;;;;;;;;;;;;;;;0;0','001'),
 ('c41','AP_PurposeRequestListFrm','LYT1','PurchaseDate','4;4;1;;;;;;;;;;;;;;;;;0;0','002'),
 ('c42','AP_PurposeRequestListFrm','LYT1','CommandButtonCtl','22;4;1;;;;;;;;;;;;;;;;;0;0','003'),
 ('c43','AP_PurposeRequestListFrm','LYT1','Notes','1;99;2;1;;;;;;;;;;;;;;;;0;0','004'),
 ('c44','AP_PurposeRequestListFrm','LYT1','Price','5;8;1;1;;;;;;;;;;;;;;;;0;0','005'),
 ('c45','AP_PurposeRequestListFrm','LYT1','RateExchange','5;8;1;;;;;;;;;;;;;;;;1;0;0','006'),
 -- Hook và nút lệnh có ghi DB: web chỉ đọc nên KHÔNG được chạy (test kiểm WEB_Canary không đổi).
 ('c50','AP_PurposeRequestListFrm','ESL','','DELETE dbo.WEB_Canary',NULL),
 ('c90','BAD_ConfigFrm','T0','TN','AP_PurchaseRequestTbl',NULL), ('c91','BAD_ConfigFrm','T0','PK','PurchaseRequestID',NULL),
 ('c92','BAD_ConfigFrm','T0','FTX','1=1; DELETE dbo.WEB_Canary',NULL);

INSERT dbo.SY_FrmFltTbl (UserAutoID,FormID,KeyID,ColumnID,Type,Source,DefaultValue,IsDisable) VALUES
 ('f01','AP_PurposeRequestListFrm','001','BranchID',3,'Select * from CF_BranchTbl','{BranchID}',0);

INSERT dbo.SY_FmatTbl (FormatID,FormatName,NumberDecimal,FormatString,Type) VALUES ('B','BaseAmount',0,'#,##0','N'),('Q2','Quantity2',2,'#,##0.##','N');
INSERT dbo.SY_FmtFldTbl (FormatID,FieldName,FormName,CaptionVN) VALUES
 (NULL,'PurchaseRequestID',NULL,N'Mã đề xuất'),('B','Price',NULL,N'Giá'),('Q2','Quantity',NULL,N'Số lượng'),(NULL,'BranchID',NULL,N'Chi nhánh'),
 (NULL,'ItemName',NULL,N'Tên hàng');

INSERT dbo.CF_BranchTbl VALUES ('HN',N'Hà Nội'),('HCM',N'Hồ Chí Minh');
INSERT dbo.CF_ItemTbl VALUES ('VT01',N'Vật tư một',N'Cái'),('VT02',N'Vật tư hai',N'Hộp');
INSERT dbo.AP_PurchaseRequestTbl (PurchaseRequestID,PurchaseDate,StatusID,BranchID,Notes,Price,RateExchange,isLock,UserCreate) VALUES
 ('PO/001',DATEADD(day,-3,CAST(GETDATE() AS date)),1,'HN',N'Đề nghị một',1000,1,0,'ketoan'),
 ('PO/002',DATEADD(day,-5,CAST(GETDATE() AS date)),2,'HCM',N'Đề nghị hai',2000,1,0,'ketoan'),
 ('PO/003',DATEADD(day,-60,CAST(GETDATE() AS date)),3,'HN',N'Đề nghị cũ',3000,1,1,'ketoan'),
 ('PO/004',DATEADD(day,-1,CAST(GETDATE() AS date)),1,'HN',N'Của người khác',4000,1,0,'khac');
INSERT dbo.AP_PurchaseRequestDetailTbl VALUES ('d1','PO/001','VT01',2,100),('d2','PO/001','VT02',3.5,200),('d3','PO/004','VT01',1,100);
INSERT dbo.WEB_Canary VALUES (1);
