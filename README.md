# Upload for Jelly

A Jellyfin server plugin that lets a client upload photos into a photo library, and
files them where the library scanner will find them.

**Status: skeleton.** The server loads it; no endpoint is served yet.

## Why it exists

Jellyfin 10.11 has no way to add a file to a library over HTTP. Of the 60 API
controllers at `v10.11.0`, exactly five accept bytes — artwork, lyrics, subtitles and
the client log — and none of them creates a library item from what was uploaded. There
is no UPnP `ImportResource` either: the DLNA server has been a plugin since 10.9.

So a photo-backup client has to either require WebDAV, or SMB, or a NAS-specific API —
that is, ask every user what their box happens to run — or get one endpoint that is the
same everywhere. This plugin is that endpoint.

## What it will do

- `GET /UploadForJelly/Targets` — which libraries accept uploads, and whether the
  filesystem really lets us write there. The probe creates and deletes a file in the
  target root on every call; mode bits lie when ACLs, mount flags or SELinux are in
  play.
- `POST /UploadForJelly/Have` — given up to 500 hashes, which ones the server does not
  have. No database: the hash is in the filename, so the month sent with each key names
  the one directory to enumerate.
- `POST /UploadForJelly/Items` — raw bytes, streamed to a hidden staging directory
  inside the library root, hashed in the same pass, verified, then moved into place with
  an atomic rename.

The full specification, including the on-disk layout, the error taxonomy and why
filenames carry 16 hex characters of the hash rather than 8, is
`../jellypic/docs/11-writer-upload-contract.md`.

## Safety

- **The allowlist of writable libraries is empty by default.** Installing the plugin
  does not, by itself, open a write surface on the host. An administrator opts one
  library in.
- Callers must be authenticated and must already be able to see the target library.
- The staging directory is hidden **and** carries an empty `.ignore`, two independent
  reasons the scanner never indexes a half-written file.
- No absolute server path is ever returned to a client.

## Requirements

- Jellyfin **10.11.6** (the ABI is pinned; any 10.x minor can break a plugin)
- Write access, from the Jellyfin process, to the library you opt in. Many installs
  already have it — `SaveSubtitlesWithMedia` defaults to true — but a `ro=true` mount,
  as Jellyfin's own container documentation suggests, does not.

## Install

Dashboard → Plugins → Repositories → **+**, and add:

```
https://raw.githubusercontent.com/RousseAddict/upload-for-jelly/main/manifest.json
```

The plugin then appears under Catalogue → General. Install it and restart the server.
Updates arrive the same way.

Jellyfin hides any plugin whose `targetAbi` is above the server's own version, so an
empty catalogue means the server is older than the requirement above.

### From source

Build the DLL, then drop it with `meta.json` into
`<config>/plugins/UploadForJelly_<version>/` and restart the server.

`tools/build.sh` does this for a setup where compilation happens on another machine
over ssh. Copy `tools/build.env.example` to `tools/build.env` and fill in your hosts:

```sh
sh tools/build.sh              # sync + build
sh tools/build.sh --deploy     # + install into Jellyfin (never restarts it)
```

## Licence

MIT. See [LICENSE](LICENSE).
