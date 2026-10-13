module FameBoy.Test.SerialTests

open NUnit.Framework
open FameBoy.Hardware
open FameBoy.IoController
open FameBoy.Serial

[<Test>]
let ``double speed does not shorten fast cgb serial transfer length`` () =
    let io = createIoController ()
    let serial = createSerial ()
    io.CgbMode <- true
    io.DoubleSpeed <- true
    io.Registers[Io.Sb] <- 0x42uy
    io.Registers[Io.Sc] <- 0x83uy

    for _ in 1..32 do
        stepSerial serial io

    Assert.That(serial.IsTransferring, Is.True)
    Assert.That(io.Registers[Io.Sc] &&& 0x80uy, Is.EqualTo 0x80uy)

    stepSerial serial io

    Assert.That(serial.IsTransferring, Is.False)
    Assert.That(io.Registers[Io.Sc] &&& 0x80uy, Is.EqualTo 0x00uy)

let private linkedPair () =
    let io1, io2 = createIoController (), createIoController ()
    let serial1, serial2 = createSerial (), createSerial ()
    pairLink serial1 io1 serial2 io2
    io1, serial1, io2, serial2

let private stepBoth (io1, serial1, io2, serial2) count =
    for _ in 1..count do
        stepSerial serial1 io1
        stepSerial serial2 io2

[<Test>]
let ``listening side that cancels and sends delivers the new byte`` () =
    let io1, serial1, io2, serial2 as pair = linkedPair ()

    // Both sides wait for a byte on the external clock.
    io1.Registers[Io.Sb] <- 0xF0uy
    io1.Registers[Io.Sc] <- 0x80uy
    io2.Registers[Io.Sb] <- 0xF0uy
    io2.Registers[Io.Sc] <- 0x80uy
    stepBoth pair 10

    // P1 stops listening and sends 0x10 on the internal clock.
    io1.Registers[Io.Sc] <- 0x01uy
    stepBoth pair 1
    io1.Registers[Io.Sb] <- 0x10uy
    io1.Registers[Io.Sc] <- 0x81uy
    stepBoth pair (128 * 8 + 1)

    Assert.That(io2.Registers[Io.Sb], Is.EqualTo 0x10uy)
    Assert.That(io1.Registers[Io.Sb], Is.EqualTo 0xF0uy)
    Assert.That(io1.Registers[Io.Sc] &&& 0x80uy, Is.EqualTo 0x00uy)
    Assert.That(io2.Registers[Io.Sc] &&& 0x80uy, Is.EqualTo 0x00uy)
    Assert.That(serial1.IsTransferring || serial2.IsTransferring, Is.False)

[<Test>]
let ``switching from external to internal clock restarts with the current SB`` () =
    let io1, _, io2, _ as pair = linkedPair ()

    io1.Registers[Io.Sb] <- 0xF0uy
    io1.Registers[Io.Sc] <- 0x80uy
    io2.Registers[Io.Sb] <- 0xF0uy
    io2.Registers[Io.Sc] <- 0x80uy
    stepBoth pair 10

    io1.Registers[Io.Sb] <- 0x3Fuy
    io1.Registers[Io.Sc] <- 0x81uy
    stepBoth pair (128 * 8 + 1)

    Assert.That(io2.Registers[Io.Sb], Is.EqualTo 0x3Fuy)
