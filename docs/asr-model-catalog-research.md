# Catálogo de modelos ASR: investigación exhaustiva (2026-10-02)

Todo lo que sigue está verificado contra la API de Hugging Face (repositorio, revisión,
tamaño exacto en bytes y SHA-256 del LFS) o contra los README/model cards de los
proyectos. Los scripts de [tools/](../tools) son los que han sacado los datos, así que
se pueden volver a ejecutar cuando cambie algo.

- Herramientas: [`tools/hf_model_files.py`](../tools/hf_model_files.py) (ficheros, tamaños y
  sha256), [`tools/hf_list_models.py`](../tools/hf_list_models.py) (repos de una
  organización) y [`tools/gguf_header.py`](../tools/gguf_header.py) (metadatos de un GGUF
  sin descargarlo entero).
- Índice oficial de NeMo-Speech.cpp:
  <https://github.com/NVIDIA/NeMo-Speech.cpp/blob/main/models/index.json>

---

## 0. Resumen ejecutivo

| Prioridad | Qué | Trabajo | Resultado |
|---|---|---|---|
| **1** | 6-10 ficheros nuevos de `ggerganov/whisper.cpp` | solo entradas de catálogo | Whisper más grande/meJOR quantizado sin runtime nuevo |
| **2** | `parakeet-ctc-1.1b` y `nemotron-speech-streaming-en-0.6b` de NVIDIA | solo entradas de catálogo | los dos únicos modelos del índice de NeMo-Speech.cpp que faltan |
| **3** | `orukeet-v0.1.0-f16.gguf` | una entrada | el mismo modelo OruKeet sin pérdida de Q8 |
| **4** | **CrispASR** como cuarto backend | ~400 líneas | un solo binario (38 MB Vulkan / 9 MB CPU) y **~60 modelos** desbloqueados, entre ellos Parakeet Ultra, Granite, Canary, GigaAM, Cohere, Voxtral y Moonshine |
| **5** | sherpa-onnx como quinto backend | ~500 líneas | Zipformer (70 MB), SenseVoice, Fun-ASR y Parakeet en ONNX |

Las tres primeras no requieren ni una línea de C#.

El hallazgo que cambia la estrategia: **CrispASR** (CrispStrobe/CrispASR) es un fork de
whisper.cpp que carga un GGUF por modelo y tiene 119 backends (57 ASR + 62 TTS) en el
mismo binario, con builds de Windows CPU / CUDA / Vulkan autocontenidas. Es, con
diferencia, la mejor relación "modelos desbloqueados / esfuerzo" para el catálogo.

---

## 1. Restricciones del proyecto

| Restricción | Valor |
|---|---|
| GPU del usuario | RTX 3050 Ti Laptop, **4 GB VRAM**, driver 596.49 (CUDA 13.2) sin cuBLAS instalado |
| Tamaño máximo cómodo | 700 MB - 1,2 GB (por encima de 2 GB la GPU se queda corta y cae a CPU) |
| Runtimes actuales | whisper.net 1.9.1 (whisper.cpp embebido, con CUDA), `nemo-speech` (NeMo-Speech.cpp) |
| Formato de catálogo | un artefacto por entrada, con `Repository` + `Revision` + `RemoteFileName` + `Sha256` |
| Idioma del usuario | español (el criterio de selección real es WER en español) |

---

## 2. Estado actual del catálogo (13 entradas)

**Whisper / whisper.cpp embebido** (repo `ggerganov/whisper.cpp`):
`ggml-large-v3-turbo-q5_0` (574 MB), `ggml-large-v3-q5_0` (1,08 GB), `ggml-medium-q5_0`
(539 MB), `ggml-medium` (1,53 GB), `ggml-small` (488 MB), `ggml-small-q8_0` (264 MB),
`ggml-small-q5_1` (190 MB, recomendada), `ggml-base` (148 MB), `ggml-base.en`,
`ggml-tiny`, `ggml-tiny.en`, `ggml-distil-large-v3-multi4` (1,52 GB).

**NeMo-Speech.cpp**: `orukeet-v0.1.0-q8.gguf` (714 MB), `parakeet-tdt-0.6b-v3.q8_0.gguf`
(714 MB), `nemotron-3.5-asr-streaming-0.6b.q8_0.gguf` (742 MB).

---

## 3. Grupo A - Drop-in sin tocar código (whisper.cpp)

Todo lo que sigue es un `ggml-*.bin` del repositorio oficial
`ggerganov/whisper.cpp`, revisión `5359861c739e955e79d9a303bcbc70fb988958b1`, que es la
revisión desde la que se midieron tamaño y SHA-256. Cualquier `.bin` de ggml lo carga
whisper.net sin configuración extra, así que todas estas entradas son válidas tal cual.

### A.1 Los que más aportan (los que faltan y merece la pena añadir)

