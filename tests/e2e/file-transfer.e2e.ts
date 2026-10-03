import { access } from 'node:fs/promises';
import { resolve } from 'node:path';
import { test } from 'e2e';
import { repositoryRoot } from './helpers/process.ts';

test('File Transfer workflow integration prerequisite', { tags: ['file-transfer'] }, async () => {
  const root = resolve(repositoryRoot, 'tools/file-transfer');
  try {
    await access(root);
  } catch {
    test.skip('tools/file-transfer is absent from the main tools tree; external import is pending. No File Transfer workflow has been verified.');
  }
  throw new Error('File Transfer is now present: replace this prerequisite with real loopback relay/runtime workflow tests before enabling this integration.');
});
