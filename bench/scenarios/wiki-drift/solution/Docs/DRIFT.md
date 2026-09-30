# Drift between Docs/wiki and app/

## Docs/wiki/Configuration.md

1. **Timeout.** The page says a transfer may take up to 60 seconds; `app/config.py` sets `DEFAULT_TIMEOUT_SECONDS = 30`.
2. **Log folder variable.** The page says logs go to `$APP_HOME/logs`; the code reads `SYNC_HOME` (`os.environ.get("SYNC_HOME", ...)`).

Retries (3) and batch size (500) match the code.

## Docs/wiki/CLI.md

1. **Logging flag.** The page lists `--verbose`; `app/cli.py` defines `--debug` for logging every file.
2. **Batch size default.** The page says the default is 1000; `--batch-size` defaults to `config.BATCH_SIZE`, which is 500.

`--target` (required) and `--dry-run` match the code.
