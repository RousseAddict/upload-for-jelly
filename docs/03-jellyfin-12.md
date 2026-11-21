# 03 — Jellyfin 12: one binary for 10.11 and 12

Verified 2026-11-20 three ways: by diffing the **`v10.11.6`** and **`v12.4`** tags of
`jellyfin` and `jellyfin-web` over everything this plugin calls or relies on; by resolving
every type and member reference of the **published 0.2.0.0 dll** against the assemblies
inside the 12.4 image; and by installing that zip, unmodified, from the catalogue into
throwaway 12.4 and 10.11.6 servers and running the whole contract against both.
Citations are `file:symbol`.

**Verdict: the published binary already serves both.** No source change, no second
build, no new release. What follows is why, what was measured, three pre-existing
behaviours the test surfaced on both versions, and the condition under which one binary
stops being enough.

The same question was answered for `heic-for-jelly` the same day
(`../heic-for-jelly/docs/05-jellyfin-12.md`). This plugin has the larger surface — an MVC
controller, authorisation, a metadata provider, a dashboard page, the library monitor —
so the diff is wider and the binary check below was added.

## 1. What Jellyfin 12 changed that touches this plugin

| Change | Where | Effect here |
|---|---|---|
| .NET 10; `Jellyfin.Controller` 12.x ships `net10.0` only | `global.json`, the package's `lib/` | a 12.x build needs net10.0; the shipped one stays net9.0 — §3 |
| **Legacy authorisation off.** A migration sets `EnableLegacyAuthorization = false` on every upgraded server, and it is the new default | `Jellyfin.Server/Migrations/Routines/DisableLegacyAuthorization.cs:PerformAsync`, `MediaBrowser.Model/Configuration/ServerConfiguration.cs:EnableLegacyAuthorization` | none: the plugin reads `IAuthorizationContext`, and the app's `UploadClient` sends `Authorization: MediaBrowser Token=…`, which still works. `X-Emby-Token`, `X-MediaBrowser-Token` and `?api_key=` stop authenticating (`Jellyfin.Server.Implementations/Security/AuthorizationContext.cs:GetAuthorizationInfoFromDictionary`); `?ApiKey=` still does |
| `"**/.*/**"` removed from the ignore list | `Emby.Server.Implementations/Library/IgnorePatterns.cs:_patterns` | the staging directory's empty `.ignore` is again the only thing hiding a file *inside* it from a change report — §1a |
| `.ignore` handling rewritten, with LRU caches | `Emby.Server.Implementations/Library/DotIgnoreIgnoreRule.cs:IsIgnoredInternal`, `ParseIgnoreFile` | none: an empty `.ignore` still ignores everything beneath it, for files as for directories |
| A reported change now also invalidates the directory cache | `Emby.Server.Implementations/IO/LibraryMonitor.cs:ReportFileSystemChanged` (`_directoryService.Invalidate`) | none; `docs/02` §7 holds — `IgnorePatterns` first, no `EnableRealtimeMonitor` gate, and `ReportFileSystemChangeComplete` still waits a hard-coded 45 s |
| Per-request culture from `Accept-Language` | `Jellyfin.Server/Startup.cs:ConfigureServices` (`RequestLocalizationOptions`), `Configure` (`UseRequestLocalization`) | none: every parse and format here passes `CultureInfo.InvariantCulture` (`UploadWriter.CapturedAt`, `NameDate`, the name built in `WriteAsync`) |
| `UsePathTrim` removed | `Jellyfin.Server/Startup.cs:Configure` | none: attribute routes match a trailing slash anyway, and the app sends none |
| OpenAPI generation on `Microsoft.OpenApi` 2 | `Jellyfin.Server/Extensions/ApiServiceCollectionExtensions.cs:AddJellyfinApiSwagger` | none: `/api-docs/openapi.json` still lists the four routes |
| Provider order cached per library and item type | `MediaBrowser.Providers/Manager/ProviderManager.cs:GetOrCreateOrderedProviders` | none: same sort, `int.MaxValue` then `GetDefaultOrder`, so `docs/02` §17 holds |
| Package name validated before install | `Emby.Server.Implementations/Updates/InstallationManager.cs:IsValidPackageDirectoryName` | none: spaces are valid; the folder is still `<name>_<version>` (`docs/02` §18) |

### 1a. The staging directory under 12

