module FameBoy.Test.MemoryTests

open NUnit.Framework
open FameBoy.IoController
open FameBoy.Memory

let private makeRom cgbFlag =
    let rom = Array.zeroCreate<byte> 0x8000
    rom[0x147] <- 0x00uy
    rom[0x143] <- cgbFlag
    rom

[<Test>]
let ``createMemory enables CGB mode for 0x80 carts without compat mode`` () =
    let io = createIoController ()
    let rom = makeRom 0x80uy

    createMemory rom io |> ignore

    Assert.That(io.CgbMode, Is.True)
    Assert.That(io.CgbCompatMode, Is.False)

[<Test>]
let ``createMemory enables CGB mode for 0xC0 carts without compat mode`` () =
    let io = createIoController ()
    let rom = makeRom 0xC0uy

    createMemory rom io |> ignore

    Assert.That(io.CgbMode, Is.True)
    Assert.That(io.CgbCompatMode, Is.False)
