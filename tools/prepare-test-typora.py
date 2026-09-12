"""Prepare ONLY the copied test application; never touches the installed Typora."""
import json
import pathlib
import struct

root = pathlib.Path(__file__).resolve().parents[1]
resources = root / 'runtime/TyporaTest/resources'
with (resources / 'app.asar').open('rb') as archive:
    header = struct.unpack('<4I', archive.read(16))
    files = json.loads(archive.read(header[3]))['files']
    out = resources / 'app'
    out.mkdir(exist_ok=True)
    for name, entry in files.items():
        archive.seek(8 + header[1] + int(entry['offset']))
        (out / name).write_bytes(archive.read(entry['size']))
(resources / 'app.asar').rename(resources / 'app.original.asar')
launch = out / 'launch.dist.js'
prelude = '''const testApp = require("electron").app;
testApp.setPath("userData", require("path").resolve(__dirname, "../../../test-profile"));
testApp.commandLine.appendSwitch("remote-debugging-port", "19224");
'''
launch.write_text(prelude + launch.read_text(encoding='utf-8'), encoding='utf-8')
