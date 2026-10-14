import { pathToFileURL } from "node:url";

// Do not import Pi (or let it spawn MCP children) before the parent assigns our
// process to its Windows kill-on-close job. The single LF is not an RPC record.
// .NET Framework's Process.StandardInput first writes the console input encoding's
// preamble, so a UTF-8 BOM (code page 65001) may precede the gate.
const BOM = [0xef, 0xbb, 0xbf];
let bomBytes = 0;
process.stdin.pause();
process.stdin.on("readable", function gate() {
  let chunk;
  while ((chunk = process.stdin.read(1)) !== null) {
    const byte = chunk[0];
    if (bomBytes < BOM.length && byte === BOM[bomBytes]) { bomBytes++; continue; }
    if (byte !== 10 || (bomBytes !== 0 && bomBytes !== BOM.length)) {
      process.stderr.write("Aperture process gate failed.\n");
      process.exit(1);
    }
    process.stdin.off("readable", gate);
    process.stdin.pause();
    void startPi();
    return;
  }
});
process.stdin.once("end", () => { process.exitCode ??= 0; });

async function startPi() {
  const cli = process.argv[2];
  process.argv = [process.execPath, cli, ...process.argv.slice(3)];
  try {
    await import(pathToFileURL(cli).href);
  } catch (error) {
    process.stderr.write(String(error?.stack ?? error) + "\n");
    process.exitCode = 1;
  }
}
