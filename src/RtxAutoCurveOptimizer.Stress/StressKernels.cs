using ComputeSharp;

namespace RtxAutoCurveOptimizer.Stress;

/// <summary>Deterministic per-thread chain of float FMAs, integer hashing and groupshared exchange.</summary>
[ThreadGroupSize(256, 1, 1)]
[GeneratedComputeShaderDescriptor]
internal readonly partial struct StressKernel(ReadWriteBuffer<uint> output, uint seed, int iterations) : IComputeShader
{
    [GroupShared(256)]
    private static uint[] exchange = null!;

    public void Execute()
    {
        int tid = ThreadIds.X;
        int lane = GroupIds.X;

        uint h = (uint)tid * 0x9E3779B1u + seed;
        h ^= h >> 15;
        h *= 0x85EBCA6Bu;
        h ^= h >> 13;
        h *= 0xC2B2AE35u;
        h ^= h >> 16;

        float a = Hlsl.AsFloat((h & 0x007FFFFFu) | 0x3F800000u) - 1.5f;
        float b = Hlsl.AsFloat(((h >> 5) & 0x007FFFFFu) | 0x3F800000u) - 1.5f;
        float c = Hlsl.AsFloat(((h >> 9) & 0x007FFFFFu) | 0x3F800000u) - 1.5f;
        float d = Hlsl.AsFloat(((h >> 3) & 0x007FFFFFu) | 0x3F800000u) - 1.5f;

        for (int i = 0; i < iterations; i++)
        {
            // Two rounds of coupled contractions (coefficients sum to 0.95): bounded, no NaN/Inf.
            float k0 = Hlsl.AsFloat((h & 0x007FFFFFu) | 0x3F800000u) - 1.5f;
            float k1 = Hlsl.AsFloat(((h >> 3) & 0x007FFFFFu) | 0x3F800000u) - 1.5f;
            float k2 = Hlsl.AsFloat(((h >> 7) & 0x007FFFFFu) | 0x3F800000u) - 1.5f;
            float k3 = Hlsl.AsFloat(((h >> 9) & 0x007FFFFFu) | 0x3F800000u) - 1.5f;

            a = a * 0.70f + b * 0.25f + k0;
            b = b * 0.70f + c * 0.25f + k1;
            c = c * 0.70f + d * 0.25f + k2;
            d = d * 0.70f + a * 0.25f + k3;

            a = a * 0.70f + c * 0.25f + k1;
            b = b * 0.70f + d * 0.25f + k2;
            c = c * 0.70f + a * 0.25f + k3;
            d = d * 0.70f + b * 0.25f + k0;

            h ^= Hlsl.AsUInt(a) + Hlsl.AsUInt(d);
            h = h * 1664525u + 1013904223u;
            h ^= h << 13;
            h ^= h >> 17;
            h ^= h << 5;

            if ((i & 7) == 7)
            {
                exchange[lane] = h;
                Hlsl.GroupMemoryBarrierWithGroupSync();
                h ^= exchange[(lane + 1 + ((i >> 3) & 15)) & 255];
                Hlsl.GroupMemoryBarrierWithGroupSync();
            }
        }

        output[tid] = h ^ Hlsl.AsUInt(a) ^ (Hlsl.AsUInt(b) * 0x85EBCA6Bu) ^ (Hlsl.AsUInt(c) * 0xC2B2AE35u) ^ (Hlsl.AsUInt(d) * 0x27D4EB2Fu);
    }
}

/// <summary>Compares kernel output with the golden buffer; counter[0] = mismatches, counter[1] = last mismatching index + 1.</summary>
[ThreadGroupSize(256, 1, 1)]
[GeneratedComputeShaderDescriptor]
internal readonly partial struct VerifyKernel(ReadWriteBuffer<uint> output, ReadWriteBuffer<uint> golden, ReadWriteBuffer<int> counter) : IComputeShader
{
    public void Execute()
    {
        int tid = ThreadIds.X;
        if (output[tid] != golden[tid])
        {
            Hlsl.InterlockedAdd(ref counter[0], 1);
            Hlsl.InterlockedMax(ref counter[1], tid + 1);
        }
    }
}
