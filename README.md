# MAIRA Typhoon Wind SimHub Plugin

A standalone SimHub plugin that drives the **MAIRA Typhoon Wind** fan hardware
over USB serial using SimHub's own normalised telemetry.

No MAIRA application process, no memory-mapped file, no iRacing SDK required.
Works with any game that SimHub supports.

---

## What it does

* Discovers the MAIRA Typhoon Wind device by scanning COM ports and performing
  the firmware handshake (`WHAT ARE YOU?` → `MAIRA WIND`).
* Reads car speed and yaw rate from SimHub's standard `GameData` each frame.
* Looks up fan power from a user-configured 10-point speed curve using the same
  Catmull-Rom / Hermite interpolation as MAIRA.
* Computes a left/right fan split (curveFactor) from yaw rate so the wind
  leans into corners, matching MAIRA's `Wind.Update()` logic exactly.
* Sends `L{left}R{right}` serial commands to the firmware at ≈ 5 Hz
  (every 12 DataUpdate frames from a ~60 Hz source), matching MAIRA's
  `UpdateInterval = 12`.
* Receives fan tachometer feedback (`L{leftRPM}R{rightRPM}`) from the firmware
  for diagnostic use.
* Exposes a WPF settings panel inside SimHub for all tuning parameters.

---

## SimHub telemetry properties used

| Property | Type | Meaning |
|---|---|---|
| `data.NewData.SpeedKmh` | `double` | Total car speed in km/h. Divided by 360 to produce the normalised 0–1 speed used by the fan curve (100 m/s = 360 km/h = 1.0, matching MAIRA's `velocity / 100`). |
| `data.NewData.OrientationYawVelocity` | `double` | Car yaw angular velocity in **degrees/second**. Positive = turning right. Used to compute the left/right fan split. Equivalent to iRacing's `YawRate` datum (converted from rad/s to deg/s). Full bias at ≈ 30 deg/s with Wind Curving = 100 %. |

---

## Requirements

* **SimHub** — tested against SimHub 9.x (`C:\Program Files (x86)\SimHub\`).
* **MAIRA Typhoon Wind hardware** — running the Typhoon Wind firmware.
* **Visual Studio 2022** (Community or higher) with the `.NET desktop development`
  workload, which includes the .NET Framework 4.8 targeting pack.

---

## Building

### 1. Set the SIMHUB_INSTALL_PATH environment variable

The project file resolves all SimHub assembly references through this variable.

Open **System Properties → Advanced → Environment Variables** and add a
**User** (or System) variable:

```
Name:  SIMHUB_INSTALL_PATH
Value: C:\Program Files (x86)\SimHub\
```

*(Include the trailing backslash.)*

Restart Visual Studio after adding the variable.

### 2. Open and build

```
File → Open → Solution/Project
  → MairaTyphoonWindSimHubPlugin.slnx
```

Build in **Debug** or **Release**. The post-build event automatically copies
`MairaTyphoonWindSimHubPlugin.dll` into the SimHub install folder.

### 3. Start SimHub

SimHub will discover the plugin on startup. The **MAIRA Typhoon Wind** entry
appears in the left-hand settings menu.

---

## Configuration

All settings are available inside SimHub under **MAIRA Typhoon Wind**:

| Section | Setting | Default | Range |
|---|---|---|---|
| Connection | Enable Wind plugin | ✓ | — |
| Connection | Auto-connect on startup | ✓ | — |
| General | Master wind power | 100 % | 0–100 % |
| General | Minimum speed floor | 0 km/h | 0–100 km/h |
| General | Wind curving | 100 % | 0–100 % |
| Curve point 1 | Speed / Fan power | 0 km/h / 0 % | 0–360 km/h / 0–100 % |
| Curve point 2 | Speed / Fan power | 24 km/h / 2 % | 0–360 km/h / 0–100 % |
| Curve point 3 | Speed / Fan power | 49 km/h / 4 % | 0–360 km/h / 0–100 % |
| Curve point 4 | Speed / Fan power | 72 km/h / 6 % | 0–360 km/h / 0–100 % |
| Curve point 5 | Speed / Fan power | 97 km/h / 10 % | 0–360 km/h / 0–100 % |
| Curve point 6 | Speed / Fan power | 145 km/h / 15 % | 0–360 km/h / 0–100 % |
| Curve point 7 | Speed / Fan power | 193 km/h / 25 % | 0–360 km/h / 0–100 % |
| Curve point 8 | Speed / Fan power | 242 km/h / 40 % | 0–360 km/h / 0–100 % |
| Curve point 9 | Speed / Fan power | 290 km/h / 65 % | 0–360 km/h / 0–100 % |
| Curve point 10 | Speed / Fan power | 338 km/h / 100 % | 0–360 km/h / 0–100 % |
| Fan test | Test Left / Test Right / Stop Test | — | — |

**Speed curve note:** Curve point speeds must be kept in ascending order for
the Hermite interpolation to work correctly. The curve is evaluated at the
current car speed and the resulting power is multiplied by Master Wind Power.

**Wind curving note:** At 100 %, a yaw rate of ≈ 30 deg/s fully biases all fan
output to one side. Reduce this setting if the split feels too aggressive.

**Minimum speed floor:** When set above 0, the fans will run at the power level
mapped to that speed even when the car is moving slower. Useful for always
feeling some airflow at low speeds.

---

## Debugging

Set the debug launch target in Visual Studio to:

```
C:\Program Files (x86)\SimHub\SimHubWPF.exe
```

Log messages are written to SimHub's log file
(`C:\Users\<you>\AppData\Local\SimHub\Logs\`) prefixed with `[MairaWindPlugin]`
and `[WindSerialHelper]`.

---

## Fan power algorithm (mirrors MAIRA Wind.Update() exactly)

```
// Step 1 — Normalise car speed
speedNorm = max( SpeedKmh / 360,  WindMinimumSpeed / 360 )

// Step 2 — Hermite interpolation on the 10-point curve
//   speedArray[i]    = WindSpeed[i]    / 360   (normalised)
//   fanPowerArray[i] = WindFanPower[i] / 100   (normalised)
fanPower = InterpolateHermite( speedNorm, speedArray, fanPowerArray )

// Step 3 — Curve factor from yaw rate
//   OrientationYawVelocity is in deg/s; MAIRA's factor 1.91 was for rad/s.
//   Converted: 1.91 / 57.296 ≈ 0.03334 per deg/s
//   Positive curveFactor → right turn → left fan gets full power
//   Negative curveFactor → left  turn → right fan gets full power
curveFactor = clamp( OrientationYawVelocity × 0.03334 × (WindCurving / 100), -1, 1 )

// Step 4 — Per-fan output
masterPower = WindMasterWindPower / 100

leftRaw  = fanPower × (1 + min(0, curveFactor)) × masterPower × 320
rightRaw = fanPower × (1 - max(0, curveFactor)) × masterPower × 320

// Send command
serial.WriteLine( "L{leftRaw}R{rightRaw}" )
```
