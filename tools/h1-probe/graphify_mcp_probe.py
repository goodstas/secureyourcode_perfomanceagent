"""H1 probe step 3: start Graphify's MCP server over stdio on a graph, list its tools, and call some of them.

Run with the Graphify venv interpreter:
  <StateRoot>/graphify-venv/Scripts/python.exe tools/h1-probe/graphify_mcp_probe.py <graph.json> [--calls calls.json]
where calls.json is a list of [tool_name, {arguments}] pairs.
"""
from __future__ import annotations

import argparse
import asyncio
import json
import sys

from mcp import ClientSession, StdioServerParameters
from mcp.client.stdio import stdio_client


async def probe(graph: str, calls: list[tuple[str, dict]]) -> int:
    params = StdioServerParameters(command=sys.executable, args=["-m", "graphify.serve", graph])
    async with stdio_client(params) as (read, write):
        async with ClientSession(read, write) as session:
            await session.initialize()
            tools = (await session.list_tools()).tools
            print(f"tools ({len(tools)}): {json.dumps([t.name for t in tools])}")
            for tool in tools:
                schema = tool.input_schema or {}
                required = schema.get("required", [])
                props = ", ".join(f"{k}{'*' if k in required else ''}" for k in schema.get("properties", {}))
                print(f"  {tool.name}({props})")
            failures = 0
            for name, arguments in calls:
                result = await session.call_tool(name, arguments)
                text = " ".join(c.text for c in result.content if getattr(c, "type", "") == "text")
                status = "ERROR" if result.is_error else "ok"
                failures += bool(result.is_error)
                print(f"call {name}({json.dumps(arguments)}) -> {status}: {text[:400]!r}")
            return failures


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("graph")
    parser.add_argument("--calls", help="JSON file with a list of [tool_name, {arguments}] pairs")
    args = parser.parse_args()
    calls = []
    if args.calls:
        with open(args.calls, encoding="utf-8") as f:
            calls = [(name, arguments) for name, arguments in json.load(f)]
    sys.exit(1 if asyncio.run(probe(args.graph, calls)) else 0)


if __name__ == "__main__":
    main()
