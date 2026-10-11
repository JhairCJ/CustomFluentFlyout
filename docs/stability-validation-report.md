# Informe de validación — fijación y dictado

Fecha: 11 de octubre de 2026. Cambio OpenSpec: `stabilize-taskbar-and-dictation`.

## Entrega

La fijación compacta usa el mecanismo oficial `WS_CHILD` + `SetParent`; la expansión utiliza una ventana independiente sin activación y transfiere el mismo control. El teclado tiene un hilo nativo propio; los gestos se procesan mediante una cola FIFO con generaciones. La captura espera `RecordingStopped` antes de liberar buffers, entregar audio o reabrir. Una parada sin confirmar conserva un único dispositivo en cuarentena.

Se añaden diagnóstico acotado y `--software-rendering` para contrastar cierres. El cierre aleatorio del Lenovo **no se declara resuelto**: falta ejecutar el binario nuevo en ese equipo y recopilar su evento de fallo.

Ejecutable local:

`FluentFlyoutWPF/bin/x64/Release/net10.0-windows10.0.22000.0/FluentFlyout.exe`

Para otro equipo, copiar **toda esa carpeta**, incluidas dependencias; el EXE por sí solo no basta. Mantener los ajustes y modelos existentes. Antes de abrir el binario nuevo, salir de la instancia anterior desde la bandeja.

SHA256 de `FluentFlyout.dll` después de la corrección de contracción: `FCD986A7FDD24992CF1F2E295E5042F40695D688BAF25531816B2ABE4F83EE65`.

## Resultados ejecutados

Comandos desde `CustomFluentFlyout/`:

| Comprobación | Resultado |
| --- | --- |
| `dotnet build FluentFlyoutWPF/FluentFlyout.csproj -c Release -p:Platform=x64` | Aprobado, 0 errores; compilación completa muestra 42 advertencias nullable preexistentes; último build incremental, 0 advertencias |
| `dotnet build FluentFlyout.sln -c Release -p:Platform=x64` | Bloqueado en MSIX: MSB4019, falta `Microsoft.DesktopBridge.props` en las herramientas instaladas |
| `dotnet run --project .selfcheck -c Release` | Router/ledger y 13 bloques existentes aprobados |
| `dotnet run --project tools/TaskbarChecks -c Release` | Fijación oficial y ciclo de vida de outside-click aprobados |
| `dotnet run --project tools/ArtworkChecks -c Release` | 51 comprobaciones aprobadas |
| `dotnet run --project tools/DictationChecks -c Release -- --taskbar` | Hijo real de Explorer, foco, transferencia, commit pendiente, ambos estilos, unión/destino, fullscreen/refresh y recuperación simulados aprobados |
| `dotnet run --project tools/DictationChecks -c Release -- --input` | Apertura bloqueada, liberación/deshabilitación antes de abrir, Escape, umbral, 100 gestos, pump independiente, excepción, envío parcial/denegado, recuperación y teardown aprobados |
| `dotnet run --project tools/DictationChecks -c Release -- --capture` | 100 paradas retrasadas, callback antiguo, error, cuarentena/confirmación tardía y watchdog/parada aprobados |
| `dotnet run --project tools/DictationChecks -c Release -- --capture-device` | 100 ciclos con micrófono real; 73 160 bytes PCM recibidos y eliminados; sin reconocimiento ni inserción |
| `dotnet run --project tools/DictationChecks -c Release -- --diagnostics` | Límites, contexto reciente tras rotación, fallo de escritura, clasificación, preferencias y arranque aprobados; modo normal |
| Mismo comando con `--software-rendering` | Aprobado en proceso independiente; modo software efectivo, preferencias iguales |
| `openspec validate stabilize-taskbar-and-dictation --strict` desde la raíz OpenSpec | Aprobado |
| `git diff --check` | Aprobado |

Los checks de entrada envían a una frontera falsa y no escriben en aplicaciones del usuario. Los checks WPF usan portadas sintéticas y ajustes efímeros; no reinician Explorer ni sustituyen la instancia abierta. El número de aperturas durante los 100 gestos rápidos puede variar según scheduling: el criterio es que ninguna generación antigua quede activa.

## Trazabilidad de requisitos

Los nombres corresponden a las tres specs delta del cambio.

