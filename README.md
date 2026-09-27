# WinThumbsPreloader
Thumbnails preloader for Windows Explorer

![Screenshot](https://raw.githubusercontent.com/bruhov/WinThumbsPreloader/master/Website/images/preview.gif)

### Download: [WinThumbsPreloader-1.0.1-setup.exe](https://github.com/bruhov/WinThumbsPreloader/releases/download/v1.0.1/WinThumbsPreloader-1.0.1-setup.exe)

## Command-line version

The `WinThumbsPreloader/WinThumbsPreloader/cli/` folder contains a console
front-end for the same thumbnail engine used by the Windows Forms app. Both
share `ThumbnailPreloader.cs` and `DirectoryScanner.cs`, so they produce
identical thumbnails and the GUI is unaffected.

Windows builds thumbnails lazily: a folder looks unstyled until every item in it
has been rendered at least once. The CLI walks a tree and asks Windows for each
thumbnail up front. That is most useful for a freshly imaged or restored drive, a
network share, or a backup archive that will be browsed later while offline.

### Build

Requires the .NET Framework 4.x runtime or targeting pack. There are no NuGet
dependencies.

```
cd WinThumbsPreloader/WinThumbsPreloader/cli
powershell -ExecutionPolicy Bypass -File build.ps1
```

`build.ps1` locates `MSBuild.exe` and `csc.exe` under the .NET Framework
directory and sets `FrameworkPathOverride` itself, so it also works without
Visual Studio or the developer targeting pack installed. The output is
`WinThumbsPreloader/WinThumbsPreloader/cli/bin/Release/WinThumbsPreloader-cli.exe`.

The CLI is a normal project inside `WinThumbsPreloader.sln`, so it builds with the
Windows Forms app from Visual Studio.

### Releases

Pushing a `v*` tag builds the CLI on GitHub Actions and publishes a zip containing
the executable as a GitHub release. The number from the tag is baked into the
binary, so `--version` always reports the release it shipped in. The WinForms
project is not built by CI because it targets .NET Framework 4.5, whose targeting
pack is not installed on the runners.

The code is kept compatible with C# 5, which is what the in-box .NET Framework
compiler supports.

### Usage

```
WinThumbsPreloader-cli [options] <path> [<path> ...]
```

Each `<path> may be a folder (scanned) or a single file. Drive roots such as
`X:\` are valid targets.

| Option | Description |
| --- | --- |
| `-r`, `--recursive` | Descend into subfolders. Without it only the given folder is scanned. |
| `-s`, `--size <px>` | Thumbnail size to request, 1-4096 (default 128). |
| `-f`, `--force` | Re-extract items that already have a cached thumbnail. |
| `--cache-only` | Only read the existing thumbnail cache, extract nothing. |
| `--files-only` | Skip folder entries, preload files only. |
| `--dirs` | Include folder entries (default). |
| `--follow-links` | Descend into junctions and symlinks (off: avoids cycles). |
| `-j`, `--jobs <n>` | Extract with `n` worker threads, 1-64 (default 1). |
| `-n`, `--dry-run` | Count and list items, touch nothing. |
| `--strict` | Treat items without a thumbnail handler as failures. |
| `-q`, `--quiet` | No progress output. |
| `-v`, `--verbose` | One line per item. |
| `-h`, `--help` | Show help. |
| `--version` | Show the version. |

Short flags may be grouped (`-rq`) and values may be attached (`-s256`). `--`
ends option parsing.

Examples:

```
# everything below a folder, in the background
WinThumbsPreloader-cli -r -q X:\Media

# a whole drive, counting only, to see the scope first
WinThumbsPreloader-cli -r -n X:\

# confirm what is already cached
WinThumbsPreloader-cli -r --cache-only X:\

# large images, files only, four workers
WinThumbsPreloader-cli -r --files-only -s 512 -j 4 X:\Media
```

### Exit codes

| Code | Meaning |
| --- | --- |
| 0 | Finished with no failures. |
| 1 | Bad arguments, or a target that does not exist. |
| 2 | Cancelled with Ctrl+C. Press it a second time to force quit. |
| 3 | At least one item failed, or `--strict` was given and items had no thumbnail. |

A non-zero code is not always a problem with the run: broken shortcuts are
counted as failures, so a tree containing them exits 3 even though every readable
item was preloaded.

### What the result categories mean

The summary splits items that produced no thumbnail, because Windows conflates
two very different situations:

- **preloaded** — a thumbnail is now in the Windows thumbnail cache.
- **without a thumbnail handler** — Windows has no thumbnail provider for that
  type, such as `svg`, `psd` or camera raw. Explorer draws those from the icon
  cache instead, so this is expected and not a fault. Mostly non-media files.
- **damaged or unreadable** — the type *does* have a provider, but extraction
  produced nothing. For images this usually means the file content is not a
  valid image: the shell reports both cases with the same error code
  (`WTS_E_CACHE_BITS_NEW_AVAIL`), so the CLI infers the difference from the file
  extension. These are worth investigating, and the first 20 are listed on
  stderr. A file whose bytes are, for example, a directory index record or an
  executable header rather than image data is mis-carved or corrupted, not
  simply unsupported.
- **failed** — anything else, such as a shortcut pointing at a missing target.

Only a sample of the middle two categories is printed; pass `--verbose` to list
every item.

### Notes

- Reparse points are skipped by default. A junction pointing at one of its own
  ancestors makes the walk loop forever, so `--follow-links` is opt-in and has no
  cycle detection.
- `--jobs 1` is the default on purpose. Extra workers help when most items are
  already cached, but on a cold cache they mostly contend and can be slower.
- Folders that cannot be read, such as `System Volume Information`, are reported
  as a note and do not affect the exit code.
- The thumbnail cache is global and shared, so preloading a drive also warms up
  other locations that use the same files.