`docs/02` §10 was read at `v10.11.0`, where the list had `"**/.*"` and nothing that
reaches inside a hidden directory. **`v10.11.6` added `"**/.*/**"`**, which does, and
**`v12.4` removed it again.** So on 12 the situation is the one §10 describes: the
dot-prefixed name keeps the scanner from descending into `.upload-for-jelly/`, and only the
empty `.ignore` stops a change report on `.upload-for-jelly/x.part` — from a real-time
watcher, say — from being queued. Both mechanisms were designed to stand alone, which is
why this costs nothing. Measured on both versions: `.ignore` present and empty, no `.part`
left behind after the error cases, and no item anywhere under the staging path.

## 2. What did not change

**Byte-identical between the two tags:** `MediaBrowser.Common/Api/Policies.cs`,
`MediaBrowser.Controller/Net/IAuthorizationContext.cs` and `AuthorizationInfo.cs`,
`MediaBrowser.Controller/Library/ILibraryMonitor.cs` and `ItemUpdateType.cs`,
`MediaBrowser.Model/IO/IFileSystem.cs` and `FileSystemMetadata.cs`,
`MediaBrowser.Controller/Entities/ICollectionFolder.cs`, `Photo.cs`, `UserView.cs` and
`BaseItemExtensions.cs`, the five provider interfaces this plugin implements or is sorted
by (`ICustomMetadataProvider`, `IMetadataProvider`, `IForcedProvider`,
`IHasItemChangeMonitor`, `IHasOrder`) and `MetadataRefreshOptions.cs`,
`MediaBrowser.Model/Plugins/IHasWebPages.cs`, `PluginPageInfo.cs` and
`BasePluginConfiguration.cs`, `MediaBrowser.Common/Plugins/BasePlugin.cs` and
`BasePluginOfT.cs`, `Jellyfin.Data/Enums/CollectionType.cs`, the JSON stack of `docs/02`
§2–§3 and §14 (`JsonDefaults.cs`, `JsonGuidConverter.cs`, `JsonStringConverter.cs`, both
profile formatters), `Jellyfin.Server/Extensions/WebHostBuilderExtensions.cs` (so Kestrel's
30 MB default still stands, §9), `Jellyfin.Api/Middleware/ExceptionMiddleware.cs`, both
authentication handlers, `Emby.Server.Implementations/Plugins/PluginLoadContext.cs` and
`Emby.Photos/PhotoProvider.cs`.

**Changed files, unchanged in what is used here:**

- `Folder.GetChildren(User, bool, InternalItemsQuery)`, `Folder.AddChildren` and
  `Folder.Children` are identical, so `UploadTargets.ForUser`/`WriteRootFor` still walk the
  user's own view of the libraries (`docs/02` §3). `LibraryManager.GetUserRootFolder` is
  identical (§13).
- `CollectionFolder.PhysicalLocations` still returns `PhysicalLocationsList`, which
  `ItemResolveArgs.PhysicalLocations` still builds as `[Path, ...AdditionalLocations]`, and
  `GetPhysicalFolders` still filters with `!FileSystem.AreEqual(i, Path)` — `docs/02` §4's
  trap is unchanged, and so is `WriteRoot`'s answer to it.
- `UserViewManager.GetUserViews` changed only its playlist and box-set branch; a photos or
  home-videos library still comes back as the `CollectionFolder` itself (§3).
- `ApplicationHost.GetApiPluginAssemblies` and the `GetExports<IMetadataProvider>()` hand-off
  are unchanged, so the controller is routed and `NameDateProvider` discovered as before.
- `MetadataService.GetProviders` (`runAllProviders`), the custom-provider loop of
  `RefreshWithProviders` and `ProviderManager.CanRefreshMetadata`'s `IForcedProvider` rule
  are unchanged (§17).
- `DashboardController.GetDashboardConfigurationPage` gained `[Tags("Plugin")]` and still no
  `[Authorize]`: **the page is still public** (§11), and still holds no data.
- `User` gained `NormalizedUsername`; `AuthorizationInfo.User` still returns it from the same
  assembly.

