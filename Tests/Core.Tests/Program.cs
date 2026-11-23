using System;
using System.IO;
using System.IO.Compression;
using River.Core;

// Headless checks of the river generator. Exit code 0 = all pass.
string mapPath = null;
for (int a = 0; a < args.Length; a++) if (args[a] == "--map" && a + 1 < args.Length) mapPath = args[++a];

int failures = 0;
void Check(bool ok, string what) { Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {what}"); if (!ok) failures++; }

var settings = new ReachSettings();
var field = ReachGenerator.Generate(settings, Console.WriteLine);

Console.WriteLine("Checks:");
// Mass conservation: what flows in flows out.
Check(MathF.Abs(field.OutflowMeasured - field.InflowTotal) < 0.03f * field.InflowTotal,
      $"discharge conserved (in {field.InflowTotal:F1}, out {field.OutflowMeasured:F1} m³/s)");
Check(field.Tributary.Count > 50, "tributary reaches the main river");
Check(field.Islands.Count >= 4, $"islands placed ({field.Islands.Count})");

// Speeds: realistic river currents.
float maxSpeed = 0f; double sumSpeed = 0; int water = 0, lakeN = 0; double lakeSpeed = 0, riffleSpeed = 0; int riffleN = 0;
for (int i = 0; i < field.Water.Length; i++)
{
    if (!field.Water[i] || field.Still[i]) continue;
    float sp = MathF.Sqrt(field.VelX[i] * field.VelX[i] + field.VelZ[i] * field.VelZ[i]);
    maxSpeed = MathF.Max(maxSpeed, sp); sumSpeed += sp; water++;
    float z = (i / field.Nx) * field.Cell;
    if (z > 1300 && z < 1500) { lakeSpeed += sp; lakeN++; }
    if (z > 2800 && z < 2880) { riffleSpeed += sp; riffleN++; }
}
float mean = (float)(sumSpeed / water), lake = (float)(lakeSpeed / Math.Max(1, lakeN)), riffle = (float)(riffleSpeed / Math.Max(1, riffleN));
Console.WriteLine($"  speeds: mean {mean:F2}, lake {lake:F2}, riffle {riffle:F2}, max {maxSpeed:F2} m/s");
Check(mean > 0.3f && mean < 1.8f, "mean current is river-like (0.3–1.8 m/s)");
Check(lake < mean, "the lake is slower than the river on average");
Check(riffle > mean, "the riffle is faster than the river on average");

// Surface: flows downhill overall, modest total drop.
float etaTop = field.SampleEta(field.Main.X[field.Main.IndexAtZ(10)], 10f);
float etaBottom = settings.OutletEta;
Console.WriteLine($"  surface: top {etaTop:F2} m, outlet {etaBottom:F2} m, trend slope {field.EtaTrendB * 1000:F3} m/km");
Check(etaTop > etaBottom + 0.3f && etaTop < etaBottom + 12f, "water surface drops downstream by a plausible amount");

// Water surface under land meshes must stay below the terrain near shores (no water floating over banks).
int floating = 0;
for (int i = 0; i < field.Water.Length; i++)
    if (!field.Water[i] && field.ShoreDist[i] < 3f && field.Height[i] < field.Eta[i] - 0.05f) floating++;
Check(floating < 50, $"banks stay above the water next to the shore ({floating} low nodes)");

// Determinism.
var again = ReachGenerator.Generate(settings);
bool same = true;
for (int i = 0; i < field.Height.Length && same; i++) same = field.Height[i] == again.Height[i] && field.VelX[i] == again.VelX[i];
Check(same, "same seed gives the same reach");

if (mapPath != null) { MapRender.Write(field, mapPath); Console.WriteLine($"map: {mapPath}"); }
Console.WriteLine(failures == 0 ? "ALL PASS" : $"{failures} FAILED");
return failures == 0 ? 0 : 1;

static class MapRender
{
    // Top-down map: land shaded by height, water by depth, current as brightness, foam white.
    public static void Write(ReachField f, string path, int scale = 1)
    {
        int w = f.Nx, h = f.Nz;
        var px = new byte[w * h * 3];
        for (int z = 0; z < h; z++)
            for (int x = 0; x < w; x++)
            {
                int i = z * w + x;
                float r, g, b;
                if (f.Water[i])
                {
                    float d = MathF.Min(f.Depth[i] / 5f, 1f);
                    float sp = MathF.Min(MathF.Sqrt(f.VelX[i] * f.VelX[i] + f.VelZ[i] * f.VelZ[i]) / 2.5f, 1f);
                    r = 0.1f + 0.3f * sp; g = 0.35f + 0.4f * sp - 0.2f * d; b = 0.7f - 0.35f * d + 0.2f * sp;
                    if (f.Still[i]) { r = 0.4f; g = 0.2f; b = 0.5f; }
                    float fo = f.Foam[i]; r += (1 - r) * fo; g += (1 - g) * fo; b += (1 - b) * fo;
                }
                else
                {
                    float above = f.Height[i] - f.Eta[i];
                    float hx = x > 0 && x < w - 1 ? f.Height[i + 1] - f.Height[i - 1] : 0;
                    float hz = z > 0 && z < h - 1 ? f.Height[i + w] - f.Height[i - w] : 0;
                    float shade = Math.Clamp(0.75f - 0.25f * (hx - hz), 0.3f, 1.1f);
                    float t = MathF.Min(above / 80f, 1f);
                    r = (0.35f + 0.4f * t) * shade; g = (0.55f + 0.1f * t) * shade; b = (0.25f + 0.35f * t) * shade;
                    if (above < 0.8f) { r = 0.8f * shade; g = 0.74f * shade; b = 0.55f * shade; }
                }
                px[i * 3] = (byte)(Math.Clamp(r, 0, 1) * 255); px[i * 3 + 1] = (byte)(Math.Clamp(g, 0, 1) * 255); px[i * 3 + 2] = (byte)(Math.Clamp(b, 0, 1) * 255);
            }
        WritePng(path, w, h, px);
    }

    static void WritePng(string path, int w, int h, byte[] rgb)
    {
        using var fs = File.Create(path);
        fs.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        var ihdr = new byte[13];
        Be(ihdr, 0, w); Be(ihdr, 4, h); ihdr[8] = 8; ihdr[9] = 2;
        Chunk(fs, "IHDR", ihdr);
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Fastest, true))
            for (int y = 0; y < h; y++) { z.WriteByte(0); z.Write(rgb, y * w * 3, w * 3); }
        Chunk(fs, "IDAT", ms.ToArray());
        Chunk(fs, "IEND", Array.Empty<byte>());
    }
    static void Be(byte[] b, int o, int v) { b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v; }
    static void Chunk(Stream s, string type, byte[] data)
    {
        var len = new byte[4]; Be(len, 0, data.Length); s.Write(len);
        var t = System.Text.Encoding.ASCII.GetBytes(type); s.Write(t); s.Write(data);
        uint crc = Crc(t, 0xffffffff); crc = Crc(data, crc) ^ 0xffffffff;
        var c = new byte[4]; Be(c, 0, (int)crc); s.Write(c);
    }
    static uint Crc(byte[] d, uint c)
    {
        foreach (byte x in d) { c ^= x; for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xedb88320 ^ (c >> 1) : c >> 1; }
        return c;
    }
}
