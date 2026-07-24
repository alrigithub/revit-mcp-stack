import json
import struct
import unittest

from revit_mcp.protocol import ProtocolError, encode_frame, read_frame, write_all


class ProtocolTests(unittest.TestCase):
    def test_partial_reads(self):
        frame = encode_frame({"ok": True, "text": "æ"})
        chunks = [frame[index:index + 1] for index in range(len(frame))]
        self.assertEqual({"ok": True, "text": "æ"}, read_frame(lambda _: chunks.pop(0)))

    def test_partial_writes(self):
        target = bytearray()

        def write(data):
            count = min(2, len(data))
            target.extend(data[:count])
            return count

        source = b"abcdefg"
        write_all(write, source)
        self.assertEqual(source, bytes(target))

    def test_size_limit_precedes_allocation(self):
        header = struct.pack("<I", 9999)
        with self.assertRaises(ProtocolError):
            read_frame(lambda count: header[:count], maximum=10)

    def test_malformed_utf8(self):
        frame = struct.pack("<I", 1) + b"\xff"
        offset = 0

        def read(count):
            nonlocal offset
            value = frame[offset:offset + count]
            offset += len(value)
            return value

        with self.assertRaises(ProtocolError):
            read_frame(read)


if __name__ == "__main__":
    unittest.main()
