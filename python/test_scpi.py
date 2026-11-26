import os
import sys
import unittest

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from ate_bench.scpi import parse_message, short_form  # noqa: E402


def keys(data, normalize=True):
    return [c.key for c in parse_message(data, normalize)]


class ScpiTest(unittest.TestCase):
    def test_short_form(self):
        for long, short in [("VOLTage", "VOLT"), ("POWer", "POW"), ("FREQuency", "FREQ"), ("SWEep", "SWE"),
                            ("TIME", "TIME"), ("OUTPut2", "OUTP2"), ("dc", "DC")]:
            self.assertEqual(short_form(long), short)

    def test_simple(self):
        self.assertEqual(keys(b"*IDN?\n"), ["*IDN?"])
        self.assertEqual(keys(b"CONFigure:VOLTage:DC 10,0.001\n"), ["CONF:VOLT:DC"])
        self.assertEqual(keys(b":meas:volt?\r\n"), ["MEAS:VOLT?"])

    def test_compound_path(self):
        self.assertEqual(keys(b"SOUR:VOLT 3.3;CURR 0.5;:OUTP ON\n"), ["SOUR:VOLT", "SOUR:CURR", "OUTP"])
        self.assertEqual(keys(b"SENS:VOLT:DC:NPLC 10;RANG 10;*OPC;RES 1\n"),
                         ["SENS:VOLT:DC:NPLC", "SENS:VOLT:DC:RANG", "*OPC", "SENS:VOLT:DC:RES"])

    def test_args_and_query(self):
        c = parse_message(b"MEAS:VOLT? CH1\n")[0]
        self.assertTrue(c.is_query)
        self.assertEqual(c.args, "CH1")

    def test_quoted_semicolon(self):
        cmds = parse_message(b"DISP:TEXT 'a;b';:OUTP ON\n")
        self.assertEqual([c.key for c in cmds], ["DISP:TEXT", "OUTP"])
        self.assertEqual(cmds[0].args, "'a;b'")

    def test_multiline(self):
        self.assertEqual(keys(b"VOLT 1\nCURR 2\n"), ["VOLT", "CURR"])

    def test_block_data(self):
        cmds = parse_message(b"DATA:ARB #216" + bytes(range(16)) + b"\n")
        self.assertEqual(len(cmds), 1)
        self.assertEqual(cmds[0].key, "DATA:ARB")
        self.assertIn("block 16 bytes", cmds[0].args)

    def test_binary(self):
        self.assertEqual(parse_message(bytes(range(0, 30)))[0].kind, "binary")

    def test_non_scpi(self):
        self.assertEqual(parse_message(b"F1R2T4\n")[0].key, "F1R2T4")

    def test_no_normalize(self):
        self.assertEqual(keys(b"VOLTage 1\n", normalize=False), ["VOLTAGE"])


if __name__ == "__main__":
    unittest.main()
