using System.Buffers.Binary;
using System.Text;
using Xunit;

namespace Aardvark.ImageCodecs.Tests;

/// <summary>
/// Black point compensation (ISO 18619:2015, ISO/TS 21830:2018) on synthetic v2 profiles whose black points follow by
/// hand from the spec procedure (lut16 tables with a Lab PCS; device values and the legacy Lab encoding normalised to 0..1).
/// </summary>
public class IccBpcTests
{
    // ------------------------------------------------------------------ synthetic profiles

    /// <summary>Normalised legacy (lut16) Lab encoding: L* 100 = 0xFF00, a* 0 = 0x8000.</summary>
    static double LN(double l) => l / 100 * 65280 / 65535;
    static double LOf(double n) => n * 100 * 65535 / 65280;
    static readonly double Ab0 = 32768 / 65535.0;

    /// <summary>A lut16 tag: identity matrix, the input curves given (sampled at 4096 points, default identity), a CLUT of
    /// <paramref name="grid"/> points per input evaluated by <paramref name="node"/> (normalised inputs to outputs), identity output curves.</summary>
    static byte[] Lut16(int inCh, int outCh, int grid, Func<double[], double[]> node, Func<double, double>[]? inCurves = null)
    {
        const int n = 4096;
        var b = new List<byte>();
        void U16(double v) { ushort u = (ushort)Math.Round(Math.Clamp(v, 0, 1) * 65535); b.Add((byte)(u >> 8)); b.Add((byte)u); }
        void U32(uint v) { b.Add((byte)(v >> 24)); b.Add((byte)(v >> 16)); b.Add((byte)(v >> 8)); b.Add((byte)v); }
        b.AddRange("mft2"u8.ToArray()); U32(0);
        b.Add((byte)inCh); b.Add((byte)outCh); b.Add((byte)grid); b.Add(0);
        for (int i = 0; i < 9; i++) U32(i % 4 == 0 ? 0x10000u : 0);
        b.Add(n >> 8); b.Add(n & 255); b.Add(0); b.Add(2);
        for (int c = 0; c < inCh; c++)
            for (int i = 0; i < n; i++) U16(inCurves?[c] is { } f ? f(i / (n - 1.0)) : i / (n - 1.0));
        int total = 1;
        for (int c = 0; c < inCh; c++) total *= grid;
        var x = new double[inCh];
        for (int i = 0; i < total; i++)
        {
            // the first input varies slowest
            for (int c = inCh - 1, q = i; c >= 0; c--, q /= grid) x[c] = q % grid / (grid - 1.0);
            foreach (var v in node(x)) U16(v);
        }
        for (int c = 0; c < outCh; c++) { U16(0); U16(1); }
        return b.ToArray();
    }

    /// <summary>A v2 profile with a Lab PCS, a 'desc' and a D50 'wtpt' tag and the tags given.</summary>
    static IccProfile Profile(IccProfileClass cls, IccColorSpace cs, params (string Sig, byte[] Data)[] tags)
    {
        var desc = new List<byte>("desc"u8.ToArray()) { 0, 0, 0, 0, 0, 0, 0, 5 };
        desc.AddRange("test\0"u8.ToArray());
        desc.AddRange(new byte[4 + 4 + 2 + 1 + 67]);
        var wtpt = new byte[20];
        "XYZ "u8.CopyTo(wtpt);
        BinaryPrimitives.WriteInt32BigEndian(wtpt.AsSpan(8), (int)Math.Round(0.9642 * 65536));
        BinaryPrimitives.WriteInt32BigEndian(wtpt.AsSpan(12), 65536);
        BinaryPrimitives.WriteInt32BigEndian(wtpt.AsSpan(16), (int)Math.Round(0.8249 * 65536));
        var all = new List<(string Sig, byte[] Data)> { ("desc", desc.ToArray()), ("wtpt", wtpt) };
        all.AddRange(tags);
        int off = 132 + 12 * all.Count;
        var body = new List<byte>();
        var dir = new List<(string, int, int)>();
        foreach (var (sig, data) in all)
        {
            while ((off + body.Count) % 4 != 0) body.Add(0);
            dir.Add((sig, off + body.Count, data.Length));
            body.AddRange(data);
        }
        var p = new byte[off + body.Count];
        BinaryPrimitives.WriteUInt32BigEndian(p, (uint)p.Length);
        p[8] = 2; p[9] = 0x10;
        BinaryPrimitives.WriteUInt32BigEndian(p.AsSpan(12), (uint)cls);
        BinaryPrimitives.WriteUInt32BigEndian(p.AsSpan(16), (uint)cs);
        BinaryPrimitives.WriteUInt32BigEndian(p.AsSpan(20), (uint)IccColorSpace.Lab);
        "acsp"u8.CopyTo(p.AsSpan(36));
        BinaryPrimitives.WriteInt32BigEndian(p.AsSpan(68), (int)Math.Round(0.9642 * 65536));
        BinaryPrimitives.WriteInt32BigEndian(p.AsSpan(72), 65536);
        BinaryPrimitives.WriteInt32BigEndian(p.AsSpan(76), (int)Math.Round(0.8249 * 65536));
        BinaryPrimitives.WriteUInt32BigEndian(p.AsSpan(128), (uint)all.Count);
        for (int i = 0; i < dir.Count; i++)
        {
            var (sig, o, len) = dir[i];
            Encoding.ASCII.GetBytes(sig).CopyTo(p, 132 + 12 * i);
            BinaryPrimitives.WriteUInt32BigEndian(p.AsSpan(136 + 12 * i), (uint)o);
            BinaryPrimitives.WriteUInt32BigEndian(p.AsSpan(140 + 12 * i), (uint)len);
        }
        body.CopyTo(p, off);
        return IccProfile.Parse(p);
    }

    /// <summary>A Lab node of an A2B table from L* (a* = b* = 0).</summary>
    static double[] Gray(double l) => new[] { LN(l), Ab0, Ab0 };

    static double BlackL(IccProfile p, IccRenderingIntent intent, bool asSource)
    {
        var b = IccTransform.BlackPoint(p, intent, asSource, false);
        return IccMath.XyzToLab(b.X, b.Y, b.Z).L;
    }

    static void Near(double expected, double actual, double tol = 0.02)
        => Assert.True(Math.Abs(actual - expected) <= tol, $"L* {actual:F3}, expected {expected:F3}");

    static readonly IccRenderingIntent[] Intents =
        { IccRenderingIntent.Perceptual, IccRenderingIntent.RelativeColorimetric, IccRenderingIntent.Saturation };

    // ------------------------------------------------------------------ source black points

    [Fact]
    public void LabSourceBlackIsNeutral()
    {
        // ISO 18619 4.2.3: a Lab data colour space starts from Lab (0, 0, 0), i.e. a* = b* = 0 (0x8000 in lut16), not the
        // normalised zeros (a* = b* = -128). The A2B here darkens with falling a*: L*out = L*in + 0.2 (1 - a) normalised.
        var p = Profile(IccProfileClass.ColorSpace, IccColorSpace.Lab,
            ("A2B0", Lut16(3, 3, 2, x => new[] { x[0] + 0.2 * (1 - x[1]), Ab0, Ab0 })));
        double expected = LOf(0.2 * (1 - Ab0)); // 10.04 (20.08 from a* = -128)
        foreach (var intent in Intents) Near(expected, BlackL(p, intent, true));
    }
}