| Requirement | Evidencia y límite |
| --- | --- |
| Fijación estable a la barra seleccionada | `AttachmentChecks` y `--taskbar`; interacciones con Inicio/menús pendientes |
| Recuperación de barra y monitor | Doubles de padre/origen/DPI y refresh/recuperación WPF; Explorer y monitores físicos pendientes |
| Expansión compatible con la fijación | `--taskbar`: mismo control, popup independiente, inversión y destino exacto |
| Publicación de identidad conservada | `ArtworkChecks` y transferencia con commit pendiente |
| Animaciones de canción conservadas | 51 checks de artwork, slide real durante transferencia y ambos estilos; inspección visual de toda la matriz pendiente |
| Fondo y tamaño sincronizados | Recursos de fondo y unión/destino verificados; geometría existente en `.selfcheck`; sincronía visual completa pendiente |
| Recuperación sin apropiarse del foco | Apertura real sin cambio de foreground; fullscreen y recuperación simulados |
| Entrada independiente de operaciones lentas | `--input`: apertura y llamador bloqueados 2 s; callback propio menor de 50 ms; excepción pasa al siguiente hook |
| Orden y vigencia de los gestos | FIFO real, 100 gestos y apertura lenta cancelada/liberada/deshabilitada; umbral de 350 ms y manos libres |
| Integridad de teclas consumidas y reproducidas | Router real/ledger, ambos órdenes/lados, parcial/denegado; reparación de un down Unicode y conservación de Enter físico |
| Recuperación tras pérdida de eventos | Reconciliador puro: ups perdidos, ambos órdenes y desktop clear; contraste Raw Input físico pendiente |
| Parada real antes de liberación o reapertura | 100 ciclos falsos retrasados y 100 ciclos reales; callback antiguo rechazado |
| Fallos y límites de captura recuperables | Cuarentena, rechazo de reapertura, confirmación tardía y error confirmado; driver/desconexión físicos pendientes |
| Inserción y cancelación coherentes | Envío falso, límite 5 s, copia de recuperación, cancelación sin escritura tardía |
| Desmontaje coordinado del dictado | Hook repetido y tareas controladas de carga/motor/captura; se espera trabajo activo antes de liberar recursos |
| Diagnóstico contextual y acotado | 64 hitos, 10 sesiones, cap de archivo, contexto reciente tras rotación, escritura fallida y clasificación render |
| Modo compatible de renderizado | Dos procesos separados; modo efectivo y serialización de preferencias idénticas |
| Dictado habilitado e inactivo | Spies: automático cero cargas/captura; mantener cargado inicia precarga, demora/fallo permiten entrada; sesiones prolongadas pendientes |
| Evidencia de validación reproducible | Este informe y protocolos enlazados; ejecución en Lenovo pendiente |

## Pendientes acordados

### Corrección posterior del retorno compacto

El reporte posterior del usuario reabrió la verificación de expansión. Se reprodujo un recorte nativo al contraer con la actualización de posición ocupada: el control volvía al hijo, pero su región aún excluía el widget. La prueba añadida falla en la implementación anterior y pasa al restaurar región, geometría y orientación compactas antes de retirar el popup. No fue necesario elevar el widget ni cambiar su fijación oficial.

Se ejecutaron cien ciclos con ambos estilos, animación activa/inactiva, inversiones rápidas y cambios de canción/ancho. Se comprobaron región nativa, puntos de interacción y píxeles reales del escritorio con tres marcadores temporales; [captura de evidencia](audits/collapse-return-desktop.png). También pasan los escenarios de pausa/autoocultación y fullscreen simulados. Los checks Taskbar, Artwork (51) y `.selfcheck` (13 bloques y router) se volvieron a ejecutar y aprobaron. El build WPF Release x64 termina con cero errores y las 42 advertencias nullable existentes.

Estos resultados identifican y corrigen el caso reproducido; la comparación interactiva con el entorno del usuario y el Lenovo continúa pendiente. Los resultados anteriores de dictado corresponden al mismo código de entrada/captura, que esta corrección no modifica. La revisión visual extensa y las sesiones prolongadas deben usar el ejecutable actualizado.

El usuario indicó **«yo lo haré»** para las sesiones prolongadas. La instancia anterior permanece abierta; no valida el código nuevo. No se ejecutaron los dos períodos de 30 minutos ni los cuatro de 10 minutos.

1. Ejecutar la matriz normal/software y dictado automático/deshabilitado/mantener cargado de [diagnóstico](application-stability-diagnostics.md).
2. Completar Inicio, búsqueda, menús, Alt-Tab, Mostrar escritorio, autoocultación, Explorer y monitores/DPI en [fijación](taskbar-attachment-validation.md).
3. Contrastar Raw Input, teclas consumidas, bloqueo/suspensión y continuidad de escritura con teclado físico en [entrada](dictation-input-validation.md).
4. Probar dictado real, desconexión/cambio de micrófono y cancelación durante transcripción en [captura](dictation-capture-validation.md).
5. Repetir en Lenovo con el mismo binario y registrar versión, configuración, duración y eventos Windows. Sin ese contraste no se atribuye su cierre a dictado o renderizado.
6. Compilar el paquete MSIX en un entorno con Desktop Bridge. El ejecutable WPF Release ya está compilado; no se produjo un paquete MSIX nuevo.

OpenSpec conserva **24/31 tareas completas** tras la corrección de contracción. Quedan abiertas 1.6, 2.5, 3.6, 5.1 y 5.2–5.4 por esas verificaciones o herramientas pendientes. El resultado inicial de 21/28 corresponde a la entrega anterior. El cambio no se archivó.
