// Shim for 'three/webgpu' (EzForensics Brain). The Brain always uses the WebGL renderer, so the WebGPU build is not shipped.
export class WebGPURenderer {
  constructor() { throw new Error('The WebGPU renderer is not included in the Brain page.'); }
}
