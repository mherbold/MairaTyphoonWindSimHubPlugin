namespace MairaSimHub.WindPlugin
{
    // Settings model for the MAIRA Typhoon Wind SimHub plugin.
    // Serialised to/from JSON by SimHub via ReadCommonSettings / SaveCommonSettings.
    // All defaults and valid ranges mirror the MAIRA Wind component settings.
    //
    // Speed breakpoints are stored in km/h (0–360), where 360 km/h = 100 m/s = 1.0
    // in MAIRA's internal normalised representation.
    // Fan power breakpoints are stored as percentages (0–100).
    // Master power and curving are stored as percentages (0–100).
    public class WindPluginSettings
    {
        // -----------------------------------------------------------------------
        // Connection
        // -----------------------------------------------------------------------

        public bool WindEnabled { get; set; } = true;
        public bool AutoConnect { get; set; } = true;

        // -----------------------------------------------------------------------
        // General settings
        // -----------------------------------------------------------------------

        // Overall fan power multiplier (%).  0 = fans off, 100 = full output.
        public float WindMasterWindPower { get; set; } = 100f;

        // Minimum fan speed floor (km/h).  Even when the car is moving slowly the
        // fans will run at the power level mapped to this speed.
        // MAIRA default is 0 (disabled).
        public float WindMinimumSpeed { get; set; } = 0f;

        // How strongly cornering biases the left/right fan split (%).
        // At 100 %, a yaw rate of ~30 deg/s fully biases to one side.
        // MAIRA default is 1.0 (= 100 % here).
        public float WindCurving { get; set; } = 100f;

        // -----------------------------------------------------------------------
        // 10-point fan power curve
        //
        // Each point pairs a car speed (km/h) with a fan power output (%).
        // The curve uses Catmull-Rom / Hermite interpolation between points,
        // matching MAIRA's MathZ.InterpolateHermite.
        //
        // Speeds must be in ascending order for the lookup to work correctly.
        // Defaults are converted from MAIRA's normalised (0–1) values where
        // 1.0 = 100 m/s ≈ 360 km/h.
        // -----------------------------------------------------------------------

        public float WindSpeed1  { get; set; } = 0f;
        public float WindSpeed2  { get; set; } = 24f;
        public float WindSpeed3  { get; set; } = 49f;
        public float WindSpeed4  { get; set; } = 72f;
        public float WindSpeed5  { get; set; } = 97f;
        public float WindSpeed6  { get; set; } = 145f;
        public float WindSpeed7  { get; set; } = 193f;
        public float WindSpeed8  { get; set; } = 242f;
        public float WindSpeed9  { get; set; } = 290f;
        public float WindSpeed10 { get; set; } = 338f;

        public float WindFanPower1  { get; set; } = 0f;
        public float WindFanPower2  { get; set; } = 2f;
        public float WindFanPower3  { get; set; } = 4f;
        public float WindFanPower4  { get; set; } = 6f;
        public float WindFanPower5  { get; set; } = 10f;
        public float WindFanPower6  { get; set; } = 15f;
        public float WindFanPower7  { get; set; } = 25f;
        public float WindFanPower8  { get; set; } = 40f;
        public float WindFanPower9  { get; set; } = 65f;
        public float WindFanPower10 { get; set; } = 100f;
    }
}
