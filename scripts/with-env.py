#!/usr/bin/env python3
"""Run a command with KEY=VALUE lines from an env file added to the environment.

Usage: with-env.py <env-file> <command> [args...]
Values are taken literally (no shell parsing), so ';' and spaces in connection strings are safe.
"""
import os
import sys

if len(sys.argv) < 3:
    sys.exit("usage: with-env.py <env-file> <command> [args...]")

env = dict(os.environ)
with open(sys.argv[1], encoding="utf-8") as f:
    for line in f:
        line = line.rstrip("\n")
        if not line.strip() or line.lstrip().startswith("#"):
            continue
        key, sep, value = line.partition("=")
        if sep:
            env[key.strip()] = value

os.execvpe(sys.argv[2], sys.argv[2:], env)
