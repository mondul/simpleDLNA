AGENTS.md
===

Guidance for anyone changing SimpleDLNA, human or AI agent.
[Readme.md](Readme.md) documents the program for its users. This file
documents the code: how to build and test it, how it fits together, and the
traps that have already cost someone a debugging session.

Commands
---

Requires the .NET SDK 10.0. `global.json` only selects the test runner, not
an SDK version.

```bash
dotnet build
```

Builds `sdlna.slnx`: the five projects and the tests. It should finish with
zero warnings; keep it that way.

```bash
dotnet test
```

Runs the whole suite in a few seconds. To run part of it:

```bash
dotnet test --filter-class NMaier.SimpleDlna.Tests.HttpServerTests
```

```bash
dotnet test --filter-method NMaier.SimpleDlna.Tests.ViewTests.UnknownViewIsReported
```

To run the program from source:

```bash
dotnet run --project sdlna -- -l DEBUG ~/Videos
```

`sdlna --server ...` reads and writes `~/.sdlna/config.json`. To try it
without touching your own configuration on Linux or macOS, build first and
point `HOME` at a scratch folder. Run the built `sdlna.dll` rather than
`dotnet run`, which would restore every package into that folder:

```bash
HOME=$(mktemp -d) dotnet sdlna/bin/Debug/net10.0/sdlna.dll --server add Test ~/Videos
```

To produce a release-style binary:

```bash
dotnet publish sdlna/sdlna.csproj -c Release -r osx-arm64 --self-contained -p:PublishSingleFile=true -p:PublishTrimmed=false
```

