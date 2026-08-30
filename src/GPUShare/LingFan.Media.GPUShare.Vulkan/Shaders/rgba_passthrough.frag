#version 450

// Plain RGBA passthrough for Android hardware buffers imported as a *non-external*
// format (VK_ANDROID_external_memory_android_hardware_buffer with a real VkFormat,
// e.g. R8G8B8A8Unorm produced by an ImageReader requesting HardwareBufferFormat.Rgba8888).
// No VkSamplerYcbcrConversion is attached: the bound sampler is a plain sampler2D
// and texture() returns the stored RGBA directly. This path exists to sidestep
// Adreno's null-deref when sampling an *external-format* (YUV) AHB with a YCbCr
// conversion -- we avoid the external format entirely by having the decoder emit RGBA.

layout(location = 0) in vec2 inUV;
layout(location = 0) out vec4 outColor;

layout(binding = 0) uniform sampler2D texRgba;

void main()
{
    // Match the YCbCr path's vertical flip so RGBA and YUV frames present identically.
    vec2 uv = vec2(inUV.x, 1.0 - inUV.y);
    outColor = texture(texRgba, uv);
}
