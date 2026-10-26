// Temporary file for a screenshot: shows highlighting and an error squiggle.
public class ThemeDemo : MonoBehaviour
{
    [Range(0, 10)] public float speed = 3f;
    public Bouncer target;
    private int _hits;

    void Update()
    {
        /* block comments
           span lines */
        var move = new Vector3(Input.GetAxis("Horizontal"), 0, 0);
        transform.Translate(move * speed * Time.deltaTime);
        if (Input.GetKeyDown(Key.Space)) _hits++;
        speed = "fast";
        Debug.Log($"hits: {_hits}");
    }
}
