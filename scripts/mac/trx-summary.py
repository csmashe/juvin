#!/usr/bin/env python3
"""Summarizes the TRX files of a dotnet test run for the session kit.

Prints the totals, each failed test, and each skip reason with its count, and NOTE: lines for the
summary. Exits 1 when a test failed, when a test was skipped for a reason not allowed, or when a
required test did not run and pass.

Usage: trx-summary.py <trx folder> [--allow-skip REASON]... [--require NAME]... [--require-only]
  --allow-skip   a skip reason that is expected on macOS (repeatable)
  --require      a substring of a test name that must match at least one test, all of them passed
  --require-only check only the required tests, ignoring other failures and skips
"""

import argparse
import collections
import glob
import os
import sys
import xml.etree.ElementTree as ET

NS = "{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}"


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("folder")
    parser.add_argument("--allow-skip", action="append", default=[])
    parser.add_argument("--require", action="append", default=[])
    parser.add_argument("--require-only", action="store_true")
    args = parser.parse_args()

    files = sorted(glob.glob(os.path.join(args.folder, "**", "*.trx"), recursive=True))
    if not files:
        print("NOTE: no TRX files under %s" % args.folder)
        return 1

    results = []
    for path in files:
        for result in ET.parse(path).getroot().iter(NS + "UnitTestResult"):
            output = result.find(NS + "Output")
            text = " ".join(t.strip() for t in output.itertext() if t.strip()) if output is not None else ""
            results.append((result.get("testName", ""), result.get("outcome", ""), text))

    counts = collections.Counter(outcome for _, outcome, _ in results)
    skips = collections.Counter(text or "(no reason)" for _, outcome, text in results if outcome == "NotExecuted")
    failed = [name for name, outcome, _ in results if outcome not in ("Passed", "NotExecuted")]
    ok = True

    if not args.require_only:
        print("NOTE: %d tests: %d passed, %d failed, %d skipped (%d TRX files)"
              % (len(results), counts["Passed"], len(failed), counts["NotExecuted"], len(files)))
        for name in failed:
            print("failed: %s" % name)
        if failed:
            ok = False
            print("NOTE: failed: %s" % ", ".join(failed[:10]))
        for reason, count in sorted(skips.items()):
            allowed = any(reason.startswith(a) for a in args.allow_skip)
            print("skipped %d: %s%s" % (count, reason, "" if allowed else " (not allowed)"))
            if not allowed:
                ok = False
                print("NOTE: %d tests skipped for a reason not allowed on macOS: %s" % (count, reason))

    for required in args.require:
        matching = [(name, outcome) for name, outcome, _ in results if required in name]
        if not matching:
            ok = False
            print("NOTE: no test matches %s" % required)
        elif any(outcome != "Passed" for _, outcome in matching):
            ok = False
            print("NOTE: %s did not pass everywhere: %s" % (required, sorted(set(o for _, o in matching))))
        else:
            print("NOTE: %s ran and passed (%d tests)" % (required, len(matching)))

    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
