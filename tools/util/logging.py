"""Logging helpers for SchrödingersCAT's Python tools.

Two halves:

* ``setup(name)`` gives a Python script a logger that writes lines in the app's format
  (``2026-10-10 14:03:12.345 +02:00 [INF] [1234] category: message``), to stderr and optionally
  to a file. The level comes from ``SCHRODINGERSCAT_LOG_LEVEL``, as in the app.
* ``read_entries()`` and the command line read the app's own log
  (``SchrodingersCAT/logs/schrodingerscat.log`` in the local application data folder, with its
  rotated files ``schrodingerscat.1.log`` …), oldest line first, and filter them.

It is a module in a package, so it never shadows the standard library's ``logging``: run it from
the repository root as ``python3 -m tools.util.logging``, or import ``tools.util.logging``.

    python3 -m tools.util.logging                          # every line, oldest first
    python3 -m tools.util.logging --level WRN --tail 20    # the last 20 warnings or worse
    python3 -m tools.util.logging --since 2026-10-10 --category Memory --grep timeout
    python3 -m tools.util.logging --folder                 # print the log folder
"""

from __future__ import annotations

import argparse
import logging
import os
import re
import sys
import tempfile
from collections import deque
from dataclasses import dataclass
from datetime import datetime
from pathlib import Path
from typing import Iterable, Iterator

__all__ = ["LEVELS", "Entry", "default_log_path", "log_files", "read_entries", "setup", "main"]

# The app's level names (RollingFileLoggerProvider.LevelName), lowest first.
LEVELS = ["TRC", "DBG", "INF", "WRN", "ERR", "CRT"]

# Microsoft.Extensions.Logging's names, which SCHRODINGERSCAT_LOG_LEVEL uses, and Python's levels.
_ENV_LEVELS = {
    "trace": logging.DEBUG - 5,
    "debug": logging.DEBUG,
    "information": logging.INFO,
    "warning": logging.WARNING,
    "error": logging.ERROR,
    "critical": logging.CRITICAL,
    "none": logging.CRITICAL + 10,
}

_TRACE = logging.DEBUG - 5
logging.addLevelName(_TRACE, "TRACE")

_LINE = re.compile(
    r"^(?P<time>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3} [+-]\d{2}:\d{2}) "
    r"\[(?P<level>[A-Za-z]+)\] \[(?P<pid>\d+)\] (?P<category>[^:]*): (?P<message>.*)$"
)


# --- Writing ---------------------------------------------------------------------------------


class _AppFormatter(logging.Formatter):
    """Formats records the way the app writes its log lines."""

    _NAMES = {_TRACE: "TRC", logging.DEBUG: "DBG", logging.INFO: "INF", logging.WARNING: "WRN",
              logging.ERROR: "ERR", logging.CRITICAL: "CRT"}

    def format(self, record: logging.LogRecord) -> str:
        time = datetime.fromtimestamp(record.created).astimezone()
        offset = time.strftime("%z")
        stamp = f"{time:%Y-%m-%d %H:%M:%S}.{time.microsecond // 1000:03d} {offset[:3]}:{offset[3:]}"
        level = self._NAMES.get(record.levelno, record.levelname)
        line = f"{stamp} [{level}] [{record.process}] {record.name}: {record.getMessage()}"
        if record.exc_info:
            line += "\n" + self.formatException(record.exc_info)
        return line.replace("\r\n", "\n").replace("\r", "\n")


def _env_level() -> int:
    value = os.environ.get("SCHRODINGERSCAT_LOG_LEVEL", "").strip().lower()
    return _ENV_LEVELS.get(value, logging.INFO)


def setup(name: str, level: int | None = None, file: str | os.PathLike[str] | None = None) -> logging.Logger:
    """Returns the logger ``name``, writing app-format lines to stderr and, if given, appended to ``file``.

    ``level`` defaults to ``SCHRODINGERSCAT_LOG_LEVEL`` (Information if unset or unknown). Calling it
    again for the same name replaces the handlers instead of adding more.
    """
    logger = logging.getLogger(name)
    logger.setLevel(_env_level() if level is None else level)
    logger.propagate = False
    for handler in list(logger.handlers):
        logger.removeHandler(handler)
        handler.close()

    formatter = _AppFormatter()
    handlers: list[logging.Handler] = [logging.StreamHandler(sys.stderr)]
    if file is not None:
        Path(file).parent.mkdir(parents=True, exist_ok=True)
        handlers.append(logging.FileHandler(file, mode="a", encoding="utf-8"))
    for handler in handlers:
        handler.setFormatter(formatter)
        logger.addHandler(handler)
    return logger


# --- Reading ---------------------------------------------------------------------------------


@dataclass
class Entry:
    """One log entry: its first line, parsed, and the lines that follow it (an exception)."""

    time: datetime
    level: str
    pid: int
    category: str
    message: str
    details: str = ""
    file: Path | None = None

    @property
    def severity(self) -> int:
        return LEVELS.index(self.level) if self.level in LEVELS else len(LEVELS)

    def __str__(self) -> str:
        offset = self.time.strftime("%z")
        stamp = f"{self.time:%Y-%m-%d %H:%M:%S}.{self.time.microsecond // 1000:03d} {offset[:3]}:{offset[3:]}"
        head = f"{stamp} [{self.level}] [{self.pid}] {self.category}: {self.message}"
        return head + ("\n" + self.details if self.details else "")


