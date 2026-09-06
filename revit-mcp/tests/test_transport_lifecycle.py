import threading
import unittest
from types import SimpleNamespace
from unittest.mock import patch

from revit_mcp.client import BridgeClient
from revit_mcp.protocol import encode_frame
from revit_mcp.winpipe import PipeIoThread, PipeTransportError


class BlockingApi:
    def __init__(self):
        self.started = threading.Event()
        self.release = threading.Event()
        self.cancelled = []
        self.connected = []
        self.responses = {}

    def connect(self, name, timeout):
        self.connected.append(name)
        self.responses[name] = bytearray(encode_frame({"state": "succeeded", "result": name}))
        return name

    def write(self, handle, data):
        return len(data)

    def read(self, handle, count):
        if handle == "long":
            self.started.set()
            if not self.release.wait(3):
                raise RuntimeError("Test failed to release the blocked read")
            if handle in self.cancelled:
                raise PipeTransportError("read_file_failed", "cancelled", 995)
        result = bytes(self.responses[handle][:count])
        del self.responses[handle][:count]
        return result

    def cancel(self, handle):
        self.cancelled.append(handle)
        self.release.set()

    def close(self, handle):
        pass


class TransportLifecycleTests(unittest.TestCase):
    def test_queued_timeout_does_not_cancel_active_or_dispatch_expired_task(self):
        api = BlockingApi()
        transport = PipeIoThread(api)
        results = []
        worker = threading.Thread(target=lambda: results.append(transport.request("long", {}, 2000)))
        worker.start()
        try:
            self.assertTrue(api.started.wait(1))
            with self.assertRaises(PipeTransportError) as caught:
                transport.request("expired", {}, 30)
            self.assertEqual("pipe_timeout", caught.exception.code)
            self.assertEqual([], api.cancelled)
            api.release.set()
            worker.join(1)
            self.assertEqual("long", results[0]["result"])
            self.assertEqual("next", transport.request("next", {}, 1000)["result"])
            self.assertEqual(["long", "next"], api.connected)
        finally:
            api.release.set()
            worker.join(1)
            transport.close()

    def test_active_timeout_recovers_for_next_request(self):
        api = BlockingApi()
        transport = PipeIoThread(api)
        try:
            with self.assertRaises(PipeTransportError) as caught:
                transport.request("long", {}, 50)
            self.assertEqual("pipe_timeout", caught.exception.code)
            self.assertEqual("next", transport.request("next", {}, 1000)["result"])
            self.assertTrue(all(name == "long" for name in api.cancelled))
        finally:
            transport.close()

    def test_connect_consumes_timeout_and_cannot_send_after_expiry(self):
        class SlowConnect(BlockingApi):
            def connect(self, name, timeout):
                self.started.set()
                self.release.wait(2)
                return super().connect(name, timeout)

            def write(self, handle, data):
                raise AssertionError("Expired request must never be written")

        api = SlowConnect()
        transport = PipeIoThread(api)
        try:
            with self.assertRaises(PipeTransportError):
                transport.request("expired_during_connect", {}, 30)
            api.release.set()
        finally:
            api.release.set()
            transport.close()
        self.assertFalse(transport._thread.is_alive())

    def test_closed_transport_rejects_new_requests(self):
        transport = PipeIoThread(BlockingApi())
        transport.close()
        with self.assertRaises(PipeTransportError) as caught:
            transport.request("next", {}, 1000)
        self.assertEqual("transport_closed", caught.exception.code)

    def test_lost_response_reports_unknown_and_preserves_request_id(self):
        class LostResponse:
            def request(self, *args):
                raise PipeTransportError("read_file_failed", "lost response", 109)

        instance = SimpleNamespace(instance_nonce="nonce", pipe_name="test")
        with patch("revit_mcp.client.get_instance", return_value=instance):
            response = BridgeClient(LostResponse()).call(123, "run_python", request_id="mutation-1")
        self.assertEqual("unknown", response["state"])
        self.assertEqual("mutation-1", response["request_id"])
        self.assertEqual(109, response["error"]["winerror"])
        self.assertIn("get_request_status", response["error"]["remediation"])


if __name__ == "__main__":
    unittest.main()
