# DriveDuplicateFinder — CHECKPOINT_DESARROLLO v16

> Hito: diagnóstico y workaround autónomo de MSB4184, corrección de inicialización SQLite y validación offline completa de `PagedGroupCleanupService`. Fecha: 11/10/2026.

## Objetivo

Aplicación WinForms C#/.NET 10 para encontrar duplicados exactos en Google Drive, mantener inventario y revisión paginados en SQLite, y desarrollar un flujo de limpieza seguro que permanezca deshabilitado hasta validar explícitamente cada fase.

## Arquitectura actual

- WinForms `net10.0-windows`, `UseWindowsForms=true`, SDK .NET 10. SQLite local con migraciones versionadas 1–4.
- OAuth de análisis y limpieza separados. El análisis usa `DriveMetadataReadonly`; la limpieza usa el servicio y ámbito existentes.
- Escaneo Full recuperable por páginas y checkpoint transaccional. Duplicados exactos por tamaño y MD5; resúmenes, miembros y decisiones se consultan paginados.
- Revisión persistente por cuenta, alcance, ScanId, identidad estable de grupo y FileId.
- `PersistedCleanupGroupReviewAdapter` y `PagedGroupPreflightService` proyectan y comprueban un grupo confirmado. `PagedGroupCleanupService` coordina revalidación, confirmación de un solo uso, historial e invalidación antes de delegar en el `GoogleDriveTrashService` existente.
- La interfaz paginada sigue siendo de solo lectura respecto a limpieza y no referencia el coordinador de ejecución.

## IMPLEMENTADO

- Coordinador de limpieza paginada offline con snapshots exactos, preflight repetido, todos los Keep protegidos y solo candidatos explícitos.
- Límite de un intento HTTP para la escritura delegada por el coordinador, para evitar reintentar automáticamente un PATCH cuya respuesta pudo perderse.
- Resultado `OutcomeUncertain` persistido como `Failed` con `FailureStage=Files.UpdateOutcomeUncertain` y `FailureReason=OutcomeUncertain`; el inventario se invalida conservadoramente.
- `ReviewStateRepository.InvalidateInventoryIfGroupSnapshotMatchesAsync` configura ahora la collation y función SQL de comparación requeridas antes de validar miembros. Sin esa inicialización, la ruta de invalidación fallaba con `no such function: DOTNET_IS_NULL_OR_WHITESPACE`.
- El verificador offline de `PagedGroupCleanupService` se invoca desde `Program.VerifyAsync()` bajo la propiedad `PagedGroupCleanup`.
- `AGENTS.md` documenta el workaround de compilación y el uso seguro de TEMP para futuras verificaciones autónomas.

## VALIDADO

### Causa y solución de MSB4184

- Proyecto: `DriveDuplicateFinder.csproj`, `net10.0-windows`; no se encontraron `global.json`, `Directory.Build.props` ni `Directory.Build.targets` en la raíz.
- Build predeterminado de SDK 10.0.401 falló durante la evaluación en `Microsoft.NET.Sdk.targets(1497,5)`: `[Microsoft.Build.Utilities.ToolLocationHelper]::GetPlatformSDKDisplayName(Windows, 7.0)`; mensaje exacto: `Requested registry access is not allowed.`
- El target framework abreviado hace que el SDK evalúe esa función para la etiqueta de plataforma; el acceso al registro lo deniega el entorno restringido. No es un error del código fuente ni una operación del proyecto. `dotnet --info` también mostró una denegación de Service Control Manager, que es una limitación separada.
- Pasar `TargetPlatformMoniker=Windows,Version=7.0`, el mismo moniker observado en la evaluación, evita la llamada innecesaria a la función. Se validó el escape MSBuild de coma `%2C` con:

  ```powershell
  dotnet build --no-restore -p:TargetPlatformMoniker=Windows%2CVersion=7.0 -v:minimal
  ```

