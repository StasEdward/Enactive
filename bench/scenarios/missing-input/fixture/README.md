# Shop

Four modules under `src/`: billing, catalog, orders, notify.

`build/` is generated and not kept in git. `build/modules.json` - every module and what it imports from the others - is
written by:

    python tools/list_modules.py
