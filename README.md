# Keyboard Input Switcher for LG

Switch your LG monitor's input (HDMI 1, HDMI 2, DisplayPort) with a keyboard shortcut instead of the joystick on the monitor.

> **Unofficial.** Not made by, affiliated with, or endorsed by LG Electronics.
> **Coded with an AI assistant** (Claude). Use at your own risk.

## Tested on

- LG 27GR83Q-B (UltraGear), connected over DisplayPort
- NVIDIA GeForce RTX 3070
- Windows 11

## Should work on

- **Monitor:** LG monitors that accept LG's own input command (VCP `0xF4` at DDC address `0x50`). Many LG UltraGear models do.
- **Graphics card:** NVIDIA. It sends the command through the NVIDIA driver.
- **Windows:** 10 or 11, 64-bit.

On Windows it won't work with AMD or Intel graphics. The [Linux version](#linux-bazzite) isn't tied to NVIDIA.

## Use

1. Download `lg-input-switcher.exe` from [Releases](../../releases).
2. Run it. An icon appears in the system tray.
3. Press **Ctrl+Alt+1** for DisplayPort, **Ctrl+Alt+2** for HDMI 1, or **Ctrl+Alt+3** for HDMI 2.
4. Right-click the tray icon > **Settings** to:
   - change the shortcuts
   - hide inputs you don't use
   - list games: while a game's window is selected, the shortcuts do nothing (running in the background doesn't count)

Settings are saved in `lg-input-switcher.ini` next to the exe.

Only the screen switches. Your keyboard stays connected to this PC, so the shortcut can always switch back.

## Linux (Bazzite)

A single Python script, `linux/lg-input-switcher`, made for Bazzite but fine on any Linux with Python 3. It needs no extra packages and works with KDE, GNOME and Steam's Game Mode. There is no tray icon: you edit the settings in a file.

**Not tested on real hardware yet.** The parts that run without a monitor are tested in CI.

### Set up

1. Download `lg-input-switcher` (the file with no extension) from [Releases](../../releases).
2. In a terminal:

   ```bash
   python3 ~/Downloads/lg-input-switcher install
   ```

   This copies it to `~/.local/bin`, adds app-menu entries, and starts a background service at every login that watches for the shortcuts. It ends with a checklist; anything marked `FIX` comes with the command to run.
3. The shortcuts read the keyboard directly, so your login must be in the `input` group. On Bazzite:

   ```bash
   ujust add-user-to-input-group add
   ```

   Then restart the PC.
4. Check everything is in place:

   ```bash
   lg-input-switcher doctor
   ```

The shortcuts are the same as on Windows: **Ctrl+Alt+1** DisplayPort, **Ctrl+Alt+2** HDMI 1, **Ctrl+Alt+3** HDMI 2. Unlike on Windows, the keys also reach the program you're in.

### Settings

Edit `~/.config/lg-input-switcher/lg-input-switcher.ini`. It's the same format as the Windows `.ini`, and the service picks up changes by itself.

- `[hotkeys]`: change the shortcuts, e.g. `hdmi1 = Ctrl+Alt+F9`
- `[menu] hide`: inputs that get no shortcut
- `[block] programs`: games during which the shortcuts do nothing while their window is selected. For Steam/Proton games use the Windows name, e.g. `Overwatch.exe`. This works for X11 and XWayland windows, which covers Proton games. A native Wayland window can't be seen, so it's never blocked.

### Without the input group

You can skip step 3 if you'd rather no program could read your keyboard. Bind the app-menu entries in your desktop's settings instead. In KDE: **System Settings > Keyboard > Shortcuts > Add New > Application**, then pick *LG monitor: HDMI 1* and set its key. Then stop the service, so the input isn't switched twice:

```bash
systemctl --user disable --now lg-input-switcher
```

These shortcuts don't work in Game Mode.

### Command line

```bash
lg-input-switcher hdmi1      # dp1, hdmi1, hdmi2
lg-input-switcher detect     # re-read the monitor's inputs
lg-input-switcher list       # monitors on the graphics card's I2C buses
lg-input-switcher probe      # read-only DDC check (brightness)
lg-input-switcher uninstall
```

The log is `~/.local/state/lg-input-switcher/lg-input-switcher.log`.

### Troubleshooting

- **No /dev/i2c devices, or no permission:** `lg-input-switcher doctor` prints the fix. Bazzite ships ddcutil, which normally sets both up already.
- **DDC/CI fails:** turn on DDC/CI in the monitor's menu, if it has the option. To check the monitor answers at all, run `ddcutil detect`. Then try LG's command directly with ddcutil: `ddcutil setvcp 0xF4 0x0090 --i2c-source-addr=x50 --noverify` should switch to HDMI 1.
- **NVIDIA, DDC still fails:** some cards need a driver option; see [ddcutil's NVIDIA notes](https://www.ddcutil.com/nvidia/).

## Build

```powershell
.\build.ps1   # builds bin\lg-input-switcher.exe (no SDK needed)
.\test.ps1    # UI tests, using a fake monitor
```

The Linux script needs no build. Its tests use a fake monitor, keyboard and X server:

```bash
python3 -m unittest discover -s linux/tests -v
```

## Credits

The LG input command comes from [kaleb422/NVapi-write-value-to-monitor](https://github.com/kaleb422/NVapi-write-value-to-monitor) and [ddcutil issue #100](https://github.com/rockowitz/ddcutil/issues/100).

## License

Public domain ([The Unlicense](LICENSE)). The NVIDIA NVAPI values used are MIT licensed; see [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
