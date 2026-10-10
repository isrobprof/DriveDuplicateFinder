# Fase 5A.4-B1: contrato de consultas SQLite paginadas

Esta fase agrega una API de solo lectura. La ruta recuperable actual y la interfaz todavía no la consumen.

## Identidades y páginas

- `ScanInventoryIdentity` identifica el inventario por `AccountKey`, `ScopeKey` y `ScanId`.
- `DuplicateGroupIdentity` añade `SizeBytes` y el checksum normalizado con `ToUpperInvariant()`. No contiene `GroupNumber`; su unicidad está limitada por la identidad del inventario.
- `DuplicateGroupSummary` solo expone identidad, cantidad de miembros, bytes recuperables y un nombre/ID representativo. No contiene miembros.
- `DuplicateGroupSummaryPage` informa elementos, total, offset, tamaño solicitado y si quedan resultados.
- `DuplicateGroupMembersPage` informa elementos, total, offset, tamaño y `IsPartial`/`IsCompleteGroup`. Es un tipo distinto de `DuplicateGroup`; una página parcial no puede pasarse directamente a los servicios actuales de revisión o limpieza.
- Los tamaños permitidos son 1–500; offset debe ser no negativo. Los parámetros `LIMIT` y `OFFSET` son SQL parametrizados.

## Lecturas SQL y comparación

`DriveFileCacheRepository.GetDuplicateGroupSummariesPageAsync` pagina los grupos en SQL y obtiene el total con una consulta agregada, sin crear una lista de todas las claves. `GetDuplicateGroupMembersPageAsync` carga una página del grupo solicitado, con el mismo filtro de inventario y orden estable por `FileId` binario.

Ambas lecturas exigen una sesión `Full` `Completed`, el `ScanId`, la cuenta y el alcance exactos, además de archivos no eliminados/no enviados a papelera. Se conservan los filtros de tamaño no negativo, MD5 no blanco y exclusión de tipos Workspace. La collation SQLite `DOTNET_ORDINAL_IGNORE_CASE` invoca `StringComparer.OrdinalIgnoreCase`; no se usa `NOCASE` para agrupar. Una función SQLite refleja `string.IsNullOrWhiteSpace` y otra genera una clave decimal de ancho fijo para ordenar los bytes recuperables sin pérdida por overflow de `Int64`. Los empates se resuelven por tamaño, checksum binario representativo e ID representativo.

No se añade migración: las consultas usan las tablas e índices existentes.

## Adaptación posterior requerida

- `Form1` deberá mostrar páginas de resúmenes y mantener selección por `DuplicateGroupIdentity`, nunca por ordinal visible. Deberá cancelar solicitudes obsoletas al cambiar de página/filtro.
- La vista de detalle deberá solicitar páginas de miembros. Mientras `IsCompleteGroup` sea falso, no debe construir ni habilitar revisión/recomendación/limpieza que presuponga el grupo completo.
- `ReviewStateStorageService` deberá leer/escribir decisiones por identidad de inventario y grupo sin deserializar el estado entero en RAM.
- Recomendaciones y selección por lotes deberán operar sobre identidades estables; para aplicar cambios locales se necesitará el estado completo del grupo o una operación transaccional equivalente.
- `CleanupBatchService` y el preflight deberán recuperar de nuevo todos los miembros del inventario confirmado, verificar que el recuento coincide y solo entonces preparar la limpieza. No deberán recibir una `DuplicateGroupMembersPage` como grupo completo.
- La ruta recuperable todavía genera todos los `DuplicateGroup` en memoria y el fallback anterior sigue pudiendo materializar el inventario completo. Esta API por sí sola no hace que el recorrido de la aplicación tenga memoria acotada ni completa la Fase 5A.4-B.
