# Fame Boy Color

[![CI](https://github.com/Thorium/fame-boy-color/actions/workflows/ci.yml/badge.svg)](https://github.com/Thorium/fame-boy-color/actions/workflows/ci.yml)

A Game Boy and Game Boy Color emulator written in F#. It runs in the browser (compiled with [Fable](https://fable.io/)) and natively on the desktop (with [Raylib](https://www.raylib.com/)).

**▶ [Play online](https://thorium.github.io/fame-boy-color/)** (browser version, no install needed — comes with a bundled demo game, or upload your own `.gb` / `.gbc` ROM)

**📱 Install as an app:** open the same link on your phone or tablet and tap **Install app** in the banner (Android / Chrome), or **Share → Add to Home Screen** (iPhone / iPad). The emulator then launches full-screen from your home screen and works offline.

![pokemon demo](./assets/pokemon.gif) ![zelda demo](./assets/zelda.gif)

## Origins

Fame Boy Color started as a fork of [Fame Boy](https://github.com/nickkossolapov/fame-boy) by Nick Kossolapov, an original Game Boy (DMG) emulator in F#. Nick wrote about its architecture and the experience of building it in [I built a Game Boy emulator in F#](https://nickkossolapov.github.io/fame-boy/building-a-game-boy-emulator-in-fsharp/) — that post is still the best introduction to the core design, and a copy of it lives in [`docs/blog.md`](./docs/blog.md).

This repository has since diverged into its own project and is developed independently: it adds Game Boy Color support, link cable multiplayer, battery saves and the installable web app, and it will not be merged back upstream. The core emulator, the readable/idiomatic F# style, and the MIT license are inherited from the original — thank you, Nick.

## Features

- Runs most popular Game Boy and Game Boy Color games with sound (Tetris, Pokémon, Mario, Zelda, and more).
- **Game Boy Color support** — full-colour rendering, VRAM/WRAM banking, HDMA transfers, CPU double-speed mode, and the boot-ROM compatibility palettes for original DMG games.
- **Link cable multiplayer** — two emulator instances side-by-side exchanging serial data, for local two-player games (Pokémon trading and battles, Tetris versus, …).
- **Battery-backed `.sav` support** for cartridges with SRAM — a `.sav` file next to the ROM on desktop, `localStorage` in the browser.
- **Browser version** with a touch-friendly, responsive on-screen Game Boy, keyboard controls, display scaling, an FPS counter and a mute button. It is an installable PWA that also works offline.
- **Desktop version** for Windows, macOS and Linux via Raylib, with fullscreen (F11) and window scaling.
- Zero-dependency [F# core](./src/FameBoy) shared by every front-end, with the unit and integration tests to match.

## Limitations

There are still gaps against the real hardware that may or may not get closed:

- Limited emulator configuration (no fast-forward, key remapping, or custom palettes).
- No save states (only cartridge SRAM saves).
- Hardware inaccuracies: CPU instruction-level rather than M-cycle-level timing, scanline-based rendering rather than pixel FIFOs, and a few missing hardware features/bugs.

## Repo structure

- `src/FameBoy` — core emulator library (CPU, PPU, APU, memory, cartridges, IO, serial)
- `src/FameBoy.Raylib` — native desktop front-end using Raylib
- `src/FameBoy.Web` — browser front-end using Fable and Vite (the GitHub Pages site)
- `src/FameBoy.Test` — unit and integration tests
- `src/FameBoy.Benchmark`, `src/FameBoy.Benchmark.Web` — performance benchmarking projects
- `docs/` — the original architecture write-up and diagram

## Getting started

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/)
- [Node.js](https://nodejs.org/) (for the web projects only)

### Desktop

``` sh
dotnet run --project src/FameBoy.Raylib -- <rom-file-path> [--link] [scale]
```

- `scale` is an optional positive integer that controls the window size multiplier (default: 4).
- `--link` enables link cable multiplayer (two instances side-by-side in one window).
- Press **F11** to toggle fullscreen.
- Cartridge SRAM is saved to a `.sav` file with the same name as the ROM, next to it.

### Web

``` sh
cd src/FameBoy.Web
npm install
npm run dev
```

This starts both Fable and Vite in watch mode. Use **Ctrl+C** to stop (not `q`).

The production build (what the [deploy workflow](./.github/workflows/deploy.yml) runs on every push to `main`) is `dotnet fable --run npx vite build`, with `PAGES_BASE_PATH` set to the repository name so the site works under `https://thorium.github.io/fame-boy-color/`.

In the browser, cartridge SRAM saves are persisted as `localStorage` entries keyed by the ROM's title and hash, so a game keeps its progress across visits as long as the same ROM is loaded again.

### Testing

The unit tests cover most of the core emulator. The [integration tests](./src/FameBoy.Test/IntegrationTests.fs) run the emulator with the [dmg-acid2](https://github.com/mattcurrie/dmg-acid2) and [cgb-acid2](https://github.com/mattcurrie/cgb-acid2) PPU test ROMs and compare the framebuffer with a known-good one, and run [Blargg's cpu_instrs](https://github.com/retrio/gb-test-roms) checking its serial output for `Passed`.

``` sh
dotnet test
```

### Benchmarks

#### Desktop

Runs a few ROMs headless with [BenchmarkDotNet](https://benchmarkdotnet.org/):

``` sh
./benchmark.ps1
```

Or directly:

``` sh
cd src/FameBoy.Benchmark
dotnet run -c release
```

#### Web

A Node.js benchmark using the same test ROMs and structure, to estimate browser performance:

``` sh
./benchmark-web.ps1
```

Or directly:

``` sh
cd src/FameBoy.Benchmark.Web
npm run bench
```

## Controls

### Player 1

| Game Boy | Key           |
|----------|---------------|
| D-pad    | W / A / S / D |
| A        | K             |
| B        | J             |
| Start    | N             |
| Select   | B             |

### Player 2 (link mode only)

| Game Boy | Key             |
|----------|-----------------|
| D-pad    | Arrow keys      |
| A        | Home            |
| B        | Page Up         |
| Start    | End             |
| Select   | Page Down       |

The web version also has on-screen buttons for mouse and touch; a keyboard is still the most comfortable way to play.

## License

The Fame Boy Color source code is licensed under the [MIT License](./LICENSE); the original copyright belongs to Nick Kossolapov.

This project redistributes an unmodified copy of [Tobu Tobu Girl DX](https://github.com/SimonLarsen/tobutobugirl-dx) by Simon Larsen as the bundled demo game, included under its original MIT/CC-BY licensing terms and not covered by the above license.
