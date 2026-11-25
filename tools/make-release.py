#!/usr/bin/env python
"""Builds the "for the mod's users" version of the mod, the one that goes to master.

The development branch has things players do not need: the ScriptEngine hot-reload support (Lifecycle, the Attach* methods),
frame-time measurement (PerfMonitor, FrameProfiler, the [PERF] scene report), diagnostics (camera diagnostic, chord probe), the
panel's test buttons, the lab and the other tools, and the developer notes. This script takes the code of the current commit
(HEAD) and writes a copy without them.

How the code says what is development-only:
  * the lines between  // RELEASE-STRIP-BEGIN  and  // RELEASE-STRIP-END  are removed;
  * PerfMonitor.Begin/End/Tick/Lap, Lifecycle.Start and TickTest calls are removed;
  * the files in DELETE_FILES / DELETE_DIRS are removed (and PerfMonitor.cs is replaced by a stub that only measures time);
  * the files in KEEP_FROM_MASTER (claude.md) stay as they are on master.

Use:
    python tools/make-release.py --check     # build the copy in a scratch folder and compile it; changes nothing in git
    python tools/make-release.py --commit    # the same, then commit it on a local branch on top of origin/master
                                             # (never pushes: pushing master is done on purpose, afterwards)

The rule: master only ever gets what this script makes from the development branch.
A commit on master that this script did not make (for example a pull request) is refused until it has been merged into
the development branch: otherwise the next release would silently throw it away.
"""
import os
import re
import shutil
import subprocess
import sys
import tarfile
import tempfile

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), ".."))
SCRATCH = os.path.join(tempfile.gettempdir(), "armor-fragments-release")
EXPORT = os.path.join(SCRATCH, "export")
WORKTREE = os.path.join(SCRATCH, "master")
BRANCH = "release/for-master"
TRAILER = "Release-From:"

DELETE_DIRS = ["tools"]
DELETE_FILES = [
    "CREATE_SP2_MOD.md",
    "CRIAR_MOD_SP2.md",
    "Armor Mod/Lifecycle.cs",          # ScriptEngine hot reload: unloading the plugin
    "Armor Mod/FrameProfiler.cs",      # per-script frame timing
    "Armor Mod/CameraDiagnostic.cs",   # the panel's "Camera diagnostic" button
    "Armor Mod/ChordProbe.cs",         # [CHORD] log lines of the simulated shot
]
KEEP_FROM_MASTER = ["claude.md"]       # the developer's notes are not part of the release

STUBS = {
    "Armor Mod/PerfMonitor.cs": '''using System.Diagnostics;

namespace ArmorFragments
{
    // The release build has no frame-time monitor; only the stopwatch helper that a few log lines use remains.
    internal static class PerfMonitor
    {
        public static double Since(long timestamp)
        {
            return (Stopwatch.GetTimestamp() - timestamp) * 1000.0 / Stopwatch.Frequency;
        }
    }
}
''',
}

STATEMENTS = [
    re.compile(r'PerfMonitor\.(?:Begin|End|Tick)\(\);'),
    re.compile(r'PerfMonitor\.Lap\("[^"]*"\);'),
    re.compile(r'Lifecycle\.Start\(\);'),
    re.compile(r'\bTickTest\(\);'),
]
# Names that must not appear anywhere in the release's code.
FORBIDDEN = ["Lifecycle", "FrameProfiler", "CameraDiagnostic", "ChordProbe", "PerfSceneReport", "RELEASE-STRIP",
             "PerfMonitor.Lap", "PerfMonitor.Begin", "PerfMonitor.Events"]


def run(args, cwd=ROOT, check=True, capture=True):
    result = subprocess.run(args, cwd=cwd, check=False, text=True, encoding="utf-8",
                            stdout=subprocess.PIPE if capture else None, stderr=subprocess.STDOUT if capture else None)
    if check and result.returncode != 0:
        raise SystemExit("command failed (%d): %s\n%s" % (result.returncode, " ".join(args), result.stdout or ""))
    return result.stdout.strip() if capture else ""


def export_head():
    """The files of HEAD (as committed) in EXPORT."""
    if os.path.isdir(EXPORT):
        shutil.rmtree(EXPORT)
    os.makedirs(EXPORT)
    archive = os.path.join(SCRATCH, "head.tar")
    run(["git", "archive", "--format=tar", "-o", archive, "HEAD"])
    with tarfile.open(archive) as tar:
        tar.extractall(EXPORT)
    os.remove(archive)


def strip_file(path):
    with open(path, encoding="utf-8-sig", newline="") as handle:
        text = handle.read()
    had_bom = open(path, "rb").read(3) == b"\xef\xbb\xbf"
    newline = "\r\n" if "\r\n" in text else "\n"
    out = []
    skipping = False
    for line in text.split(newline):
        marker = line.strip()
        if marker == "// RELEASE-STRIP-BEGIN":
            if skipping:
                raise SystemExit("%s: RELEASE-STRIP-BEGIN inside another block" % path)
            skipping = True
            continue
        if marker == "// RELEASE-STRIP-END":
            if not skipping:
                raise SystemExit("%s: RELEASE-STRIP-END without a BEGIN" % path)
            skipping = False
            continue
        if skipping:
            continue
        changed = line
        for pattern in STATEMENTS:
            changed = pattern.sub("", changed)
        if changed != line and not changed.strip():
            continue                                  # the line was only that statement
        out.append(changed.rstrip() if changed != line else line)
    if skipping:
        raise SystemExit("%s: RELEASE-STRIP-BEGIN never closed" % path)
    result = newline.join(out)
    if result != text:
        with open(path, "w", encoding="utf-8-sig" if had_bom else "utf-8", newline="") as handle:
            handle.write(result)
        return True
    return False


