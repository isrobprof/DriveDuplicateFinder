# DriveDuplicateFinder — CHECKPOINT_DESARROLLO v18

> Hito: simulación offline del flujo paginado de limpieza desde WinForms. Fecha: 12/10/2026.

## Objetivo del proyecto

Aplicación de escritorio WinForms en C#/.NET 10 para encontrar duplicados exactos en Google Drive, persistir inventarios y decisiones localmente en SQLite y permitir una limpieza explícita, comprobable y segura.

## Arquitectura actual

- WinForms `net10.0-windows`; caché, inventarios y decisiones paginadas en SQLite con migraciones versionadas 1–4.
- Escaneo Full recuperable mediante persistencia por página/checkpoint. Duplicados exactos por tamaño y MD5.
- Revisión y limpieza paginadas ligadas a cuenta, alcance, `ScanId`, identidad estable del grupo y decisiones por `FileId`.
- `PersistedCleanupGroupReviewAdapter`, `PagedGroupPreflightService` y `PagedGroupCleanupService` reutilizan contratos existentes para validar y coordinar la ruta paginada.
- `Form1` mantiene separadas la vista normal y la demostración sintética `--demo-review`.

## IMPLEMENTADO

- El modo `--demo-review` genera un inventario SQLite temporal sintético y aislado; la identidad de cuenta de prueba utiliza un dominio reservado `.invalid`.
- La demostración dispone de una acción visible solo en demo para recorrer preparación local, preflight simulado, preparación del plan, confirmación explícita y resultado simulado.
- El servicio demo acepta exclusivamente la semilla aislada y la identidad de grupo de ese inventario. El preflight y el coordinador usan instancias de `DriveService` cuyo `HttpClientFactory` está conectado a un transporte HTTP en memoria; no reciben credenciales OAuth ni el cliente de Drive de la aplicación.
- El transporte simulado entrega metadatos sintéticos y registra solicitudes. Los PATCH simulados solo aceptan IDs de candidatos explícitos y el contenido `trashed=true`; los Keep y Undecided no se envían.
- La confirmación enumera todos los Keep y candidatos y requiere que se revisen explícitamente ambas listas. Rechazarla no llama al coordinador de ejecución. La pantalla de resultados identifica la simulación, distingue resultados confirmados, fallidos, inciertos, cancelados y no procesados, y advierte que `OutcomeUncertain` no es confirmación ni base para reintentar.
- El estado del historial de demostración se redirige a una subcarpeta de la base temporal demo. Fuera del modo demo se conserva la configuración normal.
- `Program.cs --verify-sqlite-infrastructure` ejecuta también las comprobaciones `DemoPagedCleanupSimulationLocalChecks`; sus fallos provocan salida no cero.
- La captura genérica de error del preflight expone solo el tipo de excepción como diagnóstico, no su mensaje potencialmente sensible.

## VALIDADO

Validación autónoma local en Codex, usando el procedimiento de `AGENTS.md` y una carpeta temporal única bajo `obj/` para TEMP/TMP/TMPDIR:

- `dotnet build --no-restore -p:TargetPlatformMoniker=Windows%2CVersion=7.0 -v:minimal`: código 0, cero errores y cero advertencias.
- `dotnet .\bin\Debug\net10.0-windows\DriveDuplicateFinder.dll --verify-sqlite-infrastructure`: código 0.
- El verificador informó como superadas `DemoPagedCleanupSimulation` y todas las suites previas, incluidas migraciones 1–4, escaneo recuperable, persistencia, aislamiento demo, preflight offline, `PagedGroupCleanup` y vista paginada.
- La suite nueva verificó inventario sintético completado, confirmación explícita, rechazo/obsolescencia sin PATCH, identidad de grupo e inventario, preflight bloqueado, Keep protegidos, Undecided excluidos aunque estén en páginas distintas, transporte local, presentación sin afirmar escritura real, tratamiento no reintentable de resultado incierto, controles de concurrencia y aislamiento de historial.
- Las pruebas previas de `PagedGroupCleanup` siguen cubriendo cambios de cuenta/alcance/ScanId/decisiones, preflight obsoleto, escrituras parciales, cancelación, pérdida de respuesta posterior a una operación potencialmente aplicada y ausencia de reintentos automáticos.
- `git diff --check`: sin errores de whitespace. Git emitió avisos de normalización LF/CRLF en archivos de Windows.
- Se verificó estáticamente que el manejador del botón de simulación solo se muestra en modo demo y que el `Form1` normal no invoca `ExecuteConfirmedAsync`, `CleanupBatchService.ExecuteAsync` ni `GoogleDriveTrashService.ExecuteAsync` desde la nueva acción.

**Alcance:** las verificaciones usan SQLite temporal, identidad sintética y un handler HTTP completamente local. No se inició la aplicación durante esta intervención ni se realizó comprobación gráfica por Codex. El usuario no confirmó una nueva prueba visual para este flujo ampliado.

### Validación visual comunicada por el usuario (29/10/2026)

El usuario reportó haber recorrido manualmente `--demo-review` con un grupo sintético de 205 miembros: 1 Keep, 1 candidato explícito y 203 Undecided excluidos. La confirmación mostró dos casillas obligatorias; después, el preflight offline apareció aprobado, el coordinador terminó como `Completed`, se confirmó 1 PATCH simulado, con 0 fallos y 0 resultados inciertos. El usuario confirmó además que no hubo acceso ni escritura real en Google Drive.

Esta sección registra exclusivamente la observación comunicada por el usuario; no es una prueba gráfica ejecutada por Codex. El PATCH ocurrió únicamente en el transporte en memoria del modo demo y no representa una modificación de Drive.

## PENDIENTE Y RIESGOS

- El flujo paginado normal continúa sin ejecución real conectada a `Form1`; la simulación no habilita ni autoriza escrituras reales.
- No se realizó OAuth, ninguna llamada a Google Drive, escaneo real, preflight real ni operación real de papelera en esta intervención.
- No se validó la limpieza real del coordinador paginado. `Files.Update(Trashed=true)` solo se ejercitó contra el transporte simulado de demo; no se hizo `Files.Delete` ni `EmptyTrash`.
- La comprobación gráfica de esta nueva confirmación y pantalla de resultados fue reportada manualmente por el usuario; Codex no realizó automatización gráfica. Esta observación no sustituye pruebas reales de preflight ni limpieza sobre Drive.
- Antes de autorizar la futura limpieza real, se requiere una auditoría específica de guards, identidad, preflight remoto y confirmación por grupo, seguida de autorización explícita para una prueba controlada. Los resultados de demo nunca deben reutilizarse como preflight real.

## Git y continuidad

- Rama `master`; HEAD base observado: `60fc84f6979d3900deff555a19011738186933d5`.
- Cambios actuales sin commit: `Form1.cs`, `Program.cs`, `Services/DemoReviewData.cs`, `Services/GoogleDriveTrashService.cs`, `Services/PagedGroupCleanupService.cs`, `PagedGroupCleanupConfirmationForm.cs`, `PagedGroupCleanupSimulationResultForm.cs`, `Services/DemoPagedCleanupSimulationLocalChecks.cs` y `Services/DemoPagedCleanupSimulationService.cs`, además de este checkpoint.
- Se conservaron intactos los siete checkpoints históricos sin seguimiento. No se creó commit ni se hizo push.

## Próximo paso recomendado

Revisar visualmente `--demo-review` si se necesita validar la nueva confirmación y pantalla de resultados. Mantener la ejecución real deshabilitada hasta completar una auditoría separada del flujo paginado y recibir autorización específica para cualquier interacción remota de escritura.
