#!/usr/bin/env node
// Shared by the Windows build and Linux cross-compilation. No dependency install is required.
import fs from 'node:fs';
import path from 'node:path';
import os from 'node:os';
import { fileURLToPath } from 'node:url';
import crypto from 'node:crypto';

const desktop = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const runtimeIdentifier = process.argv[4] || 'win-x64';
if (!['win-x64', 'win-x86'].includes(runtimeIdentifier)) throw new Error('Only win-x64 and win-x86 payloads are supported.');
const macroSuffix = runtimeIdentifier.replaceAll('-', '_');
const publish = path.resolve(process.argv[2] || path.join(desktop, 'artifacts', runtimeIdentifier));
const generated = path.resolve(process.argv[3] || path.join(desktop, 'artifacts', `installer-payload-${runtimeIdentifier}.nsh`));
if (!fs.existsSync(path.join(publish, 'STEVANplejer.exe'))) throw new Error('Publish the Windows application first.');
const nativeDirectory = fs.existsSync(path.join(publish, 'libvlc', runtimeIdentifier, 'libvlc.dll'))
    ? path.join(publish, 'libvlc', runtimeIdentifier) : publish;
if (!fs.existsSync(path.join(nativeDirectory, 'libvlc.dll'))) throw new Error('The published native libVLC runtime is missing.');

// Mutate only an explicitly owned publish directory, never a user-selected tree.
const allowedOutputRoot = fs.realpathSync(path.join(desktop, 'artifacts'));
const resolvedPublish = fs.realpathSync(publish);
if (!resolvedPublish.startsWith(allowedOutputRoot + path.sep)
    || !fs.existsSync(path.join(publish, '.stevan-generated-output'))) {
    throw new Error('Prepare packaging only inside desktop/artifacts with the .stevan-generated-output ownership marker.');
}
function removeOwnedDirectory(directory) {
    if (!fs.existsSync(directory)) return;
    if (fs.lstatSync(directory).isSymbolicLink()) throw new Error('Native output directory must not be a symbolic link.');
    fs.rmSync(directory, { recursive: true });
}

// Whisper copies every Windows architecture; LLamaSharp's x86 fallback copies
// every OS because it has no x86 backend. Ship only the selected Windows RID.
for (const architecture of ['win-x64', 'win-x86', 'win-arm64'].filter(value => value !== runtimeIdentifier))
    removeOwnedDirectory(path.join(publish, 'libvlc', architecture));
const engineRuntimes = path.join(publish, 'runtimes');
if (!fs.existsSync(engineRuntimes)) throw new Error('The published local subtitle engine runtimes are missing.');
for (const entry of fs.readdirSync(engineRuntimes)) {
    if (entry === runtimeIdentifier) continue;
    if (entry === 'noavx') {
        const noAvx = path.join(engineRuntimes, entry);
        if (fs.lstatSync(noAvx).isSymbolicLink()) throw new Error('Native output directory must not be a symbolic link.');
        for (const architecture of fs.readdirSync(noAvx))
            if (architecture !== runtimeIdentifier) removeOwnedDirectory(path.join(noAvx, architecture));
    } else removeOwnedDirectory(path.join(engineRuntimes, entry));
}
for (const file of walk(engineRuntimes))
    if (/\.(?:so(?:\.\d+)*|dylib|a|metal)$/i.test(file)) fs.unlinkSync(file);

// Runtime.NoAvx 1.8.1's x86 MSBuild target misnames its actual PE DLL.
const x86NoAvxMisnamed = path.join(engineRuntimes, 'noavx', 'win-x86', 'whisper.ggml');
if (runtimeIdentifier === 'win-x86' && fs.existsSync(x86NoAvxMisnamed)) {
    const corrected = path.join(path.dirname(x86NoAvxMisnamed), 'whisper.dll');
    if (fs.existsSync(corrected)) throw new Error('Both correct and misnamed x86 Whisper runtimes were published.');
    fs.renameSync(x86NoAvxMisnamed, corrected);
}
const whisperNames = ['whisper.dll', 'ggml-whisper.dll', 'ggml-base-whisper.dll', 'ggml-cpu-whisper.dll'];
const requiredEngineFiles = [
    ...whisperNames.map(name => path.join(engineRuntimes, runtimeIdentifier, name)),
    ...whisperNames.map(name => path.join(engineRuntimes, 'noavx', runtimeIdentifier, name))
];
if (runtimeIdentifier === 'win-x64') {
    for (const variant of ['noavx', 'avx', 'avx2', 'avx512'])
        for (const name of ['llama.dll', 'ggml.dll', 'ggml-base.dll', 'ggml-cpu.dll', 'mtmd.dll'])
            requiredEngineFiles.push(path.join(engineRuntimes, runtimeIdentifier, 'native', variant, name));
} else if (fs.existsSync(path.join(engineRuntimes, runtimeIdentifier, 'native'))) {
    throw new Error('LLamaSharp has no supported x86 native backend; the x86 payload must not contain one.');
}
for (const file of [...requiredEngineFiles, ...['Whisper.net.dll', 'LLamaSharp.dll'].map(name => path.join(publish, name))])
    if (!fs.existsSync(file)) throw new Error(`Missing local subtitle engine file: ${file}`);

