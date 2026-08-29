using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.Networking;

/// <summary>
/// Client for communicating with the MCP server
/// Sends environment state and receives action decisions
/// </summary>
public class MCPClient : MonoBehaviour
{
    [Header("MCP Server Settings")]
    [SerializeField] private string serverUrl = "http://localhost:8000";
    [SerializeField] private float updateInterval = 1.0f; // How often to send updates
    
    [Header("References")]
    [SerializeField] private Agent agent;
    [SerializeField] private Transform goalObject;
    [SerializeField] private Container container;
    [SerializeField] private ObjectSpawner objectSpawner;
    
    [Header("Detection Settings")]
    [SerializeField] private float detectionRadius = 10f;
    [SerializeField] private LayerMask detectableLayers;
    
    [Header("Debug")]
    [SerializeField] private bool enableDebugLogs = true;
    
    private float lastUpdateTime;
    private string lastAIDecision = "None";
    private string lastAIReasoning = "Waiting for first decision...";
    private int decisionCount = 0;
    private bool isRequestInProgress = false;
    
    [System.Serializable]
    public class Vector3Data
    {
        public float x;
        public float y;
        public float z;
        
        public Vector3Data(Vector3 v)
        {
            x = v.x;
            y = v.y;
            z = v.z;
        }
    }
    
    [System.Serializable]
    public class NearbyObject
    {
        public string name;
        public string type; // "pickup", "container", "obstacle", "other"
        public float distance;
        public Vector3Data position;
        public bool is_pickable; // only relevant for pickup objects
    }
    
    [System.Serializable]
    public class ContainerState
    {
        public int current_count;
        public int max_capacity;
        public bool is_full;
        public bool is_emptying;
        public Vector3Data position;
    }
    
    [System.Serializable]
    public class InventoryState
    {
        public bool is_carrying;
        public string carried_object_name;
    }
    
    [System.Serializable]
    public class EnvironmentState
    {
        public Vector3Data agent_position;
        public Vector3Data goal_position;
        public float distance_to_goal;
        public bool is_moving;
        public bool has_reached_goal;
        public InventoryState inventory;
        public ContainerState container;
        public List<NearbyObject> nearby_objects;
        public List<string> available_pickups; // names of active pickup objects
    }
    
    [System.Serializable]
    public class ActionDecision
    {
        public string action;
        public string target;
        public string reasoning;
    }
    
    void Start()
    {
        if (agent == null)
        {
            agent = GetComponent<Agent>();
        }
        
        if (agent == null)
        {
            Debug.LogError("Agent component not found!");
            enabled = false;
            return;
        }
        
        lastUpdateTime = Time.time;
    }
    
    void Update()
    {
        // Send updates at regular intervals, but only if no request is already in progress
        if (!isRequestInProgress && Time.time - lastUpdateTime >= updateInterval)
        {
            StartCoroutine(SendEnvironmentStateAndGetAction());
            lastUpdateTime = Time.time;
        }
    }
    
