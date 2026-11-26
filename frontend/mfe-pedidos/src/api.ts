// Cliente HTTP de la API de pedidos. Recibe del shell la URL y una función para obtener el token:
// el micro frontend NO maneja el login, solo pide el token vigente en cada llamada.

export interface Pedido {
  id: string
  clienteId: string
  total: number
  estado: 'Creado' | 'Confirmado' | 'Cancelado'
  creadoUtc: string
  version: number
}

export interface Pagina<T> {
  elementos: T[]
  numeroPagina: number
  tamano: number
  total: number
}

interface ProblemDetails { title?: string; detail?: string; errors?: Record<string, string[]> }

export class ErrorApi extends Error {
  constructor(mensaje: string, readonly status: number) { super(mensaje) }
}

export type ObtenerToken = () => Promise<string | null>

export function crearClientePedidos(apiUrl: string, obtenerToken: ObtenerToken) {
  async function pedir<T>(ruta: string, init: RequestInit = {}): Promise<T> {
    const token = await obtenerToken()
    const resp = await fetch(`${apiUrl}${ruta}`, {
      ...init,
      headers: {
        'Content-Type': 'application/json',
        ...(token ? { Authorization: `Bearer ${token}` } : {}),
        ...init.headers,
      },
    })
    if (!resp.ok) {
      const p = (await resp.json().catch(() => ({}))) as ProblemDetails
      const mensajes: Record<number, string> = { 401: 'Inicia sesión para continuar.', 403: 'Tu rol no permite esta acción.' }
      const validacion = p.errors ? Object.values(p.errors).flat().join(' ') : ''
      throw new ErrorApi(mensajes[resp.status] ?? (validacion || p.detail || p.title || `Error ${resp.status}`), resp.status)
    }
    return (await resp.json()) as T
  }

  return {
    listar: (pagina = 1, signal?: AbortSignal) =>
      pedir<Pagina<Pedido>>(`/api/pedidos?pagina=${pagina}&tamano=10`, { signal }),

    /** La clave de idempotencia se genera una vez por envío del formulario: si la red falla y se reintenta, no se duplica. */
    crear: (clienteId: string, total: number, claveIdempotencia: string) =>
      pedir<Pedido>('/api/pedidos', {
        method: 'POST',
        body: JSON.stringify({ clienteId, total }),
        headers: { 'Idempotency-Key': claveIdempotencia },
      }),

    /** If-Match con la versión que se vio en pantalla: si otro usuario lo cambió, la API responde 409. */
    cancelar: (p: Pedido) =>
      pedir<Pedido>(`/api/pedidos/${p.id}/cancelar`, { method: 'POST', headers: { 'If-Match': `"${p.version}"` } }),
  }
}

export type ClientePedidos = ReturnType<typeof crearClientePedidos>
