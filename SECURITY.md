# Security

## Reporting a problem

Use **Report a vulnerability** under this repository's Security tab. It opens a private report that only the maintainers
see. Please don't open a public issue for something that could put people at risk.

Say what you did, what happened, and which version (About, in the app).

Only the latest release is fixed.

## What Ferry does and doesn't protect

- **Updates** are downloaded from this repository's releases and checked against the release's `SHA256SUMS.txt` before
  they run. The builds aren't code-signed yet.
- **Logins** (a server's key passphrase, a plugin's API key) are kept encrypted for your Windows user, never in
  `settings.json`. The queue and the settings themselves are plain files.
- **The app lock** is a lock on the window, not encryption.
- **Server mode** talks to your server over SSH with OpenSSH, and remembers the server's key: a changed key stops it.
- **Installed plugins are programs.** One runs on your server as the user Ferry signs in as and can do whatever that
  user can. There is no sandbox. A signature on a package says its files are as the holder of a key left them; it
  doesn't say who they are or that the plugin is safe. Install only what you trust, and consider a user on the server
  that is Ferry's alone.
