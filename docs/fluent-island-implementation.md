# Implementación de la revisión integral de Fluent Island

Fecha: 18 de octubre de 2026.

El código implementa el plan revisado con el usuario, incluyendo los segundos del temporizador y los contornos inconsistentes de las capturas. Se conservaron los cambios locales anteriores, la identidad pill/notch, las preferencias guardadas y la semántica del estante: aparcar traslada; quitar devuelve; no se borran archivos del usuario.

**Estado de validación:** comprobaciones estáticas y pruebas aisladas de disposición WPF realizadas. La compilación del proyecto, la suite completa, la comparación de rendimiento y la aceptación visual en la aplicación siguen pendientes. `AGENTS.md` reserva al usuario el build y la instalación y prohíbe iniciar la aplicación automáticamente. Los resultados históricos del informe anterior no validan esta revisión.

## Cambios y trazabilidad del plan

| Parte del plan | Implementación |
|---|---|
| 1–8: temporizador | Tres campos con identificación de unidad, rueda y arrastre; edición por teclado en apertura explícita. Preajustes en un desplegable compacto con menos de 360 DIP y en lista lateral a partir de ese ancho. Inicio deshabilitado con cero. Entrada limitada a dos dígitos por unidad y duración máxima de 24 horas; parser protegido ante desbordamientos. Pérdida de captura, cambio de pantalla, DPI, deshabilitación y cierre liberan el arrastre. |
| 9–15: política e interacción | Avisos ordinarios publicados como actividad; dictado activo y alarma resueltos por la política común. Una vista elegida o un gesto en curso conserva su contenido. Los avisos prevalecen sobre actividad ordinaria mientras estén vigentes, conservan su vencimiento y no se renuevan por repintar. Se restaura una vista expandida suspendida si sigue disponible, después de las exclusivas y del feedback de dictado. Reloj transcurrido independiente de ajustes de hora, incluyendo suspensión. |
| 16–21: portapapeles | Identidad SHA-256 de píxeles fuera del hilo de interfaz; originales admitidos íntegros, miniaturas separadas y vistas previas cortas. Presupuesto de 128 MiB y límite de elementos; se retiran los más antiguos o se rechaza el elemento demasiado grande con explicación. Escritura propia identificada por marcador; secuencia comprobada antes de reintentar para no sustituir una copia posterior. Reintentos acotados. Confirmación de copia ligada al resultado real y conservada durante el feedback. Historial en memoria. |
| 22–28: archivos y aplicaciones | Traslados serializados en segundo plano, cancelación de elementos pendientes, estado de avance y errores por elemento. El arrastre sobre la isla comprueba formato y capacidad sin consultar el disco en cada movimiento. Intención durable antes del movimiento; reconciliación de metadatos al iniciar. No hay sustitución de archivos ni fallback automático de copiar y borrar. Las aplicaciones se abren fuera del hilo de interfaz; un fallo mantiene la vista, muestra el motivo y permite reintentar o abrir ajustes. |
| 29–36: trabajo y estructura | Instantánea de contratos por decisión; miembros de pantallas memorizados hasta el siguiente cambio. Distribución por widget: anchos propios y separación uniforme, sin igualar columnas ni usar desplazamiento horizontal. La pantalla se mide con sus controles montados al ancho final antes de abrirse; no se construyen copias ni se anima la altura por separado. Se conservan columnas y paneles cuando la composición coincide. Lecturas y preparación de metadatos asíncronas, eventos coalescidos por sesión y comprobación de identidad tras las esperas. Ajustes agrupados en un turno y sin espera desde hilos externos. |
| 37–43: diseño y acceso | Estilo común de mosaicos con contorno y recorte de contenido redondeado. Contorno del contenedor dibujado con su misma geometría de recorte. Recursos semánticos de texto, opacidad y superficies; contraste reforzado y adaptación al alto contraste. Fuentes personalizadas conservadas y heredadas por el contenido. Columnas de al menos 200 DIP con desplazamiento horizontal cuando haga falta; contenido alto desplazable. Rueda interior protegida. Superficies con patrón Invoke, nombres, foco y activación por teclado; apertura explícita desde la bandeja. Movimiento del sistema respetado y fondo detenido en alto contraste. |
| 44–50: servicios y ajustes | Clima con carga, error, ausencia de datos y antigüedad del último dato válido; coordenadas validadas, incluido 0,0. Búsquedas canceladas al abandonar la página y respuestas comprobadas contra búsqueda y configuración actuales. Calendario con una lectura concurrente como máximo, publicación versionada, instantánea estable y estado de sincronización. Bluetooth/cargador usan el buzón de avisos. Traducción de funciones visibles y condición meteorológica desde su código WMO. Categorías de ajustes cargadas desde plantillas diferidas y controles de detalle agrupados en Avanzado. |

