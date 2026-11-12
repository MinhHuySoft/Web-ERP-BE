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
  isManager bit NULL, isAdmin bit NOT NULL DEFAULT 0);

CREATE TABLE dbo.SY_UserPermisstion (
  ID varchar(50) NOT NULL PRIMARY KEY DEFAULT CONVERT(varchar(50), NEWID()), UserName varchar(100) NOT NULL, MenuID varchar(50) NOT NULL,
  IsRun bit NOT NULL DEFAULT 0, IsAdd bit NOT NULL DEFAULT 0, IsUpdate bit NOT NULL DEFAULT 0, IsDelete bit NOT NULL DEFAULT 0,
  isManager bit NULL, isAdmin bit NOT NULL DEFAULT 0);
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
