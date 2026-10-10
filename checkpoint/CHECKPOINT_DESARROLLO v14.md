# DriveDuplicateFinder — CHECKPOINT_DESARROLLO v14

> Hito: integración del adaptador SQLite con el preflight de solo lectura para un grupo y validación offline en Windows. Fecha: 10/10/2026. La compilación y el verificador se ejecutaron desde `VALIDAR DriveDuplicateFinder.cmd` por el usuario; Codex leyó el log y no repitió las pruebas.

## Objetivo del proyecto

Aplicación WinForms C#/.NET 10 para identificar duplicados exactos de Google Drive, permitir revisión local persistente y preparar un flujo seguro previo a una eventual limpieza. La ruta paginada sigue sin ejecutar escrituras sobre Drive.

## Arquitectura actual

- WinForms .NET 10 y SQLite local, con migraciones versionadas 1–4.
- OAuth de análisis con `DriveService.Scope.DriveMetadataReadonly`; la autorización de limpieza existente es independiente.
- Escaneo Full recuperable con páginas/checkpoints SQLite e identidad de inventario `(AccountKey, ScopeKey, ScanId)`.
- Duplicados exactos por tamaño y MD5, con consultas paginadas de grupos y miembros.
- Decisiones locales persistidas por inventario, grupo estable y `FileId`.
- Vista paginada para revisar grupos y visualizar el plan local. `--demo-review` usa datos sintéticos en SQLite temporal y no debe acceder a Google Drive.
- El JSON de revisión heredado se conserva y no se atribuye a una cuenta o sesión sin procedencia verificable.

## IMPLEMENTADO

- `PersistedCleanupGroupReviewAdapter` reconstruye el contrato de revisión existente para un único grupo confirmado del inventario vigente. Verifica cuenta, alcance, ScanId, grupo, membresía, decisiones y confirmación; limita la adaptación a 2.000 miembros y rechaza grupos mayores antes de leerlos.
- `CreateReadOnlyPreflightReview()` crea la proyección mínima para preflight: un archivo Keep como ancla y exclusivamente candidatos explícitos. No incluye miembros `Undecided` ni Keeps adicionales como candidatos.
- `PagedGroupPreflightService` valida identidad con el servicio de escaneo antes y después de la lectura remota, ejecuta únicamente `GoogleDriveTrashService.PreflightAsync`, vuelve a comprobar el snapshot local y marca el preflight como no aprobado si cambian decisiones, confirmación o inventario.
- `Form1` añade «Comprobar en Google Drive» para un grupo confirmado en modo normal. Usa el servicio de análisis ya autenticado en solo lectura; no se ejecuta automáticamente. El modo demo bloquea la acción.
- `PagedGroupPreflightResultForm` muestra el resultado de solo lectura, los archivos Keep y candidatos, los bloqueos y la advertencia de que no se ejecutó limpieza.
- `CleanupPreflightFailureKind` permite clasificar cancelación, autenticación, permisos, indisponibilidad de archivos/red y fallos remotos. Las operaciones de escritura existentes no se modificaron.
- `GoogleDriveTrashPreflightLocalChecks` usa respuestas HTTP sintéticas y SQLite temporal, y está conectado a `--verify-sqlite-infrastructure`.

## VALIDADO

### Evidencia del lanzador local

Log `%TEMP%\DriveDuplicateFinder\validation-latest.log`, ejecución `29484-1061`, iniciada el 10/10/2026 a las 19:42:44; el archivo tenía última modificación 19:42:49. El log confirma la ruta efectiva del repositorio esperado.

