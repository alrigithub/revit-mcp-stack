from __future__ import annotations

import argparse
import asyncio
import os

from mcp import ClientSession, StdioServerParameters
from mcp.client.stdio import stdio_client


async def smoke(python: str, source: str) -> None:
    environment = dict(os.environ)
    parameters = StdioServerParameters(command=python, args=["-B", "-c",
        "import runpy,sys;sys.path.insert(0,sys.argv[1]);runpy.run_module('revit_mcp.server',run_name='__main__')", source], env=environment)
    async with stdio_client(parameters) as (reader, writer):
        async with ClientSession(reader, writer) as session:
            await session.initialize()
            result = await session.list_tools()
            names = sorted(tool.name for tool in result.tools)
            required = {"list_revit_instances", "run_python", "run_csharp", "execute_batch", "get_logs_tail", "capture_model", "get_request_status"}
            missing = required.difference(names)
            if missing:
                raise RuntimeError("missing tools: " + ", ".join(sorted(missing)))
            print("STDIO PASS tools=%d" % len(names))


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--python", required=True)
    parser.add_argument("--source", required=True)
    args = parser.parse_args()
    asyncio.run(smoke(args.python, args.source))


if __name__ == "__main__":
    main()
