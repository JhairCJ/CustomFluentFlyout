# Auditoría y mejoras de Fluent Island

> Informe histórico de la revisión anterior. La implementación integral posterior y sus comprobaciones pendientes se describen en [fluent-island-implementation.md](fluent-island-implementation.md). Los resultados de compilación y ejecución que siguen no corresponden a esa nueva revisión.

Fecha: 18 de octubre de 2026. Alcance: motor de presentación, animación y ciclo de vida; música y controles; temporizador; pantallas combinadas; aplicaciones, estante y portapapeles; calendario, Bluetooth, cargador, dictado, clima y fondo.

Se conserva la estética existente: pill/notch, proporciones, fuentes, rebote, recorrido de expansión, portadas y configuraciones. Sin dependencias nuevas. La autorización del usuario para esta revisión permite compilar y ejecutar comprobaciones locales.

## Plan aplicado

1. Hacer deterministas las decisiones de presentación y las transiciones de actividad.
2. Retirar las consultas nativas periódicas del pintado de la reproducción.
3. Ajustar el trabajo a la visibilidad y al contenido que necesita actualizarse.
4. Limitar y versionar el trabajo asíncrono; asegurar el cierre y las respuestas tardías.
5. Conservar las superficies visuales, corregir interrupciones y añadir regresiones.

## Hallazgos y cambios

| Prioridad | Hallazgo | Cambio implementado |
|---|---|---|
| P1 | El progreso y las capacidades consultaban al reproductor desde el hilo de interfaz. | Una lectura asíncrona compartida obtiene ambos; el pintado extrapola una instantánea local. |
| P1 | Un resultado del reproductor podía pertenecer a una sesión que ya no se mostraba. | Versión y comprobación de identidad antes de publicar; una lectura simultánea como máximo. |
| P1 | Clima de una ciudad anterior podía sobrescribir el lugar actual. | Generación por configuración, cancelación y validación tanto en servicio como en interfaz. |
| P1 | Una alerta nueva podía desplazar un dictado activo. | Prioridad explícita del dictado y protección de la entrada directa de la alerta del temporizador. |
| P1 | Reiniciar un temporizador desde alerta podía perder su actividad durante una reconciliación. | Retirar la exclusiva antes de publicar el estado vivo que la sustituye. |
| P2 | La integración numérica del rebote variaba con los FPS. | Solución exacta del muelle amortiguado con los coeficientes originales; incluye amortiguación crítica y sobreamortiguada. |
| P2 | Altura y hover dependían del tamaño del paso del renderizado. | Suavizado exponencial dependiente del tiempo; soporte de frecuencias superiores a 240 Hz. |
| P2 | Notificaciones repetidas del mismo cuadro podían duplicar trabajo. | Ignorar marcas de tiempo de renderizado ya procesadas. |
| P2 | Re-presentar el compacto estabilizado despertaba el renderizado. | Solo registrar cuadros si la geometría, hover o pulso aún deben avanzar. |
| P2 | El refresco genérico seguía a la cadencia de 300 ms para contenidos distintos. | Progreso musical a 100 ms durante reproducción o 1 s en pausa, temporizador a 250 ms y calendario a 30 s; detenerlo sin contenido visible. En pantallas combinadas el reloj usa la menor cadencia necesaria y cada columna conserva su propio plazo de refresco. |
| P2 | La visibilidad real de la ventana no detenía todo el trabajo de presentación. | Resincronizar progreso, ecualizador y rotación al cambiar visibilidad y al terminar una transición. |
| P2 | Una medición de pantallas podía consultar música e iniciar fundidos invisibles. | La medición no inicia lecturas del progreso ni altera los relojes de entrada de los paneles del temporizador. |
| P2 | Repintar un panel interrumpía su fundido aunque siguiera visible. | Conservar la animación en curso; al terminar queda la opacidad local definitiva, sin callback obsoleto que elimine otra animación. |
| P2 | Las consultas simultáneas del clima duplicaban solicitudes. | Compartir la petición pendiente de la misma generación; capturar cancelación del ciclo completo. |
| P2 | Resultados de clima encolados antes de cambiar ajustes aún podían llegar a la UI. | Comprobar la instantánea vigente y el servicio/lugar después de cada espera. |
| P2 | Una portada preparada no identificaba toda su configuración. | Caché por portada, tamaño cuantizado y radio, con un máximo de seis texturas. |
| P2 | Cambios rápidos de portada o desenfoque iniciaban trabajos que ya no interesaban. | Un trabajador activo y una solicitud deseada; continuar con la última y descartar versiones antiguas. |
| P2 | El primer fondo preparado conservaba también el desenfoque en vivo. | Adoptar la textura preparada y retirar el efecto duplicado. |
| P2 | Interrumpir un fundido del fondo podía volver a la portada anterior. | Capturar la mezcla visible antes de continuar hacia la siguiente portada, manteniendo el ángulo de rotación. |
| P2 | Refrescar aplicaciones, estante o portapapeles reemplazaba sus listas compactas. | Reutilizar los contenedores cuando los elementos visibles y su orden no cambian. |
| P2 | El calendario reemplazaba/recreaba elementos durante actualizaciones generales. | Conservar ItemsSource y renovar las cuentas relativas por intervalos de 30 s. |
| P2 | Un recordatorio de calendario sustituía la pantalla expandida elegida. | Respetar la expansión, conservando el aviso compacto y su vencimiento en modo temporal. |
| P2 | El orden de exclusivas dependía de inserción; un aviso podía ocultar su exclusiva. | Selección por fecha con desempate estable; la exclusiva conserva prioridad dentro de su funcionalidad. |
| P2 | Una funcionalidad eliminada podía conservar una expansión sin contenido. | Validar disponibilidad y pertenencia a una pantalla expandible al resolver la vista. |
| P2 | Algunos eventos esperaban sincrónicamente a la interfaz o sobrevivían al cierre. | Ruta común de encolado con guardas de cierre para servicios, medios y eventos del sistema; cancelar esperas de hover y feedback. |
| P2 | Recursos y suscripciones se liberaban en puntos diferentes del cierre. | Dispose retira calendario, progreso, temporizador, Bluetooth y cargador; invalida fondos pendientes y libera texturas. |
| P3 | El último instante de reproducción usaba hora local frente a otros eventos UTC. | Usar UTC de forma consistente. |
| P3 | El texto del progreso se formateaba en cada refresco. | Escribir tiempo y restante solo cuando cambia su segundo visible; invalidar tras arrastrar. |