| Fichero | Bytes | Tamaño | SHA-256 | Nota |
|---|---:|---:|---|---|
| `ggml-large-v3-turbo-q8_0.bin` | 874 188 075 | 834 MB | `317eb69c11673c9de1e1f0d459b253999804ec71ac4c23c17ecf5fbe24e259a1` | Q8 suele superar a Q5_0 en audio limpio; el turbo es 4x más rápido que large-v3 |
| `ggml-medium-q8_0.bin` | 823 369 779 | 785 MB | `42a1ffcbe4167d224232443396968db4d02d4e8e87e213d3ee2e03095dea6502` | medium sin pérdida frente a F16 |
| `ggml-large-v3.bin` | 3 095 033 483 | 2,9 GB | `64d182b440b98d5203c4f9bd541544d84c605196c4f7b845dfa11fb23594d1e2` | F16, el más exacto de Whisper; 2,9 GB no caben en 4 GB de VRAM |
| `ggml-large-v3-turbo.bin` | 1 624 555 275 | 1,5 GB | `1fc70f774d38eb169993ac391eea357ef47c88757ef72ee5943879b7e8e2bc69` | turbo F16 |
| `ggml-medium.en.bin` | 1 533 774 781 | 1,4 GB | `cc37e93478338ec7700281a7ac30a10128929eb8f427dda2e865faa8f6da4356` | solo inglés |
| `ggml-medium.en-q8_0.bin` | 823 382 461 | 785 MB | `43fa2cd084de5a04399a896a9a7a786064e221365c01700cea4666005218f11c` | solo inglés |
| `ggml-small.en.bin` | 487 614 201 | 465 MB | `c6138d6d58ecc8322097e0f987c32f1be8bb0a18532a3f88f734d1bbf9c41e5` | solo inglés |
| `ggml-small.en-q8_0.bin` | 264 477 561 | 252 MB | `67a179f608ea6114bd3fdb9060e762b588a3fb3bd00c4387971be4d177958067` | solo inglés |
| `ggml-small.en-q5_1.bin` | 190 098 681 | 181 MB | `bfdff4894dcb76bbf647d56263ea2a96645423f1669176f4844a1bf8e478ad30` | solo inglés |
| `ggml-medium.en-q5_0.bin` | 539 225 533 | 514 MB | `76733e26ad8fe1c7a5bf7531a9d41917b2adc0f20f2e4f5531688a8c6cd88eb0` | solo inglés |

### A.2 Los que completan la rejilla de quants

| Fichero | Bytes | Tamaño | SHA-256 |
|---|---:|---:|---|
| `ggml-base-q8_0.bin` | 81 768 585 | 78 MB | `c577b9a86e7e048a0b7eada054f4dd79a56bbfa911fbdacf900ac5b567cbb7d9` |
| `ggml-base-q5_1.bin` | 59 707 625 | 57 MB | `422f1ae452ade6f30a004d7e5c6a43195e4433bc370bf23fac9cc591f01a8898` |
| `ggml-base.en-q8_0.bin` | 81 781 811 | 78 MB | `a4d4a0768075e13cfd7e19df3ae2dbc4a68d37d36a7dad45e8410c9a34f8c87e` |
| `ggml-base.en-q5_1.bin` | 59 721 011 | 57 MB | `4baf70dd0d7c4247ba2b81fafd9c01005ac77c2f9ef064e00dcf195d0e2fdd2f` |
| `ggml-tiny-q8_0.bin` | 43 537 433 | 42 MB | `c2085835d3f50733e2ff6e4b41ae8a2b8d8110461e18821b09a15c40c42d1cca` |
| `ggml-tiny-q5_1.bin` | 32 152 673 | 31 MB | `818710568da3ca15689e31a743197b520007872ff9576237bba97bd1b469c3d7` |
| `ggml-tiny.en-q8_0.bin` | 43 550 795 | 42 MB | `5bc2b3860aa151a4c6e7bb095e1fcce7cf12c7b020ca08dcec0c6d018bb7dd94` |
| `ggml-tiny.en-q5_1.bin` | 32 166 155 | 31 MB | `c77c5766f1cef09b6b7d47f21b546cbddd4157886b3b5d6d4f709e91e66c7c2b` |

### A.3 Whisper que no es de OpenAI

| Modelo | Repositorio | Tamaño | Nota |
|---|---|---:|---|
| Distil Large v3 (6,3x más rápido que large-v3) | `distil-whisper/distil-large-v3` | 1,51 GB (fp16) | **solo inglés**; ya está en el catálogo en formato multi4 |
| Distil Large v3 GGUF de CrispASR | `cstr/distil-large-v3-GGUF` | ~1 GB Q8 | requiere CrispASR |

---

## 4. Grupo B - Drop-in con NeMo-Speech.cpp (funciona hoy)

El runtime carga un GGUF por modelo. Su índice oficial (`models/index.json`) solo
publica **cuatro** modelos ASR, y el catálogo ya tiene dos de ellos.

### B.1 Los dos que faltan (datos exactos del índice y de la API)

| Modelo | Repositorio / revisión | Fichero | Bytes | SHA-256 | Idiomas |
|---|---|---|---:|---|---|
| Parakeet CTC 1.1B | `nvidia/parakeet-ctc-1.1b` @ `20e63a0fed6aedba145b74b826dbd41df0941730` | `parakeet-ctc-1.1b.q8_0.gguf` | 1 178 100 960 | `6584fc0fdacf1c220401ea4c3a1d5b44454b655c141cb8672178072c203d92b8` | solo inglés |
| Nemotron Speech Streaming EN 0.6B | `nvidia/nemotron-speech-streaming-en-0.6b` @ `ebe59e5a817142986528bbbee5dba8db7b38ed50` | `nemotron-speech-streaming-en-0.6b.q8_0.gguf` | 699 872 960 | `d9a01898d2a611c8764e23a1c2f45e70bbd5a425dc4de93692ac951dd603812d` | solo inglés, cache-aware RNNT |

Aviso sobre el CTC: **sale en minúsculas y sin puntuación**. El runtime lo arregla con
un modelo PnC acompañante (`pnc-bert.q8_0.gguf`, convertido desde un checkpoint NeMo de
puntuación y mayúsculas), así que para dictado en español es peor elección que el TDT.

### B.2 OruKeet en F16

