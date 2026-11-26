/// <reference types="vitest/config" />
import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'
import federation from '@originjs/vite-plugin-federation'

// Micro frontend REMOTO: se construye y despliega solo, y expone PedidosApp en remoteEntry.js.
// El shell lo descarga en tiempo de ejecución, así que se puede publicar una versión nueva sin redesplegar el shell.
export default defineConfig({
  plugins: [
    react(),
    federation({
      name: 'mfe_pedidos',
      filename: 'remoteEntry.js',
      exposes: { './PedidosApp': './src/PedidosApp.tsx' },
      // Dependencias compartidas: una sola copia de React en la página (dos copias rompen los hooks).
      shared: ['react', 'react-dom', 'react-router-dom'],
    }),
  ],
  build: { target: 'esnext', modulePreload: false, cssCodeSplit: false },
  server: { port: 5002, strictPort: true },
  preview: { port: 5002, strictPort: true, cors: true },
  test: { environment: 'jsdom', globals: true, setupFiles: './src/test/setup.ts' },
})
