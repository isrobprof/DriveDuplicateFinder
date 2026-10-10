# DriveDuplicateFinder — CHECKPOINT_DESARROLLO v10

> Hito local de selección explícita y previsualización paginada del plan, 10/10/2026. Contrastar este checkpoint con el código y Git antes de continuar. La validación automatizada pasó; todavía no se hizo prueba manual de esta UI con Drive real ni ejecución remota de limpieza.

## Objetivo del proyecto

Aplicación WinForms C#/.NET 10 que identifica duplicados exactos en Google Drive, permite revisarlos localmente y prepara de forma explícita una eventual limpieza segura. Esta etapa no modifica Google Drive.

## Arquitectura actual

- UI WinForms; almacenamiento local SQLite con migraciones versionadas 1–4.
- OAuth de lectura para análisis (`DriveMetadataReadonly`) y credencial/flujo de limpieza separado (`Drive`). No se cambiaron OAuth ni credenciales en este hito.
- Escaneo Full recuperable con páginas/checkpoints SQLite; identidad de inventario `(AccountKey, ScopeKey, ScanId)` y grupos por tamaño+checksum. Los duplicados exactos conservan semántica de tamaño y MD5 con comparación OrdinalIgnoreCase de .NET.
- Las consultas y UI de grupos, miembros, decisiones y plan son paginadas. El trabajo de plan se procesa desde SQLite sin reconstruir el conjunto completo de grupos, miembros ni decisiones en colecciones .NET.
- JSON de revisión antiguo preservado y no importado de forma ambigua.

## Funcionalidad de este hito

### IMPLEMENTADO

- La tabla paginada permite tres decisiones mutuamente excluyentes por `FileId`: `Undecided`, `Keep` y `CandidateForTrash`, mostradas como **SIN DECIDIR**, **CONSERVAR** y **ENVIAR A PAPELERA (candidato local)**.
- Cada cambio se guarda en `ReviewStateRepository` con identidad completa de cuenta/alcance/ScanId/grupo/FileId. Volver a seleccionar el mismo valor es idempotente y no invalida una confirmación existente.
- Confirmar revisión exige al menos un `Keep` y un `CandidateForTrash` explícitos en el grupo. Una modificación real invalida la confirmación hasta volver a confirmar. Los miembros sin decidir se excluyen del plan; nunca se convierten automáticamente en candidatos.
- El botón visible **Ver plan de limpieza** abre una vista local paginada: resumen de grupos aptos, candidatos, bytes potenciales, y páginas de candidatos y archivos conservados con nombre, ruta, tamaño y grupo.
- El plan se obtiene para todos los grupos confirmados/aprobados del inventario vigente, no solo el grupo abierto. SQLite verifica cuenta, alcance, ScanId, Full/Completed vigente, confirmación, membresía exacta y existencia de Keep y candidatos. Si un grupo confirmado tiene inconsistencias, bloquea el plan completo en lugar de presentarlo como parcial.
- El plan usa BigInteger para sumar bytes potenciales. La UI muestra advertencia explícita de que no se verificó el estado remoto actual en Drive.
- No fue necesaria una migración nueva: el esquema 3 ya almacenaba las tres decisiones y la migración 4 agregó `IsReviewConfirmed`.
- Limpieza real, preflight remoto, recomendaciones, exportación de la ruta heredada y controles de papelera siguen bloqueados desde la vista paginada. La nueva vista no incorpora dependencias ni botones de ejecución remota.

### VALIDADO

Validación realizada en PowerShell Windows local después de la última modificación:

- `dotnet build`: correcto, 0 advertencias y 0 errores.
- `dotnet .\bin\Debug\net10.0-windows\DriveDuplicateFinder.dll --verify-sqlite-infrastructure`: código de salida 0.
- Migraciones 1–4 verificadas; escaneo recuperable/paginación, revisión persistente, aislamiento por cuenta/alcance/sesión y bloqueo de acciones heredadas correctos.
- Nueva comprobación `ReviewStatePersistence.PagedCleanupPlanVerified = true`. En datos sintéticos incluye grupo de 205 miembros, Keep y candidato en páginas diferentes, pendientes excluidos, requisito de candidato al confirmar, persistencia/reapertura, idempotencia, invalidación tras editar, paginación del plan, conteos/tamaño y rechazo de cuenta/alcance/sesión incorrectos, inventarios incompletos y obsoletos.
- `git diff --check`: salida 0. Git mostró avisos de normalización LF/CRLF en archivos ya modificados.
- El primer build aislado dio `MSB4184` por acceso al registro Windows; no se repitió allí. La compilación local autorizada sí funcionó.
- No se ejecutó la aplicación normal, escaneo real, OAuth, llamada a Google Drive, preflight ni operación de papelera. No se midió memoria con una cuenta real.

### PENDIENTE / RIESGOS

- Prueba manual de la UI con inventario Full ya completado: editar decisiones, cambiar de página/grupo, reabrir inventario y navegar la vista previa.
- No se ha comprobado el estado remoto actual de cada candidato. El plan es local y puede quedar obsoleto desde que se captura el inventario.
- No activar ejecución de limpieza desde la vista paginada hasta diseñar una fase separada con revalidación remota/preflight y confirmación explícita sobre el plan completo.
- No ejecutar limpieza real todavía. La acción de escritura existente debe permanecer `Files.Update(... Trashed = true ...)`; `Files.Delete` y `EmptyTrash` siguen prohibidos.
- No hay medición de memoria/rendimiento con más de 300.000 elementos ni validación de cuota.

## Archivos afectados por este hito

- Modificados funcionalmente: `Form1.cs`, `Data/Sqlite/Repositories/ReviewStateRepository.cs`, `Data/Sqlite/Repositories/DriveFileCacheRepository.cs`, `Services/RecoverableFullScanService.cs` y `Data/Sqlite/ReviewStatePersistenceLocalChecks.cs`.
- Nuevos: `Models/Persistence/PagedCleanupPlanContracts.cs` y `PagedCleanupPlanPreviewForm.cs`.
- No se modificaron migraciones, OAuth, preflight, historial ni servicios de papelera.
- El repositorio ya tenía cambios pendientes antes de este hito; la lista anterior identifica archivos tocados para la funcionalidad actual, no el conjunto total de cambios de fases anteriores.

## Estado Git

- Rama `master`; HEAD estable conocido `0f3b8cd feat: añadir infraestructura SQLite para escaneo incremental`.
- Hay cambios tracked y untracked anteriores sin commit. Se conservaron; no se hizo reset, clean, stash, commit ni push.
- Se crearon checkpoints v9 y v10 sin sobrescribir versiones previas. Mantener también `AGENTS.md` y el resto de `checkpoint/`.

## Próximo paso recomendado

Validar manualmente la UI y la reanudación usando un inventario Full completado ya disponible, sin iniciar escaneo nuevo ni ejecutar limpieza. Después, si la vista previa es correcta, diseñar por separado la revalidación remota y el preflight para una futura ejecución; solicitar autorización antes de cualquier operación real de Drive.