    /// <summary>
    /// Collects the current environment state
    /// </summary>
    private EnvironmentState CollectEnvironmentState()
    {
        Transform goal = goalObject != null ? goalObject : agent.GetGoal();
        Vector3 agentPos = agent.transform.position;
        EnvironmentState state = new EnvironmentState
        {
            agent_position = new Vector3Data(agentPos),
            goal_position = goal != null ? new Vector3Data(goal.position) : new Vector3Data(Vector3.zero),
            distance_to_goal = goal != null ? Vector3.Distance(agentPos, goal.position) : 0f,
            is_moving = agent.IsMoving(),
            has_reached_goal = agent.HasReachedGoal(),
            nearby_objects = new List<NearbyObject>(),
            available_pickups = new List<string>()
        };
        
        // Inventory state
        state.inventory = new InventoryState
        {
            is_carrying = agent.IsCarrying,
            carried_object_name = agent.CarriedObjectName
        };
        
        // Container state
        Container cont = container != null ? container : agent.Container;
        if (cont != null)
        {
            state.container = new ContainerState
            {
                current_count = cont.CurrentCount,
                max_capacity = cont.MaxCapacity,
                is_full = cont.IsFull,
                is_emptying = cont.IsEmptying,
                position = new Vector3Data(cont.transform.position)
            };
        }
        
        // Collect available pickup object names from spawner
        if (objectSpawner != null)
        {
            foreach (var pickup in objectSpawner.ActiveObjects)
            {
                state.available_pickups.Add(pickup.gameObject.name);
            }
        }
        
        // Detect nearby objects
        Collider[] nearbyColliders = Physics.OverlapSphere(agentPos, detectionRadius, detectableLayers);
        foreach (Collider col in nearbyColliders)
        {
            if (col.gameObject != gameObject && col.gameObject != agent.gameObject) // Don't include self or agent
            {
                float distance = Vector3.Distance(agentPos, col.transform.position);
                
                // Determine object type
                string objType = "other";
                bool isPickable = false;
                PickupObject po = col.GetComponent<PickupObject>();
                if (po != null && !po.IsPickedUp)
                {
                    objType = "pickup";
                    isPickable = true;
                }
                else if (col.GetComponent<Container>() != null)
                {
                    objType = "container";
                }
                
                state.nearby_objects.Add(new NearbyObject
                {
                    name = col.gameObject.name,
                    type = objType,
                    distance = distance,
                    position = new Vector3Data(col.transform.position),
                    is_pickable = isPickable
                });
            }
        }
        
        return state;
    }
    
    /// <summary>
    /// Sends environment state to MCP server and requests action decision
    /// </summary>
    private IEnumerator SendEnvironmentStateAndGetAction()
    {
        isRequestInProgress = true;
        
        // Collect current state
        EnvironmentState state = CollectEnvironmentState();
        
        // First, update the environment on the server
        yield return StartCoroutine(UpdateEnvironmentOnServer(state));
        
        // Then, request an action decision
        yield return StartCoroutine(RequestActionDecision());
        
        isRequestInProgress = false;
    }
    
    /// <summary>
    /// Updates the environment state on the MCP server
    /// </summary>
    private IEnumerator UpdateEnvironmentOnServer(EnvironmentState state)
    {
        string jsonData = JsonUtility.ToJson(state);
        
        using (UnityWebRequest www = new UnityWebRequest(serverUrl + "/mcp/update", "POST"))
        {
            byte[] bodyRaw = Encoding.UTF8.GetBytes(jsonData);
            www.uploadHandler = new UploadHandlerRaw(bodyRaw);
            www.downloadHandler = new DownloadHandlerBuffer();
            www.SetRequestHeader("Content-Type", "application/json");
            
            yield return www.SendWebRequest();
            
            if (www.result != UnityWebRequest.Result.Success)
            {
                Debug.LogWarning($"Failed to update environment: {www.error}");
            }
            else
            {
                if (enableDebugLogs)
                {
                    Debug.Log($"[MCP] Environment state sent successfully");
                }
            }
        }
    }
    
    /// <summary>
    /// Requests an action decision from the MCP server
    /// </summary>
    private IEnumerator RequestActionDecision()
    {
        // Build the available actions list including the new task actions
        string[] availableActions = new string[]
        {
            "MoveTo",       // Move to a named object (pickup or container)
            "Stop",         // Stop movement
            "Resume",       // Resume movement
            "SetGoal",      // Change goal object
            "Pickup",       // Pick up a nearby PickupObject (target = object name)
            "DropInContainer", // Drop carried object into the container
            "EmptyContainer"   // Empty the container when it is full
        };
        
        // Prepare available actions
        var decisionRequest = new DecisionRequestData
        {
            available_actions = availableActions,
            goal_description = "Pick up randomly spawned objects and drop them in the container. " +
                               "When the container is full, empty it before picking up more objects. " +
                               "Prioritize: if carrying an object go to container and drop it; " +
                               "if container is full go to container and empty it; " +
                               "otherwise find the nearest pickup object, move to it, and pick it up."
        };
        
        string jsonData = JsonUtility.ToJson(decisionRequest);
        
        using (UnityWebRequest www = new UnityWebRequest(serverUrl + "/mcp/decide", "POST"))
        {
            byte[] bodyRaw = Encoding.UTF8.GetBytes(jsonData);
            www.uploadHandler = new UploadHandlerRaw(bodyRaw);
            www.downloadHandler = new DownloadHandlerBuffer();
            www.SetRequestHeader("Content-Type", "application/json");
            
            yield return www.SendWebRequest();
            
            if (www.result != UnityWebRequest.Result.Success)
            {
                Debug.LogWarning($"[MCP] Failed to get action decision: {www.error}");
            }
            else
            {
                string response = www.downloadHandler.text;
                if (enableDebugLogs)
                {
                    Debug.Log($"[MCP] Raw response received from server");
                }
                
                // Parse and execute the decision
                ExecuteActionFromResponse(response);
            }
        }
    }
    
