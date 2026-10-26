import react from '@vitejs/plugin-react'
import { defineConfig } from 'vite'

// In development the site runs on :5173 (Vite, with hot reload) and the API on
// :5080 (ASP.NET). Proxying the API paths makes them the *same origin* from the
// browser's point of view, so the login cookie just works — no CORS needed.
// In production `npm run build` writes into the server's wwwroot/ and
// ASP.NET serves both, so it's one origin there too.
const api = 'http://localhost:5080'

export default defineConfig({
  plugins: [react()],
  server: {
    port: 5173,
    strictPort: true,
    proxy: {
      '/api': api,
      '/auth': api,
      '/media': api,
      '/signin-github': api,
    },
  },
  build: {
    outDir: '../Server/wwwroot',
    emptyOutDir: true,
  },
})
