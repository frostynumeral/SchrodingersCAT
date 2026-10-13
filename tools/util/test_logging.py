"""Tests for tools.util.logging. Run from the repository root: python3 -m unittest discover -s tools -t ."""

from __future__ import annotations

import io
import logging
import os
import tempfile
import unittest
from contextlib import redirect_stdout
from pathlib import Path
from unittest import mock

from tools.util import logging as catlog


def _write(folder: Path, name: str, text: str) -> None:
    (folder / name).write_text(text, encoding="utf-8")


class ReadingTests(unittest.TestCase):
    def setUp(self) -> None:
        self._temp = tempfile.TemporaryDirectory()
        self.folder = Path(self._temp.name)
        self.current = self.folder / "schrodingerscat.log"
        _write(self.folder, "schrodingerscat.2.log",
               "2026-10-09 10:00:00.001 +02:00 [INF] [100] SchrodingersCat.App.Program: Started\n")
        _write(self.folder, "schrodingerscat.1.log",
               "2026-10-09 11:00:00.001 +02:00 [ERR] [100] SchrodingersCat.Memory: Lookup failed\n"
               "System.IO.IOException: disk\n   at Foo.Bar()\n")
        _write(self.folder, "schrodingerscat.log",
               "2026-10-10 09:00:00.500 +02:00 [WRN] [200] Avalonia.Binding: Something\n"
               "2026-10-10 09:00:01.500 +02:00 [DBG] [200] SchrodingersCat.App: Detail\n")

    def tearDown(self) -> None:
        self._temp.cleanup()

    def run_main(self, *args: str) -> list[str]:
        out = io.StringIO()
        with redirect_stdout(out):
            self.assertEqual(catlog.main(["--path", str(self.current), *args]), 0)
        return out.getvalue().splitlines()

    def test_log_files_are_oldest_first(self) -> None:
        names = [file.name for file in catlog.log_files(self.current)]
        self.assertEqual(names, ["schrodingerscat.2.log", "schrodingerscat.1.log", "schrodingerscat.log"])

    def test_file_left_aside_mid_rotation_comes_before_the_current_one(self) -> None:
        _write(self.folder, "schrodingerscat.log.rotating", "")
        names = [file.name for file in catlog.log_files(self.current)]
        self.assertEqual(names[-2:], ["schrodingerscat.log.rotating", "schrodingerscat.log"])

    def test_numbers_sort_as_numbers(self) -> None:
        _write(self.folder, "schrodingerscat.10.log", "")
        self.assertEqual(catlog.log_files(self.current)[0].name, "schrodingerscat.10.log")

    def test_exception_lines_belong_to_their_entry(self) -> None:
        entries = list(catlog.read_entries(self.current))
        self.assertEqual([entry.level for entry in entries], ["INF", "ERR", "WRN", "DBG"])
        self.assertEqual(entries[1].details, "System.IO.IOException: disk\n   at Foo.Bar()")
        self.assertEqual(entries[1].pid, 100)
        self.assertEqual(entries[1].category, "SchrodingersCat.Memory")

    def test_entry_prints_as_it_was_written(self) -> None:
        text = (self.folder / "schrodingerscat.1.log").read_text(encoding="utf-8").rstrip("\n")
        self.assertEqual(str(list(catlog.read_entries(self.current))[1]), text)

    def test_text_before_the_first_entry_is_ignored(self) -> None:
        _write(self.folder, "schrodingerscat.log", "   at Cut.Off()\n2026-10-10 09:00:00.500 +02:00 [INF] [1] A: b\n")
        entries = [entry for entry in catlog.read_entries(self.current) if entry.file == self.current]
        self.assertEqual(len(entries), 1)
        self.assertEqual(entries[0].details, "")

    def test_level_filter(self) -> None:
        lines = self.run_main("--level", "warning", "--short")
        self.assertEqual([line.split(" [")[1][:3] for line in lines], ["ERR", "WRN"])

    def test_since_and_tail(self) -> None:
        lines = self.run_main("--since", "2026-10-10T00:00+02:00", "--tail", "1")
        self.assertEqual(lines, ["2026-10-10 09:00:01.500 +02:00 [DBG] [200] SchrodingersCat.App: Detail"])

    def test_grep_searches_details_and_category_ignores_case(self) -> None:
        lines = self.run_main("--grep", "disk", "--category", "memory")
        self.assertEqual(len(lines), 3)

    def test_pid_filter(self) -> None:
        self.assertEqual(len(self.run_main("--pid", "200")), 2)

    def test_missing_log_fails(self) -> None:
        with mock.patch("sys.stderr", io.StringIO()):
            self.assertEqual(catlog.main(["--path", str(self.folder / "none.log")]), 1)

    def test_level_names(self) -> None:
        for name, level in [("trace", "TRC"), ("debug", "DBG"), ("info", "INF"), ("WRN", "WRN"), ("critical", "CRT")]:
            self.assertEqual(catlog._level_arg(name), level)


class SetupTests(unittest.TestCase):
    def setUp(self) -> None:
        self._temp = tempfile.TemporaryDirectory()
        self.file = Path(self._temp.name) / "sub" / "tool.log"

    def tearDown(self) -> None:
        for handler in list(logging.getLogger("catlog.test").handlers):
            handler.close()
        self._temp.cleanup()

    def test_lines_read_back_through_the_reader(self) -> None:
        with mock.patch("sys.stderr", io.StringIO()):
            log = catlog.setup("catlog.test", file=self.file)
            log.info("hello")
            try:
                raise ZeroDivisionError("boom")
            except ZeroDivisionError:
                log.exception("failed")
        entries = list(catlog.read_entries(self.file))
        self.assertEqual([(entry.level, entry.message) for entry in entries], [("INF", "hello"), ("ERR", "failed")])
        self.assertEqual(entries[0].pid, os.getpid())
        self.assertIn("ZeroDivisionError: boom", entries[1].details)

    def test_level_comes_from_the_environment(self) -> None:
        with mock.patch.dict(os.environ, {"SCHRODINGERSCAT_LOG_LEVEL": "Debug"}):
            self.assertEqual(catlog.setup("catlog.test").level, logging.DEBUG)
        with mock.patch.dict(os.environ, {"SCHRODINGERSCAT_LOG_LEVEL": "nonsense"}):
            self.assertEqual(catlog.setup("catlog.test").level, logging.INFO)

    def test_setup_again_replaces_handlers(self) -> None:
        catlog.setup("catlog.test")
        self.assertEqual(len(catlog.setup("catlog.test").handlers), 1)

    def test_standard_logging_is_not_shadowed(self) -> None:
        self.assertTrue(Path(logging.__file__).parent.name == "logging" and "tools" not in Path(logging.__file__).parts)


if __name__ == "__main__":
    unittest.main()
