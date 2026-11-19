# Remote Controller maintenance

The user wants Remote to start automatically with Windows. After every application update (including embedded web UI changes), rebuild `dist/RemoteController.exe`, refresh the distribution ZIP, and run `scripts/update-shortcuts.ps1` to update BOTH the Desktop and Startup shortcuts. The startup shortcut must use `--minimized`; the Desktop shortcut must open normally. Preserve paired device data.

Production requires HTTPS/WSS with the per-PC LocalTlsIdentity. Preserve `%LOCALAPPDATA%\RemoteController\tls` across updates: changing/deleting the authority would invalidate the iPhone's installed trust profile. Never add a plaintext control fallback, bypass certificate validation, install a root into global trust stores, or bundle private keys. HTTP serves public setup/profile data only. iPhone certificate installation and trust are performed manually by the user.

Before replacing a running executable, close only this workspace's RemoteController process, preferably using CloseMainWindow and waiting for exit so held mouse buttons are released. Restart the updated application after verification. Desktop and Startup writes may need an elevated tool permission request because they are outside the workspace.