| Fichero | Bytes | Tamaño | SHA-256 |
|---|---:|---:|---|
| `orukeet-v0.1.0-f16.gguf` | 1 296 681 088 | 1,2 GB | `de53fb8ec251fb07ade15baabe17b00774ae3f1112f8618b062337f90fb49194` |

Mismo repositorio y misma revisión que la entrada Q8 que ya existe
(`b59c13a733fce5cf193230d0fa317ee7f145a108`). 1,2 GB no caben cómodo en 4 GB de VRAM
pero en CPU va bien.

### B.3 Modelos adicionales de `oruk/orukeet`

| Fichero | Bytes | Runtime |
|---|---:|---|
| `orukeet-transcribe-cpp-Q8_0.gguf` | 739 508 608 | transcribe.cpp, **no** NeMo-Speech.cpp |
| `onnx/combined-v0.1.0-int8/*` | 671 MB en total | onnx-asr |
| `onnx/sherpa-v0.1.0-int8/*` | 645 MB en total | sherpa-onnx |
| `orukeet-v0.1.0.nemo` | 2 509 342 720 | checkpoint original |

### B.4 Modelos NVIDIA sin GGUF oficial (convertibles una vez con `convert_model.py`)

Solo publican `.nemo`, pero el conversor de NeMo-Speech.cpp los acepta y el GGUF resultante
funciona con el runtime que ya tenemos. La lista completa de checkpoints de ASR de NVIDIA
está en <https://huggingface.co/models?author=nvidia&pipeline_tag=automatic-speech-recognition>
(100+ repos). Los interesantes:

| Checkpoint | Bytes del `.nemo` | Idiomas | Por qué |
|---|---:|---|---|
| `nvidia/parakeet-tdt-0.6b-v2` | 2 472 222 720 | en | anterior al v3, con mayúsculas y puntuación |
| `nvidia/parakeet-tdt-1.1b` | ~2,4 GB | en | más grande, en inglés |
| `nvidia/parakeet-tdt_ctc-1.1b` | ~2,4 GB | en | híbrido TDT+CTC |
| `nvidia/parakeet-tdt_ctc-110m` | ~0,5 GB | en | **el Parakeet más pequeño**, 120 MB en Q8 |
| `nvidia/parakeet-rnnt-0.6b` / `-1.1b` | ~2,4 GB | en | transductor clásico |
| `nvidia/parakeet-ctc-0.6b` | ~1,9 GB | en | |
| `nvidia/parakeet-unified-en-0.6b` | 2 474 055 680 | en | multitalker + diarización en streaming |
| `nvidia/multitalker-parakeet-streaming-0.6b-v1` | ~2,4 GB | en | ASR + separación de hablantes |
| `nvidia/canary-1b-v2` | 6 358 958 080 | 25 UE | topped el leaderboard en 2025 |
| `nvidia/canary-1b-flash` | ~2 GB | 25 UE | versión rápida |
| `nvidia/canary-180m-flash` | ~0,4 GB | **en, de, es, fr** | pequeño y con español |
| `nvidia/gigaam-v3` (via `ai-sage/GigaAM-v3`) | 448 928 167 | en | transducer grande de NVIDIA |

### B.5 Checkpoints pensados específicamente para español

| Checkpoint | Bytes | Notas |
|---|---:|---|
| `nvidia/stt_es_fastconformer_hybrid_large_pc` @ `d3fcff82e2ddec13517256402aa5da86400c5893` | 459 223 040 | español, RNNT+CTC, con mayúsculas y puntuación |
| `nvidia/stt_es_fastconformer_hybrid_large_pc_nc` @ `57634be93e98701bda5b23a5d9b9210eafb95b71` | 459 233 280 | el mismo sin mayúsculas |
| `nvidia/stt_es_conformer_ctc_large` @ `6f74fddad2a254ab0eba7c9d0c5036f18850466b` | 453 786 094 | español CTC |

### B.6 Modelos acompañantes (no son de dictado, pero el runtime los admite)

| Modelo | Fichero | Bytes | SHA-256 |
|---|---|---:|---|
| Nemotron 3 Diarization (8 hablantes) | `Nemotron-3-Diarization.q8_0.gguf` | 107 012 128 | `08456d9e22cd9a323c0364d98375f3746d6e68507ebb705cd46438c534c7a3a1` |
| Sortformer 4 hablantes v2 | `diar_streaming_sortformer_4spk-v2.q8_0.gguf` | 147 075 776 | `0679cfeb1ce356d0dea9470b31274f4bfc7eb927497d82005483770666da998a` |

---

## 5. Grupo C - CrispASR: un backend, ~60 modelos

<https://github.com/CrispStrobe/CrispASR> es un fork de whisper.cpp con un runtime ggml
por arquitectura y **un solo binario**. Detecta el backend leyendo los metadatos del GGUF
(`general.architecture`), así que el código de la app no necesita saber qué modelo es.
Descarga de Windows (release v0.8.40, 2026-10-01):

| Asset | Tamaño | Nota |
|---|---:|---|
| `crispasr-windows-x86_64-vulkan.zip` | 37,9 MB | **el que interesa**: funciona con la RTX 3050 Ti y con Intel/AMD, sin cuBLAS |
| `crispasr-windows-x86_64-cuda13.zip` | 510,6 MB | autocontenido (no hace falta CUDA Toolkit) |
| `crispasr-windows-x86_64-cuda.zip` | 726,8 MB | CUDA 12 |
| `crispasr-windows-x86_64-cpu.zip` | 8,9 MB | AVX2 |
| `crispasr-windows-x86_64-cpu-legacy.zip` | 8,3 MB | CPU sin AVX2 |

