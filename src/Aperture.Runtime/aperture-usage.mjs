// Observation only, loaded when a host records runs (BenchHost). Reports the raw usage
// block a provider attached to its stream, e.g. the billed usage.cost and the upstream
// provider of OpenRouter-style aggregators. Pi prices tokens from the configured model
// rates and drops these fields. Nothing is changed, persisted or sent to the model.
export default function apertureUsage(pi) {
  pi.on("provider_stream_event", (event, ctx) => {
    const data = event.data;
    if (!data || typeof data !== "object" || !data.usage || typeof data.usage !== "object") return;
    ctx.ui.notify("APERTURE_PROVIDER_USAGE " + JSON.stringify({
      id: typeof data.id === "string" ? data.id : null,
      model: typeof data.model === "string" ? data.model : null,
      provider: typeof data.provider === "string" ? data.provider : null,
      usage: data.usage
    }), "info");
  });
}
