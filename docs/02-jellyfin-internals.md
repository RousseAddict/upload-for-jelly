# 02 — Verified Jellyfin internals

Read in the sources at tag **v10.11.0**, cited `file:symbol`. Nothing here comes from
documentation or search results. Several findings invert the obvious design; the ones
that would have shipped as silent bugs are marked **trap**.

**Re-verified against `v12.4` on 2026-11-20.** Every section below still holds on
Jellyfin 12, and the published 0.2.0.0 binary was run against both versions with identical
results. Two details moved, neither of which changes the code: legacy authorisation
(`X-Emby-Token`, `?api_key=`) is switched off by a migration — irrelevant to §5, since
the plugin goes through `IAuthorizationContext` — and the `"**/.*/**"` pattern that
`v10.11.6` had added to the ignore list is gone again, which makes §10 exact once more.
Full record in `03-jellyfin-12.md`.

## 1. A plugin controller is routed, with no prefix

`Emby.Server.Implementations/ApplicationHost.cs:GetApiPluginAssemblies` selects every
loaded assembly containing a concrete `ControllerBase` and yields it;
`Jellyfin.Server/Extensions/ApiServiceCollectionExtensions.cs:AddJellyfinApi` clears the
application parts, adds the API assembly, then `AddApplicationPart` for each of those.

So a plugin controller is ordinary MVC attribute routing. There is no implicit prefix:
`Jellyfin.Api/BaseJellyfinApiController.cs` carries `[Route("[controller]")]`, and
controllers that want another path override it (`UserViewsController` declares
`[Route("")]`, which is why `/UserViews` is at the root). Ours declares
`[Route("UploadForJelly")]`, and that is the whole path.

Deriving from `ControllerBase` rather than `BaseJellyfinApiController` is deliberate:
the latter lives in `Jellyfin.Api`, which is not a package a plugin references.

## 2. Trap: `application/json` gets **PascalCase**

`AddJellyfinApi` inserts two output formatters. `PascalCaseJsonProfileFormatter` claims
both `application/json; profile="PascalCase"` **and plain `application/json`**;
`CamelCaseJsonProfileFormatter` claims only its own profile media type.
`Jellyfin.Extensions/Json/JsonDefaults.cs` gives the Pascal options
`PropertyNamingPolicy = null`.

A client sending `Accept: application/json` — which is what the app sends — therefore
receives `{"Targets":[{"Id":…}]}`, not the camelCase the contract specifies. The app
would have decoded nothing, reported *incompatible*, and greyed its own control with
"Update the upload plugin on your server", which is a perfect description of the wrong
problem.

Fix: **every DTO property carries `[JsonPropertyName]`.** An explicit name beats the
naming policy, so the wire shape is the same under both formatters and cannot be
changed by an `Accept` header.

## 3. Ids are dashless, and the id from `/UserViews` is the collection folder

`Jellyfin.Extensions/Json/Converters/JsonGuidConverter.cs:WriteInternal` writes
`value.ToString("N")`. Every id a client has ever seen is 32 hex characters with no
dashes, so `TargetInfo.Id` is formatted the same way. A dashed id would simply never
match on the client, silently.

`Emby.Server.Implementations/Library/UserViewManager.cs:GetUserViews` returns a
`UserView` — a different id from the library's — in three cases: `UserView.IsUserSpecific`
(playlists, or `ISupportsUserSpecificView`), grouped folders (only `movies`, `tvshows`
and untyped are eligible, per `UserView.IsEligibleForGrouping`), and
`query.PresetViews.Contains(type)`. `UserViewQuery`'s constructor defaults `PresetViews`
to empty and `UserViewsController.GetUserViews` only fills it when the caller asks.

For a `photos` or `homevideos` library, with a client that does not pass `presetViews`,
none of the three applies: the method falls through to `list.Add(folder)` and hands back
the `CollectionFolder` itself. **The id the app stored is the id we match.** No
resolution step is needed, and the contract does not change.

`UploadTargets.ForUser` walks that same
`GetUserRootFolder().GetChildren(user, true)` enumeration on purpose — matching ids by
construction, and inheriting the user's own library permissions.

