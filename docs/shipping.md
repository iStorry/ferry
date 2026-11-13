# Shipping

## Releasing

1. Put the version's notes in `CHANGELOG.md` under a heading with the version (`## 0.2.0`). The release notes are that
   section, and the release stops if it is missing or empty. `## 0.2.0 (pre-release)` publishes a pre-release: the app's
   update check asks GitHub for the latest release, which is never a pre-release, so only people who install it from its
   page get it. The tag stays plain (`v0.2.0`).
2. Tag and push a plain version: `git tag v0.2.0 && git push origin v0.2.0`. Only `vX.Y.Z` tags release, as the updater
   and the agent download look files up by that version. `.github/workflows/release.yml` runs the tests, then builds:
   - `Ferry-0.2.0-setup.exe`: the app, per user (no admin).
   - `Ferry-0.2.0-win-x64-portable.zip`: the same app to run from a folder. It doesn't update itself.
   - `ferry-0.2.0-{linux-x64,linux-arm64}.tar.gz`: the server's agent, which server mode fetches when it isn't beside
     the app.
   - `SHA256SUMS.txt`: the app's updater and the agent download check files against it.
3. The version in every build is the tag's (`-p:Version=`), so the app compares it with the latest release.

The repository has to be public for this to reach anyone: the update check and the agent download read GitHub's API
without signing in.

## How each piece is built

| Piece | Script | Notes |
|---|---|---|
| Helper | `tools/publish-cli.ps1` (Windows), `tools/publish-cli.sh` (Linux) | `src/Ferry.Agent`, one file each. On Windows it answers ssh's password prompt and a browser's extension, and is Native AOT (MSVC linker, found through `vswhere`). On Linux it is the server's agent: a trimmed self-contained single file, because a Native AOT build would need the glibc of the machine that built it; it cross-builds, so both Linux RIDs come from one x64 runner, and it carries the torrent engine, which Native AOT can't. |
| Agent, from Windows | `tools/publish-agent.ps1` | The same Linux single files, for a build of your own. The PC and the agent must be the same build, so build again after a change. |
| App | `tools/publish-app.ps1 [-Prebuilt] [-SmokeTest]` | Self-contained (.NET and the Windows App SDK inside), ReadyToRun, not trimmed. Builds the helper (`cli\`) and the agents (`agent\`) from the same source first; `-Prebuilt` takes them from `artifacts/cli`. Puts the OpenSSH client server mode runs in `openssh\` (`tools/fetch-openssh.ps1`: a pinned release, refused unless its SHA-256 is the pinned one). |
| Setup | `installer/build.ps1 -Version x.y.z` | Inno Setup 6 (`installer/ferry.iss`). Waits for a running Ferry to close; `/RELAUNCH=1` starts it again after an update. |

The builds aren't code-signed yet, so Windows SmartScreen asks before the setup runs the first time.
