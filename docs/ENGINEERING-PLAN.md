# Ghost Audio Bridge — Engineering Plan

## Product behavior (accepted)

- Input 1: M-Audio stereo capture channel 1 / left.
- Input 2: M-Audio stereo capture channel 2 / right.
- Input 3: laptop microphone.
- Each input has independent gain; the two interface channels also have independent pan.
- Headphone monitor receives the keyboard/interface mix only. The laptop microphone is never sent to the headphone monitor (no sidetone).
- ChatGPT virtual microphone receives keyboard/interface mix plus laptop microphone.
- ChatGPT's own playback remains on the selected Windows headphone output and is not routed back into the virtual microphone.

## Current phase 1 change

The existing engine had sent the same combined PCM block to both outputs. That violated the required no-sidetone behavior. Phase 1 separates the destinations:
- monitor output = M-Audio input 1 + input 2;
- virtual output = M-Audio input 1 + input 2 + laptop microphone.

The UI labels now state this routing explicitly.

## Known technical limitations (not yet solved)

- Current capture/playback uses NAudio WaveInEvent/WaveOutEvent (MME), not the M-Audio ASIO driver.
- The selected block size controls this application's software block cadence; it does not set the ASIO driver's hardware buffer.
- The current mixer loop is timer-driven and is not yet a callback-driven, clock-synchronized real-time engine.
- Independent M-Audio and laptop-microphone clocks can drift. A production implementation needs explicit clock-domain handling, buffering and, where needed, adaptive resampling.
- This branch is not yet a validated release. It must compile on Windows and be tested on the actual M-Audio hardware.

## Planned implementation sequence

1. Separate monitor and virtual-microphone routing (implemented in this branch).
2. Replace timer-driven mixing with a callback/block-driven engine and bounded ring buffers; expose underrun/overrun counters.
3. Use the M-Audio ASIO driver for the stereo interface and its actual supported hardware buffer sizes.
4. Capture the laptop microphone independently through a Windows audio API, then synchronize the two clock domains.
5. Keep output routing independent: headphone monitor excludes microphone; virtual microphone includes it.
6. Build the Ghost Matrix UI with live meters, clear device selection, buffer status, and saved profiles.
7. Windows hardware validation, installer, signing and release packaging.

## Release gates

Do not label ASIO buffer selection as working until the driver reports the selected hardware buffer and repeated tests confirm stable audio. Do not call the product release-ready until long-duration tests show no audible dropouts, feedback loops or device-reconnect failures.