## 4. Trap: `PhysicalLocations[0]` **is** the virtual folder

`MediaBrowser.Controller/Entities/CollectionFolder.cs` exposes both `Path` and
`PhysicalLocations`. `Path` is the *virtual folder* inside Jellyfin's configuration
directory — `GetLibraryOptionsPath(Path)` puts `options.xml` beside it — and it holds the
library's `.mblink` shortcuts, not its media.

The part that matters, and that an earlier version of this section got wrong:
**`PhysicalLocations` is not the complement of `Path`, it is a superset of it.**
`CollectionFolder.CreateResolveArgs` assigns `PhysicalLocationsList = args.PhysicalLocations`,
and `ItemResolveArgs.PhysicalLocations` is literally `[Path, ...AdditionalLocations]`. The
additional entries are the resolved shortcut targets —
`FileData.GetFilteredFileSystemEntries` calls `args.AddAdditionalLocation(newPath)` with the
expanded `.mblink` destination. So the *first* element is always the config directory and
only the rest is media.

That is why `CollectionFolder.GetPhysicalFolders` filters with
`PhysicalLocations.Where(i => !FileSystem.AreEqual(i, Path))`. This section previously cited
that filter as *evidence that the two differ*; it is in fact the required step, and reading
it as evidence is how `WriteRoot` shipped as "the first entry that exists" — which returns
the config directory on every server in the world.

**It failed silently and looked like success.** The config directory is perfectly writable,
so the create-and-delete probe passed and `Targets` reported `writable: true`. The upload
returned `201`, and the relative path in that response (`2026/2026-09/…`) looks correct
because it is relative to a root the client never sees. Photos were being filed into
`/config/root/default/<Library>/`, where no scan reaches them. What exposed it was
`GET /Libraries` — the only endpoint that returns an absolute path, added for an unrelated
reason.

`UploadTargets.WriteRoot` now skips the entry equal to `folder.Path` and returns the first
remaining one that exists. It uses the injected `IFileSystem.AreEqual` rather than a string
comparison, so case sensitivity is the host's decision and not a guess about the volume.

Open, and a contract question rather than a code one: a library with several physical
folders. v1 takes the first existing one.

## 4b. `capturedAt` chooses the folder and nothing else — measured

Uploading a real iPhone HEIC with `capturedAt=2026-09-23T15:02:00-04:00` filed it at
`2026/2026-09/20260923-150200_…heic` and produced an item whose `PremiereDate` is
`2023-02-19T15:01:42Z`, read out of the file's own EXIF, with `Width`/`Height` and camera
fields to match. The plugin never writes metadata into the bytes it stores, so the folder
name and the item's date come from two independent sources and are *expected* to disagree.

The consequence worth stating: **the reader sorts on `PremiereDate`, so the folder layout
has no effect on where a photo appears in the app.** The layout exists for humans browsing
the filesystem and for `Have`'s single-directory enumeration (contract §6), not for
ordering. A file whose EXIF carries no capture date will be undated in the app however
precisely `capturedAt` placed it — acceptable because iPhone originals always carry one,
and screenshots are out of scope.

## 5. Authorisation available to a plugin

- `[Authorize]` with no policy uses the default built in
  `ApiServiceCollectionExtensions.AddJellyfinApiAuthorization`:
  `AddAuthenticationSchemes(AuthenticationSchemes.CustomAuthentication)` plus
  `DefaultAuthorizationRequirement`. That is the scheme that parses
  `Authorization: MediaBrowser Token="…"`, so the app's existing header is enough and no
  named policy is needed.
- Named policies are in `MediaBrowser.Common/Api/Policies.cs`, reachable from a plugin.
  `Jellyfin.Api/Constants` and `Jellyfin.Api/Extensions/ClaimsPrincipalExtensions.cs`
  are **not** — a plugin cannot call `User.GetUserId()`.
- The way in is `MediaBrowser.Controller/Net/IAuthorizationContext.cs`:
  `GetAuthorizationInfo(HttpContext)` yields an `AuthorizationInfo` whose `User` is the
  entity. It is null for an API key (`IsApiKey`), which is why `Targets` answers 401
  there: every rule in this plugin is defined per user.

