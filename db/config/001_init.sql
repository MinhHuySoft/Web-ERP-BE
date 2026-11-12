-- 001: cấu hình công ty (QĐ-07). Chỉ thêm migration mới, không sửa tệp đã chạy.
CREATE TABLE tenant (
  tenant_key TEXT NOT NULL PRIMARY KEY COLLATE NOCASE,
  name TEXT NOT NULL,
  module_id TEXT NOT NULL DEFAULT 'SY',
  connection_protected TEXT NULL,          -- chuỗi kết nối đã mã hóa bằng Data Protection
  enabled INTEGER NOT NULL DEFAULT 1,
  maintenance_until TEXT NULL,             -- ISO 8601 UTC
  logout_before TEXT NULL,                 -- ISO 8601 UTC: cookie cấp trước mốc này bị từ chối
  tax_code TEXT NULL,
  database_label TEXT NULL,
  created_at TEXT NOT NULL,
  updated_at TEXT NOT NULL
);

-- Một domain có thể có nhiều công ty; một công ty có thể có nhiều domain.
CREATE TABLE tenant_host (
  host TEXT NOT NULL COLLATE NOCASE,
  tenant_key TEXT NOT NULL REFERENCES tenant(tenant_key) ON DELETE CASCADE,
  sort INTEGER NOT NULL DEFAULT 0,
  PRIMARY KEY (host, tenant_key)
);

-- Tăng mỗi lần sửa cấu hình. BE đang chạy so số này để nạp lại.
CREATE TABLE config_meta (
  id INTEGER NOT NULL PRIMARY KEY CHECK (id = 1),
  version INTEGER NOT NULL
);
INSERT INTO config_meta (id, version) VALUES (1, 1);

-- Nhật ký thao tác quản trị. Không ghi chuỗi kết nối hay mật khẩu.
CREATE TABLE admin_audit (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  at TEXT NOT NULL,
  actor TEXT NOT NULL,
  action TEXT NOT NULL,
  target TEXT NULL,
  detail TEXT NULL
);
