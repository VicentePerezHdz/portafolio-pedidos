import { useCallback, useEffect, useMemo, useState, type FormEvent } from 'react'
import { Link, Route, Routes, useNavigate } from 'react-router-dom'
import { crearClientePedidos, type ClientePedidos, type ObtenerToken, type Pedido } from './api'
import estilos from './PedidosApp.module.css'

/** Contrato entre el shell y este micro frontend: lo único que se comparten son estas props. */
export interface PedidosAppProps {
  apiUrl: string
  obtenerToken: ObtenerToken
  puedeEscribir: boolean
}

/**
 * Componente expuesto por Module Federation. Sus rutas son RELATIVAS: el shell lo monta en /pedidos/*
 * y aquí se definen "" (lista) y "nuevo"; el remoto no sabe en qué URL vive.
 */
export default function PedidosApp({ apiUrl, obtenerToken, puedeEscribir }: PedidosAppProps) {
  const cliente = useMemo(() => crearClientePedidos(apiUrl, obtenerToken), [apiUrl, obtenerToken])
  return (
    <div className={estilos.raiz}>
      <Routes>
        <Route index element={<Lista cliente={cliente} puedeEscribir={puedeEscribir} />} />
        <Route path="nuevo" element={<Nuevo cliente={cliente} />} />
      </Routes>
    </div>
  )
}

function Lista({ cliente, puedeEscribir }: { cliente: ClientePedidos; puedeEscribir: boolean }) {
  const [pedidos, setPedidos] = useState<Pedido[]>([])
  const [error, setError] = useState<string | null>(null)
  const [cargando, setCargando] = useState(true)

  const cargar = useCallback((signal?: AbortSignal) => {
    setCargando(true)
    return cliente.listar(1, signal)
      .then((p) => { setPedidos(p.elementos); setError(null) })
      .catch((e: Error) => { if (e.name !== 'AbortError') setError(e.message) })
      .finally(() => setCargando(false))
  }, [cliente])

  useEffect(() => {
    const control = new AbortController()
    cargar(control.signal)
    return () => control.abort()
  }, [cargar])

  async function cancelar(p: Pedido) {
    try {
      await cliente.cancelar(p)
      await cargar()
    } catch (e) {
      setError((e as Error).message)
    }
  }

  return (
    <section aria-labelledby="titulo-pedidos">
      <div className={estilos.encabezado}>
        <h2 id="titulo-pedidos" className={estilos.titulo}>Pedidos</h2>
        <button type="button" className={estilos.secundario} onClick={() => cargar()}>Actualizar</button>
        {puedeEscribir && <Link className={estilos.boton} to="nuevo">Nuevo pedido</Link>}
      </div>
      {error && <p role="alert" className={estilos.error}>{error}</p>}
      {cargando ? <p>Cargando…</p> : pedidos.length === 0 ? <p>No hay pedidos.</p> : (
        <table className={estilos.tabla}>
          <caption className={estilos.oculto}>Pedidos más recientes</caption>
          <thead><tr><th scope="col">Cliente</th><th scope="col">Total</th><th scope="col">Estado</th><th scope="col">Acciones</th></tr></thead>
          <tbody>
            {pedidos.map((p) => (
              <tr key={p.id}>
                <td>{p.clienteId}</td>
                <td>{p.total.toLocaleString('es-MX', { style: 'currency', currency: 'MXN' })}</td>
                <td><span className={estilos[`estado${p.estado}`]}>{p.estado}</span></td>
                <td>
                  {puedeEscribir && p.estado === 'Creado' &&
                    <button type="button" className={estilos.secundario} onClick={() => cancelar(p)}>Cancelar</button>}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </section>
  )
}

function Nuevo({ cliente }: { cliente: ClientePedidos }) {
  const [form, setForm] = useState({ clienteId: '', total: '' })
  const [error, setError] = useState<string | null>(null)
  const [enviando, setEnviando] = useState(false)
  // Una clave por intento de alta; se conserva si el usuario reintenta tras un error de red.
  const [clave] = useState(() => crypto.randomUUID())
  const navegar = useNavigate()

  async function enviar(e: FormEvent) {
    e.preventDefault()
    setEnviando(true)
    setError(null)
    try {
      await cliente.crear(form.clienteId, Number(form.total), clave)
      navegar('..') // vuelve a la lista (ruta relativa)
    } catch (err) {
      setError((err as Error).message)
    } finally {
      setEnviando(false)
    }
  }

  return (
    <section aria-labelledby="titulo-nuevo">
      <h2 id="titulo-nuevo" className={estilos.titulo}>Nuevo pedido</h2>
      <form onSubmit={enviar} className={estilos.formulario}>
        <label htmlFor="clienteId">Cliente</label>
        <input id="clienteId" required maxLength={64} value={form.clienteId}
          onChange={(e) => setForm({ ...form, clienteId: e.target.value })} />
        <label htmlFor="total">Total</label>
        <input id="total" type="number" min="0.01" step="0.01" required value={form.total}
          onChange={(e) => setForm({ ...form, total: e.target.value })} />
        <div>
          <button type="submit" className={estilos.boton} disabled={enviando}>{enviando ? 'Guardando…' : 'Guardar'}</button>
          <Link to=".." className={estilos.secundario}>Volver</Link>
        </div>
      </form>
      {error && <p role="alert" className={estilos.error}>{error}</p>}
    </section>
  )
}
