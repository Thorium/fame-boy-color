module FameBoy.Test.IoControllerTests

open NUnit.Framework
open FameBoy.Hardware
open FameBoy.IoController

[<Test>]
let ``bcpd write with auto increment updates BCPS register and index`` () =
    let io = createIoController ()
    io.CgbMode <- true

    io.CpuWrite (Io.IoMemoryOffset + Io.Bcps) 0x80uy
    io.CpuWrite (Io.IoMemoryOffset + Io.Bcpd) 0x12uy

    Assert.That(io.BgPaletteRam[0], Is.EqualTo 0x12uy)
    Assert.That(io.BgPaletteIndex, Is.EqualTo 0x01uy)
    Assert.That(io.Registers[Io.Bcps], Is.EqualTo 0x81uy)

[<Test>]
let ``ocpd write with auto increment updates OCPS register and index`` () =
    let io = createIoController ()
    io.CgbMode <- true

    io.CpuWrite (Io.IoMemoryOffset + Io.Ocps) 0xBFuy
    io.CpuWrite (Io.IoMemoryOffset + Io.Ocpd) 0x34uy

    Assert.That(io.ObjPaletteRam[0x3F], Is.EqualTo 0x34uy)
    Assert.That(io.ObjPaletteIndex, Is.EqualTo 0x00uy)
    Assert.That(io.Registers[Io.Ocps], Is.EqualTo 0x80uy)

[<Test>]
let ``bcpd write during mode 3 does not update palette RAM but still increments index`` () =
    let io = createIoController ()
    io.CgbMode <- true
    io.PpuMode <- PpuMode.Drawing

    io.CpuWrite (Io.IoMemoryOffset + Io.Bcps) 0x80uy
    io.CpuWrite (Io.IoMemoryOffset + Io.Bcpd) 0x56uy

    Assert.That(io.BgPaletteRam[0], Is.EqualTo 0x00uy)
    Assert.That(io.BgPaletteIndex, Is.EqualTo 0x01uy)
    Assert.That(io.Registers[Io.Bcps], Is.EqualTo 0x81uy)

[<Test>]
let ``bcpd read during mode 3 returns ff`` () =
    let io = createIoController ()
    io.CgbMode <- true
    io.BgPaletteRam[0] <- 0x78uy
    io.PpuMode <- PpuMode.Drawing

    let value = io.CpuRead (Io.IoMemoryOffset + Io.Bcpd)

    Assert.That(value, Is.EqualTo 0xFFuy)

[<Test>]
let ``ocpd write during mode 3 does not update palette RAM but still increments index`` () =
    let io = createIoController ()
    io.CgbMode <- true
    io.PpuMode <- PpuMode.Drawing

    io.CpuWrite (Io.IoMemoryOffset + Io.Ocps) 0x80uy
    io.CpuWrite (Io.IoMemoryOffset + Io.Ocpd) 0x9Auy

    Assert.That(io.ObjPaletteRam[0], Is.EqualTo 0x00uy)
    Assert.That(io.ObjPaletteIndex, Is.EqualTo 0x01uy)
    Assert.That(io.Registers[Io.Ocps], Is.EqualTo 0x81uy)

[<Test>]
let ``ocpd read during mode 3 returns ff`` () =
    let io = createIoController ()
    io.CgbMode <- true
    io.ObjPaletteRam[0] <- 0xBCuy
    io.PpuMode <- PpuMode.Drawing

    let value = io.CpuRead (Io.IoMemoryOffset + Io.Ocpd)

    Assert.That(value, Is.EqualTo 0xFFuy)
