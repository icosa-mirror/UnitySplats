# Import-time opacity and size filtering

`GsplatRuntimeLoader.LoadFile` accepts an optional `GsplatImportFilter`:

```csharp
var filter = new GsplatImportFilter(maxOpacity: 0.05f, minScaleFraction: 0.02f);
var asset = GsplatRuntimeLoader.LoadFile(path, filter: filter);
```

Both tests must match: opacity is strictly below `MaxOpacity` AND the largest
linear Gaussian scale is strictly greater than `MinScaleFraction` times the
original bounds' longest dimension. The scale is not the splat's full diameter
or screen-space footprint. Bounds describe source positions, so outliers can
affect this relative threshold. These example thresholds are not a validated preset.

The default is no filter. Removing the filter restores the complete source on
the next import. Source files and original bounds are preserved. Zero opacity
thresholds and degenerate bounds remove nothing. Invalid/non-finite thresholds
are rejected; a filter leaving no splats produces a descriptive import error.

## Loading and memory

For direct Spark loading, an uncached source is scanned for bounds without
allocating packed output. It is then read again, rejecting matching splats before
packing them. On subsequent imports the bounds cache eliminates the first pass,
even when the filter thresholds change. Existing parallel decoder workers retain
their own growing buffers; surviving records and all SH bands are combined in
source order before upload. Combining requires temporary buffers alongside the
final exact-sized arrays. Thresholds that retain most splats can therefore add
loading work and temporary memory relative to an unfiltered load.

The standard PLY, SPZ and SOG direct Spark paths remain in use. Formats that already
use canonical decoded arrays (including compressed PLY/GLB) still require those
arrays; compression containers can still require full decompression. This feature
reduces packed resident data and GPU work, not all source decoding allocations.

## Bounds cache

`LoadFile` caches bounds under `Application.persistentDataPath/GsplatBounds` by
default. Pass `boundsCacheDirectory` to choose another cache directory. Entries
include a version, source length and UTC modification timestamp; their keys include
the full source path and resolved coordinate convention. The source content is not
hashed. Changing content while preserving both length and timestamp requires deleting
the cache entry. A missing, stale or corrupt cache is ignored; an unwritable cache
does not prevent import. Atomic replacement avoids exposing partial entries.

Unfiltered file imports also populate the cache. Cache entries are independent of
filter thresholds and always store original bounds. Deleting the cache is safe.
The `[SPLAT_PRUNE_20260907]` log prefix reports cache hits/misses and retained counts
for direct Spark filtering.

Byte-array/stream and unpacked-SOG loading overloads also accept `filter` and
`sourceBounds`. They cannot identify persistent source files themselves; callers
can supply known bounds to avoid scanning. Uncompressed output uses its existing
canonical decode followed by filtering before asset packing.
