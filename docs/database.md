# Cơ sở dữ liệu (PostgreSQL)

Job, file kết quả (danh sách) và nhật ký sự kiện nằm trong PostgreSQL. `JobStore` (`be/Cadflow.Api/JobStore.cs`) là lớp duy nhất đụng tới DB, dùng Npgsql với SQL thuần.

## Lưu ở đâu

| Dữ liệu | Nơi lưu |
|---|---|
| Job, danh sách artifact, sự kiện | **PostgreSQL** (`jobs`, `job_artifacts`, `job_events`) |
| Trạng thái worker (heartbeat, Busy) | Bộ nhớ của backend, tự làm mới theo nhịp tim, **không vào DB** |
| File kết quả | R2 hoặc `data/artifacts/<jobId>/` |
| Script `.gea` import, cấu hình sản phẩm | File trong `data/scripts`, `data/config` |
| Bản nháp chat (`DraftStore`) | Bộ nhớ, hết hạn sau 30 phút |

## Lược đồ

Bảng tự tạo bằng `CREATE TABLE IF NOT EXISTS` khi backend khởi động (có khóa `pg_advisory_lock`, hai backend khởi động cùng lúc không đua nhau).

- **`jobs`**: `id` (PK, `AJ-yyyyMMdd-HHmmss-xxxx`), `product_code`, `product_version`, `status` (`queued|running|succeeded|failed|cancelled`), `step`, `error`, `requested_by`, `created_at`, `started_at`, `finished_at`, `last_heartbeat`, `worker`, `attempts`, `inputs`/`text_inputs`/`summary` (jsonb), `script_name`, `script_sha256`, `script_source`, `artifacts_purged_at`. Chỉ mục: `(status, created_at)` cho hàng đợi, `requested_by`, `product_code`.
- **`job_artifacts`**: PK `(job_id, name)`, `kind`, `size`, `r2_key` (null = file nằm trên đĩa server). Xóa theo job (`ON DELETE CASCADE`).
- **`job_events`**: `id` (bigserial), `job_id`, `time`, `message`. Chỉ thêm dòng. Chỉ mục `(job_id, time)`.

Thời gian là `timestamptz` (UTC). `inputs`, `text_inputs`, `summary` luôn đọc/ghi cả khối nên để jsonb, không tách bảng.

## Hàng đợi

`Claim` là một câu `UPDATE ... WHERE id = (SELECT ... FOR UPDATE SKIP LOCKED LIMIT 1) RETURNING ...`, nên nhiều worker hoặc nhiều backend dùng chung DB vẫn không nhận trùng job. Các thao tác của worker (`progress`, `complete`, `fail`, artifact) khóa dòng job và chỉ chấp nhận worker đang giữ job. `ReapStale` (mỗi 10 giây) đưa job Running quá 90 giây không heartbeat về hàng đợi, hoặc đánh `failed` khi hết `MaxAttempts` (3).

## Cấu hình

`ConnectionStrings:Cadflow` trong `be/Cadflow.Api/appsettings.json`, mặc định `Host=localhost;Database=cadflow;Username=admin` (không mật khẩu, hợp với Postgres local). Khi cần mật khẩu hoặc server khác, đặt trong `.env` (git bỏ qua): `ConnectionStrings__Cadflow=Host=...;Database=cadflow;Username=...;Password=...`.

```bash
brew services start postgresql@15   # macOS
createdb cadflow
```

Thiếu chuỗi kết nối hoặc không nối được DB thì backend không khởi động.

## Chuyển từ jobs.json

Lần đầu chạy, nếu `data/jobs.json` còn, backend nhập toàn bộ job vào DB (bỏ qua id đã có) rồi đổi tên thành `jobs.json.migrated`. Không cần làm gì thêm.

## Test

`be/Cadflow.Api.Tests` (xUnit) chạy trên PostgreSQL thật. Mỗi test tự tạo một DB `cadflow_test_xxxx` và xóa khi xong.

```bash
dotnet test be/Cadflow.Api.Tests
# server khác: CADFLOW_TEST_PG="Host=...;Username=...;Password=..." dotnet test ...
```

Phủ: `JobStore` (lưu bền, `Claim` song song không trùng, quyền của worker, retry, hủy, reap, heartbeat, artifact và retention, nhập `jobs.json`) và `CadTools` (validate, đề xuất/xác nhận một lần, tạo job, get/list/cancel).
