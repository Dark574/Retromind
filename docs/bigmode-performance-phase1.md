# BigMode performance: shared infrastructure, phase 1

## Scope

This change improves selection processing and shared image loading. Theme AXAML,
decode resolutions, animation durations, visual effects and VLC playback sequencing
are unchanged. No dependencies or persisted settings are added.

## Selection processing

- `SelectionIndexHelper` verifies an index against the current collection and selected
  object before reusing it. Invalid, missing or stale hints fall back to `IndexOf`.
- BigMode selection callbacks, counters and circular/wheel helpers use those hints.
  Existing public circular-helper signatures remain available unchanged.
- Reading `CircularItems`, `WheelItems`, `WheelCategories`, or either wheel selection
  index activates the corresponding window. Unused windows do not rebuild or preload
  logos. Activation is independent of theme names and needs no new theme metadata.
- Root theme replacement resets usage. Reading a property in the next theme fills the
  existing collection with current data. Active windows retain the existing incremental
  remove/add behavior, including wrap-around and finite short lists.
- Usage is tracked for the lifetime of a root theme. A theme that stops using an already
  activated property without replacing its root continues to maintain that window.

## Image ownership and cancellation

Cacheable visible requests and local preloads share one pending operation for an exact
source/DecodeWidth key. Each waiter can cancel independently. The last departing waiter
cancels the operation; a native codec already running must finish before a replacement
for the same key starts. Decode concurrency remains one for BigMode and four for desktop.

Each image owns a disposable bitmap lease. A cache hit holds a cache reference; a shared
result holds its cache reference until all its leases are released. If admission fails,
the same ownership mechanism shares the uncached result and disposes it after its last
consumer. Clearing one control cannot dispose another control's pixels.

Invalidation marks matching in-flight operations as well as retained entries. New callers
cannot join an invalidated operation, and its result cannot repopulate the cache. Existing
consumers may finish using old pixels. A referenced invalidated cache entry is removed and
disposed only when its last reference is released.

Crossfade URL changes cancel the prior load-delay token and unfinished A/B requests.
Decoded images remain available for the existing fades and local reuse. Detach invalidates
pending generations and cancels load delay; reattach resumes the current URL. Completion
callbacks use each control's owning dispatcher.

## Cache byte budget

The shared cache retains both hard limits:

- at most 200 entries;
- at most 128 MiB of decoded pixel storage, charged as `width * height * 4` bytes.

Pinned entries count against both limits and are never evicted. Admission evicts unused
entries in LRU order until both limits fit. If they cannot fit, the incoming result stays
uncached and is released with its final lease. Oversized results do not evict useful
entries just to fail admission. Every removal path updates the byte counter.

The budget does **not** bound total process memory: native codec temporaries, encoded
downloads, GPU resources, object overhead, and uncached images held by controls are outside
it. Existing per-image/download limits still apply. `DisableCache=True` continues to bypass
cache storage and request sharing; no large theme backgrounds are newly opted into caching.
The 128 MiB default is a conservative implementation choice, not a hardware-tuned optimum.

## Validation and measured work

Regression tests cover large-library index validation, stale/missing selections, unused
windows, activation order, theme usage reset, recycling, shared requests, independent and
last-consumer cancellation, in-flight invalidation, pinned invalidation, cache saturation,
LRU byte eviction, crossfade request replacement and bitmap disposal. A managed Avalonia UI
dispatcher is provided by test infrastructure; no window backend or new package is needed.

The operation-count comparison uses 50,000 objects and 1,000 selections at positions
25,000–25,999. The legacy public circular-window path performs 1,000 linear searches and
25,500,500 element comparisons. The indexed path performs zero linear searches, while
producing the same source-relative window positions. A further test combines counters'
index resolution, circular and repeating windows, and preload lookahead without a search.

The image test sends 32 concurrent consumers through the real shared loading path. It
observes one HTTP request and the same decoded bitmap in every lease. Resource tests
verify that invalidated images survive until their last lease, and that pixel storage
does not exceed 128 MiB even with every cached image pinned.

These are measured work counts and resource assertions, not end-to-end speed measurements.
No FPS, input latency, process-memory reduction or GPU improvement has been measured.
Lower selection CPU cost and less duplicate decoding are expected consequences.

Manual feedback after implementation: the user tested the themes and reported that
all felt faster. This is subjective confirmation, without recorded frametimes or latency.

Manual Linux checks remain appropriate for real controller repeats, Wheel/Arcade visual
recycling, theme detach/reattach during navigation, and VLC first-frame/video transitions
in both installed and AppImage builds. The automated image tests do not measure actual
screen presentation or GPU compositing.

Final validation on Linux:

- `dotnet build Retromind.csproj`: successful, zero warnings and zero errors.
- `dotnet test tests/Retromind.Tests/Retromind.Tests.csproj --no-restore`: 1,644 passed,
  zero failed and zero skipped. Task 1 was separately validated with 32 targeted tests
  and the then-complete 1,629-test suite before image work began.
- `git diff --check`: successful. Theme AXAML and package references have no changes.

The sandbox blocks VSTest's local communication socket, so test runs used the authorized
execution outside that sandbox. No packages or external profiling tools were installed.
