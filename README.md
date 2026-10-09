# ChartScene

Independent, reusable charting components for Uno Platform, targeting desktop and WebAssembly. The compatibility target is SciChart WPF's publicly documented 2D/3D charting behavior; ChartScene is not affiliated with SciChart and does not contain its proprietary implementation or branding.

## Development status

Implementation and conformance testing are in progress. Full API, behavioral, pixel, performance, and native-platform parity have not been established. See the forthcoming compatibility matrix and validation report for evidence rather than inferred coverage.

## Architecture

The implementation separates numerical/data foundations, chart models and layout, rendering contracts, Skia rendering, 3D geometry/rendering, interaction, serialization, reusable Uno controls, and the sample application. Core packages must remain usable independently of Uno.

## Build and deployment

The repository will include automated .NET tests, desktop compilation, a compiled Uno WebAssembly sample, browser interaction checks, NuGet packaging, and GitHub Pages deployment.
