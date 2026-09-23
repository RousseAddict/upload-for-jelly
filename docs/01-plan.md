# 01 — Plan

The specification is `../jellypic/docs/11-writer-upload-contract.md`. This file is only
the build order: what each milestone proves, and what must be verified in the Jellyfin
sources before it can be written.

## 1. Scope

Three endpoints, one configuration setting, no database.

| | |
|---|---|
| `GET /UploadForJelly/Targets` | which libraries accept uploads, and a real write probe |
| `POST /UploadForJelly/Have` | ≤500 hashes in, the missing ones out |
| `POST /UploadForJelly/Items` | raw bytes in, a file in the library out |
| `GET /UploadForJelly/Libraries` | administrators only: every candidate, probed, with its path |
| configuration | the allowlist of library ids, empty by default, ticked on a dashboard page |

Out of scope for 1.0, each being a contract change rather than an implementation
detail: Live Photos, bursts, albums, deletion propagation, and any write other than
"add a new original".

## 2. Milestones

Each one is deployed to the real server and measured there. The iOS side has its own
milestones (`../jellypic/docs/12-writer-app-plan.md`); the pairings are noted because a
plugin milestone with no client to call it is not finished, it is untested.

**U0 — the repository.** Done. Build over ssh, release workflow, catalogue manifest,
licence, ground rules. Nothing is served.

**U1 — the server loads it.** Deploy the skeleton and see `Loaded plugin: "Upload for
Jelly" "0.1.0.0"` with no warning. That is all it proves, and it is the one thing worth
proving alone: that net9.0 against `Jellyfin.Controller` 10.11.6 is the ABI this server
accepts. No endpoint, so a failure here can break nothing else.

**U2 — `Targets`.** The first endpoint, and the one the app's settings panel already
calls (W1 is built and greys its control on a 404). Proves routing, authorisation, the
allowlist, and the create-and-delete write probe. Ends with: the app's Backup control
ungreys against a real server, and shows the refusal reason against a read-only mount.

**U3 — `Items`.** One photo, end to end: streamed to the hidden staging directory,
hashed in the same pass, verified, renamed into place, library notified, visible in
Jellyfin and then in the app's grid. Pairs with the app's W2. The staging directory
gets its empty `.ignore` here, and the body-size limit is lifted here.

**U3b — the configuration page.** Pulled forward out of U5, and not for polish: the
plugin is meant to be installable by someone who is not its author, and **an allowlist
that can only be edited with a `curl` against the configuration API is not deployable**.
A dashboard page under Plugins → Upload for Jelly lists every candidate library with the
folder it would write to and a live write probe, and ticking one is the whole setup.
Proves `IHasWebPages`, the embedded resource path, and the elevation-gated
`GET /Libraries` the page reads.

**U4 — `Have`.** Written, not yet measured on the server. Batch deduplication, up to 500
`{sha, month}` keys per request, answered by listing **one directory per distinct month
named in the batch** and matching the sixteen hex characters the filename carries. A key
with no month asks about `undated/`, the bucket an upload lands in when its `capturedAt`
is missing or unparseable. The whole batch is validated before a single directory is
opened and one malformed key refuses all of it, because answering for the well-formed part
would leave the client believing the rest was stored — a photo dropped from a backup with
nothing to notice.

The month-to-directory mapping and the filename-to-hash parse are now single functions
shared with `Items`, and that is the point rather than tidiness: two copies would drift,
and the drift would show as `Have` reporting every photo as missing while every re-upload
answered `200`. A backup that never converges and never errors.

Pairs with the app's W4/W6. Still to measure at 20 000 photos over ~120 months — the whole
no-database design rests on that shape being cheap.

**U5 — hardening, and only then 1.0.0.0.** The error taxonomy exercised end to end
(read-only mount, full disk, hash mismatch, oversize body, unknown target), concurrent
uploads of the same hash, zero-byte and truncated bodies, path-traversal attempts in
every client-supplied field.

## 3. What the sources said before U2

All five questions are answered in `docs/02-jellyfin-internals.md`, with citations. The
three that changed the code:

