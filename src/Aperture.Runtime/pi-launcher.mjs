import { pathToFileURL } from "node:url";

// Do not import Pi (or let it spawn MCP children) before the parent assigns our
// process to its Windows kill-on-close job. The single LF is not an RPC record.
process.stdin.pause();
process.stdin.once("readable", async () => {
  const gate = process.stdin.read(1);
  if (!gate || gate[0] !== 10) {
    process.stderr.write("Aperture process gate failed.\n");
    process.exit(1);
  }
  process.stdin.pause();
  const cli = process.argv[2];
  process.argv = [process.execPath, cli, ...process.argv.slice(3)];
  try {
    await import(pathToFileURL(cli).href);
  } catch (error) {
    process.stderr.write(String(error?.stack ?? error) + "\n");
    process.exitCode = 1;
  }
});
process.stdin.once("end", () => { process.exitCode ??= 0; });