def _local_app_data() -> Path | None:
    # What .NET's LocalApplicationData gives on each system, without requiring the folder to exist.
    if sys.platform == "win32":
        value = os.environ.get("LOCALAPPDATA")
        return Path(value) if value else None
    home = Path.home()
    if sys.platform == "darwin":
        return home / "Library" / "Application Support"
    value = os.environ.get("XDG_DATA_HOME")
    return Path(value) if value and os.path.isabs(value) else home / ".local" / "share"


def default_log_path() -> Path:
    """The app's current log file (RollingFileLoggerProvider.DefaultPath)."""
    base = _local_app_data() or Path(tempfile.gettempdir())
    return base / "SchrodingersCAT" / "logs" / "schrodingerscat.log"


def log_files(path: str | os.PathLike[str] | None = None) -> list[Path]:
    """The log's files that exist, oldest first: ``.N.log`` … ``.1.log``, a file left aside mid-rotation, the current one."""
    current = Path(path) if path is not None else default_log_path()
    pattern = re.compile(re.escape(current.stem) + r"\.(\d+)" + re.escape(current.suffix) + "$")
    rotated = []
    if current.parent.is_dir():
        for candidate in current.parent.iterdir():
            match = pattern.match(candidate.name)
            if match:
                rotated.append((int(match.group(1)), candidate))
    files = [file for _, file in sorted(rotated, reverse=True)]
    # A crash mid-rotation leaves the current file aside; it is newer than .1 and older than a fresh current file.
    files += [file for file in (Path(str(current) + ".rotating"), current) if file.is_file()]
    return files


def _parse(lines: Iterable[str], file: Path | None) -> Iterator[Entry]:
    entry: Entry | None = None
    extra: list[str] = []
    for raw in lines:
        line = raw.rstrip("\n")
        match = _LINE.match(line)
        if match:
            if entry is not None:
                entry.details = "\n".join(extra)
                yield entry
            entry, extra = Entry(
                time=datetime.strptime(match["time"], "%Y-%m-%d %H:%M:%S.%f %z"),
                level=match["level"],
                pid=int(match["pid"]),
                category=match["category"],
                message=match["message"],
                file=file,
            ), []
        elif entry is not None:
            extra.append(line)
        # Text before the first entry (a file cut in the middle of an exception) has nothing to belong to.
    if entry is not None:
        entry.details = "\n".join(extra)
        yield entry


def read_entries(path: str | os.PathLike[str] | None = None) -> Iterator[Entry]:
    """Every entry of the log and its rotated files, oldest first. Unreadable files are skipped."""
    for file in log_files(path):
        try:
            with open(file, encoding="utf-8", errors="replace") as stream:
                yield from _parse(stream, file)
        except OSError as error:
            print(f"Skipped {file}: {error.strerror or error}", file=sys.stderr)


# --- Command line ----------------------------------------------------------------------------


def _level_arg(value: str) -> str:
    # The app's names, or the start of a full one (debug, info, warning, critical, …).
    level = value.strip().upper()[:3]
    level = {"TRA": "TRC", "DEB": "DBG", "WAR": "WRN", "CRI": "CRT"}.get(level, level)
    if level not in LEVELS:
        raise argparse.ArgumentTypeError(f"unknown level {value!r} (one of {', '.join(LEVELS)})")
    return level


def _since_arg(value: str) -> datetime:
    try:
        parsed = datetime.fromisoformat(value)
    except ValueError as error:
        raise argparse.ArgumentTypeError(f"not a date or time: {value!r}") from error
    return parsed if parsed.tzinfo else parsed.astimezone()


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(prog="python3 -m tools.util.logging",
                                     description="Read and filter SchrödingersCAT's log files, oldest first.")
    parser.add_argument("--path", type=Path, help="the current log file (default: the app's)")
    parser.add_argument("--folder", action="store_true", help="print the log folder and exit")
    parser.add_argument("--level", type=_level_arg, default="TRC", help="minimum level: TRC, DBG, INF, WRN, ERR, CRT")
    parser.add_argument("--since", type=_since_arg, help="only entries from then on (ISO date or time, local)")
    parser.add_argument("--category", help="only categories containing this (case-insensitive)")
    parser.add_argument("--pid", type=int, help="only this process")
    parser.add_argument("--grep", type=re.compile, help="only entries whose message or details match this regex")
    parser.add_argument("--tail", type=int, metavar="N", help="only the last N matching entries")
    parser.add_argument("--short", action="store_true", help="leave out exception details")
    args = parser.parse_args(argv)

    current = args.path or default_log_path()
    if args.folder:
        print(current.parent)
        return 0
    if not log_files(current):
        print(f"No log files at {current}", file=sys.stderr)
        return 1

    minimum = LEVELS.index(args.level)
    category = args.category.lower() if args.category else None

    def wanted(entry: Entry) -> bool:
        return (entry.severity >= minimum
                and (args.since is None or entry.time >= args.since)
                and (category is None or category in entry.category.lower())
                and (args.pid is None or entry.pid == args.pid)
                and (args.grep is None or bool(args.grep.search(entry.message) or args.grep.search(entry.details))))

    matching: Iterable[Entry] = (entry for entry in read_entries(current) if wanted(entry))
    if args.tail is not None:
        matching = deque(matching, maxlen=max(args.tail, 0))
    try:
        for entry in matching:
            print(f"{entry}".split("\n", 1)[0] if args.short else entry)
    except BrokenPipeError:  # piped into head
        sys.stderr.close()
    return 0


if __name__ == "__main__":
    sys.exit(main())
