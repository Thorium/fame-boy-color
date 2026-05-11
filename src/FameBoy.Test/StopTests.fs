module FameBoy.Test.StopTests

open NUnit.Framework
open FameBoy.Cpu.Execute
open FameBoy.Hardware
open FameBoy.Test.TestHelpers

[<Test>]
let ``stepCpu remains idle while stopped without pending wake event`` () =
    let cpu, io = createTestCpu [||]
    cpu.Stopped <- true
    cpu.Pc <- 0x100us
    cpu.Memory.Cartridge.Rom[0x100] <- 0x00uy

    let cycles = stepCpu cpu io

    Assert.That(cycles, Is.EqualTo 1)
    Assert.That(cpu.Stopped, Is.True)
    Assert.That(cpu.Pc, Is.EqualTo 0x100us)

[<Test>]
let ``joypad interrupt wakes stopped CPU on next step`` () =
    let cpu, io = createTestCpu [||]
    cpu.Stopped <- true
    cpu.Ime <- true
    cpu.Pc <- 0x1234us
    cpu.Sp <- 0xFFFEus
    io.InterruptEnable <- 0x10uy
    io.Registers[Io.If] <- 0x10uy

    let cycles = stepCpu cpu io

    Assert.That(cycles, Is.EqualTo 5)
    Assert.That(cpu.Stopped, Is.False)
    Assert.That(cpu.Pc, Is.EqualTo 0x0060us)
    Assert.That(cpu.Sp, Is.EqualTo 0xFFFCus)
