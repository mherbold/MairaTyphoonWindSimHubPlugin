using GameReaderCommon;
using SimHub.Plugins;
using System;
using System.Text.RegularExpressions;
using System.Windows.Media;

namespace MairaSimHub.WindPlugin
{
    // -------------------------------------------------------------------------
    // MairaWindPlugin
    //
    // SimHub plugin that drives the MAIRA Typhoon Wind hardware directly
    // over USB serial without requiring the MAIRA application to be running.
    //
    // Implements:
    //   IPlugin          – Init / End lifecycle
    //   IDataPlugin      – DataUpdate called every SimHub telemetry frame (~60 Hz)
    //   IWPFSettingsV2   – Returns a WPF settings panel for the SimHub UI
    //
    // Telemetry properties consumed from GameData / StatusDataBase:
    //
    //   data.NewData.SpeedKmh  (double)
    //       Total car speed in km/h.  Divided by 360 to produce the normalised
    //       speed (0–1) used by the 10-point fan power curve, matching MAIRA's
    //       internal representation where 100 m/s = 360 km/h = 1.0.
    //
    //   data.NewData.OrientationYawVelocity  (double, degrees/second)
    //       Car yaw angular velocity.  Used to compute the left/right fan split
    //       (curveFactor).  Positive = turning right, negative = turning left.
    //       Equivalent to iRacing's YawRate telemetry datum (converted to deg/s).
    //       Full curve bias is reached at ≈ 30 deg/s when WindCurving = 100 %.
    //
    // Serial protocol:
    //   Send:    L{left}R{right}   – fan power, 0–320 raw units (320 = 100 %)
    //   Receive: L{leftRPM}R{rightRPM} – fan tachometer feedback (informational)
    // -------------------------------------------------------------------------

    [PluginDescription("Drives the MAIRA Typhoon Wind fans over USB serial using SimHub telemetry. No MAIRA app required.")]
    [PluginAuthor("MAIRA")]
    [PluginName("MAIRA Typhoon Wind")]
    public class MairaWindPlugin : IPlugin, IDataPlugin, IWPFSettingsV2
    {
        // -----------------------------------------------------------------------
        // SimHub plugin interface
        // -----------------------------------------------------------------------

        public PluginManager PluginManager { get; set; }

        public ImageSource PictureIcon => null;

        public string LeftMenuTitle => "MAIRA Typhoon Wind";

        // -----------------------------------------------------------------------
        // Public state (SettingsControl reads these)
        // -----------------------------------------------------------------------

        public WindPluginSettings Settings { get; private set; }

        public bool IsConnected => _isConnected;

        // Last known fan power outputs (0–100 %) for display in the settings UI.
        public float LeftFanPowerPercent  { get; private set; } = 0f;
        public float RightFanPowerPercent { get; private set; } = 0f;

        // Last fan RPM readings received from the device.
        public int LeftFanRPM  { get; private set; } = 0;
        public int RightFanRPM { get; private set; } = 0;

        // -----------------------------------------------------------------------
        // Private fields
        // -----------------------------------------------------------------------

        private readonly WindSerialHelper _serial = new WindSerialHelper();
        private bool _isConnected = false;
        private bool _isOnTrack   = false;

        // Per-fan test mode: fans run at 100 % regardless of telemetry.
        private bool _testingLeft  = false;
        private bool _testingRight = false;

        // Most recent telemetry values, written every DataUpdate frame and read
        // every UpdateInterval frames inside RunWindUpdate.
        private float  _currentSpeedKmh    = 0f;
        private double _currentYawVelocity  = 0.0;

        // Matches Wind.cs: update output at ~5 Hz from a ~60 Hz DataUpdate source.
        private const int UpdateInterval = 12;
        private int _updateCounter = UpdateInterval + 7; // initial offset matches MAIRA

