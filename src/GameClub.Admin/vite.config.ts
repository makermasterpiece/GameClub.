import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

export default defineConfig({
  base: '/admin/',
  plugins: [react()],
  server: {
    port: 5173,
    strictPort: true,
    proxy: {
      '/api': { target: 'https://localhost:5001', secure: true },
      '/hubs': { target: 'https://localhost:5001', secure: true, ws: true },
    },
  },
});