The result is one executable with nothing beside it, because every
dependency is managed code; see [Traps](#traps). `PublishTrimmed=false`
matters too.

Layout
---

| Project    | Assembly                     | Depends on                   | Role |
|------------|------------------------------|------------------------------|------|
| `util`     | `SimpleDlna.Utilities`       | -                            | Logging base class, ffmpeg discovery and invocation, `StreamPump`, MAC lookup, natural sorting, `Repository<T>` |
| `server`   | `SimpleDlna.Server`          | util                         | HTTP server, SSDP, UPnP/DLNA handlers, MIME and profile maps, views, comparers |
| `thumbs`   | `SimpleDlna.Thumbnails`      | util, server                 | Thumbnails: images through ImageSharp, video through ffmpeg |
| `fsserver` | `SimpleDlna.FileMediaServer` | util, server, thumbs         | `FileServer`: scans folders into media items, reads tags with TagLibSharp, caches metadata in LiteDB |
| `sdlna`    | `sdlna`                      | util, server, fsserver       | Console entry point, command-line options, configuration file, `--server` commands |
| `tests`    | `SimpleDlna.Tests`           | all of the above             | xUnit v3 test suite |

Shared build settings live at the root: `Directory.Build.props` (target
framework, version, strong naming, `InternalsVisibleTo` for the tests) and
`Directory.Packages.props` (every package version, through Central Package
Management; `PackageReference` items in projects carry no `Version`).

### How a request flows

1. `sdlna/Program.cs` `Main` hands `--server ...` to `ServerCommand.Run`,
   which edits the configuration file and exits. Otherwise it parses
   `Options`. With folders it builds one ad-hoc `FileServer`; without,
   `ChooseConfiguredServers` either returns the configuration file's servers
   (one `FileServer` each) or falls back to serving the current directory.
2. Every `FileServer` is registered with a single `HttpServer`
   (`server/Http`), which mounts it at `/mm-N/` as a `MediaMount`
   (`server/Handlers`). `SsdpHandler` announces each mount over UDP multicast
   on port 1900, which is how TVs discover it.
3. A client fetches `/mm-N/description.xml`, then posts SOAP to
   `/mm-N/control` (`MediaMount_SOAP.cs`: ContentDirectory `Browse`,
   ConnectionManager `GetProtocolInfo`, and so on). Browse results are DIDL
   documents with absolute links to `/mm-N/file/<id>/res` and
   `/mm-N/cover/<id>/...`.
4. File responses are copied to the socket by `util/StreamPump.cs`. A browser
   pointed at `/` gets an HTML index (`IndexHandler`, `MediaMount_HTML.cs`).

### Where things are decided

- **File types.** `server/Types/DlnaMaps.cs` maps extensions to `DlnaMime`
  values, and those to MIME types, DLNA profile names (PN) and the
  GetProtocolInfo list. `fsserver/Files/BaseFile.GetFile` picks `AudioFile`,
  `ImageFile` or `VideoFile` from the media type.
- **Views** (`server/Views`) rebuild or filter the folder tree. `Identifiers`
  (`server/Types`) applies them in order when the tree is loaded.
- **Metadata cache.** `fsserver/FileStore.cs` keeps each file's metadata and
  cover in a LiteDB database, encoded by `fsserver/Files/MediaSerializer.cs`.
- **Configuration file.** `sdlna/Configuration.cs` holds the model,
  validation and loading/saving; `sdlna/ServerCommand.cs` the `--server`
  commands.

Code style
---

Match the surrounding code rather than modernising it in passing.

- Two-space indentation, LF line endings for `.cs` (`.editorconfig`). Project
  and solution files are CRLF (`.gitattributes`). Many `.cs` files start with
  a UTF-8 BOM; leave existing BOMs alone.
- Braces: types and members open on their own line; `if`, `foreach`,
  `using` and friends open on the same line, and `else` starts a new line
  after the closing brace.
- Block-scoped namespaces, explicit `using` directives (implicit usings are
  off), no nullable reference types, `var` for locals, `typeof (T)` with a
  space.
- Private fields are camelCase without an underscore; constants are
  UPPER_SNAKE_CASE.
- Classes that log derive from `NMaier.SimpleDlna.Utilities.Logging` and call
  `Debug`, `InfoFormat`, `Warn` and so on. log4net is configured once, in
  `sdlna/Options.cs`.
- Comments explain why, especially for workarounds. Existing ones often name
  the failure they prevent; keep that habit.
- User-facing behaviour is documented in `Readme.md`, and in the help text
  (`sdlna --help`, `sdlna --server help`, `sdlna --list-views`). Change both
  together.

Tests
---

The suite uses xUnit v3 on Microsoft.Testing.Platform (selected in
`global.json`). The test project is an executable; `dotnet test` builds and
runs it.

- **Internal access.** The shipped assemblies grant `InternalsVisibleTo` to
  `SimpleDlna.Tests` with the public key of `sdlna.key.snk`, which signs the
  tests too. Prefer testing through `internal` members over reflection; making
  a private method `internal` for a test is fine.
- **Collections.** Most tests run in parallel. Tests that change process-wide
  state (the console writers, environment variables such as `HOME`,
  `ConfigurationStore.HomeDirectoryOverride`) belong in
  `[Collection(ProcessStateCollection.NAME)]`. Tests that talk to the HTTP
  server belong in `[Collection(HttpServerCollection.NAME)]` and receive the
  shared `DlnaTestServer` through their constructor.
- **Skips, not silent passes.** When a test cannot run on a machine (no
  ffmpeg, no non-loopback address, a Windows-only behaviour), skip it with
  `Assert.Skip`, `Assert.SkipWhen` or `Assert.SkipUnless` and a reason, so it
  shows as skipped.
- **Cancellation.** Pass `TestContext.Current.CancellationToken` to async
  APIs (xUnit analyzer rule xUnit1051).
- **Helpers** in `tests/Support`:
  - `TempDirectory` creates and deletes a scratch folder.
  - `TestMedia` writes real JPEGs, transparent PNGs and animated GIFs
    (ImageSharp) and, when ffmpeg is installed, short videos.
  - `TestFileServer.Create` builds a `FileServer` over a folder.
  - `ConfigurationHome` redirects the configuration file to a scratch folder
    and reads back what was written.
  - `ServerCommandRunner` runs `--server` commands and captures their output
    and exit code.
  - `DlnaTestServer` and `DlnaClient` host file servers on a real
    `HttpServer` and talk to it with SOAP, choosing the User-Agent and the
    local address each request connects through.
  - `CultureScope.CommaDecimals` switches to es-CO, which writes decimals
    with a comma, so culture-dependent output fails on every machine. With
    `processWide: true` it reaches the HTTP server's threads too; use that
    only in a collection that runs alone.
- **Regression tests name their bug.** Each one's summary says what used to
  go wrong. When adding one, check that it fails with the fix reverted before
  relying on it.

`DlnaTestServer` uses the internal `HttpServer(port, announce: false)`
constructor. With SSDP off, tests never announce `test-<guid>` servers to
real TVs on the network, need no UDP port 1900, and shut down instantly
rather than after SSDP's byebye datagrams drain.

Traps
---

Each of these has broken something before.

### Runtime and platform

- **Reflection discovers views, comparers and thumbnail loaders.**
  `Repository<T>` (`util/Repository.cs`) instantiates every type in the
  interface's assembly that implements `IView` or `IItemComparer` and has a
  public parameterless constructor; `ThumbnailMaker` does the same for
  `IThumbnailLoader`. Adding one needs no registration. It also means
  **trimming must stay off**: the trimmer cannot see these types are used.
  A constructor that throws hides that type: `Repository<T>` silently,
  `ThumbnailMaker` with an Info log line (the video loader throws when ffmpeg
  is missing, which used to disable image thumbnails too).
- **`Delegate.BeginInvoke` throws `PlatformNotSupportedException` on
  .NET.** It compiles without a warning. Use `ThreadPool.QueueUserWorkItem`
  or tasks.
- **`Assembly.Location` is `""` in a single-file executable.** Use
  `AppContext.BaseDirectory` for the program's folder.
- **`Environment.GetFolderPath` returns `""` for a folder that doesn't
  exist**, such as the home folder of a service account. Pass
  `Environment.SpecialFolderOption.DoNotVerify` when you need the path, and
  skip empty results when searching (`FFmpeg.GetSpecialLocations`).
- **No native libraries.** The release is a single executable because every
  dependency is managed code; SkiaSharp and SQLite were replaced for that.
  A package with native parts either adds files beside the executable or,
  bundled with `IncludeNativeLibrariesForSelfExtract`, is extracted to
  `$HOME/.net` before `Main` runs, and then the program refuses to start at
  all when `HOME` is missing or read-only. Check the `dotnet publish` output
  before adding a package.
- **Shutdown has to be graceful.** Ctrl+C and SIGTERM (how services and
  containers are stopped) both release `Main`, which then disposes the HTTP
  server and every file server; that closes the caches and removes their
  lock files. `HttpServer.Dispose` alone only unregisters the file servers.
  Anything else that must be released at exit belongs on that path.
- **Windows-only features.** Looking up a client's MAC address
  (`util/AddressToMacResolver.cs`) only works on Windows, so MAC restrictions
  never match elsewhere. The `.sdlna` folder's hidden attribute and the
  console icon are Windows-only too. Guard such code with
  `OperatingSystem.IsWindows()`.
- **ImageSharp stays on 3.1.** ImageSharp 4 fails Release builds unless a
  Six Labors license key is configured. 3.1 is under the Six Labors Split
  License, which grants Apache 2.0 to open-source projects like this one.
- **Thumbnails decode at the size they are shown.** `ThumbnailMaker.LoadImage`
  reads the image's size first and passes the fitted size as
  `DecoderOptions.TargetSize`, so JPEGs decode at a reduced scale (about
  three times faster for a 12 MP photo). `TargetSize` also enlarges smaller
  images, so it is only set when shrinking.

### Protocol

- **The browse cache key must include everything a response depends on.**
  `MediaMount_SOAP.cs` caches SOAP responses under a key built from the
  prefix, `request.LocalEndPoint`, `DlnaMaps.MimeVariant(request.Headers)`
  and the request parameters. The endpoint is there because DIDL links embed
  the address the client connected to; without it, a browse from
  `127.0.0.1` sent network clients to localhost. The MIME variant is there
  because Samsung clients get different types. If you make a response depend
  on anything else about the request, add it to the key.
- **Samsung gets `video/x-mkv`.** Clients whose User-Agent contains
  `SEC_HHP_` or `SamsungWiselinkPro` are sent `video/x-mkv` for Matroska and
  WebM; everyone else gets `video/x-matroska` and `video/webm`. The type
  appears in three places, which must agree: DIDL `protocolInfo`, the file
  response's `Content-Type`, and the GetProtocolInfo list. Go through
  `DlnaMaps.MimeFor` and `DlnaMaps.ProtocolInfoFor` instead of reading
  `DlnaMaps.Mime` directly.
- **Protocol values must not depend on the machine's culture.** Durations
  were once formatted with `TimeSpan`'s culture-sensitive `"g"`, so an es-CO
  machine sent `duration="0:00:02,366"`. Format what goes into SOAP and DIDL
  with `CultureInfo.InvariantCulture`, and durations with
  `Formatting.FormatDuration` (`H+:MM:SS.FFF` in total hours, as UPnP
  requires; the HTML index shows the same text).
- **Loopback clients are always admitted**, by `HttpServer.AuthorizeClient`
  and by `MediaMount.HandleRequest`, whatever the restrictions. Restrictions
  can only be observed from another address.
- **Restrictions are alternatives.** A client matching any entry of any kind
  (IP, MAC, User-Agent) is admitted. User-Agent entries must match the whole
  header exactly, including case.
- **Audio-only servers get the `music` view.** `FileServer.Load` adds it when
  the server serves only audio and has no views.
- **A listed cover must exist, or be a 404.** Clients fetch every
  `albumArtURI` they are given. An item whose `Cover` is null (audio without
  embedded art) gets no cover links. Other covers are thumbnails made when
  first fetched, so a Browse can list one that can't be made; the cover
  handler answers 404 when a cover has no `InfoSize`. Both cases used to end
  in a 500.

### Persistence

- **Changing a cached payload means bumping `FileStore.SCHEMA`.**
  `MediaSerializer` replaced `BinaryFormatter` (removed from .NET 9) with an
  explicit field-by-field format. Change what `AudioFile`, `ImageFile` or
  `VideoFile` write or read, and old caches will misparse unless the schema
  number changes, which makes existing caches be discarded and rebuilt.
- **`DlnaMime` values are persisted.** Append new members at the end of the
  enum; never reorder or insert.
- **The cache is a LiteDB database, and LiteDB needs care** (see
  `fsserver/FileStore.cs`):
  - Its direct mode must not have two engines on one file, yet it lets a
    second process open and write the same file. Servers in one process
    share one database per file, and `FileStore.LockCache` keeps other
    processes out; they run without a cache.
  - The collation is stored in the file when it is created. The default is
    the current culture, which can't be loaded under invariant
    globalization (common in containers), so it is set to `/Ordinal`.
  - Index keys are limited to 1023 bytes, so documents are keyed by a
    SHA-256 of the path, and the path is stored and compared on read.
  - The schema number is the database's `UserVersion`. Anything that isn't
    this version's database (including SQLite caches from 2.1.x and
    earlier) is deleted and recreated, but never a file that is merely
    locked or read-only.
  - While it is open, LiteDB keeps a `-log` file beside the database, and
    the lock file sits there too. `FileStore.IsStoreFile` keeps changes to
    all three from triggering rescans.
