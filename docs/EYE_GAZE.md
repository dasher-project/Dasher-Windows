# Eye Gaze Support in Dasher for Windows

Dasher supports eye gaze input from multiple tracker hardware. This guide
covers setup for each supported device.

## Quick Start

1. Open **Settings** (gear icon in toolbar)
2. Go to **Input** tab
3. Set **Steering Method** to **Eye Gaze**
4. Set **Selection Method** to your preference (Continuous follows your gaze;
   Dwell holds position to select)
5. Set **Eye Tracker Device** to your hardware (see below)
6. Close Settings — a **green dot** appears in the top-right corner when
   tracking is active (red = lost tracking)

## Supported Trackers

### Tobii (Stream Engine) — PCEye 5, PCEye Go, PCEye Mini, EyeX, 4C, Eye Tracker 5

Gives **raw, unfiltered gaze data** directly from Tobii hardware — best for
Dasher because the zooming inference engine handles noise naturally.

**Setup:**

1. Install Tobii drivers (usually automatic when you connect the device)
2. Download `tobii_stream_engine.dll` from
   [Tobii Developer](https://developer.tobii.com/consumer-eye-trackers/streams-and-apis/)
3. Place the DLL in one of these locations:
   - **Installed app:** `C:\Program Files\Dasher\tobii_stream_engine.dll`
   - **User data (no admin needed):** `%APPDATA%\Dasher\tobii_stream_engine.dll`
   - **Portable/ZIP:** Next to `Dasher.Windows.exe`
   - **System-wide:** Any directory on your `PATH`
4. In Settings > Input, select **Tobii (Stream Engine)**
5. Calibrate using Tobii's own software (Gaze Point or Tobii Computer Control)

**To find the exact install path:**
- Open File Explorer, type `%APPDATA%\Dasher` for the user data location
- For MSI installs, the app is at `C:\Program Files\Dasher\`

**Notes:**
- No Tobii Computer Control software required
- No licence key needed — the Stream Engine SDK is free for Tobii hardware
- We cannot redistribute the DLL — you must download it yourself
- Settings shows "Tobii Stream Engine DLL detected" when the DLL is found

### eyetuitive (GazeFirst)

GazeFirst's eyetuitive remote eye tracker, connected via USB.

**Setup:**

1. Connect eyetuitive via USB
2. Ensure the eyetuitive service is running (automatic on connection)
3. Calibrate using the GazeFirst calibration app
4. In Settings > Input, select **eyetuitive**
5. Settings shows "eyetuitive device detected" when connected

**Notes:**
- Provides raw (unfiltered) gaze data
- Connects via gRPC to `eyetracker.local:12340`
- License: eyetuitive.NET SDK is restricted to eyetuitive hardware only

### Windows Eye Tracker (native)

Uses Windows' built-in Eye Control API (`GazeInputSourcePreview`). Works with
any tracker that registers with Windows Eye Control.

**Setup:**

1. Install your tracker's Windows Eye Control driver:
   - **Tobii**: Install Tobii Computer Control (free from Tobii)
   - **Other**: Follow your tracker's Windows Eye Control setup guide
2. Enable Eye Control in Windows Settings > Accessibility > Eye Control
3. In Settings > Input, select **Windows Eye Tracker (native)**

**Notes:**
- Data goes through the OS gaze pipeline (may be smoothed)
- No additional DLLs or software from us needed
- Calibration via your tracker's own software

### UDP Gaze Tracker (network)

For custom setups using [GazeTracker](http://www.eyetellect.com/gazetracker/)
or any tool that streams gaze coordinates over UDP.

**Setup:**

1. Start your gaze streaming software (e.g., GazeTracker)
2. In Settings > Input, select **UDP Gaze Tracker (network)**
3. Set the UDP port (default 5555)
4. Ensure the streaming software sends to `localhost:<port>`

**Supported message formats:**
```
STREAM_DATA <timestamp> <x> <y>
GazePoint X:<x> Y:<y> Timestamp:<ts>
```

Coordinates must be in screen pixels.

## Tracking Status Indicator

When eye gaze is active, a small dot appears in the top-right corner of the
Dasher canvas:

- **Green**: Tracking active (receiving gaze data)
- **Red**: Tracking lost (no gaze data for 500ms — user may have looked away
  or eyes are closed)

## Troubleshooting

**"Could not connect to [tracker]" toast notification:**
- Check the device is connected and powered on
- For Tobii: verify `tobii_stream_engine.dll` is in the right location
- For eyetuitive: verify USB connection and service status
- For Windows Native: verify Eye Control is enabled in Windows Settings

**Green dot never appears:**
- Check your tracker is calibrated (use the tracker's own calibration tool)
- Try moving your eyes around the screen to generate gaze data
- Check the tracker's status in its own software

**Gaze feels jittery or inaccurate:**
- Dasher uses raw gaze data by design — the zooming model averages noise
- If accuracy is poor, recalibrate your tracker
- Ensure you're at the recommended distance from the tracker
