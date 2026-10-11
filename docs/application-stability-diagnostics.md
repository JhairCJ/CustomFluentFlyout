# Diagnóstico de cierres

Los nuevos archivos están en `%APPDATA%\FluentFlyout\diagnostics\session-*.jsonl`. Retienen diez sesiones, hasta 1 MiB por sesión y los últimos 64 hitos en memoria, con checkpoints cada dos segundos. Al llegar al límite, el archivo se reescribe con el contexto reciente para conservar fallos posteriores. Incluyen versión, Windows, arquitectura, modo WPF, fases, generaciones, tiempos, bounds y DPI. No reciben texto dictado/tecleado, audio ni credenciales. Un fallo de escritura no interrumpe la app. Los logs NLog existentes y la reparación del OSD se conservan.

La excepción `0x88980406` se clasifica como fallo del renderizador WPF y se registra antes de la terminación. No se marca manejada para seguir ejecutando una partición de renderizado dañada. Un cierre nativo puede no llegar al handler gestionado; en ese caso sirven el último checkpoint y el evento Windows.

## Iniciar cada modo

Salir de la instancia existente desde el menú de la bandeja. La app admite una sola instancia; abrir otro ejecutable mientras está activa no cambia su código ni su modo de renderizado.

Desde `CustomFluentFlyout/`:

```powershell
$stabilityExe = Join-Path (Get-Location) 'FluentFlyoutWPF\bin\x64\Release\net10.0-windows10.0.22000.0\FluentFlyout.exe'
Start-Process -FilePath $stabilityExe -WindowStyle Hidden
# Salir desde la bandeja antes de probar el otro modo.
Start-Process -FilePath $stabilityExe -ArgumentList '--software-rendering' -WindowStyle Hidden
```

`--software-rendering` establece `RenderOptions.ProcessRenderMode = SoftwareOnly` antes de crear ventanas. No modifica ajustes guardados, estilos de animación ni selección CPU/GPU del reconocimiento. Para volver al modo normal, salir y abrir sin la opción.

Los checks separados comprueban el modo efectivo, clasificación fatal, límite de archivo, retención, hitos y fallo de escritura. Los spies de arranque comprueban micrófono cerrado y cero cargas de motor en automático; mantener cargado inicia únicamente la precarga solicitada, sin bloquear entrada.

```powershell
dotnet run --project tools/DictationChecks -c Release -- --diagnostics
dotnet run --project tools/DictationChecks -c Release -- --diagnostics --software-rendering
```

## Recoger evidencia Windows

Anotar la hora exacta y exportar el intervalo desde PowerShell, sin permisos de administrador normalmente:

```powershell
$stabilitySince = (Get-Date).AddHours(-2)
$stabilityEvents = Get-WinEvent -FilterHashtable @{ LogName = 'Application'; Id = 1000,1001,1026; StartTime = $stabilitySince } -ErrorAction SilentlyContinue
$stabilityEvents | Where-Object Message -Match 'FluentFlyout' |
    Select-Object TimeCreated,Id,ProviderName,Message |
    Format-List | Out-File -LiteralPath (Join-Path $env:TEMP 'FluentFlyout-events.txt') -Encoding utf8
```

Guardar los JSONL de la sesión y los `logs.*.txt` del mismo intervalo. El evento 1000 aporta módulo y código, 1001 el informe WER y 1026 la excepción/stack .NET cuando existe. No enviar el archivo de ajustes completo ni la carpeta de transcripciones. Si esta evidencia no basta, pedir un volcado del proceso afectado explícitamente; no se configura captura automática de volcados en este cambio.

## Sesiones prolongadas y Lenovo

| Dictado | Normal | Software | Comprobaciones |
| --- | --- | --- | --- |
| Habilitado, automático | 30 min | 30 min | Sin gesto: micrófono y motores inactivos; widget, expansión, canciones y escritura operativos |
| Deshabilitado | 10 min | 10 min | Sin captura ni precarga; escritura y fondo giratorio |
| Mantener cargado | 10 min | 10 min | Precarga del modelo solicitado; micrófono cerrado sin gesto; respuesta de entrada |

Registrar inicio/fin, versión/binario, Windows/build, CPU/GPU y driver, RAM, modelo/dispositivo de reconocimiento, modo WPF, número de monitores/DPI, scroll/fondo/estilo, CPU y memoria al inicio/final, eventos Windows y pasos de cualquier fallo. Repetir misma matriz en el Lenovo Ryzen 5 con el mismo binario. Su cierre sigue **sin causa determinada y sin validación presencial**; comparar resultados antes de atribuirlo a dictado, AMD, CUDA o renderizado.

Las sesiones prolongadas y la matriz física no están aprobadas por los checks cortos. El binario abierto durante la implementación era el anterior; los resultados de esa instancia no validan este cambio.

El usuario realizará las sesiones prolongadas. Resultados automáticos, trazabilidad y pendientes: [informe de validación](stability-validation-report.md).