**jellyfin-web**, which runs the configuration page: `configurationPage.ts`,
`viewContainer.js`, `LegacyRoute.tsx`, the dashboard `routes.tsx` and the `emby-checkbox`,
`emby-button` and `emby-input` elements are identical; `utils/dashboard.js` still assigns
`window.Dashboard` with `showLoadingMsg`, `hideLoadingMsg`, `alert` and
`processPluginConfigurationUpdateResult`; both tags pin `jellyfin-apiclient` 1.11.0, which
is `window.ApiClient`. Only the menu's page list moved to the SDK's `PluginApi`.

## 3. Why a net9.0 binary built against 10.11.6 loads on 12

The same four reasons as `heic-for-jelly` (its `docs/05` §3): `targetAbi` is a floor, never
a ceiling (`InstallationManager.GetPackages`, `PluginManager.LoadManifest`);
`PluginLoadContext.Load` defers everything outside the plugin folder to the host, whose
12.4.0.0 assemblies answer references to 10.11.6.0; .NET 10 runs a net9.0 assembly; and
every member the plugin references exists with the same signature — which, for this plugin,
§4 checked mechanically rather than by reading.

That last point is what a compiler cannot prove. A parameter gaining a default value is
source-compatible and binary-breaking — 12.4 did exactly that to `BaseItem.GetExtras` — so
`compat.yml` compiling the source against 12.4 says nothing about the shipped dll.

## 4. Every reference, resolved against the 12.4 image

A throwaway tool on `System.Reflection.Metadata` — the BCL's own metadata reader, no
package — read the published dll's type-reference and member-reference tables and resolved
each one the way the runtime binds it: by assembly name, following type forwarders, walking
base types and interfaces, then matching name and decoded signature. The host assemblies were
copied out of each image's `/jellyfin` directory, so this is what each server actually loads,
framework included.

