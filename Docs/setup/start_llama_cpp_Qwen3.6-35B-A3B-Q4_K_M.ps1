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