    [System.Serializable]
    public class DecisionRequestData
    {
        public string[] available_actions;
        public string goal_description;
    }
    
    /// <summary>
    /// Parses the MCP response and executes the recommended action
    /// </summary>
    private void ExecuteActionFromResponse(string response)
    {
        try
        {
            Debug.Log($"[MCP] Full raw response: {response}");
            
            // First, try to parse the outer response structure
            var responseData = JsonUtility.FromJson<ServerResponse>(response);
            
            if (responseData != null && !string.IsNullOrEmpty(responseData.decision))
            {
                Debug.Log($"[MCP] Extracted decision string: {responseData.decision}");
                
                // Now parse the actual decision from the nested JSON
                int jsonStart = responseData.decision.IndexOf("{");
                int jsonEnd = responseData.decision.LastIndexOf("}");
                
                if (jsonStart >= 0 && jsonEnd > jsonStart)
                {
                    string jsonStr = responseData.decision.Substring(jsonStart, jsonEnd - jsonStart + 1);
                    
                    // Clean up common LLM artefacts in the JSON
                    jsonStr = jsonStr.Replace("\\n", " ").Replace("\\t", " ");
                    // Remove unicode escapes that are just backslash noise
                    jsonStr = System.Text.RegularExpressions.Regex.Replace(jsonStr, @"\\{2,}", "");
                    
                    Debug.Log($"[MCP] Extracted JSON: {jsonStr}");
                    
                    ActionDecision decision = JsonUtility.FromJson<ActionDecision>(jsonStr);
                    
                    if (decision == null || string.IsNullOrEmpty(decision.action))
                    {
                        Debug.LogError("[MCP] Failed to parse ActionDecision from JSON or action is empty");
                        return;
                    }
                    
                    // Validate the action is known
                    string[] validActions = { "MoveTo", "Stop", "Resume", "SetGoal", "Pickup", "DropInContainer", "EmptyContainer" };
                    bool actionValid = false;
                    foreach (string va in validActions)
                    {
                        if (decision.action == va) { actionValid = true; break; }
                    }
                    
                    if (!actionValid)
                    {
                        Debug.LogWarning($"[MCP] LLM returned invalid action '{decision.action}', ignoring");
                        return;
                    }
                    
                    // Clean up target — treat null-ish strings as null
                    if (!string.IsNullOrEmpty(decision.target))
                    {
                        decision.target = decision.target.Trim().Trim('"').Trim('\\');
                        if (decision.target == "null" || decision.target == "None" || decision.target == "N/A" || decision.target.Length == 0)
                            decision.target = null;
                    }
                    
                    decisionCount++;
                    
                    Debug.Log($"<color=cyan>━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━</color>");
                    Debug.Log($"<color=yellow>[AI DECISION #{decisionCount}]</color>");
                    Debug.Log($"<color=green>LLM Action:</color> {decision.action}");
                    Debug.Log($"<color=green>LLM Target:</color> {(string.IsNullOrEmpty(decision.target) ? "None" : decision.target)}");
                    Debug.Log($"<color=green>LLM Reasoning:</color> {decision.reasoning}");
                    
                    // Override bad LLM decisions based on actual state
                    decision = OverrideDecisionIfNeeded(decision);
                    
                    // Update HUD with the final (possibly overridden) decision
                    lastAIDecision = decision.action ?? "Empty";
                    lastAIReasoning = decision.reasoning ?? "No reasoning provided";
                    
                    Debug.Log($"<color=green>Final Action:</color> {decision.action}");
                    Debug.Log($"<color=green>Final Reasoning:</color> {decision.reasoning}");
                    Debug.Log($"<color=cyan>━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━</color>");
                    
                    // Execute the action
                    ExecuteAction(decision);
                }
                else
                {
                    Debug.LogError($"[MCP] Could not extract JSON from decision string: {responseData.decision}");
                }
            }
            else
            {
                Debug.LogError("[MCP] Failed to parse server response or decision is empty");
            }
        }
        catch (Exception e)
        {
            Debug.LogError($"[MCP] Exception parsing response: {e.Message}\nFull Response: {response}");
        }
    }
    
