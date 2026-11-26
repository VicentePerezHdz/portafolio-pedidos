-- Databricks notebook source
-- Consultas de referencia sobre Delta Lake (Databricks SQL).

-- COMMAND ----------
-- MERGE en SQL (equivalente a merge_en_silver de Python). Solo actualiza si el evento es más nuevo.
MERGE INTO dev.pedidos.pedidos_silver AS t
USING nuevos_pedidos AS s
  ON t.pedido_id = s.pedido_id
WHEN MATCHED AND s.actualizado_utc > t.actualizado_utc THEN UPDATE SET *
WHEN NOT MATCHED THEN INSERT *;

-- COMMAND ----------
-- Time travel: la tabla como estaba en una versión anterior (auditoría o recuperación).
SELECT * FROM dev.pedidos.pedidos_silver VERSION AS OF 3;
RESTORE TABLE dev.pedidos.pedidos_silver TO VERSION AS OF 3;

-- COMMAND ----------
-- Change Data Feed: solo lo que cambió desde la versión 5 (procesamiento incremental hacia gold).
SELECT * FROM table_changes('dev.pedidos.pedidos_silver', 5);

-- COMMAND ----------
-- Evolución de esquema controlada: agregar una columna nueva sin reescribir la tabla.
ALTER TABLE dev.pedidos.pedidos_silver ADD COLUMN canal STRING COMMENT 'web | movil';

-- COMMAND ----------
-- Gold: agregado para reportes.
CREATE OR REPLACE TABLE dev.pedidos.pedidos_gold_por_cliente AS
SELECT cliente_id, estado, COUNT(*) AS pedidos, SUM(total) AS monto
FROM dev.pedidos.pedidos_silver
GROUP BY cliente_id, estado;

-- COMMAND ----------
-- Diagnóstico de rendimiento: tamaño y número de archivos; plan de una consulta lenta.
DESCRIBE DETAIL dev.pedidos.pedidos_silver;
EXPLAIN FORMATTED SELECT * FROM dev.pedidos.pedidos_silver WHERE cliente_id = 'cli-1';