// A directory/RID label alone does not prove the binaries have the right bitness.
// Inspect only native PE files, not AnyCPU managed assemblies, before packaging.
const expectedMachine = runtimeIdentifier === 'win-x64' ? 0x8664 : 0x014c;
for (const file of [
    ...['STEVANplejer.exe', 'coreclr.dll', 'wpfgfx_cor3.dll', 'PresentationNative_cor3.dll'].map(name => path.join(publish, name)),
    ...['libvlc.dll', 'libvlccore.dll'].map(name => path.join(nativeDirectory, name)),
    ...walk(engineRuntimes).filter(file => /\.dll$/i.test(file))
]) {
    const descriptor = fs.openSync(file, 'r');
    try {
        const dos = Buffer.alloc(64);
        if (fs.readSync(descriptor, dos, 0, dos.length, 0) !== dos.length || dos.readUInt16LE(0) !== 0x5a4d)
            throw new Error(`Not a Windows PE file: ${file}`);
        const offset = dos.readUInt32LE(60);
        if (offset < 64 || offset > 1024 * 1024 || offset + 6 > fs.fstatSync(descriptor).size)
            throw new Error(`Invalid Windows PE header: ${file}`);
        const pe = Buffer.alloc(6);
        if (fs.readSync(descriptor, pe, 0, pe.length, offset) !== pe.length || pe.readUInt32LE(0) !== 0x00004550)
            throw new Error(`Invalid Windows PE signature: ${file}`);
        if (pe.readUInt16LE(4) !== expectedMachine)
            throw new Error(`Native binary architecture does not match ${runtimeIdentifier}: ${file}`);
    } finally { fs.closeSync(descriptor); }
}

const notices = path.join(publish, 'licenses');
fs.mkdirSync(notices, { recursive: true });
for (const source of walkSourceLicenses(path.join(desktop, 'licenses'))) {
    fs.copyFileSync(source, path.join(notices, path.basename(source)));
}
for (const name of ['LICENSE', 'THIRD-PARTY-NOTICES.md', 'README.md', 'VALIDATION.md'])
    fs.copyFileSync(path.join(desktop, name), path.join(publish, name));

