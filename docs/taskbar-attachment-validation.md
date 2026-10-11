# Fijación del widget

Referencia portada: [FluentFlyout oficial, revisión efbdc8e](https://github.com/unchihugo/FluentFlyout/blob/efbdc8e351df4a39ed5713da07239c0f69acfb19/FluentFlyoutWPF/Windows/TaskbarWindow.xaml.cs). El compacto elimina `WS_POPUP`, añade `WS_CHILD` y usa `SetParent`. Se retiraron propietario nativo y hooks de orden. Las consultas de UI Automation continúan siendo asíncronas.

Los bounds del frame se leen en píxeles físicos de pantalla; `ScreenToClient` convierte su origen respecto al padre. El canvas compacto conserva coordenadas locales del frame. No ocupa una banda fuera de la barra. La expansión tiene un HWND independiente, sin activación, dimensionado por la unión de inicio y destino; al terminar adopta el destino exacto. Su canvas convierte desde el mismo origen del frame. Una sola instancia del control se transfiere y su propietario libera los recursos al destruirla, no en el `Unloaded` de transferencia.

## Checks ejecutables

Desde `CustomFluentFlyout/`:

```powershell
dotnet run --project tools/TaskbarChecks -c Release
dotnet run --project tools/DictationChecks -c Release -- --taskbar
dotnet run --project tools/ArtworkChecks -c Release
dotnet run --project .selfcheck -c Release
```

Los doubles comprueban padre válido/ausente/reemplazado, idempotencia y origen negativo con escalas 100%, 125% y 200%. El check WPF crea un hijo real de Explorer y un popup de expansión; verifica foco conservado, transferencia única, inversión de la animación, commit pendiente, ambos estilos, recursos de fondo, unión y destino exacto, supresión de fullscreen simulada, refresh de geometría y una única solicitud de recuperación. Usa ajustes efímeros y portadas sintéticas; no reinicia Explorer ni modifica ajustes guardados.

## Matriz interactiva

### Regresión de contracción corregida

El retorno ocultaba el popup y confiaba en `CalculateAndSetPosition` para restaurar la región del hijo compacto. Esa llamada puede quedar diferida por interacción del shell o por `_positionUpdateInProgress`. Durante expansión, el hijo conserva únicamente la región del visualizador: devolver el control sin restaurarla deja el widget recortado aunque el HWND siga visible. El timer tampoco lo repara necesariamente, porque omite el cálculo cuando la geometría y el flag de visibilidad no cambian.

La regresión fuerza una actualización de posición ocupada durante el retorno. Antes de corregir, falla con `Compact native region clips the returned widget`; después, el compacto restaura región, geometría y orientación vigentes antes de ocultar el host expandido. La corrección conserva `WS_CHILD`/`SetParent` y el orden de ventanas existente. Demuestra un caso de recorte que se puede percibir como pasar detrás de la barra; no demuestra que toda ocultación tenga esa misma causa.

El modo `--taskbar` comprueba cien ciclos, ambos estilos, animación activa/inactiva, inversiones rápidas y cambios de canción/ancho. Comprueba puntos de la región con `GetWindowRgn`/`PtInRegion`, interacción nativa con `WindowFromPoint` y tres marcadores temporales sobre el compacto en una captura real del escritorio. La captura usa `BitBlt` con `CAPTUREBLT` para incluir superficies transparentes. Los marcadores se retiran al terminar; no se simulan clics ni se escribe texto. También comprueba retorno con autoocultación por pausa y supresión/restauración de fullscreen simuladas.

Evidencia: [compacto tras retornar, con tres marcadores de prueba](audits/collapse-return-desktop.png). El control usa canciones y portadas sintéticas; la captura se limita al rectángulo del compacto. La matriz interactiva restante sigue pendiente.

| Caso | Resultado registrado / pendiente |
| --- | --- |
| Apertura/cierre e inversión de expansión | Check WPF real aprobado; mismo control y foco conservado |
| Publicación durante transferencia | Check aprobado; última identidad y portada conservadas |
| Fundido/slide, marquee y fondo giratorio | Check WPF aprobado; revisión visual completa pendiente |
| Origen negativo / DPI mixto | Doubles aprobados; traslado entre pantallas físicas pendiente |
| Fullscreen y cambio de geometría | Simulación aprobada; interacción con aplicaciones fullscreen pendiente |
| Inicio, búsqueda, menús, Alt-Tab, Mostrar escritorio | Pendiente de prueba interactiva con el binario nuevo |
| Barra con autoocultación | Pendiente de prueba interactiva; no se cambiaron opciones de Windows |
| Reiniciar Explorer | Recuperación única comprobada con señal simulada; reinicio real pendiente |
| Windhawk / Nilesoft / otras barras | Pendiente en equipos que tengan esas modificaciones |

En cada caso comprobar que el compacto sigue dentro de la barra, la expansión no se recorta, los botones y menú funcionan, no cambia el foco y una canción nueva no aparece a medias. Repetir con artista igual/distinto, título corto/largo, Anterior/Siguiente y saltos rápidos. Registrar Windows, DPI, monitores, estilo, ajustes de scroll/fondo, hora y resultado; no marcar casos no ejecutados como aprobados.