El modo heredado de ajustes «All» sigue cargando todas las categorías por compatibilidad; una categoría concreta solo construye su propio cuerpo.

## Correcciones adicionales de coherencia

- Los preajustes no compiten por espacio con los segundos. La distribución se establece al ancho final antes de iniciar la apertura.
- La política distingue un cambio hacia o desde una exclusiva aunque coincidan el identificador y el estado expandido. Una alarma puede sustituir la configuración abierta de ese mismo temporizador.
- Actualizar metadatos musicales no adopta ni cambia la pantalla que el usuario abrió.
- Los paneles guardan su ubicación original antes de mover el primero. Esto evita invertir su orden al volver de una pantalla combinada.
- El título del lugar y su hora tienen columnas separadas; la sincronización del calendario ocupa su propia línea.
- La confirmación de copia no desaparece cuando llega la actualización encolada del historial.
- La barra musical usa una cadencia transcurrida y una referencia local al extrapolar, reduciendo saltos por cambios del reloj civil.

## Estante: recuperación y compatibilidad

Los registros se guardan en la subcarpeta `.operations` de la carpeta existente del estante. Cada intención contiene origen, destino, origen de devolución y tipo. Se escribe y confirma antes de llamar al traslado nativo. Los movimientos de carpetas entre volúmenes no admitidos por Windows fallan conservando el origen; no se implementa una eliminación posterior a una copia.

Al recuperar, solo la intención más reciente de cada ruta aparcada decide sus metadatos. Una ruta aparcada existente vuelve a la colección; una ruta ya devuelta deja de figurar. Si existen ambas ubicaciones, se conservan ambas y se comunica la ambigüedad. Si faltan ambas, se comunica que hace falta revisión. La recuperación no mueve ni elimina archivos del usuario.

Los registros se retiran únicamente cuando los metadatos completos del estante coinciden con los ajustes realmente escritos en disco y el estado de los archivos es inequívoco. Si el guardado sigue pendiente, los registros permanecen para la siguiente recuperación.

## Comprobaciones disponibles

Desde la raíz del proyecto, la comprobación que no compila ni inicia la aplicación es:

```powershell
.\tools\CheckIslandSource.ps1
git diff --check
```

Comprueba sintaxis C# con el analizador de PowerShell, XML, existencia de manejadores de los eventos y recursos Island del diccionario base, además de claves duplicadas y referencias a las APIs sustituidas en esta revisión. No sustituye una compilación ni verifica tipos, enlaces generados o composición visual.

Resultado de esta entrega: sintaxis de **54 archivos C#**, XML de **5 archivos**, **114 manejadores** y referencias de recursos correctos. `git diff --check` sin errores. No se ejecutaron la aplicación, la compilación ni los modos WPF de regresión.

Para ejecutar **por el usuario después de compilar**, se ampliaron los modos existentes:

```powershell
dotnet run --project .selfcheck -c Release
dotnet run --project tools/DictationChecks -c Release -- --island
dotnet run --project tools/DictationChecks -c Release -- --visualizer
```

Las regresiones nuevas cubren distribución real del editor a nueve anchos durante cien cambios por valor; segundos 01/30/45/59; parser y reloj inyectado; imágenes del mismo tamaño con píxeles distintos; texto de 30.000 caracteres; cuota de memoria con presupuesto reducido; escritura ocupada y correcta sin escribir en el portapapeles real; archivos temporales con registro previo y devolución sin sobrescritura; contorno/recorte del mosaico, patrón Invoke y restauración del orden de paneles. Las pruebas puras incluyen protección de una interacción compacta, prioridad de avisos y descarte de exclusivas no disponibles.

## Distribución vigente tras la última captura

El usuario revocó la uniformidad de dimensiones: cada widget debe conservar sus proporciones. Se retiró el ancho común y el reparto por columnas iguales.

