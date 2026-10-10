# DriveDuplicateFinder — CHECKPOINT_DESARROLLO v11

> Hito: corrección de arranque STA y navegación paginada del modo demo. Fecha: 10/10/2026. Este checkpoint documenta validaciones automáticas locales; la edición visual del ComboBox todavía debe confirmarse en escritorio interactivo.

## Objetivo del proyecto

Aplicación WinForms C#/.NET 10 para analizar metadatos de Google Drive, revisar duplicados exactos y preparar una eventual limpieza segura. La revisión y el plan se almacenan localmente; ningún resultado parcial habilita operaciones remotas.

## Arquitectura actual

- WinForms y SQLite local con migraciones versionadas 1–4.
- Análisis OAuth separado de limpieza OAuth. La detección usa tamaño + MD5, sin descargar contenido.
- Escaneo Full recuperable con páginas/checkpoints SQLite, identidad `(AccountKey, ScopeKey, ScanId)` y reconciliación al completar.
- Consultas paginadas para grupos, miembros, decisiones y plan. Revisión por FileId con estados explícitos `Undecided`, `Keep`, `CandidateForTrash` y confirmación operativa por grupo.
- La ruta paginada mantiene limpieza real, preflight remoto y recomendaciones bloqueados. El plan es una previsualización SQLite local.
- El JSON heredado no se importa ni sobrescribe de forma ambigua.

## IMPLEMENTADO

- `--demo-review` abre los controles normales de revisión paginada con una SQLite temporal aislada; no usa OAuth ni el escaneo de Drive.
- La semilla ficticia tiene identidad `DEMO-ONLY`, tres grupos, 210 archivos y un grupo de 205 miembros. Incluye nombres, rutas derivadas, tamaños, fechas y MD5 sintéticos.
- Las decisiones se persisten durante la sesión en la base temporal. El directorio solo se elimina si está bajo `%TEMP%\DriveDuplicateFinder\demo-*`; al cerrar se intenta borrar.
- `Program.Main` es síncrono y está marcado `[STAThread]`. La preparación SQLite del demo ocurre de forma asíncrona desde `Form1_Shown`, donde WinForms ya ha instalado su contexto de sincronización. El constructor de Form1 también valida STA.
- En modo demo, conexión, escaneo y reapertura real están deshabilitados en controles y manejadores. `IsLegacyActionsAllowed` también incorpora el modo demo, bloqueando acciones heredadas aunque se invoquen directamente.
- La navegación conserva la página anterior mientras consulta el nuevo offset. Una respuesta vacía con total positivo se trata como inconsistencia; un error de miembros no borra la página de grupos; los errores muestran el motivo y no se presentan como inventario vacío.
- Los límites anterior/siguiente usan predicados compartidos y el botón solo permite offsets válidos.

## Problemas resueltos en este hito

- **ThreadStateException OLE/ComboBox:** el punto de entrada anterior era `async Task Main`. El `await` de creación del demo podía reanudar en un hilo del pool MTA antes de crear Form1 y ejecutar `Application.Run`. Se convirtió `Main` en síncrono con `[STAThread]`; ahora `Application.Run` ocurre en ese hilo STA y la semilla se crea con async/await desde `Form1_Shown`. Hay guardas STA en el punto de entrada y el constructor de Form1.
- **Lista de grupos vacía durante/cerca de navegación:** el cargador borraba la fuente de datos y el estado visible antes de terminar la consulta; una respuesta cancelada/obsoleta podía dejar la tabla vacía. Además, el `catch` compartido para resumen y miembros borraba los grupos si fallaba la carga de miembros. Ahora se conserva la página anterior hasta confirmar el nuevo resumen; una página vacía con total positivo es error/inconsistencia, no “sin grupos”; los errores de miembros se aíslan y no vacían la lista.
- Las pruebas de navegación y persistencia son de servicio/SQLite con datos sintéticos; no simulan eventos visuales de DataGridView.

## VALIDADO

Validado desde PowerShell Windows local, sin abrir la interfaz normal ni contactar Drive:

- `dotnet build`: correcto, 0 errores y 0 advertencias.
- `dotnet .\bin\Debug\net10.0-windows\DriveDuplicateFinder.dll --verify-sqlite-infrastructure`: código 0.
- `WinFormsSta = true` en el hilo de entrada marcado STA.
- Migraciones 1–4, escaneo recuperable, persistencia de revisión e integridad del plan: verificaciones existentes correctas.
- Pruebas demo offline: base temporal distinta de la personal, identidad sintética, Full Completed abrible, tres grupos, navegación SQL 1→2→3→2→1, límites del primer/último grupo, miembro grande paginado 100/100/5, decisión conservada tras cambiar de grupo y regresar, y acciones heredadas bloqueadas.
- `git diff --check`: código 0. Git emitió avisos LF/CRLF en archivos preexistentes modificados.
- Las verificaciones crearon SQLite bajo `%TEMP%` y reportaron su eliminación. No se abrió ni modificó la base personal.

## PENDIENTE / LIMITACIONES

- No se automatizó ni ejecutó visualmente WinForms en esta sesión. En particular, el editor `DataGridViewComboBoxCell` debe confirmarse en el escritorio: no se afirma que la interacción visual esté validada, aunque el arranque STA quedó comprobado.
- Ejecutar manualmente `dotnet .\bin\Debug\net10.0-windows\DriveDuplicateFinder.dll --demo-review` y probar cambiar decisiones, páginas de miembros, grupos y plan. El demo no puede enviar nada a Drive.
- No activar limpieza real hasta completar una fase separada de preflight/revalidación remota y revisión manual.
- No se midió memoria o rendimiento con un inventario real de cientos de miles de archivos.

## Git y cambios pendientes

- Rama observada: `master`; HEAD observado: `0f3b8cd feat: añadir infraestructura SQLite para escaneo incremental`.
- Había muchos cambios sin commit antes de esta intervención; se conservaron. Los cambios relacionados con este hito incluyen `Program.cs`, `Form1.cs`, `Services/DemoReviewData.cs`, `Services/PagedRequestGeneration.cs`, `Services/RecoverableFullScanService.cs`, el nuevo `WinFormsStartupLocalChecks.cs` y este checkpoint.
- No se creó commit ni se hizo push. No se modificó v10.

## Riesgos y próximo paso recomendado

La comprobación SQL simula los offsets y la persistencia, pero no reproduce eventos reales de WinForms ni la inicialización OLE del combo. El siguiente paso es ejecutar el demo en escritorio interactivo y confirmar el ComboBox y la navegación visual con la aplicación aislada; mantener la ejecución real de limpieza deshabilitada.