        // Regex for parsing fan RPM lines sent back by the firmware.
        private static readonly Regex _rpmRegex = new Regex(
            @"^\s*L(?<left>\d+)\s*R(?<right>\d+)\s*$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // -----------------------------------------------------------------------
        // IPlugin / IDataPlugin lifecycle
        // -----------------------------------------------------------------------

        public void Init(PluginManager pluginManager)
        {
            SimHub.Logging.Current.Info("[MairaWindPlugin] Init");

            Settings = this.ReadCommonSettings<WindPluginSettings>(
                "WindPluginSettings",
                () => new WindPluginSettings());

            if (!Settings.WindEnabled)
            {
                SimHub.Logging.Current.Info("[MairaWindPlugin] Wind disabled in settings — skipping device scan.");
                return;
            }

            _serial.PortClosed   += OnPortClosed;
            _serial.DataReceived += OnDataReceived;
            _serial.FindDevice();

            if (Settings.AutoConnect && _serial.DeviceFound)
                Connect();
        }

        public void DataUpdate(PluginManager pluginManager, ref GameData data)
        {
            if (!_isConnected || !Settings.WindEnabled)
                return;

            // Update on-track status and capture current telemetry every frame.
            _isOnTrack = data.GameRunning && !data.GameInMenu && !data.GameReplay
                         && data.NewData != null;

            if (_isOnTrack)
            {
                // SpeedKmh – total car speed in km/h (always available generically).
                _currentSpeedKmh   = (float)data.NewData.SpeedKmh;

                // OrientationYawVelocity – car yaw rate in degrees/second.
                // Positive = turning right.  Equivalent to iRacing's YawRate * (180/π).
                _currentYawVelocity = data.NewData.OrientationYawVelocity;
            }

            _updateCounter--;
            if (_updateCounter <= 0)
            {
                _updateCounter = UpdateInterval;
                RunWindUpdate();
            }
        }

        public void End(PluginManager pluginManager)
        {
            SimHub.Logging.Current.Info("[MairaWindPlugin] End");
            Disconnect();
            this.SaveCommonSettings("WindPluginSettings", Settings);
        }

        public System.Windows.Controls.Control GetWPFSettingsControl(PluginManager pluginManager)
        {
            return new SettingsControl(this);
        }

        // -----------------------------------------------------------------------
        // Connection management
        // -----------------------------------------------------------------------

        public bool Connect()
        {
            SimHub.Logging.Current.Info("[MairaWindPlugin] Connect requested");

            if (!_serial.DeviceFound)
            {
                _serial.FindDevice();
                if (!_serial.DeviceFound)
                {
                    SimHub.Logging.Current.Warn("[MairaWindPlugin] MAIRA Typhoon Wind not found on any COM port.");
                    return false;
                }
            }

            _isConnected = _serial.Open();

            if (_isConnected)
                SimHub.Logging.Current.Info("[MairaWindPlugin] Connected to MAIRA Typhoon Wind.");

            return _isConnected;
        }

        public void Disconnect()
        {
            _isConnected = false;
            _testingLeft  = false;
            _testingRight = false;
            _serial.Close();
            SimHub.Logging.Current.Info("[MairaWindPlugin] Disconnected.");
        }

        // -----------------------------------------------------------------------
        // Fan test helpers
        //
        // Test mode runs the named fan at 100 % raw power (320 units) regardless
        // of telemetry.  Calling with enable=false restores normal operation.
        // -----------------------------------------------------------------------

        public void SetTestLeft(bool enable)
        {
            _testingLeft = enable;
        }

        public void SetTestRight(bool enable)
        {
            _testingRight = enable;
        }

        // -----------------------------------------------------------------------
        // Event handlers
        // -----------------------------------------------------------------------

        private void OnPortClosed(object sender, EventArgs e)
        {
            SimHub.Logging.Current.Warn("[MairaWindPlugin] Serial port closed unexpectedly — marking disconnected.");
            _isConnected  = false;
            _testingLeft  = false;
            _testingRight = false;
            LeftFanRPM    = 0;
            RightFanRPM   = 0;
        }

        private void OnDataReceived(object sender, string data)
        {
            // The firmware sends back "L{leftRPM}R{rightRPM}\n" periodically.
            if (string.IsNullOrWhiteSpace(data))
                return;

            foreach (var line in data.Split('\n'))
            {
                var trimmed = line.Trim();
                var match = _rpmRegex.Match(trimmed);
                if (!match.Success)
                    continue;

                if (int.TryParse(match.Groups["left"].Value, out int leftRpm))
                    LeftFanRPM = leftRpm;

                if (int.TryParse(match.Groups["right"].Value, out int rightRpm))
                    RightFanRPM = rightRpm;
            }
        }

        // -----------------------------------------------------------------------
        // RunWindUpdate  — called at ~5 Hz (every UpdateInterval DataUpdate frames)
        //
        // Reproduces MAIRA's Wind.Update() method:
        //   1. Build normalised speed from SpeedKmh.
        //   2. Look up fan power using the 10-point Hermite-interpolated curve.
        //   3. Compute curveFactor from yaw rate to split power left/right.
        //   4. Apply master power multiplier and convert to raw 0–320 units.
        //   5. Send L{left}R{right} command to hardware.
        // -----------------------------------------------------------------------
        private void RunWindUpdate()
        {
            if (!_isConnected)
                return;

            // --- Test mode: override normal telemetry-driven calculation ---
            if (_testingLeft || _testingRight)
            {
                int testLeft  = _testingLeft  ? 320 : 0;
                int testRight = _testingRight ? 320 : 0;
                SendFanPower(testLeft, testRight);
                return;
            }

            // --- Off-track or no game: fans off ---
            if (!_isOnTrack)
            {
                SendFanPower(0, 0);
                return;
            }

            // ----------------------------------------------------------------
            // Step 1 — Normalise car speed to [0, 1]
            //
            // SpeedKmh / 360 matches MAIRA's (velocity_ms / 100), because
            // 100 m/s = 360 km/h = 1.0 in the normalised model.
            //
            // Apply the minimum-speed floor from settings (also in km/h).
            // ----------------------------------------------------------------
            float speedNorm = Math.Max(
                _currentSpeedKmh / 360f,
                Settings.WindMinimumSpeed / 360f);

            // ----------------------------------------------------------------
            // Step 2 — Look up fan power using the 10-point Hermite curve
            //
            // Speed breakpoints are stored in km/h; divide by 360 to normalise.
            // Fan power breakpoints are stored in % (0–100); divide by 100 for [0,1].
            // ----------------------------------------------------------------
            float[] speedArray = GetNormalisedSpeedArray();
            float[] fanPowerArray = GetNormalisedFanPowerArray();

            // Default to the power mapped to the highest speed point.
            float fanPower = fanPowerArray[9];

            for (int speedIndex = 0; speedIndex < speedArray.Length; speedIndex++)
            {
                if (speedNorm < speedArray[speedIndex])
                {
                    int i0 = Math.Max(0, speedIndex - 2);
                    int i1 = Math.Max(0, speedIndex - 1);
                    int i2 = speedIndex;
                    int i3 = Math.Min(speedArray.Length - 1, speedIndex + 1);

                    if (speedArray[i2] > speedArray[i1])
                    {
                        float t = (speedNorm - speedArray[i1]) / (speedArray[i2] - speedArray[i1]);
                        fanPower = WindMath.InterpolateHermite(
                            fanPowerArray[i0], fanPowerArray[i1],
                            fanPowerArray[i2], fanPowerArray[i3], t);
                    }
                    else
                    {
                        fanPower = fanPowerArray[i1];
                    }

                    break;
                }
            }

            // ----------------------------------------------------------------
            // Step 3 — Compute curveFactor to split wind left/right
            //
            // Ported from Wind.Update():
            //   curveFactor = Clamp(VelocityY * 0.08 * Curving + YawRate * 1.91 * Curving, -1, 1)
            //
            // Where MAIRA's YawRate is in rad/s and VelocityY is lateral velocity
            // in m/s (not available as a generic SimHub property).
            //
            // SimHub's OrientationYawVelocity is in degrees/second.  Converting:
            //   1.91 (per rad/s)  →  1.91 / 57.296 ≈ 0.03334  (per deg/s)
            // At 30 deg/s yaw rate with WindCurving = 100 %:
            //   30 × 0.03334 × 1.0 = 1.0  → fully biased to one side.
            //
            // The VelocityY (lateral velocity) term is omitted here because no
            // generic SimHub property for body-frame lateral velocity exists.
            // The yaw-rate term alone covers the dominant cornering effect.
            // ----------------------------------------------------------------
            const float YawRatePerDegS = 1.91f / 57.296f; // ≈ 0.03334

            float curvingFactor = Settings.WindCurving / 100f;
            float curveFactor   = ClampFloat(
                (float)_currentYawVelocity * YawRatePerDegS * curvingFactor,
                -1f, 1f);

            // ----------------------------------------------------------------
            // Step 4 — Apply master power and curve, compute raw 0–320 values
            //
            //   Negative curveFactor biases left fan; positive biases right fan.
            //   Left  = fanPower × (1 + min(0, curveFactor)) × masterPower × 320
            //   Right = fanPower × (1 - max(0, curveFactor)) × masterPower × 320
            // ----------------------------------------------------------------
            float masterPower = Settings.WindMasterWindPower / 100f;

            float leftRaw  = fanPower * (1f + Math.Min(0f, curveFactor)) * masterPower * 320f;
            float rightRaw = fanPower * (1f - Math.Max(0f, curveFactor)) * masterPower * 320f;

            leftRaw  = Math.Max(0f, leftRaw);
            rightRaw = Math.Max(0f, rightRaw);

            SendFanPower((int)Math.Round(leftRaw), (int)Math.Round(rightRaw));
        }

        // -----------------------------------------------------------------------
        // SendFanPower
        //
        // Sends the L{left}R{right} command to the firmware and updates the
        // public power-display properties for the settings UI.
        // leftVal and rightVal are raw units (0–320 where 320 = 100 %).
        // -----------------------------------------------------------------------
        private void SendFanPower(int leftVal, int rightVal)
        {
            leftVal  = ClampInt(leftVal,  0, 320);
            rightVal = ClampInt(rightVal, 0, 320);

            LeftFanPowerPercent  = leftVal  * 100f / 320f;
            RightFanPowerPercent = rightVal * 100f / 320f;

            _serial.WriteLine($"L{leftVal}R{rightVal}");
        }

        // -----------------------------------------------------------------------
        // Array helpers — convert stored settings values to normalised floats
        // -----------------------------------------------------------------------

        private float[] GetNormalisedSpeedArray()
        {
            return new float[]
            {
                Settings.WindSpeed1  / 360f,
                Settings.WindSpeed2  / 360f,
                Settings.WindSpeed3  / 360f,
                Settings.WindSpeed4  / 360f,
                Settings.WindSpeed5  / 360f,
                Settings.WindSpeed6  / 360f,
                Settings.WindSpeed7  / 360f,
                Settings.WindSpeed8  / 360f,
                Settings.WindSpeed9  / 360f,
                Settings.WindSpeed10 / 360f,
            };
        }

        private float[] GetNormalisedFanPowerArray()
        {
            return new float[]
            {
                Settings.WindFanPower1  / 100f,
                Settings.WindFanPower2  / 100f,
                Settings.WindFanPower3  / 100f,
                Settings.WindFanPower4  / 100f,
                Settings.WindFanPower5  / 100f,
                Settings.WindFanPower6  / 100f,
                Settings.WindFanPower7  / 100f,
                Settings.WindFanPower8  / 100f,
                Settings.WindFanPower9  / 100f,
                Settings.WindFanPower10 / 100f,
            };
        }

        // -----------------------------------------------------------------------
        // Helpers — Math.Clamp not available in .NET Framework 4.8
        // -----------------------------------------------------------------------

        private static int   ClampInt(int value, int min, int max)
            => value < min ? min : (value > max ? max : value);

        private static float ClampFloat(float value, float min, float max)
            => value < min ? min : (value > max ? max : value);
    }
}