- **`Accept: application/json` gets PascalCase**, not camelCase. Every DTO property now
  carries `[JsonPropertyName]`, which beats the naming policy under both formatters.
  Without it the app would have decoded nothing and blamed its own version.
- **A library's `Path` is not its media folder** — it is the virtual folder in Jellyfin's
  config directory. `PhysicalLocations` is the media. One function, `WriteRoot`, owns
  that choice so the probe and the upload cannot disagree.
- **The id from `/UserViews` is the collection folder id** for a photos or home-videos
  library, and ids are dashless everywhere. No resolution step, no contract change.

## 3b. What the sources said before U3

`docs/02` §7–§10, with citations. Two of the three inverted the obvious design:

- **`ReportFileSystemChangeBeginning`/`Complete` is the wrong pair for an upload.** It is
  Jellyfin's "I am writing this myself, ignore me" bracket, and `Complete` un-ignores the
  path only after a hard-coded `Task.Delay(45000)`. Using it would hide each new photo for
  three quarters of a minute. The plugin reports the destination, once, after the rename.
- **A queued refresh is debounced by `LibraryMonitorDelay`, 60 s by default, and restarts
  on every sibling.** One backup run therefore costs one scan, not 20 000 — but the `201`
  cannot promise visibility, which is why it returns a key and a relative path and no item
  id.
- **Nothing in Jellyfin raises Kestrel's 30 MB body limit**, and the supported lever from a
  plugin action is the same MVC attribute core uses to lower it. Ours is set to 4 GiB:
  declared, not removed.

The staging directory's two ignore mechanisms were re-read and hold as contract §9 states.

## 3c. What the sources said before U3b

`docs/02` §11–§12. One finding, and it decided the whole shape of the page:

- **`DashboardController.GetDashboardConfigurationPage` has no `[Authorize]`**, and there
  is no fallback policy behind it. The configuration page's HTML is served to anyone who
  can reach the server. So the page carries no data — not a path, not a library name, not
  the allowlist — and fetches all of it from a new `GET /UploadForJelly/Libraries` gated on
  `Policies.RequiresElevation`. Had the page been generated server-side with the libraries
  baked in, as the obvious implementation would, it would have published the host's
  directory layout anonymously.

## 3d. What the sources said before U4

`docs/02` §14–§15, and both are about the *request* side of JSON, which §2 never covered
because no endpoint had a body until now:

- **A body binds case-insensitively, but `string` is not a type check.** `AddJellyfinApi`
  copies four properties out of `JsonDefaults.PascalCaseOptions` and leaves
  `PropertyNameCaseInsensitive` at MVC's web default of `true`. It also copies the whole
  converter list, which contains `JsonStringConverter` — that one returns the raw token text
  for anything that is not a string, so `{"sha": 12345}` binds as `"12345"` instead of
  failing. Every request string here is therefore validated character by character before it
  can reach a path.
- **`[ApiController]`'s automatic 400 is not the contract's error body**, and a plugin cannot
  change that: `ApiBehaviorOptions` is global. A body MVC cannot deserialize at all gets
  `ValidationProblemDetails`; anything that does deserialize reaches the action and gets
  `{"code":"BAD_REQUEST"}`. The status is the part the error taxonomy keys off, so this is
  survivable, but it is the one place the wire shape is not ours.

## 4. Risks

**ABI drift.** The plugin pins `Jellyfin.Controller` 10.11.6 and any 10.x minor can
break it. Same risk as the sibling plugin; nothing retires it.

**A write endpoint is a different risk class from a decoder.** `heic-for-jelly` could
only ever fail to show a photo. This one can write files into someone's library as any
authenticated user. That is why the allowlist is empty by default, why every
client-supplied string is rebuilt rather than trusted, and why U5 exists as a milestone
instead of as a wish.

**Two implementations of one contract, on two devices, in two languages.** The failure
mode is silent divergence — a field renamed on one side, a status code reinterpreted on
the other. The mitigation is that the contract lives in one file, in the other
repository, and is edited before either side is.
