"""Transformaciones de pedidos para Databricks / Delta Lake.

Son funciones puras sobre DataFrames: no leen ni escriben archivos, por eso se prueban
con pytest y una SparkSession local. El notebook de Databricks solo orquesta (leer -> transformar -> MERGE).

Capas (arquitectura medallion):
  bronze  -> eventos crudos tal como llegan (JSON del outbox / Service Bus)
  silver  -> un renglón por pedido, limpio, validado y deduplicado (MERGE incremental)
  gold    -> agregados para reportes
"""
from __future__ import annotations

from pyspark.sql import Column, DataFrame, Window
from pyspark.sql import functions as F
from pyspark.sql.types import (
    DecimalType, StringType, StructField, StructType, TimestampType,
)

ESTADOS_VALIDOS = ("Creado", "Confirmado", "Cancelado")

# Esquema explícito (schema enforcement): no se infiere de los datos, así un campo con
# tipo incorrecto se detecta en vez de "contaminar" la tabla.
ESQUEMA_BRONZE = StructType([
    StructField("pedido_id", StringType(), nullable=False),
    StructField("cliente_id", StringType(), nullable=True),
    StructField("total", StringType(), nullable=True),          # llega como texto; se castea en silver
    StructField("estado", StringType(), nullable=True),
    StructField("actualizado_utc", TimestampType(), nullable=True),
])


def limpiar(df: DataFrame) -> DataFrame:
    """Normaliza tipos y textos. total inválido -> null (lo detecta la validación)."""
    return (
        df.withColumn("pedido_id", F.lower(F.trim("pedido_id")))
          .withColumn("cliente_id", F.trim("cliente_id"))
          .withColumn("estado", F.initcap(F.trim("estado")))
          .withColumn("total", F.col("total").try_cast(DecimalType(18, 2)) if hasattr(Column, "try_cast")
                      else F.expr("try_cast(total AS DECIMAL(18,2))"))
    )


def deduplicar_ultimo(df: DataFrame) -> DataFrame:
    """Entrega at-least-once => puede haber varios eventos del mismo pedido en el lote.
    Se queda con el más reciente por pedido (ventana ordenada por actualizado_utc)."""
    ventana = Window.partitionBy("pedido_id").orderBy(F.col("actualizado_utc").desc_nulls_last())
    return (df.withColumn("_rn", F.row_number().over(ventana))
              .filter(F.col("_rn") == 1)
              .drop("_rn"))


def motivo_rechazo() -> Column:
    """Reglas de calidad de datos. Devuelve el primer motivo que falla o null si el renglón es válido."""
    return (
        F.when(F.col("pedido_id").isNull() | (F.length("pedido_id") == 0), F.lit("pedido_id vacío"))
         .when(F.col("cliente_id").isNull() | (F.length("cliente_id") == 0), F.lit("cliente_id vacío"))
         .when(F.col("total").isNull(), F.lit("total no numérico"))
         .when(F.col("total") <= 0, F.lit("total debe ser > 0"))
         .when(~F.col("estado").isin(*ESTADOS_VALIDOS), F.lit("estado desconocido"))
         .when(F.col("actualizado_utc").isNull(), F.lit("sin fecha de actualización"))
    )


def separar_validos(df: DataFrame) -> tuple[DataFrame, DataFrame]:
    """Divide el lote en válidos (van a silver) y rechazados (cuarentena con su motivo)."""
    con_motivo = df.withColumn("motivo_rechazo", motivo_rechazo())
    validos = con_motivo.filter(F.col("motivo_rechazo").isNull()).drop("motivo_rechazo")
    rechazados = con_motivo.filter(F.col("motivo_rechazo").isNotNull())
    return validos, rechazados


def preparar_lote(df_bronze: DataFrame) -> tuple[DataFrame, DataFrame]:
    """Pipeline completo de un micro-lote: limpiar -> validar -> deduplicar."""
    validos, rechazados = separar_validos(limpiar(df_bronze))
    return deduplicar_ultimo(validos), rechazados


# Condición del MERGE: solo actualiza si el evento es más nuevo que lo guardado.
# Así un evento atrasado (llegó fuera de orden) no pisa un estado más reciente.
CONDICION_MERGE = "t.pedido_id = s.pedido_id"
CONDICION_ACTUALIZAR = "s.actualizado_utc > t.actualizado_utc"


def merge_en_silver(spark, nombre_tabla: str, lote: DataFrame) -> None:
    """Upsert idempotente en Delta Lake: reprocesar el mismo lote deja la tabla igual."""
    from delta.tables import DeltaTable  # import local: el módulo se puede probar sin delta instalado

    (DeltaTable.forName(spark, nombre_tabla).alias("t")
        .merge(lote.alias("s"), CONDICION_MERGE)
        .whenMatchedUpdateAll(condition=CONDICION_ACTUALIZAR)
        .whenNotMatchedInsertAll()
        .execute())


def resumen_gold(silver: DataFrame) -> DataFrame:
    """Agregado para reportes: pedidos y monto por cliente y estado."""
    return (silver.groupBy("cliente_id", "estado")
                  .agg(F.count("*").alias("pedidos"), F.sum("total").alias("monto"))
                  .orderBy("cliente_id", "estado"))
