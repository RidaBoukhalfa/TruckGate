import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

// The build goes straight into the server's static folder; the server serves it on the same origin.
export default defineConfig({
  plugins: [react()],
  build: {
    outDir: '../server/src/TruckGate.Api/wwwroot',
    emptyOutDir: true,
  },
  server: {
    proxy: { '/api': 'http://127.0.0.1:5080' },
  },
})
