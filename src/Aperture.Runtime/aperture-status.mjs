// Read-only diagnostics for the shell and smoke checks. This never calls a model
// or executes an Excel tool. /mcp first waits for Pi's native MCP initialization.
export default function apertureStatus(pi) {
  pi.registerCommand("aperture-status", {
    description: "Show the tools actually loaded in this Aperture session",
    handler: async (_args, ctx) => {
      ctx.ui.notify("APERTURE_STATUS " + JSON.stringify({
        activeTools: pi.getActiveTools(),
        excelTools: pi.getAllTools().filter(t => t.name.startsWith("mcp__aperture_excel__")).map(t => t.name)
      }), "info");
    }
  });
}
