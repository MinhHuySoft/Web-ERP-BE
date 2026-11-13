-- Schema web (QĐ-02): bảng riêng của bản web trong DB khách. Chạy lại được (kiểm tồn tại trước khi tạo).
-- Không FK, không trigger trỏ vào bảng dbo của ERP.
IF SCHEMA_ID(N'web') IS NULL EXEC(N'CREATE SCHEMA web AUTHORIZATION dbo');
GO
IF OBJECT_ID(N'web.MigrationHistory', N'U') IS NULL
CREATE TABLE web.MigrationHistory (
  Id int NOT NULL CONSTRAINT PK_web_MigrationHistory PRIMARY KEY,
  Name nvarchar(200) NOT NULL,
  Checksum char(64) NOT NULL,
  AppliedAt datetime NOT NULL CONSTRAINT DF_web_MigrationHistory_AppliedAt DEFAULT (GETDATE()),
  AppliedBy nvarchar(200) NULL);
GO
-- Nhật ký lệnh ghi: chống gửi trùng (__idem) và giữ kết quả để trả lại; ghi trong CÙNG giao dịch với dữ liệu ERP.
IF OBJECT_ID(N'web.CommandJournal', N'U') IS NULL
CREATE TABLE web.CommandJournal (
  JournalID bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_web_CommandJournal PRIMARY KEY,
  IdemKey uniqueidentifier NOT NULL,
  UserName varchar(100) NOT NULL,
  RuntimeName varchar(250) NOT NULL,
  Action varchar(40) NOT NULL,
  KeyBefore nvarchar(250) NULL,
  KeyAfter nvarchar(250) NULL,
  PayloadHash binary(32) NOT NULL,
  Status tinyint NOT NULL,
  HttpStatus smallint NULL,
  Location nvarchar(1000) NULL,
  MessagesJson nvarchar(max) NULL,
  PostCommitJson nvarchar(max) NULL,
  CreatedAt datetime NOT NULL CONSTRAINT DF_web_CommandJournal_CreatedAt DEFAULT (GETDATE()),
  CommittedAt datetime NULL,
  FinishedAt datetime NULL,
  CONSTRAINT UX_web_CommandJournal_Idem UNIQUE (UserName, IdemKey));
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_web_CommandJournal_CreatedAt' AND object_id = OBJECT_ID(N'web.CommandJournal'))
CREATE INDEX IX_web_CommandJournal_CreatedAt ON web.CommandJournal (CreatedAt);
GO
-- Bước sau commit chạy chậm / chạy lại (hook ESS2, nhật ký…).
IF OBJECT_ID(N'web.PostCommitTask', N'U') IS NULL
CREATE TABLE web.PostCommitTask (
  TaskID bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_web_PostCommitTask PRIMARY KEY,
  JournalID bigint NOT NULL,
  Kind varchar(20) NOT NULL,
  SqlText nvarchar(max) NOT NULL,
  NotBefore datetime NOT NULL,
  Attempts int NOT NULL CONSTRAINT DF_web_PostCommitTask_Attempts DEFAULT (0),
  DoneAt datetime NULL,
  LastError nvarchar(2000) NULL);
