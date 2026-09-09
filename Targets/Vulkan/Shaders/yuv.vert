#version 450

layout(location = 0) out vec2 outUV;

layout(push_constant) uniform PushConstants {
    vec4 srcCrop; // xy = min uv, zw = max uv
    int format;
    int isBgra;
    int flipY;
    int pad;
} pc;

void main()
{
    // Fullscreen triangle from SV_VertexID, no vertex buffer.
    // id=0 -> (-1, 1), id=1 -> (3, 1), id=2 -> (-1, -3)
    // The resulting uv is in {0,2}; the fragment shader scales it to [0,1]
    // after applying the optional vertical flip.
    vec2 uv = vec2(float((gl_VertexIndex << 1) & 2), float(gl_VertexIndex & 2));
    gl_Position = vec4(uv * vec2(2.0, -2.0) + vec2(-1.0, 1.0), 0.0, 1.0);

    outUV = uv;
}