## Límites preservados

- Sigue existiendo la recuperación general de 5 segundos y las alarmas puntuales de avisos/temporizador. Detener el renderizado no elimina esa red de recuperación.
- La música mantiene su selección explícita de sesión. Estado y metadatos aún usan sus lecturas memorizadas por reconciliación; no se ha reemplazado todo el gestor multimedia compartido.
- El visualizador de Island conserva sus 30 FPS. Los toggles independientes del widget y del fondo mantienen sus valores y funcionamiento.
- Pantallas combinadas conservan columnas y altura compartida. Aplicaciones y estante conservan lanzamiento, arrastre y persistencia; portapapeles mantiene su historial.
- No se han cambiado los permisos, ajustes guardados, autenticación de calendario ni el motor de dictado. La revisión no incluye tareas de accesibilidad.

## Verificación

- Compilación Release x64 de la aplicación y del ejecutable de comprobaciones: correcta. La compilación completa conserva las 42 advertencias previas, sin errores.
- `.selfcheck`: 16 bloques correctos, además de las comprobaciones de enrutamiento del dictado. Incluye trayectoria y velocidad del muelle a 30/60/144/240/360 FPS, inversión, duraciones de 30–1000 ms, recuperación de valores inválidos, política de presentación y extrapolación del progreso.
- `DictationChecks --island`: correcto. Comprueba refinamientos Bluetooth, geometría compacta nativa, movimiento hacia reposo, reutilización de listas, color/opacidad de la mezcla interrumpida y carreras del clima con respuestas controladas, cancelación y cierre, sin consultar la red.
- `DictationChecks --visualizer`: correcto. Comprueba decaimiento durante recuperación, reposo, invalidación, autoocultado, ausencia de procesamiento oculto y cancelación, incluido el preset compartido de Island. Este modo independiente evita necesitar un host de Explorer.
- `DictationChecks --taskbar`: las comprobaciones del visualizador pasan; la integración posterior falla en este entorno porque el host de prueba no queda adjunto como hijo de Explorer. No se considera validada esa integración.
- `git diff --check`: correcto.

La instancia instalada no se ha sustituido. No se ha medido una reducción porcentual de CPU/GPU/RAM ni se afirma ausencia absoluta de bugs. Quedan como comprobaciones de hardware: suspensión/despertar real, Bluetooth/desconexión de audio, varios monitores/DPI y barra autoocultable. Para medir ahorro deben compararse muestras equivalentes antes/después en el mismo equipo.

## Próximas mejoras que requieren evidencia adicional

1. Medir el coste de la altura compartida con muchas pantallas; introducir caché solo si la medición identifica un coste significativo y una invalidación fiable.
2. Registrar tiempos del gestor multimedia durante desconexiones reales antes de trasladar también sus lecturas de estado/metadatos a otro servicio.
3. Validar la composición GPU y los fundidos en la instalación real, especialmente tras el fallo de renderizado observado anteriormente. Estos cambios no prueban por sí mismos la causa de aquel cierre.

Estas propuestas quedan documentadas; no se añaden otra caché general, dependencias ni cambios de renderizador sin datos que los justifiquen.
