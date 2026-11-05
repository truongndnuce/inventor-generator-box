# inventor-generator-box

Web đặt hàng → hàng đợi → add-in trong Inventor dựng model → trả hồ sơ. Kế hoạch ở `docs/` (bước **W0 + một phần W2** của mục 15.7 trong `cadflow-order-to-model-plan.md`).

```
fe/  (web tĩnh)  ──►  be/Cadflow.Api (ASP.NET Core, hàng đợi job)  ◄──  InventorAddin (nhận job, dựng hộp, tải kết quả lên)
```

| Thư mục | Vai trò |
|---------|---------|
| `fe/` | Configurator (form sinh từ schema, kiểm tra luật qua API), danh sách đơn, trang job theo dõi thời gian thực. HTML/JS thuần, không cần build. |
| `be/Cadflow.Api/` | API: sản phẩm, đặt đơn, job, tải hồ sơ, API cho worker. Dùng chung file `BoxParams.cs` với add-in nên luật chỉ có một bản. Phục vụ luôn `fe/`. |
| `InventorAddin/` | Add-in Inventor 2027. Ngoài UI chỉnh tay, thư mục `Web/` là **web worker**: hỏi server nhận job, dựng trên UI thread của Inventor, xuất `.ipt`, `.stp`, `.png`. |

## Chạy

1. **Server + web** (cần .NET 10 SDK):
   ```
   dotnet run --project be/Cadflow.Api
   ```
   Mở http://localhost:18080. Dữ liệu job nằm ở `be/Cadflow.Api/data/` (đổi bằng cấu hình `DataDir`).
2. **Add-in**: build `InventorAddin/InventorAddin.sln` (tự ghi file `.addin` vào `%AppData%\Autodesk\Inventor 2027\Addins`), mở Inventor 2027. Khi nạp, add-in tự kết nối server. Tiêu đề cửa sổ Box Generator hiện `web: connected`.
3. Đặt đơn trên web: job chuyển `queued → running → succeeded`, trang job có ảnh, STEP, `.ipt`.

## Cấu hình

- **Add-in**: `%AppData%\InventorBoxAddin\worker.json` (tự tạo lần đầu): `enabled`, `serverUrl`, `workerKey`, `workerName`, `pollSeconds`. Log ở `%AppData%\InventorBoxAddin\logs`.
- **Server**: `be/Cadflow.Api/appsettings.json`: `Urls`, `Worker:Key`. **Đổi `Worker:Key` mặc định `dev-worker-key` trước khi mở ra ngoài localhost.**
- Muốn Inventor ở máy khác: đặt `Urls` thành `http://0.0.0.0:18080` và `serverUrl` của add-in trỏ tới máy chủ (nên đặt sau VPN/mạng riêng, xem mục 10 của kế hoạch).

## API worker (add-in gọi, header `X-Worker-Key`)

`POST /api/worker/heartbeat` · `POST /api/worker/claim` · `POST /api/worker/jobs/{id}/progress|artifacts|complete|fail?worker=...`

Job quá 90 giây không có nhịp tim thì tự trả về hàng đợi; tối đa 3 lần thử.