Los pesos están en la organización `cstr` (conversiones propias, con F16 / Q8_0 / Q5_K_M /
Q4_K) y también en `handy-computer` (mismas arquitecturas, otro formato de nombre).

### C.1 Los candidatos que de verdad aportan para dictado en español

| Modelo | GGUF recomendado | Bytes | SHA-256 | Idiomas | Nota |
|---|---|---:|---|---|---|
| **Parakeet Ultra** (`moondream/parakeet-ultra`) | `cstr/parakeet-ultra-GGUF@252cd632a21e98ba5edbdeb61c7274d01c54cb73` / `parakeet-ultra-q8_0.gguf` | 674 342 400 | `ebf1186c3dc7e77f71877a5380a73e39d5c0aaf5cb55e65e56b077b1b2aacef1` | 25 UE | **el mejor WER en español medido (2,72 en FLEURS)**; mismo tokenizador que Parakeet |
| Parakeet Ultra Q4_K (para 4 GB) | mismo repo, `parakeet-ultra-q4_k.gguf` | 402 226 496 | `09bb4a91da4c14f158ad01829b9bb3d81eedc85156d884e9a9c483dfe09238c6` | 25 UE | cabe en cualquier GPU de 4 GB |
| **Parakeet Redux** (`moondream/parakeet-redux`) | `cstr/parakeet-redux-GGUF@3f288b7fb33caff80f1424bd5ea1cecaf9808209` / `parakeet-redux-q8_0.gguf` | 674 342 400 | `606135796d55fd64b5baeb21966b3fcb469b61e6950ba2e0f9269c49cc734ff2` | 25 UE | 113x tiempo real en CPU; peor en ruido (9,04 vs 6,72) |
| OruKeet (misma arquitectura) | `cstr/orukeet-GGUF@2a93474c2771a6ca2228a7e001bd06ce0c97f880` / `orukeet-q5_0.gguf` | 470 255 456 | `2bcbce1f63be6f6f0828c14b131230c24d535b3bbff9c50edfdcc68687b6baa9` | 25 UE | versión más pequeña del OruKeet que ya tenemos |
| **Cohere Transcribe 03-2026** | `cstr/cohere-transcribe-03-2026-GGUF@4e98a8c31c95a92d651e60e3bfae4fe29fc6e026` / `cohere-transcribe-q4_k.gguf` | 1 289 178 240 | `116f4c4f7ff1b03997100d3a097e13fecd84350555b9979867b241cd6803e4f5` | 14 (**incluye español**), Apache-2.0 | #1 WER en su leaderboard, Conformer |
| Canary 1B v2 | `cstr/canary-1b-v2-GGUF@f4a12db73daa964aa56a188826682f6b11fdc960` / `canary-1b-v2-q8_0.gguf` | 1 049 050 784 | `81421a82cdcb23746f6c3e8c2859094f7e8c27dd8ba77d4b8f8440395fbc7c7a` | 25 UE | requiere `-sl`/`-tl` explícitos |
| Canary 1B v2 Q5_0 | mismo repo | 720 322 208 | `e312694d877c0df7efe8ffc974cdf74bf06605c154552af214ee279741325703` | 25 UE | |
| Canary 1B v2 Q4_K | mismo repo | 610 746 016 | `5027f74ba0eb9bb707b14a11c7a68510a704748bb392e5f3af45115329ee501d` | 25 UE | |
| Parakeet TDT+CTC 110M | `cstr/parakeet-tdt_ctc-110m-GGUF@6432cc0f804c91dae1e8d4dacf27c02c3627a7e7` / `parakeet-tdt_ctc-110m-q8_0.gguf` | 126 617 600 | `5f98a5f29a02c8164ff2288a24bf24f98c7cd9ffd1a206747b9360d0193cfc78` | en | el más rápido y pequeño de la familia |
| Granite Speech 4.1 2B NAR | `cstr/granite-speech-4.1-2b-nar-GGUF@7e8ef538a1a9eac847f670e3831e52fb36730241` / `granite-speech-4.1-2b-nar-q4_k-mini.gguf` | 1 623 632 224 | `4c36f5d45182c2279bcb3d63ae9e25d2172a416e84e92803558238f05685c8c6` | **en, fr, de, es, pt, ja** | no autorregresivo: rapidísimo |
| Granite Speech 4.1 2B (+plus) | `cstr/granite-speech-4.1-2b-plus-GGUF` | 1,1 GB Q5_K | - | en, fr, de, es, pt | salida puntuada |
| Phonon-2 (`FermionResearch/Phonon-2`) | `cstr/phonon2-GGUF@3ed3e6ad6e7ce63affffeede37328ff756efaa2f` / `phonon2-q8_0.gguf` | 674 342 560 | `a8c4a874195b83604ceb5d87c0e9b5ddf641ef1d5ec2d8e1ca2a040d25b2563a` | en | Parakeet TDT v3 con pesos de cinco valores reentrenados |
| GigaAM v3 (RNNT y CTC) | `cstr/gigaam-v3-GGUF@5df03d88f87f66ab6afc17c615370d20cda93669` / `gigaam-v3-ctc-q8_0.gguf` | 245 137 312 | `71ef12d883230f5f8dc50b4822164a73629f69a1a7e76b9bf937a20f55438c00` | en | transducer de NVIDIA |
| Moonshine base / tiny | `cstr/moonshine-base-GGUF@ccc6dc6e0fc1fd0fdf3dfb9448e09bb9bd8aa370` / `moonshine-base-q8_0.gguf` | 75 890 784 | `27378245b95f700da66cdffc6222e89e65b7dbc3f21f2f4a25bb167e80e7d83f` | en (+6 idiomas) | 72 MB, para CPU |
| Fun-ASR Nano (MLT) | `handy-computer/Fun-ASR-MLT-Nano-2512-gguf` / Q8_0 | 891 271 232 | `d12476d8d9f2baa0ebf738fa955fa05ed33a654f1567289033a810c45d9d9002` | 31 idiomas **sin español** | descartado para este usuario |
| SenseVoice Small | `cstr/sensevoice-small-GGUF@e1ad67de9d05137a6a0b02beba1fd66e28df6ae4` / `sensevoice-small-q8_0.gguf` | 251 674 976 | `6b84003db9da214c129bcdbb1c471d25b211775a905aecb253dd23aebf18b7f4` | zh, en, ja, ko, yue | **sin español** |
| FireRedASR2 AED | `cstr/firered-asr2-aed-GGUF@ccff17634cee46ed679bd6ab04cbd5cc2808d9da` / `firered-asr2-aed-q4_k.gguf` | 962 807 328 | `c5f40fe5b467296395027c7397d87043a39e3223fcd049056ed5ba88974e9e0d` | zh, en + dialectos | mayoritariamente chino |
| Voxtral Mini 4B Realtime | `cstr/voxtral-mini-4b-realtime-GGUF@1c61dc461970d53264d784363b31ca113c7e01fd` / `voxtral-mini-4b-realtime-q4_k.gguf` | 2 524 137 216 | `7dda1dba692f18c9d30a6064943b92c562853b399e96320929d2e1399c9d41cc` | 13, streaming | 2,4 GB: demasiado para 4 GB de VRAM |
| Voxtral Mini 3B | `cstr/voxtral-mini-3b-2507-GGUF` | ~1,7 GB Q4_K | - | 8 idiomas | |
| GLM-ASR-Nano | `cstr/glm-asr-nano-GGUF@ef692d843f59538949dd137f0dad61b60a453d18` / `glm-asr-nano-q4_k.gguf` | 1 325 316 448 | `9e090376a77f82223f6264048884d0a6cb581e8ea191cb2e73b5b9974edb6e0d` | zh, en, yue | sin español |
| MiMo-ASR (Xiaomi) | `cstr/mimo-asr-GGUF@e2d7dfebf0afd8076771903e92958039c5074eab` / `mimo-asr-q4_k.gguf` | 4 517 988 832 | `12dbc7cc7a20c7add6ff00bf8b12bca1c46304e0100a5c5a6e74bdecfc57a306` | multilingüe | 4,2 GB: no |
| Granite Speech 4.0 1B (GGUF oficial de IBM) | `ibm-granite/granite-4.0-1b-speech-GGUF@7e8f817c0ebab568622dadec5ad5ee1283ceeb85` | modelo 1,1 GB Q4_K **+ `mmproj-model-f16.gguf` 1,1 GB** | `d7a6d0a801d1cb27600456302d1cb73ef8a3cc8d5938d462a19bcd8315338b74` (mmproj) | en, fr, de, es, pt, ja | para llama.cpp: hacen falta los dos ficheros |

