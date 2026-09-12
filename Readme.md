SimpleDLNA
===
A simple, zero-config DLNA media server, that you can just fire up and be done with it.


See [the github page](http://nmaier.github.io/simpleDLNA/) for more details and downloads.

Building
---

Requires the [.NET SDK 10.0](https://dotnet.microsoft.com/download) or newer.

```
dotnet build -c Release
```

Runs on Windows, Linux and macOS.

Running
---

```
dotnet run --project sdlna -- [OPTION]... DIRECTORY...
```

Or, from a release build:

```
dotnet sdlna/bin/Release/net10.0/sdlna.dll [OPTION]... DIRECTORY...
```

`sdlna --help` lists the options. A typical invocation, serving two
directories on a fixed port with an on-disk metadata cache:

```
dotnet sdlna.dll -p 8895 -c ~/.sdlna-cache.db ~/Music ~/Videos
```

Thumbnailing video files and reading their duration requires
[ffmpeg](https://ffmpeg.org/) on `PATH` (or pointed at by `FFMPEG_HOME`).
Image and audio handling have no external dependencies.

Layout
---

| Project    | Output                        | Role                                                        |
|------------|-------------------------------|-------------------------------------------------------------|
| `util`     | `SimpleDlna.Utilities`        | SQLite access, ffmpeg invocation, stream pumps, sorting      |
| `server`   | `SimpleDlna.Server`           | HTTP and SSDP servers, UPnP/DLNA handlers, views, comparers  |
| `thumbs`   | `SimpleDlna.Thumbnails`       | Thumbnail generation                                         |
| `fsserver` | `SimpleDlna.FileMediaServer`  | Filesystem media source and its metadata cache               |
| `sdlna`    | `sdlna`                       | Console entry point                                          |
