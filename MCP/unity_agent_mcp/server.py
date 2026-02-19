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
    "nearby_objects": []
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
        
        prompt = f"""You are controlling a Unity agent. Based on the current environment state, decide which action the agent should take.

Current Environment State:
{env_state_str}

Available Actions:
{', '.join(available_actions)}

Goal: {goal_description}

Analyze the situation and respond with a JSON object containing:
1. "action" - one of: {', '.join(available_actions)}
2. "target" - STRING name of the GameObject to interact with (e.g., "Goal", "Obstacle1") or null if not applicable
3. "reasoning" - brief explanation

IMPORTANT: 
- The "target" field must be a STRING (object name), NOT a position or coordinates
- If using MoveTo, find the nearest relevant object name from nearby_objects
- Respond ONLY with valid JSON, no markdown formatting

Example response:
{{
    "action": "MoveTo",
    "target": "Goal",
    "reasoning": "Agent should move towards the goal"
}}
"""
        
        # Use LLM to decide (vLLM with OpenAI-compatible API)
        try:
            async with httpx.AsyncClient(timeout=60.0) as client:
                response = await client.post(
                    f"{VLLM_HOST}/v1/chat/completions",
                    json={
                        "model": VLLM_MODEL,
                        "messages": [
                            {"role": "system", "content": "You are an AI agent controller. Always respond with valid JSON only."},
                            {"role": "user", "content": prompt}
                        ],
                        "temperature": 0.1,
                        "max_tokens": 256
                    }
                )
                
                if response.status_code == 200:
                    result = response.json()
                    response_text = result["choices"][0]["message"]["content"]
                    
                    return [
                        types.TextContent(
                            type="text",
                            text=f"LLM Decision:\n{response_text}"
                        )
                    ]
                else:
                    print(f"vLLM error: {response.status_code} - {response.text}")
                    raise Exception(f"vLLM returned status {response.status_code}")
                    
        except Exception as e:
            print(f"Error calling vLLM: {str(e)}")
            print("Falling back to rule-based decision...")
        
        # Fallback rule-based logic if no LLM available
        distance = current_environment_state.get("distance_to_goal", 0)
        is_moving = current_environment_state.get("is_moving", False)
        
        if distance > 1.0 and not is_moving:
            decision = {
                "action": "MoveTo",
                "target": "Goal",
                "reasoning": "Agent is far from goal and not moving"
            }
        elif distance <= 1.0:
            decision = {
                "action": "Stop",
                "target": None,
                "reasoning": "Agent has reached the goal"
            }
        else:
            decision = {
                "action": "Resume",
                "target": None,
                "reasoning": "Continue moving to goal"
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
