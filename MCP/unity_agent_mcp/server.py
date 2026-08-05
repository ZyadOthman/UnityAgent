"""
Unity Agent MCP Server
Receives environment state from Unity and uses LLM to decide agent actions
"""

import asyncio
import json
from typing import Any
from mcp.server.models import InitializationOptions
import mcp.types as types
from mcp.server import NotificationOptions, Server
import mcp.server.stdio
import os
import httpx


# Initialize the MCP server
server = Server("unity-agent-mcp")

# Store the current environment state
current_environment_state = {
    "agent_position": {"x": 0, "y": 0, "z": 0},
    "goal_position": {"x": 0, "y": 0, "z": 0},
    "distance_to_goal": 0,
    "is_moving": False,
    "has_reached_goal": False,
    "inventory": {
        "is_carrying": False,
        "carried_object_name": ""
    },
    "container": {
        "current_count": 0,
        "max_capacity": 5,
        "is_full": False,
        "is_emptying": False,
        "position": {"x": 0, "y": 0, "z": 0}
    },
    "nearby_objects": [],
    "available_pickups": []
}

# Configure LLM (vLLM with OpenAI-compatible API)
VLLM_HOST = os.environ.get("VLLM_HOST", "http://localhost:8080")
VLLM_MODEL = os.environ.get("VLLM_MODEL", "Qwen/Qwen2.5-1.5B-Instruct")

print(f"Using vLLM at {VLLM_HOST} with model {VLLM_MODEL}")


@server.list_resources()
async def handle_list_resources() -> list[types.Resource]:
    """List available resources - in this case, the environment state"""
    return [
        types.Resource(
            uri="unity://environment/state",
            name="Unity Environment State",
            description="Current state of the Unity agent and its environment",
            mimeType="application/json",
        )
    ]


@server.read_resource()
async def handle_read_resource(uri: str) -> str:
    """Read the current environment state"""
    if uri == "unity://environment/state":
        return json.dumps(current_environment_state, indent=2)
    else:
        raise ValueError(f"Unknown resource: {uri}")


@server.list_tools()
async def handle_list_tools() -> list[types.Tool]:
    """List available tools for the Unity agent"""
    return [
        types.Tool(
            name="update_environment",
            description="Update the current environment state from Unity",
            inputSchema={
                "type": "object",
                "properties": {
                    "agent_position": {
                        "type": "object",
                        "properties": {
                            "x": {"type": "number"},
                            "y": {"type": "number"},
                            "z": {"type": "number"}
                        },
                        "description": "Current position of the agent"
                    },
                    "goal_position": {
                        "type": "object",
                        "properties": {
                            "x": {"type": "number"},
                            "y": {"type": "number"},
                            "z": {"type": "number"}
                        },
                        "description": "Position of the goal object"
                    },
                    "distance_to_goal": {
                        "type": "number",
                        "description": "Distance from agent to goal"
                    },
                    "is_moving": {
                        "type": "boolean",
                        "description": "Whether the agent is currently moving"
                    },
                    "nearby_objects": {
                        "type": "array",
                        "items": {
                            "type": "object",
                            "properties": {
                                "name": {"type": "string"},
                                "distance": {"type": "number"},
                                "position": {
                                    "type": "object",
                                    "properties": {
                                        "x": {"type": "number"},
                                        "y": {"type": "number"},
                                        "z": {"type": "number"}
                                    }
                                }
                            }
                        },
                        "description": "List of nearby objects"
                    }
                },
                "required": ["agent_position"]
            }
        ),
        types.Tool(
            name="decide_agent_action",
            description="Use LLM to decide which action the agent should take based on current environment",
            inputSchema={
                "type": "object",
                "properties": {
                    "available_actions": {
                        "type": "array",
                        "items": {"type": "string"},
                        "description": "List of available actions (e.g., 'MoveTo', 'Stop', 'Resume')"
                    },
                    "goal_description": {
                        "type": "string",
                        "description": "Description of what the agent should achieve"
                    }
                },
                "required": ["available_actions"]
            }
        ),
        types.Tool(
            name="get_agent_command",
            description="Get a specific command for the Unity agent to execute",
            inputSchema={
                "type": "object",
                "properties": {
                    "context": {
                        "type": "string",
                        "description": "Additional context about the current situation"
                    }
                }
            }
        )
    ]


