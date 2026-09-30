"""Defaults for the folder sync tool."""

import os

# How long one transfer may take before it is abandoned.
DEFAULT_TIMEOUT_SECONDS = 30

# How many times a failed transfer is tried again.
MAX_RETRIES = 3

# Where the logs go: under SYNC_HOME when it is set, the user's home folder otherwise.
LOG_DIR = os.path.join(os.environ.get("SYNC_HOME", os.path.expanduser("~")), "logs")

# How many files are sent in one batch unless --batch-size says otherwise.
BATCH_SIZE = 500
