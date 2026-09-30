# Module dependencies

From `build/modules.json` (written by `python tools/list_modules.py`).

| Module | Depends on |
| --- | --- |
| billing | — |
| catalog | — |
| orders | billing, catalog |
| notify | orders |
