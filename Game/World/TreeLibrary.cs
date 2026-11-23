using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json;
using Godot;

namespace River.World;

/// <summary>One tree model: its four LOD meshes (0–2 real, 3 the billboard) and what it is.</summary>
public sealed class TreeVariant
{
    public int Id, Species;
    public string Name;
    public Mesh[] Lods = new Mesh[4];
    public float Height;                 // m, from the manifest
    public ShaderMaterial BillboardMat;  // hides itself where the real meshes draw (Foliage.gdshader hide_within)
}

public sealed class TreeSpecies
{
    public string Name;
    /// <summary>Where LOD 0, 1 and 2 end (m from the camera), from the pack's manifest; the billboard beyond.</summary>
    public float[] LodEnd = new float[3];
    public readonly List<TreeVariant> Variants = new();
}

/// <summary>
/// The NoorForm tree packs in Assets/Trees (Noor Interchange: GLB per LOD + a <c>*.manifest.json</c> per pack, +Y up,
/// metres, pivot at the base of the trunk). Loaded once on the main thread before streaming starts.
///
/// Each LOD's GLB has a Trunk and a Leaves node; they are merged into one two-surface mesh (one MultiMesh per
/// variant and LOD). The packs embed the same textures in every LOD, so LODs 1–2 reuse LOD 0's materials (Godot would
/// otherwise keep three copies of every texture). All materials are <c>Foliage.gdshader</c>.
/// </summary>
public static class TreeLibrary
{
    public enum Kind { Birch, Oak, Pine, Spruce }
    static readonly string[] PackNames = { "Birch", "Oak", "Pine", "Spruce" };
    /// <summary>Variants used per species (of the pack's 10): each costs memory and a MultiMesh per LOD.</summary>
    public const int VariantsPerSpecies = 5;
    const string Dir = "res://Assets/Trees/";

    public static readonly List<TreeSpecies> Species = new();
    public static readonly List<TreeVariant> All = new();
    public static bool Loaded { get; private set; }
    public static float MaxLodEnd { get; private set; }

    static Shader _shader;

    public static void Load()
    {
        if (Loaded) return;
        var sw = Stopwatch.StartNew();
        _shader = GD.Load<Shader>("res://Game/Shaders/Foliage.gdshader");
        for (int s = 0; s < PackNames.Length; s++)
        {
            string manifestPath = Dir + PackNames[s] + ".manifest.json";
            if (!FileAccess.FileExists(manifestPath)) { GD.PushWarning($"Tree pack missing: {manifestPath}"); continue; }
            using var doc = JsonDocument.Parse(FileAccess.GetFileAsString(manifestPath));
            var sp = new TreeSpecies { Name = PackNames[s] };
            int n = 0;
            foreach (var v in doc.RootElement.GetProperty("variants").EnumerateArray())
            {
                if (n++ >= VariantsPerSpecies) break;
                var lods = v.GetProperty("lods");
                if (sp.Variants.Count == 0)
                    for (int l = 0; l < 3; l++) sp.LodEnd[l] = lods[l].GetProperty("visibleTo").GetSingle();
                var tv = new TreeVariant
                {
                    Id = All.Count, Species = Species.Count, Name = v.GetProperty("name").GetString(),
                    Height = v.GetProperty("heightMetres").GetSingle(),
                };
                var mats = new Dictionary<string, ShaderMaterial>();
                bool ok = true;
                for (int l = 0; l < 4 && ok; l++)
                {
                    string file = lods[l].GetProperty("file").GetString();
                    tv.Lods[l] = LoadMerged(Dir + file, l, tv, sp, mats);
                    ok = tv.Lods[l] != null;
                }
                if (!ok) { GD.PushWarning($"Tree {tv.Name}: a LOD failed to load, skipped"); continue; }
                sp.Variants.Add(tv);
                All.Add(tv);
            }
            if (sp.Variants.Count == 0) continue;
            Species.Add(sp);
            MaxLodEnd = MathF.Max(MaxLodEnd, sp.LodEnd[2]);
        }
        Loaded = Species.Count > 0;
        GD.Print($"Trees: {All.Count} variants of {Species.Count} species loaded in {sw.ElapsedMilliseconds} ms");
    }

    /// <summary>The species index of a kind, or −1 if its pack isn't there.</summary>
    public static int IndexOf(Kind k)
    {
        for (int i = 0; i < Species.Count; i++) if (Species[i].Name == PackNames[(int)k]) return i;
        return -1;
    }

