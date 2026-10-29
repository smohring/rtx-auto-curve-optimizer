# Changelog

## 0.1.0 – pre-release

First public version. Developed and tested on one GPU (GeForce RTX 5080, driver 621.01); see Limits in the README.

- Automatic search for the highest stable clock at a set of anchor voltages (Quick / Standard / Thorough presets),
  with gallop-and-bisect search, confirmation runs and a safety margin.
- Built-in DirectX 12 compute stress test that compares every result against a reference computed at stock
  settings, so computation errors end a test within milliseconds; driver resets (TDR) are detected and recovered.
- Operating point judged by voltage, so the normal thermal clock reduction under load is not mistaken for a failure.
- Proven clocks: only the clock the GPU actually ran counts, plus a cold confirm (short bursts after a cool-down)
  that proves higher clocks for a cold game start.
- Proposals: Efficient (lowest voltage that keeps stock performance, validated under variable load),
  Max efficiency, Performance and Uniform offset (the common manual method: one offset, capped at the stock boost
  clock); identical proposals are merged.
- Results page: shift the curve and set its top clock (sliders or a click into the chart), with a live estimate of
  cap, power and performance.
- Tests run in a separate worker process; a watchdog restarts the app after a crash or hang and the run continues.
- Apply a curve directly through the NVIDIA driver, restore the original curve, export as JSON/CSV,
  or save it into an MSI Afterburner profile slot (with backup).
- Crash recovery: the state is saved before every test; after a hang or reboot the original curve is restored
  and the run can be resumed.
- Never raises voltage; power limit, fans and memory overclock are left untouched.
- Windows 11 look (WinUI 3, Mica, light/dark); English and German UI; demo mode with a simulated GPU.
- Command line tool `rtxaco` for diagnostics, scripted runs and a curve write self-test.
- App icon.
