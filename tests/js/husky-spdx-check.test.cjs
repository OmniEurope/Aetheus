// SPDX-License-Identifier: EUPL-1.2
// The pre-commit spdx-check task counted missing headers in $script:missing but tested its own local
// $missing, which stayed 0: it printed "Missing SPDX" and let the commit through (HUSKY-001). The
// task's command is read from .husky/task-runner.json and run for real under PowerShell, in a scratch
// tree, exactly as Husky runs it.
const test = require('node:test');
const assert = require('node:assert/strict');
const { spawnSync } = require('node:child_process');
const { mkdirSync, mkdtempSync, rmSync, writeFileSync } = require('node:fs');
const { tmpdir } = require('node:os');
const path = require('node:path');

const taskRunner = require(path.resolve(__dirname, '../../.husky/task-runner.json'));
const task = taskRunner.tasks.find(candidate => candidate.name === 'spdx-check');
const pwsh = spawnSync('pwsh', ['-NoProfile', '-Command', 'exit 0']);
const skip = pwsh.error ? 'PowerShell 7 (pwsh) is not installed' : false;

function runCheck(files) {
    const root = mkdtempSync(path.join(tmpdir(), 'aetheus-spdx-'));
    try {
        mkdirSync(path.join(root, 'src'));
        mkdirSync(path.join(root, 'tests'));
        for (const [name, content] of Object.entries(files)) writeFileSync(path.join(root, name), content);
        return spawnSync(task.command, task.args, { cwd: root, encoding: 'utf8' });
    } finally {
        rmSync(root, { recursive: true, force: true });
    }
}

test('a source file without the SPDX header fails the commit', { skip }, () => {
    const result = runCheck({
        'src/WithHeader.cs': '// SPDX-License-Identifier: EUPL-1.2\nclass A { }\n',
        'src/WithoutHeader.cs': 'class B { }\n'
    });
    assert.notEqual(result.status, 0, result.stdout);
    assert.match(result.stdout, /Missing SPDX: .*WithoutHeader\.cs/);
});

test('sources that all carry the SPDX header pass', { skip }, () => {
    const result = runCheck({
        'src/WithHeader.cs': '// SPDX-License-Identifier: EUPL-1.2\nclass A { }\n',
        'tests/Component.razor': '@* SPDX-License-Identifier: EUPL-1.2 *@\n<p></p>\n'
    });
    assert.equal(result.status, 0, result.stdout + result.stderr);
});
