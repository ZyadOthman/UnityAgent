using UnityEngine;

/// <summary>
/// Represents an object that can be picked up by the Agent.
/// Attach this to any GameObject that should be collectible.
/// </summary>
[RequireComponent(typeof(Collider))]
public class PickupObject : MonoBehaviour
{
    [Header("Pickup Settings")]
    [SerializeField] private float pickupRadius = 1.5f;
    [SerializeField] private bool autoRotate = true;
    [SerializeField] private float rotateSpeed = 45f;
    [SerializeField] private float bobAmplitude = 0.15f;
    [SerializeField] private float bobFrequency = 1.5f;

    private Vector3 startPosition;
    private bool isPickedUp = false;

    /// <summary>Whether this object has already been picked up</summary>
    public bool IsPickedUp => isPickedUp;

    /// <summary>Radius within which the agent can pick this object up</summary>
    public float PickupRadius => pickupRadius;

    void Start()
    {
        startPosition = transform.position;
    }

    void Update()
    {
        if (isPickedUp) return;

        // Visual idle animation
        if (autoRotate)
            transform.Rotate(Vector3.up, rotateSpeed * Time.deltaTime);

        if (bobAmplitude > 0f)
        {
            Vector3 pos = startPosition;
            pos.y += Mathf.Sin(Time.time * bobFrequency) * bobAmplitude;
            transform.position = pos;
        }
    }

    /// <summary>
    /// Called when the agent picks this object up.
    /// Attaches the object to the given hold point so it follows the agent.
    /// </summary>
    public void OnPickedUp(Transform holdPoint)
    {
        isPickedUp = true;

        // Disable collider so it doesn't interfere while carried
        var col = GetComponent<Collider>();
        if (col != null) col.enabled = false;

        // Parent to hold point
        transform.SetParent(holdPoint);
        transform.localPosition = Vector3.zero;
        transform.localRotation = Quaternion.identity;
    }

    /// <summary>
    /// Detaches the object from the agent and hides it (dropped into container).
    /// </summary>
    public void OnDropped()
    {
        transform.SetParent(null);
        gameObject.SetActive(false);
    }

    /// <summary>
    /// Resets the object so it can be spawned / used again.
    /// </summary>
    public void ResetObject(Vector3 newPosition)
    {
        isPickedUp = false;
        transform.SetParent(null);
        startPosition = newPosition;
        transform.position = newPosition;
        transform.rotation = Quaternion.identity;

        // Re-enable collider
        var col = GetComponent<Collider>();
        if (col != null) col.enabled = true;

        gameObject.SetActive(true);
    }

    void OnDrawGizmosSelected()
    {
        Gizmos.color = new Color(0f, 1f, 0.5f, 0.3f);
        Gizmos.DrawWireSphere(transform.position, pickupRadius);
    }
}
