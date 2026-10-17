# Instrucciones del repositorio

## Logs y archivos temporales

- No crear logs de comandos, compilación, publicación o pruebas dentro del repositorio ni en su carpeta contenedora `custom fluent flyout`. Esto incluye `.log`, `.binlog` y archivos `.txt` usados para guardar salida de comandos.
- Mostrar la salida en la terminal por defecto. Si hace falta conservarla, usar una carpeta por ejecución bajo `%LOCALAPPDATA%\Codex\Logs\CustomFluentFlyout\`, fuera del proyecto.
- No sustituir este destino por una carpeta `logs` dentro del proyecto. `.gitignore` no evita el desorden de archivos locales.
- Eliminar los logs temporales cuando ya no hagan falta. Conservar los diagnósticos propios de la aplicación en sus ubicaciones externas existentes.

## Entrega local

- No compilar, publicar ni instalar el proyecto. El usuario hará el build y la instalación por su cuenta.
- No iniciar ni reiniciar automáticamente `FluentFlyout.exe`.
