"""Validate the release version before it reaches MSBuild, package paths, or GitHub environment output."""
import os
import re

version = os.environ.get("INPUT_VERSION") or os.environ.get("GITHUB_REF_NAME", "").removeprefix("v")
if not re.fullmatch(r"(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)(?:-[0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*)?", version):
    raise SystemExit("Release version must be a three-part SemVer, optionally with a prerelease suffix.")
with open(os.environ["GITHUB_ENV"], "a", encoding="utf-8") as output:
    output.write("RELEASE_VERSION=" + version + "\n")
print("Validated release version " + version)