    /// <summary>
    /// Overrides bad LLM decisions based on current agent state.
    /// Returns a corrected decision, or the original if it's fine.
    /// </summary>
    private ActionDecision OverrideDecisionIfNeeded(ActionDecision decision)
    {
        Container cont = container != null ? container : agent.Container;
        
        // RULE 1: If container is being emptied, just wait
        if (cont != null && cont.IsEmptying)
        {
            if (decision.action != "Stop")
            {
                Debug.Log("<color=orange>[MCP OVERRIDE] Container is emptying → Stop</color>");
                return new ActionDecision { action = "Stop", target = null, reasoning = "Container is being emptied — waiting for it to finish before resuming tasks" };
            }
            return decision;
        }
        
        // RULE 2: If carrying an object, MUST go to container and drop — never pick up another
        if (agent.IsCarrying)
        {
            // If already at container, drop
            if (cont != null && cont.IsInRange(agent.transform.position))
            {
                if (cont.IsFull)
                {
                    Debug.Log("<color=orange>[MCP OVERRIDE] Carrying + at full container → EmptyContainer first</color>");
                    return new ActionDecision { action = "EmptyContainer", target = null, reasoning = $"Carrying '{agent.CarriedObjectName}' but container is full ({cont.CurrentCount}/{cont.MaxCapacity}) — emptying container first" };
                }
                Debug.Log("<color=orange>[MCP OVERRIDE] Carrying + at container → DropInContainer</color>");
                return new ActionDecision { action = "DropInContainer", target = null, reasoning = $"Dropping '{agent.CarriedObjectName}' into container ({cont.CurrentCount}/{cont.MaxCapacity})" };
            }
            
            // Otherwise, go to container
            if (decision.action != "MoveTo" || decision.target == null || !decision.target.ToLower().Contains("container"))
            {
                string containerName = cont != null ? cont.gameObject.name : "Container";
                Debug.Log($"<color=orange>[MCP OVERRIDE] Carrying object → MoveTo {containerName}</color>");
                return new ActionDecision { action = "MoveTo", target = containerName, reasoning = $"Carrying '{agent.CarriedObjectName}' — heading to container to drop it off" };
            }
            return decision;
        }
        
        // RULE 3: If container is full and not carrying, must empty it
        if (cont != null && cont.IsFull)
        {
            if (cont.IsInRange(agent.transform.position))
            {
                Debug.Log("<color=orange>[MCP OVERRIDE] Container full + near → EmptyContainer</color>");
                return new ActionDecision { action = "EmptyContainer", target = null, reasoning = $"Container is full ({cont.CurrentCount}/{cont.MaxCapacity}) — emptying to make room for more objects" };
            }
            
            if (decision.action != "MoveTo" || decision.target == null || !decision.target.ToLower().Contains("container"))
            {
                string containerName = cont.gameObject.name;
                Debug.Log($"<color=orange>[MCP OVERRIDE] Container full → MoveTo {containerName}</color>");
                return new ActionDecision { action = "MoveTo", target = containerName, reasoning = $"Container is full ({cont.CurrentCount}/{cont.MaxCapacity}) — heading to container to empty it" };
            }
            return decision;
        }
        
        // No override needed
        return decision;
    }
    