    static Mesh LoadMerged(string path, int lod, TreeVariant tv, TreeSpecies sp, Dictionary<string, ShaderMaterial> mats)
    {
        var scene = GD.Load<PackedScene>(path);
        if (scene == null) return null;
        var root = scene.Instantiate<Node3D>();
        var merged = new ArrayMesh();
        void Walk(Node n, Transform3D parent)
        {
            var xf = n is Node3D n3 ? parent * n3.Transform : parent;
            if (n is MeshInstance3D mi && mi.Mesh != null)
                for (int s = 0; s < mi.Mesh.GetSurfaceCount(); s++)
                {
                    var arrays = mi.Mesh.SurfaceGetArrays(s);
                    if (!xf.IsEqualApprox(Transform3D.Identity)) TransformArrays(arrays, xf);
                    var src = mi.GetActiveMaterial(s) as BaseMaterial3D;
                    string name = src?.ResourceName ?? "";
                    if (string.IsNullOrEmpty(name)) name = mi.Name;
                    bool leaves = name.Contains("Lea", StringComparison.OrdinalIgnoreCase) || src?.Transparency != BaseMaterial3D.TransparencyEnum.Disabled;
                    ShaderMaterial mat;
                    if (lod == 3) { mat = Make(src, true, tv.Height, sp.LodEnd[2]); tv.BillboardMat = mat; }
                    else if (!mats.TryGetValue(name, out mat)) mats[name] = mat = Make(src, leaves, tv.Height, 0f);
                    // Custom channels (the packs' extra wind texcoords land in CUSTOM0..) need their formats passed on.
                    long custom = 0;
                    if (mi.Mesh is ArrayMesh am)
                    {
                        long mask = 0;
                        for (int c = 0; c < 4; c++)
                            mask |= (long)Mesh.ArrayFormat.FormatCustomMask << ((int)Mesh.ArrayFormat.FormatCustomBase + c * (int)Mesh.ArrayFormat.FormatCustomBits);
                        custom = (long)am.SurfaceGetFormat(s) & mask;
                    }
                    merged.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays, null, null, (Mesh.ArrayFormat)custom);
                    merged.SurfaceSetMaterial(merged.GetSurfaceCount() - 1, mat);
                }
            foreach (var c in n.GetChildren()) Walk(c, xf);
        }
        Walk(root, Transform3D.Identity);
        root.Free();
        return merged.GetSurfaceCount() > 0 ? merged : null;
    }

    static ShaderMaterial Make(BaseMaterial3D src, bool cutout, float height, float hideWithin)
    {
        var m = new ShaderMaterial { Shader = _shader };
        if (src != null)
        {
            m.SetShaderParameter("albedo_tex", src.AlbedoTexture);
            m.SetShaderParameter("albedo_color", src.AlbedoColor);
            if (src.NormalEnabled && src.NormalTexture != null) { m.SetShaderParameter("normal_tex", src.NormalTexture); m.SetShaderParameter("has_normal", true); }
            if (src.RoughnessTexture != null) { m.SetShaderParameter("orm_tex", src.RoughnessTexture); m.SetShaderParameter("has_orm", true); }
        }
        m.SetShaderParameter("cutout", cutout);
        m.SetShaderParameter("tree_height", height);
        m.SetShaderParameter("hide_within", hideWithin);
        return m;
    }

    static void TransformArrays(Godot.Collections.Array arrays, Transform3D xf)
    {
        if (arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array() is { } v)
        {
            for (int i = 0; i < v.Length; i++) v[i] = xf * v[i];
            arrays[(int)Mesh.ArrayType.Vertex] = v;
        }
        if (arrays[(int)Mesh.ArrayType.Normal].VariantType != Variant.Type.Nil)
        {
            var n = arrays[(int)Mesh.ArrayType.Normal].AsVector3Array();
            for (int i = 0; i < n.Length; i++) n[i] = (xf.Basis * n[i]).Normalized();
            arrays[(int)Mesh.ArrayType.Normal] = n;
        }
        if (arrays[(int)Mesh.ArrayType.Tangent].VariantType != Variant.Type.Nil)
        {
            var t = arrays[(int)Mesh.ArrayType.Tangent].AsFloat32Array();
            for (int i = 0; i + 3 < t.Length; i += 4)
            {
                var d = (xf.Basis * new Vector3(t[i], t[i + 1], t[i + 2])).Normalized();
                t[i] = d.X; t[i + 1] = d.Y; t[i + 2] = d.Z;
            }
            arrays[(int)Mesh.ArrayType.Tangent] = t;
        }
    }
}
