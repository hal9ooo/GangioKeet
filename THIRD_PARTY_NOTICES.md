# Third-party notices

GangioKeet itself is distributed under the MIT license; see `LICENSE`.

The Windows package does **not** bundle model weights. The client downloads
missing model files on demand from Hugging Face; `models/candidates.json` pins
the revisions and SHA-256 hashes, `models/integrity.json` pins the catalog,
runtime and cleanup policy, and `models/selection.json` is the default seed for
the per-user model combination. Native runtimes are pinned by
`native/runtime-lock.json`.

## Model weights

### Default stack: Qwen3-ASR-1.7B + Gemma 4 E2B-it

#### Qwen3-ASR-1.7B

- Source checkpoint: `Qwen/Qwen3-ASR-1.7B`, revision
  `0e0a596e3959c3f3f2ab7d443a5b6f0f4a4bba6f`.
- Artifact used: `handy-computer/Qwen3-ASR-1.7B-gguf`, file
  `qwen3_asr_1_7b/Qwen3-ASR-1.7B-F16.gguf`.
- License: Apache License 2.0,
  <https://www.apache.org/licenses/LICENSE-2.0>. The full text ships under
  `licenses/models/APACHE-2.0.txt`; attribution is in `licenses/models/MODELS.txt`.
- SHA-256: `edb09c29b8f73822c639168d5ef72aa2dccdf8b4e48fc4b8518885352ff62c71`.

#### Gemma 4 E2B-it (QAT Q4_0)

- Source checkpoint: `google/gemma-4-E2B-it`, artifact repository
  `google/gemma-4-E2B-it-qat-q4_0-gguf`, revision
  `675cff42a74c774d6cb76f76d8eacb49b48c9b93`.
- File: `gemma4_e2b/gemma-4-E2B_q4_0-it.gguf`.
- License: Apache License 2.0, <https://ai.google.dev/gemma/docs/gemma_4_license>.
  The full Apache text ships under `licenses/models/APACHE-2.0.txt`; attribution
  is in `licenses/models/MODELS.txt`.
- SHA-256: `fa401b55b07ee70a54c6dae3903c783a6e65064312529ea57175cb5f8dec6634`.

### Alternate stack: Parakeet TDT 0.6B v3 + Qwen3-1.7B

#### Parakeet TDT 0.6B v3

- Source checkpoint: `nvidia/parakeet-tdt-0.6b-v3`, artifact repository
  `handy-computer/parakeet-tdt-0.6b-v3-gguf`, revision
  `d8d8c32cb2017c57e5307767343e97870f8a7f53`.
- File: `parakeet_v3/parakeet-tdt-0.6b-v3-F16.gguf`.
- License: Creative Commons Attribution 4.0 International (CC-BY-4.0),
  <https://creativecommons.org/licenses/by/4.0/>. The full text ships under
  `licenses/models/CC-BY-4.0.txt`; attribution is in `licenses/models/MODELS.txt`.
- SHA-256: `d9ec7e2c39da7b4fec3e01e71e82c7f7bbe97741e08c0c054091bf3d520a41d3`.

#### Qwen3-1.7B

- Source checkpoint: `Qwen/Qwen3-1.7B`, artifact repository
  `Qwen/Qwen3-1.7B-GGUF`, revision
  `90862c4b9d2787eaed51d12237eafdfe7c5f6077`.
- File: `qwen3_1_7b/Qwen3-1.7B-Q8_0.gguf`.
- License: Apache License 2.0,
  <https://www.apache.org/licenses/LICENSE-2.0>. The full text ships under
  `licenses/models/APACHE-2.0.txt`; attribution is in `licenses/models/MODELS.txt`.
- SHA-256: `061b54daade076b5d3362dac252678d17da8c68f07560be70818cace6590cb1a`.

### Experimental stack: Nemotron 3.5 Transcribe 30B A4B

#### Nemotron 3.5 Transcribe 30B A4B (IQ4_NL + F16 encoder)

- Source checkpoint: `nvidia/NVIDIA-Nemotron-3.5-Transcribe-30B-A4B`, artifact
  repository `AtomicChat/NVIDIA-Nemotron-3.5-Transcribe-30B-A4B-GGUF`, revision
  `dbd06c5cad7a2f33c8e06be2fb3ef814dbd9aa59`.
- Files: `NVIDIA-Nemotron-3.5-Transcribe-30B-A4B-AD-IQ4_NL.gguf` (19,678,656,672
  bytes) and `NVIDIA-Nemotron-3.5-Transcribe-30B-A4B-Encoder-F16.gguf`
  (1,670,410,080 bytes).
- License: OpenMDW-1.1, <https://openmdw.ai/license/1-1/>.
- SHA-256: `0c47d4ab6c783e7606ec03714b354fab0b2052027a7ea1991e7e2d3009c345ad`
  and `4221029c8a565c4a80c507ffa760c716e595acf663f9f4273ce69910050e7ba2`.
- The client downloads these files on demand; they are never bundled in the
  installer.

## Native inference runtimes

### transcribe.cpp 0.4.1

