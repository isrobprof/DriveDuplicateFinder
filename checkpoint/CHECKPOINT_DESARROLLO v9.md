# DriveDuplicateFinder — CHECKPOINT_DESARROLLO v9

> Estado reconstruido el 10/10/2026 a partir del checkpoint v8 disponible, el historial de esta conversación, la inspección del código actual, Git y la validación automatizada confirmada en la intervención inmediatamente anterior. No se han repetido compilaciones ni pruebas para crear este documento.

## Estado ejecutivo

- Proyecto C# WinForms sobre .NET 10 para localizar duplicados exactos de Google Drive y permitir una revisión explícita antes de cualquier limpieza.
- Persistencia local SQLite. El escaneo Full es recuperable mediante páginas y checkpoints transaccionales; los grupos y miembros se consultan paginados.
- Esquema SQLite versionado con migraciones 1, 2, 3 y 4 verificadas.
- La ruta paginada ahora permite marcar/desmarcar explícitamente archivos para conservar, confirmar la revisión de un grupo o saltarlo. Las decisiones por `FileId` y la confirmación se persisten en SQLite y se recuperan al reabrir el último inventario Full completado.
- Las acciones de recomendaciones heredadas, preflight y limpieza siguen bloqueadas en la ruta paginada. No se ejecuta limpieza desde páginas parciales.
- Estado Git observado: rama `master`, HEAD `0f3b8cd feat: añadir infraestructura SQLite para escaneo incremental`. Hay cambios tracked y untracked posteriores sin commit, incluidos `checkpoint/` y `docs/`; deben preservarse. No se hizo commit ni push.
- **No se ha validado manualmente la navegación/revisión de archivos reales ni la limpieza desde la ruta paginada. No autorizar operaciones reales de limpieza.**

## Arquitectura y reglas de seguridad

- UI: aplicación de escritorio WinForms C#/.NET 10 (`Form1.cs`).
- Almacenamiento: SQLite local, inicialización versionada y repositorios bajo `Data/Sqlite`.
- Google Drive: OAuth de solo lectura para análisis mediante `DriveService.Scope.DriveMetadataReadonly`; la autorización de limpieza es independiente y usa `DriveService.Scope.Drive` en su servicio específico.
- Duplicados exactos: mismo tamaño y mismo `md5Checksum`, con comparación case-insensitive Ordinal de .NET; archivos sin MD5 comparable no forman grupos. Las páginas de grupos/miembros están ligadas a identidad de cuenta, alcance e inventario (`AccountKey`, `ScopeKey`, `ScanId`). La identidad del grupo no depende de índice visual ni `GroupNumber`.
- Escaneo recuperable: cada página de `files.list` se persiste junto con contadores/checkpoint en una transacción; la reanudación usa el último pageToken confirmado. Inventarios incompletos no se publican como válidos; al completar se reconcilian registros dentro del alcance correspondiente.
- Las consultas paginadas mantienen limitada la materialización de grupos y miembros. Las páginas de la UI son aproximadamente un grupo y hasta 100 miembros; no se ha medido el pico de memoria en un Drive real de más de 300.000 elementos.
- JSON heredado de revisión: se conserva; no se importa ni atribuye a una cuenta/sesión cuando su procedencia no puede verificarse.
- Protección Drive: la operación de limpieza existente usa `Files.Update(... Trashed = true ...)`; está prohibida la eliminación permanente (`Files.Delete`, `EmptyTrash`). La ruta paginada aún no habilita limpieza.

## Funcionalidades: implementado, validado y pendiente

### IMPLEMENTADO en el código actual

- Escaneo Full recuperable persistido en SQLite y consultas paginadas de resúmenes y miembros.
- Detección de duplicados exactos por tamaño y MD5.
- UI simplificada inspirada en dedrive: flujo guiado, un grupo visible cada vez y miembros por páginas.
- Navegación anterior/siguiente entre grupos y páginas de miembros.
- Marcar/desmarcar archivos individuales como **Conservar** usando el `FileId` real.
- Confirmar revisión de grupo (requiere al menos una conservación explícita) o **Saltar grupo**. Saltar no confirma ni clasifica automáticamente miembros.
- Decisiones locales individuales y confirmación persistidas en SQLite, con identidad de inventario/grupo/archivo. Se registran y recuperan decisiones al reabrir el mismo Full completado vigente.
- Opción **Reabrir último análisis Full completado** sin iniciar otro escaneo.
- Acciones heredadas de recomendaciones y limpieza bloqueadas en la ruta paginada; el mensaje de confirmación explica que no se modifican miembros no visibles y no se crean candidatos automáticamente.
- Cuatro migraciones SQLite registradas; la migración 4 añade `IsReviewConfirmed` a `ReviewGroupStates`.

### VALIDADO (evidencia local confirmada, 10/10/2026)

En la validación previa a este checkpoint:

- `dotnet build` en PowerShell Windows: correcto, 0 errores y 0 advertencias. Un primer intento en el sandbox falló por `MSB4184`/acceso al registro; no se repitió en el mismo entorno. La compilación local elevada sí tuvo éxito.
- `dotnet .\bin\Debug\net10.0-windows\DriveDuplicateFinder.dll --verify-sqlite-infrastructure`: código de salida 0.
- Migraciones 1, 2, 3 y 4 aplicadas/verificadas.
- Comprobaciones de escaneo recuperable y paginación: persistencia de múltiples páginas, reanudación tras reinicio de conexión, cancelación y rollback, repetición idempotente, token inválido, aislamiento cuenta/alcance, rechazo de inventario incompleto, reconciliación y consultas paginadas: correctas.
- Comprobaciones de revisión persistente: decisión/estado, selección, reapertura de SQLite, aislamiento cuenta/alcance/sesión/grupo, rechazo de inventarios incompletos/obsoletos, integridad de membresía y conservación del JSON heredado: correctas.
- Comprobaciones de vistas/solicitudes paginadas: cancelación de solicitudes sustituidas, descarte de respuestas obsoletas, cancelación y bloqueo de acciones heredadas en vista paginada: correctas.
- `git diff --check`: correcto. Solo avisos informativos de normalización LF/CRLF.
- Esta evidencia es de pruebas locales deterministas; no demuestra una prueba manual de la interfaz con Drive real, medición de memoria real ni limpieza paginada.

### PENDIENTE

- Implementar la selección explícita de los archivos **candidatos a enviar a papelera** y una vista previa local del plan de limpieza, reutilizando los servicios existentes.
- Mantener esas capacidades en preparación únicamente: el usuario ha indicado que todavía no se ejecuten operaciones reales de limpieza.
- Validación manual de revisar varios grupos, conservar/desmarcar, saltar, cerrar/reabrir y comprobar el estado visible tras reanudación.
- Diseñar y verificar el preflight de una selección paginada completa antes de habilitar limpieza en esa ruta. Una página parcial nunca es suficiente.
- Pruebas controladas de memoria y rendimiento con inventarios grandes antes de considerar escaneos de más de 300.000 elementos.
- `changes.list`/escaneo incremental y heurísticas de posibles duplicados no están dentro del objetivo actual.

## Git y archivos pendientes

- Rama actual: `master`.
- Último commit: `0f3b8cd feat: añadir infraestructura SQLite para escaneo incremental`.
- El árbol no está limpio. Cambios tracked observados: `Data/Sqlite/Repositories/DriveFileCacheRepository.cs`, `Data/Sqlite/Repositories/ScanSessionRepository.cs`, `Data/Sqlite/SqliteDatabaseInitializer.cs`, `Data/Sqlite/SqliteInfrastructureLocalChecks.cs`, `Data/Sqlite/SqlitePersistenceFormat.cs`, `Form1.cs`, `Models/DriveScanResult.cs`, `Models/Persistence/PersistenceRecords.cs`, `Program.cs`, `Services/GoogleDriveFileService.cs` y `Utilities/FileSizeFormatter.cs`.
- Untracked observados antes de crear v9: migraciones SQLite 2–4, comprobaciones de escaneo/revisión/UI paginada, repositorio de revisión, contratos de páginas/revisión, servicios de escaneo recuperable, paginación y clasificación de pageToken, además de `checkpoint/` y `docs/`.
- No se han descartado, reescrito ni preparado cambios previos. v9 es un archivo nuevo; no se sobrescribieron checkpoints anteriores.
- No hay commits nuevos ni push. El estado debe volver a inspeccionarse antes de cada cambio futuro.

## Checkpoints disponibles y procedencia

- Se encontró `CHECKPOINT_DESARROLLO v8.md` (guardado con `%20` literal en su nombre), que es la fuente versionada más reciente disponible. También existen el checkpoint base y las versiones v2, v3, v4, v5 y v7. No se encontraron archivos v1 ni v6; parte del historial está incorporado dentro de versiones posteriores. v9 no existía antes de esta tarea.
- Para esta reconstrucción se contrastaron v8 con el historial de conversación, `git status`, rama/HEAD, `SqliteDatabaseInitializer`, `Migration004ExplicitReviewConfirmation`, `Form1`, `ReviewStateRepository`, los contratos de persistencia y la salida de verificación confirmada en la intervención anterior.
- No se leyeron credenciales, tokens ni datos privados de Google Drive.

## Próximo paso recomendado

Diseñar e implementar únicamente la selección local explícita de candidatos y la vista previa del plan, reutilizando las decisiones SQLite actuales. Mantener OAuth, preflight final y cualquier operación `Files.Update(Trashed=true)` fuera de alcance. Antes de modificar, inspeccionar Git y preservar los cambios ya pendientes; después validar con pruebas deterministas y revisión manual sin ejecutar limpieza real.
