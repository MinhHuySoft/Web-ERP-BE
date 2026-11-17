-- 004: mô tả form viết tay sửa trên trang quản trị (cùng định dạng JSON với src/WebErp.Erp/Metadata/Overlays/*.json).
-- Có hiệu lực ngay khi lưu (Host đọc lại khi config_meta.version đổi), không cần build / deploy BE.
-- Thứ tự áp: mô tả riêng công ty → mô tả chung (tenant_key = '') → tệp nhúng trong BE.

CREATE TABLE form_overlay (
  form TEXT NOT NULL COLLATE NOCASE,        -- tên form (FormID / tên lớp WinForms), trùng khóa "form" trong JSON
  tenant_key TEXT NOT NULL DEFAULT '' COLLATE NOCASE, -- '' = mọi công ty
  json TEXT NOT NULL,
  enabled INTEGER NOT NULL DEFAULT 1,       -- 0 = tạm tắt (dùng lại tệp nhúng nếu có)
  note TEXT NULL,                           -- ghi chú của người sửa (không chứa bí mật)
  revision INTEGER NOT NULL DEFAULT 1,      -- tăng mỗi lần lưu; dùng làm __version và để phát hiện hai người cùng sửa
  updated_by TEXT NOT NULL,
  updated_at TEXT NOT NULL,
  PRIMARY KEY (form, tenant_key)
);

-- Lịch sử: mỗi lần lưu / tắt / xóa giữ lại bản JSON lúc đó để xem và khôi phục.
CREATE TABLE form_overlay_history (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  form TEXT NOT NULL COLLATE NOCASE,
  tenant_key TEXT NOT NULL DEFAULT '' COLLATE NOCASE,
  revision INTEGER NOT NULL,
  action TEXT NOT NULL,                     -- save / delete
  json TEXT NULL,
  enabled INTEGER NOT NULL DEFAULT 1,
  note TEXT NULL,
  actor TEXT NOT NULL,
  at TEXT NOT NULL
);
CREATE INDEX ix_form_overlay_history ON form_overlay_history (form, tenant_key, id);
