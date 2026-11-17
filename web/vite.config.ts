import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

export default defineConfig({
  plugins: [react()],
  server: {
    // Explicit IPv4 loopback: Node may resolve "localhost" to ::1 only, and browsers that try
    // 127.0.0.1 then get "connection refused". Still local-only (not exposed on the network).
    host: '127.0.0.1',
    port: 5173,
    strictPort: true,
    proxy: {
      '/api': 'http://localhost:5080',
      '/dev': 'http://localhost:5080',
    },
  },
});
