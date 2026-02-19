"""
FastAPI server wrapper for the MCP server
Allows Unity to communicate via HTTP instead of stdio
"""

from fastapi import FastAPI, HTTPException
from fastapi.middleware.cors import CORSMiddleware
from pydantic import BaseModel, Field
from typing import Optional, List, Dict, Any
import asyncio
import json
from unity_agent_mcp.server import (
    handle_call_tool,
    current_environment_state
)
import uvicorn

app = FastAPI(
    title="Unity Agent MCP Server",
    description="MCP server for Unity Agent with LLM decision making",
    version="0.1.0"
)

# Enable CORS for Unity requests
app.add_middleware(
    CORSMiddleware,
    allow_origins=["*"],
    allow_credentials=True,
    allow_methods=["*"],
    allow_headers=["*"],
)


# Pydantic models
class Vector3Data(BaseModel):
    x: float
    y: float
    z: float


class NearbyObject(BaseModel):
    name: str
    distance: float
    position: Vector3Data


class EnvironmentState(BaseModel):
    agent_position: Vector3Data
    goal_position: Optional[Vector3Data] = None
    distance_to_goal: float = 0.0
    is_moving: bool = False
    nearby_objects: List[NearbyObject] = []


class DecisionRequest(BaseModel):
    available_actions: List[str] = Field(default=["MoveTo", "Stop", "Resume", "SetGoal"])
    goal_description: str = Field(default="Navigate to the goal efficiently")


class CommandRequest(BaseModel):
    context: Optional[str] = None


@app.get('/health')
async def health_check():
    """Health check endpoint"""
    return {"status": "healthy", "service": "unity-agent-mcp"}


@app.post('/mcp/update')
async def update_environment(state: EnvironmentState):
    """Update environment state from Unity"""
    try:
        # Convert Pydantic model to dict
        data = state.model_dump()
        
        # Call the MCP tool
        result = await handle_call_tool("update_environment", data)
        
        return {
            "success": True,
            "message": "Environment updated",
            "result": result[0].text if result else None
        }
    except Exception as e:
        raise HTTPException(status_code=500, detail=str(e))


@app.post('/mcp/decide')
async def decide_action(request: DecisionRequest):
    """Get action decision from LLM"""
    try:
        data = request.model_dump()
        
        # Call the MCP tool
        result = await handle_call_tool("decide_agent_action", data)
        
        return {
            "success": True,
            "decision": result[0].text if result else None
        }
    except Exception as e:
        raise HTTPException(status_code=500, detail=str(e))


@app.get('/mcp/state')
async def get_state():
    """Get current environment state"""
    return current_environment_state


@app.post('/mcp/command')
async def get_command(request: CommandRequest):
    """Get agent command"""
    try:
        data = request.model_dump()
        
        result = await handle_call_tool("get_agent_command", data)
        
        return {
            "success": True,
            "command": result[0].text if result else None
        }
    except Exception as e:
        raise HTTPException(status_code=500, detail=str(e))


def run_http_server(host='0.0.0.0', port=8000):
    """Run the FastAPI server"""
    print(f"Starting Unity Agent MCP HTTP Server on {host}:{port}")
    print("Endpoints:")
    print(f"  - POST http://{host}:{port}/mcp/update - Update environment state")
    print(f"  - POST http://{host}:{port}/mcp/decide - Get action decision")
    print(f"  - GET  http://{host}:{port}/mcp/state - Get current state")
    print(f"  - POST http://{host}:{port}/mcp/command - Get agent command")
    print(f"  - GET  http://{host}:{port}/docs - API documentation")
    print(f"  - GET  http://{host}:{port}/health - Health check")
    uvicorn.run(app, host=host, port=port)


if __name__ == '__main__':
    run_http_server()
