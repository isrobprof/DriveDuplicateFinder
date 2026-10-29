# DriveDuplicateFinder — CHECKPOINT_DESARROLLO v21

> Hito: autorización persistente local de un solo uso preparada para una futura prueba paginada; emisión real y escrituras reales siguen deshabilitadas. Fecha: 29/10/2026.

## Objetivo del proyecto

Aplicación WinForms C#/.NET 10 para detectar duplicados exactos de Google Drive a partir de metadatos, conservar inventarios recuperables y preparar una limpieza controlada sin eliminación permanente.

## Arquitectura actual

- WinForms `net10.0-windows` y SQLite local con migraciones versionadas.
- Escaneo Full recuperable y paginado; duplicados exactos por tamaño y MD5.
- Consultas paginadas de grupos/miembros y revisión persistente ligada a `AccountKey`, `ScopeKey`, `ScanId`, identidad estable de grupo y `FileId`.
- `PersistedCleanupGroupReviewAdapter`, preflight remoto de solo lectura y `PagedGroupCleanupService` reutilizan los servicios existentes.
- OAuth de análisis y limpieza separados. La operación de escritura existente continúa siendo `Files.Update(Trashed=true)`; no hay borrado permanente.

## IMPLEMENTADO

- Migración SQLite 5, sin modificar las migraciones 1–4. Crea un único slot de autorización persistente con estados `Unissued`, `Issued` y `Consumed`. La migración solo crea el slot vacío; no concede ni emite autorizaciones.
- El contrato local vincula permiso, cuenta, alcance, sesión Full, `GroupId` estable, tamaño y checksum del grupo, `CandidateFileId`, tamaño conocido del candidato, hora de confirmación y hash del conjunto completo de Keep. No contiene credenciales ni tokens OAuth.
- La modalidad limitada requiere una revisión completa y confirmada, al menos un Keep explícito, exactamente un candidato explícito, tamaño candidato igual al tamaño del grupo y límite de 10.000.000 bytes. Los Keep se vinculan en conjunto; `Undecided` no entra en el conjunto de candidatos.
- En el coordinador, después de la última identidad, lectura de decisiones, preflight remoto reciente y confirmación, se comprueba que el slot coincida exactamente. Tras validar e invalidar atómicamente la instantánea vigente, el coordinador consume el slot en una transacción SQLite inmediatamente antes de delegar el único intento de escritura. La transición no recibe el token de cancelación del usuario: una vez iniciada, se completa o falla cerrada.
- El slot consumido no puede volver a disponible ni borrarse mediante las operaciones normales del esquema. El consumo es global al archivo SQLite, no al objeto `CleanupDriveSession`; una sesión nueva o un reinicio no habilitan otro intento.
- No se implementó emisor productivo ni interfaz/comando para emitir el permiso. Solo las comprobaciones offline siembran filas sintéticas directamente en SQLite temporal.
- Se mantienen `RealPagedCleanupExecutionEnabled = false` y `SingleCandidateTrialExecutionEnabled = false`; Form1 continúa bloqueando antes de OAuth de escritura y el coordinador conserva su guarda independiente. Demo y ruta heredada no se habilitan ni modifican.

## VALIDADO

Validación autónoma local de Codex el 29/10/2026, con SDK .NET 10.0.403 y el procedimiento seguro de `AGENTS.md` (TEMP/TMP/TMPDIR y `DOTNET_CLI_HOME` aislados bajo un directorio temporal único dentro de `obj/`, eliminado al finalizar):

- `dotnet build --no-restore -p:TargetPlatformMoniker=Windows%2CVersion=7.0 -v:minimal`: código 0, 0 errores y 0 advertencias.
- `dotnet .\bin\Debug\net10.0-windows\DriveDuplicateFinder.dll --verify-sqlite-infrastructure`: código 0.
- `git diff --check`: correcto; Git informó únicamente avisos de normalización LF/CRLF.
- Migraciones 1–5 se aplicaron una sola vez e idempotentemente en SQLite temporal. El verificador general de escaneo, revisión, adaptador, preflight, demo y limpieza paginada pasó.
- `PagedGroupCleanup` informó `true` para las pruebas previas y las nuevas: consumo exacto; rechazo de otro candidato, grupo, cuenta, alcance, sesión o decisión cambiada; persistencia tras reabrir SQLite; un único consumidor ganador entre repositorios/conexiones independientes; slot/tabla ausente o corrupta bloquea antes de PATCH; el permiso queda consumido después de fallo definitivo, respuesta perdida incierta e interrupción simulada tras consumo; no existe emisor operativo; ambos interruptores siguen cerrados.
- El PATCH exitoso y el error definitivo/resultado incierto se ejercitaron exclusivamente contra transporte HTTP local simulado. El fixture persistente se creó en SQLite temporal.

**Límites de evidencia:** no se abrió la aplicación normal, no se solicitó OAuth, no se accedió a Google Drive ni a la SQLite personal. No se emitió permiso para un archivo real y no hubo escritura remota. La concurrencia comprobada usó repositorios/conexiones SQLite independientes dentro del mismo proceso; no fue una prueba de procesos de sistema separados.

## PENDIENTE Y RIESGOS

- Los dos interruptores de escritura siguen en `false`. El slot real permanece sin emitir y la futura autorización de un archivo real requiere una fase posterior y autorización explícita.
- Una respuesta remota incierta o una interrupción tras el consumo deja el permiso gastado incluso si no se sabe si el PATCH llegó a aplicarse; no se debe reintentar automáticamente. Una revisión humana y un procedimiento explícito de reconciliación serán necesarios.
- SQLite local y sus triggers protegen frente al uso accidental por la aplicación, no frente a un usuario/herramienta con capacidad de alterar o borrar deliberadamente el archivo o reemplazar la base. No constituye una autorización criptográfica ni una barrera frente a manipulación local privilegiada.
- La secuencia SQLite y Google Drive no es una transacción distribuida. La auditoría/historial local puede quedar pendiente si el proceso falla alrededor de la escritura remota.
- No se ha probado el nuevo recorrido de ejecución contra Drive real. No cambiar OAuth, `Files.Update(Trashed=true)`, ni las guardas de UI/coordinador hasta recibir autorización específica.

## Git y continuidad

- Rama `master`; HEAD base `34a94281dc30493ffcf172d1cb4b30fccd1f2684`.
- Cambios pendientes del hito: migración 5, contrato de autorización, consumo transaccional en `ReviewStateRepository`, integración previa al PATCH en `PagedGroupCleanupService`, comprobaciones y actualizaciones de expectativas de migración, más este checkpoint.
- No se creó commit ni se hizo push. Los siete checkpoints históricos locales sin seguimiento se conservaron intactos.

## Próximo paso recomendado

Revisar v21 y la migración/código de consumo. Mantener ambos interruptores cerrados. Solo tras autorización explícita posterior se podrá diseñar y auditar la emisión controlada del permiso para el candidato real seleccionado; esa emisión no debe habilitar la limpieza general ni por lotes.
