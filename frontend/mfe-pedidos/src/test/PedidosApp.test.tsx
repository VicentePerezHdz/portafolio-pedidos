import { render, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { afterEach, describe, expect, it, vi } from 'vitest'
import PedidosApp from '../PedidosApp'

const pedido = { id: 'p1', clienteId: 'cli-1', total: 150, estado: 'Creado', creadoUtc: '2026-11-23T00:00:00Z', version: 1 }

const json = (status: number, cuerpo: unknown) =>
  Promise.resolve(new Response(JSON.stringify(cuerpo), { status, headers: { 'Content-Type': 'application/json' } }))

// Se monta igual que en el shell: dentro de una ruta "/pedidos/*".
function montar(ruta = '/pedidos', puedeEscribir = true) {
  return render(
    <MemoryRouter initialEntries={[ruta]} future={{ v7_startTransition: true, v7_relativeSplatPath: true }}>
      <Routes>
        <Route path="/pedidos/*" element={
          <PedidosApp apiUrl="http://api" obtenerToken={async () => 'tok-123'} puedeEscribir={puedeEscribir} />} />
      </Routes>
    </MemoryRouter>,
  )
}

afterEach(() => vi.restoreAllMocks())

describe('PedidosApp', () => {
  it('lista los pedidos enviando el token Bearer', async () => {
    const f = vi.spyOn(globalThis, 'fetch').mockImplementation(() => json(200, { elementos: [pedido], numeroPagina: 1, tamano: 10, total: 1 }))
    montar()
    expect(await screen.findByRole('cell', { name: 'cli-1' })).toBeInTheDocument()
    expect((f.mock.calls[0][1]!.headers as Record<string, string>).Authorization).toBe('Bearer tok-123')
  })

  it('cancelar manda If-Match con la versión y muestra el 409 si cambió', async () => {
    const f = vi.spyOn(globalThis, 'fetch')
      .mockImplementationOnce(() => json(200, { elementos: [pedido], numeroPagina: 1, tamano: 10, total: 1 }))
      .mockImplementationOnce(() => json(409, { title: 'Conflicto', detail: 'El pedido cambió (versión actual 2).' }))
    montar()
    const fila = (await screen.findByRole('cell', { name: 'cli-1' })).closest('tr')!
    await userEvent.click(within(fila).getByRole('button', { name: 'Cancelar' }))

    expect(await screen.findByRole('alert')).toHaveTextContent('El pedido cambió')
    expect((f.mock.calls[1][1]!.headers as Record<string, string>)['If-Match']).toBe('"1"')
  })

  it('un lector no ve acciones de escritura', async () => {
    vi.spyOn(globalThis, 'fetch').mockImplementation(() => json(200, { elementos: [pedido], numeroPagina: 1, tamano: 10, total: 1 }))
    montar('/pedidos', false)
    await screen.findByRole('cell', { name: 'cli-1' })
    expect(screen.queryByRole('link', { name: 'Nuevo pedido' })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Cancelar' })).not.toBeInTheDocument()
  })

  it('crear reutiliza la misma Idempotency-Key si se reintenta tras un error', async () => {
    const f = vi.spyOn(globalThis, 'fetch')
      .mockImplementationOnce(() => json(503, { title: 'No disponible' }))
      .mockImplementationOnce(() => json(201, pedido))
      .mockImplementation(() => json(200, { elementos: [pedido], numeroPagina: 1, tamano: 10, total: 1 }))
    montar('/pedidos/nuevo')

    await userEvent.type(screen.getByLabelText('Cliente'), 'cli-1')
    await userEvent.type(screen.getByLabelText('Total'), '150')
    await userEvent.click(screen.getByRole('button', { name: 'Guardar' }))
    expect(await screen.findByRole('alert')).toHaveTextContent('No disponible')
    await userEvent.click(screen.getByRole('button', { name: 'Guardar' }))

    const clave = (i: number) => (f.mock.calls[i][1]!.headers as Record<string, string>)['Idempotency-Key']
    expect(clave(0)).toBeTruthy()
    expect(clave(1)).toBe(clave(0))
    expect(await screen.findByRole('heading', { name: 'Pedidos' })).toBeInTheDocument() // volvió a la lista
  })

  it('traduce 401 a un mensaje claro', async () => {
    vi.spyOn(globalThis, 'fetch').mockImplementation(() => json(401, {}))
    montar()
    expect(await screen.findByRole('alert')).toHaveTextContent('Inicia sesión')
  })
})
