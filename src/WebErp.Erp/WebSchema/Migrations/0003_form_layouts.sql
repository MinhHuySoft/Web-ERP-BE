-- Trình vẽ layout form trên web (M4, tài liệu FE 07-ve-layout-form.md): lưu THẲNG vào dbo.SY_FrmCfg như LayoutSettingFrm của WinForms
-- (WinForms và web cùng thấy bố cục mới). Bảng này giữ ảnh các dòng bố cục của form TRƯỚC và SAU mỗi lần lưu để xem lịch sử và
-- khôi phục. Mỗi lần lưu một dòng, không sửa / xóa. Chạy lại được. Không FK sang bảng dbo của ERP.
IF OBJECT_ID(N'web.FormLayoutVersion', N'U') IS NULL
CREATE TABLE web.FormLayoutVersion (
  VersionID int IDENTITY(1,1) NOT NULL CONSTRAINT PK_web_FormLayoutVersion PRIMARY KEY,
  FID varchar(250) NOT NULL,
  VersionNo int NOT NULL,
  -- Dòng SY_FrmCfg của bố cục (LYT*, LYS*, T?V), mảng JSON [{UserAutoID,KeyID,SubID,KeyValue,SubValue,VDate,PFID}].
  BeforeJson nvarchar(max) NOT NULL,
  BeforeSha256 char(64) NOT NULL,
  AfterJson nvarchar(max) NOT NULL,
  AfterSha256 char(64) NOT NULL,
  -- save = lưu từ trình vẽ; restore = khôi phục về ảnh "trước" của một phiên bản (RestoredFrom).
  Kind varchar(10) NOT NULL,
  RestoredFrom int NULL,
  Comment nvarchar(500) NULL,
  CreatedAt datetime NOT NULL CONSTRAINT DF_web_FormLayoutVersion_CreatedAt DEFAULT (GETDATE()),
  CreatedBy nvarchar(100) NOT NULL,
  CONSTRAINT UQ_web_FormLayoutVersion UNIQUE (FID, VersionNo));
GO
