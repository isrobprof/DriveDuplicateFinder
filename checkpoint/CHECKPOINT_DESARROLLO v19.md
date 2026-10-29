# DriveDuplicateFinder — CHECKPOINT_DESARROLLO v19

> Hito: flujo WinForms paginado conectado al coordinador existente, con ejecución real cerrada por una guarda de código. Fecha: 29/10/2026.

## Objetivo del proyecto

Aplicación WinForms C#/.NET 10 para localizar duplicados exactos en Google Drive, conservar inventarios y decisiones paginados en SQLite y permitir una futura limpieza por grupo con confirmación y registro, sin eliminación permanente.

## Arquitectura actual

- WinForms `net10.0-windows`, base SQLite local y migraciones versionadas 1–4.
- Escaneo Full recuperable, páginas/checkpoints persistidos y detección exacta por tamaño + MD5.
- Revisión y consultas paginadas ligadas a cuenta, alcance, `ScanId`, grupo estable y decisiones explícitas por `FileId`.
- `PersistedCleanupGroupReviewAdapter`, `PagedGroupPreflightService`, `GoogleDriveTrashService` y `PagedGroupCleanupService` son los servicios existentes reutilizados; no hay un segundo motor de limpieza.
- La autorización OAuth de limpieza sigue separada y utiliza `DriveService.Scope.Drive`; la autorización de análisis no se modificó.

## IMPLEMENTADO

- La vista paginada normal incorpora **Preparar limpieza paginada (escritura bloqueada)** para un grupo Full confirmado vigente con Keep y candidatos explícitos. La acción exige inventario/grupo activo coherentes y bloquea doble ejecución, navegación concurrente y solicitudes obsoletas.
- El recorrido usa un preflight de solo lectura para el grupo actual. Su pantalla presenta los Keep y candidatos con `FileId`. No se reutiliza una aprobación previa. Con la guarda cerrada, el flujo termina tras mostrar el resultado y no solicita autorización de limpieza.
- El código posterior a la guarda conserva la integración existente con `GoogleDriveCleanupAuthService.ConnectSessionAsync`, validación de identidad y `PagedGroupCleanupService.PreparePlanAsync`, pero es inalcanzable mientras `RealPagedCleanupExecutionEnabled` sea `false`. El adaptador conserva Keep explícitos, solo incluye candidatos explícitos y excluye Undecided; mantiene el límite de 2.000 miembros.
- Se reutiliza `PagedGroupCleanupConfirmationForm` con modo y texto propios para la ruta real; el control final permanece inalcanzable con la guarda cerrada. Las dos casillas exigen revisar todos los Keep y candidatos antes de crear la confirmación exacta del coordinador.
- Antes de solicitar consentimiento OAuth de escritura, Form1 comprueba `PagedGroupCleanupService.IsRealExecutionEnabled`. Mientras la guarda sea `false`, muestra un mensaje claro y retorna después del preflight de solo lectura y de su pantalla de resultados. No llama a `ConnectSessionAsync`, no abre la confirmación final de ejecución y no emite PATCH. La preparación local y el botón de preflight de solo lectura se mantienen disponibles.
- El handler no llama directamente a `Files.Update`, `GoogleDriveTrashService.ExecuteAsync` ni `CleanupBatchService.ExecuteAsync`; la ruta posterior a la guarda solo delegaría en `PagedGroupCleanupService.ExecuteConfirmedAsync` si la ejecución se habilitara expresamente en una fase futura.
- El coordinador tiene `RealPagedCleanupExecutionEnabled = false`. Una sesión OAuth real se rechaza en el punto de entrada de `ExecuteConfirmedAsync` con `PagedGroupCleanupExecutionDisabledException`, antes de peticiones adicionales, historial, invalidación del inventario o escritura. El valor no se controla mediante UI, argumento CLI ni configuración de usuario.
- Solo las sesiones creadas explícitamente para transporte simulado pueden superar esa guarda cerrada; el modo `--demo-review` continúa usando semilla sintética temporal y transporte HTTP local en memoria. El botón demo no invoca OAuth ni usa la sesión real.
- Para la futura ruta habilitada, el coordinador conserva revalidaciones existentes de cuenta, alcance, sesión, decisiones, confirmación, miembros, metadatos remotos y conjuntos Keep/Candidate antes de enviar solamente snapshots de candidatos al servicio de papelera. La cancelación y el límite de intentos de PATCH existentes se mantienen.
- La ruta paginada presenta resultados por categorías mutuamente distinguibles: confirmados/verificados, pendientes de verificar, fallidos, inciertos, cancelados y no procesados. `OutcomeUncertain` no se considera confirmado ni reintentable; el detalle de UI acota la lista visible y remite el resto al historial.

## VALIDADO

Build y verificación autónomos locales de Codex, 29/10/2026, con SDK .NET 10.0.403, `DOTNET_CLI_HOME`, TEMP, TMP y TMPDIR aislados en una carpeta única bajo `obj/` y eliminada al terminar:

- `dotnet build --no-restore -p:TargetPlatformMoniker=Windows%2CVersion=7.0 -v:minimal`: código 0, 0 errores y 0 advertencias de compilación.
- `dotnet .\bin\Debug\net10.0-windows\DriveDuplicateFinder.dll --verify-sqlite-infrastructure`: código 0.
- El verificador confirmó migraciones 1–4, persistencia SQLite, escaneo recuperable, aislamiento demo, preflight offline y `PagedGroupCleanup`.
- Nuevas comprobaciones identificables: `RealUiBlocksWriteOAuthBeforeConnect=true` y `ReadOnlyPreflightAndLocalPreparationRemainAvailable=true`. La primera revisión del verificador detectó una expectativa de texto demasiado estricta en el test estático; se ajustó el reconocimiento para verificar la guarda, el mensaje, el `return` y el orden frente a OAuth/confirmación, sin cambiar la guarda de producción. La reejecución completa pasó.
- `PagedGroupCleanup` también confirmó `NoWritesFromPagedUi=true` y `RealExecutionBlockedAtCoordinator=true`, además de todas las comprobaciones existentes de identidad, decisiones, preflight obsoleto, resultados parciales/inciertos, Keep/Undecided y demo. La UI corta antes de OAuth; el test del coordinador conserva la comprobación independiente de que una sesión real se rechaza sin PATCH.
- Nuevas propiedades en `PagedGroupCleanup`: `RealExecutionBlockedAtCoordinator=true` y `PagedOutcomesAreSeparated=true`. La prueba de guarda usó un transporte local clasificado como sesión real: `ExecuteConfirmedAsync` rechazó la confirmación antes de nuevas solicitudes, PATCH, historial o invalidación de la confirmación local.
- El test estático de integración confirmó el orden preflight → presentación de Keep/Candidate → guarda UI cerrada con aviso/retorno → (solo tras abrir la guarda) autorización separada → plan → confirmación final → coordinador, y que el handler no llama directamente a servicios de escritura.
- `DemoPagedCleanupSimulation` siguió pasando con `SimulatorUsesLocalTransportWithoutOAuth=true`, `PagedUiSimulationGuardedAndDoubleClickBlocked=true` y sus demás comprobaciones.
- La prueba de presentación separó correctamente resultados confirmados, pendientes, fallidos, inciertos, cancelados y no procesados.
- `git diff --check`: sin errores de whitespace; Git mostró avisos de normalización LF/CRLF.

**Alcance de la evidencia:** todas las pruebas usaron SQLite temporal y transportes simulados. No se abrió WinForms, no se ejecutó OAuth, no se consultó Google Drive y no se hicieron escrituras reales. El preflight real y la limpieza paginada real siguen sin validarse.

## PENDIENTE Y RIESGOS

- El flujo está preparado, pero la guarda efectiva está cerrada (`RealPagedCleanupExecutionEnabled = false`); una sesión real no puede ejecutar `Files.Update` por esta ruta.
- Al estar cerrada la guarda, la llamada real al coordinador se detiene inmediatamente. La revalidación final que corre después de esa guarda se cubre offline con el coordinador existente, pero no se ejecutó para una sesión de Google real.
- En el estado actual, la acción de preparación puede efectuar el preflight de metadatos de solo lectura, pero la comprobación UI evita pedir OAuth de limpieza mientras la ejecución esté deshabilitada. La prueba es estática/offline; Codex no abrió WinForms ni inició OAuth.
- No se modificaron `Files.Update(Trashed=true)`, los servicios heredados de limpieza, ni se añadieron `Files.Delete` o `EmptyTrash`.
- No se probó visualmente el nuevo recorrido normal de autorización/preflight/bloqueo. Solo la simulación demo previa cuenta con validación visual reportada por el usuario (registrada en v18).

## Git y continuidad

- Rama `master`; HEAD base publicado: `4807acfdf8ca27517b923deeb4a38a354bc8a88b` (`feat: integrar confirmación y simulación offline de limpieza paginada`).
- Cambios pendientes de este hito: `Form1.cs`, `PagedGroupCleanupConfirmationForm.cs`, `Services/DemoPagedCleanupSimulationLocalChecks.cs`, `Services/DemoPagedCleanupSimulationService.cs`, `Services/GoogleDriveCleanupAuthService.cs`, `Services/GoogleDriveTrashPreflightLocalChecks.cs`, `Services/PagedGroupCleanupLocalChecks.cs`, `Services/PagedGroupCleanupService.cs` y este checkpoint.
- Los siete checkpoints históricos sin seguimiento se conservaron intactos. No se creó commit ni se hizo push.

## Cambio mínimo para una primera limpieza real controlada

Requiere autorización posterior y explícita. Tras una auditoría de la ruta, habilitar deliberadamente la guarda de código en una intervención separada y probar con un solo grupo vigente y un único candidato explícito, confirmando la cuenta correcta, Keep protegidos, preflight remoto reciente, confirmación final y resultado. No debe activarse mediante argumento CLI, valor de usuario ni resultado de la demo.
