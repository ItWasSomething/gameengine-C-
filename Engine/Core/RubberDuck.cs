using System.Numerics;

namespace Engine.Core;

// A rubber duck, for rubber-duck debugging: explain your bug to the duck,
// out loud, line by line, and halfway through you usually spot it yourself.
//
// What it does (all deliberately useless):
//   • In the editor, every duck in the scene quacks in the Console whenever
//     your scripts fail to compile, and a "QUACK!" bubble pops up above it in
//     the Scene view.
//   • Select a duck and it offers one piece of debugging wisdom.
//   • In Play mode it bobs gently, like it's floating in a bath.
// GameObject → Create → Rubber Duck builds one out of primitives; adding this
// component to anything else turns that thing into an honorary duck.
[AddComponentMenu("Fun/Rubber Duck")]
[DisallowMultipleComponent]
public class RubberDuck : MonoBehaviour
{
    [Tooltip("Quack in the Console when scripts fail to compile.")]
    public bool quackOnCompileErrors = true;

    [Header("Bath time (Play mode)")]
    [Range(0, 0.5f)] public float bobHeight = 0.06f;
    [Range(0.1f, 3f)] public float bobSpeed = 0.7f;

    [Tooltip("Lifetime quacks. Goes up. Means nothing.")]
    public int quackCount;

    private Vector3 _startPosition;
    private float _phase;

    private static readonly string[] Wisdom =
    [
        "Have you tried turning it off and on again?",
        "Read the error message. Out loud. To me.",
        "What did you expect to happen, and what happened instead?",
        "Is it actually saved? Ctrl+S. I'll wait.",
        "Print the value right before it goes wrong.",
        "It worked yesterday. What changed since yesterday?",
        "Off-by-one? It's always off-by-one.",
        "Is that variable null, or are you just hoping it isn't?",
        "Explain the bug to me one line at a time. I'm listening.",
        "Did you forget to multiply by Time.deltaTime?",
        "Take a break. The bug will still be here. So will I.",
        "Comment out half the code. Is the bug in the other half?",
        "quack.",
    ];

    public static string RandomWisdom() => Wisdom[Random.Shared.Next(Wisdom.Length)];

    // Called by the editor when a compile fails. Returns the line to log.
    public string Quack(int errorCount)
    {
        quackCount++;
        string quacks = errorCount switch
        {
            1 => "Quack.",
            <= 3 => "Quack quack.",
            _ => "QUACK QUACK QUACK.",
        };
        return $"{quacks} ({errorCount} error{(errorCount == 1 ? "" : "s")}) — {RandomWisdom()}";
    }

    private void Start()
    {
        _startPosition = transform.localPosition;
        // Each duck bobs a little out of step with the others.
        _phase = Random.Shared.NextSingle() * MathF.Tau;
    }

    private void Update()
    {
        if (bobHeight <= 0) return;
        float t = Time.time * bobSpeed * MathF.Tau + _phase;
        transform.localPosition = _startPosition + Vector3.UnitY * MathF.Sin(t) * bobHeight;
        // A slight rocking tilt, out of phase with the bob.
        transform.localEulerAngles = new Vector3(MathF.Cos(t) * 4f, transform.localEulerAngles.Y, MathF.Sin(t * 0.5f) * 3f);
    }
}