### C.2 Lo que aporta este backend y no el actual

1. **Parakeet Ultra**: el mejor WER en español que existe hoy en código abierto
   (2,72 en FLEURS, frente a 3,12 del Parakeet TDT v3 y 2,77 del OruKeet), y además es
   más rápido que el original según su propia author's card.
2. **Modelos que NVIDIA no convierte a GGUF**: Canary, Canary-180m-flash (es), GigaAM,
   Granite, Cohere, Voxtral, X-ASR, Dolphin, kyutai, LFM2.5-Audio.
4. **Un solo tipo de artefacto**: un GGUF por modelo, mismo formato de descarga y misma
   verificación de integridad que ya tiene el catálogo.

### C.3 Cómo clasificar un GGUF sin leerlo entero

```console
python tools/gguf_header.py "handy-computer/parakeet-tdt-0.6b-v3-gguf parakeet-tdt-0.6b-v3-Q8_0.gguf"
# general.architecture = parakeet        <- runtime CrispASR / parakeet.cpp
python tools/gguf_header.py "nvidia/parakeet-tdt-0.6b-v3 parakeet-tdt-0.6b-v3.q8_0.gguf"
# general.architecture = asr             <- runtime NeMo-Speech.cpp
```

| `general.architecture` | Runtime |
|---|---|
| `asr` (+ claves `asr.head_type`) | NeMo-Speech.cpp |
| `parakeet`, `canary`, `gigaam`, `granite_speech5_ctc`, `funasr_nano`, `sensevoice`, `moonshine_streaming`, `medasr`, `whisper`, `voxtral`… | CrispASR / handy-computer |
| `llama` con `tokenizer.ggml.*` | llama.cpp |

Los GGUFs de `handy-computer` y de `cstr` son **el mismo formato** (arquitectura leída de
los metadatos), solo cambia el nombre del fichero.

### C.4 Verificado ejecutándolo en esta máquina (2026-10-02)

Todo lo anterior era lectura de ficha; esto es una ejecución real del release `v0.8.40`
de Vulkan con `parakeet-ultra-q4_k.gguf` sobre el audio de muestra `jfk.wav` (11 s).

