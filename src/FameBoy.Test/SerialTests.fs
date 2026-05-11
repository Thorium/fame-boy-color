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
