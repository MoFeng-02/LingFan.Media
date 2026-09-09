#version 450

// Single-sample YCbCr path for Android hardware buffers imported with an
// external format (VK_ANDROID_external_memory_android_hardware_buffer).
// The sampler attached to binding 0 carries a VkSamplerYcbcrConversion, so a
// plain texture() fetch returns fully converted RGB -- no manual YUV math.
// The image view must reference the same conversion object.

layout(location = 0) in vec2 inUV;
layout(location = 0) out vec4 outColor;

layout(binding = 0) uniform sampler2D texYcbcr;

void main()
{
    // Vertex shader emits per-vertex uv in {0,2}; barycentric interpolation
    // across the fullscreen triangle yields inUV in [0,1] for the visible
    // region. The vertical flip matches the software-frame and NV12 converter
    // paths (clip-space top-left maps to texture bottom-left after flip).
    vec2 uv = vec2(inUV.x, 1.0 - inUV.y);
    outColor = vec4(texture(texYcbcr, uv).rgb, 1.0);
}