- **A stored cover belongs to one version of a file.** Metadata is often
  stored again without the cover, which loads lazily, so the stored cover is
  kept, but only while the file's size and modification time are unchanged.

### Configuration file

- **The configuration binder appends to collections** that already hold
  items instead of replacing them, so the model's collections start empty
  and `ConfigurationStore.Normalize` fills in defaults after binding. An
  empty JSON array (`[]`) binds to `null`, not to an empty list, which
  `Normalize` also handles.
- Unknown keys are errors (`ErrorOnUnknownConfiguration`), so renaming a key
  breaks existing files. Add new keys; don't rename old ones.
- Writes are atomic (a temporary file, then a move) so a crash never leaves a
  half-written configuration, and `--edit` keeps working on an invalid file.
  Keep both properties.
- `ConfigurationStore.HomeDirectoryOverride` redirects the file's location
  for tests. Use it (through `ConfigurationHome`) rather than changing `HOME`.

Releases
---

`.github/workflows/release.yml` publishes a GitHub release for every push to
`master` that changes shipped code.

- **What doesn't release.** Pushes that only touch `**.md` files, `LICENSE`,
  `assets/`, `tests/` or git and editor settings are ignored.
- **Version.** The latest `vX.Y.Z` tag with the patch number bumped. For a new
  minor or major version, run the workflow by hand (Actions, Release, Run
  workflow) and enter it.
