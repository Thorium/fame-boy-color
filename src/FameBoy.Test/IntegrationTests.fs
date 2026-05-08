module FameBoy.Test.IntegrationTests

open System.IO
open System.Text
open NUnit.Framework
open FameBoy.Emulator
open FameBoy.Joypad
open FameBoy.Memory


let private defaultJoypadState: JoypadState =
    { Up = false
      Down = false
      Left = false
      Right = false
      A = false
      B = false
      Start = false
      Select = false }

[<Test>]
let ``dmg-acid2 framebuffer matches expected output after 150000 CPU cycles`` () =
    let rom = Path.Combine("Resources", "dmg-acid2.gb") |> File.ReadAllBytes
    let ppu, _, _, _, stepEmulator, _, _ = createEmulator rom 4096 (fun () -> defaultJoypadState)

    for _ in 0..150000 do
        stepEmulator () |> ignore

    let actual = ppu.Framebuffer |> Array.map byte
    let expected = Path.Combine("Resources", "dmg-acid2.bin") |> File.ReadAllBytes

    Assert.That(actual, Is.EqualTo(expected))

[<Test>]
let ``cpu_instrs serial output contains Passed after 25000000 CPU cycles`` () =
    let rom = Path.Combine("Resources", "cpu_instrs.gb") |> File.ReadAllBytes
    let _, _, serial, _, stepEmulator, _, _ = createEmulator rom 4096 (fun () -> defaultJoypadState)

    let serialOutput = StringBuilder()
    let mutable lastIsTransferring = false

    for _ in 0..25000000 do
        stepEmulator () |> ignore

        // Capture serial output: when IsTransferring transitions to true, OutgoingByte holds the byte being sent
        if serial.IsTransferring && not lastIsTransferring then
            serialOutput.Append(char serial.OutgoingByte) |> ignore

        lastIsTransferring <- serial.IsTransferring

    let output = serialOutput.ToString()
    Assert.That(output, Does.Contain("Passed"), $"Expected serial output to contain 'Passed', but got: {output}")

[<Test>]
let ``cgb-acid2 color framebuffer matches expected output after 150000 CPU cycles`` () =
    let rom = Path.Combine("Resources", "cgb-acid2.gbc") |> File.ReadAllBytes
    let ppu, _, _, _, stepEmulator, _, _ = createEmulator rom 4096 (fun () -> defaultJoypadState)

    for _ in 0..150000 do
        stepEmulator () |> ignore

    // Serialize color framebuffer to byte array (R, G, B per pixel)
    let actual =
        ppu.ColorFramebuffer
        |> Array.collect (fun c -> [| c.R; c.G; c.B |])

    let expected = Path.Combine("Resources", "cgb-acid2.bin") |> File.ReadAllBytes

    Assert.That(actual, Is.EqualTo(expected))
