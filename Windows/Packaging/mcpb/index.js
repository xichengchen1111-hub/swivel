'use strict';
// Claude Desktop extension launcher: starts the Swivel installed on this PC as `Swivel.exe --mcp` and
// hands it this process's standard input and output. The extension carries no copy of Swivel: the
// installed app, with its own settings and (from the MSIX package) its identity for text recognition,
// does the work. No shell, no other arguments, no network.
const { spawn } = require('child_process');
const path = require('path');

function fail(message) {
  process.stderr.write(message + '\n');
  process.exit(1);
}

const local = /^[A-Za-z]:[\\/]/;

// A portable Swivel.exe chosen in the extension's settings, else the MSIX package's command alias.
function server() {
  const value = process.env.SWIVEL_EXE || '';
  if (value !== '' && !value.startsWith('${')) {
    const chosen = path.win32.normalize(value);
    if (!local.test(chosen) || path.win32.basename(chosen).toLowerCase() !== 'swivel.exe')
      fail('The Swivel.exe setting must be the full path of Swivel.exe on a local drive, such as C:\\Tools\\Swivel\\Swivel.exe.');
    return chosen;
  }
  const apps = process.env.LOCALAPPDATA || '';
  if (!local.test(apps)) fail('LOCALAPPDATA is not a local folder, so the Swivel MSIX package cannot be found. Choose Swivel.exe in this extension\'s settings.');
  return path.win32.join(apps, 'Microsoft', 'WindowsApps', 'swivel-preview.exe');
}

if (process.platform !== 'win32') fail('This extension is for Swivel on Windows.');
const command = server();
const child = spawn(command, ['--mcp'], { stdio: 'inherit', windowsHide: true, shell: false });
child.on('error', (error) => fail(error.code === 'ENOENT'
  ? 'Swivel was not found at ' + command + '. Install the Swivel MSIX package, or choose Swivel.exe from the portable ZIP in this extension\'s settings, then restart the extension.'
  : 'Swivel could not be started: ' + error.message));
child.on('exit', (code) => process.exit(code === null ? 1 : code));
