using System;
using UnityEngine;
using UnityEngine.AI;

public class Agent : MonoBehaviour
{
    [Header("Navigation Settings")]
    [SerializeField] private Transform goalObject;
    [SerializeField] private float stoppingDistance = 0.5f;
    [SerializeField] private bool updateDestinationContinuously = false; // Set false for MCP control
    [SerializeField] private bool autoMoveToGoalOnStart = false; // Disable for MCP control
    [SerializeField] private bool mcpControlled = true; // If true, only MCP can move the agent
    
    [Header("Inventory")]
    [SerializeField] private Transform holdPoint; // Where carried object attaches to the agent
    private PickupObject carriedObject = null;
    
    [Header("Container Reference")]
    [SerializeField] private Container container;
    
    [Header("Debug")]
    [SerializeField] private bool logActions = true;
    
    private NavMeshAgent navMeshAgent;
    
    /// <summary>Whether the agent is currently carrying an object</summary>
    public bool IsCarrying => carriedObject != null;
    
    /// <summary>Name of the carried object, or empty string</summary>
    public string CarriedObjectName => carriedObject != null ? carriedObject.gameObject.name : "";
    
    /// <summary>Reference to the container</summary>
    public Container Container => container;
    
    void Start()
    {
        // Get the NavMeshAgent component
        navMeshAgent = GetComponent<NavMeshAgent>();
        
        if (navMeshAgent == null)
        {
            Debug.LogError("NavMeshAgent component not found on " + gameObject.name);
            return;
        }
        
        // Set stopping distance
        navMeshAgent.stoppingDistance = stoppingDistance;
        
        // Create a default hold point if not assigned
        if (holdPoint == null)
        {
            GameObject hp = new GameObject("HoldPoint");
            hp.transform.SetParent(transform);
            hp.transform.localPosition = new Vector3(0f, 1.5f, 0.5f);
            holdPoint = hp.transform;
        }
        
        // Move to goal if it's set and auto-move is enabled
        if (autoMoveToGoalOnStart && goalObject != null)
        {
            MoveToGoal();
        }
        else if (goalObject == null)
        {
            Debug.LogWarning("Goal object not assigned to Agent.");
        }
    }

    void Update()
    {
        // Continuously update destination if goal object moves (only if not MCP controlled)
        if (!mcpControlled && updateDestinationContinuously && goalObject != null && navMeshAgent != null)
        {
            if (Vector3.Distance(navMeshAgent.destination, goalObject.position) > 0.1f)
            {
                MoveToGoal();
            }
        }
    }
    
    /// <summary>
    /// Moves the agent to the goal object's position
    /// </summary>
    public void MoveToGoal()
    {
        if (goalObject != null && navMeshAgent != null)
        {
            if (logActions)
                Debug.Log($"[Agent] MoveToGoal() called - Moving to {goalObject.name}");
            navMeshAgent.isStopped = false;
            navMeshAgent.SetDestination(goalObject.position);
        }
    }
    
    /// <summary>
    /// Sets a new goal object for the agent
    /// </summary>
    public void SetGoal(Transform newGoal)
    {
        goalObject = newGoal;
        if (goalObject != null)
        {
            MoveToGoal();
        }
    }
    
    /// <summary>
    /// Checks if the agent has reached the goal
    /// </summary>
    public bool HasReachedGoal()
    {
        if (navMeshAgent == null || goalObject == null)
            return false;
        
        // If no path is set yet, agent hasn't reached goal
        if (!navMeshAgent.hasPath && !navMeshAgent.pathPending)
            return false;
            
        // Check if agent is close to the goal using actual distance
        float actualDistance = Vector3.Distance(transform.position, goalObject.position);
        bool reachedByDistance = actualDistance <= navMeshAgent.stoppingDistance;
        
        // Also check NavMeshAgent's remaining distance (for agents following paths)
        bool reachedByPath = !navMeshAgent.pathPending && 
                             navMeshAgent.hasPath &&
                             navMeshAgent.remainingDistance <= navMeshAgent.stoppingDistance;
        
        if (logActions)
        {
            Debug.Log($"[Agent] HasReachedGoal Check:\n" +
                     $"  - Actual Distance: {actualDistance:F2}\n" +
                     $"  - Remaining Path Distance: {navMeshAgent.remainingDistance:F2}\n" +
                     $"  - Stopping Distance: {navMeshAgent.stoppingDistance:F2}\n" +
                     $"  - Has Path: {navMeshAgent.hasPath}\n" +
                     $"  - Result: {(reachedByDistance || reachedByPath)}");
        }
        
        return reachedByDistance || reachedByPath;
    }
    
