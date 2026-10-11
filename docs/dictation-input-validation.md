# Validación de entrada

El hook y su delegado pertenecen a `KeyboardInputThread`, con un message pump nativo independiente de WPF. El callback decide consumo y encola comandos FIFO; apertura, parada, modelos y transcripción quedan fuera del hilo de entrada. La generación se reserva al observar el gesto e invalida aperturas anteriores al cancelar, deshabilitar o cerrar.

El router conserva downs físicos por lado y downs consumidos hasta su up. Los atajos ajenos se reproducen en un lote corto en el hilo de entrada para conservar orden. `SendInput` parcial o denegado mantiene consumidas las liberaciones correspondientes a downs no aceptados; nunca se sueltan todos los modificadores artificialmente. La inyección está etiquetada y no activa dictado.

Raw Input se registra con `INPUTSINK`, sin `NOLEGACY`: observa únicamente modificadores y teclas del gesto, sin traducir o guardar caracteres. Espera 250 ms para tolerar el orden de ambos flujos; compara también down/up. La actividad física confirmada sin evento del hook cancela el gesto y reinstala el hook una vez por lote. Cambios de sesión, energía o escritorio desarman el gesto hasta un ciclo de liberación. Un cero de `GetAsyncKeyState` nunca basta: la inserción también consulta snapshot y acceso al escritorio de entrada.

## Comandos

```powershell
dotnet run --project .selfcheck -c Release
dotnet run --project tools/DictationChecks -c Release -- --input
```

Checks aprobados: ambos órdenes/lados de Ctrl+Shift, repeticiones, Ctrl+Shift+C, Escape, cambios de ajustes, ledger de eventos entregados/consumidos, envío parcial/denegado, releases perdidos y flujos en distinto orden. El servicio real se ejercita con captura ficticia bloqueada dos segundos, cien gestos, mantener/manos libres a ambos lados de 350 ms, cancelación e inyección. El pump real continúa con el llamador bloqueado dos segundos; una frontera nativa falsa comprueba delegado vivo, propagación de excepciones en menos de 50 ms y teardown repetido. La inserción usa envío y recuperación falsos: verifica cinco segundos de espera, copia de recuperación y cancelación sin escribir en otra aplicación.

## Contraste físico pendiente

Con el nuevo binario, abrir un editor vacío creado para la prueba. Escribir normalmente, probar Ctrl+Shift en ambos órdenes y añadir C, soltar primero Ctrl y después Shift y viceversa. Mantener la tecla para generar autorepeat, cambiar el ajuste con teclas mantenidas y probar Escape. Repetir al bloquear/desbloquear y suspender/reanudar Windows. Verificar que los atajos ajenos llegan una sola vez y que después se puede escribir.

La recepción de Raw Input de teclas consumidas necesita contraste con un teclado físico: los tests automáticos no lo sustituyen con `SendInput`. Registrar ausencia de doble activación y continuidad de la escritura. Si la entrega física difiere en una versión de Windows, adjuntar el caso y los hitos `InputRecovery`, sin texto tecleado. Este contraste y los casos de escritorio seguro siguen pendientes.
