#!/usr/bin/env sh
# Publish the ferry CLI as one file for a Linux RID (linux-x64, linux-arm64).
# Usage: publish-cli.sh [rid] [version]. Writes artifacts/cli/ferry-<rid>.
# A self-contained single file, not trimmed, as it loads the plugins people install. Native AOT would tie the binary to the glibc of the build
# machine, so a server older than it couldn't run the agent; .NET's own runtime host is built for older ones. It also
# cross-builds, so both Linux RIDs come from one x64 machine.
set -eu
root=$(cd "$(dirname "$0")/.." && pwd)

if [ $# -gt 0 ]; then
  rid=$1
else
  case "$(uname -s)-$(uname -m)" in
    Linux-x86_64) rid=linux-x64 ;;
    Linux-aarch64 | Linux-arm64) rid=linux-arm64 ;;
    *) echo "unknown platform $(uname -s)-$(uname -m); pass a RID" >&2; exit 2 ;;
  esac
fi

out="$root/artifacts/publish/cli-$rid"
version=${2:-}
case "$rid" in
  linux-*) ;;
  *) echo "$rid is not a Linux RID; on Windows use tools/publish-cli.ps1" >&2; exit 2 ;;
esac
dotnet publish "$root/src/Ferry.Agent/Ferry.Agent.csproj" -c Release -r "$rid" -o "$out" \
  -p:ServerAgent=true -p:SelfContained=true -p:PublishSingleFile=true \
  -p:EnableCompressionInSingleFile=true ${version:+"-p:Version=$version"}
mkdir -p "$root/artifacts/cli"
cp "$out/ferry" "$root/artifacts/cli/ferry-$rid"
chmod +x "$root/artifacts/cli/ferry-$rid"
ls -l "$root/artifacts/cli/ferry-$rid"
