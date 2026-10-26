// Example script: makes its GameObject hop up and down, squashing when it
// lands. Add it to any object with Add Component > Scripts > Bouncer, then
// try changing the numbers in the Inspector — or edit this file and save
// while the game is playing to see it hot-reload.
public class Bouncer : MonoBehaviour
{
    [Header("Hop")]
    [Range(0.1f, 5f)] public float height = 1.5f;
    [Range(0.2f, 3f)] public float hopsPerSecond = 1f;

    [Header("Squash")]
    [Range(0f, 0.6f)] public float squashAmount = 0.3f;
    public bool logLandings;

    private Vector3 _startPosition;
    private Vector3 _startScale;
    private float _phase;
    private int _landings;

    void Start()
    {
        _startPosition = transform.localPosition;
        _startScale = transform.localScale;
    }

    void Update()
    {
        float previous = _phase;
        _phase = (_phase + Time.deltaTime * hopsPerSecond) % 1f;
        if (_phase < previous) OnLanded();

        // A parabola: 0 at take-off and landing, 1 at the top.
        float arc = 4f * _phase * (1f - _phase);
        transform.localPosition = _startPosition + new Vector3(0, arc * height, 0);

        // Squash near the ground, stretch in the air. Volume stays about the
        // same: when Y shrinks, X and Z grow.
        float squash = (1f - arc) * squashAmount - arc * squashAmount * 0.5f;
        transform.localScale = _startScale * new Vector3(1f + squash * 0.5f, 1f - squash, 1f + squash * 0.5f);
    }

    void OnLanded()
    {
        _landings++;
        if (logLandings) Debug.Log($"{name} landed ({_landings} times)");
    }
}
