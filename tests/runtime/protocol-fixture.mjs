import { spawn } from "node:child_process";
const child = spawn(process.execPath, ["-e", "setInterval(() => {}, 1000)"], { stdio: "ignore" });
let buffer = "";
function send(record) { process.stdout.write(JSON.stringify(record) + "\n"); }
process.stdin.setEncoding("utf8");
process.stdin.on("data", chunk => {
  buffer += chunk;
  while (buffer.includes("\n")) {
    const index = buffer.indexOf("\n");
    const line = buffer.slice(0, index); buffer = buffer.slice(index + 1);
    if (!line) continue;
    const command = JSON.parse(line);
    if (command.type === "get_state") {
      setTimeout(() => send({type:"response", id:command.id, success:true, command:command.type,
        data:{tag:command.tag, childPid:child.pid, thinkingLevel:"high"}}), command.delay ?? 0);
    } else if (command.type === "get_messages") {
      send({type:"response",id:command.id,success:false,command:command.type,error:"fixture rejection"});
    } else if (command.type === "prompt") {
      if (command.message === "timeout") continue;
      const event = JSON.stringify({type:"message_update",assistantMessageEvent:{type:"text_delta",delta:"Привет\u2028мир\u2029!"}}) + "\n";
      process.stdout.write(event.slice(0, 17));
      setTimeout(() => {
        process.stdout.write(event.slice(17));
        send({type:"response",id:command.id,success:true,command:command.type,data:{disposition:"started"}});
        send({type:"agent_settled"});
      }, 30);
    } else send({type:"response",id:command.id,success:true,command:command.type});
  }
});
process.stdin.on("end", () => process.exit(0));