- Resultado final tras las correcciones: código 0, 0 errores y 0 advertencias. También se probó `MSBuild.exe` completo de Visual Studio 18.9.1; reprodujo el mismo MSB4184 en este entorno, por lo que cambiar solo de ejecutable no resuelve el problema.
- Comparación: `Consigna` es WinForms `net8.0-windows10.0.17763.0`; un build de Codex reportado como exitoso usó Visual Studio MSBuild 18.9.1. El registro confirma PowerShell 7 y el ejecutable, pero no conserva prueba suficiente del token/permisos exactos de esa ejecución para afirmar equivalencia de sandbox. En Consigna, `dotnet` también tuvo fallos propios (`MSB4803` por `ResolveComReference`). La diferencia técnica relevante es el SDK/target y el uso de MSBuild completo, no evidencia de que DriveDuplicateFinder dependa funcionalmente del registro.

### Verificador offline

- La primera ejecución tras el build falló antes de las pruebas paginadas porque el TEMP virtual del sandbox no permitió `File.Move` en el historial sintético. No se cambió la base personal. Se redirigieron `TEMP`, `TMP` y `TMPDIR` a un directorio único bajo `obj/`, y se eliminó tras finalizar comprobando que permanecía dentro de `obj/`.
- El siguiente fallo real fue la función SQLite no registrada en `InvalidateInventoryIfGroupSnapshotMatchesAsync`. Se añadió `ConfigureComparison(connection)` en ese método; no se cambiaron SQL, reglas, esquema ni migraciones.
- Build final: `dotnet build --no-restore -p:TargetPlatformMoniker=Windows%2CVersion=7.0 -v:minimal`, código 0, cero errores y cero advertencias.
- Verificación final: `dotnet .\bin\Debug\net10.0-windows\DriveDuplicateFinder.dll --verify-sqlite-infrastructure`, con TEMP/TMP/TMPDIR redirigidos al directorio único bajo `obj/`; código 0.
- Todas las secciones del JSON fueron verdaderas: STA WinForms, infraestructura/migraciones 1–4, escaneo recuperable, persistencia de revisión, adaptador, preflight, vista paginada y aislamiento demo.
- `PagedGroupCleanup` pasó expresamente: cambios de cuenta/alcance/ScanId/sesión y decisiones rechazados antes de PATCH; preflight stale y cambio remoto rechazados; éxito de candidato único y múltiple; Keep/Undecided excluidos; resultado parcial, cancelación y respuesta perdida tras posible aplicación; rechazo de confirmación antigua; cero reintentos definitivos, inciertos o por cancelación; ruta de UI paginada sin ejecución.
- En el caso de respuesta perdida, el fake aplica potencialmente el cambio antes de lanzar, falla también la verificación GET y comprueba el estado incierto, no confirmado, además de que no se repita PATCH. Todo el transporte fue local simulado.
- `git diff --check`: sin errores de whitespace; Git mostró avisos de normalización LF/CRLF.

### Evidencia que no cubre este hito

- No se ejecutaron OAuth, escaneos, preflight remoto, `Files.Update`, `Files.Delete`, `EmptyTrash` ni limpieza real.
- La ejecución real del nuevo coordinador no se ha validado. La ruta paginada continúa desconectada de la UI y la limpieza real permanece deshabilitada allí.
- No se validó otra vez el escaneo masivo de más de 300.000 archivos; el alcance fue el verificador determinista local.

## PENDIENTE

- Antes de cualquier integración visible, autorización y validación separadas: auditar UI, confirmación humana, manejo de resultado incierto e historial; cualquier futura prueba real requerirá autorización explícita y un grupo pequeño.
- Mantener la protección contra escritura, no habilitar la limpieza paginada desde `Form1` por esta validación.
- Revalidar el workaround si cambia SDK/TargetFramework; si el moniker de Windows ya no es `Windows,Version=7.0`, no reutilizarlo a ciegas.

## Git y continuidad

- Rama `master`; HEAD publicado estable conocido: `643d6ca3b771226763cbc0addf083dbd11a73264`.
- Hay cambios pendientes de implementación y validación documentados en v15; este checkpoint y la actualización de `AGENTS.md` también quedan pendientes. Se preservan los siete checkpoints históricos sin seguimiento. No se hizo commit ni push.
- No se modificaron ni eliminaron checkpoints históricos.

## Próximo paso recomendado

Continuar con pruebas offline y revisión de seguridad del nuevo coordinador. Mantener la ejecución de limpieza paginada fuera de la UI y no confundir el éxito del transporte simulado con validación real de escrituras en Google Drive.
