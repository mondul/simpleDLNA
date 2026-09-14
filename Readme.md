SimpleDLNA
===
A simple, zero-config DLNA media server, that you can just fire up and be done with it.


See [the github page](http://nmaier.github.io/simpleDLNA/) for more details and downloads.

- [Downloads](#downloads)
- [Building](#building)
- [Quick start](#quick-start)
- [Two ways to run](#two-ways-to-run)
- [Configuring servers](#configuring-servers)
  - [The configuration file](#the-configuration-file)
  - [Adding and removing servers](#adding-and-removing-servers)
  - [Changing a server](#changing-a-server)
  - [Editing the file directly](#editing-the-file-directly)
- [Views](#views)
- [Restrictions](#restrictions)
- [Thumbnails and ffmpeg](#thumbnails-and-ffmpeg)
- [Project layout](#project-layout)

Downloads
---

Every code change on `master` publishes a
[release](../../releases) with self-contained binaries for Windows x64,
macOS ARM64, and Linux x64 and ARM64. They carry their own runtime, so
nothing needs to be installed: unpack and run. Each archive holds the `sdlna`
executable and two native libraries it needs (SkiaSharp and SQLite). Keep them
in the same folder; to put `sdlna` on your `PATH`, link to it rather than
moving the executable on its own.

Each release ships a `SHA256SUMS.txt`; verify a download with
`sha256sum -c SHA256SUMS.txt` (`shasum -a 256 -c` on macOS).

Building
---

Requires the [.NET SDK 10.0](https://dotnet.microsoft.com/download) or newer.

```
dotnet build -c Release
```

Runs on Windows, Linux and macOS.

The examples below write `sdlna` for the program. From a source build, that
means either of:

```
dotnet run --project sdlna -- [OPTION]...
dotnet sdlna/bin/Release/net10.0/sdlna.dll [OPTION]...
```

Quick start
---

With nothing set up, just run `sdlna` in a folder to serve it:

```
cd ~/Videos
sdlna
```

It warns that no servers are configured and serves the current directory. Or
name the folders to serve:

```
sdlna ~/Videos ~/Music
```

Either way nothing is saved. To keep a setup, add a server. From then on,
running `sdlna` on its own starts it, along with any others you add:

```
sdlna --server add Videos ~/Videos
sdlna
```

Any DLNA client on the network, such as a smart TV, games console or VLC,
finds the server by its name.

Two ways to run
---

**With folders**, `sdlna <folder>...` serves exactly those folders, using only
the options on the command line. The configuration file is not read at all.

**Without folders**, `sdlna` starts every server in the
[configuration file](#the-configuration-file). If no servers are configured,
because the file doesn't exist or lists none, it serves the current directory
instead, exactly as `sdlna .` would, and prints a warning explaining how to
save servers. A configuration file that exists but is invalid is reported as an
error rather than skipped.

The options split accordingly. `sdlna --help` lists them all.

| Option | With folders, or no servers configured | Configured servers |
|---|---|---|
| `-p`, `--port` | HTTP port (default 0: any free port) | Overrides `port` from the file |
| `-c`, `--cache` | Metadata cache file (default: none) | Overrides `cache` from the file |
| `-l`, `--log-level`, `--log-file` | Logging | Logging |
| `--no-rescanning` | Don't watch folders for changes | Same, for every server |
| `-t`, `-s`, `-d`, `-v`, `-n`, `-i`, `-m`, `-u`, `--seperate` | Media types, sort order, views, name and restrictions | Rejected: each configured server has its own |

Configuring servers
---

The old Windows GUI let you keep several named servers, each with its own
folders, media types, views and restrictions. `sdlna --server` does the same
from the command line, on any platform. Every server runs inside one `sdlna`
process, on one port.

`sdlna --server help` summarises everything below.

### The configuration file

| Platform | Location |
|---|---|
| Linux, macOS | `~/.sdlna/config.json` |
| Windows | `%USERPROFILE%\.sdlna\config.json` (the `.sdlna` folder is created hidden) |

It is created by the first `sdlna --server add`. A complete example:

```json
{
  "port": 8200,
  "cache": "/home/me/.sdlna/cache.db",
  "editor": "code --wait",
  "servers": [
    {
      "name": "Movies",
      "folders": [
        "/home/me/Movies",
        "/mnt/nas/Movies"
      ],
      "mediaTypes": ["video"],
      "sortOrder": "title",
      "sortDirection": "asc",
      "views": ["series"],
      "restrictions": {
        "macs": [],
        "ips": ["192.168.1.20"],
        "userAgents": []
      }
    },
    {
      "name": "Music",
      "folders": ["/home/me/Music"],
      "mediaTypes": ["audio"],
      "sortOrder": "title",
      "sortDirection": "asc",
      "views": ["music"],
      "restrictions": { "macs": [], "ips": [], "userAgents": [] }
    }
  ]
}
```

**Global settings**

| Key | Meaning | Default |
|---|---|---|
| `port` | HTTP port; `0` picks any free port each run. Clients find the server either way, but a fixed port keeps its web page at a stable address. | `0` |
| `cache` | File that stores media metadata and thumbnails, so restarts don't rescan every file. `"none"` turns it off. A leading `~` means your home folder. If missing or empty, the default is used. | `~/.sdlna/cache.db` |
| `editor` | Command that `--edit` opens the file with. | `notepad` on Windows, `nano` elsewhere |

**Per server**

| Key | Meaning | Default for a new server |
|---|---|---|
| `name` | Name shown to clients. Unique, ignoring case. | (required) |
| `folders` | Folders to serve; at least one. A folder missing at startup, such as an unmounted drive, is skipped with a warning. | (required) |
| `mediaTypes` | Any of `video`, `audio`, `images`; at least one. | all three |
| `sortOrder` | `title`, `date` (file date) or `size`. | `title` |
| `sortDirection` | `asc` or `desc`. | `asc` |
| `views` | [Views](#views), applied in order. | none |
| `restrictions` | [Restrictions](#restrictions): `macs`, `ips`, `userAgents`. | none |

### Adding and removing servers

```
sdlna --server add <name> <folder> [<folder>...]
sdlna --server remove <name>
```

A new server serves video, audio and images, sorted by title ascending, with no
views and no restrictions. Folders may be relative and must exist. Use quotes
for names with spaces:

```
sdlna --server add "Home Videos" ~/Videos /mnt/camera
```

### Changing a server

```
sdlna --server config                        # every setting and every server
sdlna --server config <name>                 # one server
sdlna --server config <name> <option>...     # change one server
```

Server names ignore case. **Each option given without values prints its
current setting**, e.g. `sdlna --server config Movies --views`.

Several options can be combined in one call. They are all checked first, and
the file is only saved if every one of them succeeds.

#### `--media-types set|unset <type>...`

Turns media types on or off: `video`, `audio`, `images`. At least one must stay
on.

```
sdlna --server config Movies --media-types unset audio images
sdlna --server config Movies --media-types set images
```

#### `--sort-order <order> [asc|desc]`

`title`, `date` or `size`. The direction defaults to `asc`.

```
sdlna --server config Movies --sort-order date desc
```

#### `--folders add|remove <folder>...`

A server needs at least one folder. Two spellings of the same folder, such as a
path and a symlink to it, are recognised as one folder rather than served
twice.

```
sdlna --server config Movies --folders add /mnt/nas/Movies
sdlna --server config Movies --folders remove ~/Downloads
```

#### `--views add|remove <view>...`

Takes the same values as `-v`; see [Views](#views). Views are applied in the
order they were added. Removing a view by its bare name removes it whatever its
options, so `remove large` also removes `large:size=1000`.

```
sdlna --server config TV --views add new series
sdlna --server config TV --views remove new
```

To change the order, remove and re-add, or [edit the file](#editing-the-file-directly).

#### `--restrictions add|remove --mac|--ip|--user-agent <entry>... [-- ...]`

See [Restrictions](#restrictions) for how these behave. Entries of one kind
follow its flag. `--` ends a group, after which another kind can follow, and
you can switch between `add` and `remove`:

```
sdlna --server config Movies --restrictions add --ip 192.168.1.20 192.168.1.21
sdlna --server config Movies --restrictions add --mac 01:AF:BC:00:0A:FF -- --ip 192.168.1.30 -- --user-agent "Some Player/1.0"
sdlna --server config Movies --restrictions remove --ip 192.168.1.21 -- add --ip 192.168.1.22
```

#### `--edit` and `--editor`

These act on the whole file, so they need no server name.

```
sdlna --server config --edit                  # open the file in the editor
sdlna --server config --editor                # show the editor
sdlna --server config --editor "code --wait"  # set it
sdlna --server config --editor default        # back to notepad / nano
```

`sdlna` waits for the editor to close, then checks the file and reports any
problem. Graphical editors that return immediately need their "wait" flag,
e.g. `code --wait`. On Windows, give the full name of script launchers, such
as `code.cmd --wait`.

### Editing the file directly

You can edit `config.json` by hand, with `--edit` or any editor.

- Comments (`//`) and trailing commas are accepted. They are removed the next
  time a `sdlna --server` command rewrites the file.
- Unknown keys are errors, so a typo is reported rather than silently ignored.
- Every problem is reported at once, with the server and setting it concerns:

  ```
  Error: The configuration in /home/me/.sdlna/config.json is invalid:
    - server "Movies" has an unknown view "musik" (see sdlna --list-views)
    - server "Movies" has an invalid MAC address "00:00:00:00" (expected six hex pairs, like 01:AF:BC:00:0A:FF)
  Fix it with: sdlna --server config --edit
  ```

`--edit` works even when the file is broken, so it is always the way back.

Views
---

Views change how clients see your media, without touching the files. Apply
them with `-v` on the command line, or `--views add` for a configured server.

**Reorganizing views** build new virtual folders:

| View | What clients see | Options |
|---|---|---|
| `bytitle` | Every file sorted into A-Z folders by the first letter of its title. A letter with over 100 files is split further by the words its titles start with. | |
| `flatten` | Folders holding three files or fewer dissolved into their parent, so sparse, deeply nested trees become shallower. | |
| `music` | `Artists`, `Performers`, `Albums` and `Genre` folders, built from the files' tags. The original folders move under `Folders`. | |
| `plain` | Every file directly in the top folder. | |
| `series` | A folder for each TV show with at least two episodes, recognised from names such as `Show S01E02`, `Show 1x02`, `Show 102` or a date. | `no-cascade` |
| `sites` | A folder for each site with at least two files, recognised from names such as `[site] title` or `site - title`. | `no-cascade` |

Once `series` or `sites` produce more than 50 folders, those folders are
gathered into A-Z folders. `no-cascade` keeps them all at the top.

In `music`, *Artists* comes from the album artist tag (or the composer), and
*Performers* from the track artist.

**Filtering views** hide what doesn't match:

| View | Shows only | Options | Default |
|---|---|---|---|
| `new` | Files modified recently | `date`: earliest modification date, e.g. `2026-01-31` | last 7 days |
| `large` | Files of at least a size | `size`: in MB | 300 MB |
| `dimension` | Images and videos within a pixel size range. Items without a known size, including all audio, are hidden. | `min`: shorter side at least; `max`: longer side at most; `minwidth`, `maxwidth`, `minheight`, `maxheight` | no limits |
| `filter` | Files whose title or path contains one of the words, ignoring case. A word containing `*` or `?` must match the whole title or path instead. | the words themselves | shows everything |

Options follow the view's name after a colon, separated by commas:

```
sdlna -v large:size=1000 ~/Videos
sdlna -v filter:holiday,beach ~/Pictures
sdlna -v dimension:min=1080 ~/Pictures
sdlna -v series:no-cascade ~/TV
```

**Views are applied in order**, each working on the result of the one before.
You can combine filtering and reorganizing views; for example, this shows only
this week's episodes, organised by show:

```
sdlna -t video -v new -v series ~/TV
```

and the same for a configured server:

```
sdlna --server config TV --views add new series
```

`sdlna --list-views` prints this summary.

Restrictions
---

Restrictions limit which clients may browse and play a server.

- **No restrictions** means every client may connect.
- **With restrictions**, only clients matching **at least one entry of any
  kind** may connect. A MAC entry and an IP entry are alternatives, not
  requirements that must both hold.
- **Clients on the same machine as `sdlna` are always allowed.**
- For configured servers, restrictions apply to each server separately. With
  `-i`, `-m` and `-u`, they apply to everything the process serves.

The three kinds match differently:

| Kind | Matches | Format |
|---|---|---|
| IP (`--ip`, `-i`) | The client's IP address | e.g. `192.168.1.20` or an IPv6 address |
| MAC (`--mac`, `-m`) | The client's network card. **Only works when `sdlna` runs on Windows**; on Linux and macOS a client's MAC can't be looked up, so a MAC entry never matches. | Six hex pairs: `01:AF:BC:00:0A:FF`. The Windows form `01-AF-BC-00-0A-FF` is accepted too. |
| User-Agent (`--user-agent`, `-u`) | The client's `User-Agent` HTTP header, which **must be identical, including case**. Part of the header is not enough. | Any text; quote it |

A server restricted only by MAC addresses therefore admits no other machine
when run on Linux or macOS. `sdlna --server` warns when you add one there.

**Finding a client's User-Agent or MAC.** Only the kinds of entry a server
actually has are checked and logged. So add a placeholder entry of the kind you
want to discover, run `sdlna -l DEBUG`, and browse from the client (from another
machine, since local clients skip the check):

```
sdlna --server config Movies --restrictions add --user-agent placeholder
sdlna -l DEBUG
```

Each refused request is logged with the value that was checked, e.g.
`Rejecting Some Player/1.0. Not in User-Agent whitelist`. Replace the
placeholder with the real value afterwards. For a MAC, use a placeholder such
as `--mac 00:00:00:00:00:00` (Windows only).

Thumbnails and ffmpeg
---

Thumbnailing video files and reading their duration requires
[ffmpeg](https://ffmpeg.org/) on `PATH`, in `FFMPEG_HOME`, or beside the
`sdlna` executable. Image and audio handling have no external dependencies.

Project layout
---

| Project    | Output                        | Role                                                        |
|------------|-------------------------------|-------------------------------------------------------------|
| `util`     | `SimpleDlna.Utilities`        | SQLite access, ffmpeg invocation, stream pumps, sorting      |
| `server`   | `SimpleDlna.Server`           | HTTP and SSDP servers, UPnP/DLNA handlers, views, comparers  |
| `thumbs`   | `SimpleDlna.Thumbnails`       | Thumbnail generation                                         |
| `fsserver` | `SimpleDlna.FileMediaServer`  | Filesystem media source and its metadata cache               |
| `sdlna`    | `sdlna`                       | Console entry point and configuration file                   |
