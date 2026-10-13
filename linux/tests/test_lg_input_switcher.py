# Tests for the Linux version, using a fake monitor, keyboard and X server (nothing real is touched).
#   python3 -m unittest discover -s linux/tests -v
import importlib.machinery
import importlib.util
import os
import socket
import struct
import sys
import tempfile
import threading
import time
import types
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
_loader = importlib.machinery.SourceFileLoader("lgis", os.path.join(HERE, "..", "lg-input-switcher"))
_spec = importlib.util.spec_from_loader("lgis", _loader)
lgis = importlib.util.module_from_spec(_spec)
_loader.exec_module(lgis)
lgis.time = types.SimpleNamespace(sleep=lambda s: None, monotonic=time.monotonic, strftime=time.strftime)  # no DDC delays


def ddc_reply(payload):
    # A monitor's reply: source 0x6E, length, payload, checksum against 0x50.
    msg = [0x6E, 0x80 | len(payload)] + list(payload)
    chk = 0x50
    for b in msg:
        chk ^= b
    return bytes(msg + [chk])


def edid(mfg="GSM", product=0x5C55, name="LG ULTRAGEAR"):
    e = bytearray(128)
    e[0:8] = b"\x00\xff\xff\xff\xff\xff\xff\x00"
    m = sum((ord(c) - ord("A") + 1) << s for c, s in zip(mfg, (10, 5, 0)))
    e[8], e[9] = m >> 8, m & 0xFF
    e[10], e[11] = product & 0xFF, product >> 8
    e[54 + 3] = 0xFC
    e[54 + 5:54 + 18] = (name.encode() + b"\n").ljust(13, b" ")
    return bytes(e)


class FakeBus:
    def __init__(self, replies=()):
        self.writes = []
        self.replies = list(replies)
        self.fail_writes = 0
        self.closed = False

    def write(self, address, data):
        if self.fail_writes:
            self.fail_writes -= 1
            raise OSError(5, "Input/output error")
        self.writes.append((address, list(data)))

    def read(self, address, length):
        return self.replies.pop(0).ljust(length, b"\0")

    def close(self):
        self.closed = True


def make_switcher(bus, edid_id="", displays=None):
    displays = displays or [lgis.DisplayInfo(4, "NVIDIA i2c adapter 3 at 1:00.0", "GSM5C55", "LG ULTRAGEAR")]
    return lgis.LgSwitcher(edid_id, open_bus=lambda n: bus, displays=lambda problems=None: displays)


CAPS = "(prot(monitor)type(LCD)model(27GR83Q)cmds(01 02 03 0C E3 F3)vcp(02 04 05 08 10 12 14(05 08 0B) 60(0F 11 12) 62)mccs_ver(2.1))"


def caps_replies(text):
    # Capabilities fragments of up to 32 bytes, then an empty one at the end.
    data = text.encode()
    replies = [ddc_reply([0xE3, off >> 8, off & 0xFF] + list(data[off:off + 32])) for off in range(0, len(data), 32)]
    return replies + [ddc_reply([0xE3, len(data) >> 8, len(data) & 0xFF])]


