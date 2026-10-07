# Sizoscope

Sizoscope is a binary size investigation tool to help you optimize and reduce the size of your .NET Native AOT binaries.

It supports visualizing the size contributions of individual methods and types, but also namespaces and assemblies. It also allows diffing before/after snapshots and offers basic root cause analysis.

## Installing Sizoscope

```shell
$ dotnet tool install sizoscope --global
```

For command-line diffing (no GUI), install the CLI tool:

```shell
$ dotnet tool install sizoscope-cli --global
```

Alternatively, download a release from the [Releases](https://github.com/MichalStrehovsky/sizoscope/releases) tab. The releases on the Releases tab are built with native AOT and are fully standalone/portable ZIPs. They work great under Linux with Wine.

There's an Avalonia-based fork of Sizoscope maintained by @hez2010 [here](https://github.com/hez2010/sizoscopeX).

## Using Sizoscope

The tool only supports the [Native AOT deployment model](https://learn.microsoft.com/dotnet/core/deploying/native-aot/) in .NET 7 or later. After enabling Native AOT deployment on your project as documented in the linked doc (basically, add `<PublishAot>true</PublishAot>` to a `PropertyGroup` in your project), add following lines to the project to enable generation of additional compile-time diagnostic files:

```xml
<PropertyGroup>
  <IlcGenerateMstatFile>true</IlcGenerateMstatFile>
  <IlcGenerateDgmlFile>true</IlcGenerateDgmlFile>
</PropertyGroup>
```

Once you have that, `dotnet publish` your project as usual. After publishing, you should see a *.mstat and *.dgml.xml file under `obj\Release\net[7|8].0\[linux|win]-[x64|arm64]\native` - these are the files Sizoscope operates on.

Launch the tool and open the MSTAT file. Size information loads without reading the associated dependency graph. Use **Dependencies > Load / retry dependencies**, or double-click an analyzable item, to load its companion `*.scan.dgml.xml` on demand. If the companion file does not exist, root cause analysis will not work.

Once the file loads, you'll be greeted with the main screen that shows assembly contributions:

![Main window screenshot](docs/mainwindow.png)

You can click around things that look interesting. You can also open the search subwindow where you can sort individual entries by exclusive/inclusive size.

If you have a different MSTAT file you'd like to compare with, click the Diff button to enter a diff view:

![Diff window screenshot](docs/diffwindow.png)

The diff view will show you things that are unique to the baseline on the left and things that are unique to the compared MSTAT on the right. Dependencies are not loaded automatically for either side; use **Left dependencies** and **Right dependencies** independently.

## Root analysis

ℹ️ NOTE: Root analysis is only supported starting in .NET 8. The MSTAT files generated with older SDKs don't have the necessary information.

The main window and the diff window will show you what is in the binary, but not _why_ it's in the binary. Double-clicking a node in the main window or diff window requests its dependency graph if necessary, then opens a root analysis window. This will try to explain _why_ something was included.

![Roots window screenshot](docs/rootswindow.png)

In the above screenshot, the reason why the top node was included in the executable was all the things nested under it. Some nodes have two reasons. This is typical for e.g. virtual method implementations - the reason why a virtual method implementation is generated is that _both_ the virtual method gets called somewhere _and_ a type implementing the method was allocated. The screenshot has one such situation for `SyncTextWriter.WriteLine`.

Root windows load dependencies only when you expand a branch, rather than constructing the whole explanation tree. A page contains up to 200 links (less for exceptionally large labels). Double-click **Load more**, or select it and press Enter, to fetch the next page. Links and reasons are not silently discarded. A `[cycle]` marker stops a branch that returns to an ancestor; shared dependencies remain visible on other branches.

In diff root windows, purple indicates that a node is absent from the other graph only when that graph is loaded. The window title indicates when comparison dependencies are unavailable.

## Large dependency graphs

Dependencies are streamed into a packed binary disk index instead of retaining every node and edge in memory. The first import reads the whole XML and performs external sorting and validation, so very large graphs still require substantial disk I/O and temporary space. Progress appears alongside the accounted size; **Cancel loading** cancels indexing without disabling size browsing.

Completed indexes are cached under `%LOCALAPPDATA%\Sizoscope\DependencyGraphs` on Windows. Set `SIZOSCOPE_GRAPH_CACHE` before launching to use a dedicated cache directory on a drive with more space. The **Dependencies** menu provides the cache location, **Rebuild dependency index**, and **Remove inactive cached indexes**. Active indexes are retained until their windows release them. A canceled or failed build never replaces a completed index.

Cache identity includes the source path, length, last-write timestamp, ZIP entry identity where applicable, and the index format version. Warm loading verifies the cached index's checksums instead of reparsing and sorting XML; this still reads the index files. The source's full digest is recorded on import, but warm loading does not recompute the original XML digest. If you deliberately replace a source while preserving its length and timestamp, explicitly rebuild the index. If the source changes after opening the MSTAT, reload it so that statistics and dependencies describe the same build.

Indexing is serialized across graphs within the process to share a **512 MiB graph buffer/cache budget**. This is not a limit on total process memory: the MSTAT, runtime, user-expanded WinForms tree, and operating system's file cache consume memory separately. The importer has no graph-sized dictionary or global string-interning table. Individual XML records and encoded labels/reasons are limited to 8 MiB and fail explicitly if oversized. XML nesting is limited to 128 levels, and the XML name table to 4,096 distinct names / 1,048,576 characters; these are parser safety limits, not limits on graph dependency depth.

Allow room for both the completed index and external-sort staging files. Their sizes depend on labels and link counts; the index is not guaranteed to be smaller than the XML. Disk-full, permissions, corrupt-cache, and input-change failures are reported and can be retried or rebuilt. Completed indexes do not keep the original graph open.

Sizes and aggregate differences use 64-bit accounting; large values display in GB or TB. This does **not** change MSTAT input limits: MSTAT files are still copied into unmanaged memory and must fit the existing signed 32-bit PE-reader length (at most 2,147,483,647 bytes). Large DGML files are separate from this limit.

## Tips and tricks

* Passing a MSTAT file name on the command line will launch Sizoscope with the file open. You can associated *.mstat files with Sizoscope in shell.
* Passing two MSTAT file names on the command line will open the diff view directly.
* You can drag and drop .mstat files into the UI to open them.
* Holding Alt when dragging and dropping will open a diff against the open file.
* MSTAT files are loaded to memory and the associated file system files are closed. This is done on purpose so that you can quickly do before/after comparisons: simply open the MSTAT, make your change to the project and re-publish, and do a diff _against the same file_.
* Both the GUI and CLI tools accept `.zip` archives containing `.mstat` (and optionally `.scan.dgml.xml`) files. The `.mstat` is read directly from the archive without extracting to disk.

## Command-line summaries and diffing

With one MSTAT input, `sizoscope-cli` ranks assemblies and the largest types:

```shell
$ sizoscope-cli app.mstat.zip --top 10
Total accounted size: 12.3 MB

=== Assemblies ===
  7.1 MB          57.7% System.Private.CoreLib
  ...

=== Top 10 types ===
  312.4 kB         2.5% [MyApp] Generated.Metadata
  ...
```

With two inputs, it prints a non-interactive size diff:

```shell
$ sizoscope-cli baseline.mstat compare.mstat
Total accounted size difference: 206.2 kB

=== New / Grown ===
  +129.0 kB     System.Private.CoreLib
  +52.4 kB      System.Net.Http
  ...

=== Removed / Shrunk ===
  -1.2 kB       System.Collections
  ...
```

The diff includes exact signed 64-bit totals, new and removed assembly content,
retained-item size changes reconciled by assembly, blob and unowned frozen-object
before/after deltas, and method-folding changes when those records are available.

Use `--output` to write either report to a file instead of stdout:

```shell
$ sizoscope-cli baseline.mstat compare.mstat --output diff.txt
```

ZIP archives work as well:

```shell
$ sizoscope-cli baseline.mstat.zip compare.mstat.zip
```

Run `sizoscope-cli --help` for concise usage and option details.

## Explaining why a node is in a dependency graph

`sizoscope-cli why` answers "why is this here?" from a `.scan.dgml.xml` or `.codegen.dgml.xml` file. It works even when no MSTAT exists, for example when ILC failed after writing the graph. The graph is indexed once into the disk-backed cache, so multi-gigabyte graphs stay out of memory. Later queries reuse the index.

```shell
$ sizoscope-cli why app.scan.dgml.xml "Big.Generated.Context"
Node #3: Reflectable type: Big.Generated.Context

Direct dependents:
  #2 Method: Startup  [typeof]

Shortest chain from a root (2 steps, 4 nodes visited):
  #1 Root: Main
    -> #2 Method: Startup  [call]
      -> #3 Reflectable type: Big.Generated.Context  [typeof]
```

The query is an exact node label, a case-insensitive substring, or `#<id>`. If a query matches several nodes, the command lists them. Options: `--exact`, `--limit N`, `--incoming N`, `--max-visit N`, `--cache DIR`, `--rebuild`, `--output FILE`.

## Regression tests

The non-GUI regression harness covers MSTAT decoding and model lifetimes, 64-bit accounting, graph indexing, cache integrity, session lifetimes, and input archive handling:

```shell
dotnet test .\sizoscope.Tests\sizoscope.Tests.csproj --configuration Release
```