def make_release_tree():
    export_head()
    for directory in DELETE_DIRS:
        shutil.rmtree(os.path.join(EXPORT, directory), ignore_errors=True)
    for name in DELETE_FILES + KEEP_FROM_MASTER:
        target = os.path.join(EXPORT, name)
        if os.path.exists(target):
            os.remove(target)
    for name, content in STUBS.items():
        with open(os.path.join(EXPORT, name), "w", encoding="utf-8", newline="\n") as handle:
            handle.write(content)

    changed = 0
    for folder, _, files in os.walk(os.path.join(EXPORT, "Armor Mod")):
        for file in files:
            if file.endswith(".cs") and strip_file(os.path.join(folder, file)):
                changed += 1
                path = os.path.join(folder, file)
                with open(path, encoding="utf-8-sig") as handle:
                    empty = not handle.read().strip()
                if empty:
                    os.remove(path)                   # the whole file was development-only
    for folder, folders, files in os.walk(os.path.join(EXPORT, "Armor Mod"), topdown=False):
        if not os.listdir(folder):
            os.rmdir(folder)
    print("stripped %d source files" % changed)

    problems = []
    for folder, _, files in os.walk(os.path.join(EXPORT, "Armor Mod")):
        for file in files:
            if not file.endswith(".cs"):
                continue
            path = os.path.join(folder, file)
            with open(path, encoding="utf-8-sig") as handle:
                for number, line in enumerate(handle, 1):
                    if line.strip().startswith("//"):
                        continue                      # a comment may still mention them
                    for word in FORBIDDEN:
                        if word in line:
                            problems.append("%s:%d: %s" % (os.path.relpath(path, EXPORT), number, line.strip()[:110]))
    if problems:
        print("\n".join(problems))
        raise SystemExit("the release code still mentions development-only things (above): fix the markers")


def build():
    project = os.path.join(EXPORT, "Armor Mod", "ArmorFragments.csproj")
    output = run(["dotnet", "build", project, "-c", "Release", "--nologo"], check=False)
    errors = [l for l in output.splitlines() if ": error " in l]
    print("\n".join(errors[:30]) if errors else "release build: 0 errors")
    if errors:
        raise SystemExit("the release copy does not compile")
    dll = os.path.join(EXPORT, "Armor Mod", "bin", "Release", "netstandard2.1", "ArmorFragments.dll")
    print("DLL: %s (%d bytes)" % (dll, os.path.getsize(dll)))


def check_master_is_ours():
    """Every commit on master that is not in the development branch must have been made by this script."""
    run(["git", "fetch", "origin"])
    base = run(["git", "merge-base", "origin/master", "HEAD"])
    commits = run(["git", "rev-list", "--first-parent", base + "..origin/master"]).split()
    for sha in commits:
        if TRAILER not in run(["git", "log", "-1", "--format=%B", sha]):
            raise SystemExit("master has a commit that is not a release (%s %s).\n"
                             "Merge it into the development branch first, then run this again."
                             % (sha[:7], run(["git", "log", "-1", "--format=%s", sha])))


def commit():
    if run(["git", "status", "--porcelain"]):
        raise SystemExit("the working tree has uncommitted changes: commit them first (the release is made from HEAD)")
    check_master_is_ours()
    head = run(["git", "rev-parse", "--short", "HEAD"])
    version = re.search(r'Version = "([^"]+)"', open(os.path.join(ROOT, "Armor Mod", "ArmorFragmentsPlugin.cs"), encoding="utf-8-sig").read()).group(1)

    run(["git", "worktree", "prune"])
    if os.path.isdir(WORKTREE):
        run(["git", "worktree", "remove", "--force", WORKTREE])
    run(["git", "branch", "-f", BRANCH, "origin/master"])
    run(["git", "worktree", "add", WORKTREE, BRANCH])

    keep = set(KEEP_FROM_MASTER) | {".git"}
    for name in os.listdir(WORKTREE):
        if name in keep:
            continue
        target = os.path.join(WORKTREE, name)
        shutil.rmtree(target) if os.path.isdir(target) else os.remove(target)
    for name in os.listdir(EXPORT):
        if name in keep:
            continue
        source = os.path.join(EXPORT, name)
        target = os.path.join(WORKTREE, name)
        if os.path.isdir(source):
            shutil.copytree(source, target, ignore=shutil.ignore_patterns("bin", "obj"))
        else:
            shutil.copy2(source, target)

    run(["git", "add", "-A"], cwd=WORKTREE)
    if not run(["git", "status", "--porcelain"], cwd=WORKTREE):
        print("master already has exactly this release: nothing to commit")
        return
    message = ("Armor Fragments %s (release build)\n\n"
               "Made by tools/make-release.py from the development branch: without the hot-reload support, the frame-time\n"
               "measurement, the diagnostics, the lab and the other developer tools.\n\n"
               "%s %s\n" % (version, TRAILER, head))
    run(["git", "commit", "-q", "-m", message], cwd=WORKTREE)
    print("committed on branch %s: %s" % (BRANCH, run(["git", "log", "--oneline", "-1"], cwd=WORKTREE)))
    print("to publish it:  git push origin %s:master" % BRANCH)


def main():
    os.makedirs(SCRATCH, exist_ok=True)
    make_release_tree()
    build()
    if "--commit" in sys.argv:
        commit()


if __name__ == "__main__":
    main()