class DdcTests(unittest.TestCase):
    def test_switch_sends_lg_command(self):
        bus = FakeBus()
        sw = make_switcher(bus)
        sw.switch_to(lgis.InputDef(0x11))
        # Source 0x50 (LG), length 0x84, Set VCP 0x03, VCP 0xF4, value 0x0090, checksum
        self.assertEqual(bus.writes, [(0x37, [0x50, 0x84, 0x03, 0xF4, 0x00, 0x90, 0xDD])])

    def test_switch_values(self):
        for code, value in ((0x0F, 0xD0), (0x10, 0xD1), (0x11, 0x90), (0x12, 0x91)):
            bus = FakeBus()
            make_switcher(bus).switch_to(lgis.InputDef(code))
            data = bus.writes[0][1]
            self.assertEqual(data[5], value)
            self.assertEqual(data[6], lgis.checksum(0x6E, 0x50, data[1:]))

    def test_unknown_switch_code(self):
        with self.assertRaises(lgis.SwitchError):
            make_switcher(FakeBus()).switch_to(lgis.InputDef(0x1B))

    def test_switch_retries_after_io_error(self):
        bus = FakeBus()
        bus.fail_writes = 2
        make_switcher(bus).switch_input(0x90)
        self.assertEqual(len(bus.writes), 1)

    def test_switch_gives_up(self):
        bus = FakeBus()
        bus.fail_writes = 3
        with self.assertRaises(lgis.SwitchError):
            make_switcher(bus).switch_input(0x90)

    def test_picks_lg_monitor_when_none_configured(self):
        displays = [lgis.DisplayInfo(3, "a", "DEL4321", "DELL"), lgis.DisplayInfo(5, "b", "GSM5C55", "LG")]
        opened = []
        sw = lgis.LgSwitcher("", open_bus=lambda n: opened.append(n) or FakeBus(), displays=lambda p=None: displays)
        self.assertEqual(sw.find_monitor().bus, 5)
        self.assertEqual(opened, [5])

    def test_configured_monitor_not_found(self):
        with self.assertRaises(lgis.SwitchError) as cm:
            make_switcher(FakeBus(), edid_id="GSM0001").find_monitor()
        self.assertIn("GSM5C55", str(cm.exception))

    def test_keeps_last_bus_when_monitor_not_reported(self):
        displays = [lgis.DisplayInfo(4, "a", "GSM5C55", "LG")]
        sw = lgis.LgSwitcher("", open_bus=lambda n: FakeBus(), displays=lambda p=None: displays)
        sw.find_monitor()
        displays.clear()
        self.assertEqual(sw.find_monitor(refresh=True).bus, 4)

    def test_read_vcp(self):
        bus = FakeBus([ddc_reply([0x02, 0x00, 0x10, 0x00, 0x00, 0x64, 0x00, 0x32])])
        self.assertEqual(make_switcher(bus).read_vcp(0x10), (0x32, 0x64))
        self.assertEqual(bus.writes[0], (0x37, [0x51, 0x82, 0x01, 0x10, 0x6E ^ 0x51 ^ 0x82 ^ 0x01 ^ 0x10]))

    def test_read_vcp_retries_busy_and_garbled(self):
        good = ddc_reply([0x02, 0x00, 0x10, 0x00, 0x00, 0x64, 0x00, 0x32])
        garbled = good[:-1] + bytes([good[-1] ^ 1])
        bus = FakeBus([bytes([0x6E, 0x80, 0xBE]), garbled, good])
        self.assertEqual(make_switcher(bus).read_vcp(0x10), (0x32, 0x64))

    def test_capabilities_and_detect(self):
        bus = FakeBus(caps_replies(CAPS))
        d, model, inputs = make_switcher(bus).detect()
        self.assertEqual(d.edid_id, "GSM5C55")
        self.assertEqual(model, "LG ULTRAGEAR (27GR83Q)")
        self.assertEqual(inputs, [0x0F, 0x11, 0x12])

    def test_edid(self):
        e = edid()
        self.assertEqual(lgis.parse_edid_id(e), "GSM5C55")
        self.assertEqual(lgis.parse_edid_name(e), "LG ULTRAGEAR")


