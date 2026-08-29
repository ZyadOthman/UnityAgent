using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// A container where the Agent drops picked-up objects.
/// When it reaches max capacity the Agent must empty it before adding more.
/// </summary>
public class Container : MonoBehaviour
{
    [Header("Container Settings")]
    [SerializeField] private int maxCapacity = 5;
    [SerializeField] private float dropRadius = 2.0f;
    [SerializeField] private float emptyDuration = 2.0f; // seconds to empty

    [Header("Visual Feedback")]
    [SerializeField] private Renderer containerRenderer;
    [SerializeField] private Color emptyColor = Color.green;
    [SerializeField] private Color fullColor = Color.red;

    private int currentCount = 0;
    private bool isEmptying = false;
    private float emptyTimer = 0f;

    /// <summary>Number of objects currently inside</summary>
    public int CurrentCount => currentCount;

    /// <summary>Maximum number of objects the container can hold</summary>
    public int MaxCapacity => maxCapacity;

    /// <summary>True when the container is at max capacity</summary>
    public bool IsFull => currentCount >= maxCapacity;

    /// <summary>True when the container is being emptied</summary>
    public bool IsEmptying => isEmptying;

    /// <summary>Radius within which the agent can interact with the container</summary>
    public float DropRadius => dropRadius;

    void Start()
    {
        if (containerRenderer == null)
            containerRenderer = GetComponent<Renderer>();

        UpdateVisual();
    }

    void Update()
    {
        if (isEmptying)
        {
            emptyTimer -= Time.deltaTime;
            if (emptyTimer <= 0f)
            {
                currentCount = 0;
                isEmptying = false;
                Debug.Log($"<color=lime>[Container] Emptied! ({currentCount}/{maxCapacity})</color>");
                UpdateVisual();
            }
        }
    }

    /// <summary>
    /// Try to add an object to the container.
    /// Returns true if successful, false if full or currently being emptied.
    /// </summary>
    public bool TryAddObject()
    {
        if (IsFull || isEmptying)
        {
            Debug.LogWarning($"[Container] Cannot add — Full: {IsFull}, Emptying: {isEmptying}");
            return false;
        }

        currentCount++;
        Debug.Log($"<color=yellow>[Container] Object added ({currentCount}/{maxCapacity})</color>");
        UpdateVisual();
        return true;
    }

    /// <summary>
    /// Begins the emptying process. Agent must stay near the container for the duration.
    /// Returns false if container is already empty or already being emptied.
    /// </summary>
    public bool StartEmptying()
    {
        if (currentCount == 0)
        {
            Debug.LogWarning("[Container] Already empty, nothing to do.");
            return false;
        }
        if (isEmptying)
        {
            Debug.LogWarning("[Container] Already emptying.");
            return false;
        }

        isEmptying = true;
        emptyTimer = emptyDuration;
        Debug.Log($"<color=orange>[Container] Emptying started, will finish in {emptyDuration}s...</color>");
        return true;
    }

    /// <summary>Check if the given position is within drop radius</summary>
    public bool IsInRange(Vector3 position)
    {
        return Vector3.Distance(transform.position, position) <= dropRadius;
    }

    private void UpdateVisual()
    {
        if (containerRenderer == null) return;
        float t = (float)currentCount / maxCapacity;
        containerRenderer.material.color = Color.Lerp(emptyColor, fullColor, t);
    }

    void OnDrawGizmosSelected()
    {
        Gizmos.color = new Color(1f, 0.6f, 0f, 0.35f);
        Gizmos.DrawWireSphere(transform.position, dropRadius);
    }
}
