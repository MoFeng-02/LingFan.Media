#version 450

layout(location = 0) in vec2 inUV;
layout(location = 0) out vec4 outColor;

layout(push_constant) uniform PushConstants {
    vec4 srcCrop;
    int format;
    int isBgra;
    int flipY;
    int pad;
} pc;

layout(binding = 0) uniform sampler2D texY; // Y plane or RGBA/BGRA direct texture
layout(binding = 1) uniform sampler2D texU; // U plane (planar) or UV plane (NV12/NV21)
layout(binding = 2) uniform sampler2D texV; // V plane (planar); unused for NV12/NV21/direct

// BT.601 full range (JFIF) matrix -- matches D3D11 shader path exactly.
vec3 YuvToRgb(float y, float u, float v)
{
    float d = u - 0.5019608; // 128/255
    float e = v - 0.5019608;
    return clamp(vec3(
        y + 1.402 * e,
        y - 0.344136 * d - 0.714136 * e,
        y + 1.772 * d), 0.0, 1.0);
}

void main()
{
    // Vertex shader emits per-vertex uv in {0,2}; barycentric interpolation
    // across the fullscreen triangle yields inUV in [0,1] for the visible region.
    vec2 uv = inUV;
    if (pc.flipY != 0)
        uv.y = 1.0 - uv.y;

    if (pc.format == 0)
    {
        // Direct RGBA/BGRA sampling path.
        vec4 s = texture(texY, uv);
        outColor = vec4(s.rgb, 1.0);
        if (pc.isBgra != 0)
            outColor.rgb = outColor.bgr;
    }
    else if (pc.format == 1)
    {
        // Planar YUV (YUV420P / YUV422P / YUV444P).
        float y = texture(texY, uv).r;
        float u = texture(texU, uv).r;
        float v = texture(texV, uv).r;
        vec3 rgb = YuvToRgb(y, u, v);
        outColor = vec4(pc.isBgra != 0 ? rgb.bgr : rgb, 1.0);
    }
    else if (pc.format == 2)
    {
        // NV12: RG = (U, V)
        float y = texture(texY, uv).r;
        vec2 c = texture(texU, uv).rg;
        vec3 rgb = YuvToRgb(y, c.x, c.y);
        outColor = vec4(pc.isBgra != 0 ? rgb.bgr : rgb, 1.0);
    }
    else
    {
        // NV21: RG = (V, U)
        float y = texture(texY, uv).r;
        vec2 c = texture(texU, uv).rg;
        vec3 rgb = YuvToRgb(y, c.y, c.x);
        outColor = vec4(pc.isBgra != 0 ? rgb.bgr : rgb, 1.0);
    }
}
