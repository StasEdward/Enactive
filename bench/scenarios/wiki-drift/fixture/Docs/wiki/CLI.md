# Command line

Run the tool as `python -m app.cli --target <folder>`. The options are defined in `app/cli.py`.

| Option | Meaning |
| --- | --- |
| `--target` | Where the files go. Required. |
| `--dry-run` | Say what would be copied, copy nothing. |
| `--verbose` | Log every file as it is sent. |
| `--batch-size` | Files per batch. Default 1000. |
