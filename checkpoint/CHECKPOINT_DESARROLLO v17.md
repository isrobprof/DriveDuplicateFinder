# DriveDuplicateFinder — CHECKPOINT_DESARROLLO v17

> Hito: preparación y presentación local, no ejecutable, del plan de limpieza para un grupo paginado confirmado. Fecha: 11/10/2026.

## Objetivo del proyecto

Aplicación WinForms C#/.NET 10 para detectar duplicados exactos de Google Drive, persistir inventarios y revisiones paginados en SQLite y ofrecer una limpieza segura, explícita y auditable.

## Arquitectura actual

- WinForms `net10.0-windows`, SQLite local con migraciones versionadas 1–4.
- OAuth de análisis y limpieza separados. El análisis usa `DriveMetadataReadonly`; el preflight de Drive existente es solo lectura.
- Escaneo Full recuperable con persistencia de páginas/checkpoints y duplicados exactos por tamaño y MD5.
- Grupos, miembros y decisiones locales se consultan paginados, ligados a cuenta, alcance, `ScanId` e identidad estable del grupo.
- `PersistedCleanupGroupReviewAdapter` valida la revisión confirmada y reconstruye el grupo completo dentro del límite de 2.000 miembros.
- `PagedGroupCleanupService` expone ahora `PrepareLocalPreviewAsync`, que usa SQLite/adaptador y devuelve un snapshot no ejecutable. Sus métodos de preflight de limpieza y `ExecuteConfirmedAsync` no están conectados a la UI.

## IMPLEMENTADO

- La vista paginada tiene la acción **Preparar plan del grupo (solo local)**, habilitada solo para el grupo activo confirmado que contiene Keep y candidatos explícitos.
- La preparación vuelve a cargar desde SQLite la membresía completa y todas las decisiones del grupo, no la página de 100 miembros visible. El adaptador valida inventario Full Completed vigente, cuenta/alcance/ScanId, pertenencia de FileId, decisiones, confirmación y límite de 2.000.
- La vista muestra todos los Keep y candidatos explícitos, el tamaño potencial calculado como tamaño exacto del grupo por candidatos, y el número de pendientes excluidos. Los pendientes no se incorporan a las entradas.
- Una excepción de identidad, confirmación o límite bloquea la vista y se comunica al usuario. Cancelar o cambiar grupo/página/decisiones invalida la generación activa; las respuestas antiguas no se muestran.
- La vista indica expresamente que es una preparación local no ejecutable, que no hubo preflight remoto y que no se modificará Drive. El modo `--demo-review` conserva su aislamiento y puede usar la misma ruta exclusivamente local.

## VALIDADO

Validación autónoma local de Codex, 11/10/2026, con el procedimiento documentado en `AGENTS.md`:

- `dotnet build --no-restore -p:TargetPlatformMoniker=Windows%2CVersion=7.0 -v:minimal`: código 0, 0 errores y 0 advertencias.
- `dotnet .\bin\Debug\net10.0-windows\DriveDuplicateFinder.dll --verify-sqlite-infrastructure`: código 0. TEMP/TMP/TMPDIR se redirigieron a una carpeta única dentro de `obj/`, eliminada después de comprobar su ubicación.
- Todas las secciones emitidas por el verificador fueron correctas: STA, migraciones 1–4, escaneo recuperable, persistencia, adaptador, preflight offline, vista paginada y aislamiento demo.
- `PagedGroupCleanup`: sus 22 comprobaciones anteriores y las nuevas comprobaciones de vista previa fueron verdaderas. El caso añadido utilizó un grupo sintético de 205 miembros: 100 candidatos en la primera página, 103 pendientes y 2 Keep posteriores; se recuperaron los 102 explícitos, se excluyeron los pendientes, el total potencial fue correcto y no hubo solicitudes remotas.
- Se rechazaron cuenta, alcance y `ScanId` incorrectos, inventario pausado/obsoleto, decisión editada y grupos mayores de 2.000 antes de leer todos sus miembros. La prueba de solicitudes obsoletas verificó cancelación de la generación sustituida.
- Las comprobaciones estáticas del flujo confirmaron que la UI llama únicamente a `PrepareLocalPreviewAsync`; no llama desde la ruta paginada a `ExecuteConfirmedAsync`, `CleanupBatchService.ExecuteAsync` ni `GoogleDriveTrashService.ExecuteAsync`. Las guardas heredadas continúan verificadas.
- `git diff --check`: sin errores de whitespace; Git puede informar normalización LF/CRLF.

**Alcance de la evidencia:** la validación fue automatizada y offline, con SQLite temporal y transporte simulado para las pruebas preexistentes. No se inició la aplicación para una prueba gráfica de esta nueva ventana. La prueba 205-miembros valida el snapshot del coordinador, no la navegación visual WinForms.

### Validación visual posterior reportada por el usuario (12/10/2026)

El usuario confirmó manualmente en `--demo-review` la vista previa local sobre un grupo sintético de 205 miembros. Reportó que seleccionó un Keep y un Candidate desde páginas de miembros diferentes, confirmó el grupo y abrió la vista previa. Esta mostró los archivos correspondientes, 203 miembros Undecided excluidos y 1,5 MB potenciales; también confirmó que el aviso rojo se veía completo, el botón «Cerrar» era visible y la distribución de la ventana era correcta.

Esta evidencia es **VALIDACIÓN VISUAL REPORTADA POR EL USUARIO**; Codex no afirma haber realizado esa interacción gráfica. No se validó ningún envío real a papelera desde la nueva interfaz. Tampoco se probó visualmente el cambio entre escalados DPI distintos.

## PENDIENTE Y RIESGOS

- No se hizo OAuth, consulta real de Drive, escaneo, preflight desde esta intervención ni operación de limpieza.
- La nueva vista previa local no es una autorización ni un plan remoto aprobado; sus metadatos pueden quedar obsoletos frente a Drive hasta un preflight remoto independiente.
- La ejecución paginada real permanece deshabilitada y `ExecuteConfirmedAsync` no tiene manejador de UI. No se ejecutó `Files.Update`; no se modificaron `Files.Delete` ni `EmptyTrash`.
- La ruta heredada de limpieza permanece sin cambios.
- La revisión visual de la vista previa local fue reportada como satisfactoria por el usuario, pero no equivale a una validación de distintos escalados DPI ni de operaciones remotas. En una fase autorizada separada queda integrar el preflight remoto y una confirmación explícita de ejecución para un grupo pequeño. La primera escritura real requiere autorización independiente y no debe inferirse de estas pruebas.

## Git y continuidad

- Rama `master`; commit base publicado: `cafc6a4a979ba5d26897a73129fc61e2cf33afd1` (`feat: añadir coordinador paginado de limpieza con pruebas offline`).
- Cambios de esta intervención pendientes y sin commit: `Form1.cs`, `Services/PagedGroupCleanupService.cs`, `Services/PagedGroupCleanupLocalChecks.cs`, `Data/Sqlite/PersistedCleanupGroupReviewAdapterLocalChecks.cs`, `PagedGroupCleanupLocalPreviewForm.cs` y este checkpoint.
- Los siete checkpoints históricos sin seguimiento se conservaron intactos. No se hizo commit ni push.

## Próximo paso recomendado

Realizar, si se desea, una prueba visual del modo demo para la acción de preparación local. Después, auditar y autorizar por separado la integración del preflight remoto y la primera ejecución controlada; mantener bloqueada la escritura paginada hasta entonces.
