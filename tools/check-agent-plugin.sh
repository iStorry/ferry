#!/usr/bin/env sh
# Starts a published agent with the sample plugin installed and talks to it as the PC does: the proof that a plugin
# loads in the real build, not only in a debug one.
# Usage: check-agent-plugin.sh <agent binary> <sample .ferryplugin>
set -eu
agent=$1
package=$2
root=$(mktemp -d)
trap '"$agent" agent stop --root "$root" >/dev/null 2>&1 || true; rm -rf "$root"' EXIT
mkdir -p "$root/plugins"
cp "$package" "$root/plugins/ferry.sample.ferryplugin"

"$agent" agent run --root "$root" 2> "$root/agent.err" &
tries=0
until grep -q 'plugin ferry.sample' "$root/agent.err" 2>/dev/null; do
  tries=$((tries + 1))
  [ "$tries" -lt 40 ] || { echo "The agent didn't say anything about the plugin:" >&2; cat "$root/agent.err" >&2; exit 1; }
  sleep 0.5
done
grep -q 'plugin ferry.sample .* loaded' "$root/agent.err" || { echo "The plugin wasn't loaded:" >&2; cat "$root/agent.err" >&2; exit 1; }

version=$(sed -n 's/.*agent \([^ ]*\) starting.*/\1/p' "$root/agent.err" | head -n 1)
answers=$( { printf '%s\n' "{\"id\":1,\"command\":\"Hello\",\"version\":\"$version\"}"; sleep 1
             printf '%s\n' '{"id":2,"command":"Read","link":"https://files.example/not-a-page"}'; sleep 3; } \
           | "$agent" agent attach --root "$root")
echo "$answers"
echo "$answers" | grep -q '"id":"ferry.sample","version":"[0-9.]*","sha256":"[0-9a-f]*"}' || { echo "The hello doesn't list the plugin as running." >&2; exit 1; }
echo "$answers" | grep -q 'page on files.example' || { echo "The sample's site didn't answer for its link." >&2; exit 1; }
echo "The published agent loaded the sample plugin and asked it about a link."