- **Tests gate the binaries.** The `test` job runs `dotnet test -c Release`
  on Linux, Windows and macOS, with ffmpeg installed so the video tests can't
  quietly skip. Nothing is built unless all three pass.
- **Binaries.** `build` publishes `win-x64`, `osx-arm64`, `linux-x64` and
  `linux-arm64` from Linux: self-contained, single-file and untrimmed (see
  [Traps](#traps)). The build fails if the publish output holds anything
  but the executable, and each archive holds just it and `LICENSE`.
- **Smoke test.** `smoke` runs each archive on its own platform (including an
  Arm Linux runner) with `.github/smoke/run.sh`: it serves two images with
  `HOME` pointing nowhere, browses them, checks downloads, JPEG thumbnails
  and the cache, and stops the server with SIGTERM. `release` waits for all
  four, then uploads the archives with `SHA256SUMS.txt` and notes listing the
  commits since the previous tag.
- **Skip tokens.** GitHub skips the workflow when the pushed head commit's
  message contains `[skip ci]`, `[ci skip]`, `[no ci]`, `[skip actions]` or
  `[actions skip]` anywhere, even quoted while explaining something. Keep
  those strings out of messages you want released, merge commits included.

Before you commit
---

- `dotnet build` with no warnings, and `dotnet test` passing.
- New behaviour has a test; a bug fix has a regression test that fails
  without the fix.
- `Readme.md` and the command help describe any user-visible change.
- Cached payload changed? `FileStore.SCHEMA` bumped.
- New response dependency? Added to the browse cache key.
