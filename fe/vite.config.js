import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

// Build ra fe/dist; server (be/Cadflow.Api) phục vụ thư mục đó. Khi chạy `npm run dev`, /api được chuyển tiếp sang server.
export default defineConfig({
  plugins: [react()],
  server: { port: 5173, proxy: { '/api': 'http://localhost:18080' } },
  build: { outDir: 'dist', emptyOutDir: true },
});
