// Tipos de los módulos remotos (TypeScript no puede verlos porque llegan en tiempo de ejecución).
declare module 'mfe_pedidos/PedidosApp' {
  import type { ComponentType } from 'react'
  const PedidosApp: ComponentType<{
    apiUrl: string
    obtenerToken: () => Promise<string | null>
    puedeEscribir: boolean
  }>
  export default PedidosApp
}