- Pantallas combinadas: anchos propios para música, temporizador, aplicaciones, archivos y clima. Separación de 14 DIP y padding lateral de 14 DIP. La composición usa un StackPanel horizontal: ningún widget puede saltar debajo de otro. Si falta ancho se reducen proporcionalmente los anchos preferidos, descontando los espacios entre widgets. El antiguo ScrollViewer horizontal se sustituyó por un Border; ya no hay barra lateral que capture la navegación.
- Tamaño: se mide únicamente el contenido montado de la pantalla abierta, al ancho final y antes de empezar la animación. La altura configurada es un mínimo; no recorta el inicio de widgets más altos. Se conserva una sola animación de expansión, sin seguimiento independiente del alto ni medición al terminar la animación.
- Navegación: el viewport exterior no se considera una lista interna que impida cambiar de pantalla con la rueda. Listas interiores y dígitos conservan su gesto propio.
- Música: se restituyen la carátula de 48 DIP, los controles de reproducción de 32/36 DIP y la separación original de la barra temporal.
- Temporizador: cada TextBox usa una plantilla explícita sin barras horizontales ni verticales. La rueda se atiende en Preview sobre los dígitos, antes de su ScrollViewer interno. Se conserva el desplegable compacto de preajustes y la edición H:M:S.

Comprobaciones WPF aisladas ejecutadas sin compilar ni iniciar FluentFlyout: los tres campos muestran ambos scrollbars en estado Collapsed y permanecen dentro de la columna de 200 DIP; el editor mide 100 DIP. Una fila representativa de archivos, aplicaciones y clima usa anchos de 96, 192 y 236 DIP, con 552 DIP de ancho total y 100 DIP de alto, sin ScrollViewer horizontal. La suite preparada comprueba la ausencia de barras internas y del contenedor horizontal. También verifica la fila real del XAML a 252, 400, 552 y 800 DIP: todos los widgets mantienen y=0, sin solaparse ni exceder el ancho; la fila mantiene 100 DIP de altura con contenido de prueba. Esa disposición se comprobó además con WPF aislado.

## Aceptación local pendiente

- [ ] Compilar WPF x64 y, donde esté disponible, ARM64; ejecutar los modos anteriores. Corregir cualquier problema de tipos o generación XAML antes de considerar entregada una versión ejecutable.
- [ ] En pill y notch, ajustar e iniciar `00:00:01`, `00:00:30`, `01:01:45` y `24:00:00`; comprobar cero, límites, rueda, arrastre, pérdida de captura y teclado.
- [ ] Repetir expansión/contracción y navegación con una a cuatro funciones, anchos mínimos y escalas 100/150/200 %. Comprobar segundos, contornos, esquinas, títulos y barras de desplazamiento con portadas claras y oscuras.
- [ ] Abrir desde la bandeja con teclado; recorrer controles con Tab, activar con Enter/Espacio y salir con Escape. Contrastar foco, lector de pantalla, alto contraste y movimiento reducido, sin robo de foco en la interacción ordinaria.
- [ ] Probar música con dos sesiones, cambios rápidos, pausa y seek mientras llegan respuestas; dictado con alarma pendiente y restauración de la vista anterior.
- [ ] Probar clima sin red, cambio de lugar durante una petición y salida de ajustes durante búsqueda; calendario con cierre de sesión durante sincronización; conexiones Bluetooth y cargador durante una expansión.
- [ ] Probar recuperación del estante solo con archivos desechables: traslado interrumpido, origen ausente, nombre ocupado, lote parcial y cancelación. Confirmar persistencia tras reiniciar la aplicación por decisión del usuario.
- [ ] Comparar CPU, memoria, tiempos de cuadro y recursos en reposo, reproducción, cuatro columnas y cambios de portada con una versión anterior disponible y la misma configuración. No se ha medido ni se afirma un porcentaje de ahorro.

## Diagnóstico de rendimiento

La recuperación existente de cinco segundos registra un hito numérico `IslandPerformance`, sin añadir otro reloj ni incluir títulos, texto copiado o rutas. En ese hito: `Generation` cuenta reconciliaciones del intervalo; `DurationMs` es su coste máximo; `X` cuenta medidas nuevas; `Y` cuenta cuadros del motor geométrico; `Width` y `Height` son tamaños de las cachés de medidas y fondos; `Dpi` indica visibilidad (0/1) y `Phase` si el motor geométrico sigue activo (0/1). Son métricas de la isla; no miden por sí mismas tiempos de composición GPU ni consumo de todo el proceso.

En reposo estable, el motor geométrico debe dejar de producir cuadros. Una vista oculta no debe sostener visualizador ni rotación. Las muestras generales de recursos siguen en el diagnóstico acotado ya existente. El contraste físico de suspensión, DPI, dispositivos y renderizado continúa siendo necesario.
