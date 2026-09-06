# Third-party components

Release dependencies are listed in each component's `sbom.json`; exact Windows wheels are pinned in `revit-mcp/requirements.lock`. The runtime includes CPython's license and dependency metadata/licenses under `runtime/Lib/site-packages`.

- **CPython 3.12.10** embedded Windows runtime: Python Software Foundation license, included in the runtime.
- **MCP Python SDK and dependencies**: licenses retained in each wheel's `.dist-info` directory. No telemetry exporter is configured by this bridge.
- **Microsoft.CodeAnalysis C# compiler 4.11.0**: MIT; compiler distribution notices are included under `licenses/`.
- **Lucide/Feather ribbon icons**: ISC/MIT; full notices in `licenses/LUCIDE.txt`.
- **Autodesk Revit API** and **pyRevit/IronPython**: installed host prerequisites. Autodesk binaries and pyRevit are not redistributed in this ZIP.

See [Lucide's license](https://github.com/lucide-icons/lucide/blob/main/LICENSE) and [Roslyn's license](https://github.com/dotnet/roslyn/blob/main/License.txt). These notices do not assign a new license to the repository's own code.