- `dotnet build`: código 0, cero advertencias y cero errores, con .NET SDK 10.0.401.
- `--verify-sqlite-infrastructure`: código 0; resultado final `VALIDACION SUPERADA`.
- Infraestructura SQLite: migraciones 1–4, claves foráneas, WAL, índices, rollback y cascadas verificados; directorio temporal eliminado.
- Escaneo recuperable: persistencia multipágina, reanudación, cancelación, repetición idempotente, token inválido, aislamiento de cuenta/alcance, no publicación incompleta y reconciliación completados correctamente.
- Persistencia de revisiones: migraciones 1–4 idempotentes; flujo de revisión y plan paginados, ciclo de vida de decisiones, selección, reinicio SQLite, aislamiento, inventarios incompletos/obsoletos, membresía y conservación del JSON heredado verificados.
- Adaptador: grupos normal y de 205 miembros, Keeps múltiples, proyección de candidatos explícitos, confirmación invalidada, sesiones/cuentas/alcances incorrectos, FileId ajeno, límite de 2.001 miembros y cancelación verificados.
- Preflight offline: `ValidGroupApprovedWithGetOnly`, `MissingKeepOrCandidateRejectedLocally`, `UndecidedRejectedBeforeMemberGets`, `MissingFileBlocked`, `TrashedFileBlocked`, `ChangedSizeAndChecksumBlocked`, `PermissionAndNetworkErrorsClassified`, `CancellationHandledWithoutWrites`, `AccountMismatchRejectedBeforeGroupReads`, `DecisionChangeDuringPreflightDiscarded`, `NoWriteRequestsSent` y limpieza del SQLite temporal aparecen en `true`.
- La prueba `DecisionChangeDuringPreflightDiscarded` ejecuta `PagedGroupPreflightService` y exige conjuntamente resultado obsoleto, preflight no aprobado, fallo clasificado y transporte exclusivamente GET. El callback del fixture cambia la decisión al recibirse la solicitud del candidato; por tanto, el caso cubre las consultas de archivos, no solo las lecturas de identidad/raíz.
- Vista paginada y aislamiento demo: solicitudes actuales/obsoletas, bloqueo de acciones heredadas, grupos y páginas, decisiones al navegar y acciones demo bloqueadas aparecen en `true`.
- Todas las comprobaciones anteriores finalizaron sin excepción; sus bloques JSON independientes aparecen en el log.
- La prueba fue enteramente offline: HTTP simulado y bases SQLite temporales. No se realizó OAuth, escaneo, preflight contra Google Drive real ni llamada real a Drive.
- `git diff --check` se ejecutó después de esta validación y terminó correctamente; solo mostró avisos de normalización LF/CRLF.

## Pendiente, riesgos y límites

- **Preflight real pendiente:** no se ha ejecutado contra Google Drive real. La validación actual demuestra la lógica con transporte simulado, no permisos o comportamiento remoto de una cuenta real.
- La comprobación remota usa `Files.Get` para los metadatos del Keep de referencia y candidatos explícitos. El preflight existente compara identidad, nombre, tamaño, MD5, padres, fecha de modificación, versión y estado de papelera; también comprueba restricciones de propiedad, compartición, destacado, unidad compartida y capacidad de papelera para candidatos. No hace una consulta independiente de la carpeta padre.
- **Limpieza real desde la ruta paginada sigue deshabilitada.** Un preflight aprobado no autoriza ni inicia escrituras. No se conectó `CleanupBatchService.ExecuteAsync`; no se cambió la política existente `Files.Update(... Trashed = true ...)`.
- No se probó un escaneo masivo ni una limpieza real en Google Drive. No se implementó eliminación permanente; siguen prohibidos `Files.Delete` y `EmptyTrash`.
- El adaptador materializa un grupo completo solo hasta el límite declarado de 2.000 miembros; grupos mayores se rechazan de forma segura.

## Git y trabajo pendiente

- Rama `master`; HEAD estable `cc487ef3c28d1a4a1b0fd6c163dd470a6f26b137`. No se creó commit ni se hizo push.
- Los cambios de integración y los checkpoints existentes permanecen pendientes y se conservan. El v14 es un checkpoint nuevo; no sobrescribe v13 ni los históricos.
- Revisar una allowlist explícita antes de cualquier staging futuro. Mantener fuera credenciales, tokens, bases SQLite personales, logs y artefactos de compilación.

## Próximo paso recomendado

Solo si se autoriza expresamente, probar una lectura de preflight controlada con una cuenta real y un único grupo, manteniendo la escritura deshabilitada. Revisar errores y permisos sin ampliar scopes por defecto. La ejecución de limpieza debe permanecer como intervención separada, con confirmación y controles propios.
