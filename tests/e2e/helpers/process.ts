import { spawn } from 'node:child_process';
import { resolve } from 'node:path';

export const repositoryRoot = resolve(import.meta.dirname, '../../..');

export async function runProcess(executable: string, args: string[], options: { cwd?: string; timeout?: number; env?: NodeJS.ProcessEnv } = {}) {
  return await new Promise<{ exitCode: number; stdout: string; stderr: string }>((done, reject) => {
    const child = spawn(executable, args, {
      cwd: options.cwd ?? repositoryRoot,
      env: { ...process.env, ...options.env },
      windowsHide: true,
      shell: false,
      stdio: ['ignore', 'pipe', 'pipe'],
    });
    let stdout = '';
    let stderr = '';
    child.stdout.on('data', chunk => { stdout += chunk; });
    child.stderr.on('data', chunk => { stderr += chunk; });
    const timer = setTimeout(() => {
      // taskkill terminates descendants too, including a stuck testhost.
      if (process.platform === 'win32' && child.pid) {
        const killer = spawn('taskkill.exe', ['/PID', String(child.pid), '/T', '/F'], { windowsHide: true, shell: false, stdio: 'ignore' });
        killer.on('error', () => child.kill());
      } else child.kill('SIGKILL');
      reject(new Error(`${executable} timed out after ${options.timeout ?? 120_000}ms\n${stdout}\n${stderr}`));
    }, options.timeout ?? 120_000);
    child.on('error', error => { clearTimeout(timer); reject(error); });
    child.on('close', code => { clearTimeout(timer); done({ exitCode: code ?? -1, stdout, stderr }); });
  });
}
