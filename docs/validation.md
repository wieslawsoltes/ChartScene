# Validation and evidence

## Required gates

| Scope | Entry point | Evidence |
| --- | --- | --- |
| Data/numerics/models/geometry/interaction/Skia/examples | `dotnet run --project tests/ChartScene.Tests -c Release` | `artifacts/core-tests.json` and generated PNGs |
| Native GPU | `dotnet run --project tests/ChartScene.GpuTests -c Release` | `artifacts/gpu-tests/results.json`, GL/GLES readback images and upload counts |
| 2D browser | `tests/browser/validate.py --catalog` | All 43 examples, real pointer/keyboard input, PNG/CSV downloads and screenshots |
| Linked charts | `tests/browser/validate-linked.py` | Unit conversion, cursor handoff, links/toggles, model replacement, resize and reattachment |
| 3D browser | `tests/browser/validate-3d.py` | Real GLCanvas pixels, cameras/history, projections, selection, all 10 examples and reattachment |
| Packages | `tools/verify-packages.py` | Exact package identities and isolated desktop/browser consumer builds |

The application workflow reruns core tests, builds desktop, publishes browser, serves the current output at `/ChartScene/`, runs all three browser suites and verifies packages. Native EGL checks run independently. Pages deployment requires both jobs to pass on main.

Browser diagnostic objects are read-only observations. Tests never modify them to pass. Generated DTOs avoid recursive computed properties and remain trimming-safe. Managed render completion occurs before Uno framebuffer readback/composition: screenshot checks therefore poll actual visible pixels, bounded to eight seconds, requiring the unchanged color threshold in two consecutive observations. No input or forced render is used to make the initial frame appear. `presentation.json` records every observation. This is presentation correctness evidence, not a hardware latency benchmark.

## Browser checks

After publishing/staging as described in the README:

```sh
python3 -m pip install playwright==1.56.0 pillow
python3 -m playwright install chromium
mkdir -p artifacts/server/ChartScene
cp -a artifacts/site/. artifacts/server/ChartScene/
python3 -m http.server 8765 --directory artifacts/server
```

In another terminal:

```sh
python3 tests/browser/validate.py --url http://127.0.0.1:8765/ChartScene/ --catalog
python3 tests/browser/validate-linked.py --url http://127.0.0.1:8765/ChartScene/
python3 tests/browser/validate-3d.py --url http://127.0.0.1:8765/ChartScene/
```

Native EGL tests require Linux EGL/OpenGL drivers. CI uses `libegl1-mesa-dev` and `libgl1-mesa-dri`. Mesa/SwiftShader may be software renderers; their results do not establish physical-GPU throughput or presentation latency.

## Package verification

```sh
for project in Core Data Numerics Charting Rendering Rendering.Skia Interaction ThreeD Rendering.OpenGL Controls Samples Samples3D; do
  dotnet pack "src/ChartScene.$project" -c Release -o artifacts/packages
done
python3 tools/verify-packages.py
```

The verifier copies the actual application outside the repository, replaces project references with exact generated package versions, uses an isolated cache and maps `ChartScene.*` to the local feed. Both target frameworks must resolve every ChartScene dependency as a package. Producing `.nupkg` files alone is insufficient.

## Uno GL host compatibility

Pinned Uno 6.7.135 allocates its framebuffer in `Loaded` using `RenderSize`. A stretched child may not have a valid size yet. `ArrangedGlHost` arranges off-tree before attachment and unloads instead of allocating zero-size buffers when collapsed. Positive resizes continue through Uno's own management.

The same host assumes BGRA readback is converted by a JS resolver, which does not necessarily apply to native GLES entry points. `BrowserGlReadback` selects the existing RGBA readback plus red/blue swap. The exact nonpublic field is preserved for trimming and checked at runtime. Requalify this contract on Uno upgrades; failure is explicit rather than a silently blank chart. See the [pinned upstream source](https://github.com/unoplatform/uno/blob/6.7.135/src/AddIns/Uno.WinUI.Graphics3DGL/GLCanvasElement.cs) and [CadSpace's MIT-licensed adapter](https://github.com/wieslawsoltes/CadSpace/blob/main/src/CadSpace.Controls/PortableGlCanvasElement.cs).

GL resources must be deleted with their owning context current. The reusable Uno surface does so from `OnDestroy`. Data/model engines are independent of the context and can rebuild GPU resources after reattachment.

## Artifacts and limits

`application-validation` contains exact source, commit identity, build logs, browser results/images, failure details and package-consumer evidence. `gpu-validation` contains native fixtures. `browser-app` contains the staged application and `build-info.json`. `reusable-packages` contains NuGet artifacts. Always inspect conclusions and result JSON; artifact existence alone does not prove a pass.

PR checks use GitHub's synthetic merge tree. Verify tested/merged trees and compare the deployed build identity to the main workflow. These are ChartScene regression/integration tests, not complete SciChart reference conformance. Cross-browser, Windows/macOS runtime, accessibility, physical touch, complete visual parity and hardware performance remain unqualified. CPU submission duration is not a GPU timestamp or FPS guarantee.