class ConfigTests(unittest.TestCase):
    def setUp(self):
        self.dir = tempfile.TemporaryDirectory()
        self.addCleanup(self.dir.cleanup)
        self.path = os.path.join(self.dir.name, "lg-input-switcher.ini")

    def test_reads_windows_ini(self):
        cfg = lgis.Config.parse("""
            ; comment
            [monitor]
            edid = GSM5C55
            model = LG ULTRAGEAR (27GR83Q)
            inputs = 0F 11 12 zz
            [hotkeys]
            dp1 = Ctrl+Alt+1
            hdmi1 = Ctrl+Alt+2
            [menu]
            hide = hdmi2
            [block]
            programs = Overwatch.exe, cs2
            """.splitlines())
        self.assertEqual(cfg.input_codes, [0x0F, 0x11, 0x12])
        self.assertEqual([(i.key, i.hotkey, i.hidden) for i in cfg.inputs],
                         [("dp1", "Ctrl+Alt+1", False), ("hdmi1", "Ctrl+Alt+2", False), ("hdmi2", "", True)])
        self.assertEqual(cfg.block_programs, ["Overwatch.exe", "cs2"])
        self.assertEqual(cfg.find("hdmi").label, "HDMI 1")

    def test_save_round_trip(self):
        cfg = lgis.Config.load(self.path)  # first run: defaults
        cfg.edid_id, cfg.model, cfg.input_codes = "GSM5C55", "LG", [0x0F, 0x11, 0x12]
        cfg.block_programs = ["Overwatch.exe"]
        cfg.hidden = {"hdmi2"}
        cfg.save(self.path)
        again = lgis.Config.load(self.path)
        self.assertEqual(again.edid_id, "GSM5C55")
        self.assertEqual(again.input_codes, [0x0F, 0x11, 0x12])
        self.assertEqual(again.hotkeys, {"dp1": "Ctrl+Alt+1", "hdmi1": "Ctrl+Alt+2", "hdmi2": "Ctrl+Alt+3"})
        self.assertEqual(again.hidden, {"hdmi2"})
        self.assertEqual(again.block_programs, ["Overwatch.exe"])

    def test_blocking_matches_names(self):
        cfg = lgis.Config.parse(["[block]", "programs = Overwatch.exe, cs2"])
        self.assertEqual(cfg.is_blocked(["wine64-preload", "Overwatch.exe"]), "Overwatch.exe")
        self.assertEqual(cfg.is_blocked(["overwatch"]), "Overwatch.exe")
        self.assertEqual(cfg.is_blocked(["cs2.exe"]), "cs2")
        self.assertIsNone(cfg.is_blocked(["firefox"]))
        self.assertIsNone(cfg.is_blocked([]))

    def test_hotkeys(self):
        self.assertEqual(lgis.parse_hotkey("Ctrl+Alt+1"), (frozenset({"ctrl", "alt"}), 2))
        self.assertEqual(lgis.parse_hotkey("ctrl + shift + F9"), (frozenset({"ctrl", "shift"}), 67))
        self.assertEqual(lgis.parse_hotkey("Win+NumPad1"), (frozenset({"win"}), 79))
        self.assertEqual(lgis.parse_hotkey("Control+D0"), (frozenset({"ctrl"}), 11))
        self.assertEqual(lgis.parse_hotkey("Alt+F13"), (frozenset({"alt"}), 183))
        self.assertEqual(lgis.parse_hotkey("Ctrl+Alt+Q"), (frozenset({"ctrl", "alt"}), 16))
        self.assertIsNone(lgis.parse_hotkey("Ctrl+Alt"))
        self.assertIsNone(lgis.parse_hotkey("Ctrl+Bogus"))
        self.assertIsNone(lgis.parse_hotkey(""))


class FakeKeyboards:
    def __init__(self):
        self.mods = frozenset()

    def held_modifiers(self):
        return self.mods


class FakeSwitcher:
    def __init__(self, edid_id):
        self.edid_id = (edid_id or "").upper()
        self.switched = []

    def switch_to(self, inp):
        self.switched.append(inp.key)

    def close(self):
        pass


