# KeyLighting

KeyLighting is a lightweight C# console application that mirrors your screen's display colors onto your RGB keyboard lighting in real time.

## Features

- Real-time screen color reflection onto your RGB keyboard
- Supports color customization via configuration
- Simple and fast setup

## Requirements

- Windows OS
- An RGB keyboard supported by [OpenRGB](https://openrgb.org/)
- OpenRGB must be running (with SDK server enabled)

## Setup and Usage

1. **Download the latest release**
   - Go to the [Releases](https://github.com/ModMaker101/KeyLighting.NET/releases/) page.
   - Download the latest `.zip` file.

2. **Extract the archive**
   - Right-click the downloaded ZIP file and choose **Extract All**.

3. **Run the program**
   - Open the extracted folder.
   - Double-click `KeyLighting.exe` to launch the application.

4. **Configure the settings**
   - A `config.json` file will be generated (if not already present).
   - Open `config.json` with any text editor and adjust settings such as:
     - Brightness
     - Contrast
     - Vibrance
     - Darkening
     - Refresh rate
     - Screen targeting
     - And much more

5. **Enjoy**
   - Watch your keyboard lighting reflect your display in real time!

## Notes

- Make sure OpenRGB is running with SDK support enabled.
- If the app doesn't seem to work, try running it as Administrator.
- Close other programs that may interfere with keyboard lighting control.
