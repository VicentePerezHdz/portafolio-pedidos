/* eslint-disable react-refresh/only-export-components */
import { createContext, useCallback, useContext, useMemo, useState, type ReactNode } from 'react'

// Autenticación centralizada en el shell. Los micro frontends solo reciben obtenerToken().
// Desarrollo: token de /dev/token de la API. Producción: MSAL (@azure/msal-react) con Entra ID
// (flujo Authorization Code + PKCE); obtenerToken llamaría acquireTokenSilent({ scopes: ['api://pedidos/.default'] }).

export type Rol = 'lector' | 'operador'

interface Sesion { usuario: string; rol: Rol; token: string }

interface AuthApi {
  sesion: Sesion | null
  iniciarSesion: (usuario: string, rol: Rol) => Promise<void>
  cerrarSesion: () => void
  obtenerToken: () => Promise<string | null>
}

const AuthContext = createContext<AuthApi | null>(null)
export const API_URL = import.meta.env.VITE_API_URL ?? 'http://localhost:5090'

export function AuthProvider({ children }: { children: ReactNode }) {
  // El token vive solo en memoria (no en localStorage) para reducir el riesgo ante XSS.
  const [sesion, setSesion] = useState<Sesion | null>(null)

  const iniciarSesion = useCallback(async (usuario: string, rol: Rol) => {
    const r = await fetch(`${API_URL}/dev/token`, {
      method: 'POST', headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ usuario, roles: [rol] }),
    })
    if (!r.ok) throw new Error('No se pudo iniciar sesión (¿la API está corriendo en Development?).')
    setSesion({ usuario, rol, token: (await r.json()).access_token })
  }, [])

  const cerrarSesion = useCallback(() => setSesion(null), [])
  // useCallback con dependencia en el token: el remoto recibe una función estable mientras no cambie la sesión.
  const obtenerToken = useCallback(async () => sesion?.token ?? null, [sesion?.token])

  const api = useMemo(() => ({ sesion, iniciarSesion, cerrarSesion, obtenerToken }),
    [sesion, iniciarSesion, cerrarSesion, obtenerToken])
  return <AuthContext.Provider value={api}>{children}</AuthContext.Provider>
}

export function useAuth(): AuthApi {
  const ctx = useContext(AuthContext)
  if (!ctx) throw new Error('useAuth requiere <AuthProvider>')
  return ctx
}