const nuget = process.env.NUGET_PACKAGES || path.join(os.homedir(), '.nuget', 'packages');
// Include all license/notice/copyright documents present in each shipped package;
// retain their directory names instead of collapsing licenses with the same filename.
const runtime = JSON.parse(fs.readFileSync(path.join(publish, 'STEVANplejer.runtimeconfig.json'), 'utf8'));
const frameworks = runtime.runtimeOptions.includedFrameworks || [];
const coreVersion = frameworks.find(item => item.name === 'Microsoft.NETCore.App')?.version;
const desktopVersion = frameworks.find(item => item.name === 'Microsoft.WindowsDesktop.App')?.version;
if (!coreVersion || !desktopVersion) throw new Error('The application must publish both .NET and WPF runtimes self-contained.');
const packages = [
    ['libvlcsharp', '3.10.1'], ['libvlcsharp.wpf', '3.10.1'],
    ['videolan.libvlc.windows', '3.0.24'], ['whisper.net', '1.8.1'],
    ['whisper.net.runtime', '1.8.1'], ['whisper.net.runtime.noavx', '1.8.1'],
    ['llamasharp', '0.27.0'], ['llamasharp.backend.cpu', '0.27.0'],
    [`microsoft.netcore.app.runtime.${runtimeIdentifier}`, coreVersion],
    [`microsoft.windowsdesktop.app.runtime.${runtimeIdentifier}`, desktopVersion]
];
// Include licenses for the actual bundled managed transitive dependencies too.
const dependencyGraph = JSON.parse(fs.readFileSync(path.join(publish, 'STEVANplejer.deps.json'), 'utf8'));
const target = dependencyGraph.targets[dependencyGraph.runtimeTarget.name];
if (!target) throw new Error('Published application dependency target is missing.');
for (const [name, item] of Object.entries(target)) {
    if (dependencyGraph.libraries[name]?.type !== 'package'
        || !['runtime', 'native', 'runtimeTargets', 'resources'].some(kind => Object.keys(item[kind] || {}).length)) continue;
    const separator = name.lastIndexOf('/');
    const id = name.slice(0, separator).toLowerCase();
    const version = name.slice(separator + 1);
    if (!packages.some(([existing]) => existing === id)) packages.push([id, version]);
}
function walk(directory) {
    return fs.readdirSync(directory, { withFileTypes: true }).flatMap(entry => {
        const full = path.join(directory, entry.name);
        if (entry.isSymbolicLink()) throw new Error(`Symlinks are not allowed in the installer payload: ${full}`);
        return entry.isDirectory() ? walk(full) : [full];
    });
}
function walkSourceLicenses(directory) {
    return fs.readdirSync(directory).map(name => path.join(directory, name)).filter(file => fs.statSync(file).isFile());
}
const inventory = [];
for (const [id, pinned] of packages) {
    const root = path.join(nuget, id);
    if (!fs.existsSync(root)) throw new Error(`Missing NuGet package cache: ${id}. Restore first or set NUGET_PACKAGES.`);
    const version = pinned;
    if (!fs.existsSync(path.join(root, version))) throw new Error(`Missing exact package cache: ${id}/${version}`);
    const directory = path.join(root, version);
    fs.mkdirSync(path.join(notices, `${id}-${version}`), { recursive: true });
    const documents = walk(directory).filter(file => /^(?:copying|license|licence|notice|thirdparty|copyright)/i.test(path.basename(file))
        && !/\.(?:dll|xml|nupkg|sha512|pdb)$/i.test(file));
    for (const file of documents) {
        const destination = path.join(notices, `${id}-${version}`, path.relative(directory, file));
        fs.mkdirSync(path.dirname(destination), { recursive: true });
        fs.copyFileSync(file, destination);
    }
    const nuspec = path.join(directory, `${id}.nuspec`);
    if (fs.existsSync(nuspec)) fs.copyFileSync(nuspec, path.join(notices, `${id}-${version}`, `${id}.nuspec`));
    const metadata = fs.existsSync(nuspec) ? fs.readFileSync(nuspec, 'utf8') : '';
    const license = /<license\s+type="([^"]+)"[^>]*>([^<]+)<\/license>/i.exec(metadata);
    inventory.push({ package: id, version,
        license: license ? { type: license[1], value: license[2] } : null,
        licenseDocuments: documents.map(file => path.relative(directory, file).replaceAll(path.sep, '/')) });
}
fs.writeFileSync(path.join(notices, 'bundled-package-inventory.json'), JSON.stringify(inventory, null, 2) + '\n');

// NSIS receives an exact ownership manifest: uninstall never recursively removes
// arbitrary files or the separate per-user settings/subtitle directory.
const files = walk(publish).filter(file => !path.basename(file).startsWith('.stevan-'))
    .map(file => path.relative(publish, file).replaceAll(path.sep, '/')).sort();
const nsis = value => value.replaceAll('$', '$$').replaceAll('"', '$\\"').replaceAll('/', '\\');
const directories = [...new Set(files.flatMap(file => {
    const result = []; let directory = path.posix.dirname(file);
    while (directory !== '.') { result.push(directory); directory = path.posix.dirname(directory); }
    return result;
}))].sort((a, b) => b.split('/').length - a.split('/').length || b.localeCompare(a));
let manifest = `; Generated exact ownership manifest for ${runtimeIdentifier}.\n!macro InstallPayload_${macroSuffix} publishRoot\n`;
for (const file of files) {
    const directory = path.posix.dirname(file);
    manifest += `  SetOutPath "$INSTDIR${directory === '.' ? '' : '\\' + nsis(directory)}"\n`;
    manifest += `  File "/oname=${nsis(path.posix.basename(file))}" "\${publishRoot}/${file.replaceAll('$', '$$').replaceAll('"', '$\\"')}"\n`;
}
manifest += `!macroend\n\n!macro UninstallPayload_${macroSuffix}\n`;
for (const file of files) manifest += `  Delete "$INSTDIR\\${nsis(file)}"\n`;
for (const directory of directories) manifest += `  RMDir "$INSTDIR\\${nsis(directory)}"\n`;
manifest += '!macroend\n';
fs.mkdirSync(path.dirname(generated), { recursive: true });
fs.writeFileSync(generated, manifest, 'utf8');
const hashes = files.map(file => `${crypto.createHash('sha256').update(fs.readFileSync(path.join(publish, file))).digest('hex')}  ${file}`).join('\n') + '\n';
fs.writeFileSync(path.join(path.dirname(generated), `publish-sha256-${runtimeIdentifier}.txt`), hashes);
console.log(`Prepared ${runtimeIdentifier}: native PE architecture verified; ${files.length} owned files and ${inventory.length} package license inventories.\nPayload manifest: ${generated}`);
