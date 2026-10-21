import { dotnet } from './_framework/dotnet.js';

const status = document.getElementById('status');
const fail = (message) => {
    status.textContent = message;
    throw new Error(message);
};

// The engine asks for its GPU synchronously; the browser only answers asynchronously. So the
// adapter and device are acquired here, before the runtime starts, and Zigote.Engine/web/
// zigote_webgpu.js hands them to the engine when it asks.
if (!navigator.gpu) fail('This browser does not support WebGPU.');
const adapter = await navigator.gpu.requestAdapter({ powerPreference: 'high-performance' });
if (!adapter) fail('No WebGPU adapter is available.');
const requiredLimits = {};
for (const key in adapter.limits) requiredLimits[key] = adapter.limits[key];
const device = await adapter.requestDevice({
    requiredFeatures: ['float32-filterable', 'rg11b10ufloat-renderable'].filter((f) => adapter.features.has(f)),
    requiredLimits,
});
device.lost.then((info) => console.error(`WebGPU device lost: ${info.message}`));
globalThis.zigoteGpu = { adapter, device };

const runtime = await dotnet
    .withModuleConfig({ canvas: document.getElementById('canvas') })
    .withApplicationArgumentsFromQuery()
    .create();
status.remove();
// runMain, not run: Main returns once the frame loop is registered, and the app lives on.
await runtime.runMain();
