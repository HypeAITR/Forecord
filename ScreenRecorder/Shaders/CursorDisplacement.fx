// Reference HLSL source for the cursor reactive surface.
// Compiled at runtime via SharpDX.D3DCompiler; this file is for editing only.

sampler2D input : register(s0);

float2 center   : register(c0);
float  strength : register(c1);
float  radius   : register(c2);

float4 main(float2 uv : TEXCOORD) : COLOR
{
    float2 delta = uv - center;
    float dist = length(delta);

    float n = saturate(dist / radius);
    float fall = 1.0 - n;
    fall = fall * fall;

    float h1 = frac(sin(dot(uv, float2(91.345, 47.853))) * 4382.31);
    float h2 = frac(sin(dot(uv, float2(33.971, 76.123))) * 7211.77);

    float2 dir  = delta / max(dist, 0.00001);
    float2 tang = float2(-dir.y, dir.x);

    float2 offset = (dir * (0.55 + h1 * 0.35) + tang * (h2 - 0.5) * 0.55) * fall * strength;

    float4 color = tex2D(input, uv + offset);

    float lift = fall * strength * 5.5;
    color.rgb += float3(0.86, 0.88, 0.92) * lift;

    return color;
}
