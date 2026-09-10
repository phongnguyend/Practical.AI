import { defineConfig, loadEnv } from 'vite'
import react from '@vitejs/plugin-react'

// The API runs on its own origin. In development requests are proxied instead of sent cross-origin, so
// the browser never needs the API's CORS policy; set VITE_API_PROXY_TARGET when the API is not on 5263.
export default defineConfig(({ mode }) => {
  const env = loadEnv(mode, '.', '')
  const target = env.VITE_API_PROXY_TARGET || 'http://localhost:5263'

  return {
    plugins: [react()],
    server: {
      port: 5173,
      strictPort: true,
      proxy: {
        '/api': { target, changeOrigin: true, secure: false },
      },
    },
  }
})
