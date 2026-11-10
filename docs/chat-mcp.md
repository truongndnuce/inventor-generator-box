# Chat MCP: tạo job bằng mô tả

Hai cửa vào, cùng một bộ công cụ `CadTools` (`be/Cadflow.Api/Mcp/CadTools.cs`):

| Cửa vào | Dùng khi | Tạo job thế nào |
|---|---|---|
| Web `#/chat` -> `POST /api/chat` (SSE) | User mô tả trong trình duyệt | Mặc định (`Chat:AutoCreate=true`): LLM gọi `validate_job` rồi `create_job`, job vào hàng đợi ngay, giao diện hiện thẻ job. Với `Chat__AutoCreate=false`: LLM chỉ **đề xuất** (`propose_job`) và job chỉ vào hàng đợi khi user bấm **Create job** (`POST /api/chat/confirm/{draftId}`, bản nháp dùng một lần, hết hạn sau 30 phút) |
| MCP `/mcp` (HTTP) | Claude Desktop, MCP Inspector, client khác | Công cụ `create_job` bình thường; client tự xin phép user |

Công cụ: `list_products`, `get_product_schema`, `list_configs`, `validate_job`, `create_job`, `import_gea`, `get_script_form`, `get_job`, `list_jobs`, `cancel_job`. Đơn vị luôn là mm, độ, số nguyên.

## Cấu hình

Đặt trong `be/Cadflow.Api/.env` (git bỏ qua; mẫu ở `.env.example`; `KEY__SUB=value` ứng với `KEY:SUB`; biến môi trường thật được ưu tiên hơn `.env`). `dotnet user-secrets` và `ANTHROPIC_API_KEY` vẫn dùng được.

```
Anthropic__ApiKey=<key>
Anthropic__Model=claude-sonnet-5-5          # tùy chọn
# Nhà cung cấp tương thích Anthropic, ví dụ DeepSeek:
# Anthropic__BaseUrl=https://api.deepseek.com/anthropic
# Anthropic__Model=deepseek-chat
# Chat__AutoCreate=false                    # quay về chế độ chờ xác nhận
# Mcp__Key=<khóa>                           # khóa cho /mcp (header X-Api-Key)
```

- Chưa đặt `Anthropic:ApiKey`: `/api/chat` trả 503, phần còn lại vẫn chạy.
- Chưa đặt `Mcp:Key`: `/mcp` chỉ nhận kết nối từ chính máy chủ (loopback). Khi mở ra mạng phải đặt khóa.
- `/api/chat` và `/api/chat/confirm` giới hạn 20 yêu cầu/phút/IP. API người dùng chưa có xác thực, hãy thêm trước khi công khai.
- Ở chế độ tự tạo, LLM tạo job mà không hỏi lại; server vẫn kiểm tra luật một lần nữa khi tạo, nhưng mô tả mơ hồ có thể cho ra thông số không đúng ý. Nên xem lại thông số ở trang job.
- Job lưu trong PostgreSQL, xem `database.md`.

## Thử nhanh

```bash
npx @modelcontextprotocol/inspector   # URL: http://localhost:18080/mcp
```

Ví dụ: `validate_job` với `{"code":"gas-turbine","values":{"GT_CanCount":6}}` báo "Can count must be a multiple of 4".
