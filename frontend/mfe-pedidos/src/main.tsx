import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import { BrowserRouter, Route, Routes } from 'react-router-dom'
import PedidosApp from './PedidosApp'

// Modo independiente (npm run dev en el puerto 5002): el equipo del micro frontend lo desarrolla y prueba
// sin levantar el shell. Pide un token de desarrollo con rol "operador" a la API local.
const apiUrl = import.meta.env.VITE_API_URL ?? 'http://localhost:5090'

async function tokenDesarrollo(): Promise<string | null> {
  const r = await fetch(`${apiUrl}/dev/token`, {
    method: 'POST', headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ usuario: 'dev-mfe', roles: ['operador'] }),
  })
  return r.ok ? (await r.json()).access_token : null
}

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <BrowserRouter future={{ v7_startTransition: true, v7_relativeSplatPath: true }}>
      <Routes>
        <Route path="/*" element={<PedidosApp apiUrl={apiUrl} obtenerToken={tokenDesarrollo} puedeEscribir />} />
      </Routes>
    </BrowserRouter>
  </StrictMode>,
)