class DaemonTests(unittest.TestCase):
    def setUp(self):
        d = tempfile.TemporaryDirectory()
        self.addCleanup(d.cleanup)
        self.old = lgis.CONFIG_PATH, lgis.LOG_PATH, lgis.notify
        lgis.CONFIG_PATH = os.path.join(d.name, "lg-input-switcher.ini")
        lgis.LOG_PATH = os.path.join(d.name, "lg-input-switcher.log")
        lgis.notify = lambda *a: None
        self.addCleanup(self.restore)
        with open(lgis.CONFIG_PATH, "w") as f:
            f.write("[monitor]\nedid = GSM5C55\ninputs = 0F 11 12\n[hotkeys]\ndp1 = Ctrl+Alt+1\n"
                    "hdmi1 = Ctrl+Alt+2\nhdmi2 = Ctrl+Alt+3\n[menu]\nhide = hdmi2\n[block]\nprograms = Overwatch.exe\n")
        self.kb = FakeKeyboards()
        self.selected = []
        self.daemon = lgis.Daemon(keyboards=self.kb, create_switcher=FakeSwitcher, selected=lambda: self.selected)
        self.daemon.load()

    def restore(self):
        lgis.CONFIG_PATH, lgis.LOG_PATH, lgis.notify = self.old

    def press(self, mods, key):
        self.kb.mods = frozenset(mods)
        self.daemon.on_press(key)

    def test_shortcut_switches(self):
        self.press({"ctrl", "alt"}, 3)  # Ctrl+Alt+2
        self.press({"ctrl", "alt"}, 2)  # Ctrl+Alt+1
        self.assertEqual(self.daemon.switcher.switched, ["hdmi1", "dp1"])

    def test_modifiers_must_match_exactly(self):
        self.press({"ctrl"}, 3)
        self.press({"ctrl", "alt", "shift"}, 3)
        self.press(set(), 3)
        self.assertEqual(self.daemon.switcher.switched, [])

    def test_hidden_input_has_no_shortcut(self):
        self.press({"ctrl", "alt"}, 4)  # Ctrl+Alt+3 = hdmi2, hidden
        self.assertEqual(self.daemon.switcher.switched, [])

    def test_blocked_while_game_selected(self):
        self.selected = ["wine64-preload", "Overwatch.exe"]
        self.press({"ctrl", "alt"}, 3)
        self.assertEqual(self.daemon.switcher.switched, [])
        self.selected = ["firefox"]
        self.press({"ctrl", "alt"}, 3)
        self.assertEqual(self.daemon.switcher.switched, ["hdmi1"])

    def test_reloads_changed_settings(self):
        self.assertFalse(self.daemon.config_changed())
        with open(lgis.CONFIG_PATH, "a") as f:
            f.write("[hotkeys]\nhdmi1 = Ctrl+Alt+F9\n")
        os.utime(lgis.CONFIG_PATH, (1, 1))
        self.assertTrue(self.daemon.config_changed())
        self.daemon.load()
        self.press({"ctrl", "alt"}, 67)
        self.assertEqual(self.daemon.switcher.switched, ["hdmi1"])

    def test_detects_inputs_on_first_run(self):
        os.remove(lgis.CONFIG_PATH)
        bus = FakeBus(caps_replies(CAPS))
        daemon = lgis.Daemon(keyboards=self.kb, create_switcher=lambda e: make_switcher(bus, e), selected=lambda: [])
        daemon.load()
        self.assertEqual(daemon.bindings, {})
        daemon.detect_if_needed()
        self.assertEqual(daemon.cfg.input_codes, [0x0F, 0x11, 0x12])
        self.assertEqual(sorted(i.key for i in daemon.bindings.values()), ["dp1", "hdmi1", "hdmi2"])
        self.kb.mods = frozenset({"ctrl", "alt"})
        daemon.on_press(4)  # Ctrl+Alt+3
        self.assertEqual(bus.writes[-1], (0x37, [0x50, 0x84, 0x03, 0xF4, 0x00, 0x91, 0xDC]))


