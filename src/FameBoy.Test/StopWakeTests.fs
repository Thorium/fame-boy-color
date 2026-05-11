module FameBoy.Test.StopWakeTests

open NUnit.Framework
open FameBoy.Cpu.Execute
open FameBoy.Hardware
open FameBoy.Joypad
open FameBoy.Test.TestHelpers

[<Test>]
let ``selected joypad line wakes stopped cpu without enabled interrupt`` () =
    let cpu, io = createTestCpu [||]
    cpu.Stopped <- true
    cpu.Pc <- 0x100us
    io.Registers[Io.Joyp] <- 0b0001_1111uy

    let state =
        { Up = false
          Down = false
          Left = false
          Right = false
          A = false
          B = false
          Start = true
          Select = false }

    io.JoypadState <- state
    io.Registers[Io.Joyp] <- applyJoypadState state io.Registers[Io.Joyp] io.TriggerInterrupt
    io.InterruptEnable <- 0x00uy
    io.Registers[Io.If] <- 0x00uy

    let cycles = stepCpu cpu io

    Assert.That(cycles, Is.EqualTo 1)
    Assert.That(cpu.Stopped, Is.False)
    Assert.That(cpu.Pc, Is.EqualTo 0x100us)