## 6. No free-space API in `IFileSystem`

`MediaBrowser.Model/IO/IFileSystem.cs` has no `GetFreeSpace`, no `CanWrite`, no
`IsWritable` — confirming contract §3 from the other side. `freeBytes` comes from
`DriveInfo.GetDrives()` with a longest-mount-point match; `new DriveInfo(root)` would
answer for `/`, which on a server is never the volume the media sits on.

The read-only verdict comes from `/proc/self/mounts`, also by longest match, rather than
from the exception: whether EROFS surfaces as `UnauthorizedAccessException` or
`IOException` is a .NET errno-mapping detail, not a contract to hang a user-visible
sentence on. Where there is no `/proc`, the probe says `PERMISSION_DENIED` or
`WRITE_FAILED` rather than claiming a mount flag it cannot see.

## 7. Trap: the "I am writing this myself" pair hides the file for 45 seconds

`Emby.Server.Implementations/IO/LibraryMonitor.cs` offers three methods and only one of
them is right for an upload.

`ReportFileSystemChangeBeginning` puts a path into `_tempIgnoredPaths`.
`ReportFileSystemChangeComplete` takes it back out — after `await Task.Delay(45000)`, a
hard-coded three quarters of a minute, and only *then* optionally reports the change. That
pair exists for Jellyfin's own metadata writes, where being ignored is the whole point.

Used around an upload it would be exactly backwards: `ReportFileSystemChanged` drops any
path that is equal to, beneath, or the parent of a temp-ignored entry, so the plugin would
be suppressing the one file it wants seen. **The plugin calls `ReportFileSystemChanged`
alone, on the destination, after the rename**, and never brackets anything.

Two more things that method does before queueing, both load-bearing here:

- It runs `IgnorePatterns.ShouldIgnore(path)` first. Reporting the staging file would be a
  no-op, which is correct but worth knowing: the only useful path to report is the final
  one.
- It does **not** consult `LibraryOptions.EnableRealtimeMonitor`. That flag gates
  `StartWatching` only, so an explicit report works on a library whose real-time
  monitoring is switched off — which is the common case on a NAS.

## 8. A queued refresh is debounced, and the debounce restarts

`CreateRefresher` either hands the path to an existing `FileRefresher` covering the same
folder or makes a new one; `FileRefresher.RestartTimer` arms it for
`ServerConfiguration.LibraryMonitorDelay` seconds, **default 60**
(`MediaBrowser.Model/Configuration/ServerConfiguration.cs`), and `AddPath` restarts it.

So a 20 000-photo backup produces one scan, roughly a minute after the last upload, not
20 000 scans. That is the behaviour we want and the reason the `201` returns a key and a
relative path rather than an item id: **the endpoint cannot truthfully promise
visibility**, so its response shape does not pretend to.

## 9. The body limit is Kestrel's default, and an attribute is the supported lever

`Jellyfin.Server/Extensions/WebHostBuilderExtensions.cs:SetupJellyfinWebServer` configures
listeners, HTTPS and the unix socket, and never touches `KestrelServerOptions.Limits`.
Neither `Jellyfin.Server/Startup.cs` nor `ApiServiceCollectionExtensions` mentions
`MaxRequestBodySize`, `FormOptions` or a size filter. Kestrel's own 30 MB default therefore
stands for every endpoint in the server, ours included.

The lever available from inside a plugin action is an ordinary MVC filter, and core uses
the same one in the other direction: `Jellyfin.Api/Controllers/ClientLogController.cs`
carries `[RequestSizeLimit(MaxDocumentSize)]` to *lower* it to 1 MB.

We raise it to **4 GiB, declared rather than removed**. `[DisableRequestSizeLimit]` would
let one authenticated request stream without bound onto the host; a number can be quoted
in a bug report.

## 10. Re-read for the staging directory, and confirmed

`IgnorePatterns.cs` lists `"**/.*"` under *Unix hidden files* — and spells out
`"**/.actors"` **and** `"**/.actors/**"` for every hidden directory it means to exclude
wholesale, which is the proof that the glob only matches the final segment. A dot-prefixed
directory alone says nothing about the files inside it.