VALID_ACTIONS = {"MoveTo", "Stop", "Resume", "SetGoal", "Pickup", "DropInContainer", "EmptyContainer"}

# Actions that require a non-empty target to be useful
ACTIONS_REQUIRING_TARGET = {"MoveTo", "Pickup", "SetGoal"}


def _validate_llm_decision(raw_text: str, available_actions: list[str]) -> dict | None:
    """
    Attempt to parse and validate the LLM response.
    Returns a clean decision dict if valid, None otherwise.
    """
    import re
    
    # Try to extract JSON from the response (handle markdown wrapping etc.)
    text = raw_text.strip()
    
    # Remove markdown code fences if present
    text = re.sub(r'^```(?:json)?\s*', '', text)
    text = re.sub(r'\s*```$', '', text)
    text = text.strip()
    
    # Find the first complete JSON object
    start = text.find('{')
    end = text.rfind('}')
    if start < 0 or end <= start:
        return None
    
    json_str = text[start:end + 1]
    
    try:
        decision = json.loads(json_str)
    except json.JSONDecodeError:
        # Try fixing common issues: trailing commas, single quotes
        try:
            fixed = re.sub(r',\s*}', '}', json_str)
            fixed = fixed.replace("'", '"')
            decision = json.loads(fixed)
        except json.JSONDecodeError:
            return None
    
    if not isinstance(decision, dict):
        return None
    
    action = decision.get("action", "").strip()
    if action not in VALID_ACTIONS:
        return None
    
    target = decision.get("target")
    # Normalise null-ish targets
    if target is not None:
        target = str(target).strip().strip('"').strip("'").strip("\\")
        if target.lower() in ("", "null", "none", "n/a"):
            target = None
    
    # If the action requires a target but we don't have one, reject
    if action in ACTIONS_REQUIRING_TARGET and not target:
        return None
    
    # Resolve generic/descriptive targets to actual object names
    if target:
        target = _resolve_target(target, action)
        # If resolution returned None for an action that needs a target, reject
        if action in ACTIONS_REQUIRING_TARGET and not target:
            return None
    
    reasoning = decision.get("reasoning", "LLM decision")
    if not isinstance(reasoning, str):
        reasoning = str(reasoning)
    
    return {
        "action": action,
        "target": target,
        "reasoning": reasoning
    }


def _resolve_target(target: str, action: str) -> str | None:
    """
    If the LLM returned a generic/descriptive target instead of an actual
    object name, try to resolve it to a real object from the environment state.
    """
    nearby = current_environment_state.get("nearby_objects", [])
    pickups = current_environment_state.get("available_pickups", [])
    
    # Check if target already matches an actual object name
    all_known_names = set(pickups)
    for obj in nearby:
        all_known_names.add(obj.get("name", ""))
    
    if target in all_known_names:
        return target  # Already a valid name
    
    # Generic pickup references → resolve to nearest pickup
    generic_pickup_words = {"nearest_pickup", "nearest pickup", "pickup", "closest_pickup",
                            "closest pickup", "nearest_object", "nearest object", "object"}
    if target.lower().replace("-", "_") in generic_pickup_words:
        nearby_pickups = [o for o in nearby if o.get("is_pickable", False) or o.get("type") == "pickup"]
        if nearby_pickups:
            closest = min(nearby_pickups, key=lambda o: o.get("distance", 999))
            print(f"Resolved generic target '{target}' -> '{closest['name']}'")
            return closest["name"]
        elif pickups:
            print(f"Resolved generic target '{target}' -> '{pickups[0]}' (from available_pickups)")
            return pickups[0]
        return None
    
    # Generic container references
    if "container" in target.lower():
        for obj in nearby:
            if obj.get("type") == "container" or "container" in obj.get("name", "").lower():
                print(f"Resolved generic target '{target}' -> '{obj['name']}'")
                return obj["name"]
        return "Container"  # Default name
    
    # If still unresolved, return as-is and let Unity handle it
    print(f"Warning: target '{target}' not found in known objects: {all_known_names}")
    return target


