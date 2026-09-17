'use strict';
// Platform access shared by the plugin modules.
// Windows/Linux Typora is Electron: Node modules come from window.reqnode.
// macOS Typora is a WKWebView app without Node: bootstrap.js installs a small polyfill (host-mac.cjs) as
// window.typoraAsrHost, and its node() provides the subset of fs/path/crypto/os the plugin uses.
const hostOf=w=>w&&w.typoraAsrHost||null;
const isMac=w=>!!hostOf(w)&&hostOf(w).platform==='mac';
function node(w,name){
 const host=hostOf(w);
 if(host)return host.node(name);
 if(typeof w.reqnode!=='function')throw new Error('当前 Typora 不提供 Node 接口');
 return w.reqnode(name);
}
// Shortcut label shown in the panel. Control+Option+R on macOS is the same key combination as Ctrl+Alt+R.
const shortcutLabel=w=>isMac(w)?'⌃⌥R（Control+Option+R）':'Ctrl+Alt+R';
const secretStoreLabel=w=>isMac(w)?'当前 Mac 用户的钥匙串':'当前 Windows 用户';
module.exports={hostOf,isMac,node,shortcutLabel,secretStoreLabel};
