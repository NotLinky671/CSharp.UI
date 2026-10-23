import { spawn } from 'node:child_process';
import readline from 'node:readline';

// Deliberately limited to connectivity and read-only delegation checks.
const args = process.argv.slice(2);
const inspect = args.includes('--inspect');
const smoke = inspect || args.includes('--smoke');
const executable = process.platform === 'win32' ? 'devin.exe' : 'devin';
const child = spawn(executable, ['acp'], {
  cwd: process.cwd(), windowsHide: true, stdio: ['pipe', 'pipe', 'pipe'],
});
const pending = new Map();
let sequence = 0;
let sessionId;
let assistantText = '';
let toolCalls = 0;
let deniedPermissions = 0;
let stderrBytes = 0;
let finished = false;
let stage = 'initialize';
let result = {};
const exited = new Promise(resolve => child.once('close', (code, signal) => {
  finished = true;
  for (const item of pending.values()) {
    clearTimeout(item.timer);
    item.reject(new Error('ACP process exited before responding'));
  }
  pending.clear();
  resolve({ code, signal });
}));
child.on('error', () => {});
child.stdin.on('error', () => {});
child.stderr.on('data', data => { stderrBytes += data.length; });
function send(message) { child.stdin.write(JSON.stringify(message) + '\n'); }
readline.createInterface({ input: child.stdout }).on('line', line => {
  let message;
  try { message = JSON.parse(line); } catch { return; }
  if (message.method === 'session/update' && message.params?.sessionId === sessionId) {
    const update = message.params.update;
    if (update?.sessionUpdate === 'agent_message_chunk' && update.content?.type === 'text')
      assistantText += update.content.text;
    if (update?.sessionUpdate === 'tool_call') toolCalls++;
  }
  if (message.method && Object.hasOwn(message, 'id')) {
    if (message.method === 'session/request_permission') {
      deniedPermissions++;
      send({ jsonrpc: '2.0', id: message.id, result: { outcome: { outcome: 'cancelled' } } });
    } else send({ jsonrpc: '2.0', id: message.id,
      error: { code: -32601, message: 'Client operation not implemented' } });
    return;
  }
  const item = pending.get(message.id);
  if (!item) return;
  pending.delete(message.id);
  clearTimeout(item.timer);
  if (message.error) item.reject(new Error(`RPC ${item.method}: ${message.error.code} ${message.error.message}`));
  else item.resolve(message.result);
});
function request(method, params, timeout = 60000) {
  return new Promise((resolve, reject) => {
    const id = ++sequence;
    const timer = setTimeout(() => { pending.delete(id); reject(new Error(`${method} timed out`)); }, timeout);
    pending.set(id, { resolve, reject, timer, method });
    send({ jsonrpc: '2.0', id, method, params });
  });
}
async function waitExit(ms) {
  let timer;
  try { return await Promise.race([exited, new Promise(resolve => { timer = setTimeout(() => resolve(null), ms); })]); }
  finally { clearTimeout(timer); }
}
try {
  const init = await request(stage, { protocolVersion: 1, clientCapabilities: {},
    clientInfo: { name: 'codex-devin-acp-check', version: '0.1.0' } });
  if (init.protocolVersion !== 1) throw new Error('ACP v1 not negotiated');
  console.log(JSON.stringify({ stage, protocolVersion: init.protocolVersion,
    agentInfo: init.agentInfo, agentCapabilities: init.agentCapabilities }));
  result = { ok: true, createdSessions: 0 };
  if (smoke) {
    stage = 'session/new';
    const session = await request(stage, { cwd: process.cwd(), mcpServers: [] });
    if (typeof session.sessionId !== 'string') throw new Error('Missing session id');
    sessionId = session.sessionId;
    console.log(JSON.stringify({ stage, sessionId, modes: session.modes,
      configOptions: session.configOptions?.map(({ id, currentValue }) => ({ id, currentValue })) }));
    if (inspect) await request('session/set_mode', { sessionId, modeId: 'ask' });
    stage = 'session/prompt';
    const prompt = inspect
      ? 'This is a read-only handoff feasibility check from Codex. Read AGENTS.md, README.md, docs/task/README.md and docs/task/07-windows-ime-and-host-routing.md using read-only tools. In Chinese, report the next pending task, its dependencies, 3 implementation steps and required tests, and confirm the implementer must wait for Codex review before committing/pushing. Do not implement anything, edit files, run shell commands, use subagents, commit or push. Do not claim tests passed. If you cannot read files, explicitly say so.'
      : 'Connectivity test only. Do not call tools, read or write files, execute commands, or create commits. Reply exactly ACP_OK.';
    const response = await request(stage, { sessionId, prompt: [{ type: 'text', text: prompt }] }, 180000);
    if (response.stopReason !== 'end_turn') throw new Error(`Unexpected stop reason: ${response.stopReason}`);
    if (!inspect && assistantText.trim() !== 'ACP_OK') throw new Error('Acknowledgment mismatch');
    result = { ok: true, sessionId, createdSessions: 1, stopReason: response.stopReason,
      assistantText: assistantText.trim(), toolCalls, deniedPermissions };
  }
} catch (error) {
  process.exitCode = 1;
  result = { ok: false, stage, reason: error.message, stderrBytes, toolCalls, deniedPermissions };
} finally {
  if (sessionId && !finished) {
    try {
      await request('session/close', { sessionId }, 10000);
      result.ownedSessionClosed = true;
    } catch { result.ownedSessionClosed = false; }
  }
  if (!finished) child.stdin.end();
  let exit = await waitExit(6000);
  const forcedTermination = !exit;
  if (!exit) { child.kill(); exit = await waitExit(3000); }
  if (!exit) {
    process.exitCode = 1;
    child.stdout.destroy(); child.stderr.destroy(); child.unref();
  }
  console.log(JSON.stringify({ ...result, serverExitCode: exit?.code, forcedTermination,
    cleanupConfirmed: Boolean(exit) }));
}
