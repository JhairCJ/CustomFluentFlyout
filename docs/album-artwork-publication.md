# Publicación y animación de portadas

El widget de la barra de tareas y la isla usan la misma política, tanto en compacto
como en expandido. Título, artista, portada, fondo y acento se publican juntos;
reproducción, pausa y controles responden inmediatamente.

## Datos de Chrome y otros reproductores

- Una identidad nueva es sesión + título + artista. La miniatura no forma parte de
  la identidad de canción ni dispara por sí sola otro aviso de cambio de pista.
- Si la nueva identidad llega sin portada o con una portada visualmente similar a
  la anterior, se conserva el conjunto anterior durante una espera de 350 ms.
  Los duplicados no extienden ese plazo. Otra identidad reemplaza el pendiente.
- Una portada distinta completa el conjunto inmediatamente. Al vencer la espera
  se consultan de nuevo los metadatos de la sesión, con un límite adicional de
  250 ms para esa consulta. Si falla o falta portada, se publica la nota musical
  y se elimina el fondo y el acento del álbum anterior.
- Una consulta antigua no puede sobrescribir una canción nueva ni publicar después
  del cierre o la cancelación. Una imagen recibida después de un marcador entra
  con fundido; no vuelve a animar los textos ni genera otro giro.

## Comparación y transiciones

La comparación normaliza cada imagen a 9 × 8 píxeles y guarda una huella débil por
imagen, sin mantenerla viva fuera de sus consumidores. Se consideran similares si
la estructura difiere como máximo 6 de 64 bits y el error medio RGB es como máximo
0,06. La huella ignora diferencias menores de cuatro niveles de luminancia para
tolerar ruido de compresión; los colores conservan su distribución espacial.

- Primera portada válida: presentación directa.
- Portadas similares: presentación directa, sin giro ni fundido.
- Portadas válidas diferentes: giro, o fundido si ese es el estilo seleccionado.
- Entrada o salida de un marcador: fundido después de la primera portada válida.
- Animaciones desactivadas: actualización directa.
- Durante un giro se conserva solo el último destino. Antes de encadenar otro giro
  se compara ese destino con la imagen presentada. Duplicados no reinician los relojes.
- Ocultar, expandir o contraer conserva el historial de presentación.

Windows no garantiza que una miniatura repetida pertenezca a la identidad recién
recibida. La consulta posterior reduce la ambigüedad: una miniatura repetida en esa
consulta se acepta como portada compartida. Si Chrome sigue entregando datos
obsoletos, la aplicación no puede distinguirlos con certeza de una portada legítima.

## Color del visualizador de la barra de tareas

Con el widget habilitado, su portada efectivamente presentada es la fuente del
color del visualizador. El color cambia al mostrar la imagen nueva (a mitad del
volteo o al iniciar el fundido), conservándose mientras llegan metadatos pendientes.
La isla y otras sesiones no pueden sobrescribir ese acento. Una portada ausente
usa el acento del sistema; tema, desaturación y el interruptor de acento se aplican
al color conservado del widget. Con el widget deshabilitado, el visualizador
independiente conserva su fuente global anterior. El ecualizador de la isla
continúa utilizando su fuente existente.

## Comprobaciones

`dotnet run --project tools/ArtworkChecks -c Release` comprueba la comparación,
publicación atómica, portadas ausentes/tardías, cambios rápidos, cancelación,
consultas fallidas, resultados asíncronos obsoletos y propiedad del color con el
código de producción. Un host WPF oculto comprueba los relojes de la transición
compartida: último destino, cancelación y ausencia de un segundo giro para imágenes
equivalentes recibidas después del punto de intercambio.

Verificación visual: en Chrome cambiar entre videos con imágenes distintas y sin
miniatura; en un reproductor musical avanzar entre pistas del mismo álbum y entre
álbumes distintos. Repetir en compacto/expandido, ambos estilos de portada, con
animaciones desactivadas y con títulos largos. Debe aparecer el conjunto coherente,
sin giro para una portada compartida ni portada anterior persistente tras una ausencia.
