using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;

namespace Rinha.Api;

public static class SimdDistance
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static unsafe float EuclideanSquaredPtr(float* a, float* b)
    {
        if (Fma.IsSupported)
            return FmaDistanceSquared(a, b);
        if (Avx.IsSupported)
            return AvxDistanceSquared(a, b);
        if (AdvSimd.Arm64.IsSupported)
            return NeonDistanceSquared(a, b);
        if (Sse.IsSupported)
            return SseDistanceSquared(a, b);
        return ScalarDistanceSquared(a, b);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe float NeonDistanceSquared(float* a, float* b)
    {
        var acc = Vector128<float>.Zero;
        for (int i = 0; i < 16; i += 4)
        {
            var va = AdvSimd.LoadVector128(a + i);
            var vb = AdvSimd.LoadVector128(b + i);
            var d = AdvSimd.Subtract(va, vb);
            acc = AdvSimd.Add(acc, AdvSimd.Multiply(d, d));
        }
        return acc.GetElement(0) + acc.GetElement(1) + acc.GetElement(2) + acc.GetElement(3);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe float FmaDistanceSquared(float* a, float* b)
    {
        var d0 = Avx.Subtract(Avx.LoadVector256(a), Avx.LoadVector256(b));
        var d1 = Avx.Subtract(Avx.LoadVector256(a + 8), Avx.LoadVector256(b + 8));
        var acc = Fma.MultiplyAdd(d0, d0, Vector256<float>.Zero);
        acc = Fma.MultiplyAdd(d1, d1, acc);
        var hi128 = Avx.ExtractVector128(acc, 1);
        var lo128 = acc.GetLower();
        var r = Sse.Add(hi128, lo128);
        r = Sse.Add(r, Sse.MoveHighToLow(r, r));
        r = Sse.AddScalar(r, Sse.Shuffle(r, r, 1));
        return r.GetElement(0);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe float AvxDistanceSquared(float* a, float* b)
    {
        var d0 = Avx.Subtract(Avx.LoadVector256(a), Avx.LoadVector256(b));
        var d1 = Avx.Subtract(Avx.LoadVector256(a + 8), Avx.LoadVector256(b + 8));
        d0 = Avx.Multiply(d0, d0);
        d1 = Avx.Multiply(d1, d1);
        var sum = Avx.Add(d0, d1);
        var hi128 = Avx.ExtractVector128(sum, 1);
        var lo128 = sum.GetLower();
        var r = Sse.Add(hi128, lo128);
        r = Sse.Add(r, Sse.MoveHighToLow(r, r));
        r = Sse.AddScalar(r, Sse.Shuffle(r, r, 1));
        return r.GetElement(0);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe float SseDistanceSquared(float* a, float* b)
    {
        var acc = Vector128<float>.Zero;
        for (int i = 0; i < 16; i += 4)
        {
            var d = Sse.Subtract(Sse.LoadVector128(a + i), Sse.LoadVector128(b + i));
            acc = Sse.Add(acc, Sse.Multiply(d, d));
        }
        acc = Sse.Add(acc, Sse.MoveHighToLow(acc, acc));
        acc = Sse.AddScalar(acc, Sse.Shuffle(acc, acc, 1));
        return acc.GetElement(0);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe float ScalarDistanceSquared(float* a, float* b)
    {
        float sum = 0f;
        for (int i = 0; i < 16; i++)
        {
            float d = a[i] - b[i];
            sum += d * d;
        }
        return sum;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static unsafe int ByteSAD(byte* a, byte* b)
    {
        if (Sse2.IsSupported)
        {
            var va = Sse2.LoadVector128(a);
            var vb = Sse2.LoadVector128(b);
            var sad = Sse2.SumAbsoluteDifferences(va, vb);
            return (int)(sad.GetElement(0) + sad.GetElement(4));
        }
        if (AdvSimd.Arm64.IsSupported)
        {
            var va = AdvSimd.LoadVector128(a);
            var vb = AdvSimd.LoadVector128(b);
            var abd = AdvSimd.AbsoluteDifference(va, vb);
            var wide = AdvSimd.AddPairwiseWidening(abd);
            return AdvSimd.Arm64.AddAcross(wide).ToScalar();
        }
        int sum = 0;
        for (int i = 0; i < 16; i++)
        {
            int d = a[i] - b[i];
            sum += d < 0 ? -d : d;
        }
        return sum;
    }

    /// <summary>
    /// Full int16 L2² over 16 dimensions. ~1 AVX2 madd on x86 / 2 NEON wide-mul on ARM.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static unsafe int Int16L2Squared(short* a, short* b)
    {
        if (Avx2.IsSupported)
        {
            var diff = Avx2.Subtract(
                Avx2.LoadVector256(a).AsInt16(),
                Avx2.LoadVector256(b).AsInt16());
            var madd = Avx2.MultiplyAddAdjacent(diff, diff);
            var hi128 = Avx2.ExtractVector128(madd, 1);
            var lo128 = madd.GetLower();
            var s = Sse2.Add(hi128, lo128);
            s = Sse2.Add(s, Sse2.Shuffle(s, 0x4E));
            s = Sse2.Add(s, Sse2.Shuffle(s, 0xB1));
            return s.GetElement(0);
        }
        if (AdvSimd.Arm64.IsSupported)
        {
            var d0 = AdvSimd.Subtract(AdvSimd.LoadVector128(a), AdvSimd.LoadVector128(b));
            var d1 = AdvSimd.Subtract(AdvSimd.LoadVector128(a + 8), AdvSimd.LoadVector128(b + 8));
            var sq0Lo = AdvSimd.MultiplyWideningLower(d0.GetLower(), d0.GetLower());
            var sq0Hi = AdvSimd.MultiplyWideningUpper(d0, d0);
            var sq1Lo = AdvSimd.MultiplyWideningLower(d1.GetLower(), d1.GetLower());
            var sq1Hi = AdvSimd.MultiplyWideningUpper(d1, d1);
            var acc = AdvSimd.Add(AdvSimd.Add(sq0Lo, sq0Hi), AdvSimd.Add(sq1Lo, sq1Hi));
            return AdvSimd.Arm64.AddAcross(acc).ToScalar();
        }
        if (Sse2.IsSupported)
        {
            var dLo = Sse2.Subtract(
                Sse2.LoadVector128(a).AsInt16(),
                Sse2.LoadVector128(b).AsInt16());
            var dHi = Sse2.Subtract(
                Sse2.LoadVector128(a + 8).AsInt16(),
                Sse2.LoadVector128(b + 8).AsInt16());
            var s = Sse2.Add(
                Sse2.MultiplyAddAdjacent(dLo, dLo),
                Sse2.MultiplyAddAdjacent(dHi, dHi));
            s = Sse2.Add(s, Sse2.Shuffle(s, 0x4E));
            s = Sse2.Add(s, Sse2.Shuffle(s, 0xB1));
            return s.GetElement(0);
        }
        int total = 0;
        for (int i = 0; i < 16; i++)
        {
            int d = a[i] - b[i];
            total += d * d;
        }
        return total;
    }

    /// <summary>
    /// Partial int16 L2² over the FIRST 8 dimensions. Used for early-exit pre-filter:
    /// if partial distance already exceeds the worst K-NN distance, the full 16-dim
    /// distance is guaranteed to exceed it too (sum of squares is monotonic), so we
    /// can skip the remaining 8 dims.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static unsafe int Int16L2SquaredFirst8(short* a, short* b)
    {
        if (Sse2.IsSupported)
        {
            var diff = Sse2.Subtract(
                Sse2.LoadVector128(a).AsInt16(),
                Sse2.LoadVector128(b).AsInt16());
            var madd = Sse2.MultiplyAddAdjacent(diff, diff);
            madd = Sse2.Add(madd, Sse2.Shuffle(madd, 0x4E));
            madd = Sse2.Add(madd, Sse2.Shuffle(madd, 0xB1));
            return madd.GetElement(0);
        }
        if (AdvSimd.Arm64.IsSupported)
        {
            var d = AdvSimd.Subtract(AdvSimd.LoadVector128(a), AdvSimd.LoadVector128(b));
            var sqLo = AdvSimd.MultiplyWideningLower(d.GetLower(), d.GetLower());
            var sqHi = AdvSimd.MultiplyWideningUpper(d, d);
            var acc = AdvSimd.Add(sqLo, sqHi);
            return AdvSimd.Arm64.AddAcross(acc).ToScalar();
        }
        int total = 0;
        for (int i = 0; i < 8; i++)
        {
            int d = a[i] - b[i];
            total += d * d;
        }
        return total;
    }

    /// <summary>
    /// Partial int16 L2² over the LAST 8 dimensions (offsets 8..15). Used to complete
    /// the early-exit pre-filter: full_distance = First8 + Last8.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static unsafe int Int16L2SquaredLast8(short* a, short* b)
        => Int16L2SquaredFirst8(a + 8, b + 8);

    /// <summary>
    /// Int16 bbox lower bound — sum of squared per-dim gaps to bbox [min, max].
    /// Returns 0 if query is inside the bbox.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static unsafe int Int16BboxLowerBound(short* query, short* bboxMin, short* bboxMax)
    {
        if (Avx2.IsSupported)
        {
            var q = Avx2.LoadVector256(query);
            var lo = Avx2.Max(Avx2.Subtract(Avx2.LoadVector256(bboxMin), q), Vector256<short>.Zero);
            var hi = Avx2.Max(Avx2.Subtract(q, Avx2.LoadVector256(bboxMax)), Vector256<short>.Zero);
            var gap = Avx2.Add(lo, hi);

            var madd = Avx2.MultiplyAddAdjacent(gap, gap);
            var h128 = Avx2.ExtractVector128(madd, 1);
            var l128 = madd.GetLower();
            var s = Sse2.Add(h128, l128);
            s = Sse2.Add(s, Sse2.Shuffle(s, 0x4E));
            s = Sse2.Add(s, Sse2.Shuffle(s, 0xB1));
            return s.GetElement(0);
        }
        if (AdvSimd.Arm64.IsSupported)
        {
            var q0 = AdvSimd.LoadVector128(query);
            var q1 = AdvSimd.LoadVector128(query + 8);
            var lo0 = AdvSimd.Max(AdvSimd.Subtract(AdvSimd.LoadVector128(bboxMin), q0), Vector128<short>.Zero);
            var hi0 = AdvSimd.Max(AdvSimd.Subtract(q0, AdvSimd.LoadVector128(bboxMax)), Vector128<short>.Zero);
            var g0 = AdvSimd.Add(lo0, hi0);
            var lo1 = AdvSimd.Max(AdvSimd.Subtract(AdvSimd.LoadVector128(bboxMin + 8), q1), Vector128<short>.Zero);
            var hi1 = AdvSimd.Max(AdvSimd.Subtract(q1, AdvSimd.LoadVector128(bboxMax + 8)), Vector128<short>.Zero);
            var g1 = AdvSimd.Add(lo1, hi1);

            var sq0Lo = AdvSimd.MultiplyWideningLower(g0.GetLower(), g0.GetLower());
            var sq0Hi = AdvSimd.MultiplyWideningUpper(g0, g0);
            var sq1Lo = AdvSimd.MultiplyWideningLower(g1.GetLower(), g1.GetLower());
            var sq1Hi = AdvSimd.MultiplyWideningUpper(g1, g1);
            var acc = AdvSimd.Add(AdvSimd.Add(sq0Lo, sq0Hi), AdvSimd.Add(sq1Lo, sq1Hi));
            return AdvSimd.Arm64.AddAcross(acc).ToScalar();
        }

        int lb = 0;
        for (int d = 0; d < 16; d++)
        {
            int gap = 0;
            if (query[d] < bboxMin[d]) gap = bboxMin[d] - query[d];
            else if (query[d] > bboxMax[d]) gap = query[d] - bboxMax[d];
            lb += gap * gap;
        }
        return lb;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static unsafe void Prefetch(void* ptr)
    {
        if (Sse.IsSupported)
            Sse.Prefetch0(ptr);
    }
}
