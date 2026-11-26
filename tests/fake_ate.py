"""測試用假 ATE 程式: 透過 VISA 對兩台假儀器下 SCPI 指令 (需先執行 fake_instruments.py)。

用法: python tests/fake_ate.py [迴圈次數]
"""
import sys

import pyvisa

loops = int(sys.argv[1]) if len(sys.argv) > 1 else 5
rm = pyvisa.ResourceManager()
dmm = rm.open_resource("TCPIP0::127.0.0.1::5025::SOCKET", read_termination="\n", write_termination="\n")
psu = rm.open_resource("TCPIP0::127.0.0.1::5026::SOCKET", read_termination="\n", write_termination="\n")
dmm.timeout = psu.timeout = 500

dmm.query("*IDN?")
psu.query("*IDN?")
dmm.write("*RST;*CLS")
dmm.write("CONFigure:VOLTage:DC 10,0.001")
dmm.write("SENS:VOLT:DC:NPLC 10;RANG 10;:TRIG:SOUR IMM;:SAMP:COUN 1")
psu.write("SOUR:VOLT 3.3;CURR 0.5;:OUTP ON")
for i in range(loops):
    psu.write("SOURce:VOLTage %.1f" % (1.0 + i % 5))
    dmm.query("READ?")
    psu.query("MEAS:VOLT?;CURR?")
dmm.write("DISP:TEXT 'semi;colon inside string'")
try:
    psu.query("NOREPLY?")  # 假儀器不回應 -> 逾時
except pyvisa.VisaIOError:
    pass
psu.write("OUTP OFF")
dmm.close()
psu.close()
rm.close()
