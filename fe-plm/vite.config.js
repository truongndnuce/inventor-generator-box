import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

// The PLM vault front-end. The API is the same server as fe/ (be/Cadflow.Api); the server serves this build at /plm/.
// `npm run dev` runs on port 5174 and forwards /api to the server.
export default defineConfig({
  base: '/plm/',
  plugins: [react()],
  server: { port: 5174, proxy: { '/api': 'http://localhost:18080' } },
  build: { outDir: 'dist', emptyOutDir: true },
});
