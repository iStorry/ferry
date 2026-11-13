#!/usr/bin/env sh
# Fails when a name that belongs to a private plugin, or the app's old name, is anywhere in the repository.
# The patterns are written so that this file doesn't match itself.
set -eu
root=$(cd "$(dirname "$0")/.." && pwd)
found=$(grep -rIil -e 'b[u]nkr' -e 'bal[b]ums' -e 'f[9]5' -e 'bl[u]nkr' -e 'inde[x]ers\?\b' \
  --exclude-dir=.git --exclude-dir=bin --exclude-dir=obj --exclude-dir=artifacts "$root" || true)
if [ -n "$found" ]; then
  echo "These files name something that doesn't belong in this repository:" >&2
  echo "$found" >&2
  exit 1
fi
echo "No private names."
