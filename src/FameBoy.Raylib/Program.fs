open System
open System.IO
open FameBoy.Apu
open FameBoy.Debug
open FameBoy.Emulator
open FameBoy.Hardware
open FameBoy.IoController
open FameBoy.Joypad
open FameBoy.Raylib
open FameBoy.Raylib.Graphics.GraphicsPipeline
open FameBoy.Raylib.Joypad
open FameBoy.Raylib.RaylibBindings
open FameBoy.Serial
open Raylib_cs

let args = Environment.GetCommandLineArgs()

// Parse arguments: fame-boy <rom-file> [--link] [--no-audio] [scale]
let mutable romPath = ""
let mutable scaleArg: int option = None
let mutable noAudio = false

let nonFlagArgs =
    args
    |> Array.skip 1
    |> Array.filter (fun a ->
        if a = "--link" then
            Config.linkMode <- true
            false
        elif a = "--cgb-trace" then
            CgbTrace.Enabled <- true
            false
        elif a = "--no-audio" then
            noAudio <- true
            false
        else
            true)

if nonFlagArgs.Length < 1 || nonFlagArgs.Length > 2 then
    eprintfn "Usage: fame-boy <rom-file> [--link] [--no-audio] [scale]"
    eprintfn ""
    eprintfn "Options:"
    eprintfn "  --link    Run two linked instances side-by-side (local multiplayer)"
    eprintfn "  --no-audio  Run without sound, paced by the clock instead of the audio device"
    eprintfn "  --cgb-trace  Print sparse CGB palette/HDMA diagnostics"
    eprintfn "  scale     Window scale factor (positive integer, default 4)"
    eprintfn ""
    eprintfn "Controls:"
    eprintfn "  P1: WASD=D-pad, K=A, J=B, N=Start, B=Select"
    eprintfn "  P2: Arrows=D-pad, Home=A, PgUp=B, End=Start, PgDn=Select"
    eprintfn "  F11=Fullscreen"
    exit 1

romPath <- nonFlagArgs[0]

if nonFlagArgs.Length = 2 then
    match Int32.TryParse(nonFlagArgs[1]) with
    | true, s when s > 0 -> Config.scale <- s
    | _ ->
        eprintfn $"Invalid scale value: %s{nonFlagArgs[1]} (must be a positive integer)"
        exit 1

if not (File.Exists romPath) then
    eprintfn $"File not found: %s{romPath}"
    exit 1

[<Literal>]
let audioSamplingRate = 48000

[<Literal>]
let bufferSize = 1024

let windowWidth =
    if Config.linkMode then
        (Config.width * 2 + 4) * Config.scale
    else
        Config.width * Config.scale

Raylib.InitWindow(windowWidth, Config.height * Config.scale, "Fame Boy")
let icon = Raylib.LoadImage("icon.png")

Raylib.SetWindowIcon(icon)
Raylib.UnloadImage(icon)
Raylib.SetTargetFPS 120
if not noAudio then
    Raylib.InitAudioDevice()

/// The audio stream paces the emulator. Without an audio device it never asks
/// for samples, so the clock paces it instead.
let audioReady = isAudioDeviceReady ()

if not (noAudio || audioReady) then
    eprintfn "No audio device: running without sound."

Raylib.SetAudioStreamBufferSizeDefault(bufferSize)

let audioBuffer = Array.zeroCreate<float32> bufferSize
let audioStream = Raylib.LoadAudioStream(uint32 audioSamplingRate, 32u, 1u)

let tryQueueAudio (apu: Apu) stepEmulator =
    while isAudioStreamProcessed audioStream do
        while samplesAvailable apu < nativeSamplesNeeded apu bufferSize audioSamplingRate do
            stepEmulator () |> ignore

        readResampledBuffer apu audioBuffer audioSamplingRate
        updateAudioStream audioStream audioBuffer

/// Normal-speed M-cycles owed for the time already drawn.
let mutable cycleBudget = 0.0

/// Runs the emulator for the last frame's wall-clock time, at most 1/30 s so a
/// stall (window drag, breakpoint) doesn't fast-forward afterwards.
let runForFrameTime (io: IoController) (stepEmulator: unit -> int) =
    let dt = min (float (Raylib.GetFrameTime())) (1.0 / 30.0)
    cycleBudget <- cycleBudget + dt * float cpuFrequency

    while cycleBudget > 0.0 do
        let mCycles = float (stepEmulator ())
        // The budget is in normal-speed cycles; a double-speed CPU runs two per cycle.
        cycleBudget <- cycleBudget - (if io.DoubleSpeed then mCycles / 2.0 else mCycles)

