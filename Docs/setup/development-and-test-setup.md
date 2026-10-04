# Enactive · Development & Test Setup

> The project's development and testing environment: hardware, tools, and model configuration.
>
> **Snapshot: October 1, 2026** · Windows x64 · local execution + cloud planning and review

**Ryzen 9 9950X3D** · **RTX 5080 / 16 GB VRAM** · **64 GB DDR5-6000**  
**Claude Sonnet 5** → planning and review · **Qwen3.6-35B-A3B / Q4_K_M** → execution

This document records the current workstation and saved Enactive configuration. It describes the environment used to develop and test the project; historical run results and model performance measurements are recorded separately.

## Contents

- [Workstation](#workstation)
- [Development environment](#development-environment)
- [Models and workflow](#models-and-workflow)
- [Local model settings](#local-model-settings)
- [Launching llama.cpp](#launching-llamacpp)
- [Snapshot sources](#snapshot-sources)

## Workstation

| Component | Configuration |
| :--- | :--- |
| **CPU** | AMD Ryzen 9 9950X3D |
| **Cores / threads** | 16 physical cores / 32 logical processors |
| **Motherboard** | GIGABYTE X870E AORUS MASTER |
| **RAM** | 64 GiB: 2 × 32 GiB CORSAIR |
| **Memory modules** | `CMH64GX5M2D6000C40` |
| **Configured memory speed** | 6000 MT/s, as reported by Windows |
| **GPU** | NVIDIA GeForce RTX 5080 |
| **VRAM** | 16 GB; NVIDIA-SMI reports 16,303 MiB |
| **NVIDIA driver** | 616.92 |
| **CUDA UMD** | 13.4, as reported by NVIDIA-SMI; the driver interface version, not an installed CUDA Toolkit version |
| **Operating system** | Microsoft Windows 11 Pro Insider Preview, 64-bit |
| **Windows version / build** | `10.0.26220` / `26220` |

### Storage

| Drive | Interface | Capacity |
| :--- | :--- | ---: |
| Corsair MP700 PRO XT | NVMe SSD | 1 TB |
| WD Blue SN580 | NVMe SSD | 500 GB |
| Samsung SSD 870 EVO | SATA SSD | 2 TB |

Drive capacities use the manufacturers' decimal units. RAM capacity is expressed in GiB.

## Development environment

| Tool | Recorded version |
| :--- | :--- |
| **Project platform** | .NET 10, `net10.0` |
| **Active .NET SDK in the project directory** | `10.0.401` |
| **Other installed SDKs** | `10.0.203`, `9.0.313` |
| **global.json configuration** | Baseline `10.0.100`, `rollForward: latestFeature` |
| **Desktop UI** | Avalonia `12.1.2` |
| **PowerShell in the current session** | `7.6.5` |
| **Git** | `2.53.0.windows.2` |
| **llama.cpp / llama-server** | `0.5.0-dev`, build `11160`, commit `70c4e1582` |
| **llama-server build toolchain** | Clang `20.1.8`, Windows x86_64 |
| **Repository HEAD when the snapshot was collected** | `9970dbf` |

## Models and workflow

The primary workflow, taken from the application's saved `Bindings`:

| Stage | Provider | Model |
| :--- | :--- | :--- |
| **Planning** | Anthropic | `claude-sonnet-5` |
| **Result review** | Anthropic | `claude-sonnet-5` |
| **Light step execution** | llama.cpp, local | `qwen3.6-35b-a3b` |
| **Heavy step execution** | llama.cpp, local | `qwen3.6-35b-a3b` |

The cloud model plans the work and reviews results. The local model executes steps using Enactive's tools. These are the saved stage assignments; each run's history records which models actually participated.

### Cloud model

| Setting | Value |
| :--- | :--- |
| **Primary model** | `claude-sonnet-5` |
| **API** | Anthropic, `https://api.anthropic.com` |
| **Effort** | `medium` |
| **Provider context window setting** | Not explicitly configured |
| **Provider output limit setting** | Not explicitly configured |
| **Provider ID in the saved configuration** | `Antropic` — the exact spelling stored by the application |

The `developer` role has **DeepSeek `deepseek-flash`** configured as its fallback, using `https://api.deepseek.com` with provider `MaxTokens = 32768`. A configured fallback does not imply that it participated in every run.

### Local model

| Setting | Value |
| :--- | :--- |
| **Model / file** | `Qwen3.6-35B-A3B-Q4_K_M.gguf` |
| **Quantization, as named in the file** | `Q4_K_M` |
| **File size** | 20,419,565,568 bytes — approximately 20.42 GB / 19.02 GiB |
| **Server alias** | `qwen3.6-35b-a3b` |
| **Enactive provider** | `llama.cpp`, `OpenAiCompatible` |
| **Application endpoint** | `http://127.0.0.1:8080/v1` |
| **Model path on the workstation** | `D:\llama_cpp\Models\Qwen3.6-35B-A3B-Q4_K_M.gguf` |

### Saved roles

Roles have their own preferred and fallback models. These settings are listed separately from the stage assignments above.

| Role | Preferred model | Fallback | Permission level |
| :--- | :--- | :--- | :--- |
| `developer` | `llama.cpp/qwen3.6-35b-a3b` | `DeepSeek/deepseek-flash` | Execute |
| `reviewer` | `Jan/Jan-v3.5-4B-Q4_K_XL` | — | Observe |
| `ops` | `Jan/Jan-v3.5-4B-Q4_K_XL` | — | Execute |
| `writer` | `Jan/Qwen3_5-35B-A3B-Q4_K_M` | — | Execute |

Additional saved local endpoints include Jan (`127.0.0.1:1337/v1`), Ollama (`localhost:11434/v1`), and LM Studio (`localhost:1234/v1`). Their presence in the configuration does not confirm that these servers are running or assigned to the primary workflow.

## Local model settings

### In Enactive

| Setting | Value |
| :--- | :--- |
| **Declared context window** | 196,608 tokens |
| **Threshold for handing a step over to a fresh context** | 75% of the window |
| **Temperature** | `server`: the application leaves the choice to the server |
| **Reasoning sent back in conversation history** | `SendReasoningBack = true` |
| **WorkingContextTokens / AnswerReserveTokens** | Not explicitly configured |

Saved generation budgets: Action — **8192**, FileWrite — **8192**, FinalAnswer — **2048**, Handover — **8192**, Planner — **8192** tokens. These are application settings; actual token usage and response lengths vary by request.

### In the llama-server script

| Setting | Value |
| :--- | :--- |
| **Bind address / port** | `127.0.0.1:8080` |
| **Context** | `-c 196608` |
| **Parallel slots** | `-np 1` |
| **Batch / micro-batch** | `-b 4096` / `-ub 2048` |
| **CPU threads** | `-t 16` |
| **Flash Attention** | `on` |
| **K/V cache types** | `q8_0` / `q8_0` |
| **RAM cache** | `--cache-ram 16384` MiB |
| **Context checkpoints** | `--ctx-checkpoints 128`, `--checkpoint-min-step 512` |
| **Speculative mode** | `--spec-type ngram-cache` |
| **Sampling** | temperature `0.6`, top_p `0.95`, top_k `20`, min_p `0.0` |
| **Penalties** | presence `0.0`, repeat `1.0` |
| **Reasoning** | `--reasoning on --reasoning-preserve` |
| **Other arguments** | `--load-mode none --jinja --metrics` |
| **Log level** | `-lv 4` |
| **Manual layer placement** | Neither `-ngl` nor `--n-cpu-moe` is specified |

These are the configured launch arguments. Actual GPU/RAM placement and generation speed were not measured for this snapshot.

## Launching llama.cpp

A separate copy of the original script is included: [start_llama_cpp_Qwen3.6-35B-A3B-Q4_K_M.ps1](start_llama_cpp_Qwen3.6-35B-A3B-Q4_K_M.ps1).

On the workstation, it is launched with:

```powershell
& 'D:\llama_cpp\start_llama_cpp_Qwen3.6-35B-A3B-Q4_K_M.ps1'
```

The script locates `llama-server.exe` beside itself using `$PSScriptRoot` and loads the model from the specified absolute path. To use the published copy, place it in the server directory and adjust `$ModelPath` for your machine.

The server binds to the loopback address `127.0.0.1`. The script clears `LLAMA_API_KEY` and `LLAMA_ARG_API_KEY` in its own session and starts the local API without authentication. The original script is reproduced below, including its comments and arguments.

<details>
<summary><strong>Full launch script</strong></summary>

```powershell
# start_llama_cpp_Qwen3.6-35B-A3B-Q4_K_M.ps1
# Launches llama-server for Qwen3.6-35B-A3B (hybrid SSM/attention MoE) on RTX 5080 16 GB.
#
# Notes:
# - GPU layer placement is left to -fit (auto): do NOT pass -ngl / --n-cpu-moe,
#   otherwise auto-fit is disabled.
# - --cache-reuse is intentionally absent: unsupported for hybrid SSM models.

$ErrorActionPreference = 'Stop'

# ---- Configuration ---------------------------------------------------------
$ServerExe = Join-Path $PSScriptRoot 'llama-server.exe'
$ModelPath = 'd:\llama_cpp\Models\Qwen3.6-35B-A3B-Q4_K_M.gguf'
$ModelAlias = 'qwen3.6-35b-a3b'
$HostAddr  = '127.0.0.1'
$Port      = 8080
$Context   = 196608   # cheap: only 10 of 40 layers hold KV
$Batch     = 4096
$UBatch    = 2048     # benchmarked sweet spot: A-level prefill, near-B generation
$Threads   = 16       # physical cores of 9950X3D
$CacheRam  = 16384    # MiB, RAM prompt cache (slot save/restore)
$LogLevel  = 4        # use 4 only for debugging

# ---- Sanity checks ---------------------------------------------------------
if (-not (Test-Path $ServerExe)) { throw "llama-server.exe not found: $ServerExe" }
if (-not (Test-Path $ModelPath)) { throw "Model file not found: $ModelPath" }

# ---- Arguments -------------------------------------------------------------
$llamaArgs = @(
    '-m', $ModelPath
    '--alias', $ModelAlias
    '--host', $HostAddr
    '--port', "$Port"
    '-c', "$Context"
    '-np', '1'
    '-fa', 'on'
    '-ctk', 'q8_0'
    '-ctv', 'q8_0'
    '-b', "$Batch"
    '-ub', "$UBatch"
    '-t', "$Threads"
    '--cache-ram', "$CacheRam"
    '--ctx-checkpoints', '128'
    '--checkpoint-min-step', '512'
    '--spec-type', 'ngram-cache'
    '--flash-attn', 'on'

    '--temperature', '0.6'
    '--top_p', '0.95'
    '--top_k', '20'
    '--min_p', '0.0'
    '--presence_penalty', '0.0'
    '--repeat_penalty', '1.0'

    '--load-mode', 'none'
    '--jinja'
    '--reasoning', 'on'
    '--reasoning-preserve'
    '--metrics'
    '-lv', "$LogLevel"
)

# ---- Launch ----------------------------------------------------------------
# llama-server picks up the API key from the environment - clear it so the server runs without auth
Get-ChildItem Env: | Where-Object { $_.Name -in 'LLAMA_API_KEY', 'LLAMA_ARG_API_KEY' } |
    ForEach-Object { Remove-Item "Env:$($_.Name)" }

# Print the exact command line to verify every argument is passed
Write-Host "llama-server $($llamaArgs -join ' ')" -ForegroundColor Cyan

Set-Location $PSScriptRoot
& $ServerExe @llamaArgs
exit $LASTEXITCODE
```

</details>

## Snapshot sources

- Workstation specifications: Windows CIM and the physical drive inventory.
- GPU, VRAM, and driver: `nvidia-smi`.
- Models, stage assignments, and budgets: the saved `%APPDATA%\Enactive\settings.json` configuration.
- Server arguments: the original `D:\llama_cpp\start_llama_cpp_Qwen3.6-35B-A3B-Q4_K_M.ps1`.
- llama.cpp version: `llama-server.exe --version`.
- Platform and UI versions: installed SDKs, `global.json`, and solution project files.

**SHA-256 of the original script and its separate copy:**

```text
D30776F9867D22F1D207201F5D4900468E9FD60F1BFBAC672249D1344ECC86C9
```

API keys, protected key values, authorization headers, remote tokens, SMTP settings, the computer name, and serial numbers are omitted from this document.

