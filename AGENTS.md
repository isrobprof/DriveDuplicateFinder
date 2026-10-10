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
