// Read-only diagnostics for the shell and smoke checks. This never calls a model
// or executes an Excel tool. /mcp first waits for Pi's native MCP initialization.
export default function apertureStatus(pi) {
  pi.registerCommand("aperture-status", {
    description: "Show the tools actually loaded in this Aperture session",
    handler: async (_args, ctx) => {
      const excel = pi.getAllTools().filter(t => t.name.startsWith("mcp__aperture_excel__"));
      ctx.ui.notify("APERTURE_STATUS " + JSON.stringify({
        activeTools: pi.getActiveTools(),
        excelTools: excel.map(t => t.name),
        // Tool definitions as registered in Pi, for run records (BenchHost harness_info).
        excelToolDefinitions: excel.map(t => ({ name: t.name, description: t.description, parameters: t.parameters }))
      }), "info");
    }
  });
}
