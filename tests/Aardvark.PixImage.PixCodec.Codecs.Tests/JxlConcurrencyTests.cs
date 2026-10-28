using Aardvark.ImageCodecs;
using Aardvark.ImageCodecs.JpegXl;
using Xunit;
using static Aardvark.ImageCodecs.Tests.JxlEncodeTests;

namespace Aardvark.ImageCodecs.Tests;

/// <summary>
/// JPEG XL decodes on many threads under constant garbage collection. Runs alone: its collections stall every other
/// test, and timed ones fail.
/// </summary>
[Collection("Measurements")]
public class JxlConcurrencyTests
{
    [Fact]
    public void ConcurrentDecodesUnderGcAreExact()
    {
        // groups share the lazily built ANS fast tables; a race once left a reader on tables a GC then freed
        var img = Synthetic(300, 170, ColorModel.GrayAlpha, SampleType.UInt16, 0, seed: 5);
        var enc = Jxl.Encode(img, new JxlEncodeOptions { Lossless = true, Effort = 5 });
        var want = (ushort[])img.Data;
        int failures = 0, decodes = 0;
        string? first = null;
        bool stop = false;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var gc = new System.Threading.Thread(() =>
        {
            var keep = new List<byte[]>();
            while (!System.Threading.Volatile.Read(ref stop))
            {
                for (int k = 0; k < 64; k++) keep.Add(GC.AllocateArray<byte>(8192 + k * 64, pinned: true));
                if (keep.Count > 512) keep.Clear();
                GC.Collect();
            }
        });
        gc.Start();
        var ts = new System.Threading.Thread[8];
        for (int t = 0; t < ts.Length; t++)
        {
            ts[t] = new System.Threading.Thread(() =>
            {
                while (clock.ElapsedMilliseconds < 4000)
                {
                    System.Threading.Interlocked.Increment(ref decodes);
                    try
                    {
                        if (((ushort[])Jxl.Decode(enc).Data).AsSpan().SequenceEqual(want)) continue;
                        System.Threading.Interlocked.CompareExchange(ref first, "wrong pixels", null);
                    }
                    catch (Exception e) { System.Threading.Interlocked.CompareExchange(ref first, e.Message, null); }
                    System.Threading.Interlocked.Increment(ref failures);
                }
            });
            ts[t].Start();
        }
        foreach (var t in ts) t.Join();
        System.Threading.Volatile.Write(ref stop, true);
        gc.Join();
        Assert.True(failures == 0, $"{failures} of {decodes} decodes failed, first: {first}");
    }
}
