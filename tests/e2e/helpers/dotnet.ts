import { mkdir, readFile } from 'node:fs/promises';
import { randomUUID } from 'node:crypto';
import { resolve } from 'node:path';
import { repositoryRoot, runProcess } from './process.ts';

export async function runDotnetTests(filter: string, options: { project?: string; minimumTests?: number; timeout?: number } = {}) {
  const directory = resolve(repositoryRoot, '.e2e/dotnet', randomUUID());
  await mkdir(directory, { recursive: true });
  const result = await runProcess('dotnet', [
    'test', options.project ?? 'src/MyPowerTools.Tests/MyPowerTools.Tests.csproj',
    '--no-build', '--no-restore', '-c', process.env.MPT_E2E_CONFIGURATION ?? 'Debug',
    '--nologo', '--filter', filter, '--logger', 'trx;LogFileName=results.trx',
    '--results-directory', directory,
  ], { timeout: options.timeout ?? 150_000 });
  if (result.exitCode !== 0) throw new Error(`Workflow regression failed (${result.exitCode})\n${result.stdout}\n${result.stderr}`);
  const trx = await readFile(resolve(directory, 'results.trx'), 'utf8');
  const counters = trx.match(/<Counters\s+([^>]+)\/?\s*>/);
  if (!counters) throw new Error(`Missing TRX counters: ${directory}`);
  const attributes = Object.fromEntries([...counters[1].matchAll(/([\w]+)="(\d+)"/g)].map(match => [match[1], Number(match[2])]));
  const minimum = options.minimumTests ?? 1;
  if ((attributes.total ?? 0) < minimum || attributes.executed !== attributes.total || attributes.passed !== attributes.total) {
    throw new Error(`Expected at least ${minimum} executed, passing regressions with no skips; TRX counters: ${JSON.stringify(attributes)}\n${result.stdout}`);
  }
  return { ...result, counters: attributes, trxPath: resolve(directory, 'results.trx') };
}