`DotIgnoreIgnoreRule.IsIgnored` walks `FindIgnoreFile` up the ancestors from any entry and,
on the **file** branch as much as the directory branch, returns true when the content is
blank: `if (string.IsNullOrWhiteSpace(ignoreFileString)) { return true; }`.

Hence `.upload-for-jelly/` **and** an empty `.ignore` inside it. Two mechanisms, neither
depending on the other, and the `.ignore` goes in the staging directory — never at the
library root, where the same rule would hide the whole library.

Version note: `v10.11.6` briefly added `"**/.*/**"`, which does match inside a hidden
directory, and `v12.4` removed it. The design never relied on it; on 12 the `.ignore` is
again the only thing stopping a report on a file inside the staging directory
(`03-jellyfin-12.md` §1a).

## 11. Trap: the configuration page's HTML is served to anyone

A plugin adds a dashboard page by implementing `IHasWebPages`
(`MediaBrowser.Model/Plugins/IHasWebPages.cs`) and returning a `PluginPageInfo` whose
`EmbeddedResourcePath` names a resource in the plugin assembly.
`Jellyfin.Api/Controllers/DashboardController.cs:GetDashboardConfigurationPage` finds the
page by matching `Name` against `?name=` case-insensitively and streams
`Assembly.GetManifestResourceStream`.

That action carries **no `[Authorize]` at all**, and neither does
`BaseJellyfinApiController`. Its sibling `GetConfigurationPages` does
(`Policies.RequiresElevation`), which is what makes the omission look deliberate rather
than overlooked. Nor is there a fallback: `AddJellyfinApiAuthorization` sets
`options.DefaultPolicy` — the policy a bare `[Authorize]` resolves to — and never
`options.FallbackPolicy`, which is the one that would cover unattributed endpoints. So the
HTML file is public, to anyone who can reach the server, signed in or not.

Hence the shape of ours: **the page holds no data of its own.** It ships markup and a
script, nothing else; every fact it displays — library names, host paths, the probe
verdict, the current allowlist — is fetched at runtime through
`GET /UploadForJelly/Libraries`, which is `[Authorize(Policy = Policies.RequiresElevation)]`.
An anonymous reader gets an empty form and a 403 from the fetch.

Two smaller notes on the same path:

- The resource name is `RootNamespace` plus the file's path with separators as dots, which
  is what `Plugin.GetPages` composes. Verified in the built assembly rather than assumed:
  `Jellyfin.Plugin.UploadForJelly.Configuration.configPage.html`. A typo here is a 404 on
  the page and nothing else.
- `ConfigurationPageInfo`'s `DisplayName` falls back to the plugin's own `Name`, so the
  dashboard entry reads "Upload for Jelly" without the page declaring it twice.

## 12. Why `Libraries` is the one endpoint that returns a host path

The ground rule is that no absolute server path leaves the plugin. The configuration page
is the deliberate exception, and it is carved out rather than waived: two libraries can
share a name, and an administrator about to grant write access to a mount needs to know
*which* mount. That is exactly the decision the path informs.

It is contained three ways. The endpoint requires elevation. The response uses its own DTO,
`LibraryInfo`, rather than extra nullable fields on the caller-facing `TargetInfo`, so a
later change to the latter cannot start leaking one. And the page renders every
server-supplied string through `textContent`; the only value ever assigned to `innerHTML` is
a static `<input is="emby-checkbox">` literal, present so the custom element upgrades on
parse.

`ForAdministrator` also ignores the allowlist on purpose — the page's whole job is to show
what *could* be opted in — and probes every candidate, including the ones already ticked.
A mount that went read-only since it was ticked should say so before the next upload fails.

## 13. Trap: `ILibraryManager.RootFolder` is not where the libraries are

`LibraryManager.RootFolder` is an **`AggregateFolder`** over the physical media roots.
`GetUserRootFolder()` is a **`UserRootFolder`** rooted at
`ApplicationPaths.DefaultUserViewsPath`, and *its* children are the `CollectionFolder`
objects — one per library. The two are different trees, and only the second one has the
type every check in this plugin keys off.

