import { Component, lazy, Suspense, useState, type ReactNode } from 'react'
import { NavLink, Route, Routes } from 'react-router-dom'
import { API_URL, useAuth, type Rol } from './auth'

// Carga diferida del remoto: el navegador descarga remoteEntry.js solo al entrar a /pedidos.
const PedidosApp = lazy(() => import('mfe_pedidos/PedidosApp'))

/** Si el remoto no está desplegado o falla, el shell sigue funcionando y muestra un aviso. */
class LimiteDeError extends Component<{ children: ReactNode }, { error: boolean }> {
  state = { error: false }
  static getDerivedStateFromError() { return { error: true } }
  render() {
    return this.state.error
      ? <p role="alert">El módulo de pedidos no está disponible en este momento.</p>
      : this.props.children
  }
}

function Login() {
  const { iniciarSesion } = useAuth()
  const [usuario, setUsuario] = useState('vicente')
  const [rol, setRol] = useState<Rol>('operador')
  const [error, setError] = useState<string | null>(null)
  return (
    <form className="login" onSubmit={(e) => { e.preventDefault(); iniciarSesion(usuario, rol).catch((x: Error) => setError(x.message)) }}>
      <label htmlFor="usuario">Usuario</label>
      <input id="usuario" value={usuario} onChange={(e) => setUsuario(e.target.value)} />
      <label htmlFor="rol">Rol</label>
      <select id="rol" value={rol} onChange={(e) => setRol(e.target.value as Rol)}>
        <option value="operador">operador (lee y escribe)</option>
        <option value="lector">lector (solo lee)</option>
      </select>
      <button type="submit">Entrar (token de desarrollo)</button>
      {error && <p role="alert">{error}</p>}
    </form>
  )
}

export default function App() {
  const { sesion, cerrarSesion, obtenerToken } = useAuth()
  return (
    <div className="shell">
      <header>
        <strong>Portafolio Pedidos</strong>
        <nav aria-label="Principal">
          <NavLink to="/" end>Inicio</NavLink>
          <NavLink to="/pedidos">Pedidos</NavLink>
        </nav>
        {sesion && <span className="sesion">{sesion.usuario} · {sesion.rol} <button type="button" onClick={cerrarSesion}>Salir</button></span>}
      </header>
      <main>
        {!sesion ? <Login /> : (
          <Routes>
            <Route path="/" element={<p>Shell (host) de micro frontends. El módulo <em>Pedidos</em> es un remoto desplegado por separado.</p>} />
            <Route path="/pedidos/*" element={
              <LimiteDeError>
                <Suspense fallback={<p>Cargando módulo de pedidos…</p>}>
                  <PedidosApp apiUrl={API_URL} obtenerToken={obtenerToken} puedeEscribir={sesion.rol === 'operador'} />
                </Suspense>
              </LimiteDeError>
            } />
          </Routes>
        )}
      </main>
    </div>
  )
}
