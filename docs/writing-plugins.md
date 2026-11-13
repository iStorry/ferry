# Writing a plugin

A plugin teaches Ferry a site: which links are its own, and how to get from one of them to the file. The plugins that
come with Ferry are compiled in. One you write is a package, `name.ferryplugin`, that people install.

**Where this stands (0.0.1):** all of it is built, and little of it has been used. Packing, checking and the agent
loading a package have been run. Putting a package on a real server over SSH has not.

## Installing one

In Ferry: Plugins, the + beside the search box, then From a file or From a link (https only). A link to a file of a
GitHub release in a repository that isn't public works too: Ferry asks for an access token that can read that
repository, uses it for the download, and doesn't keep it. Ferry reads the package,
shows who it says it is from, whether it is signed, and what a plugin can do, and installs it if you agree. The next
time Ferry connects to your server it puts the package there, in `~/.ferry/plugins`, and starts the agent again.
Removing it is on the plugin's own page.

The PC decides what the server has: a package that this PC doesn't have installed is taken off the server when Ferry
connects, however it got there. `~/.ferry/agent.log` on the server says whether each one loaded, and why not.

## What a plugin is

- It runs **on the server only**, inside the agent Server Support puts there. The Windows app never runs its code: it
  reads the package's manifest for the plugin's name, its links, its settings and its secrets.
- It has no UI. Its switch, settings and secrets are drawn by the app from what it declares.
- It runs as the user Ferry signs in to the server as, with everything that user can do. There is no sandbox.
- Its secrets (an API key, a login) are typed in on the PC and kept there, encrypted. When Ferry connects they go to the
  server over SSH and stay in the agent's memory: nothing of them is written on the server, and an agent that started
  again has none until the PC is back. In code, `context.Secret(...)` gives one.
- A plugin that throws is reported and the agent carries on: its link is refused with the reason, and other plugins and
  downloads are untouched. One whose name, site ids or settings are already taken by another plugin isn't used.

## The contract

Build a class library for `net10.0` against `Ferry.Core`, with one public class that is an `IFerryPlugin` and has a
public constructor that takes nothing. `samples/Ferry.Plugins.Sample` is one to copy from.

| Member | What it is |
|---|---|
| `Id` | Its own name among all plugins, never shown: lower-case letters and digits with dots or dashes, such as `acme.example`. |
| `Publisher` | Who made it. |
| `Plugin` | A `SitePlugin`: its name, its on/off setting, its other settings and secrets, how it is shown (`About`), and its sites (`ISite`). |
| `Claims` | The hosts and link schemes that are its own, so the app can tell without running it. A host covers the hosts under it. |

An `ISite` says whether a link is its own (`Claims`), reads it as a file (`TryParseFile`), and finds where the bytes are
(`ResolveAsync`). Ferry's engine does the downloading: segments, retries, resume, the speed limit.

`Claims` can also name one of the plugin's own settings, of hosts, whose hosts are its own too. That is for a site whose
addresses change: people add a new one in the plugin's settings, without waiting for a new build.

### A site to browse

A plugin whose site has albums can give Ferry's Browser page something to show. It implements `IBrowsingPlugin` as well:

| Member | What it is |
|---|---|
| `Pages` | Two patterns over a link's path, one for an album's page and one for a file's, each naming the page in a group called `slug`. The app tells pages apart by them without running the plugin, so they are patterns that can't take long: no lookarounds or back-references. |
| `Browse` | What reads the site (`ISiteBrowsing`): an album's or file's page, the site's list of albums with a search, an album's picture and its size. |

The server reads all of it and watches albums for new files, also while the PC is off. One installed plugin is browsed
at a time, the first that is on.

### Titles to look up

A plugin whose site lists titles with their versions can tell Ferry's Library page what is out of date. The Library reads
a folder on the PC, where each title is a folder or archive named with its version, as in "Name (v1.2)". The plugin
implements `ILookupPlugin`: `Lookup` gives what searches its site for a name and answers with the titles it lists, newest
first, each as a name and a version. The server does the asking; the PC compares. One installed plugin is asked, the
first that is on.

Reference `Ferry.Core` with `Private="false"`, as the sample does. Ferry has its own, and a package doesn't carry one.

Setting names start with `FERRY_`. `PluginApi.Version` is the contract's number; a package says which it was built
against, and a Ferry with another number refuses it and says so.

## Packing

```powershell
dotnet build samples/Ferry.Plugins.Sample -c Release
dotnet run --project tools/Ferry.PluginPack -- samples/Ferry.Plugins.Sample/bin/Release/net10.0/Ferry.Plugins.Sample.dll --out artifacts/plugins
```

The tool loads the plugin, writes `plugin.json` from it, and zips that with the plugin's assemblies:

```
plugin.json     the manifest
lib/            the plugin's .dll files, and no folders
plugin.sig      a signature, if it was signed
```

`--update <https link>` records where a newer package is to be looked for.

Then it reads the package as an install would. `--check <file>` does only that, for any package:

```powershell
dotnet run --project tools/Ferry.PluginPack -- --check artifacts/plugins/ferry.sample-0.0.1.ferryplugin
```

## Signing

Signing is optional. A signature says the files are as the holder of a key left them; it says nothing about who that is.

```powershell
dotnet run --project tools/Ferry.PluginPack -- --new-key my-plugins.pem
dotnet run --project tools/Ferry.PluginPack -- <plugin.dll> --key my-plugins.pem
```

Keep the `.pem` to yourself. The tool prints the key's fingerprint, which is what a package signed with it shows as
"signed by". A package whose files were changed after signing is refused.

## Limits

| | |
|---|---|
| Package | 64 MB |
| Unpacked | 256 MB |
| Files | 500 |
