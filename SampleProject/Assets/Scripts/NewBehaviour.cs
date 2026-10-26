// A MonoBehaviour script. Public fields (and [SerializeField] private
// ones) show up in the Inspector. Save this file and the editor
// recompiles it and swaps the new code in — even while playing.
public class NewBehaviour : MonoBehaviour
{
    public float speed = 2f;

    // Called once, before the first Update.
    void Start()
    {
    }

    // Called every frame.
    void Update()
    {
        // Example: spin around the Y axis.
        transform.Rotate(new Vector3(0, 90 * speed * Time.deltaTime, 0));
    }
}
