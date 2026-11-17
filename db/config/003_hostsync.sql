-- 003: đồng bộ domain công ty với IIS (agent WebErp.HostSync chạy quyền LocalSystem).
-- Trang quản trị chỉ ghi trạng thái MONG MUỐN (tenant_host). Agent đọc tenant_host, so với binding của site WebErp rồi thêm / gỡ.
-- Agent ghi các bảng dưới đây nhưng KHÔNG tăng config_meta.version (Host không cần nạp lại khi trạng thái IIS đổi).

-- Domain do agent quản lý. Agent CHỈ được gỡ domain có trong bảng này.
CREATE TABLE managed_host (
  host TEXT NOT NULL PRIMARY KEY COLLATE NOCASE,
  state TEXT NOT NULL,                     -- pending_dns / issuing / active / error / removing / removal_blocked / conflict
  adopted INTEGER NOT NULL DEFAULT 0,      -- 1 = cấu hình tay từ trước, agent nhận quản lý
  cert_thumbprint TEXT NULL,
  cert_expires TEXT NULL,                  -- ISO 8601 UTC
  last_error TEXT NULL,                    -- lý do ngắn, không chứa bí mật
  attempts INTEGER NOT NULL DEFAULT 0,     -- số lần thử liên tiếp chưa thành công (tính thời gian chờ)
  next_attempt_at TEXT NULL,               -- ISO 8601 UTC: chưa tới mốc này thì chưa thử lại
  remove_approved INTEGER NOT NULL DEFAULT 0, -- quản trị viên đã xác nhận gỡ (vượt ngưỡng gỡ hàng loạt)
  last_checked_at TEXT NULL,
  updated_at TEXT NOT NULL
);

-- Trạng thái chung của agent (một dòng).
CREATE TABLE hostsync_meta (
  id INTEGER NOT NULL PRIMARY KEY CHECK (id = 1),
  requested_at TEXT NULL,                  -- quản trị viên bấm "Đồng bộ lại ngay"
  heartbeat_at TEXT NULL,                  -- lần cuối agent chạy một vòng
  applied_version INTEGER NULL,            -- config_meta.version của vòng đồng bộ gần nhất
  agent_version TEXT NULL,
  staged_version TEXT NULL,                -- bản agent mới đang chờ cài (tools\hostsync-staging)
  last_error TEXT NULL
);
INSERT INTO hostsync_meta (id) VALUES (1);
