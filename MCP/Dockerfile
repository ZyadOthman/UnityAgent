# Unity Agent MCP Server Dockerfile
FROM python:3.11-slim

# Set working directory
WORKDIR /app

# Install system dependencies
RUN apt-get update && apt-get install -y \
    curl \
    && rm -rf /var/lib/apt/lists/*

# Copy project files
COPY pyproject.toml .
COPY unity_agent_mcp ./unity_agent_mcp

# Install Python dependencies
RUN pip install --no-cache-dir -e .

# Expose FastAPI port
EXPOSE 8000

# Environment variables (can be overridden)
ENV VLLM_HOST=http://vllm:8080
ENV VLLM_MODEL=Qwen/Qwen2.5-1.5B-Instruct

# Health check
HEALTHCHECK --interval=30s --timeout=10s --start-period=5s --retries=3 \
    CMD curl -f http://localhost:8000/health || exit 1

# Run the server
CMD ["python", "-m", "unity_agent_mcp.http_server"]
