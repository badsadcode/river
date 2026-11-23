using System;
using Godot;
using River.Core;

namespace River.Raft;

/// <summary>
/// The raft's look (placeholder art, built in code): a dozen lashed pine logs, cross-beams, a plank deck,
/// a canvas wall tent with a stove pipe, crates and a barrel, a lantern, and the long sweep oar at the stern.
/// Also builds the walkable deck colliders (an AnimatableBody3D that follows the raft).
/// </summary>
public static class RaftModel
{
    static StandardMaterial3D Mat(Color c, float rough = 0.85f) => new() { AlbedoColor = c, Roughness = rough };

    public static void Build(Raft raft)
    {
        var rng = new Rng(4242);
        var visual = new Node3D { Name = "Model" };
        raft.AddChild(visual);
        float L = Raft.Length, W = Raft.Width, D = Raft.LogDiameter;

        // Logs.
        int logs = (int)MathF.Round(W / D);
        for (int i = 0; i < logs; i++)
        {
            float r = D * 0.5f * rng.Range(0.92f, 1.06f);
            float len = L + rng.Range(-0.25f, 0.35f);
            var bark = new Color(0.36f, 0.27f, 0.18f) * rng.Range(0.8f, 1.1f);
            bark.A = 1f;
            var log = new MeshInstance3D
            {
                Mesh = new CylinderMesh { TopRadius = r, BottomRadius = r * rng.Range(1.0f, 1.08f), Height = len, RadialSegments = 10, Rings = 1 },
                MaterialOverride = Mat(bark, 0.95f),
                Position = new Vector3((i + 0.5f) * W / logs - W * 0.5f, 0, rng.Range(-0.12f, 0.12f)),
            };
            // Lie along z, spun about its own axis (setting Euler angles would tilt it sideways).
            log.Basis = new Basis(Vector3.Right, Mathf.Pi / 2f) * new Basis(Vector3.Up, rng.Range(0f, 6.28f));
            visual.AddChild(log);
            // Cut ends: pale wood discs.
            foreach (float end in new[] { -0.5f, 0.5f })
            {
                visual.AddChild(new MeshInstance3D
                {
                    Mesh = new CylinderMesh { TopRadius = r * 0.97f, BottomRadius = r * 0.97f, Height = 0.02f, RadialSegments = 10 },
                    MaterialOverride = Mat(new Color(0.62f, 0.5f, 0.34f)),
                    Position = log.Position + new Vector3(0, 0, end * len + Math.Sign(end) * 0.005f),
                    Rotation = new Vector3(Mathf.Pi / 2f, 0, 0),
                });
            }
        }

        // Cross-beams (lashed on top near both ends and the middle).
        var beamMat = Mat(new Color(0.42f, 0.32f, 0.22f));
        foreach (float z in new[] { -L * 0.44f, -L * 0.05f, L * 0.42f })
            visual.AddChild(new MeshInstance3D
            {
                Mesh = new BoxMesh { Size = new Vector3(W + 0.3f, 0.12f, 0.22f) }, MaterialOverride = beamMat,
                Position = new Vector3(0, D * 0.5f + 0.02f, z),
            });

        // Plank deck over most of the raft (open logs at the bow).
        float deckFrom = -L * 0.32f, deckTo = L * 0.5f - 0.1f;
        for (float z = deckFrom; z < deckTo; z += 0.26f)
        {
            var c = new Color(0.55f, 0.43f, 0.30f) * rng.Range(0.82f, 1.08f);
            c.A = 1f;
            visual.AddChild(new MeshInstance3D
            {
                Mesh = new BoxMesh { Size = new Vector3(W - 0.1f + rng.Range(-0.08f, 0.08f), 0.045f, 0.24f) },
                MaterialOverride = Mat(c, 0.9f),
                Position = new Vector3(rng.Range(-0.04f, 0.04f), Raft.DeckTop - 0.02f, z + 0.12f),
                Rotation = new Vector3(0, rng.Range(-0.012f, 0.012f), 0),
            });
        }

        // Canvas wall tent: low walls and a ridge roof.
        float tz0 = -L * 0.22f, tz1 = L * 0.16f, tw = 2.6f, wallH = 0.9f, ridge = 2.15f;
        visual.AddChild(TentMesh(tz0, tz1, tw, wallH, ridge));
        // Ridge pole and front poles.
        var poleMat = Mat(new Color(0.40f, 0.30f, 0.20f));
        visual.AddChild(new MeshInstance3D
        {
            Mesh = new CylinderMesh { TopRadius = 0.035f, BottomRadius = 0.035f, Height = tz1 - tz0 + 0.3f, RadialSegments = 6 },
            MaterialOverride = poleMat, Position = new Vector3(0, Raft.DeckTop + ridge + 0.02f, (tz0 + tz1) * 0.5f),
            Rotation = new Vector3(Mathf.Pi / 2f, 0, 0),
        });
        foreach (float z in new[] { tz0 - 0.1f, tz1 + 0.1f })
            visual.AddChild(new MeshInstance3D
            {
                Mesh = new CylinderMesh { TopRadius = 0.04f, BottomRadius = 0.045f, Height = ridge, RadialSegments = 6 },
                MaterialOverride = poleMat, Position = new Vector3(0, Raft.DeckTop + ridge * 0.5f, z),
            });
        // Stove pipe through the roof.
        visual.AddChild(new MeshInstance3D
        {
            Mesh = new CylinderMesh { TopRadius = 0.07f, BottomRadius = 0.07f, Height = 1.1f, RadialSegments = 8 },
            MaterialOverride = Mat(new Color(0.12f, 0.11f, 0.10f), 0.6f),
            Position = new Vector3(0.6f, Raft.DeckTop + 2.05f, tz1 - 0.6f),
        });

        // Crates, a barrel, a coil of rope.
        var crate = Mat(new Color(0.50f, 0.39f, 0.25f));
        visual.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.6f, 0.5f, 0.5f) }, MaterialOverride = crate, Position = new Vector3(-1.55f, Raft.DeckTop + 0.25f, L * 0.27f) });
        visual.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.5f, 0.4f, 0.45f) }, MaterialOverride = crate, Position = new Vector3(-1.5f, Raft.DeckTop + 0.7f, L * 0.27f), Rotation = new Vector3(0, 0.3f, 0) });
        visual.AddChild(new MeshInstance3D
        {
            Mesh = new CylinderMesh { TopRadius = 0.3f, BottomRadius = 0.3f, Height = 0.85f, RadialSegments = 12 },
            MaterialOverride = Mat(new Color(0.38f, 0.26f, 0.16f)), Position = new Vector3(1.55f, Raft.DeckTop + 0.42f, L * 0.25f),
        });
        visual.AddChild(new MeshInstance3D
        {
            Mesh = new TorusMesh { InnerRadius = 0.18f, OuterRadius = 0.3f, Rings = 12, RingSegments = 6 },
            MaterialOverride = Mat(new Color(0.6f, 0.52f, 0.36f)), Position = new Vector3(1.4f, Raft.DeckTop + 0.04f, -L * 0.28f),
        });

        // Lantern on a short post at the stern.
        var post = new Vector3(-1.75f, Raft.DeckTop, L * 0.46f);
        visual.AddChild(new MeshInstance3D
        {
            Mesh = new CylinderMesh { TopRadius = 0.04f, BottomRadius = 0.05f, Height = 1.6f, RadialSegments = 6 },
            MaterialOverride = poleMat, Position = post + new Vector3(0, 0.8f, 0),
        });
        var glass = new StandardMaterial3D { AlbedoColor = new Color(1f, 0.85f, 0.6f), EmissionEnabled = true, Emission = new Color(1f, 0.7f, 0.35f), EmissionEnergyMultiplier = 2.5f };
        visual.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.16f, 0.22f, 0.16f) }, MaterialOverride = glass, Position = post + new Vector3(0, 1.5f, 0) });
        var lantern = new OmniLight3D
        {
            Name = "Lantern", LightColor = new Color(1f, 0.72f, 0.42f), LightEnergy = 0f, OmniRange = 9f,
            Position = post + new Vector3(0, 1.5f, 0), ShadowEnabled = true, OmniAttenuation = 1.4f,
        };
        lantern.SetMeta("max_energy", 1.3f);
        lantern.AddToGroup("night_lights");   // EnvironmentRig turns it on at dusk
        visual.AddChild(lantern);

        // Sweep oar: a crutch post at the stern, the oar pivoting on it and trailing behind into the water.
        var crutch = new Vector3(0, Raft.DeckTop, L * 0.5f - 0.2f);
        visual.AddChild(new MeshInstance3D
        {
            Mesh = new CylinderMesh { TopRadius = 0.06f, BottomRadius = 0.07f, Height = 0.9f, RadialSegments = 6 },
            MaterialOverride = poleMat, Position = crutch + new Vector3(0, 0.45f, 0),
        });
        var pivot = new Node3D { Name = "OarPivot", Position = crutch + new Vector3(0, 0.9f, 0) };
        visual.AddChild(pivot);
        // Oar shaft: from 2.2 m inboard (handle) to 4 m outboard, sloping down into the water.
        float inboard = 2.2f, outboard = 4.2f, slope = 0.33f;  // blade dips into the water
        var shaftLen = inboard + outboard;
        var shaft = new MeshInstance3D
        {
            Mesh = new CylinderMesh { TopRadius = 0.05f, BottomRadius = 0.06f, Height = shaftLen, RadialSegments = 8 },
            MaterialOverride = Mat(new Color(0.58f, 0.46f, 0.30f)),
        };
        var mid = (outboard - inboard) * 0.5f;
        shaft.Position = new Vector3(0, -mid * MathF.Sin(slope), mid * MathF.Cos(slope));
        shaft.Rotation = new Vector3(Mathf.Pi / 2f + slope, 0, 0);
        pivot.AddChild(shaft);
        var blade = new MeshInstance3D
        {
            Mesh = new BoxMesh { Size = new Vector3(0.05f, 0.45f, 1.3f) }, MaterialOverride = Mat(new Color(0.55f, 0.43f, 0.28f)),
            Position = new Vector3(0, -(outboard - 0.6f) * MathF.Sin(slope), (outboard - 0.6f) * MathF.Cos(slope)),
            Rotation = new Vector3(slope, 0, 0),
        };
        pivot.AddChild(blade);
        raft.OarPivot = pivot;
        raft.OarHandleLocal = crutch + new Vector3(0, 0, -inboard * MathF.Cos(slope) + 0.5f);

        BuildDeckColliders(raft, deckFrom, deckTo, tz0, tz1, tw, ridge);
    }

    static MeshInstance3D TentMesh(float z0, float z1, float w, float wallH, float ridge)
    {
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        float y0 = Raft.DeckTop, hw = w * 0.5f;
        var canvas = new Color(0.80f, 0.76f, 0.64f);
        void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d) { st.AddVertex(a); st.AddVertex(b); st.AddVertex(c); st.AddVertex(a); st.AddVertex(c); st.AddVertex(d); }
        void Tri(Vector3 a, Vector3 b, Vector3 c) { st.AddVertex(a); st.AddVertex(b); st.AddVertex(c); }
        var lb0 = new Vector3(-hw, y0, z0); var lb1 = new Vector3(-hw, y0, z1);
        var lt0 = new Vector3(-hw, y0 + wallH, z0); var lt1 = new Vector3(-hw, y0 + wallH, z1);
        var rb0 = new Vector3(hw, y0, z0); var rb1 = new Vector3(hw, y0, z1);
        var rt0 = new Vector3(hw, y0 + wallH, z0); var rt1 = new Vector3(hw, y0 + wallH, z1);
        var r0 = new Vector3(0, y0 + ridge, z0); var r1 = new Vector3(0, y0 + ridge, z1);
        Quad(lb0, lt0, lt1, lb1);   // left wall
        Quad(rb1, rt1, rt0, rb0);   // right wall
        Quad(lt0, r0, r1, lt1);     // left roof
        Quad(rt1, r1, r0, rt0);     // right roof
        // Back (stern side, z1): closed. Front (bow side, z0): flaps tied open — a doorway.
        Quad(lb1, lt1, rt1, rb1); Tri(lt1, r1, rt1);
        float door = 0.55f;
        Quad(rb0, rt0, new Vector3(door, y0 + wallH + 0.25f, z0), new Vector3(door, y0, z0));
        Quad(new Vector3(-door, y0, z0), new Vector3(-door, y0 + wallH + 0.25f, z0), lt0, lb0);
        Tri(lt0, new Vector3(-door, y0 + wallH + 0.25f, z0), r0); Tri(rt0, r0, new Vector3(door, y0 + wallH + 0.25f, z0));
        st.GenerateNormals();
        var mat = new StandardMaterial3D
        {
            AlbedoColor = canvas, Roughness = 0.95f, CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            BacklightEnabled = true, Backlight = new Color(0.35f, 0.3f, 0.22f),
        };
        return new MeshInstance3D { Name = "Tent", Mesh = st.Commit(), MaterialOverride = mat };
    }

    static void BuildDeckColliders(Raft raft, float deckFrom, float deckTo, float tz0, float tz1, float tw, float ridge)
    {
        // Layer 3 (value 4): walkable deck. The raft's own hull (layer 2) never touches it.
        var deck = new AnimatableBody3D { Name = "Deck", CollisionLayer = 4, CollisionMask = 0, SyncToPhysics = false };
        raft.AddChild(deck);
        void Box(Vector3 size, Vector3 pos) => deck.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = size }, Position = pos });
        float L = Raft.Length, W = Raft.Width;
        // Walking surface over the whole raft (logs at the bow are walkable too).
        Box(new Vector3(W, 0.1f, L), new Vector3(0, Raft.DeckTop - 0.05f, 0));
        // Tent walls (you go in through the front doorway).
        Box(new Vector3(0.06f, 1.2f, tz1 - tz0), new Vector3(-tw * 0.5f, Raft.DeckTop + 0.6f, (tz0 + tz1) * 0.5f));
        Box(new Vector3(0.06f, 1.2f, tz1 - tz0), new Vector3(tw * 0.5f, Raft.DeckTop + 0.6f, (tz0 + tz1) * 0.5f));
        Box(new Vector3(tw, 1.2f, 0.06f), new Vector3(0, Raft.DeckTop + 0.6f, tz1));
        // Cargo.
        Box(new Vector3(0.6f, 0.9f, 0.5f), new Vector3(-1.55f, Raft.DeckTop + 0.45f, L * 0.27f));
        Box(new Vector3(0.6f, 0.85f, 0.6f), new Vector3(1.55f, Raft.DeckTop + 0.42f, L * 0.25f));
        raft.Deck = deck;
    }
}
