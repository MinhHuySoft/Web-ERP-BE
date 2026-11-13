-- Kho mẫu in web (M6 GĐ2, docs FE 06-mau-in.md §7): mẫu đã sửa bằng trình thiết kế web. Không ghi vào SY_RpFTbl — WinForms vẫn
-- dùng .rpx của nó; hai bên chạy song song. Chạy lại được. Không FK sang bảng dbo của ERP.
IF OBJECT_ID(N'web.PrintTemplate', N'U') IS NULL
CREATE TABLE web.PrintTemplate (
  TemplateID int IDENTITY(1,1) NOT NULL CONSTRAINT PK_web_PrintTemplate PRIMARY KEY,
  ReportName varchar(100) NOT NULL,
  TemplateKey varchar(120) NOT NULL,
  CurrentVersionID int NULL,
  CreatedAt datetime NOT NULL CONSTRAINT DF_web_PrintTemplate_CreatedAt DEFAULT (GETDATE()),
  CreatedBy nvarchar(100) NOT NULL,
  UpdatedAt datetime NULL,
  UpdatedBy nvarchar(100) NULL,
  CONSTRAINT UQ_web_PrintTemplate UNIQUE (ReportName, TemplateKey));
GO
-- Mỗi lần lưu là một phiên bản mới, không sửa / xóa (lịch sử, khôi phục). SourceKind: rpx = sửa từ mẫu WinForms, web = từ bản web trước.
IF OBJECT_ID(N'web.PrintTemplateVersion', N'U') IS NULL
CREATE TABLE web.PrintTemplateVersion (
  VersionID int IDENTITY(1,1) NOT NULL CONSTRAINT PK_web_PrintTemplateVersion PRIMARY KEY,
  TemplateID int NOT NULL CONSTRAINT FK_web_PrintTemplateVersion_Template REFERENCES web.PrintTemplate (TemplateID),
  VersionNo int NOT NULL,
  Json nvarchar(max) NOT NULL,
  JsonSha256 char(64) NOT NULL,
  Comment nvarchar(500) NULL,
  SourceKind varchar(10) NOT NULL,
  SourceFileDate datetime NULL,
  SourceSha256 char(64) NULL,
  CreatedAt datetime NOT NULL CONSTRAINT DF_web_PrintTemplateVersion_CreatedAt DEFAULT (GETDATE()),
  CreatedBy nvarchar(100) NOT NULL,
  CONSTRAINT UQ_web_PrintTemplateVersion UNIQUE (TemplateID, VersionNo));
GO