    /// <summary>
    /// Executes the given action decision
    /// </summary>
    private void ExecuteAction(ActionDecision decision)
    {
        if (string.IsNullOrEmpty(decision.action))
        {
            Debug.LogWarning("[MCP] Action is null or empty, skipping execution");
            return;
        }
        
        Debug.Log($"<color=cyan>[MCP] ExecuteAction - Action: '{decision.action}', Target: '{decision.target ?? "null"}'</color>");
        
        switch (decision.action)
        {
            case "MoveTo":
                if (!string.IsNullOrEmpty(decision.target) && decision.target != "Goal" && decision.target != "goal")
                {
                    GameObject target = GameObject.Find(decision.target);
                    
                    // If not found, try to resolve generic names like "nearest_pickup"
                    if (target == null)
                    {
                        target = ResolveTarget(decision.target);
                    }
                    
                    if (target != null)
                    {
                        // Use the AGENT's position for distance checks, not MCPClient's
                        Vector3 agentPos = agent.transform.position;
                        float distToTarget = Vector3.Distance(agentPos, target.transform.position);
                        Debug.Log($"<color=white>[MCP] MoveTo check: distance to {decision.target} = {distToTarget:F2}, agent pos = {agentPos}, target pos = {target.transform.position}</color>");
                        
                        // Check if target is a pickup object and we're in range
                        PickupObject pickupComp = target.GetComponent<PickupObject>();
                        if (pickupComp != null)
                        {
                            Debug.Log($"<color=white>[MCP] Pickup check: isPickedUp={pickupComp.IsPickedUp}, pickupRadius={pickupComp.PickupRadius}, inRange={distToTarget <= pickupComp.PickupRadius}, isCarrying={agent.IsCarrying}</color>");
                        }
                        
                        if (pickupComp != null && !pickupComp.IsPickedUp && distToTarget <= pickupComp.PickupRadius)
                        {
                            bool picked = agent.TryPickup(pickupComp);
                            Debug.Log(picked
                                ? $"<color=lime>✓ AUTO-PICKUP: Already near {decision.target}, picked it up!</color>"
                                : $"<color=yellow>⚡ Near {decision.target} but pickup failed (already carrying?), moving anyway</color>");
                            if (picked) break; // Done, no need to move
                        }
                        
                        // Check if target is the container and we're carrying + in range
                        Container containerComp = target.GetComponent<Container>();
                        float agentDistToContainer = Vector3.Distance(agentPos, target.transform.position);
                        if (containerComp != null && agentDistToContainer <= containerComp.DropRadius)
                        {
                            if (agent.IsCarrying && !containerComp.IsFull)
                            {
                                bool dropped = agent.TryDropInContainer();
                                Debug.Log(dropped
                                    ? $"<color=lime>✓ AUTO-DROP: Already near container, dropped object!</color>"
                                    : $"<color=yellow>⚡ Near container but drop failed</color>");
                                if (dropped) break;
                            }
                            else if (containerComp.IsFull && !containerComp.IsEmptying)
                            {
                                bool emptied = agent.TryEmptyContainer();
                                Debug.Log(emptied
                                    ? $"<color=orange>✓ AUTO-EMPTY: Already near full container, emptying!</color>"
                                    : $"<color=yellow>⚡ Near container but empty failed</color>");
                                if (emptied) break;
                            }
                        }
                        
                        Debug.Log($"<color=lime>✓ EXECUTING: MoveTo({decision.target})</color>");
                        agent.MoveTo(target);
                    }
                    else
                    {
                        Debug.LogWarning($"<color=red>✗ Target object '{decision.target}' not found in scene</color>");
                        // List available objects for debugging
                        GameObject[] allObjects = FindObjectsByType<GameObject>(FindObjectsSortMode.None);
                        Debug.Log($"<color=yellow>Available objects: {string.Join(", ", System.Array.ConvertAll(allObjects, obj => obj.name))}</color>");
                    }
                }
                else
                {
                    // Try MCPClient's goalObject first, then fall back to Agent's own goal
                    Transform goal = goalObject != null ? goalObject : agent.GetGoal();
                    if (goal != null)
                    {
                        Debug.Log($"<color=lime>✓ EXECUTING: MoveToGoal() - Target: {goal.name}</color>");
                        agent.MoveToGoal();
                    }
                    else
                    {
                        Debug.LogError("<color=red>✗ Goal object is not assigned on MCPClient or Agent!</color>");
                    }
                }
                break;
                
            case "Stop":
                Debug.Log($"<color=lime>✓ EXECUTING: Stop()</color>");
                agent.Stop();
                break;
                
            case "Resume":
                Debug.Log($"<color=lime>✓ EXECUTING: Resume()</color>");
                agent.Resume();
                break;
                
            case "SetGoal":
                if (!string.IsNullOrEmpty(decision.target))
                {
                    GameObject newGoal = GameObject.Find(decision.target);
                    if (newGoal != null)
                    {
                        Debug.Log($"<color=lime>✓ EXECUTING: SetGoal({decision.target})</color>");
                        agent.SetGoal(newGoal.transform);
                    }
                    else
                    {
                        Debug.LogWarning($"<color=red>✗ Goal object '{decision.target}' not found</color>");
                    }
                }
                break;
            
            // ── New task-based actions ────────────────────────
            
            case "Pickup":
                if (!string.IsNullOrEmpty(decision.target))
                {
                    GameObject pickupGO = GameObject.Find(decision.target);
                    if (pickupGO != null)
                    {
                        PickupObject pickup = pickupGO.GetComponent<PickupObject>();
                        if (pickup != null)
                        {
                            bool success = agent.TryPickup(pickup);
                            Debug.Log(success
                                ? $"<color=lime>✓ EXECUTING: Pickup({decision.target}) — SUCCESS</color>"
                                : $"<color=red>✗ Pickup({decision.target}) — FAILED (too far or already carrying)</color>");
                        }
                        else
                        {
                            Debug.LogWarning($"<color=red>✗ '{decision.target}' has no PickupObject component</color>");
                        }
                    }
                    else
                    {
                        Debug.LogWarning($"<color=red>✗ Pickup target '{decision.target}' not found</color>");
                    }
                }
                else
                {
                    Debug.LogWarning("<color=red>✗ Pickup action requires a target name</color>");
                }
                break;
                
            case "DropInContainer":
                {
                    bool success = agent.TryDropInContainer();
                    Debug.Log(success
                        ? "<color=lime>✓ EXECUTING: DropInContainer — SUCCESS</color>"
                        : "<color=red>✗ DropInContainer — FAILED (not carrying, not near container, or container full)</color>");
                }
                break;
                
            case "EmptyContainer":
                {
                    bool success = agent.TryEmptyContainer();
                    Debug.Log(success
                        ? "<color=orange>✓ EXECUTING: EmptyContainer — STARTED</color>"
                        : "<color=red>✗ EmptyContainer — FAILED (not near container, already empty, or already emptying)</color>");
                }
                break;
                
            default:
                Debug.LogWarning($"<color=orange>? Unknown action: {decision.action}</color>");
                break;
        }
    }
    