    /// <summary>
    /// Stops the agent's movement
    /// </summary>
    public void Stop()
    {
        if (navMeshAgent != null)
        {
            if (logActions)
                Debug.Log($"[Agent] Stop() called");
            navMeshAgent.isStopped = true;
        }
    }
    
    /// <summary>
    /// Resumes the agent's movement
    /// </summary>
    public void Resume()
    {
        if (navMeshAgent != null)
        {
            if (logActions)
                Debug.Log($"[Agent] Resume() called");
            navMeshAgent.isStopped = false;
        }
    }
    
    /// <summary>
    /// Checks if the agent is currently moving
    /// </summary>
    public bool IsMoving()
    {
        if (navMeshAgent == null)
            return false;
            
        return navMeshAgent.velocity.magnitude > 0.1f;
    }
    
    /// <summary>
    /// Gets the agent's current velocity
    /// </summary>
    public Vector3 GetVelocity()
    {
        return navMeshAgent != null ? navMeshAgent.velocity : Vector3.zero;
    }
    
    /// <summary>
    /// Moves the agent to a specific position
    /// </summary>
    public void MoveTo(Vector3 position)
    {
        if (navMeshAgent != null)
        {
            if (logActions)
                Debug.Log($"[Agent] MoveTo(Vector3) called - Position: {position}");
            navMeshAgent.isStopped = false;
            navMeshAgent.SetDestination(position);
        }
    }
    
    /// <summary>
    /// Moves the agent to a specific GameObject
    /// </summary>
    public void MoveTo(GameObject target)
    {
        if (target != null)
        {
            if (logActions)
                Debug.Log($"[Agent] MoveTo(GameObject) called - Target: {target.name}");
            navMeshAgent.isStopped = false;
            SetGoal(target.transform);
        }
    }
    
    /// <summary>
    /// Gets the current goal object
    /// </summary>
    public Transform GetGoal()
    {
        return goalObject;
    }
    
    // ──────────────────────────────────────────────
    //  Pickup / Drop / Empty actions
    // ──────────────────────────────────────────────
    
    /// <summary>
    /// Attempts to pick up the given PickupObject.
    /// Agent must be close enough and not already carrying something.
    /// </summary>
    public bool TryPickup(PickupObject target)
    {
        if (target == null || target.IsPickedUp)
        {
            if (logActions) Debug.Log("[Agent] TryPickup failed — target is null or already picked up");
            return false;
        }
        
        if (carriedObject != null)
        {
            if (logActions) Debug.Log("[Agent] TryPickup failed — already carrying an object");
            return false;
        }
        
        float dist = Vector3.Distance(transform.position, target.transform.position);
        if (dist > target.PickupRadius)
        {
            if (logActions) Debug.Log($"[Agent] TryPickup failed — too far ({dist:F2} > {target.PickupRadius})");
            return false;
        }
        
        carriedObject = target;
        target.OnPickedUp(holdPoint);
        if (logActions) Debug.Log($"<color=lime>[Agent] Picked up {target.gameObject.name}</color>");
        return true;
    }
    
    /// <summary>
    /// Attempts to drop the carried object into the container.
    /// Agent must be near the container and container must not be full.
    /// </summary>
    public bool TryDropInContainer()
    {
        if (carriedObject == null)
        {
            if (logActions) Debug.Log("[Agent] TryDrop failed — not carrying anything");
            return false;
        }
        if (container == null)
        {
            if (logActions) Debug.Log("[Agent] TryDrop failed — no container reference");
            return false;
        }
        if (!container.IsInRange(transform.position))
        {
            if (logActions) Debug.Log("[Agent] TryDrop failed — not close enough to container");
            return false;
        }
        if (container.IsFull)
        {
            if (logActions) Debug.Log("[Agent] TryDrop failed — container is full, empty it first!");
            return false;
        }
        
        bool added = container.TryAddObject();
        if (added)
        {
            if (logActions) Debug.Log($"<color=lime>[Agent] Dropped {carriedObject.gameObject.name} in container ({container.CurrentCount}/{container.MaxCapacity})</color>");
            carriedObject.OnDropped();
            carriedObject = null;
            return true;
        }
        return false;
    }
    
    /// <summary>
    /// Begins emptying the container.
    /// Agent must be near the container.
    /// </summary>
    public bool TryEmptyContainer()
    {
        if (container == null)
        {
            if (logActions) Debug.Log("[Agent] TryEmpty failed — no container reference");
            return false;
        }
        if (!container.IsInRange(transform.position))
        {
            if (logActions) Debug.Log("[Agent] TryEmpty failed — not close enough to container");
            return false;
        }
        
        bool started = container.StartEmptying();
        if (started && logActions)
            Debug.Log("<color=orange>[Agent] Started emptying the container</color>");
        return started;
    }
}
