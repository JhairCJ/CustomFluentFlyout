# Validación de captura

La frontera `IDictationCaptureDevice` conserva NAudio 3.1.0 y PCM16 mono a 16 kHz. `WaveIn` se construye en el worker sin contexto WPF y usa `WAVE_MAPPER` para abrir el dispositivo predeterminado actual. `RecordingStopped` se suscribe antes de `StartRecording` y completa una espera con continuaciones asíncronas.

El dispositivo, la escritura y los callbacks pertenecen a una sesión. La parada y el watchdog esperan confirmación antes de liberar buffers, reabrir o entregar el archivo. La solicitud de parada también corre fuera de WPF; se repite hasta confirmar para cubrir la carrera `Starting -> Capturing` de WaveIn. Se espera tanto el callback como el retorno de la solicitud nativa.

El límite es cinco segundos. Un timeout deja un único dispositivo en cuarentena, rechaza nuevas aperturas y comunica el fallo; conserva su PCM ya válido hasta su limpieza confirmada. La confirmación tardía libera una sola vez y permite un gesto nuevo, sin reactivar el antiguo. Un error confirmado de driver conserva el PCM recibido. Los archivos temporales se eliminan al terminar; no se incluyen en diagnóstico.

La precarga, inferencia y mantenimiento se registran como trabajos propietarios. El cierre cancela y espera sus operaciones fuera del Dispatcher antes de destruir factories y semáforos. La política de recursos no libera motores mientras haya un usuario activo de esos recursos.

## Comandos y resultados locales

```powershell
dotnet run --project tools/DictationChecks -c Release -- --capture
dotnet run --project tools/DictationChecks -c Release -- --input
dotnet run --project tools/DictationChecks -c Release -- --capture-device
```

El doble verifica cien confirmaciones retrasadas, callbacks de dispositivos antiguos, errores de parada, timeout, apertura rechazada en cuarentena, confirmación tardía y carrera watchdog/parada sin reapertura. El check de entrada verifica cierre durante apertura y durante precarga todavía en uso.

El micrófono local completó **100 ciclos reales** de inicio/parada: recibió **73 160 bytes PCM**, eliminados al terminar, sin reconocimiento ni inserción de texto. El proceso de prueba terminó correctamente. Esto comprueba la carrera abordada; no garantiza contener fallos nativos arbitrarios de un driver o motor dentro del proceso.

## Matriz de driver pendiente

Con una frase de prueba sin datos personales: gesto corto, mantener, manos libres, Escape, cien gestos rápidos, cancelar durante precarga/transcripción, desconectar/reconectar micrófono y cambiar el predeterminado mientras se graba. Verificar escritura operativa, error visible o recuperación segura, ausencia de reapertura después de cancelar y copia de recuperación cuando corresponda.

Desconexión física, cambio de driver/dispositivo y dictado real completo requieren ejecución interactiva. Revisar logs y eventos 1000/1001/1026 en el intervalo y buscar `waveInAddBuffer`, `WaveInBuffer.Reuse` y `WaveIn.DoRecording`. Distinguir fallos de `FluentFlyout.exe` de ejecutables de checks; registrar siempre versión, driver, hora y pasos.

Referencia: [ciclo de vida de WaveIn en NAudio 3.1.0](https://github.com/naudio/NAudio/blob/v3.1.0/src/NAudio.WinMM/WaveIn.cs).