let runEmulator (apu: Apu) (io: IoController) stepEmulator =
    if audioReady then
        tryQueueAudio apu stepEmulator
    else
        runForFrameTime io stepEmulator

let bytes = File.ReadAllBytes romPath
let savePath = Path.ChangeExtension(romPath, ".sav")

let tryLoadSave (ram: byte array) =
    if ram.Length > 0 && File.Exists savePath then
        let saveBytes = File.ReadAllBytes savePath
        Array.Copy(saveBytes, ram, min saveBytes.Length ram.Length)

let writeSave (ram: byte array) =
    if ram.Length > 0 then
        File.WriteAllBytes(savePath, ram)

let mutable joypadState: JoypadState =
    { Up = false
      Down = false
      Left = false
      Right = false
      A = false
      B = false
      Start = false
      Select = false }

let mutable joypadState2: JoypadState = joypadState

if Config.linkMode then
    // Link mode: two emulators side-by-side with serial link
    let ppu1, apu1, serial1, io1, stepEmulator1, applyJoypadState1, _, memory1 =
        createEmulatorWithMemory bytes 4096 (fun () -> joypadState)

    let ppu2, _apu2, serial2, io2, stepEmulator2, applyJoypadState2, _, memory2 =
        createEmulatorWithMemory bytes 4096 (fun () -> joypadState2)

    tryLoadSave memory1.Cartridge.Ram
    tryLoadSave memory2.Cartridge.Ram

    // Link arbitration: shared arbiter, dynamic master/slave assignment
    // on first SC=0x81 write from either device. See Serial.fs.
    pairLink serial1 io1 serial2 io2

    if audioReady then
        Raylib.PlayAudioStream audioStream

    let mutable cycles1 = 0L
    // Player 2 waits until player 1 has run this long.
    let mutable cycles2 = int64 linkBootDelayCycles

    while (not (windowShouldClose ())) do
        if isKeyPressed KeyboardKey.F11 then
            Config.fullscreen <- not Config.fullscreen
            Raylib.ToggleBorderlessWindowed()

        joypadState <- getJoypadState ()
        joypadState2 <- getJoypadStateP2 ()
        applyJoypadState1 joypadState
        applyJoypadState2 joypadState2

        // Only use P1 audio in link mode
        runEmulator apu1 io1 (fun () ->
            let c1 = stepEmulator1 ()
            cycles1 <- cycles1 + int64 c1

            // Step emulator 2 until it catches up
            while cycles2 < cycles1 do
                cycles2 <- cycles2 + int64 (stepEmulator2 ())

            // Exchange serial data after both are in sync
            exchangeSerial serial1 io1 serial2 io2
            c1)

        beginDrawing ()
        Raylib.ClearBackground(Color.Black)

        if ppu1.IoController.CgbMode then
            loadColorFramebufferP1 ppu1.ColorFramebuffer
        else
            loadPpuFramebufferP1 ppu1.Framebuffer

        if ppu2.IoController.CgbMode then
            loadColorFramebufferP2 ppu2.ColorFramebuffer
        else
            loadPpuFramebufferP2 ppu2.Framebuffer

        endDrawing ()

    writeSave memory1.Cartridge.Ram
    writeSave memory2.Cartridge.Ram
    close ()
else
    // Single player mode
    let ppu, apu, _, io, stepEmulator, applyJoypadState, _, memory =
        createEmulatorWithMemory bytes 4096 (fun () -> joypadState)

    tryLoadSave memory.Cartridge.Ram

    if audioReady then
        Raylib.PlayAudioStream audioStream

    while (not (windowShouldClose ())) do
        if isKeyPressed KeyboardKey.F11 then
            Config.fullscreen <- not Config.fullscreen
            Raylib.ToggleBorderlessWindowed()

        joypadState <- getJoypadState ()
        applyJoypadState joypadState

        runEmulator apu io stepEmulator

        beginDrawing ()
        Raylib.ClearBackground(Color.Black)

        if ppu.IoController.CgbMode then
            loadColorFramebuffer ppu.ColorFramebuffer
        else
            loadPpuFramebuffer ppu.Framebuffer

        if Config.enableDebugView then
            loadDebugFramebuffers ppu

        endDrawing ()

    writeSave memory.Cartridge.Ram
    close ()
