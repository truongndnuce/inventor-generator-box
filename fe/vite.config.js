import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

// Builds into fe/dist; the server (be/Cadflow.Api) serves that folder. When running `npm run dev`, /api is forwarded to the server.
export default defineConfig({
  plugins: [react()],
  server: { port: 5173, proxy: { '/api': 'http://localhost:18080' } },
  build: { outDir: 'dist', emptyOutDir: true },
});
