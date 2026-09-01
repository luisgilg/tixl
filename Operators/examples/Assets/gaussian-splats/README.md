# Gaussian Splat example asset

The **Gaussian Splat Viewer** example demonstrates TiXL's experimental Gaussian Splat pipeline with `LoadGaussianSplat`.

Export or optimize the standard Mip-NeRF 360 `garden` scene as a compact SPZ (for example with SuperSplat), then place it here as:

```text
Operators/examples/Assets/gaussian-splats/garden.spz
```

The graph resolves that file as `Examples:gaussian-splats/garden.spz`. The asset is deliberately not versioned because full reference-scene exports are too large for the examples package. You can also select any compatible `.spz` or Nerfstudio `.ply` file directly on the `LoadGaussianSplat` node.
