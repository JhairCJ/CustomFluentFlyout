# Auditoría de rendimiento: taskbar widget e Island

Fecha: 2026-10-04. Base: af3a6d4.

Revisión estática de los cambios de expansión, controles y portada del widget, sus consultas a Windows y el indicador de dictado de Island. Se revisaron temporizadores, raíces estáticas, suscripciones, trabajos pendientes y asignaciones durante las animaciones. No se ejecutaron pruebas visuales ni de interacción, por petición del usuario.

## Hallazgos y correcciones

| Prioridad | Hallazgo | Corrección |
| --- | --- | --- |
| P1 | Las consultas UI Automation de la barra de tareas esperaban hasta 1000/500 ms en el dispatcher de WPF. Podían congelar ambas superficies, que comparten ese hilo. | Consulta asíncrona, una pendiente por elemento, resultados cacheados durante tres segundos y última geometría conservada durante una actualización. Resultados antiguos se descartan al cambiar de barra/monitor. Los trabajos pendientes usan una referencia débil a su ventana. |
| P2 | El panel expandido leía playback y timeline del reproductor cada 100 ms, incluso en pausa. Cada aviso adicional encolaba un Task antes de comprobar si había otra lectura. | Interpolación local a 100 ms mientras reproduce; lectura de respaldo a 1 s. Los eventos de reproducción/timeline y las acciones del usuario siguen solicitando lecturas inmediatas. Las ráfagas se agrupan antes de crear el trabajo. El temporizador baja a 1 s en pausa y se detiene al contraer, ocultar o cerrar. |
| P2 | El indicador de dictado consideraba Transcribing como captura activa y seguía despertando a 20 Hz. Podía continuar con su vista retirada. | El temporizador solo corre con fase Listening y tarjeta visible/habilitada. La visibilidad lo pausa/reanuda y el cierre desanexa su Tick y Changed. |
| P2 | Cada actualización del micrófono modificaba Height de doce Borders, invalidando el diseño de su StackPanel. | Geometría medida fija y ScaleTransform reutilizado para dibujar las alturas. Se conserva el rango visual de 4–20 DIPs y la cadencia de 20 Hz. |
| P2 | Play/pause usaban SymbolIcon estáticos. Un UIElement estático puede mantener su padre y un control/ventana antiguo alcanzables tras recrearlos. | Iconos reutilizados por instancia; se elimina esa raíz estática sin crear iconos por evento. Es un riesgo de retención identificado en código, no una fuga cuantificada con profiler. |
| P2 | La expansión creaba seis MatrixTransform y dos clips nuevos por actualización, además del clip exterior por cambios de tamaño. | Objetos mutables reutilizados durante la transición; identidad congelada se sustituye antes de modificarla. |
| P2 | Las portadas con fundido generaban bitmaps para superficies ocultas o capas de Island con opacidad cero. | Captura solo si la superficie y sus antecesores son visibles. Se consulta el estado de ambos fundidos para no interrumpir el visible. Los bitmaps se liberan al completar/cancelar. |
| P2 | Un bake pendiente podía terminar después del Unloaded e iniciar otra transición; el cierre directo de Island no garantizaba toda su limpieza. | Invalidación de la portada/caché al desmontar el widget. Island ejecuta Dispose al cerrar, de forma idempotente, y detiene los relojes de portada. |
| P3 | La barra de tiempo reestablecía sus límites y formateaba ambos textos en cada tick aunque no cambiaran. | Límites solo cuando cambian, y textos solo cuando cambia el segundo mostrado. |

La reducción de 10 a 1 lectura periódica por segundo es una propiedad del código, no una medición de mejora de CPU: los eventos legítimos añaden lecturas inmediatas.

## Rutas verificadas

- TaskbarWindow libera sus dos WinEvent hooks, el hook de clic exterior y sus temporizadores al cerrar. La interacción con Inicio mantiene la posición y conserva la propiedad de ventana usada para evitar que el widget quede detrás de la barra.
- TaskbarWidgetExpandedContent desanexa los eventos WinRT al desactivarse o cerrarse. El versionado evita aplicar lecturas de una sesión anterior.
- Island detiene CompositionTarget.Rendering al asentarse; no tiene un bucle por frame permanente en reposo. El cierre ahora también garantiza la limpieza completa de dictado y demás servicios.
- El fondo rotatorio ya se pausa mientras está oculto o en pausa. El widget limita su caché a seis fondos horneados de 256 px.
- DictationService limita su buffer de captura a dos minutos y detiene/libera el dispositivo y su watchdog. La retención del modelo depende de la política de recursos configurada; no se fuerza su descarga para evitar penalizar el siguiente dictado.

## Validación y límites

Compilación/publicación Release x64 y revisión de git diff --check. Graphify actualizado; su advertencia de parseo en MonitorUtil.cs:84 es previa y ajena a estos cambios. No se cambiaron modelos, runtimes CUDA ni motores de transcripción.

No hay medición prolongada de memoria/CPU ni prueba de captura/transcripción en esta auditoría. Por ello no se garantiza ausencia absoluta de fugas ni se atribuye un porcentaje de aceleración del dictado. La corrección de UI Automation debe observarse especialmente al mover el widget entre monitores o reiniciar Explorer, además de las transiciones y controles habituales.
