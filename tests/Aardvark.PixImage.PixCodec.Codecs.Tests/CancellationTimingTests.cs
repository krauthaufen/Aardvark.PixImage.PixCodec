using System.Threading;
using Xunit;

namespace Aardvark.ImageCodecs.Tests;

/// <summary>A cancelled WebP encode or decode stops soon after the cancel, not at the end of a long stretch of work.</summary>
[Collection("Measurements")]
public class CancellationTimingTests
{
    // the colour conversion before the first macroblock row is about a fifth of a lossy encode
    [Fact]
    public void WebpLossyEncodeCancelsBeforeTheMacroblocks()
    {
        var img = DecodeOptionsTests.Photo(2048, 2048);
        AssertCancelsEarly(ct => Webp.Encode(img, new WebpEncodeOptions { Quality = 88, CancellationToken = ct }), 40, 10);
    }

    // large enough that the decode takes many scheduler time slices
    [Fact]
    public void WebpLosslessDecodeCancelsWhileRunning()
    {
        var data = Webp.Encode(DecodeOptionsTests.Photo(4096, 4096), new WebpEncodeOptions { Lossless = true, Method = 0 });
        AssertCancelsEarly(ct => Webp.Decode(data, new WebpDecodeOptions { CancellationToken = ct }), 10, 2);
    }

    [Fact]
    public void WebpLosslessEncodeCancelsWhileRunning()
    {
        var img = DecodeOptionsTests.Photo(1024, 1024);
        AssertCancelsEarly(ct => Webp.Encode(img, new WebpEncodeOptions { Lossless = true, CancellationToken = ct }), 10, 2);
    }

    /// <summary>
    /// Cancelled at 1 / <paramref name="cancelAt"/> of its uncancelled time T (the best of two runs just before), the
    /// work throws OperationCanceledException before T / <paramref name="endBy"/>: checks only before and after a long
    /// stretch of work let it run on to the end of that stretch. A busy machine can delay a single run, so there are up
    /// to three attempts; a missing check makes every attempt late.
    /// </summary>
    static void AssertCancelsEarly(Action<CancellationToken> work, int cancelAt, int endBy)
    {
        work(CancellationToken.None);
        var late = new List<string>();
        while (late.Count < 3)
        {
            double full = double.MaxValue;
            for (int i = 0; i < 2; i++)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                work(CancellationToken.None);
                full = Math.Min(full, sw.Elapsed.TotalMilliseconds);
            }
            using var cts = new CancellationTokenSource();
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var canceller = new Thread(() =>
            {
                while (clock.Elapsed.TotalMilliseconds < full / cancelAt) Thread.SpinWait(100);
                cts.Cancel();
            }) { IsBackground = true };
            canceller.Start();
            Assert.ThrowsAny<OperationCanceledException>(() => work(cts.Token));
            double ended = clock.Elapsed.TotalMilliseconds;
            canceller.Join();
            if (ended < full / endBy) return;
            late.Add($"cancelled at {full / cancelAt:F1} ms, ended at {ended:F1} ms, uncancelled {full:F1} ms");
        }
        Assert.Fail(string.Join("; ", late));
    }
}
