import json
import threading
import unittest

from revit_mcp.protocol import encode_frame, read_frame
from revit_mcp.winpipe import PipeIoThread, PipeTransportError


class FakeWin32:
    def __init__(self):
        self.written = bytearray()
        self.response = bytearray(encode_frame({"protocol_version": "0.1", "request_id": "x", "state": "succeeded", "result": {"fake": True}}))
        self.cancelled = False

    def connect(self, pipe_name, timeout_ms):
        self.pipe_name = pipe_name
        return 42

    def write(self, handle, data):
        count = min(3, len(data))
        self.written.extend(data[:count])
        return count

    def read(self, handle, count):
        count = min(2, count, len(self.response))
        value = bytes(self.response[:count])
        del self.response[:count]
        return value

    def cancel(self, handle):
        self.cancelled = True

    def close(self, handle):
        pass


class FakeBridgeTests(unittest.TestCase):
    def test_same_framing_adapter_over_dedicated_io_thread(self):
        api = FakeWin32()
        transport = PipeIoThread(api)
        try:
            response = transport.request("fake", {"protocol_version": "0.1", "request_id": "x", "tool": "health"}, 1000)
            self.assertEqual(True, response["result"]["fake"])
            data = bytearray(api.written)

            def read(count):
                value = bytes(data[:count])
                del data[:count]
                return value

            self.assertEqual("health", read_frame(read)["tool"])
            self.assertEqual("revit-mcp-pipe-io", transport._thread.name)
        finally:
            transport.close()

    def test_transport_recovers_after_broken_pipe(self):
        class BreakOnce(FakeWin32):
            def __init__(self):
                super().__init__()
                self.break_next = True

            def read(self, handle, count):
                if self.break_next:
                    self.break_next = False
                    return b""
                return super().read(handle, count)

        api = BreakOnce()
        transport = PipeIoThread(api)
        try:
            with self.assertRaises(PipeTransportError) as caught:
                transport.request("fake", {"request_id": "one"}, 1000)
            self.assertEqual("pipe_disconnected", caught.exception.code)
            api.response = bytearray(encode_frame({"state": "succeeded", "result": {"recovered": True}}))
            response = transport.request("fake", {"request_id": "two"}, 1000)
            self.assertTrue(response["result"]["recovered"])
        finally:
            transport.close()


if __name__ == "__main__":
    unittest.main()