| Dato | Valor comprobado |
|---|---|
| `crispasr-windows-x86_64-vulkan.zip` | 37 857 390 bytes, sha256 `d78135b46d7881aec909aa398def315427ad784a268979accd18fa569a3b5ce9` |
| Contenido del zip | una carpeta `crispasr-windows-x86_64-vulkan/` con `crispasr.exe`, `crispasr.dll`, `whisper.dll`, `ggml.dll`, `ggml-cpu.dll`, `ggml-base.dll`, `ggml-vulkan.dll`, `crispasr-quantize.exe`, licencia y avisos |
| Detección de backend | automática: `backend arg = 'parakeet'` sin pasar `--backend` |
| Parámetros del modelo | `vocab=8192 d_model=1024 n_layers=24 n_heads=8 ff=4096 pred=640 joint=640` |
| GPU enumerada por defecto | **la Intel UHD integrada**, no la RTX: `Vulkan0` es el primer dispositivo Vulkan que encuentra |
| `-dev 1` | sí selecciona la RTX 3050 Ti (pasa a `type=1`, GPU discreta) |
| Salida | con `-oj -of <base>` escribe `<base>.json`: `crispasr{backend,model,language,…}` + `transcription[{timestamps,offsets,text}]`; con `-np` por stdout solo sale el texto |
| `-l` | el backend parakeet **lo ignora** (mismo texto con `en`, `es` y `fr`): el modelo autodetecta el idioma |

Tiempos reales (11 s de audio, proceso de un solo uso, 8 hilos):

| Modo | 1ª ejecución | siguientes | Comentario |
|---|---:|---:|---|
| CPU (`-ng`) | 3,7 s | 2,1 s | ~5x tiempo real, incluye la carga del modelo |
| Vulkan iGPU (el que elige solo) | 97,6 s | 27,4 s | la primera compila los pipelines, las demás arrastran ~10 s fijos |
| Vulkan RTX 3050 Ti (`-dev 1`) | 27,2 s | 27,2 s | mismo coste de arranque, sin mejora |

**Conclusión para esta máquina**: con el runtime Vulkan de CrispASR la GPU **pierde**
contra la CPU en un proceso de un solo uso, así que mientras se pruebe Parakeet Ultra
conviene dejar apagado el interruptor de aceleración de la página de dictado. Los `-t`
(4 por defecto, 6, 12) no cambian nada medible.

---

## 6. Grupo D - sherpa-onnx

Un binario nativo (`sherpa-onnx-offline`), ONNX Runtime, con builds de Windows CPU y
CUDA 12/13 (`sherpa-onnx-v1.13.8-cuda-13.x-...-win-x64-cuda.tar.bz2`, 478 MB). Cubre la
familia NeMo, Zipformer, Paraformer, SenseVoice y Fun-ASR.

| Modelo | Repositorio | Fichero | Bytes | SHA-256 | Idiomas |
|---|---|---|---:|---|---|
| Parakeet TDT v3 (fp32) | `csukuangfj/sherpa-onnx-nemo-parakeet-tdt-0.6b-v3` @ `1a468a35cbba69418f126de829e75261dea4a4e4` | `encoder.onnx` + `decoder.onnx` + `joiner.onnx` | 41 766 257 + 47 233 743 + 25 286 330 | `3eed7ce4…` / `d593cdb0…` / `b9b0bcf8…` | 25 UE |
| Parakeet Ultra (fp16) | `beshkenadze/parakeet-ultra-onnx` @ `e5f01a8647be2c3f60cd8fe817e1677bc2d9141a` | `encoder-model.fp16.onnx` + `decoder_joint-model.onnx` | 1 218 263 142 + 72 522 409 | `9ce4a4d9…` / `56b40b3c…` | 25 UE |
| Zipformer medium EN (int8, con puntuación) | `csukuangfj/sherpa-onnx-zipformer-en-libriheavy-20230830-medium-punct-case` @ `455ba8887f2ffde208d3dbbed5d74e3f9920719e` | `encoder-epoch-50-avg-15.int8.onnx` + decoder + joiner | 65 634 141 + 2 616 855 + 1 551 717 | `80200015…` / `7d6289e3…` / `481ca000…` | solo inglés |
| SenseVoice Small (int8) | `csukuangfj/sherpa-onnx-sense-voice-zh-en-ja-ko-yue-int8-2025-09-09` | `model.int8.onnx` | 237 115 547 | `12ca1a2ae7ecf3e0019ef2822307ee0b5cadc9196569e379b4c4026f8205276d` | sin español |
| Fun-ASR Nano int8 | `csukuangfj/sherpa-onnx-sense-voice-funasr-nano-int8-2025-12-17` | `model.int8.onnx` | 263 531 902 | `9dc6e72aa8bc6f5966cf2857a0ce3a425b1d72e91500e147d66329f407c017a1` | 31 idiomas, sin español |

Ventaja: los Zipformer son los más rápidos en CPU de todo el catálogo (70 MB en int8).
Inconveniente: son tres o cuatro ficheros por modelo, así que el modelo de datos del
catálogo (un artefacto) necesitaGeneralizarse o duplicarse.

---

## 7. Grupo E - Checkpoints Python (sin integración en la app)

Estos checkpoints requieren un backend adicional; la app no depende de Python para el dictado.

| Modelo | Repositorio | Tamaño | Idiomas | Nota |
|---|---|---:|---|---|
| `ibm-granite/granite-speech-5.0-470m-turboctc` | `ibm-granite/granite-speech-5.0-470m-turboctc` @ `286456107c8ba1161f5c22dfe85466402c88333b` | `model.safetensors` 946 180 704 | solo inglés | **5,00 % WER agregado** en los sets públicos del OpenASR Leaderboard, Apache-2.0, 470 M, encoder-only (CTC) |
| `ibm-granite/granite-speech-5.0-470m-turboctc-nc` | mismo, rev `0eb7b4fe726a294815dc45d342860465b5af68ef` | 946 180 704 | inglés | 4,85 % WER pero licencia CC-BY-NC-SA-4.0 (no comercial) |

