# DriveDuplicateFinder — CHECKPOINT_DESARROLLO v13

> Hito: validación local del adaptador que reconstruye una revisión completa de un único grupo desde SQLite. Fecha: 10/10/2026. La evidencia de compilación y pruebas procede del log generado por el lanzador de Windows ejecutado por el usuario; Codex leyó ese log y no repitió los comandos.

## Objetivo del proyecto

Aplicación WinForms C#/.NET 10 para analizar metadatos de Google Drive, encontrar duplicados exactos y facilitar una revisión local y segura antes de cualquier limpieza remota. La revisión paginada y el plan SQLite siguen sin habilitar operaciones reales de limpieza.

## Arquitectura actual

- WinForms sobre .NET 10 con persistencia SQLite local y migraciones versionadas 1–4.
- OAuth de análisis y autorización de limpieza separados. El análisis usa metadatos; no descarga contenido.
- Escaneo Full recuperable con páginas/checkpoints SQLite e identidad de inventario `(AccountKey, ScopeKey, ScanId)`.
- Duplicados exactos determinados por tamaño y MD5, consultados de forma paginada.
- Decisiones de revisión persistidas por cuenta, alcance, sesión, grupo estable y `FileId`.
- La interfaz paginada admite revisión local y una vista previa local del plan. El modo `--demo-review` usa SQLite temporal con datos sintéticos.
- El JSON heredado se conserva y no se atribuye a una sesión sin procedencia verificable.

## IMPLEMENTADO

- `PersistedCleanupGroupReviewAdapter` construye el contrato existente `DuplicateGroupReview` para un grupo estable confirmado, leyendo desde SQLite solo ese grupo y sus decisiones.
- El adaptador valida inventario Full Completed vigente, cuenta, alcance, sesión, grupo, pertenencia de cada `FileId`, decisión explícita y confirmación actual. Conserva `Keep`, `CandidateForTrash` y `Undecided` sin convertir pendientes en candidatos ni descartar Keeps múltiples.
- La lectura de miembros es paginada internamente y el límite de 2.000 miembros se comprueba antes de leer páginas; grupos mayores se rechazan con `CleanupGroupMemberLimitExceededException`. La reconstrucción de rutas también tiene un límite explícito de carpetas antecesoras.
- `PersistedCleanupGroupReviewAdapterLocalChecks` añade comprobaciones SQLite offline y se invoca desde `--verify-sqlite-infrastructure` en `Program.cs`.
- El resultado del adaptador indica que el preflight remoto está pendiente y la ejecución deshabilitada. El adaptador no depende de `DriveService` ni llama a preflight, servicios de papelera o ejecución por lotes.

## VALIDADO

### Evidencia automatizada leída en el log local de Windows

El usuario ejecutó `VALIDAR DriveDuplicateFinder.cmd`. Codex leyó `%TEMP%\DriveDuplicateFinder\validation-latest.log`; no repitió compilación ni verificaciones.

- `dotnet build`: código 0, 0 advertencias y 0 errores.
- `--verify-sqlite-infrastructure`: código 0. El log contiene un bloque independiente `PersistedCleanupGroupReviewAdapter`, por lo que confirma que las nuevas comprobaciones se invocaron y ejecutaron; no es solo una inferencia a partir del código de salida global.
- En ese bloque: `NormalGroupMappedExactly`, `LargeGroupPagesAndPendingMappedExactly`, `MultipleKeepDecisionsPreserved`, `ChangedConfirmationRejected`, `OldIncompleteAndWrongAccountRejected`, `WrongScopeRejected`, `ForeignFileIdRejected`, `OversizedGroupRejectedBeforeMemberRead`, `CancellationReturnsNoPartialReview` y `AdapterHasNoRemoteExecutionDependency` aparecen en `true`. El directorio SQLite temporal figura eliminado.
- El mismo log registra migraciones 1–4 y las verificaciones anteriores de infraestructura, escaneo recuperable, persistencia de revisiones, vista paginada y aislamiento demo como correctas.
- La verificación es local/offline. No se hizo escaneo, OAuth, preflight remoto ni llamada a Google Drive.
- `git diff --check`: código 0 tras la validación.

La comprobación cubre la correspondencia de candidatos explícitos con el modelo adaptado, la exclusión de `Undecided`, varios `Keep`, un grupo de 205 miembros, rechazo de 2.001 miembros antes de materializar, inventario incompleto/obsoleto/otra cuenta, alcance incorrecto, `FileId` ajeno, cancelación y ausencia de dependencia de ejecución remota.

## Cambios pendientes en Git

- Rama `master`; HEAD estable `cc487ef3c28d1a4a1b0fd6c163dd470a6f26b137` (`feat: consolidar escaneo recuperable y revisión paginada con demo aislada`). No se creó commit ni se hizo push en este hito.
- Cambios funcionales pendientes: `Program.cs`, `Services/PersistedCleanupGroupReviewAdapter.cs` y `Data/Sqlite/PersistedCleanupGroupReviewAdapterLocalChecks.cs`.
- Este checkpoint v13 es nuevo. Se preservan los siete checkpoints históricos sin seguimiento; no se modificaron ni renombraron.

## Decisiones técnicas y problemas resueltos

- Se reutiliza el contrato completo existente por grupo y el repositorio SQLite, en vez de introducir otro motor de limpieza o materializar todos los grupos.
- El límite del grupo se comprueba con el contador persistido antes de recuperar sus miembros. El test verifica el tipo y los valores del diagnóstico de límite.
- Las comprobaciones de identificadores adulterados y límite se realizan antes de sembrar la nueva sesión incompleta, porque iniciar un Full nuevo invalida el inventario previo y alteraría qué condición estaba probando cada caso.
- La cancelación no entrega un resultado parcial. El adaptador es local-only; no se agregó ningún camino de ejecución remota.

## PENDIENTE, riesgos y límites

- **Preflight remoto pendiente:** no se ha conectado ni validado `GoogleDriveTrashService.PreflightAsync` con el grupo adaptado. El metadato local no sustituye la lectura remota actual.
- **Ejecución real pendiente y bloqueada:** no se ha habilitado `CleanupBatchService`, el servicio de papelera ni `Files.Update(Trashed = true)` desde esta ruta.
- No se ha validado un escaneo masivo ni una limpieza real contra Google Drive.
- El contrato antiguo representa un grupo completo en memoria; para esta primera versión se acota a 2.000 miembros y se rechaza sin truncamiento por encima del límite. La ruta paginada de presentación continúa siendo el mecanismo para grupos mayores.
- No agregar ni publicar cambios sin revisar el staging explícito. Mantener fuera secretos, tokens, bases personales SQLite y artefactos.

## Próximo paso recomendado

Diseñar una intervención separada para conectar el preflight remoto por grupo con confirmación explícita, cancelación y comprobaciones de inventario vigentes. Mantener deshabilitada la ejecución hasta auditar el resultado remoto; cualquier ejecución futura debe reutilizar el comportamiento permitido con `Files.Update(... Trashed = true ...)` y nunca incorporar `Files.Delete`, `EmptyTrash` ni eliminación permanente.