    [System.Serializable]
    public class ServerResponse
    {
        public bool success;
        public string decision;
    }
    
    /// <summary>
    /// Resolves generic/descriptive target names to actual GameObjects.
    /// E.g. "nearest_pickup" → the closest active PickupObject.
    /// </summary>
    private GameObject ResolveTarget(string targetName)
    {
        if (string.IsNullOrEmpty(targetName)) return null;
        
        string lower = targetName.ToLower().Replace("-", "_").Replace(" ", "_");
        
        // Generic pickup references
        if (lower.Contains("pickup") || lower.Contains("object") || lower == "nearest" || lower == "closest")
        {
            GameObject closest = null;
            float closestDist = float.MaxValue;
            Vector3 agentPos = agent.transform.position;
            
            // Try the spawner first
            if (objectSpawner != null)
            {
                foreach (var pickup in objectSpawner.ActiveObjects)
                {
                    float dist = Vector3.Distance(agentPos, pickup.transform.position);
                    if (dist < closestDist)
                    {
                        closestDist = dist;
                        closest = pickup.gameObject;
                    }
                }
            }
            
            // Fallback: find ALL PickupObjects in scene (handles null spawner or untracked objects)
            if (closest == null)
            {
                PickupObject[] allPickups = FindObjectsByType<PickupObject>(FindObjectsSortMode.None);
                foreach (var pickup in allPickups)
                {
                    if (pickup.IsPickedUp || !pickup.gameObject.activeInHierarchy) continue;
                    float dist = Vector3.Distance(agentPos, pickup.transform.position);
                    if (dist < closestDist)
                    {
                        closestDist = dist;
                        closest = pickup.gameObject;
                    }
                }
            }
            
            if (closest != null)
            {
                Debug.Log($"<color=yellow>[MCP] Resolved '{targetName}' → '{closest.name}' (dist: {closestDist:F2})</color>");
                return closest;
            }
        }
        
        // Generic container references
        if (lower.Contains("container") || lower.Contains("bin") || lower.Contains("drop"))
        {
            Container cont = container != null ? container : agent.Container;
            if (cont != null)
            {
                Debug.Log($"<color=yellow>[MCP] Resolved '{targetName}' → '{cont.gameObject.name}'</color>");
                return cont.gameObject;
            }
        }
        
        return null;
    }
    
