# Contributing

Thanks for looking. Ferry is small and has one maintainer, so a short issue first saves work on both sides.

## Before a pull request

- **A bug:** open an issue with what you did, what you expected and what happened, and the version.
- **A new site:** it is usually better as a plugin of your own than as code here. `docs/writing-plugins.md` says how;
  `samples/Ferry.Plugins.Sample` is one to copy from.
- **A change to the app or the engine:** say what and why in an issue before writing it.

## Building

The .NET 10 SDK pinned in `global.json`; the app builds on Windows only.

```powershell
dotnet build Ferry.slnx
dotnet test Ferry.slnx
dotnet format Ferry.slnx --verify-no-changes
```

CI runs the same three. Set `FERRY_CONFIG_PATH` to a file in a scratch folder before running a build of the app, so it
doesn't share settings with an installed Ferry.

## How the code is written

- One project per plugin, with no UI in it. The core never names a plugin, and neither does the app.
- No reflection where a contract will do; JSON is source-generated.
- Tests never contact a real site. They run against local fake servers.
- Comments say why, in a line. Messages are for the person reading them: what happened, and what they can do.
- A change deletes what it replaces.

By contributing you agree that your work is under the MIT License, as the rest is.
