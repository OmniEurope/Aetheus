// SPDX-License-Identifier: EUPL-1.2
// `launch-windows.ps1 -vps` reclaims the volumes its abandoned VPS-sim compose projects left behind. It used
// to take every volume whose name contained "vpssim", another project's included, and removed it with
// its containers (SCRIPTS-001). The selection function is read from scripts/launch-core.ps1 and run
// for real under PowerShell on representative names; nothing is asked of Docker.
const test = require('node:test');
const assert = require('node:assert/strict');
const { spawnSync } = require('node:child_process');
const { readFileSync } = require('node:fs');
const path = require('node:path');

const source = readFileSync(path.resolve(__dirname, '../../scripts/launch-core.ps1'), 'utf8').replace(/\r\n/g, '\n');
const definition = source.match(/^ {4}function Select-AetheusVpsSimStaleVolumes[\s\S]*?\n {4}\}\n/m)?.[0];
const pwsh = spawnSync('pwsh', ['-NoProfile', '-Command', 'exit 0']);
const skip = pwsh.error ? 'PowerShell 7 (pwsh) is not installed' : false;

function select(names, keepProject) {
    const list = names.map(name => `'${name}'`).join(',');
    const result = spawnSync('pwsh', ['-NoProfile', '-Command',
        `${definition}\n(Select-AetheusVpsSimStaleVolumes @(${list}) '${keepProject}') -join ','`], { encoding: 'utf8' });
    assert.equal(result.status, 0, result.stderr);
    return result.stdout.trim().split(',').filter(Boolean);
}

test('the launcher defines the selection the -vps sweep uses', () => {
    assert.ok(definition, 'Select-AetheusVpsSimStaleVolumes is no longer defined in launch-core.ps1');
    assert.match(source, /\$staleVolumes = @\(Select-AetheusVpsSimStaleVolumes /);
});

test('abandoned Aetheus VPS-sim volumes are reclaimed, the starting project and foreign ones are kept', { skip }, () => {
    const selected = select([
        'aetheus-vpssim-full_docker-data',
        'aetheus-vpssim-full_containerd-data',
        'aetheus-vpssim-blank_docker-data',
        'client-vpssim-data',
        'vpssim_cache',
        'other-aetheus-vpssim-full_docker-data',
        'aetheus-vpssim-full-copy',
        'Aetheus-vpssim-full_docker-data'
    ], 'aetheus-vpssim-blank');
    assert.deepEqual(selected, ['aetheus-vpssim-full_docker-data', 'aetheus-vpssim-full_containerd-data']);
});

test('nothing listed, nothing selected', { skip }, () => {
    assert.deepEqual(select([], 'aetheus-vpssim-full'), []);
});

// SCRIPTS-001, decision of 2026-10-08: another flavor's volumes hold its provisioned state, so a plain
// -vps only reports them; removing them takes the explicit -vpsclean.
test('the sweep only removes volumes behind -vpsclean', () => {
    const sweep = source.match(/\n {4}if \(\$staleVolumes\.Count -gt 0 -and -not \$VpsClean\) \{[\s\S]*?\n {4}elseif \(\$staleVolumes\.Count -gt 0\) \{[\s\S]*?docker volume rm/);
    assert.ok(sweep, 'the volume removal is no longer behind the -vpsclean branch');
    assert.doesNotMatch(sweep[0].split('elseif')[0], /docker (volume )?rm/, 'the default branch removes something');
    const entry = readFileSync(path.resolve(__dirname, '../../launch-windows.ps1'), 'utf8');
    assert.match(entry, /\n {4}\[switch\]\$VpsClean,/, 'launch-windows.ps1 no longer declares -VpsClean');
});

// PowerShell refuses the whole param block when an alias equals a parameter name, case ignored
// ([Alias("vpsclean")] on $VpsClean stopped every ylaunch call on 2026-10-08).
test('no ylaunch alias repeats a parameter name', () => {
    const entry = readFileSync(path.resolve(__dirname, '../../launch-windows.ps1'), 'utf8');
    const block = entry.match(/\nparam\(([\s\S]*?)\n\)/)[1];
    const names = [...block.matchAll(/\$(\w+)/g)].map(m => m[1].toLowerCase());
    const aliases = [...block.matchAll(/\[Alias\(([^)]*)\)\]/g)]
        .flatMap(m => [...m[1].matchAll(/"([^"]+)"/g)].map(a => a[1].toLowerCase()));
    assert.ok(names.length > 10, 'param block not found');
    assert.deepEqual(aliases.filter(alias => names.includes(alias)), []);
});
