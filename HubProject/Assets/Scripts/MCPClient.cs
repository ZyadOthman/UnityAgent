using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
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
        public float distance;
        public Vector3Data position;
    }
    
    [System.Serializable]
    public class EnvironmentState
    {
        public Vector3Data agent_position;
        public Vector3Data goal_position;
        public float distance_to_goal;
        public bool is_moving;
        public bool has_reached_goal;
        public List<NearbyObject> nearby_objects;
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
        EnvironmentState state = new EnvironmentState
        {
            agent_position = new Vector3Data(transform.position),
            goal_position = goal != null ? new Vector3Data(goal.position) : new Vector3Data(Vector3.zero),
            distance_to_goal = goal != null ? Vector3.Distance(transform.position, goal.position) : 0f,
            is_moving = agent.IsMoving(),
            has_reached_goal = agent.HasReachedGoal(),
            nearby_objects = new List<NearbyObject>()
        };
        
        // Detect nearby objects
        Collider[] nearbyColliders = Physics.OverlapSphere(transform.position, detectionRadius, detectableLayers);
        foreach (Collider col in nearbyColliders)
        {
            if (col.gameObject != gameObject) // Don't include self
            {
                float distance = Vector3.Distance(transform.position, col.transform.position);
                state.nearby_objects.Add(new NearbyObject
                {
                    name = col.gameObject.name,
                    distance = distance,
                    position = new Vector3Data(col.transform.position)
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
        // Prepare available actions
        var decisionRequest = new
        {
            available_actions = new string[] { "MoveTo", "Stop", "Resume", "SetGoal" },
            goal_description = "Navigate to the goal object efficiently"
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
                    Debug.Log($"[MCP] Extracted JSON: {jsonStr}");
                    
                    ActionDecision decision = JsonUtility.FromJson<ActionDecision>(jsonStr);
                    
                    if (decision == null)
                    {
                        Debug.LogError("[MCP] Failed to parse ActionDecision from JSON");
                        return;
                    }
                    
                    decisionCount++;
                    lastAIDecision = decision.action ?? "Empty";
                    lastAIReasoning = decision.reasoning ?? "No reasoning provided";
                    
                    Debug.Log($"<color=cyan>━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━</color>");
                    Debug.Log($"<color=yellow>[AI DECISION #{decisionCount}]</color>");
                    Debug.Log($"<color=green>Action:</color> {decision.action}");
                    Debug.Log($"<color=green>Target:</color> {(string.IsNullOrEmpty(decision.target) ? "None" : decision.target)}");
                    Debug.Log($"<color=green>Reasoning:</color> {decision.reasoning}");
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
                    if (target != null)
                    {
                        Debug.Log($"<color=lime>✓ EXECUTING: MoveTo({decision.target})</color>");
                        agent.MoveTo(target);
                    }
                    else
                    {
                        Debug.LogWarning($"<color=red>✗ Target object '{decision.target}' not found in scene</color>");
                        // List available objects for debugging
                        GameObject[] allObjects = FindObjectsOfType<GameObject>();
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
        float distToGoal = goal != null ? Vector3.Distance(transform.position, goal.position) : 0f;
        bool hasReached = agent != null && agent.HasReachedGoal();
        
        string status = $"<b>MCP AI Control Status</b>\n" +
                       $"━━━━━━━━━━━━━━━━━━━━━━\n" +
                       $"Decisions Made: {decisionCount}\n" +
                       $"Last Action: {lastAIDecision}\n" +
                       $"Reasoning: {lastAIReasoning}\n" +
                       $"━━━━━━━━━━━━━━━━━━━━━━\n" +
                       $"Agent Moving: {(agent != null ? agent.IsMoving().ToString() : "N/A")}\n" +
                       $"Distance to Goal: {distToGoal:F2}\n" +
                       $"Has Reached Goal: {hasReached}\n" +
                       $"Next Update: {(updateInterval - (Time.time - lastUpdateTime)):F1}s";
        
        GUI.Box(new Rect(10, 10, 400, 210), status, style);
    }
}
