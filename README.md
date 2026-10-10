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

It won't work with AMD or Intel graphics.

## Use

1. Download `lg-input-switcher.exe` from [Releases](../../releases).
2. Run it. An icon appears in the system tray.
3. Press **Ctrl+Alt+1** for DisplayPort, **Ctrl+Alt+2** for HDMI 1, or **Ctrl+Alt+3** for HDMI 2.
4. Right-click the tray icon > **Settings** to:
   - change the shortcuts
   - hide inputs you don't use
   - list games that should never be switched away from

Settings are saved in `lg-input-switcher.ini` next to the exe.

Only the screen switches. Your keyboard stays connected to this PC, so the shortcut can always switch back.

## Build

```powershell
.\build.ps1   # builds bin\lg-input-switcher.exe (no SDK needed)
.\test.ps1    # UI tests, using a fake monitor
```

## Credits

The LG input command comes from [kaleb422/NVapi-write-value-to-monitor](https://github.com/kaleb422/NVapi-write-value-to-monitor) and [ddcutil issue #100](https://github.com/rockowitz/ddcutil/issues/100).

## License

Public domain ([The Unlicense](LICENSE)). The NVIDIA NVAPI values used are MIT licensed; see [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
