# DriveDuplicateFinder — CHECKPOINT_DESARROLLO v20

> Hito: capacidad futura de prueba limitada de limpieza paginada, implementada y validada offline, mantenida desactivada. Fecha: 29/10/2026.

## Objetivo del proyecto

Aplicación WinForms C#/.NET 10 para identificar duplicados exactos en Google Drive mediante metadatos, mantener inventarios recuperables y decisiones persistidas en SQLite, y preparar una limpieza controlada sin eliminación permanente.

## Arquitectura actual

- WinForms `net10.0-windows`, SQLite local, migraciones versionadas 1–4.
- Escaneo Full recuperable y paginado; detección exacta por tamaño y MD5.
- Revisión/decisiones persistidas por `AccountKey`, `ScopeKey`, `ScanId`, grupo estable y `FileId`.
- Preflight de grupo y servicios existentes `GoogleDriveTrashService` / `PagedGroupCleanupService`; no se añadió un segundo motor.
- OAuth de análisis y de limpieza separados. La autorización de limpieza usa el alcance `Drive`.

## IMPLEMENTADO

- Se añadió al coordinador una modalidad de prueba de un candidato con límite codificado de 10.000.000 bytes. Exige exactamente un candidato explícito, tamaño local/remoto conocido e idéntico, al menos un Keep, cobertura completa y coherente de decisiones, pertenencia al grupo, grupo confirmado vigente, identidad del inventario y preflight aprobado. Los `Undecided` quedan fuera.
- El límite se revalida al preparar el plan, crear la confirmación, iniciar la ejecución, después del preflight final y justo antes de delegar al servicio de papelera. La sesión de limpieza admite como máximo un intento de esta modalidad y el transporte de escritura se limita a un intento; un resultado incierto no se reintenta ni se presenta como confirmado.
- El diálogo final identifica todos los Keep y el único candidato y exige aceptación expresa. `Form1` conserva primero el preflight de solo lectura y solo puede solicitar OAuth de limpieza después de una admisión explícita de la modalidad limitada.
- Los interruptores permanecen cerrados en código: `RealPagedCleanupExecutionEnabled = false` y `SingleCandidateTrialExecutionEnabled = false`. No existe activación mediante CLI, preferencias, demo ni controles ocultos. La UI retorna antes de OAuth de escritura mientras ambos estén desactivados; el coordinador mantiene su propia guarda para sesiones autenticadas reales.
- Se corrigió la lectura del Keep del preflight inicial: el contrato de solo lectura heredado expone un `KeepFile` ancla, mientras el preflight con alcance de limpieza expone `KeepFiles`. El validador acepta ambos formatos solo donde corresponde; el preflight de limpieza sigue exigiendo todos los Keep.
- La confirmación explícita puede tener estado derivado `PartiallyReviewed` cuando hay varios Keep o miembros pendientes. La modalidad admite ese estado únicamente si la confirmación continúa vigente y no corresponde a un grupo saltado; exige igualmente un Keep y un candidato explícitos.

## VALIDADO

Validación autónoma local de Codex, 29/10/2026. Build ejecutado con SDK .NET 10.0.403 mediante el procedimiento documentado en `AGENTS.md`, con TEMP/TMP/TMPDIR y `DOTNET_CLI_HOME` aislados bajo un directorio único de `obj/`, eliminado al finalizar:

- `dotnet build --no-restore -p:TargetPlatformMoniker=Windows%2CVersion=7.0 -v:minimal`: código 0, 0 errores y 0 advertencias.
- `dotnet .\bin\Debug\net10.0-windows\DriveDuplicateFinder.dll --verify-sqlite-infrastructure`: código 0.
- El verificador informó `true` en las migraciones 1–4, infraestructura SQLite, escaneo recuperable, persistencia de revisión, adaptador, preflight offline, limpieza paginada, vista de solo lectura, aislamiento demo y simulación demo.
- `PagedGroupCleanup` pasó explícitamente: `TrialModeDisabledByDefault`, candidato válido de exactamente 10.000.000 bytes con un PATCH simulado confirmado, rechazo de múltiples candidatos, tamaño excesivo o desconocido, protección de varios Keep y exclusión de Undecided, autorización de sesión de un solo uso, aceptación final explícita, cambios de cuenta/alcance/ScanId/grupo/decisiones rechazados antes de PATCH, preflight obsoleto rechazado, resultado incierto con un solo intento, rechazo de sesión real mientras la modalidad está desactivada, bloqueo general del coordinador y resultados separados.
- La simulación de resultado incierto pierde la respuesta después de que el transporte local haya podido aplicar el PATCH; se comprobó un único intento, `OutcomeUncertain` y rechazo de reutilización de la autorización.
- `git diff --check`: correcto; Git solo mostró avisos de normalización LF/CRLF.

**Alcance de evidencia:** todas las operaciones de escritura de las verificaciones usaron SQLite temporal y transporte HTTP local simulado. No se inició la aplicación normal, no se solicitó OAuth, no se contactó con Google Drive y no se modificó ningún archivo real. La pasada de un PATCH corresponde exclusivamente al transporte simulado.

## PENDIENTE Y RIESGOS

- La modalidad limitada y la ejecución general siguen desactivadas; el flujo normal no puede solicitar OAuth de escritura ni emitir PATCH. No se ha probado esta modalidad contra Google Drive real.
- Una futura activación requiere una autorización posterior y específica. El cambio de habilitación debe ser deliberado y limitado al interruptor de prueba de candidato; no abrir la ejecución general ni introducir opciones de usuario/CLI. Antes de cualquier escritura real se requiere revisar la cuenta y el grupo concretos en UI, volver a confirmar el candidato/Keep, efectuar el preflight inmediatamente anterior y obtener confirmación final explícita.
- No se ha validado en vivo la semántica de permisos, metadatos, concurrencia remota ni respuesta incierta contra Google Drive. Ante respuesta perdida no debe repetirse la operación automáticamente; se necesita reconciliación/decisión humana antes de cualquier nuevo intento.
- La ruta heredada no se modificó. `Files.Update(Trashed=true)` permanece como mecanismo existente; no se añadieron `Files.Delete` ni `EmptyTrash`.

## Git y continuidad

- Rama `master`; HEAD base: `34ab3c0d68ff24349f1f36ded6cd5f9c701c3c3b` (`feat: preparar ejecución paginada segura en WinForms con bloqueo de escritura`).
- Cambios pendientes del hito: `Form1.cs`, `PagedGroupCleanupConfirmationForm.cs`, `Services/GoogleDriveCleanupAuthService.cs`, `Services/PagedGroupCleanupLocalChecks.cs`, `Services/PagedGroupCleanupService.cs` y este checkpoint. No se creó commit ni se hizo push.
- Los siete checkpoints históricos locales sin seguimiento se conservaron intactos.

## Próximo paso recomendado

Revisar este checkpoint y efectuar una revisión de seguridad independiente antes de autorizar cualquier activación. Solo con autorización explícita posterior podría habilitarse deliberadamente la modalidad limitada y planificarse una primera operación real sobre un único candidato pequeño. Hasta entonces, mantener ambos interruptores en `false`.
