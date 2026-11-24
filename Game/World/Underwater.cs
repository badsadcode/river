using System;
using Godot;

namespace River.World;

/// <summary>
/// The underwater view (Tom: "add an underwater effect"): while the current camera is below the water surface, a
/// full-screen quad in front of it (Underwater.gdshader) tints, fogs and wobbles the picture; the river surface is then
/// seen from below (River.gdshader, back faces). Works for any camera: eyes, chase, fly camera.
/// </summary>
public partial class Underwater : Node
{
    MeshInstance3D _quad;
    ShaderMaterial _mat;
    Camera3D _attached;
    readonly StringName _below = new("below"), _light = new("light");
    public bool Active { get; private set; }

    public override void _Ready()
    {
        _mat = new ShaderMaterial { Shader = GD.Load<Shader>("res://Game/Shaders/Underwater.gdshader"), RenderPriority = 127 };
        _quad = new MeshInstance3D
        {
            Name = "UnderwaterQuad", Mesh = new QuadMesh { Size = new Vector2(2f, 2f) }, MaterialOverride = _mat,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, ExtraCullMargin = 16384f, Visible = false,
        };
    }

    public override void _Process(double delta)
    {
        var main = River.Main.Instance;
        var cam = GetViewport().GetCamera3D();
        if (main?.Raft == null || cam == null) return;
        var p = cam.GlobalPosition;
        var (wx, wz) = Origin.ToWorld(p);
        var here = main.World.At(wx, wz);
        float eta = main.Raft.WaterHeight(p.X, p.Z);
        float below = eta - p.Y;
        Active = here.Water && below > 0.03f;
        if (Active && _attached != cam)
        {
            _quad.GetParent()?.RemoveChild(_quad);
            cam.AddChild(_quad);
            _quad.Position = new Vector3(0, 0, -0.2f);
            _attached = cam;
        }
        _quad.Visible = Active;
        if (!Active) return;
        _mat.SetShaderParameter(_below, below);
        _mat.SetShaderParameter(_light, Math.Clamp(0.12f + main.Env.SunElevation / 35f, 0.06f, 1f));
    }
}