@unittest.skipUnless(sys.platform.startswith("linux"), "needs Unix sockets and Linux paths")
class X11Tests(unittest.TestCase):
    # A tiny fake X server: answers the connection setup, InternAtom and GetProperty.
    ROOT, ACTIVE, PID = 0x100, 0x3200007, 4242

    def setUp(self):
        d = tempfile.TemporaryDirectory()
        self.addCleanup(d.cleanup)
        self.old = lgis.X11_SOCKET, os.environ.get("XAUTHORITY"), os.environ.get("DISPLAY")
        self.addCleanup(self.restore)
        lgis.X11_SOCKET = os.path.join(d.name, "X%d")
        auth = os.path.join(d.name, "xauth")
        with open(auth, "wb") as f:
            for number, cookie in ((b"7", b"wrong-display---"), (b"5", b"0123456789abcdef")):
                f.write(struct.pack(">H", 256))  # FamilyLocal
                for field in (b"host", number, b"MIT-MAGIC-COOKIE-1", cookie):
                    f.write(struct.pack(">H", len(field)) + field)
        os.environ["XAUTHORITY"] = auth
        os.environ["DISPLAY"] = ":5"
        self.server = socket.socket(socket.AF_UNIX)
        self.server.bind(lgis.X11_SOCKET % 5)
        self.server.listen(1)
        self.addCleanup(self.server.close)
        self.cookies = []
        threading.Thread(target=self.serve, daemon=True).start()

    def restore(self):
        lgis.X11_SOCKET = self.old[0]
        for k, v in (("XAUTHORITY", self.old[1]), ("DISPLAY", self.old[2])):
            if v is None:
                os.environ.pop(k, None)
            else:
                os.environ[k] = v

    def recv(self, c, n):
        buf = b""
        while len(buf) < n:
            buf += c.recv(n - len(buf))
        return buf

    def serve(self):
        c, _ = self.server.accept()
        head = self.recv(c, 12)
        nlen, dlen = struct.unpack_from("<HH", head, 6)
        body = self.recv(c, nlen + (-nlen % 4) + dlen + (-dlen % 4))
        self.cookies.append(body[nlen + (-nlen % 4):][:dlen])
        vendor = b"Fake"
        extra = struct.pack("<IIIIHHBBBBBBBBxxxx", 0, 0, 0, 0, len(vendor), 0xFFFF, 1, 1, 0, 0, 32, 32, 8, 255)
        extra += vendor + struct.pack("<BBBxxxxx", 24, 32, 32) + struct.pack("<I", self.ROOT) + b"\0" * 36
        c.sendall(struct.pack("<BBHHH", 1, 0, 11, 0, len(extra) // 4) + extra)
        atoms = {}
        while True:
            req = c.recv(4)
            if not req:
                return
            op, _, length = struct.unpack("<BBH", req)
            rest = self.recv(c, length * 4 - 4)
            if op == 16:
                name = rest[4:4 + struct.unpack_from("<H", rest, 0)[0]].decode()
                atoms.setdefault(name, 300 + len(atoms))
                c.sendall(struct.pack("<BxHII20x", 1, 0, 0, atoms[name]))
            elif op == 20:
                window, prop = struct.unpack_from("<II", rest, 0)
                name = next(k for k, v in atoms.items() if v == prop)
                value = {(self.ROOT, "_NET_ACTIVE_WINDOW"): self.ACTIVE,
                         (self.ACTIVE, "_NET_WM_PID"): self.PID}.get((window, name))
                if value is None:
                    c.sendall(struct.pack("<BBHIIII12x", 1, 0, 0, 0, 0, 0, 0))
                else:
                    c.sendall(struct.pack("<BBHIIII12xI", 1, 32, 0, 1, 6, 0, 1, value))

    def test_reads_selected_window_pid(self):
        x = lgis.X11(5)
        try:
            window = x.cardinal(x.root, "_NET_ACTIVE_WINDOW")
            self.assertEqual(window, self.ACTIVE)
            self.assertEqual(x.cardinal(window, "_NET_WM_PID"), self.PID)
            self.assertIsNone(x.cardinal(self.ROOT, "_NET_WM_PID"))
        finally:
            x.close()
        self.assertEqual(self.cookies, [b"0123456789abcdef"])

    def test_process_names_of_this_process(self):
        names = lgis.process_names(os.getpid())
        self.assertTrue(any("python" in n for n in names), names)


if __name__ == "__main__":
    unittest.main()