The first version of `ForAdministrator` walked `RootFolder.Children`, filtered on
`is ICollectionFolder`, and returned an empty list on a server that plainly has a photo
library. **Nothing failed**: the endpoint answered `200 []` and the page correctly rendered
its "no photo or home-video library was found" message, which is the one sentence that
looks like a finding rather than a bug.

Core's own answer to the same question is
`LibraryManager.GetVirtualFolders`: `var topLibraryFolders = GetUserRootFolder().Children`.
That is the enumeration behind the dashboard's library list, and now behind ours.

`Children`, not `GetChildren(user, true)` — the administrator is configuring libraries,
not browsing them, and may not hold view permission on every one. That is the deliberate
difference from `ForUser`, where the user-scoped enumeration *is* half the access rule (§3).

## 14. Reading a request body: case-insensitive, and `string` is not a type check

§2 is about output. `Have` is the first endpoint here with a body, so the input side had to
be read too, and it behaves differently in both directions.

`ApiServiceCollectionExtensions:AddJellyfinApi` ends in `.AddJsonOptions(…)`, and that
delegate copies exactly four properties out of `JsonDefaults.PascalCaseOptions` —
`ReadCommentHandling`, `WriteIndented`, `DefaultIgnoreCondition`, `NumberHandling` — then
clears and refills `Converters`, then sets `PropertyNamingPolicy`. It never touches
`PropertyNameCaseInsensitive`, and `Microsoft.AspNetCore.Mvc.JsonOptions` constructs its
`JsonSerializerOptions` from `JsonSerializerDefaults.Web`, where that flag is `true`.

So `targetId`, `TargetId` and `TARGETID` all bind to the same property. The
`[JsonPropertyName]` attributes still go on request DTOs — they are the names the contract
states, and renaming a C# property must not move the wire name — but on the way *in* they
are a declaration, not a fix, which is the opposite of their role on the way out.

**The trap is in the converter list.**
`Jellyfin.Extensions/Json/Converters/JsonStringConverter.cs:Read` returns
`reader.GetString()` for a string token and `GetRawValue(reader)` — the raw UTF-8 text of
whatever is there — for anything else. That converter is in `JsonDefaults.Options` and is
therefore copied into MVC's options verbatim, so it governs every `string` property on
every plugin DTO. `{"sha": 12345}` binds as `"12345"`; `{"month": true}` binds as `"true"`.

Declaring a field as `string` is consequently not a type check, and a DTO cannot be relied
on to reject a caller who sends the wrong JSON type. Every request string in this plugin is
validated character by character before it is allowed to form part of a path —
`UploadWriter.IsSha256`, `IsMonth`, `Extension`, `Slug` — which is what makes this
harmless rather than interesting.

## 15. `[ApiController]`'s automatic 400 is not the contract's error body

Neither `AddJellyfinApi` nor `Jellyfin.Server/Startup.cs` configures `ApiBehaviorOptions`,
so the default `InvalidModelStateResponseFactory` stands. A body MVC cannot deserialize at
all — malformed JSON, or the wrong shape entirely — is answered with a
`ValidationProblemDetails` 400 before the action ever runs, not with the contract's
`{"code":"BAD_REQUEST"}`.

This is accepted rather than worked around. The lever is
`services.Configure<ApiBehaviorOptions>`, which is **global**: a plugin that set it would
change the error shape of every endpoint in the server to suit one of its own. And the
status is the part the contract's taxonomy keys off (§10 there), so a client reading 400 as
"terminal for this item, it is a client bug" behaves correctly either way.

The boundary worth knowing: anything that *does* deserialize reaches the action and gets the
contract's object — an empty `keys` array, a `keys` array of nulls, a 501-key batch, a
`month` of `"2026-13"`. Only bytes that are not JSON escape it.

There is a second, cheaper consequence of "the body is deserialized before the action runs":
`MaxKeys` cannot bound allocation, only work. Kestrel's 30 MB default would let an
authenticated caller materialise on the order of 200 000 `HaveKey` instances and only then
be told `400`. `Have` therefore carries its own `[RequestSizeLimit]` — 256 KB against a real
cost of about 50 KB — so the refusal happens before the heap does.

