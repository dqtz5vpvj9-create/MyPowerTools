import { execFile, spawn, type ChildProcessWithoutNullStreams } from 'node:child_process';
import { promisify } from 'node:util';
import { createInterface } from 'node:readline';
import { mkdtemp } from 'node:fs/promises';
import { expect } from 'e2e';
import type { Locator, Screen } from 'e2e/engine';

export const serial = process.env.MPT_E2E_SERIAL ?? '10.33.0.155:5555';
export const packageName = process.env.MPT_E2E_PACKAGE ?? 'com.mypowertools.android';
const execute = promisify(execFile);
export async function adb(...args: string[]) {
  return (await execute('adb', ['-s', serial, ...args], { timeout: 30_000, maxBuffer: 8 * 1024 * 1024 })).stdout;
}

// Never trust the engine's remembered bundle id to prove what is on screen.
export async function assertForeground() {
  const activities = await adb('shell', 'dumpsys', 'activity', 'activities');
  const resumed = activities.split('\n').filter(line => /(?:topResumedActivity|mResumedActivity)=/.test(line));
  expect(resumed.some(line => line.includes(packageName + '/'))).toBe(true);
}

export async function tap(screen: Screen, target: Locator) {
  await assertForeground();
  const offset = Number(process.env.MPT_E2E_TOUCH_OFFSET_Y ?? '0');
  if (!offset) return target.tap();
  // Explicit diagnostic workaround only. The semantic-click case never uses it.
  const box = await target.boundingBox();
  if (!box) throw new Error('The visible control has no bounds');
  await screen.tapAt({ x: box.x + box.width / 2, y: box.y + box.height / 2 + offset });
}

export async function phoneState() {
  await assertForeground();
  return JSON.parse(await adb('shell', 'run-as', packageName, 'cat',
    'files/MyPowerTools/state/modules/file-transfer/data/assistant/assistant.json'));
}

export async function waitUntil<T>(read: () => Promise<T>, accepted: (value: T) => boolean, timeout = 60_000): Promise<T> {
  const end = Date.now() + timeout;
  do {
    const value = await read();
    if (accepted(value)) return value;
    await new Promise(resolve => setTimeout(resolve, 1000));
  } while (Date.now() < end);
  throw new Error('The expected transfer state did not arrive before the deadline');
}

export class Peer {
  root = '';
  private process?: ChildProcessWithoutNullStreams;
  private lines?: AsyncIterator<string>;
  async start() {
    this.root = await mkdtemp('/mnt/cache/data-cache/mpt-mobile-e2e-peer-');
    this.process = spawn(process.env.MPT_DOTNET ?? '/home/chris/.dotnet/dotnet',
      ['tests/e2e/MobilePeer/bin/Debug/net10.0/MobilePeer.dll', this.root], { windowsHide: true });
    this.lines = createInterface({ input: this.process.stdout })[Symbol.asyncIterator]();
    // Module diagnostics stay private; report only whether the peer exited.
    this.process.stderr.resume();
  }
  async call(name: string, args = {}) {
    this.process!.stdin.write(JSON.stringify({ name, args }) + '\n');
    const answer = await this.lines!.next();
    if (answer.done) throw new Error('The production transfer peer exited');
    const result = JSON.parse(answer.value);
    if (!result.ok) throw new Error('Production peer command failed: ' + name);
    return result.data;
  }
  async stop() { this.process?.stdin.end(); }
}
