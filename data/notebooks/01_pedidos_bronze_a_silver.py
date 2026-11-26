# Databricks notebook source
# MAGIC %md
# MAGIC # Pedidos: bronze → silver (incremental con Auto Loader + MERGE)
# MAGIC
# MAGIC - **Entrada (bronze)**: archivos JSON de eventos de pedidos que deja el exportador del outbox en el lago.
# MAGIC - **Salida (silver)**: tabla Delta `pedidos_silver`, un renglón por pedido, validado y deduplicado.
# MAGIC - **Cuarentena**: tabla Delta `pedidos_rechazados` con el motivo de rechazo.
# MAGIC
# MAGIC Se ejecuta como **Job/Workflow** programado. Es incremental: Auto Loader guarda en el checkpoint
# MAGIC qué archivos ya procesó, y el MERGE es idempotente (reprocesar no duplica).

# COMMAND ----------

# Parámetros del Job (widgets). En un Workflow se pasan como "job parameters".
dbutils.widgets.text("catalogo", "dev")
dbutils.widgets.text("ruta_bronze", "/Volumes/dev/pedidos/bronze/eventos")
dbutils.widgets.text("ruta_checkpoint", "/Volumes/dev/pedidos/checkpoints/silver")

catalogo = dbutils.widgets.get("catalogo")
ruta_bronze = dbutils.widgets.get("ruta_bronze")
ruta_checkpoint = dbutils.widgets.get("ruta_checkpoint")
tabla_silver = f"{catalogo}.pedidos.pedidos_silver"
tabla_rechazados = f"{catalogo}.pedidos.pedidos_rechazados"

# COMMAND ----------

# MAGIC %md
# MAGIC ## 1. Tablas destino
# MAGIC Esquema explícito + `delta.enableChangeDataFeed` para que gold pueda leer solo los cambios.

# COMMAND ----------

spark.sql(f"""
CREATE TABLE IF NOT EXISTS {tabla_silver} (
  pedido_id        STRING NOT NULL,
  cliente_id       STRING NOT NULL,
  total            DECIMAL(18,2) NOT NULL,
  estado           STRING NOT NULL,
  actualizado_utc  TIMESTAMP NOT NULL,
  CONSTRAINT total_positivo CHECK (total > 0)
)
USING DELTA
CLUSTER BY (cliente_id)
TBLPROPERTIES ('delta.enableChangeDataFeed' = 'true')
""")

spark.sql(f"""
CREATE TABLE IF NOT EXISTS {tabla_rechazados} (
  pedido_id STRING, cliente_id STRING, total DECIMAL(18,2), estado STRING,
  actualizado_utc TIMESTAMP, motivo_rechazo STRING, cargado_utc TIMESTAMP
) USING DELTA
""")

# COMMAND ----------

# MAGIC %md
# MAGIC ## 2. Lectura incremental con Auto Loader
# MAGIC `cloudFiles.schemaEvolutionMode = rescue`: columnas nuevas no rompen el job; quedan en `_rescued_data` para revisarlas.

# COMMAND ----------

from pedidos_lake.transformaciones import ESQUEMA_BRONZE, merge_en_silver, preparar_lote
from pyspark.sql import functions as F

bronze = (spark.readStream.format("cloudFiles")
          .option("cloudFiles.format", "json")
          .option("cloudFiles.schemaLocation", f"{ruta_checkpoint}/esquema")
          .option("cloudFiles.schemaEvolutionMode", "rescue")
          .schema(ESQUEMA_BRONZE)
          .load(ruta_bronze))

# COMMAND ----------

# MAGIC %md
# MAGIC ## 3. foreachBatch: limpiar → validar → deduplicar → MERGE
# MAGIC Cada micro-lote es un DataFrame normal; ahí se puede usar MERGE (que no existe en streaming directo).

# COMMAND ----------

def procesar_lote(lote, lote_id: int) -> None:
    validos, rechazados = preparar_lote(lote)
    merge_en_silver(spark, tabla_silver, validos)
    (rechazados.withColumn("cargado_utc", F.current_timestamp())
               .write.mode("append").saveAsTable(tabla_rechazados))
    print(f"Lote {lote_id}: {validos.count()} válidos, {rechazados.count()} rechazados")

(bronze.writeStream
    .foreachBatch(procesar_lote)
    .option("checkpointLocation", ruta_checkpoint)
    .trigger(availableNow=True)      # procesa lo pendiente y termina (ideal para Jobs programados)
    .start()
    .awaitTermination())

# COMMAND ----------

# MAGIC %md
# MAGIC ## 4. Mantenimiento y verificación

# COMMAND ----------

spark.sql(f"OPTIMIZE {tabla_silver}")            # compacta archivos pequeños
display(spark.sql(f"DESCRIBE HISTORY {tabla_silver} LIMIT 5"))  # auditoría: cada MERGE queda versionado
display(spark.sql(f"SELECT estado, COUNT(*) AS pedidos FROM {tabla_silver} GROUP BY estado"))
