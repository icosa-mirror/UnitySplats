<!--
# PlayCanvas LOD Streaming

`GsplatLodStreamer` reads PlayCanvas `lod-meta.json` manifests and streams their referenced Gaussian-splat resources. It uses the same depth-first leaves, nearest-AABB distance, perspective-FOV correction, geometric distance bands, behind-camera penalty, underfill preference, and one-step finer prefetch policy as the PlayCanvas engine.

## Quick setup

For a local player build, keep the converter output together under StreamingAssets:

```text
Assets/StreamingAssets/playbot/
  lod-meta.json
  0_0/meta.json
  0_0/means_l.webp
  ...
```

Select `lod-meta.json` and choose **Assets > UnitySplats > Import selected lod-meta.json**. Because Unity keeps StreamingAssets as raw files, this creates or updates a matching asset under `Assets/UnitySplats/LodManifests/` while retaining the original `streaming-assets://` data base. Then choose **GameObject > UnitySplats > LOD Streamer** and assign that manifest asset.

The component also accepts a `ManifestUri`. A relative URI resolves below `Application.streamingAssetsPath`; absolute `http://` and `https://` URLs are supported through `UnityWebRequest`.

```csharp
var streamer = gameObject.AddComponent<GsplatLodStreamer>();
streamer.ManifestUri = "https://cdn.example.com/capture/lod-meta.json";
streamer.MaxConcurrentLoads = 2;
streamer.MaxRetries = 2;
streamer.CacheCooldownSeconds = 2;
```

## Manifest contract and validation

The parser accepts the PlayCanvas shape:

```json
{
  "lodLevels": 7,
  "environment": null,
  "filenames": ["0_0/meta.json", "1_0/meta.json"],
  "tree": {
    "bound": { "min": [-1, -1, -1], "max": [1, 1, 1] },
    "children": [
      {
        "bound": { "min": [-1, -1, -1], "max": [0, 1, 1] },
        "lods": {
          "0": { "file": 0, "offset": 0, "count": 100000 },
          "1": { "file": 1, "offset": 0, "count": 50000 }
        }
      }
    ]
  }
}
```

- LOD `0` is full detail; larger indices are coarser.
- Leaves are flattened in depth-first order. A node must be either a branch with non-empty `children` or a leaf with `lods`.
- Missing LOD entries are legal. Present keys must be canonical decimal indices within `lodLevels`.
- `file` indexes `filenames`; `offset` and `count` are non-negative splat indices. Checked arithmetic rejects interval overflow.
- Bounds must have three finite, ordered values. They receive the same coordinate conversion as the splats; Auto defaults to the usual 3DGS/OpenGL RUB frame.
- Once a file decodes, every interval is checked against its actual `SplatCount` before it can render.
- Duplicate filenames and overlapping leaf intervals are accepted for PlayCanvas compatibility. Active intervals for each file are sorted and unioned, so a source splat is submitted once.

## Selection behavior

For each leaf, the camera is transformed into the streamer's local space and distance is measured to the nearest point of its AABB. Perspective FOV is normalized against a 45-degree reference using the smaller horizontal/vertical tangent. When enabled, nodes behind the camera receive PlayCanvas' linearly interpolated angular penalty.

```text
LOD 0 when distance < baseDistance
LOD = 1 + floor(log(distance / baseDistance) / log(multiplier)) otherwise
```

The result is clamped to `LodRangeMin`, `LodRangeMax`, and the manifest. With `LodUnderfillLimit > 0`, an already-resident coarser level in the allowed range is preferred while finer data loads. Prefetch advances only one valid level toward the optimal LOD.

The old level remains active until the replacement is decoded and its asynchronous GPU upload is complete. At transition, all leaf intervals are regrouped per file and applied together using `GsplatRenderer.SetActiveRanges`, avoiding a deliberate empty frame. Initial display still necessarily waits for the first resource.

## Resource loading

- At most `MaxConcurrentLoads` manifest files load at once; the default matches PlayCanvas at two.
- Genuine failures retry `MaxRetries` times. A permanently failed URL becomes eligible again after `FailureCooldownSeconds`.
- Unreferenced decoded assets and their renderer/GPU resources remain cached for `CacheCooldownSeconds`, then are destroyed deterministically.
- Local desktop paths use asynchronous file I/O. HTTP, Android `jar:` StreamingAssets, and content URLs use `UnityWebRequest`.
- Referenced `meta.json` files are treated as unpacked SOG: metadata and all WebP dependencies are fetched before decoding. Bundled SOG, PLY, SPZ, and Gaussian-splat GLB use the normal runtime decoder.
- The optional `environment` resource has its own full-range renderer and remains referenced while the streamer is active.

## Current limitations

- File/network I/O and GPU uploads are asynchronous, but format decoding and Spark packing currently run as a non-incremental main-thread step because Unity object creation and the native WebP bridge are main-thread-bound. Very large files can still produce a decode hitch.
- Unity uses one renderer and sort resource per resident manifest file rather than PlayCanvas' unified work buffer. Exact ranges are preserved, but many simultaneously resident files mean multiple draws and sorts.
- PlayCanvas' global active-splat budget balancer, per-camera GPU frustum interval compaction, shadow/projector integration, and device-loss reconstruction are not implemented by this controller.
- Explicit active ranges bypass compute cutouts. Clear the ranges before using cutouts on a normal renderer.
- Assets outside StreamingAssets import with an editor-local absolute base path and will not be readable in a player build. Move the complete directory under StreamingAssets or use a remote URI.
- Remote hosting must satisfy platform networking rules such as browser CORS, iOS ATS, and Android cleartext restrictions.

The engine fixture mirrored by the package tests is `engine/examples/assets/splats/playbot/lod-meta.json`: seven levels, two leaves, and shared per-level files split into exact adjacent intervals.
-->
