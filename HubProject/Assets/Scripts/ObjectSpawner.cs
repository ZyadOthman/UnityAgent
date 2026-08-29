using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Spawns PickupObjects at random positions on the NavMesh within a defined area.
/// </summary>
public class ObjectSpawner : MonoBehaviour
{
    [Header("Spawn Settings")]
    [SerializeField] private GameObject pickupPrefab;
    [SerializeField] private int maxObjectsInScene = 5;
    [SerializeField] private float spawnInterval = 8f;
    [SerializeField] private float spawnRadius = 15f;
    [SerializeField] private float spawnHeight = 0.5f;

    [Header("Area Constraints")]
    [SerializeField] private Vector3 spawnCenter = Vector3.zero;
    [Tooltip("Keep spawned objects at least this far from the container")]
    [SerializeField] private float minDistFromContainer = 3f;
    [SerializeField] private Transform containerTransform;

    private List<PickupObject> spawnedObjects = new List<PickupObject>();
    private float spawnTimer;

    /// <summary>All currently active (not picked-up) pickup objects in the scene</summary>
    public List<PickupObject> ActiveObjects
    {
        get
        {
            spawnedObjects.RemoveAll(o => o == null);
            return spawnedObjects.FindAll(o => o.gameObject.activeInHierarchy && !o.IsPickedUp);
        }
    }

    void Start()
    {
        spawnTimer = 0f; // Spawn first batch immediately

        if (pickupPrefab == null)
        {
            Debug.LogError("[ObjectSpawner] Pickup prefab is not assigned!");
            enabled = false;
            return;
        }

        // Initial spawn burst
        for (int i = 0; i < maxObjectsInScene; i++)
        {
            SpawnObject();
        }
    }

    void Update()
    {
        // Clean up destroyed references
        spawnedObjects.RemoveAll(o => o == null);

        int activeCount = 0;
        foreach (var obj in spawnedObjects)
        {
            if (obj.gameObject.activeInHierarchy && !obj.IsPickedUp)
                activeCount++;
        }

        spawnTimer -= Time.deltaTime;
        if (spawnTimer <= 0f && activeCount < maxObjectsInScene)
        {
            SpawnObject();
            spawnTimer = spawnInterval;
        }
    }

    private void SpawnObject()
    {
        Vector3 pos = GetRandomSpawnPosition();

        // Try to reuse an inactive pooled object first
        foreach (var obj in spawnedObjects)
        {
            if (obj != null && !obj.gameObject.activeInHierarchy)
            {
                obj.ResetObject(pos);
                Debug.Log($"[ObjectSpawner] Recycled object at {pos}");
                return;
            }
        }

        // Otherwise instantiate a new one
        GameObject go = Instantiate(pickupPrefab, pos, Quaternion.identity);
        go.name = $"Pickup_{spawnedObjects.Count}";
        PickupObject pickup = go.GetComponent<PickupObject>();
        if (pickup == null)
            pickup = go.AddComponent<PickupObject>();

        spawnedObjects.Add(pickup);
        Debug.Log($"[ObjectSpawner] Spawned {go.name} at {pos}");
    }

    private Vector3 GetRandomSpawnPosition()
    {
        for (int attempt = 0; attempt < 30; attempt++)
        {
            Vector2 rnd = Random.insideUnitCircle * spawnRadius;
            Vector3 candidate = spawnCenter + new Vector3(rnd.x, spawnHeight, rnd.y);

            // Ensure minimum distance from container
            if (containerTransform != null)
            {
                float dist = Vector3.Distance(candidate, containerTransform.position);
                if (dist < minDistFromContainer)
                    continue;
            }

            // Try to project onto NavMesh
            UnityEngine.AI.NavMeshHit hit;
            if (UnityEngine.AI.NavMesh.SamplePosition(candidate, out hit, 2f, UnityEngine.AI.NavMesh.AllAreas))
            {
                Vector3 result = hit.position;
                result.y += spawnHeight;
                return result;
            }
        }

        // Fallback: just return a random position
        Vector2 fallback = Random.insideUnitCircle * spawnRadius;
        return spawnCenter + new Vector3(fallback.x, spawnHeight, fallback.y);
    }

    void OnDrawGizmosSelected()
    {
        Gizmos.color = new Color(0.2f, 0.6f, 1f, 0.2f);
        Gizmos.DrawWireSphere(spawnCenter, spawnRadius);
    }
}