    /// <summary>
    /// Manually trigger an environment update and action request
    /// </summary>
    public void ManualUpdate()
    {
        StartCoroutine(SendEnvironmentStateAndGetAction());
    }
    
    void OnDrawGizmosSelected()
    {
        // Visualize detection radius
        Gizmos.color = Color.yellow;
        Gizmos.DrawWireSphere(transform.position, detectionRadius);
    }
    
    /// <summary>
    /// Get the last AI decision for debugging
    /// </summary>
    public string GetLastDecision()
    {
        return $"Decision #{decisionCount}: {lastAIDecision} - {lastAIReasoning}";
    }
    
    void OnGUI()
    {
        if (!enableDebugLogs) return;
        
        // Display AI decision status on screen
        GUIStyle style = new GUIStyle(GUI.skin.box);
        style.fontSize = 14;
        style.alignment = TextAnchor.UpperLeft;
        style.normal.textColor = Color.white;
        
        Transform goal = goalObject != null ? goalObject : (agent != null ? agent.GetGoal() : null);
        Vector3 aPos = agent != null ? agent.transform.position : transform.position;
        float distToGoal = goal != null ? Vector3.Distance(aPos, goal.position) : 0f;
        bool hasReached = agent != null && agent.HasReachedGoal();
        
        Container cont = container != null ? container : (agent != null ? agent.Container : null);
        string containerInfo = cont != null
            ? $"{cont.CurrentCount}/{cont.MaxCapacity} (Full: {cont.IsFull}, Emptying: {cont.IsEmptying})"
            : "N/A";
        
        int pickupsAvailable = objectSpawner != null ? objectSpawner.ActiveObjects.Count : 0;
        
        string status = $"<b>MCP AI Control Status</b>\n" +
                       $"━━━━━━━━━━━━━━━━━━━━━━\n" +
                       $"Decisions Made: {decisionCount}\n" +
                       $"Last Action: {lastAIDecision}\n" +
                       $"Reasoning: {lastAIReasoning}\n" +
                       $"━━━━━━━━━━━━━━━━━━━━━━\n" +
                       $"Agent Moving: {(agent != null ? agent.IsMoving().ToString() : "N/A")}\n" +
                       $"Carrying: {(agent != null && agent.IsCarrying ? agent.CarriedObjectName : "Nothing")}\n" +
                       $"Container: {containerInfo}\n" +
                       $"Pickups Available: {pickupsAvailable}\n" +
                       $"Distance to Goal: {distToGoal:F2}\n" +
                       $"Next Update: {(updateInterval - (Time.time - lastUpdateTime)):F1}s";
        
        GUI.Box(new Rect(10, 10, 420, 260), status, style);
    }
}
