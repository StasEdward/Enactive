# Configuration

The sync tool reads its defaults from `app/config.py`.

- **Timeout.** One transfer may take up to 60 seconds before it is abandoned.
- **Retries.** A failed transfer is tried again up to 3 times.
- **Logs.** Logs are written to `$APP_HOME/logs`, or to `~/logs` when the variable is not set.
- **Batch size.** Files are sent 500 at a time.
