"""Pruebas de las transformaciones con una SparkSession local (no necesitan Databricks).

Correr:  .venv\\Scripts\\python -m pytest -v   (requiere JAVA_HOME apuntando a un JDK 17)
"""
from datetime import datetime
from decimal import Decimal
from functools import reduce

import pytest

from pyspark.sql import DataFrame, SparkSession
from pyspark.sql import functions as F

from pedidos_lake.transformaciones import (
    ESQUEMA_BRONZE, deduplicar_ultimo, limpiar, preparar_lote, resumen_gold, separar_validos,
)


@pytest.fixture(scope="session")
def spark():
    s = (SparkSession.builder.master("local[1]").appName("pruebas-pedidos")
         .config("spark.sql.shuffle.partitions", "1")
         .config("spark.ui.enabled", "false")
         .config("spark.sql.session.timeZone", "UTC")
         .getOrCreate())
    yield s
    s.stop()


T1 = datetime(2026, 11, 23, 10, 0)
T2 = datetime(2026, 11, 23, 11, 0)


def lote(spark, filas):
    """Arma el DataFrame con literales dentro de la JVM.
    En Windows, spark.createDataFrame(lista) levanta un "Python worker" que se cae con Python 3.12;
    así se evita y las transformaciones (100 % nativas de Spark) se prueban igual."""
    renglones = [
        spark.range(1).select(*[F.lit(v).cast(c.dataType).alias(c.name) for v, c in zip(fila, ESQUEMA_BRONZE.fields)])
        for fila in filas
    ]
    return reduce(DataFrame.unionByName, renglones)


def test_limpiar_normaliza_textos_y_castea_total(spark):
    df = limpiar(lote(spark, [(" ABC ", " cli-1 ", "10.567", "confirmado", T1)]))
    r = df.collect()[0]
    assert r.pedido_id == "abc"
    assert r.cliente_id == "cli-1"
    assert r.estado == "Confirmado"
    assert r.total == Decimal("10.57")


def test_deduplicar_se_queda_con_el_evento_mas_reciente(spark):
    df = lote(spark, [
        ("p1", "cli-1", "10", "Creado", T1),
        ("p1", "cli-1", "10", "Confirmado", T2),   # duplicado más nuevo
        ("p2", "cli-2", "20", "Creado", T1),
    ])
    r = {x.pedido_id: x.estado for x in deduplicar_ultimo(df).collect()}
    assert r == {"p1": "Confirmado", "p2": "Creado"}


@pytest.mark.parametrize(("fila", "motivo"), [
    (("p1", "", "10", "Creado", T1), "cliente_id vacío"),
    (("p1", "cli", "abc", "Creado", T1), "total no numérico"),
    (("p1", "cli", "-5", "Creado", T1), "total debe ser > 0"),
    (("p1", "cli", "10", "Perdido", T1), "estado desconocido"),
    (("p1", "cli", "10", "Creado", None), "sin fecha de actualización"),
])
def test_reglas_de_calidad_mandan_a_cuarentena_con_motivo(spark, fila, motivo):
    validos, rechazados = separar_validos(limpiar(lote(spark, [fila])))
    assert validos.count() == 0
    assert rechazados.collect()[0].motivo_rechazo == motivo


def test_preparar_lote_completo(spark):
    df = lote(spark, [
        ("p1", "cli-1", "10", "Creado", T1),
        ("p1", "cli-1", "10", "Confirmado", T2),
        ("p2", "cli-1", "x", "Creado", T1),
        ("p3", "cli-2", "30", "Cancelado", T1),
    ])
    validos, rechazados = preparar_lote(df)
    assert sorted(x.pedido_id for x in validos.collect()) == ["p1", "p3"]
    assert rechazados.count() == 1


def test_resumen_gold(spark):
    validos, _ = preparar_lote(lote(spark, [
        ("p1", "cli-1", "10", "Confirmado", T1),
        ("p2", "cli-1", "15", "Confirmado", T1),
        ("p3", "cli-2", "30", "Creado", T1),
    ]))
    r = {(x.cliente_id, x.estado): (x.pedidos, x.monto) for x in resumen_gold(validos).collect()}
    assert r[("cli-1", "Confirmado")] == (2, Decimal("25.00"))
    assert r[("cli-2", "Creado")] == (1, Decimal("30.00"))
