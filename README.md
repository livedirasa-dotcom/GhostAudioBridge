# Ghost Audio Bridge — MVP

Windows 11 / .NET 8 WPF audio-routing prototype.

## Features
- Stereo capture from a two-channel interface (M-Audio Inputs 1 and 2).
- Separate mono laptop microphone input.
- Independent gain and pan for the two interface channels.
- Software monitoring to a selected playback device.
- Mixed feed to VB-Audio Virtual Cable for ChatGPT.
- Selectable software processing block: 64 / 128 / 256 / 512 / 1024 samples (default 128).

## Virtual Cable routing
Install VB-Audio Virtual Cable if you want ChatGPT to receive the mixed signal:
- In this app, select **CABLE Input** as the virtual playback output.
- In ChatGPT, select **CABLE Output** as the microphone input.

## Build
The GitHub Actions workflow builds the app on Windows and uploads a self-contained publish artifact.
For a local build, install the .NET 8 SDK and run:
```powershell
dotnet restore GhostAudioBridge.sln
dotnet publish GhostAudioBridge/GhostAudioBridge.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

## Important limitations
- The current audio backend uses Windows MME/WaveIn and WaveOut at 48 kHz. It does not set the M-Audio ASIO driver's actual hardware buffer size.
- The selected sample count controls the app's software processing block and is translated to a millisecond WaveIn request; MME cannot guarantee an exact 128-sample hardware buffer.
- Actual latency and stability must be tested on the target Windows PC with the M-Audio interface.
- The app expects the interface to expose one stereo capture endpoint.
- Monitoring is software monitoring. Use headphones and do not route ChatGPT speaker output back into its virtual microphone.

## Test status
A successful GitHub build confirms compilation and publishing only. It does not certify real-time audio performance on the user's hardware.
