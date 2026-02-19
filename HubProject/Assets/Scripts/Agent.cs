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
    
    [Header("Debug")]
    [SerializeField] private bool logActions = true;
    
    private NavMeshAgent navMeshAgent;
    
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
}
