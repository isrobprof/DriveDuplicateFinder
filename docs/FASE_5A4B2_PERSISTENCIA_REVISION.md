# Fase 5A.4-B2: persistencia de revisión

Esta fase añade una API SQLite aislada para guardar decisiones y selección local por inventario y grupo. No la conecta a `Form1`, al JSON actual, a recomendaciones ni a limpieza.

## Identidades y esquema

- Inventario: `(AccountKey, ScopeKey, ScanId)`, registrado solo si corresponde al escaneo `Full` completado más reciente.
- Grupo: inventario + `SizeBytes` + checksum normalizado con `ToUpperInvariant()`. La identidad no usa el número visible del grupo.
- `ReviewInventories` conserva los inventarios y marca los anteriores como obsoletos; no elimina decisiones históricas.
- `ReviewGroupStates` conserva estado, indicador revisado-sin-limpieza, notas, selección y cantidad de miembros.
- `ReviewFileDecisions` guarda una fila por `FileId` y estado `Undecided`, `Keep` o `CandidateForTrash`.

La migración 3 se ejecuta en la transacción del inicializador SQLite. Las claves primarias compuestas incluyen cuenta, alcance, sesión y grupo; las claves foráneas en cascada limitan decisiones a su fila de grupo e inventario. Los accesos del repositorio también verifican que el inventario siga vigente, que la sesión sea `Full/Completed` y que cada `FileId` pertenezca al grupo exacto.

Al comenzar otro escaneo Full del mismo ámbito, un trigger marca inmediatamente obsoletas las revisiones de sesiones anteriores, antes de que el escaneo pueda actualizar la caché; un segundo trigger en la finalización conserva esa protección para transiciones de estado. Si el nuevo escaneo no termina, las decisiones antiguas permanecen históricas y bloqueadas, sin reactivación automática. Las operaciones de escritura y lectura de decisiones rechazan inventarios obsoletos o grupos cuyo conjunto actual de miembros ya no coincida. La selección es local y no habilita por sí misma ninguna operación de Drive.

## Operaciones disponibles

`ReviewStateRepository` permite registrar inventarios/grupos, recuperar resúmenes de grupos por páginas, recuperar decisiones por páginas, recuperar páginas de grupos seleccionados, guardar decisiones individuales, actualizar notas, marcar revisado-sin-limpieza y seleccionar/deseleccionar grupos. Las escrituras se realizan en transacciones SQLite y reciben `CancellationToken`.

`GetDecisionsPageAsync` devuelve una página de decisiones, no un grupo completo apto para limpieza. Para futura integración, `Form1` debe usar la identidad estable del resumen; cargar todos los miembros solo cuando el flujo de revisión lo requiera; no convertir una página parcial en un grupo de limpieza; y mantener separados estado de inventario, decisiones y selección. `ReviewStateStorageService`, recomendaciones y `CleanupBatchService` aún no usan esta API y requerirán adaptación explícita en una fase posterior.

El estado de decisión conserva la semántica actual: una copia `Keep` no puede ser candidata; no se puede dejar el grupo sin copia conservada; elegir una candidata que complete N-1 candidatas exige guardar el `FileId` que la lógica de revisión eligió automáticamente para conservar; revisar sin limpieza limpia las candidatas pero mantiene la copia conservada; editar una decisión quita el indicador revisado-sin-limpieza. Las notas vacías se normalizan a `null` y las restantes se recortan igual que en el servicio actual.

## JSON heredado

El JSON antiguo no contiene una asociación verificable a cuenta, alcance y `ScanId`, y su campo `UserIdentifier` no se establece de manera fiable al guardar. No se importa automáticamente, no se sobrescribe ni se elimina. La transición queda pendiente hasta que exista procedencia inequívoca o una decisión explícita de migración; las pruebas solo crean un JSON centinela temporal y verifican que esta API no lo altera.

## Validación

Las comprobaciones offline se integran en `--verify-sqlite-infrastructure`: migración 3 e idempotencia, ciclo de decisiones, actualización repetida, selección/deselección, páginas, re-apertura de SQLite, aislamiento, rechazo de sesiones incompletas/obsoletas, pertenencia de `FileId` y conservación del JSON centinela. La verificación no llama a Google Drive. En esta intervención no se compila ni se ejecuta el verificador; debe validarse localmente en PowerShell.