OruKeet, OruKeet F16 y todo lo de NVIDIA entra por el Grupo B, que ya funciona.

---

## 8. Grupo F - Los mejores del mundo, pero no viables aquí

| Modelo | Por qué no |
|---|---|
| `CohereLabs/cohere-transcribe-03-2026` en Q8 | 2,42 GB; en Q4_K son 1,29 GB y sí entraría (ya está en C.1) |
| `mistralai/Voxtral-Mini-4B-Realtime-2602` | 2,4 GB en Q4_K, y es un LLM de 3,4 B: lento en CPU |
| `zai-org/GLM-ASR-Nano-2512` | 1,33 GB en Q4_K pero sin español |
| `XiaomiMiMo/MiMo-ASR` (vía `cstr/mimo-asr-GGUF`) | 4,52 GB en Q4_K |
| `DataoceanAI/dolphin-small` / `DataoceanAI1/dolphin-*` | languages orientales (40 Idiomas del este), no español |
| `microsoft/VibeVoice-ASR` | 17,8 GB en 8 shards |
| `FireRedTeam/FireRedASR2-AED` | ≈ 0,9 GB en Q4_K pero centricado en chino; el LID cubre 120 idiomas, el ASR no |
| `kyutai/stt-2.6b-en`, `kyutai/stt-1b-en_fr` | inglés/francés |
| `IndexTeam/Index-Echo-S2TT-2B` | subtítulos bilingües zh → en/ja/es, 2,6 GiB en Q8 |
| `ai-sage/GigaAM-v3` vía transformers | ya está mejor como GGUF en CrispASR |
| `moondream/parakeet-ultra` en transformers | 1,2 GB de safetensors: el GGUF de CrispASR son 643 MB |

---

## 9. Comparativa de calidad (lo que decide la elección)

WER en FLEURS español (menor es mejor), de las model cards de cada autor:

| Modelo | FLEURS es WER | FLEURS 25 idiomas | Fuente |
|---|---:|---:|---|
| **Parakeet Ultra** | **2,72** | **9,55** | model card de moondream, pipeline del OpenASR Leaderboard |
| **OruKeet** | **2,77** | **10,08** | `oruk/orukeet/docs/standard-asr-benchmarks.md` |
| Parakeet TDT 0.6B v3 | 3,12 | 11,62 | model card de moondream |
| Parakeet TDT 0.6B v3 (medición de OruKeet) | 3,22 | 11,07 | normalizador distinto, no comparable 1:1 |
| Parakeet Redux | 3,71 | 10,56 | model card de moondream |

WER agregado inglés (OpenASR Leaderboard, 7 sets):

| Modelo | WER | Nota |
|---|---:|---|
| Granite Speech 5.0 470M TurboCTC (Apache) | 5,00 | 470 M, 12 600 RTFx en H200 |
| Granite Speech 5.0 470M TurboCTC-NC | 4,85 | licencia no comercial |
| Parakeet Ultra | 5,80 | |
| Parakeet TDT 0.6B v3 | 6,26 | |
| Parakeet Redux | 6,55 | |
| Whisper large-v3 turbo | ~7,83 | fuente secundaria |

Velocidad (referencia de cada autor, no comparable entre máquinas):

| Runtime / modelo | Máquina | Tiempo real |
|---|---|---:|
| Parakeet Redux (Photon) | 8 núcleos Zen 5 | 113x |
| parakeet.cpp Q8_0 | mismos 8 núcleos | 45x |
| sherpa-onnx int8 | mismos 8 núcleos | 42x |
| NeMo con Parakeet v3 | B200, batch 128 | 6 005x |
| Photon con Parakeet Ultra | B200, batch 128 | 9 743x |

---

## 10. Plan recomendado

### Fase 1 - sin código (media hora)

1. Añadir las 10 entradas del §A.1 (Whisper Q8/EN/F16).
2. Añadir `parakeet-ctc-1.1b` y `nemotron-speech-streaming-en-0.6b` del §B.1.
3. Añadir `orukeet-v0.1.0-f16.gguf` del §B.2.
4. Marcar `parakeet-ctc-1.1b` con `Runtime = "Requiere NeMo-Speech.cpp"` y una nota de
   que sale sin puntuación.

### Fase 2 - CrispASR (el backend que lo cambia todo)

1. Nuevo miembro `DictationModelBackend.CrispAsr` y un `CrispAsrTranscriber` con la misma
   forma que `ExternalAsrTranscriber`: un proceso por transcripción, `--json`, un modelo
   por GGUF. No hace falta caché ni warming: el binario es el que decide el backend
   leyendo el GGUF.
2. Descarga del runtime al estilo de la que ya hace la app para el resto (los assets de
   37,9 MB Vulkan / 8,9 MB CPU encajan en el mismo patrón que usa CUDA hoy).
3. Entradas iniciales: Parakeet Ultra Q8_0 y Q4_K, Parakeet Redux, OruKeet Q5_0,
   Canary 1B v2 Q5_0/Q4_K, Granite 4.1 2B NAR mini, Cohere
   Transcribe Q4_K, Zipformer no (eso es sherpa).

### Fase 3 - opcional

- sherpa-onnx para los Zipformer (los más rápidos en CPU) y SenseVoice.
- Convertir los checkpoints NVIDIA que no tienen GGUF oficial una sola vez y subirlos a
  un repositorio propio con `convert_model.py`.

---

## 11. Checklist para añadir una entrada

