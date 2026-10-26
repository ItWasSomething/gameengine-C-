using System.Numerics;
using Engine.Core;

namespace Editor;

// The scene a brand-new project starts with: something nice to look at that
// exercises the built-in components and the material model (metal vs.
// rough, emissive, checker). Built with plain Unity-style code — `new
// GameObject`, AddComponent, CreatePrimitive — into the active scene.
public static class DemoScene
{
    public static void Populate()
    {
        var cam = new GameObject("Main Camera") { tag = TagManager.MainCamera };
        cam.AddComponent<Camera>();
        cam.transform.position = new Vector3(0, 3.2f, -9f);
        cam.transform.localEulerAngles = new Vector3(14, 0, 0);

        var sun = new GameObject("Directional Light");
        var light = sun.AddComponent<Light>();
        light.type = LightType.Directional;
        light.color = new Color(1f, 0.88f, 0.74f);
        light.intensity = 1.1f;
        sun.transform.position = new Vector3(0, 6, 0);
        sun.transform.localEulerAngles = new Vector3(38, -40, 0);

        var env = new GameObject("Sky Environment").AddComponent<SkyEnvironment>();
        env.skyTop = new Color(0.22f, 0.36f, 0.62f);
        env.skyHorizon = new Color(0.86f, 0.72f, 0.6f);
        env.ground = new Color(0.2f, 0.17f, 0.16f);
        env.ambientIntensity = 0.55f;
        env.fogDensity = 0.012f;

        var ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
        ground.name = "Ground";
        ground.transform.localScale = new Vector3(4, 1, 4);
        var groundMat = ground.GetComponent<MeshRenderer>()!.material;
        groundMat.color = new Color(0.42f, 0.44f, 0.48f);
        groundMat.smoothness = 0.08f;
        groundMat.checker = true;

        var turntable = new GameObject("Turntable");
        turntable.AddComponent<Rotator>().degreesPerSecond = new Vector3(0, 30, 0);

        var sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        sphere.name = "Gold Sphere";
        sphere.transform.SetParent(turntable.transform);
        sphere.transform.localPosition = new Vector3(-2.2f, 0.9f, 0);
        sphere.transform.localScale = new Vector3(1.8f);
        var gold = sphere.GetComponent<MeshRenderer>()!.material;
        gold.color = new Color(1f, 0.78f, 0.36f);
        gold.metallic = 1f;
        gold.smoothness = 0.78f;

        var crate = GameObject.CreatePrimitive(PrimitiveType.Cube);
        crate.name = "Crate";
        crate.transform.SetParent(turntable.transform);
        crate.transform.localPosition = new Vector3(2.2f, 0.7f, 0);
        crate.transform.localScale = new Vector3(1.4f);
        crate.transform.localEulerAngles = new Vector3(0, 25, 0);
        var crateMat = crate.GetComponent<MeshRenderer>()!.material;
        crateMat.color = new Color(1f, 0.54f, 0.24f);
        crateMat.smoothness = 0.45f;

        var glow = GameObject.CreatePrimitive(PrimitiveType.Capsule);
        glow.name = "Glow Capsule";
        glow.transform.position = new Vector3(0, 1.4f, 3.5f);
        glow.transform.localScale = new Vector3(0.8f, 0.9f, 0.8f);
        var glowMat = glow.GetComponent<MeshRenderer>()!.material;
        glowMat.color = new Color(0.1f, 0.1f, 0.12f);
        glowMat.emissionColor = new Color(0.25f, 0.85f, 1f) * 2.5f;
        var bob = glow.AddComponent<Rotator>();
        bob.degreesPerSecond = Vector3.Zero;
        bob.bobHeight = 0.25f;
        bob.bobSpeed = 0.5f;

        var glowLight = new GameObject("Capsule Light");
        glowLight.transform.SetParent(glow.transform, worldPositionStays: false);
        glowLight.transform.localPosition = new Vector3(0, 0, -1f);
        var point = glowLight.AddComponent<Light>();
        point.color = new Color(0.4f, 0.85f, 1f);
        point.intensity = 2f;
        point.range = 7f;

        var pillars = new GameObject("Pillars");
        for (int i = 0; i < 4; i++)
        {
            float angle = i / 4f * MathF.Tau + 0.4f;
            var pillar = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            pillar.name = $"Pillar ({i + 1})";
            pillar.transform.SetParent(pillars.transform);
            pillar.transform.position = new Vector3(MathF.Cos(angle) * 7f, 1.5f, MathF.Sin(angle) * 7f);
            pillar.transform.localScale = new Vector3(0.9f, 1.5f, 0.9f);
            var m = pillar.GetComponent<MeshRenderer>()!.material;
            m.color = new Color(0.78f, 0.76f, 0.72f);
            m.smoothness = 0.25f;
        }
    }
}