That refusal is a `BadHttpRequestException` raised during model binding (§16), before the
action, and left alone it reaches `Jellyfin.Api/Middleware/ExceptionMiddleware.cs:GetStatusCode`,
which has no case for it: **500**, the retryable status. Measured on 10.11.6 and 12.4
(`03-jellyfin-12.md` §6a). Since 0.2.1.0 `RequestBodyErrorsAttribute`, an exception filter on
the controller, answers it 413 `TOO_LARGE`. That is the lever this section's global
`ApiBehaviorOptions` is not: an exception filter sees model-binding exceptions and applies
to this controller only. It does not change the `ValidationProblemDetails` above, which is
model state, not an exception.

## 16. `BadHttpRequestException` derives from `IOException`, and `[RequestSizeLimit]` is lazy

Two facts that only bite together, and the pair of them made the contract's `413`
unreachable from U3 until it was found.

`Microsoft.AspNetCore.Http.BadHttpRequestException` is declared
`public class BadHttpRequestException : IOException` (verified in `dotnet/aspnetcore`
v9.0.0, `src/Http/Http.Abstractions/src/BadHttpRequestException.cs`), and Kestrel's own
subclass inherits from it. Separately, `[RequestSizeLimit]` does not reject an oversize
request up front: it sets `IHttpMaxRequestBodySizeFeature.MaxRequestBodySize`, and the
ceiling is enforced *inside* `body.ReadAsync`.

So the "request body too large" exception is raised in the middle of `DrainAsync`, where
`UploadWriter.WriteAsync`'s own `catch (Exception ex) when (ex is IOException or
UnauthorizedAccessException)` caught it first and ASP.NET's 413 path was never reached. The
answer came from `Refuse`, which inspects the filesystem, finds nothing wrong, and says
`500 WRITE_FAILED` — retryable per contract §10, forever, for an item that can never fit. On
a volume under the 1 MiB free-space threshold the same exception yielded `507`, which is
terminal for the **whole queue**: a client resetting a connection could stop a backup.

That last case is not hypothetical. A client that hangs up mid-body raises
`ConnectionResetException` or `BadHttpRequestException("Unexpected end of request content")`,
both of them `IOException`, so which of `WriteAsync`'s handlers ran was a race between the
exception and the `RequestAborted` token.

Fixed by catching `BadHttpRequestException` *before* the filesystem filter and reading its
`StatusCode`: `413` becomes `TOO_LARGE`, anything else becomes `BAD_REQUEST`. Both are
item-terminal, which is what a body that will not arrive actually is. The ordering is the
entire fix, and nothing warns about getting it wrong — the base type is reached through an
exception *filter*, not a type match, so the compiler does not see a derived-after-base
`catch` chain to complain about.

A second ordering in the same handler, fixed in 0.2.1.0: the filesystem branch removed the
partial file and *then* asked `Refuse` whether the volume was full. A partial that had filled
the volume handed its space back first, so the 1 MiB test only fired on a volume already
full before the upload, and a file larger than the free space came back `500 WRITE_FAILED`.
The diagnosis now runs first and the removal in a `finally` (`03-jellyfin-12.md` §6b).

## 17. A custom metadata provider: when it runs, in what order, and what it may overwrite

Read for U6 (`docs/01` §3e), v10.11.6.

**Order.** `MediaBrowser.Providers/Manager/ProviderManager.cs:GetMetadataProvidersInternal`
sorts local providers by the library's reader order and remote ones by the fetcher order;
everything else — every `ICustomMetadataProvider` — gets `int.MaxValue`, then
`ThenBy(GetDefaultOrder)`. `GetDefaultOrder` returns `IHasOrder.Order` when the provider
implements it (`MediaBrowser.Controller/Providers/IHasOrder.cs`), else **50**. Neither
`Emby.Photos/PhotoProvider.cs:PhotoProvider` nor `heic-for-jelly`'s `HeicMetadataProvider`
implements `IHasOrder`, so both sit at 50 and a provider declaring `Order = 100` runs after
them. `MediaBrowser.Providers/Manager/MetadataService.cs:RefreshWithProviders` then runs the
custom providers in that list order (`RunCustomProvider`).

**What `PhotoProvider` does to the date.** `PhotoProvider.FetchAsync`, inside the
`_includeExtensions` gate (`.jpg .jpeg .png .tiff .cr2 .webp .avif`) and the TagLib `try`:

```
var dateTaken = image.ImageTag.DateTime;
if (dateTaken.HasValue) { DateCreated = …ToUniversalTime(); PremiereDate = …; ProductionYear = …; }
```

It sets the date only when it finds one and **never clears it**: a JFIF-only JPEG reaches
the block with `DateTime == null` and leaves `PremiereDate` as it was; a HEIC renamed `.jpg`
throws inside TagLib and is caught. `heic-for-jelly` has the same shape. So a provider that
runs last and fills only a null `PremiereDate` cannot be overwritten in the same refresh,
and cannot overwrite a real date.

**What a refresh resets first.** `MediaBrowser.Controller/Entities/BaseItem.cs:BeforeMetadataRefresh`
clears `_sortName` and back-fills an empty `Name`; it does not touch dates. With no local or
remote provider for `Photo`, `RefreshWithProviders` performs no `MergeData` before the custom
providers, so a date set by an earlier refresh is still there when they run.

**When providers run at all.** `MetadataService.GetProviders`:

```
runAllProviders = ReplaceAllMetadata || mode == FullRefresh
               || (isFirstRefresh && mode >= Default) || (requiresRefresh && mode >= Default)
