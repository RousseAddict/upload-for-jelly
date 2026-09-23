# upload-for-jelly

A Jellyfin server plugin that **accepts photo uploads and files them into a photo
library**. C#/.NET 9, loaded by the Jellyfin server.

It is the transport half of `../jellypic` (an iOS photo app on top of Jellyfin), but
**a separate project on a separate release cycle**: Jellyfin's, not the app's. The app
is a consumer of the endpoint, never a dependency of this repository.

Sibling plugin, same author, same shape: `../heic-for-jelly`. Read-path only, no HTTP
surface. **This one is its opposite** — see the ground rules below.

## The contract is not in this repository

`../jellypic/docs/11-writer-upload-contract.md` is the specification: the three
endpoints, the on-disk layout, the hash rules, the staging directory, the error
taxonomy, the authorisation model. It was written before either side existed, and both
sides implement it.

**Read it before writing any code here.** Anything this repository decides that
contradicts it is a contract change, and a contract change is edited *there* first —
otherwise the app and the plugin drift and only a device test finds out.

## Layout

```
CLAUDE.md
manifest.json                the plugin catalogue users subscribe to
docs/
  01-plan.md                 milestones, what each one proves, risks
src/Jellyfin.Plugin.UploadForJelly/
  Plugin.cs                  identity, configuration ownership, the dashboard page
  PluginConfiguration.cs     the library allowlist, empty by default
  Configuration/
    configPage.html          embedded; holds no data, fetches everything it shows
  meta.json                  the single file to edit when cutting a release
tools/
  build.sh                   sync + build over ssh, --deploy installs
  publish-manifest.py        records a release in manifest.json
```

## Ground rules

**The HTTP surface is the point, and that inverts `heic-for-jelly`'s first rule.**
That plugin advertises *"no endpoint is added"*, which is what lets it inherit auth,
per-library ACL and caching for free. Here there is no such shelter: every endpoint is
a write path reachable by any authenticated user, so each one earns its guards
explicitly.

- `[Authorize]` on every action. Anonymous access is never correct here.
- The target library must be in the configured allowlist **and** visible to the caller.
  Two independent checks; neither substitutes for the other.
- Nothing derived from a request — filename, target id, capture date — reaches a path
  without being rebuilt from validated components. Never concatenated, never trusted.
  The same rule the app applies to server-supplied filenames, taken from the other end.
- **No absolute server path is ever returned**, with exactly one carve-out:
  `GET /Libraries`, which requires elevation and exists so an administrator can tell two
  same-named libraries apart before opening one for writing. It has its own DTO,
  `LibraryInfo`, so the caller-facing `TargetInfo` cannot grow a path by accident.
- The plugin keeps **no database**. The filename carries the hash and the filesystem is
  the index (contract §6). Anything that wants a database is a design smell here.

**Verify Jellyfin behaviour by reading source, not search results.** `curl` against
`raw.githubusercontent.com` works; `gh` CLI is not installed. Pin to release tags
(`v10.11.0`), never `master`. The git trees API works unauthenticated for finding
paths: `https://api.github.com/repos/jellyfin/jellyfin/git/trees/v10.11.0?recursive=1`.

**Every claim in `docs/` cites `file:symbol`, never `file:line`.** Line numbers rot
across releases; symbols survive.

**Third-party code is scanned before use, not after.** That rule is what caught an
unauthenticated arbitrary-file-read in the prior art surveyed for `heic-for-jelly`.
Applies to NuGet packages too. This plugin should need none beyond `Jellyfin.*`.

**Commits carry no co-author trailer**, no "Generated with" footer, no tool
attribution. Message = the change and its reason. (Carried over from jellypic.)

## Conventions

jellypic's "no comments in app sources" rule **does not apply here**. StyleCop stays
on, with its SA16xx documentation rules and `TreatWarningsAsErrors`, and **every public
member carries an XML doc comment**. Chosen for conformance with the Jellyfin plugin
ecosystem, should this ever be published.

Write doc comments that earn their place — why a guard exists, what a caller must not
assume. `<summary>Gets or sets the name.</summary>` satisfies the analyzer and tells
the reader nothing; prefer a sentence that would have saved someone an hour.

## Status

**U0 through U3b are passed on the real server**, including a 1.7 MB iPhone HEIC that
arrived, was filed, was resolved by `heic-for-jelly` and appeared as a `Photo` item with
its EXIF date and camera intact. The error taxonomy was exercised end to end: replay `200`,
`422` for hash mismatch / empty body / extension off the allowlist, `404` for an unknown
target, `400` for a malformed hash, and a `../../etc` device slug that came back rebuilt as
`etc`. Next is U4 (`Have`).

