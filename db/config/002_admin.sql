-- 002: tài khoản quản trị (trang quản trị trên web, M5) và cài đặt chung có mã hóa.
CREATE TABLE admin_user (
  user_name TEXT NOT NULL PRIMARY KEY COLLATE NOCASE,
  password_hash TEXT NOT NULL,             -- PBKDF2-SHA256, không phải hash kiểu ERP
  totp_protected TEXT NOT NULL,            -- khóa TOTP đã mã hóa bằng Data Protection
  totp_last_step INTEGER NOT NULL DEFAULT 0,
  failed_count INTEGER NOT NULL DEFAULT 0,
  locked_until TEXT NULL,
  created_at TEXT NOT NULL,
  last_login_at TEXT NULL
);

-- Cài đặt chung. Giá trị luôn mã hóa (vd hằng mật khẩu kiểu cũ của ERP).
CREATE TABLE setting (
  name TEXT NOT NULL PRIMARY KEY,
  value_protected TEXT NULL,
  updated_at TEXT NOT NULL
);

-- Số phiên bản của từng công ty, tăng mỗi lần sửa thông tin / domain. Dùng làm __version khi sửa trên trang quản trị
-- (phát hiện hai người cùng sửa, kể cả khi hai lần lưu trùng thời điểm).
ALTER TABLE tenant ADD COLUMN revision INTEGER NOT NULL DEFAULT 0;