```

and otherwise only providers whose `IHasItemChangeMonitor.HasChanged` says the file changed.
So a new upload gets the provider on its first refresh, and an existing item needs an
explicit one. `Jellyfin.Api/Controllers/ItemRefreshController.cs:RefreshItem` with
`metadataRefreshMode=FullRefresh` is enough — `replaceAllMetadata` is not needed, and the
`ForceSave` it sets for `FullRefresh` makes the result persist.

**Locked items.** `ProviderManager.CanRefreshMetadata` skips every non-local provider on an
`IsLocked` item unless it is an `IForcedProvider`. `PhotoProvider` is one; so is U6's, for
the same reason.

## 18. Plugin folders: where a catalogue install lands, and what startup deletes

Read for the first release (0.2.0.0), v10.11.6.

**Where a catalogue install lands.**
`Emby.Server.Implementations/Updates/InstallationManager.cs:PerformPackageInstallation`
builds `Path.Combine(PluginsPath, package.Name) + "_" + package.Version`, deletes that
folder if it exists, and extracts the zip into it. `package.Name` is the manifest's `name`
(`GetCompatibleVersions` copies it), which `tools/publish-manifest.py` takes from
meta.json: `Upload for Jelly_0.2.0.0`, spaces included.

**What startup does with several folders.**
`Emby.Server.Implementations/Plugins/PluginManager.cs:DiscoverPlugins` reads every folder's
meta.json (`LoadManifest`; the version that counts is meta.json's, not the folder's), sorts
with `MediaBrowser.Common/Plugins/LocalPlugin.cs:Compare` (name, then id, then version),
walks from the newest, keeps the first enabled entry per name, and `Directory.Delete`s every
other folder of that name, marking it `Deleted` when the delete fails. A newer deploy beside
an older one is therefore harmless: the older folder is gone after the restart. That is the
worry that kept the plugin unreleased at 0.1.0.0 (`docs/01` §3e), and it does not hold.

**Trap: two folders with the same version.** `Compare` returns 0 for them, and nothing
defines which survives: `Directory.EnumerateDirectories` promises no order and `List.Sort`
no stability. A dev build deployed as `UploadForJelly_0.2.0.0` beside a catalogue install of
`Upload for Jelly_0.2.0.0` would be deleted at some restarts and not others. Each time, the
log would show the right name and the right version loading, from the other build.
`tools/build.sh` deploys into the catalogue's folder name for that reason, so the two
overwrite each other instead.
