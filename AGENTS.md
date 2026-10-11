# Checkpoints de continuidad

1. Guardar todos los checkpoints del proyecto en `checkpoint/`.
2. Después de cada hito importante, crear una versión nueva y consecutiva (`CHECKPOINT_DESARROLLO v10.md`, `CHECKPOINT_DESARROLLO v11.md`, etc.). No crear un checkpoint por cada cambio menor.
3. Nunca sobrescribir versiones anteriores.
4. Tomar el checkpoint más reciente como referencia y contrastarlo con el código, el estado Git y las validaciones disponibles.
5. Documentar en cada checkpoint: objetivo del proyecto; arquitectura actual; funcionalidades implementadas; funcionalidades realmente verificadas; commits y cambios pendientes; decisiones técnicas importantes; problemas resueltos; riesgos y tareas pendientes; próximo paso recomendado.
6. Distinguir explícitamente entre **IMPLEMENTADO**, **VALIDADO** y **PENDIENTE**.
7. No inventar resultados de pruebas ni información que no esté disponible. Identificar claramente la fuente y el alcance de la evidencia.
8. No guardar secretos, credenciales, tokens OAuth ni datos personales.
9. Crear checkpoints solo tras hitos relevantes, no por cada modificación pequeña.
10. Escribirlos para que otra conversación de Codex pueda continuar el desarrollo sin perder contexto.
11. No crear commits ni hacer push sin autorización explícita.

## Compilación y verificación autónoma en Codex

- Para el proyecto actual `net10.0-windows`, el `dotnet build` predeterminado puede fallar durante la evaluación del SDK en `Microsoft.NET.Sdk.targets`: `ToolLocationHelper.GetPlatformSDKDisplayName(Windows, 7.0)` intenta leer el registro de Windows y el entorno restringido devuelve `MSB4184 / Requested registry access is not allowed`.
- En esta configuración se validó que el moniker calculado por el SDK es `Windows,Version=7.0`. Evitar la evaluación de esa etiqueta, sin cambiar el moniker efectivo, usando: `dotnet build --no-restore -p:TargetPlatformMoniker=Windows%2CVersion=7.0 -v:minimal`. No eliminar la propiedad codificada `%2C`; es el escape de la coma para MSBuild.
- Para `--verify-sqlite-infrastructure`, redirigir solo `TEMP`, `TMP` y `TMPDIR` del proceso a un directorio único bajo `obj/`, porque el TEMP virtual del sandbox puede denegar `File.Move` en pruebas locales. Ejecutar el verificador únicamente si el build termina con código 0. Al final, borrar solo ese directorio temporal si su ruta normalizada sigue dentro de `obj/`; no tocar la base personal ni otros temporales.
- Secuencia PowerShell validada (desde la raíz del repositorio):

  ```powershell
  $repo = (Get-Location).Path
  $obj = [IO.Path]::GetFullPath((Join-Path $repo 'obj'))
  $temp = [IO.Path]::GetFullPath((Join-Path $obj ('codex-validation-' + [guid]::NewGuid().ToString('N'))))
  New-Item -ItemType Directory -Path $temp | Out-Null
  $env:TEMP = $temp; $env:TMP = $temp; $env:TMPDIR = $temp
  try {
      dotnet build --no-restore -p:TargetPlatformMoniker=Windows%2CVersion=7.0 -v:minimal
      $buildExit = $LASTEXITCODE
      if ($buildExit -eq 0) {
          dotnet .\bin\Debug\net10.0-windows\DriveDuplicateFinder.dll --verify-sqlite-infrastructure
          $verifyExit = $LASTEXITCODE
      }
  }
  finally {
      $fullTemp = [IO.Path]::GetFullPath($temp)
      if ($fullTemp.StartsWith($obj + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
          Remove-Item -LiteralPath $fullTemp -Recurse -Force
      }
  }
  ```

- Procedimiento validado el 11/10/2026: build anterior con código 0, cero errores/advertencias; verificador con código 0 y todas las propiedades, incluida `PagedGroupCleanup`, verdaderas. La invocación exacta y sus límites están en `checkpoint/CHECKPOINT_DESARROLLO v16.md`.
- Interpretación: el MSB4184 anterior es un bloqueo de acceso del entorno a una consulta de registro del SDK, no un error de fuente. Los fallos posteriores de compilador o de las aserciones sí deben tratarse como errores reales. Este workaround está comprobado para el `TargetFramework` actual; si cambia el SDK o el target framework, volver a inspeccionar el moniker y revalidar antes de reutilizarlo.
- No usar GitHub Actions, runners externos ni modificar permisos/registro global. El ejecutable completo de Visual Studio MSBuild también reprodujo el MSB4184 en el entorno actual; cambiar de ejecutable por sí solo no es la solución.
