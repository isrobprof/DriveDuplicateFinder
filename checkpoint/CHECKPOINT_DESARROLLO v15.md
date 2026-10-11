# DriveDuplicateFinder — CHECKPOINT_DESARROLLO v15

> Hito: primer escaneo Full recuperable y primer preflight real de solo lectura comunicados por el usuario. Fecha reportada: 10/10/2026. Estas observaciones visuales son evidencia del usuario, no automatización ejecutada por Codex.

## Objetivo del proyecto

Aplicación WinForms C#/.NET 10 para detectar duplicados exactos de Google Drive, persistir inventarios/revisiones en SQLite local y controlar con seguridad cualquier futura limpieza.

## Arquitectura actual

- WinForms .NET 10, SQLite con migraciones 1–4 y persistencia local del estado.
- OAuth separado: análisis con `DriveService.Scope.DriveMetadataReadonly`; limpieza con `DriveService.Scope.Drive` y almacén/token independientes.
- Escaneo Full recuperable por páginas `Files.List`, con persistencia transaccional página+checkpoint.
- Duplicados exactos por tamaño y MD5; consulta paginada de grupos y miembros.
- Revisión persistente por inventario/grupo/FileId y vista paginada.
- `PersistedCleanupGroupReviewAdapter` prepara un grupo SQLite confirmado para el contrato legado, límite 2.000 miembros.
- `PagedGroupPreflightService` realiza preflight de un solo grupo y descarta su resultado si cambia la revisión.
- `--demo-review` utiliza datos sintéticos y SQLite temporal.

## IMPLEMENTADO

- Escaneo recuperable y presentación paginada.
- Decisiones locales `Keep`, `CandidateForTrash` y `Undecided`, confirmación explícita y persistencia SQLite.
- Preflight paginado de solo lectura por grupo; no habilita ejecución.
- Limpieza heredada conserva su implementación existente. No se ha conectado desde la interfaz paginada.

## VALIDADO

### Validación automatizada offline

Según el log de Windows leído en el hito v14, ejecución `29484-1061` del 10/10/2026 19:42:44: build con 0 errores/advertencias y `--verify-sqlite-infrastructure` con código 0. Incluyó migraciones 1–4, persistencia/reanudación, adaptador, aislamiento y pruebas de preflight sintético, incluida invalidación por cambio concurrente. No equivale a un preflight real.

### Observación visual reportada por el usuario (10/10/2026)

El usuario comunicó haber comprobado en la aplicación normal:

- Escaneo Full completado y resultados paginados visibles. Se omiten las cantidades exactas del inventario por privacidad.
- La interfaz permaneció responsive; se omiten las mediciones exactas de memoria.
- Inventario SQLite creado y resultados paginados visibles.
- Primer preflight real exclusivamente de lectura reportado por el usuario sobre un grupo pequeño: una copia quedó como Keep y otra como Candidate. El usuario reportó resultado APROBADO, sin bloqueos. Se omiten nombres y tamaños de archivos por privacidad.
- El usuario confirmó que no envió ningún archivo a la papelera.

Los nombres, cantidades, consumo de RAM y resultado del preflight anterior son observaciones comunicadas por el usuario, no mediciones verificadas por Codex ni pruebas automatizadas. No se registran IDs, correos, rutas privadas, tokens ni credenciales.

## Estado al crear v15: pendiente integración segura de limpieza por un grupo

Objetivo de la siguiente intervención: reutilizar el adaptador, preflight, historial obligatorio y `GoogleDriveTrashService.ExecuteAsync` para un único grupo confirmado. Aún no declarar implementada ni validada esta integración.

Condiciones antes de cualquier futura escritura:

