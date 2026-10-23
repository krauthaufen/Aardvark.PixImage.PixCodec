using System.Buffers.Binary;
using System.Diagnostics;
using System.Text.Json;
using Xunit;

namespace Aardvark.ImageCodecs.Tests;

/// <summary>
/// Helpers for the volume format tests: PXVR sidecars (tests/oracle/pxref.py write_vref), JSON metadata sidecars, and the
/// python oracle with pynrrd / nibabel / SimpleITK / astropy (a venv in .tmp/venv of the repository, or $PIXCODEC_PYTHON).
/// </summary>
public static class VolumeTestUtil
{
    /// <summary>A PXVR sidecar, also gzip compressed (a path ending in .gz).</summary>
    public static RawVolume LoadVref(string path)
    {
        byte[] b;
        if (path.EndsWith(".gz", StringComparison.Ordinal))
        {
            using var gz = new System.IO.Compression.GZipStream(File.OpenRead(path), System.IO.Compression.CompressionMode.Decompress);
            using var ms = new MemoryStream();
            gz.CopyTo(ms);
            b = ms.ToArray();
        }
        else b = File.ReadAllBytes(path);
        Assert.True(b.AsSpan(0, 4).SequenceEqual("PXVR"u8), "bad vref magic");
        int n = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(4));
        var sizes = new int[n];
        for (int i = 0; i < n; i++) sizes[i] = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(8 + 4 * i));
        int c = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(8 + 4 * n));
        var t = (SampleType)BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(12 + 4 * n));
        long count = c;
        foreach (var s in sizes) count *= s;
        var data = RawImage.CreateArray(t, (int)count);
        var dst = new RawImage(1, (int)count, ColorModel.Gray, data).GetBytes();
        b.AsSpan(16 + 4 * n).CopyTo(dst);
        return new RawVolume(sizes, c <= 4 ? RawImage.DefaultColorModel(c) : ColorModel.Other, data, c);
    }

    public static double[] Doubles(Array a)
        => TestUtil.ToDoubles(new RawImage(1, (int)a.LongLength, ColorModel.Gray, a));

    /// <summary>Same sizes, channels, sample type and samples (NaN equals NaN; tolerance for float data).</summary>
    public static void AssertSame(RawVolume expected, RawVolume actual, double tol = 0)
    {
        Assert.Equal(expected.Sizes, actual.Sizes);
        Assert.Equal(expected.Channels, actual.Channels);
        Assert.Equal(expected.SampleType, actual.SampleType);
        if (expected.SampleType is SampleType.UInt64 or SampleType.Int64 || tol == 0 && VolumeIOIsInteger(expected.SampleType))
        {
            var e = new RawImage(1, (int)expected.Data.LongLength, ColorModel.Gray, expected.Data).GetBytes();
            var a = new RawImage(1, (int)actual.Data.LongLength, ColorModel.Gray, actual.Data).GetBytes();
            if (!e.SequenceEqual(a))
            {
                int i = 0;
                while (e[i] == a[i]) i++;
                Assert.Fail($"samples differ (first at byte {i})");
            }
            return;
        }
        var ed = Doubles(expected.Data); var ad = Doubles(actual.Data);
        for (int i = 0; i < ed.Length; i++)
        {
            if (double.IsNaN(ed[i])) { Assert.True(double.IsNaN(ad[i]), $"sample {i}: expected NaN, got {ad[i]}"); continue; }
            double d = Math.Abs(ed[i] - ad[i]);
            Assert.True(d <= tol * Math.Max(1, Math.Abs(ed[i])), $"sample {i}: expected {ed[i]}, got {ad[i]}");
        }
    }

    static bool VolumeIOIsInteger(SampleType t) => t is not (SampleType.Float16 or SampleType.Float32 or SampleType.Float64);

    static double D(JsonElement e) => e.ValueKind == JsonValueKind.Null ? double.NaN : e.GetDouble();

    /// <summary>Checks the metadata against the JSON sidecar written by gen_volume.py.</summary>
    public static void AssertMetadata(string jsonPath, RawVolume v)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(jsonPath));
        var j = doc.RootElement;
        var m = v.Metadata;
        Assert.NotNull(m);
        double tol = j.TryGetProperty("tol", out var tt) ? Math.Max(tt.GetDouble(), 1e-12) : 1e-6;
        if (j.TryGetProperty("space", out var sp)) Assert.Equal(sp.GetString(), m!.Space);
        if (j.TryGetProperty("spacing", out var spc) && spc.ValueKind == JsonValueKind.Array)
        {
            int i = 0;
            foreach (var s in spc.EnumerateArray())
            {
                double e = D(s);
                Assert.True(i < m!.Spacing.Length, "missing spacing");
                if (double.IsNaN(e)) Assert.True(double.IsNaN(m.Spacing[i]), $"spacing {i}: expected unknown, got {m.Spacing[i]}");
                else Assert.True(Math.Abs(e - m.Spacing[i]) <= tol * Math.Max(1, Math.Abs(e)), $"spacing {i}: expected {e}, got {m.Spacing[i]}");
                i++;
            }
        }
        if (j.TryGetProperty("origin", out var org) && org.ValueKind == JsonValueKind.Array)
        {
            Assert.NotNull(m!.Origin);
            int i = 0;
            foreach (var s in org.EnumerateArray())
            {
                Assert.True(Math.Abs(D(s) - m.Origin![i]) <= tol * Math.Max(1, Math.Abs(D(s))), $"origin {i}: expected {D(s)}, got {m.Origin[i]}");
                i++;
            }
        }
        if (j.TryGetProperty("direction", out var dir) && dir.ValueKind == JsonValueKind.Array)
        {
            Assert.NotNull(m!.Direction);
            int r = 0;
            foreach (var row in dir.EnumerateArray())
            {
                int c = 0;
                foreach (var x in row.EnumerateArray())
                {
                    Assert.True(Math.Abs(D(x) - m.Direction![r, c]) <= Math.Max(tol, 1e-6), $"direction [{r},{c}]: expected {D(x)}, got {m.Direction[r, c]}");
                    c++;
                }
                r++;
            }
        }
        if (j.TryGetProperty("units", out var un))
        {
            int i = 0;
            foreach (var u in un.EnumerateArray()) Assert.Equal(u.GetString(), m!.Units[i++]);
        }
        if (j.TryGetProperty("slope_value", out var sl)) Assert.Equal(sl.GetDouble(), m!.Slope, 6);
        if (j.TryGetProperty("intercept", out var ic)) Assert.Equal(ic.GetDouble(), m!.Intercept, 6);
        if (j.TryGetProperty("value_unit", out var vu)) Assert.Equal(vu.GetString(), m!.ValueUnit);
        if (j.TryGetProperty("blank", out var bl)) Assert.Equal(bl.GetDouble(), m!.Blank);
        if (j.TryGetProperty("color", out var col)) Assert.Equal(col.GetString(), v.Color.ToString());
        if (j.TryGetProperty("header", out var hd))
            foreach (var p in hd.EnumerateObject()) Assert.Equal(p.Value.GetString(), m!.Get(p.Name));
    }

    // ------------------------------------------------------------------ python oracle

    static string? FindPython()
    {
        var env = Environment.GetEnvironmentVariable("PIXCODEC_PYTHON");
        if (!string.IsNullOrEmpty(env) && File.Exists(env)) return env;
        var d = new DirectoryInfo(TestUtil.OracleDir);
        while (d != null)
        {
            foreach (var c in new[] { Path.Combine(d.FullName, ".tmp", "venv", "bin", "python"), Path.Combine(d.FullName, ".tmp", "venv", "Scripts", "python.exe") })
                if (File.Exists(c)) return c;
            d = d.Parent;
        }
        return TestUtil.PythonExe;
    }

    static readonly string? Python = FindPython();
    static readonly Dictionary<string, bool> Modules = new();

    /// <summary>Whether the oracle python has the module (pynrrd's is "nrrd", SimpleITK, nibabel, astropy).</summary>
    public static bool Has(string module)
    {
        bool ok = false;
        if (Python != null)
            lock (Modules)
            {
                if (!Modules.TryGetValue(module, out ok))
                    Modules[module] = ok = Run($"-c \"import {module}\"").exit == 0;
            }
        return Oracles.Have("python:" + module, ok, $"the python module {module} ($PIXCODEC_PYTHON, .tmp/venv or the python on the PATH)");
    }

    public static (int exit, string output) Run(string args)
    {
        var psi = new ProcessStartInfo(Python!, args)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, WorkingDirectory = TestUtil.OracleDir,
        };
        using var p = Process.Start(psi)!;
        var so = p.StandardOutput.ReadToEndAsync();
        var se = p.StandardError.ReadToEndAsync();
        p.WaitForExit();
        return (p.ExitCode, so.Result + se.Result);
    }

    /// <summary>
    /// Decodes <paramref name="path"/> with the given reference reader (tests/oracle/decode_volume.py: nrrd, nibabel, sitk,
    /// astropy) and returns the samples in our layout with the metadata JSON path, or null when it is not installed.
    /// </summary>
    public static (RawVolume vol, string json)? Oracle(string tool, string path)
    {
        string module = tool switch { "nrrd" => "nrrd", "nibabel" => "nibabel", "sitk" => "SimpleITK", "astropy" => "astropy", _ => tool };
        if (!Has(module)) return null;
        var outp = path + "." + tool + ".out";
        var (exit, output) = Run($"decode_volume.py {tool} \"{path}\" \"{outp}\"");
        Assert.True(exit == 0, $"{tool} could not read {path}: {output}");
        try { return (LoadVref(outp + ".vref"), outp + ".json"); }
        finally { File.Delete(outp + ".vref"); }
    }

    public static string TempDir()
    {
        var d = Path.Combine(TestUtil.FixtureDir, "_tmp", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }
}
