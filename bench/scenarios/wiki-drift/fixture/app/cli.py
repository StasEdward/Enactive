"""Command line of the folder sync tool."""

import argparse

from app import config


def parse(argv=None):
    parser = argparse.ArgumentParser(prog="sync", description="Copy a folder to a target, in batches.")
    parser.add_argument("--target", required=True, help="where the files go")
    parser.add_argument("--dry-run", action="store_true", help="say what would be copied, copy nothing")
    parser.add_argument("--debug", action="store_true", help="log every file as it is sent")
    parser.add_argument("--batch-size", type=int, default=config.BATCH_SIZE, help="files per batch")
    return parser.parse_args(argv)


if __name__ == "__main__":
    print(parse())
