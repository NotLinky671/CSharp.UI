import { spawn } from 'node:child_process';
import readline from 'node:readline';
import path from 'node:path';

const argv = process.argv.slice(2);
if (argv.includes('--help')) {
  console.log('Usage: node check-connection.mjs [--install-dir DIRECTORY] [--workspace DIRECTORY] [--timeout-ms NUMBER] [--smoke|--list-workspaces]');
  console.log('Default: initialize and workspace-filtered session/list only. --smoke creates one owned session, asks for ACP_OK without tools, then closes it.');
  process.exit(0);
}
function option(name, fallback) {
  const i = argv.indexOf(name);
  return i < 0 ? fallback : argv[i + 1];
}
const installDir = path.resolve(option('--install-dir', 'C:/Users/nothh/AppData/Local/Programs/DeepSeek Harness'));
const workspace = path.resolve(option('--workspace', process.cwd()));
const timeoutMs = Number(option('--timeout-ms', '25000'));
const smoke = argv.includes('--smoke');
const listWorkspaces = argv.includes('--list-workspaces');
if (smoke && listWorkspaces) throw new Error('Workspace listing cannot be combined with the smoke task');
if (!Number.isFinite(timeoutMs) || timeoutMs < 100 || timeoutMs > 60000) throw new Error('Invalid timeout');
const executable = path.join(installDir, 'DeepSeek Harness.exe');
const entry = path.join(installDir, 'resources/app.asar/dsh/node_modules/@deepseek-ai/dsh-desktop-host/lib/cli.js');
const child = spawn(executable, ['--expose-internals', entry, '--profile', 'acp'], {
  cwd: workspace,
  env: { ...process.env, ELECTRON_RUN_AS_NODE: '1' },
  windowsHide: true,
  stdio: ['pipe', 'pipe', 'pipe'],
});
let sequence = 0;
let stderrBytes = 0;
let stderrTail = '';
let nonProtocolLines = 0;
let finished = false;
let spawnError;
let ownedSessionId;
let ownedSessionClosed = false;
let assistantText = '';
const pending = new Map();
const exited = new Promise(resolve => {
  child.once('close', (code, signal) => {
    finished = true;
    for (const entry of pending.values()) {
      clearTimeout(entry.timer);
      entry.reject(new Error('Server process closed before the response'));
    }
    pending.clear();
    resolve({ code, signal });
  });
});
child.on('error', error => { spawnError = error.code ?? 'SPAWN_ERROR'; });
child.stdin.on('error', () => {});
child.stderr.on('data', data => {
  stderrBytes += data.length;
  stderrTail = (stderrTail + data.toString('utf8')).slice(-32000);
});
function send(message) {
  if (child.stdin.destroyed || child.stdin.writableEnded) throw new Error('Protocol stdin is closed');
  child.stdin.write(JSON.stringify(message) + '\n');
}
readline.createInterface({ input: child.stdout, crlfDelay: Infinity }).on('line', line => {
  let message;
  try { message = JSON.parse(line); }
  catch { nonProtocolLines++; return; }
  if (message?.jsonrpc !== '2.0') { nonProtocolLines++; return; }
  if (message.method === 'session/update' && message.params?.sessionId === ownedSessionId) {
    const update = message.params.update;
    if (update?.sessionUpdate === 'agent_message_chunk' && update.content?.type === 'text') {
      assistantText = (assistantText + update.content.text).slice(0, 8192);
    }
    return;
  }
  if (typeof message.method === 'string' && Object.hasOwn(message, 'id')) {
    if (message.method === 'session/request_permission') {
      send({ jsonrpc: '2.0', id: message.id, result: { outcome: { outcome: 'cancelled' } } });
    } else {
      send({ jsonrpc: '2.0', id: message.id, error: { code: -32601, message: 'Unsupported client operation' } });
    }
    return;
  }
  const operation = pending.get(message.id);
  if (!operation) return;
  clearTimeout(operation.timer);
  pending.delete(message.id);
  if (Object.hasOwn(message, 'error')) {
    operation.reject(new Error(`RPC ${operation.method} failed with code ${message.error?.code ?? 'unknown'}`));
  } else if (Object.hasOwn(message, 'result')) operation.resolve(message.result);
  else operation.reject(new Error('Malformed RPC response'));
});
function request(method, params) {
  if (finished || spawnError) return Promise.reject(new Error('Server is unavailable'));
  return new Promise((resolve, reject) => {
    const id = ++sequence;
    const timer = setTimeout(() => {
      pending.delete(id);
      reject(new Error(`RPC ${method} timed out`));
    }, timeoutMs);
    pending.set(id, { method, resolve, reject, timer });
    try { send({ jsonrpc: '2.0', id, method, params }); }
    catch (error) { clearTimeout(timer); pending.delete(id); reject(error); }
  });
}
async function waitForExit(milliseconds) {
  let timer;
  try {
    return await Promise.race([exited, new Promise(resolve => { timer = setTimeout(() => resolve(null), milliseconds); })]);
  } finally { clearTimeout(timer); }
}
let stage = 'initialize';
let result;
try {
  const initialization = await request('initialize', {
    protocolVersion: 1,
    clientCapabilities: {},
    clientInfo: { name: 'codex-local-acp-check', version: '0.1.0' },
  });
  if (initialization?.protocolVersion !== 1) throw new Error('ACP v1 was not negotiated');
  console.log(JSON.stringify({ stage, ok: true, protocolVersion: initialization.protocolVersion,
    agentInfo: initialization.agentInfo, agentCapabilities: initialization.agentCapabilities }));
  stage = 'session/list';
  if (listWorkspaces) {
    const directories = new Map();
    const sessionIds = new Set();
    const cursors = new Set();
    let cursor;
    let pages = 0;
    let missingWorkspace = 0;
    do {
      if (++pages > 1000) throw new Error('Session listing exceeded the pagination limit');
      const listing = await request(stage, cursor ? { cursor } : {});
      if (!Array.isArray(listing?.sessions)) throw new Error('Malformed session listing');
      for (const session of listing.sessions) {
        if (typeof session.sessionId !== 'string') throw new Error('Malformed session identity');
        if (sessionIds.has(session.sessionId)) continue;
        sessionIds.add(session.sessionId);
        if (typeof session.cwd !== 'string' || !session.cwd) { missingWorkspace++; continue; }
        const normalized = session.cwd.replaceAll('\\', '/').replace(/\/+$/, '');
        const key = process.platform === 'win32' ? normalized.toLowerCase() : normalized;
        const entry = directories.get(key) ?? { cwd: session.cwd, sessionCount: 0 };
        entry.sessionCount++;
        directories.set(key, entry);
      }
      cursor = listing.nextCursor;
      if (cursor && (typeof cursor !== 'string' || cursors.has(cursor))) throw new Error('Session pagination did not advance');
      if (cursor) cursors.add(cursor);
    } while (cursor);
    result = { ok: true, stage, source: 'persistent session working directories',
      workspaces: [...directories.values()].sort((a, b) => a.cwd.localeCompare(b.cwd)),
      sessionCount: sessionIds.size, missingWorkspace, pages, createdSessions: 0, submittedPrompts: 0 };
  } else {
    const listing = await request(stage, { cwd: workspace });
    if (!Array.isArray(listing?.sessions)) throw new Error('Malformed session listing');
    result = { ok: true, stage, workspace, sessionCount: listing.sessions.length,
      hasMore: Boolean(listing.nextCursor), createdSessions: 0, submittedPrompts: 0 };
  }
  if (smoke) {
    stage = 'session/new';
    const session = await request(stage, { cwd: workspace, mcpServers: [] });
    if (typeof session?.sessionId !== 'string') throw new Error('Session creation did not return an id');
    ownedSessionId = session.sessionId;
    stage = 'session/prompt';
    const response = await request(stage, {
      sessionId: ownedSessionId,
      prompt: [{ type: 'text', text: 'This is an ACP connectivity test, not a coding task. Do not invoke any tools, read files, edit files, run commands, or create commits. Reply with exactly ACP_OK and nothing else.' }],
    });
    if (response?.stopReason !== 'end_turn') throw new Error('Smoke prompt did not finish normally');
    if (assistantText.trim() !== 'ACP_OK') throw new Error('Smoke reply did not match the fixed acknowledgment');
    stage = 'session/close';
    await request(stage, { sessionId: ownedSessionId });
    ownedSessionClosed = true;
    result = { ...result, stage, createdSessions: 1, submittedPrompts: 1,
      acknowledgment: assistantText.trim(), stopReason: response.stopReason, ownedSessionClosed };
  }
} catch (error) {
  process.exitCode = 1;
  result = { ok: false, stage, reason: error.message, spawnError, stderrBytes, nonProtocolLines,
    diagnosticCategories: [...new Set(stderrTail.match(/\b(?:EACCES|EPERM|ENOENT|ENOTDIR|ERR_[A-Z_]+)\b/g) ?? [])],
    accessErrors: stderrTail.split('\n').filter(line => /\b(?:EPERM|EACCES)\b/.test(line)).map(line => line.trim().slice(0, 300)) };
} finally {
  if (ownedSessionId && !ownedSessionClosed && !finished) {
    try {
      await request('session/close', { sessionId: ownedSessionId });
      ownedSessionClosed = true;
    } catch { process.exitCode = 1; }
    result = { ...result, ownedSessionClosed };
  }
  child.stdin.end();
  let outcome = await waitForExit(6000);
  let forcedTermination = false;
  if (!outcome) {
    forcedTermination = true;
    child.kill();
    outcome = await waitForExit(3000);
  }
  if (!outcome) {
    process.exitCode = 1;
    result = { ...result, ok: false, cleanup: 'process exit not confirmed' };
    child.stdout.destroy();
    child.stderr.destroy();
    child.unref();
  } else result = { ...result, cleanup: 'process exited', serverExitCode: outcome.code,
    serverExitSignal: outcome.signal, forcedTermination };
}
console.log(JSON.stringify(result));
