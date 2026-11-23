using Godot;
using River.Core;

namespace River.World;

/// <summary>
/// The reach field packed into GPU textures, shared by the water, terrain and mist shaders.
///   FlowTex  (RGBA half): current x, current z, depth, foam
///   ShapeTex (RGBA half): water surface η, signed shore distance, terrain height, eddy (lee zone 0..1)
/// Sample at uv = (world.xz / cell + 0.5) / size.
/// </summary>
public sealed class FieldTextures
{
    public ImageTexture FlowTex, ShapeTex;
    public Vector2 Size;
    public float Cell;

    public FieldTextures(ReachField f)
    {
        int n = f.Nx * f.Nz;
        var flow = new float[n * 4];
        var shape = new float[n * 4];
        for (int i = 0; i < n; i++)
        {
            flow[i * 4 + 0] = f.VelX[i];
            flow[i * 4 + 1] = f.VelZ[i];
            flow[i * 4 + 2] = f.Depth[i];
            flow[i * 4 + 3] = f.Foam[i];
            shape[i * 4 + 0] = f.Eta[i];
            shape[i * 4 + 1] = Mathf.Clamp(f.ShoreDist[i], -500f, 500f);
            shape[i * 4 + 2] = f.Height[i];
            shape[i * 4 + 3] = f.Eddy != null ? f.Eddy[i] : 0f;
        }
        // Float32 rather than half: η around 20–30 m needs better than half-float's ~1 cm steps.
        FlowTex = ImageTexture.CreateFromImage(Image.CreateFromData(f.Nx, f.Nz, false, Image.Format.Rgbaf, ToBytes(flow)));
        ShapeTex = ImageTexture.CreateFromImage(Image.CreateFromData(f.Nx, f.Nz, false, Image.Format.Rgbaf, ToBytes(shape)));
        Size = new Vector2(f.Nx, f.Nz);
        Cell = f.Cell;
    }

    static byte[] ToBytes(float[] a)
    {
        var b = new byte[a.Length * 4];
        System.Buffer.BlockCopy(a, 0, b, 0, b.Length);
        return b;
    }

    public void Apply(ShaderMaterial m)
    {
        m.SetShaderParameter("flow_tex", FlowTex);
        m.SetShaderParameter("shape_tex", ShapeTex);
        m.SetShaderParameter("field_size", Size);
        m.SetShaderParameter("field_cell", Cell);
    }
}
