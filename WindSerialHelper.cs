using System;
using System.IO.Ports;
using System.Management;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MairaSimHub.WindPlugin
{
    // Manages USB serial communication with the MAIRA Typhoon Wind device.
    //
    // Adapted from the SBT plugin's SbtSerialHelper, extended to also receive
    // data back from the device (the firmware periodically sends fan RPM readings
    // in the form "L{leftRPM}R{rightRPM}").
    //
    // Serial parameters: 115200 baud, 8N1, no hardware handshake, ASCII, "\n" line terminator.
    internal sealed class WindSerialHelper : IDisposable
    {
        // True once FindDevice has successfully located the MAIRA Wind port.
        public bool DeviceFound => _portName != string.Empty;

        // Raised on the thread-pool when the port closes unexpectedly.
        public event EventHandler PortClosed;

        // Raised on the thread-pool when a line of data is received from the device.
        public event EventHandler<string> DataReceived;

        private const string HandshakeQuery    = "WHAT ARE YOU?";
        private const string HandshakeResponse = "MAIRA WIND";
        private const int    BaudRate          = 115200;

        private string _portName = string.Empty;

        private SerialPort              _serialPort = null;
        private CancellationTokenSource _monitorCts = null;

        // Guards all access to _serialPort so WriteLine on the DataUpdate thread
        // is safe against the background monitor calling Close on port loss.
        private readonly object _lock = new object();

        // -----------------------------------------------------------------------
        // FindDevice
        //
        // Scans Win32_PnPEntity COM entries and sends the handshake to each one.
        // Stops at the first port that replies with "MAIRA WIND".
        // Safe to call if DeviceFound is already true (returns immediately).
        // -----------------------------------------------------------------------
        public void FindDevice()
        {
            if (DeviceFound)
                return;

            SimHub.Logging.Current.Info("[WindSerialHelper] Scanning COM ports for MAIRA Typhoon Wind...");

            try
            {
                using (var searcher = new ManagementObjectSearcher(
                    "SELECT * FROM Win32_PnPEntity WHERE Name LIKE '%(COM%'"))
                {
                    foreach (ManagementObject device in searcher.Get())
                    {
                        var name = device["Name"]?.ToString();
                        if (string.IsNullOrEmpty(name))
                            continue;

                        int startIndex = name.IndexOf("(COM");
                        if (startIndex < 0)
                            continue;

                        int endIndex = name.IndexOf(')', startIndex);
                        if (endIndex < 0)
                            continue;

                        string portName = name.Substring(startIndex + 1, endIndex - startIndex - 1);

                        if (TryHandshake(portName))
                        {
                            _portName = portName;
                            SimHub.Logging.Current.Info($"[WindSerialHelper] MAIRA Typhoon Wind found on {portName}");
                            break;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Error($"[WindSerialHelper] COM scan error: {ex.Message}");
            }

            if (!DeviceFound)
                SimHub.Logging.Current.Info("[WindSerialHelper] MAIRA Typhoon Wind not found on any COM port.");
        }

        // Opens portName, sends the handshake query, waits 200 ms, checks the response.
        private static bool TryHandshake(string portName)
        {
            try
            {
                using (var port = new SerialPort(portName, BaudRate, Parity.None, 8, StopBits.One)
                {
                    Handshake    = Handshake.None,
                    Encoding     = Encoding.ASCII,
                    ReadTimeout  = 500,
                    WriteTimeout = 500,
                    NewLine      = "\n"
                })
                {
                    port.Open();
                    port.DiscardInBuffer();
                    port.DiscardOutBuffer();
                    port.WriteLine(HandshakeQuery);

                    Thread.Sleep(200);

                    string response = port.ReadExisting()?.Trim() ?? string.Empty;
                    return response.IndexOf(HandshakeResponse, StringComparison.OrdinalIgnoreCase) >= 0;
                }
            }
            catch
            {
                return false;
            }
        }

        // -----------------------------------------------------------------------
        // Open
        //
        // Opens the discovered port, starts the background reader and monitor.
        // Returns true on success.
        // -----------------------------------------------------------------------
        public bool Open()
        {
            if (!DeviceFound)
            {
                SimHub.Logging.Current.Warn("[WindSerialHelper] Open called but no device port is known.");
                return false;
            }

            lock (_lock)
            {
                if (_serialPort != null)
                    return _serialPort.IsOpen;

                _serialPort = new SerialPort(_portName, BaudRate, Parity.None, 8, StopBits.One)
                {
                    Handshake    = Handshake.None,
                    Encoding     = Encoding.ASCII,
                    ReadTimeout  = 3000,
                    WriteTimeout = 3000,
                    NewLine      = "\n"
                };

                try
                {
                    _serialPort.Open();
                    _serialPort.DiscardInBuffer();
                    _serialPort.DiscardOutBuffer();

                    // Subscribe to the DataReceived event for incoming fan RPM lines.
                    _serialPort.DataReceived += OnSerialDataReceived;

                    _monitorCts = new CancellationTokenSource();
                    Task.Run(() => MonitorPort(_monitorCts.Token));

                    SimHub.Logging.Current.Info($"[WindSerialHelper] Port {_portName} opened.");
                    return true;
                }
                catch (Exception ex)
                {
                    SimHub.Logging.Current.Error($"[WindSerialHelper] Failed to open {_portName}: {ex.Message}");
                    _serialPort.Dispose();
                    _serialPort = null;
                    return false;
                }
            }
        }

        // -----------------------------------------------------------------------
        // Close
        // -----------------------------------------------------------------------
        public void Close()
        {
            _monitorCts?.Cancel();
            _monitorCts = null;

            lock (_lock)
            {
                if (_serialPort == null)
                    return;

                _serialPort.DataReceived -= OnSerialDataReceived;

                if (_serialPort.IsOpen)
                {
                    try { _serialPort.BaseStream.Flush(); } catch { }
                    try { _serialPort.Close();            } catch { }
                }

                _serialPort.Dispose();
                _serialPort = null;

                SimHub.Logging.Current.Info("[WindSerialHelper] Port closed.");
            }
        }

        public void Dispose()
        {
            GC.SuppressFinalize(this);
            Close();
        }

        // -----------------------------------------------------------------------
        // WriteLine
        //
        // Sends text + "\n" to the firmware.
        // -----------------------------------------------------------------------
        public void WriteLine(string line)
        {
            lock (_lock)
            {
                if (_serialPort == null || !_serialPort.IsOpen)
                    return;

                try
                {
                    _serialPort.WriteLine(line);
                }
                catch (Exception ex)
                {
                    SimHub.Logging.Current.Error($"[WindSerialHelper] Write error: {ex.Message}");
                }
            }
        }

        // -----------------------------------------------------------------------
        // OnSerialDataReceived  (called by SerialPort on a thread-pool thread)
        //
        // Reads all available data and raises the DataReceived event for each
        // complete line received from the device.
        // -----------------------------------------------------------------------
        private void OnSerialDataReceived(object sender, SerialDataReceivedEventArgs e)
        {
            string data;
            lock (_lock)
            {
                if (_serialPort == null || !_serialPort.IsOpen)
                    return;

                try
                {
                    data = _serialPort.ReadExisting();
                }
                catch
                {
                    return;
                }
            }

            if (!string.IsNullOrEmpty(data))
                DataReceived?.Invoke(this, data);
        }

        // -----------------------------------------------------------------------
        // MonitorPort  (background Task)
        // -----------------------------------------------------------------------
        private async Task MonitorPort(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(1000, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                bool portLost;
                lock (_lock)
                {
                    portLost = _serialPort == null || !_serialPort.IsOpen;
                }

                if (portLost)
                {
                    SimHub.Logging.Current.Warn("[WindSerialHelper] Port lost — triggering PortClosed.");
                    Close();
                    PortClosed?.Invoke(this, EventArgs.Empty);
                    break;
                }
            }
        }
    }
}
