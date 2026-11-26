import { defineConfig, loadEnv } from 'vite'
import react from '@vitejs/plugin-react'
import federation from '@originjs/vite-plugin-federation'

// SHELL (host): layout, login y rutas. Carga los micro frontends remotos en tiempo de ejecución.
// La URL del remoto viene de una variable de entorno: en cada ambiente (dev/QA/prod) apunta a su despliegue.
export default defineConfig(({ mode }) => {
  const env = loadEnv(mode, '.', '')
  return {
    plugins: [
      react(),
      federation({
        name: 'shell',
        remotes: {
          mfe_pedidos: env.VITE_MFE_PEDIDOS_URL ?? 'http://localhost:5002/assets/remoteEntry.js',
        },
        shared: ['react', 'react-dom', 'react-router-dom'],
      }),
    ],
    build: { target: 'esnext', modulePreload: false },
    server: { port: 5001, strictPort: true },
    preview: { port: 5001, strictPort: true },
  }
})
