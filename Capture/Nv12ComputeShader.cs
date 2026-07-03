namespace Recorder.Capture;

internal static class Nv12ComputeShader
{
    public const string Source = @"
Texture2D<float4> SourceTexture : register(t0);
RWByteAddressBuffer Nv12Output : register(u0);

cbuffer Nv12Constants : register(b0)
{
    uint SourceWidth;
    uint SourceHeight;
    uint OutputWidth;
    uint OutputHeight;
};

float3 LoadRgb(uint2 p)
{
    if (p.x >= SourceWidth || p.y >= SourceHeight)
        return float3(0.0, 0.0, 0.0);

    float4 c = SourceTexture.Load(int3(p, 0));
    return c.rgb;
}

uint PackByte(uint value, uint byteIndex)
{
    return (value & 0xffu) << (byteIndex * 8u);
}

uint Pack4(uint b0, uint b1, uint b2, uint b3)
{
    return PackByte(b0, 0u) | PackByte(b1, 1u) | PackByte(b2, 2u) | PackByte(b3, 3u);
}

uint ToY(float3 rgb)
{
    float y = 16.0 + 219.0 * dot(rgb, float3(0.2126, 0.7152, 0.0722));
    return (uint)clamp(round(y), 16.0, 235.0);
}

uint ToU(float3 rgb)
{
    float u = 128.0 + 224.0 * dot(rgb, float3(-0.114572, -0.385428, 0.500000));
    return (uint)clamp(round(u), 16.0, 240.0);
}

uint ToV(float3 rgb)
{
    float v = 128.0 + 224.0 * dot(rgb, float3(0.500000, -0.454153, -0.045847));
    return (uint)clamp(round(v), 16.0, 240.0);
}

[numthreads(16, 16, 1)]
void CSMain(uint3 dispatchThreadId : SV_DispatchThreadID)
{
    uint blockX = dispatchThreadId.x * 4u;
    uint blockY = dispatchThreadId.y * 2u;
    if (blockX >= OutputWidth || blockY >= OutputHeight)
        return;

    float3 c00 = LoadRgb(uint2(blockX + 0u, blockY + 0u));
    float3 c10 = LoadRgb(uint2(blockX + 1u, blockY + 0u));
    float3 c20 = LoadRgb(uint2(blockX + 2u, blockY + 0u));
    float3 c30 = LoadRgb(uint2(blockX + 3u, blockY + 0u));
    float3 c01 = LoadRgb(uint2(blockX + 0u, blockY + 1u));
    float3 c11 = LoadRgb(uint2(blockX + 1u, blockY + 1u));
    float3 c21 = LoadRgb(uint2(blockX + 2u, blockY + 1u));
    float3 c31 = LoadRgb(uint2(blockX + 3u, blockY + 1u));

    uint yBase0 = blockY * OutputWidth + blockX;
    uint yBase1 = (blockY + 1u) * OutputWidth + blockX;
    Nv12Output.Store(yBase0, Pack4(ToY(c00), ToY(c10), ToY(c20), ToY(c30)));
    Nv12Output.Store(yBase1, Pack4(ToY(c01), ToY(c11), ToY(c21), ToY(c31)));

    uint uvBase = OutputWidth * OutputHeight + (blockY / 2u) * OutputWidth + blockX;
    float3 avg0 = (c00 + c10 + c01 + c11) * 0.25;
    float3 avg1 = (c20 + c30 + c21 + c31) * 0.25;
    Nv12Output.Store(uvBase, Pack4(ToU(avg0), ToV(avg0), ToU(avg1), ToV(avg1)));
}
";
}