@server.call_tool()
async def handle_call_tool(
    name: str, arguments: dict | None
) -> list[types.TextContent | types.ImageContent | types.EmbeddedResource]:
    """Handle tool calls"""
    
    if name == "update_environment":
        # Update the global environment state
        if arguments:
            current_environment_state.update(arguments)
        
        return [
            types.TextContent(
                type="text",
                text=f"Environment state updated successfully:\n{json.dumps(current_environment_state, indent=2)}"
            )
        ]
    
    elif name == "decide_agent_action":
        available_actions = arguments.get("available_actions", ["MoveTo", "Stop", "Resume", "SetGoal"])
        goal_description = arguments.get("goal_description", "Navigate to the goal")
        
        # Prepare context for LLM
        env_state_str = json.dumps(current_environment_state, indent=2)
        
        # Build a concise situation summary for the small LLM
        inv = current_environment_state.get("inventory", {})
        cont = current_environment_state.get("container", {})
        nearby = current_environment_state.get("nearby_objects", [])
        pickups = current_environment_state.get("available_pickups", [])
        is_carrying = inv.get("is_carrying", False)
        carried_name = inv.get("carried_object_name", "")
        cont_full = cont.get("is_full", False)
        cont_emptying = cont.get("is_emptying", False)
        cont_count = cont.get("current_count", 0)
        cont_max = cont.get("max_capacity", 5)
        is_moving = current_environment_state.get("is_moving", False)
        
        # Find nearby pickups and container with distances
        nearby_pickups = [o for o in nearby if o.get("is_pickable", False) or o.get("type") == "pickup"]
        nearby_container = [o for o in nearby if o.get("type") == "container" or "container" in o.get("name", "").lower()]
        
        nearest_pickup_str = "none nearby"
        if nearby_pickups:
            closest_p = min(nearby_pickups, key=lambda o: o.get("distance", 999))
            nearest_pickup_str = f"{closest_p['name']} at distance {closest_p.get('distance', 0):.1f}"
        
        container_dist_str = "unknown"
        if nearby_container:
            container_dist_str = f"{nearby_container[0].get('distance', 0):.1f}"
        
        # Determine the best example target name to show the LLM
        example_pickup = pickups[0] if pickups else "Pickup_0"
        example_nearest = nearest_pickup_str
        
        situation = f"""Carrying: {is_carrying} ({carried_name})
Container: {cont_count}/{cont_max}, full={cont_full}, emptying={cont_emptying}, distance={container_dist_str}
Nearest pickup: {nearest_pickup_str}
All pickups in scene: {pickups}
Agent moving: {is_moving}"""

        prompt = f"""You control a Unity agent. Pick up objects, bring them to the container, empty container when full.

SITUATION:
{situation}

RULES:
- If container is emptying: {{"action": "Stop", "target": null, "reasoning": "waiting"}}
- If container is full: {{"action": "MoveTo", "target": "Container", "reasoning": "going to empty"}}
- If carrying object: {{"action": "MoveTo", "target": "Container", "reasoning": "delivering"}}
- If not carrying: {{"action": "MoveTo", "target": "{example_pickup}", "reasoning": "going to pick up"}}
- target MUST be an exact object name from the lists above (e.g. "{example_pickup}", "Container")
- NEVER use generic names like "nearest_pickup" — use the actual name like "{example_pickup}"

Respond with ONLY valid JSON:
{{"action": "MoveTo", "target": "{example_pickup}", "reasoning": "moving to nearest pickup"}}"""
        
        # Use LLM to decide (vLLM with OpenAI-compatible API)
        llm_decision = None
        try:
            async with httpx.AsyncClient(timeout=60.0) as client:
                response = await client.post(
                    f"{VLLM_HOST}/v1/chat/completions",
                    json={
                        "model": VLLM_MODEL,
                        "messages": [
                            {"role": "system", "content": "You are an AI agent controller for a Unity game. You MUST respond with a single valid JSON object only, nothing else. No markdown, no explanation. The JSON must have keys: action, target, reasoning."},
                            {"role": "user", "content": prompt}
                        ],
                        "temperature": 0.05,
                        "max_tokens": 150
                    }
                )
                
                if response.status_code == 200:
                    result = response.json()
                    response_text = result["choices"][0]["message"]["content"].strip()
                    
                    # Try to validate the LLM response
                    llm_decision = _validate_llm_decision(response_text, available_actions)
                    
                    if llm_decision:
                        print(f"LLM decision validated: {llm_decision}")
                        return [
                            types.TextContent(
                                type="text",
                                text=f"LLM Decision:\n{json.dumps(llm_decision)}"
                            )
                        ]
                    else:
                        print(f"LLM returned invalid decision, falling back. Raw: {response_text}")
                else:
                    print(f"vLLM error: {response.status_code} - {response.text}")
                    
        except Exception as e:
            print(f"Error calling vLLM: {str(e)}")
        
        print("Using rule-based decision...")
        
        # Fallback rule-based logic if no valid LLM decision
        inventory = current_environment_state.get("inventory", {})
        container_state = current_environment_state.get("container", {})
        is_carrying = inventory.get("is_carrying", False)
        container_full = container_state.get("is_full", False)
        container_emptying = container_state.get("is_emptying", False)
        is_moving = current_environment_state.get("is_moving", False)
        nearby = current_environment_state.get("nearby_objects", [])
        available_pickups = current_environment_state.get("available_pickups", [])
        
        if container_emptying:
            # Wait for emptying to finish
            decision = {
                "action": "Stop",
                "target": None,
                "reasoning": "Container is being emptied, waiting for it to finish"
            }
        elif container_full and not is_carrying:
            # Container is full — need to empty it
            # Check if agent is near container
            near_container = any(o.get("type") == "container" or o.get("name", "").lower() == "container"
                                 for o in nearby if o.get("distance", 999) < 2.5)
            if near_container:
                decision = {
                    "action": "EmptyContainer",
                    "target": None,
                    "reasoning": "Container is full and agent is near it — emptying"
                }
            else:
                decision = {
                    "action": "MoveTo",
                    "target": "Container",
                    "reasoning": "Container is full — moving to container to empty it"
                }
        elif is_carrying:
            # Agent is carrying an object — go to container and drop
            near_container = any(o.get("type") == "container" or o.get("name", "").lower() == "container"
                                 for o in nearby if o.get("distance", 999) < 2.5)
            if near_container:
                decision = {
                    "action": "DropInContainer",
                    "target": None,
                    "reasoning": "Agent is carrying an object and is near the container — dropping"
                }
            else:
                decision = {
                    "action": "MoveTo",
                    "target": "Container",
                    "reasoning": "Agent is carrying an object — moving to container to drop it"
                }
        elif available_pickups:
            # Find closest pickup from nearby_objects
            pickup_objs = [o for o in nearby if o.get("type") == "pickup" or o.get("is_pickable", False)]
            if pickup_objs:
                closest = min(pickup_objs, key=lambda o: o.get("distance", 999))
                if closest.get("distance", 999) < 2.0:
                    decision = {
                        "action": "Pickup",
                        "target": closest["name"],
                        "reasoning": f"Pickup object {closest['name']} is within reach — picking up"
                    }
                else:
                    decision = {
                        "action": "MoveTo",
                        "target": closest["name"],
                        "reasoning": f"Moving to nearest pickup object {closest['name']}"
                    }
            else:
                # Pickups exist but not in detection range, move to first available
                decision = {
                    "action": "MoveTo",
                    "target": available_pickups[0],
                    "reasoning": f"Moving to pickup object {available_pickups[0]}"
                }
        else:
            decision = {
                "action": "Stop",
                "target": None,
                "reasoning": "No pickup objects available, waiting for spawns"
            }
        
        return [
            types.TextContent(
                type="text",
                text=f"Rule-based Decision:\n{json.dumps(decision, indent=2)}"
            )
        ]
    
    elif name == "get_agent_command":
        context = arguments.get("context", "") if arguments else ""
        
        # Return a command based on current state
        command = {
            "timestamp": asyncio.get_event_loop().time(),
            "environment_state": current_environment_state,
            "context": context
        }
        
        return [
            types.TextContent(
                type="text",
                text=json.dumps(command, indent=2)
            )
        ]
    
    else:
        raise ValueError(f"Unknown tool: {name}")


async def main():
    """Main entry point for the MCP server"""
    # Run the server using stdin/stdout streams
    async with mcp.server.stdio.stdio_server() as (read_stream, write_stream):
        await server.run(
            read_stream,
            write_stream,
            InitializationOptions(
                server_name="unity-agent-mcp",
                server_version="0.1.0",
                capabilities=server.get_capabilities(
                    notification_options=NotificationOptions(),
                    experimental_capabilities={},
                ),
            ),
        )


if __name__ == "__main__":
    asyncio.run(main())