| Host | Type refs | Member refs | Unresolved |
|---|---|---|---|
| **12.4** (the image of §5) | 153 | 278 | **0** |
| 10.11.6 (production's image) | 153 | 278 | 0 |
| 10.10.7, as a negative control | 153 | 278 | 4 |

The control is what makes the zeros mean something. On 10.10.7 the tool reports exactly the
ABI break that 10.11 made: `Jellyfin.Database.Implementations.Entities.User` absent (it was
`Jellyfin.Data.Entities.User`), and with it `AuthorizationInfo.get_User` and
`Folder.GetChildren(User, …)`, plus `BaseItemExtensions.HasChanged`.

The other direction was checked too: the abstract and default-interface members the host
requires of an implementer — `BasePlugin`, `IPlugin`, `IHasPluginConfiguration`,
`IPluginAssembly`, `IHasWebPages`, `IMetadataProvider`, `ICustomMetadataProvider<>`,
`IHasItemChangeMonitor`, `IHasOrder`: **22 members, identical lists on 10.11.6 and 12.4.**
A member added there would have been a `TypeLoadException` here.

What this does not cover is constants compiled into the dll — `CollectionType.photos` and
`.homevideos`, `Policies.RequiresElevation` — whose defining files are byte-identical (§2),
and behaviour, which is §5.

## 5. The live test

**Setup.** Two containers beside production on the photo host, which stayed on 10.11.6 and
was never restarted:

- **12.4:** `jellyfin/jellyfin:12.4` pulled **by digest** — index
  `sha256:fea863d0cd9ec445985d9c75fc6ed64e38a1d2766cf0c250a33d553f4fc1b771`, the image
  `heic-for-jelly` was tested on;
- **10.11.6:** production's own image (`7009e8f0…`);
- each with a fresh config, published on `127.0.0.1` only, `--userns=keep-id` as production
  runs, capped at 2 CPUs and 2 GB;
- three `homevideos` + `EnablePhotos` libraries each: a writable directory, a `:ro` bind
  mount, and a 3 MiB tmpfs. **Real-time monitoring off**, so any refresh after an upload
  can only have come from the plugin's own `ReportFileSystemChanged`;
- three users: `admin`; `member`, who sees every library and is not an administrator;
  `outsider`, who sees none.

The plugin was installed the way a user installs it: the catalogue URL added under
Repositories, then **the published 0.2.0.0 zip installed from it** — MD5 checksum accepted,
dll MD5 `4aba9e0b…`, the CI build. `heic-for-jelly` 1.0.0.0 came from its own catalogue so
that a HEIC becomes a `Photo`. Both servers logged `Loaded plugin: Upload for Jelly 0.2.0.0`
with no error.

That CI dll had never run on 10.11 before this: production runs a local build of the same
source (`a47f588d…`; the build is deterministic, and the csproj change of this round keeps
the default build at that same MD5).

**Endpoints — 43 checks, identical on 10.11.6 and 12.4** except the list of plugins each
server bundles. On 12.4:

| Check | Result |
|---|---|
| `Targets` anonymous / `Libraries` anonymous / `Libraries` as `member` | 401 / 401 / 403 |
| `Targets`, empty allowlist | the three libraries, `NOT_CONFIGURED`; keys camelCase under `Accept: application/json` (§2) |
| `Libraries` as admin | `/media/photos` writable, `/media/readonly` `READ_ONLY_MOUNT`, `/media/tiny` writable |
| Configuration page, anonymous | 200 HTML, and no host path in it (§11) |
| `Targets` after opting in; as `outsider` | writable + `freeBytes`, `READ_ONLY_MOUNT`; `[]` |
| iPhone HEIC, 1.7 MB, `capturedAt=2026-09-23T15:02:00-04:00` | 201, `2026/2026-09/20260923-150200_iphone15_a49b457a42ac6578.heic`; replay 200 `created:false` |
| JPEG with EXIF; JPEG without; no `capturedAt`; unparseable `capturedAt` | 201 dated; 201 dated; 201 `undated/…`; 200 replay into `undated/` |
| `deviceSlug=../../etc` | 201, slug rebuilt as `etc` |
| Hash mismatch / empty body / `.nfo` / no extension | 422 `HASH_MISMATCH` / `EMPTY_BODY` / `UNSUPPORTED_TYPE` ×2 |
| Malformed hash / malformed target | 400 `BAD_REQUEST` |
| Unknown target / allowlisted target `outsider` cannot see | 404 `UNKNOWN_TARGET`, both — indistinguishable, as intended |
| Read-only mount | 409 `TARGET_NOT_WRITABLE` |
| `Content-Length: 4 GiB + 1` | 413 `TOO_LARGE` (`docs/02` §16) |
| 1 MiB into a volume with 512 KiB free | 507 `INSUFFICIENT_STORAGE` |
| 4 MiB into an *empty* 3 MiB volume | 500 `WRITE_FAILED` — §6b |
| `Have`: present, missing, undated, 2025-12 keys | exactly the two missing hashes |
| `Have`: month `2026-13` / 501 keys / null key / unknown target / `outsider` | 400 / 400 / 400 / 404 / 404; 500 keys 200 |
| `Have`: `TARGETID`/`SHA` casing / malformed JSON | 200 (§14) / 400 `ValidationProblemDetails` (§15) |
| `Have`: 300 KB body | 500 — §6a |
| `/api-docs/openapi.json` | 200, the four `/UploadForJelly/*` routes |

**What the library made of it — identical on both versions.** After one library scan (§6c
says why one was needed):

| File | `PremiereDate` | From |
|---|---|---|
| `2026-09/…heic` | `2023-02-19T15:01:42` | EXIF, via `heic-for-jelly` — the same value U3 recorded for the same photo |
| `2012-11/…jpg` | `2012-11-03T14:28:12` | EXIF, which beats `capturedAt` (`docs/02` §4b) |
| `2025-12/…_whatsapp_…jpg`, no EXIF | `2025-12-24T18:30:00` | **the name, via `NameDateProvider`** |
| `2026-01/…_etc_…jpg`, no EXIF | `2026-01-02T03:04:05` | the name |
| `undated/…jpg` | null | nothing to read, as designed |

Then, **with no scan**, three more uploads: one into the existing `2026-09/`, two into month
folders that did not exist yet. On 12.4 all three appeared on their own, 60–75 s after the
upload, dated from their names. On 10.11.6 so did two; the third is §6d.

**The configuration page, in a browser.** On 12.4: the three libraries listed with their
paths, the ticks read from the saved configuration, "Read-only mount — /media/readonly"
on the `:ro` one; unticking one and saving showed "Settings saved." and the stored allowlist
lost exactly that id. On 10.11.6 the page rendered identically.

**Logs.** Neither server logged a warning or error from the plugin other than the ones the
error cases are meant to produce.

## 6. Found along the way: the same on both versions, so not Jellyfin 12

None of these is caused or changed by 12, and none is fixed here. They are recorded because
the live test is where they showed.

**a. `Have` answers an oversize body with 500, not 413.** `[RequestSizeLimit]` does refuse
the 300 KB body before the heap takes it (`docs/02` §15), but the refusal is a
`BadHttpRequestException` raised while MVC reads the body, before the action runs — so the
plugin's own handler (`docs/02` §16) never sees it, and
`Jellyfin.Api/Middleware/ExceptionMiddleware.cs:GetStatusCode` has no case for it and returns
500, logged as "Request body too large". 500 is the retryable bucket of the contract. The app
never sends 300 KB here — 500 keys cost about 50 KB — so this is a wrong status, not a live
failure.

**b. An upload larger than the free space answers 500, not 507.** `UploadWriter.WriteAsync`
removes the partial file before `Refuse` measures free space, so a volume is only reported
full when it already had under 1 MiB free *before* the upload. A 4 GiB video onto 2 GiB free
gets `WRITE_FAILED`, retryable, rather than `INSUFFICIENT_STORAGE`.

**c. A library that was empty at its last scan does not see uploads until the next one.**
`Folder.IsLibraryFolderAccessible` skips an empty top-level folder with "Library folder … is
inaccessible or empty, skipping", and the plugin's reports then found nothing to refresh:
no "will be refreshed" line was logged, and the files stayed invisible until a scan. An
administrator who creates a fresh, empty library and opts it in straight away meets exactly
this, on both versions.

**d. Once on 10.11.6, a new month folder was missed.** Two uploads a second apart, one into
`2026-09/` and one into a new `2026-11/`, queued two refreshes that ran together — of
`2026-09` and of its parent `2026`. On 10.11.6 the new folder was not created and its photo
was still invisible more than four minutes later, until a later upload refreshed `2026` again
and picked it up;
12.4 found it first time. Repeated alone, a new month folder appeared after 60 s on both. One
miss in two attempts is not enough to call it a race or to credit 12.4 with a fix;
`Folder.ValidateChildrenInternal2` was largely rewritten between the tags and the cause was
not isolated.

## 7. Releasing, and when this stops being enough

- **The release is unchanged.** One zip, built against 10.11.6 on net9.0, `targetAbi`
  10.11.6.0 — the version it is compiled against, per `CLAUDE.md`. The published 0.2.0.0
  entry says "Requires Jellyfin 10.11.6"; it is not rewritten, and the next release's
  changelog says 10.11.6 or later, including 12. The README carries the claim meanwhile.
- **`.github/workflows/compat.yml`** compiles the same source against
  `Jellyfin.Controller` 12.4.0 on net10.0 at every push; it compiles clean, with no warning
  under `TreatWarningsAsErrors`. It catches source breaks only, for the reason in §3. The
  csproj takes `JellyfinVersion` and `JellyfinTargetFramework` as a pair; `TargetFramework`
  itself cannot be overridden, because NuGet restore drops that global property and fails
  NU1202.
- **The 12.4.0 graph was scanned before use.** `jellyfin.controller` and `jellyfin.model`
  12.4.0 have the same SHA-256 as the packages scanned for `heic-for-jelly` on the same day.
  This plugin's graph resolves five more packages at a different version than that scan saw —
  `Microsoft.Extensions.Caching.Abstractions`, `.Caching.Memory`,
  `.DependencyInjection.Abstractions`, `.Options` and `.Primitives`, all 10.0.14 — and those
  were scanned here: authored by Microsoft, Microsoft-signed and repository-signed by
  nuget.org, their only build-time content the standard TFM-support warning targets and the
  `Options` source generator's wiring.
- **Bump the 12.x row deliberately**, after scanning the new packages, never by letting
  NuGet float.
- **Switch to two builds only if** a 12.x breaks the old binary — `MissingMethodException`,
  `TypeLoadException`, or the plugin shown `Malfunctioned` — while 10.11 still matters. The
  manifest then needs two entries with **distinct** versions, the 12 build numbered higher
  (`heic-for-jelly` `docs/05` §6 says why). A server on 10.11 drops the 12 entry by its
  `targetAbi`.
- **Before claiming a new major**, repeat §4 and §5: the binary check takes a minute and is
  what makes the live test's coverage complete, since a missing member in a method the test
  never reaches would only fail when that method is first compiled.
- **When 10.11 is dropped**, flip the csproj defaults to 12.x/net10.0 and raise `targetAbi`
  with them.
