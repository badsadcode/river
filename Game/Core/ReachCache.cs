using System;
using System.IO;

namespace River.Core;

/// <summary>
/// Binary cache of a generated reach, so launching the game (and screenshot captures) doesn't re-solve the flow
/// every time. The caller provides a key that changes whenever the generator code or settings change.
/// </summary>
public static class ReachCache
{
    const int Magic = 0x52495652; // "RIVR"

    public static void Save(ReachField f, string path, string key)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var w = new BinaryWriter(File.Create(path));
        w.Write(Magic); w.Write(key);
        w.Write(f.Nx); w.Write(f.Nz); w.Write(f.Cell); w.Write(f.Seed);
        w.Write(f.EtaTrendA); w.Write(f.EtaTrendB); w.Write(f.OutflowMeasured); w.Write(f.InflowTotal);
        WriteBools(w, f.Water); WriteBools(w, f.Still);
        foreach (var a in new[] { f.Height, f.Eta, f.Depth, f.VelX, f.VelZ, f.Foam, f.ShoreDist, f.Eddy }) WriteFloats(w, a);
        WriteLine(w, f.Main); WriteLine(w, f.Tributary);
        w.Write(f.Islands.Count);
        foreach (var i in f.Islands)
        {
            w.Write(i.X); w.Write(i.Z); w.Write(i.DirX); w.Write(i.DirZ); w.Write(i.LengthUp); w.Write(i.LengthDown);
            w.Write(i.HalfWidth); w.Write(i.Roughness); w.Write(i.MaxHeight); w.Write(i.Seed);
        }
        w.Write(f.Rocks.Count);
        foreach (var r in f.Rocks) { w.Write(r.X); w.Write(r.Z); w.Write(r.Radius); w.Write(r.Top); w.Write(r.Seed); w.Write(r.FlowX); w.Write(r.FlowZ); }
    }

    public static ReachField TryLoad(string path, string key)
    {
        if (!File.Exists(path)) return null;
        try
        {
            using var rd = new BinaryReader(File.OpenRead(path));
            if (rd.ReadInt32() != Magic || rd.ReadString() != key) return null;
            var f = new ReachField { Nx = rd.ReadInt32(), Nz = rd.ReadInt32(), Cell = rd.ReadSingle(), Seed = rd.ReadInt32() };
            f.EtaTrendA = rd.ReadSingle(); f.EtaTrendB = rd.ReadSingle(); f.OutflowMeasured = rd.ReadSingle(); f.InflowTotal = rd.ReadSingle();
            int n = f.Nx * f.Nz;
            f.Water = ReadBools(rd, n); f.Still = ReadBools(rd, n);
            f.Height = ReadFloats(rd, n); f.Eta = ReadFloats(rd, n); f.Depth = ReadFloats(rd, n);
            f.VelX = ReadFloats(rd, n); f.VelZ = ReadFloats(rd, n); f.Foam = ReadFloats(rd, n); f.ShoreDist = ReadFloats(rd, n);
            f.Eddy = ReadFloats(rd, n);
            f.Main = ReadLine(rd); f.Tributary = ReadLine(rd);
            int ni = rd.ReadInt32();
            for (int k = 0; k < ni; k++)
                f.Islands.Add(new Island
                {
                    X = rd.ReadSingle(), Z = rd.ReadSingle(), DirX = rd.ReadSingle(), DirZ = rd.ReadSingle(),
                    LengthUp = rd.ReadSingle(), LengthDown = rd.ReadSingle(), HalfWidth = rd.ReadSingle(),
                    Roughness = rd.ReadSingle(), MaxHeight = rd.ReadSingle(), Seed = rd.ReadInt32(),
                });
            int nr = rd.ReadInt32();
            for (int k = 0; k < nr; k++)
                f.Rocks.Add(new Rock
                {
                    X = rd.ReadSingle(), Z = rd.ReadSingle(), Radius = rd.ReadSingle(), Top = rd.ReadSingle(), Seed = rd.ReadInt32(),
                    FlowX = rd.ReadSingle(), FlowZ = rd.ReadSingle(),
                });
            return f;
        }
        catch (Exception) { return null; }
    }

    static void WriteBools(BinaryWriter w, bool[] a) { var b = new byte[a.Length]; for (int i = 0; i < a.Length; i++) b[i] = a[i] ? (byte)1 : (byte)0; w.Write(b); }
    static bool[] ReadBools(BinaryReader r, int n) { var b = r.ReadBytes(n); var a = new bool[n]; for (int i = 0; i < n; i++) a[i] = b[i] != 0; return a; }
    static void WriteFloats(BinaryWriter w, float[] a) { var b = new byte[a.Length * 4]; Buffer.BlockCopy(a, 0, b, 0, b.Length); w.Write(b); }
    static float[] ReadFloats(BinaryReader r, int n) { var b = r.ReadBytes(n * 4); var a = new float[n]; Buffer.BlockCopy(b, 0, a, 0, b.Length); return a; }

    static void WriteLine(BinaryWriter w, Centerline c)
    {
        w.Write(c.Count);
        for (int i = 0; i < c.Count; i++) { w.Write(c.X[i]); w.Write(c.Z[i]); w.Write(c.HalfLeft[i]); w.Write(c.HalfRight[i]); w.Write(c.MaxDepth[i]); }
    }
    static Centerline ReadLine(BinaryReader r)
    {
        var c = new Centerline();
        int n = r.ReadInt32();
        for (int i = 0; i < n; i++) c.Add(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
        c.Finish();
        return c;
    }
}
