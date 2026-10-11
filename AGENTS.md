# Instrucciones del repositorio

## Logs y archivos temporales

- No crear logs de comandos, compilación, publicación o pruebas dentro del repositorio ni en su carpeta contenedora `custom fluent flyout`. Esto incluye `.log`, `.binlog` y archivos `.txt` usados para guardar salida de comandos.
- Mostrar la salida en la terminal por defecto. Si hace falta conservarla, usar una carpeta por ejecución bajo `%LOCALAPPDATA%\Codex\Logs\CustomFluentFlyout\`, fuera del proyecto.
- No sustituir este destino por una carpeta `logs` dentro del proyecto. `.gitignore` no evita el desorden de archivos locales.
- Eliminar los logs temporales cuando ya no hagan falta. Conservar los diagnósticos propios de la aplicación en sus ubicaciones externas existentes.

## Entrega local

- Después de cambiar código, compilar Release x64 y validar el cambio. Preparar la publicación antes de cerrar la instancia activa; conservarla si falla la compilación.
- Publicar con sus dependencias en `C:\Users\Admin\Desktop\custom fluent flyout\CustomFluentFlyout\bin\FluentFlyout` y reiniciar automáticamente `FluentFlyout.exe`, sin pedir otra confirmación.
- Intentar primero un cierre normal; finalizar únicamente la instancia afectada si no responde. Conservar ajustes, modelos, transcripciones y otros datos del usuario.
- Lanzar con `Start-Process -WindowStyle Hidden` y verificar que el nuevo proceso permanece activo y utiliza ese ejecutable.