1. ¿Existe ya el artefacto GGUF/ggml publicado? Si no, hay que convertirlo una vez (y
   entonces el modelo necesita un `RemoteFileName` que nadie más tiene; mejor publicar
   el GGUF en un repositorio propio con su licencia y su `NOTICE`).
2. `python tools/hf_model_files.py <repo> --ext gguf` → **revisión exacta, bytes y SHA-256**.
3. Comprobar la licencia de los pesos, no la del código:
   - Parakeet / Canary / OruKeet (base): CC-BY-4.0.
   - OruKeet: el repo trae `LICENSE` + `LICENSE-WEIGHTS` (CC-BY-SA-4.0 según CrispASR).
   - Nemotron: NVIDIA Open Model License.
   - Granite, Cohere Transcribe, FireRedASR2, Zipformer: Apache-2.0.
   - `granite-speech-5.0-470m-turboctc-nc` y varios fine-tunes: **no comercial**, fuera.
4. Idiomas: el texto de la entrada debe decir solo los que el modelo soporta de verdad
   (Fun-ASR-MLT y SenseVoice no tienen español; Dolphin es oriental; los `-en` de NVIDIA
   son solo inglés).
5. `python tools/gguf_header.py <repo> <fichero>` → `general.architecture` dice qué
   runtime lo lee, y `general.license` confirma la licencia sin salir a la web.
6. Tamaño en la tarjeta: el `EstimatedBytes` de la barra de progreso tiene que ser el
   tamaño real (el `EstimateSingleFile` solo cubre los ficheros ya conocidos).

## 12. Licencias que hay que tener presentes

| Licencia | Modelos |
|---|---|
| MIT / Apache-2.0 | Whisper, Granite (4.x), Cohere Transcribe, FireRedASR2, Zipformer, Moonshine, Dolphin, SenseVoice |
| CC-BY-4.0 | Parakeet TDT/CTC/RNNT, Canary, Nemotron-3-Diarization, Sortformer, parakeet-ultra, parakeet-redux, Phonon-2 |
| CC-BY-SA-4.0 | OruKeet (pesos), según la tabla de CrispASR |
| NVIDIA Open Model License (OpenMDW 1.1) | Nemotron 3.5 ASR, Nemotron Speech Streaming EN |
| CC-BY-NC-SA-4.0 / CC-BY-NC-4.0 | `granite-speech-5.0-470m-turboctc-nc`, Raon-Speech, Dolphin-cn-dialect |

CC-BY obliga a citar al autor; CC-BY-SA obliga además a compartir igual. Nada de esto
impide distribuir el binario, pero sí obliga a que la entrada del catálogo diga de dónde
sale el modelo.

## 13. Integración implementada en la app

Parakeet Ultra Q8_0 y Q4_K usan CrispASR 0.8.40. La app descarga el runtime
Windows CPU/Vulkan con versión y SHA-256 fijados. El engranaje junto a «En uso»
permite elegir procesador o gráfica integrada (Vulkan, sin NVIDIA CUDA). La app
consulta los dispositivos al runtime, identifica la integrada y pasa su índice
explícito; no depende de que la gráfica predeterminada sea la integrada.

Whisper, OruKeet y Parakeet/NeMo permiten procesador o dedicada NVIDIA
(CUDA). Cada archivo del catálogo, incluidas sus variantes cuantizadas, conserva
su propia elección en `DictationModelDevices`. El antiguo `DictationUseGpu` sirve
como valor inicial para modelos sin preferencia; ya no hay interruptor global en
la interfaz. Una biblioteca Whisper CPU ya cargada necesita reinicio para activar
CUDA; la biblioteca CUDA admite también contextos CPU sin reiniciar.

La integración actual usa un proceso persistente, no el CLI de un solo uso de las
mediciones del apartado C.4. Al empezar a grabar, inicia la precarga en una tarea
de fondo. Al terminar, envía el audio al mismo proceso local para transcribirlo.
Los registros distinguen grabación iniciada, precarga iniciada, modelo listo y
comienzo de la transcripción. La preparación del modelo no elimina el tiempo
necesario para procesar el audio después de terminar la grabación.

Con `Keep Model loaded`, la app precarga al iniciar y conserva el modelo. Sin ese
ajuste, cada dictado reinicia `Release after inactivity` (15–600 segundos).
Desactivar Keep Model Loaded conserva el modelo actual hasta vencer ese plazo.
La cancelación detiene la inferencia nativa; el siguiente dictado recrea el worker.
Cambiar de modelo o de dispositivo sustituye el proceso cuando corresponde.

Las 20 comprobaciones de integración cubren precarga durante la fase Listening,
reutilización de procesos, retención, reinicio del contador, liberación por
inactividad, cancelación, recuperación, cambio entre CPU y Vulkan, persistencia
XML independiente por modelo e identificación explícita de la integrada. Se ejecutan
con el runtime instalado y un audio local mono PCM16 a 16 kHz:

```powershell
dotnet run --project tools/DictationChecks/DictationChecks.csproj -c Release -p:Platform=x64 -- <modelo.gguf> <audio.wav>
```

La sección Modelos de voz tiene encabezado superior y lista de ancho completo.
El modo `--ui` verifica el engranaje y sus menús reales, las elecciones independientes
y la distribución WPF en inglés/oscuro y español/claro a distintos anchos, sin
alterar el archivo de ajustes del usuario:

```powershell
dotnet run --project tools/DictationChecks/DictationChecks.csproj -c Release -p:Platform=x64 -- --ui
```

`graphify-out/` contiene resultados regenerables de exploración y se conserva
localmente, fuera del control de versiones.