- Revalidar Full Completed vigente, cuenta/alcance/ScanId, grupo y decisiones actuales.
- Proteger todos los Keep; incluir únicamente candidatos explícitos y excluir `Undecided`.
- Respetar el máximo de 2.000 miembros del adaptador y los límites del servicio actual.
- Repetir preflight remoto después de la confirmación final e inmediatamente antes de la operación; descartar cambios locales/remotos.
- Historial obligatorio, resultado incierto sin reintento automático e invalidación conservadora del inventario/confirmación.
- OAuth de análisis y de limpieza separado.
- La ejecución debe permanecer inaccesible desde botones, menús y manejadores durante desarrollo y pruebas.
- La única operación futura permitida es el mecanismo existente `Files.Update(... Trashed = true ...)`; nunca `Files.Delete` ni `EmptyTrash`.

## Riesgos y tareas pendientes

- No se ha repetido el escaneo masivo ni el preflight real en esta conversación; la evidencia real descrita arriba es del usuario.
- El preflight real tuvo éxito para un grupo pequeño según el usuario. No se ha probado ningún envío real por el flujo paginado.
- Antes de integrar ejecución, auditar qué contrato del servicio existente limita a un Keep, cómo se conservan múltiples Keep en la confirmación/historial y cómo se revalida la decisión inmediatamente antes de escribir.
- Ejecutar verificador offline desde Windows después de implementar; Codex no debe afirmar éxito antes de leer los códigos reales del lanzador.

## Git y continuidad

- Rama `master`; HEAD publicado conocido `643d6ca3b771226763cbc0addf083dbd11a73264` (`feat: integrar adaptador SQLite y preflight de solo lectura por grupo`).
- Al comenzar el hito no había cambios funcionales pendientes. Se conservan los siete checkpoints históricos locales sin seguimiento.
- No crear commit ni push sin autorización explícita.

## Próximo paso recomendado al crear v15

Auditar e implementar el coordinador offline de limpieza futura para un grupo, reutilizando los servicios actuales; mantener la ruta de ejecución sin conexión desde la UI y validar con SQLite/HTTP simulados antes de considerar cualquier activación.

## Actualización de implementación de esta intervención (10/10/2026)

- **IMPLEMENTADO, pendiente de validación local:** se añadió `PagedGroupCleanupService` como coordinador de un solo grupo. No está conectado a `Form1`, menús, atajos ni manejadores; la ruta paginada visible sigue sin poder ejecutar limpieza.
- La preparación requiere una sesión `CleanupDriveSession` creada por `GoogleDriveCleanupAuthService.ConnectSessionAsync`, verifica cuenta/alcance e inventario, exige el preflight previo vigente y repite lecturas remotas con la autorización de limpieza.
- Se añadió un preflight específico de revisiones persistidas: comprueba todos los Keep, consulta únicamente candidatos explícitos, excluye por completo los Undecided y conserva el límite de candidatos existente. El contrato legado no se relajó.
- La confirmación es un token de un solo uso ligado al plan y a las listas exactas Keep/Candidate. Antes de delegar en el `GoogleDriveTrashService.ExecuteAsync` existente, se repite el preflight, se comparan los metadatos remotos y se valida e invalida atómicamente la instantánea SQLite para cerrar la carrera con cambios locales concurrentes.
- El historial registra todos los Keep y los candidatos explícitos. No se añadieron migraciones. `Files.Delete` y `EmptyTrash` no se implementan.
- Se ampliaron las comprobaciones offline para preparar el plan con HTTP simulado, múltiples Keep, excluir Undecided y rechazar un intento de escritura simulado sin reintento. **Estas comprobaciones todavía no se han ejecutado**; requieren el lanzador local de Windows. Ningún preflight, OAuth o escritura real se ejecutó para esta implementación.
- Estado pendiente: build y `--verify-sqlite-infrastructure` desde Windows. No crear v16 ni considerar habilitada la limpieza paginada hasta revisar el log y validar el código.

## Próximo paso actual

Ejecutar el lanzador de validación en PowerShell interactivo de Windows, revisar build y todas las propiedades del verificador (incluidas las nuevas de limpieza). Corregir solo errores reales. Si pasa, actualizar el checkpoint consecutivo en una intervención posterior; mantener la ruta paginada sin UI de ejecución real hasta una autorización separada.