U1 proved the only thing it was meant to: net9.0 against `Jellyfin.Controller` 10.11.6
is the ABI this server loads, cleanly, with no controller in the assembly.

U2 added the first endpoint. Three findings from the sources shaped it, all in
`docs/02-jellyfin-internals.md` with citations — read that before touching the response
shape or the path resolution:

- **`Accept: application/json` is served PascalCase**, so every DTO property carries
  `[JsonPropertyName]`. Without it the app decodes nothing and reports the wrong problem.
- **`CollectionFolder.Path` is the virtual folder in Jellyfin's config directory, not the
  media folder.** `PhysicalLocations` is the media. `UploadTargets.WriteRoot` is the only
  place that decision is made.
- **The id a client stored from `/UserViews` is the collection folder id**, dashless.

U3 added the upload. Three more findings, `docs/02` §7–§10 — the first is the trap:

- **`ReportFileSystemChangeBeginning`/`Complete` would hide the new photo for 45 seconds.**
  The plugin never brackets; it reports the destination once, after the rename.
- **The queued refresh is debounced 60 s and restarts on every sibling**, so the `201`
  returns a key and a relative path and deliberately no item id.
- **Kestrel's 30 MB body limit is unmodified by Jellyfin.** Raised to 4 GiB with
  `[RequestSizeLimit]`, declared rather than removed.

Two further traps were paid for during U3's own testing, both in `docs/02` — read §4
before touching path resolution and §13 before touching library enumeration:

- **`PhysicalLocations` includes `Path` as its first element**, so "the first entry that
  exists" resolves to Jellyfin's config directory, always. Every signal said success: the
  probe passed, `Targets` said `writable`, the `201` returned a plausible relative path.
  Only `GET /Libraries`, which returns an absolute path, exposed it.
- **`ILibraryManager.RootFolder` is not where libraries live** — that is the
  `AggregateFolder` over physical media roots. Libraries hang off `GetUserRootFolder()`.
  Getting this wrong returned `200 []`, which the page rendered as a legitimate "no library
  found".

U3b added the configuration page, pulled forward out of U5 because **a plugin whose
allowlist can only be edited with a `curl` is not deployable by anyone but its author**.
One finding, `docs/02` §11–§12, and it decided the design:

- **`DashboardController.GetDashboardConfigurationPage` carries no `[Authorize]`**, and
  Jellyfin sets no fallback policy. The page's HTML is public. So the page holds no data at
  all and fetches every library name, path and probe verdict from `GET /Libraries`, which
  requires elevation. Baking the libraries into the page — the obvious implementation —
  would have published the host's directory layout to anonymous callers.

Milestones and what each is meant to prove are in `docs/01-plan.md`.

Server: Jellyfin **10.11.6** under podman, amd64.

Building: the dev Mac is Big Sur and no supported .NET runs there, so `tools/build.sh`
rsyncs to a Monterey box and compiles over ssh. `--deploy` installs the dll into
Jellyfin but never restarts it — a restart cuts playback, so it stays a manual call.

## Releasing

The repository is also its own Jellyfin plugin catalogue: `manifest.json` at the root
of `main` is the URL users subscribe to. Cutting a release means editing **meta.json
only** — `version` and `changelog` — bumping `AssemblyVersion`/`FileVersion` to match,
then pushing the tag `v<version>` (four segments, e.g. `v0.2.0.0`). The `release`
workflow builds, zips, publishes a GitHub release and commits the new entry into
`manifest.json`. It refuses to run if the three versions disagree.

Two invariants it guards, both learned from the official plugin template:

- `Jellyfin.Controller`/`Jellyfin.Model` carry `<ExcludeAssets>runtime</ExcludeAssets>`.
  Without it the build copies Jellyfin's own assemblies into `bin/`, and a plugin that
  ships a second copy of the host's types does not register. The Package step fails if
  more than one dll lands in the output.
- The zip holds the dll and `meta.json` at its root with no enclosing directory:
  `InstallationManager.InstallPackageInternal` extracts it straight into the plugin
  folder, after checking the zip's **MD5** against the manifest's `checksum`.

`targetAbi` stays at the exact version the plugin was built and tested against.
Jellyfin drops any entry whose `targetAbi` exceeds the running server
(`InstallationManager.GetPackages`), so lowering it to widen reach would publish a
claim nobody has verified.

**Version starts at 0.x on purpose.** The app's own endpoint probe treats a 2xx that
does not decode as "update the plugin", so the contract's shape is allowed to move
while both halves are being built. 1.0.0.0 is the moment the endpoints stop moving.