- Project: <https://github.com/handy-computer/transcribe.cpp>, commit
  `4d7c1ccefd39e5a9ea36cca5f8fefc6dcc14b99b`.
- License: MIT, copyright (c) 2026 The transcribe.cpp authors.
- The release archive also bundles ggml (MIT, copyright (c) 2023-2026 The ggml
  authors), miniz (MIT, copyright 2013-2014 RAD Game Tools and Valve Software;
  copyright 2010-2014 Rich Geldreich and Tenacious Software LLC) and silero-vad
  (MIT, copyright (c) 2020-present Silero Team).
- The complete texts ship under `native/transcribe/licenses/`.

### llama.cpp and LLamaSharp 0.30.0

- llama.cpp/ggml: <https://github.com/ggml-org/llama.cpp>, MIT, copyright
  (c) 2023-2026 The ggml authors. The Vulkan and CPU ggml DLLs are shipped as
  `lgml-base.dll`, `lgml-vulkan.dll`, `lgml-cpu.dll` and `lgml.dll`; only the
  exported symbol namespace was changed so they can coexist with the
  transcribe.cpp ggml DLLs.
- LLamaSharp: <https://github.com/SciSharp/LLamaSharp>, version 0.30.0,
  commit `d03b6cbca3c66357e2ed9ee9d0324f5eaeabfd4f`, MIT, copyright (c) 2025
  SciSharp STACK. The managed assembly is patched from that package with the
  same namespace-only change and shipped as `LLamaSharp.dll`.
- The complete MIT texts ship under `licenses/llama/`.

### NeMo-Speech.cpp (experimental, downloaded on demand)

- Project: <https://github.com/NVIDIA/NeMo-Speech.cpp>, nightly build
  `0.2.0+nightly.66d50d9`, asset
  `nemo-speech-nightly-windows-x86_64-vulkan.zip`, SHA-256
  `fddd6be11c47e98855bac10106fee64dcdc255e596e232023c76b120a9597ab0`.
- License: Apache License 2.0, copyright (c) 2026 NVIDIA CORPORATION &
  AFFILIATES. The downloaded archive carries the complete texts under
  `share/licenses/nemo-speech/`, including its ggml and llama.cpp components.
- The runtime is downloaded only when Nemotron is selected, under
  `%LOCALAPPDATA%\GangioKeet\runtimes\nemo-speech\`. Its ggml and llama DLLs are
  renamed to a private `ngml`/`nlama` namespace at extraction so they can
  coexist in-process with the transcribe.cpp and LLamaSharp runtimes.

## Managed dependencies

All are MIT-licensed:

- NAudio 3.3.0, copyright 2020 Mark Heath.
- Hardcodet.NotifyIcon.Wpf 2.0.1, copyright (c) Philipp Sumi.
- YamlDotNet 18.1.0, copyright (c) 2008-2014 Antoine Aubry and contributors.
- CommunityToolkit.HighPerformance, copyright (c) .NET Foundation and
  Contributors.
- Microsoft.Extensions.* packages, copyright (c) .NET Foundation and
  Contributors.

Their full license texts ship under `licenses/managed/`; package metadata is
available from the NuGet package sources pinned in
`client-windows/src/GangioKeet.Client/GangioKeet.Client.csproj` and
`client-windows/src/GangioKeet.Inference/GangioKeet.Inference.csproj`.

## .NET runtime

The package is published self-contained for `win-x64` and redistributes the
.NET 10 runtime under the MIT license. Microsoft's `LICENSE.txt` and
`ThirdPartyNotices.txt` for the runtime ship under `licenses/dotnet/`.

## Microsoft Visual C++ 2022 runtime

The transcribe.cpp and llama.cpp DLLs link dynamically to the Microsoft Visual
C++ runtime. The package installs these unmodified Microsoft binaries app-local
next to the application and next to each native runtime directory:
`msvcp140.dll`, `vcruntime140.dll`, `vcruntime140_1.dll` and `vcomp140.dll`
(version 14.51.36247.0). This requires no machine-wide runtime installation and
no administrator elevation.

They are redistributed under the Microsoft Visual C++ Redistributable license
terms: <https://learn.microsoft.com/cpp/windows/latest-supported-vc-redist>.
Origin, hashes and the license pointer ship under `licenses/msvc/`.

## FLEURS

`tests/fixtures/stt_eval/public/fleurs_it/manifest.jsonl` contains transcriptions
derived from the FLEURS dataset (`google/fleurs`, config `it_it`, split `test`,
revision `70bb2e84b976b7e960aa89f1c648e09c59f894dd`).

FLEURS is distributed under the Creative Commons Attribution 4.0 International
license (CC-BY-4.0): <https://creativecommons.org/licenses/by/4.0/>.

Citation: Conneau et al., *FLEURS: Few-shot Learning Evaluation of Universal
Representations of Speech*, 2022, <https://arxiv.org/abs/2205.12446>.

The audio files are **not** distributed in this repository; only the manifest
and its provenance metadata are tracked, and the FLEURS manifest is not part of
the installed product payload.
