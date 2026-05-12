#if !FABLE_COMPILER
eprintfn "This project is a Fable (F#-to-JavaScript) web app and cannot be run with 'dotnet run'."
eprintfn "To start the web frontend, run:"
eprintfn "  cd src/FameBoy.Web"
eprintfn "  npm run dev"
eprintfn ""
eprintfn "For the desktop version, use:"
eprintfn "  dotnet run --project src/FameBoy.Raylib -- <rom-file>"
exit 1
#endif

open System
open Browser
open Browser.Types
open Fable.Core
open Fable.Core.JsInterop
open FameBoy.Apu
open FameBoy.Emulator
open FameBoy.Hardware
open FameBoy.Ppu
open FameBoy.Serial
open FameBoy.Web.Audio
open FameBoy.Web.Joypad
open FameBoy.Web.JsBindings


let private getElement id =
    match document.getElementById id with
    | null -> failwith $"Element '{id}' not found"
    | el -> el

let getJoypadState = initJoypad ()
let getJoypadState2 = initJoypadP2 ()

let frameDrivenParam =
    match URLSearchParams.Create(window.location.search).get("frame-driven") with
     | Some "false" -> false
     | Some _ -> true
     | _ -> false

let linkModeParam =
    match URLSearchParams.Create(window.location.search).get("link") with
    | Some _ -> true
    | _ -> false

let screenCanvas = getElement "screen" :?> HTMLCanvasElement
let root = getElement "root"
let startOverlay = getElement "start-overlay"
let fpsCounter = getElement "fps-counter"
let fileUploadButton = getElement "rom-file"

screenCanvas.width <- Screen.width
screenCanvas.height <- Screen.height

let ctx = screenCanvas.getContext "2d" :?> CanvasRenderingContext2D
let imageData = ctx.createImageData (Screen.width, Screen.height)

// P2 canvases for link mode. Small layouts reuse the inline screen,
// wide layouts render into a second shell.
let screen2Canvases =
    if linkModeParam then
        [ "screen2"; "screen2-wide" ]
        |> List.choose (fun id ->
            match document.getElementById id with
            | null -> None
            | el ->
                let canvas = el :?> HTMLCanvasElement
                canvas.width <- Screen.width
                canvas.height <- Screen.height
                Some canvas)
    else
        []

let ctx2 =
    screen2Canvases |> List.map (fun c -> c.getContext "2d" :?> CanvasRenderingContext2D)

let imageData2 =
    ctx2
    |> List.tryHead
    |> Option.map (fun c -> c.createImageData (Screen.width, Screen.height))

let shades =
    [| (186uy, 218uy, 85uy)
       (130uy, 153uy, 59uy)
       (74uy, 87uy, 34uy)
       (19uy, 22uy, 8uy) |]

let loadImageData (ppu: Ppu) (imgData: ImageData) =
    let len = Array.length ppu.Framebuffer - 1

    if ppu.IoController.CgbMode then
        for i in 0..len do
            let color = ppu.ColorFramebuffer[i]
            let j = i * 4

            imgData.data[j] <- color.R
            imgData.data[j + 1] <- color.G
            imgData.data[j + 2] <- color.B
            imgData.data[j + 3] <- 255uy
    else
        for i in 0..len do
            let r, g, b = shades[int ppu.Framebuffer[i]]
            let j = i * 4

            imgData.data[j] <- r
            imgData.data[j + 1] <- g
            imgData.data[j + 2] <- b
            imgData.data[j + 3] <- 255uy

let mutable currentAnimationFrame = None
let mutable currentSaveState: (string * byte array) option = None

let private romTitle (bytes: byte array) =
    if bytes.Length <= 0x134 then
        "rom"
    else
        let titleChars =
            [| for i in 0x134 .. min 0x143 (bytes.Length - 1) do
                   let b = bytes[i]

                   if b >= 32uy && b <= 126uy then
                       yield char b |]

        let title = String(titleChars).Trim().Replace(" ", "_")

        if String.IsNullOrWhiteSpace title then "rom" else title

let private romHash (bytes: byte array) =
    let mutable hash = 5381

    for b in bytes do
        hash <- ((hash <<< 5) + hash) ^^^ int b

    hash &&& 0x7FFFFFFF

let private saveKey (bytes: byte array) =
    $"fameboy:sav:{romTitle bytes}:{bytes.Length}:{romHash bytes}"

let private toHex (bytes: byte array) =
    let chars = Array.zeroCreate<char> (bytes.Length * 2)

    for i in 0 .. bytes.Length - 1 do
        let value = int bytes[i]
        let offset = i * 2

        chars[offset] <- "0123456789abcdef"[value >>> 4]
        chars[offset + 1] <- "0123456789abcdef"[value &&& 0x0F]

    String chars

let private hexValue c =
    if c >= '0' && c <= '9' then
        int c - int '0'
    elif c >= 'a' && c <= 'f' then
        int c - int 'a' + 10
    elif c >= 'A' && c <= 'F' then
        int c - int 'A' + 10
    else
        -1

let private tryParseHex (value: string) =
    if String.IsNullOrEmpty value || value.Length % 2 <> 0 then
        None
    else
        let bytes = Array.zeroCreate<byte> (value.Length / 2)
        let mutable isValid = true

        for i in 0 .. bytes.Length - 1 do
            let hi = hexValue value[i * 2]
            let lo = hexValue value[i * 2 + 1]

            if hi < 0 || lo < 0 then
                isValid <- false
            else
                bytes[i] <- byte ((hi <<< 4) ||| lo)

        if isValid then Some bytes else None

let private loadSaveRam key (ram: byte array) =
    if ram.Length > 0 then
        try
            match window.localStorage.getItem key with
            | null -> ()
            | value ->
                match tryParseHex value with
                | Some saveBytes -> Array.Copy(saveBytes, ram, min saveBytes.Length ram.Length)
                | None -> ()
        with _ ->
            ()

let private persistCurrentSave () =
    match currentSaveState with
    | Some (key, ram) when ram.Length > 0 ->
        try
            window.localStorage.setItem(key, toHex ram)
        with _ ->
            ()
    | _ -> ()

let private showOverlayError (message: string) =
    startOverlay?innerHTML <- message
    startOverlay?classList?remove "hidden"

let startEmulator bytes =
    persistCurrentSave ()
    currentSaveState <- None
    currentAnimationFrame |> Option.iter window.cancelAnimationFrame
    currentAnimationFrame <- None

    startOverlay?classList?add "hidden"

    let ppu1, apu1, serial1, io1, stepEmulator1, applyJoypadState1, _, memory1 =
        try
            createEmulatorWithMemory bytes 4096 getJoypadState
        with ex ->
            showOverlayError "Error!<br>Invalid ROM"
            raise ex

    let saveKey = saveKey bytes
    loadSaveRam saveKey memory1.Cartridge.Ram
    currentSaveState <- Some (saveKey, memory1.Cartridge.Ram)

    // In link mode, create a second emulator instance
    let linkState =
        if linkModeParam then
            let ppu2, _apu2, serial2, io2, stepEmulator2, applyJoypadState2, _ =
                createEmulator bytes 4096 getJoypadState2
            // Link arbitration: shared arbiter, dynamic master/slave
            // assignment on first SC=0x81 write from either device.
            // See Serial.fs.
            pairLink serial1 io1 serial2 io2
            Some (ppu2, serial2, io2, stepEmulator2, applyJoypadState2)
        else
            None

    ensureInitialized ()
    resetPlayback ()

    let draw () =
        loadImageData ppu1 imageData
        ctx.putImageData (imageData, 0, 0)

        match linkState, imageData2 with
        | Some (ppu2, _, _, _, _), Some img2 ->
            loadImageData ppu2 img2
            for c2 in ctx2 do
                c2.putImageData (img2, 0, 0)
        | _ -> ()
    
    let targetCyclesPerMs = float cpuFrequency / 1000.0
    let maxCyclesPerFrame = float cpuFrequency / 60.0
    let mutable accumulator = 0.0
    let mutable cycles1 = 0.0
    let mutable cycles2 = 0.0

    let fpsWindowSize = 30
    let fpsHistory = Array.zeroCreate<float> fpsWindowSize
    let mutable fpsIndex = 0
    let mutable fpsFrameCount = 0
    let mutable lastFpsLogTime = 0.0
    let mutable lastSavePersistTime = -2000.0

    let rec runEmulator (last: float) (timestamp: float) =
        let dt = timestamp - last

        getJoypadState () |> applyJoypadState1

        match linkState with
        | Some (_, _, _, _, applyJoypadState2) ->
            getJoypadState2 () |> applyJoypadState2
        | None -> ()
        
        let frameDriven = frameDrivenParam || isUserMuted ()

        if frameDriven then
            let cycles = Math.Min(targetCyclesPerMs * dt, maxCyclesPerFrame)
            accumulator <- accumulator + cycles
            
            while accumulator > 0 do
                let mCycles = float (stepEmulator1 ())
                cycles1 <- cycles1 + mCycles

                match linkState with
                | Some (_, serial2, io2, stepEmulator2, _) ->
                    while cycles2 < cycles1 do
                        cycles2 <- cycles2 + float (stepEmulator2 ())
                    exchangeSerial serial1 io1 serial2 io2
                | None -> ()

                accumulator <- accumulator - mCycles
 
        let stepWithLink () =
            let c = stepEmulator1 ()
            cycles1 <- cycles1 + float c

            match linkState with
            | Some (_, serial2, io2, stepEmulator2, _) ->
                while cycles2 < cycles1 do
                    cycles2 <- cycles2 + float (stepEmulator2 ())
                exchangeSerial serial1 io1 serial2 io2
            | None -> ()

            c

        tryQueueAudio apu1 stepWithLink frameDriven

        reportFrameTime dt

        fpsHistory[fpsIndex] <- dt
        fpsIndex <- (fpsIndex + 1) % fpsWindowSize
        fpsFrameCount <- min (fpsFrameCount + 1) fpsWindowSize

        if timestamp - lastFpsLogTime >= 500.0 then
            let mutable total = 0.0

            for i = 0 to fpsFrameCount - 1 do
                total <- total + fpsHistory[i]

            let avgDt = total / float fpsFrameCount
            let fps = 1000.0 / avgDt
            fpsCounter.textContent <- $"%.0f{fps}"
            lastFpsLogTime <- timestamp

        if timestamp - lastSavePersistTime >= 2000.0 then
            persistCurrentSave ()
            lastSavePersistTime <- timestamp

        draw ()
        currentAnimationFrame <- window.requestAnimationFrame (runEmulator timestamp) |> Some

    currentAnimationFrame <- window.requestAnimationFrame (runEmulator 0) |> Some

let onFileLoaded (ev: Event) =
    let input = ev.target :?> HTMLInputElement
    let files = input.files

    if not (isNull files) && files.length > 0 then
        let file = files.[0]
        let reader = FileReader.Create()

        reader.onload <-
            fun _ ->
                let arrayBuffer = reader.result :?> JS.ArrayBuffer
                let uint8Array = JS.Constructors.Uint8Array.Create(arrayBuffer)
                let bytes: byte array = Array.init (int uint8Array.length) (fun i -> uint8Array[i])

                try
                    startEmulator bytes
                with _ ->
                    ()

        reader.readAsArrayBuffer file

fileUploadButton.addEventListener ("change", onFileLoaded)
window.addEventListener ("beforeunload", fun _ -> persistCurrentSave ())
window.addEventListener ("pagehide", fun _ -> persistCurrentSave ())
document.addEventListener ("visibilitychange", fun _ -> if document.visibilityState = "hidden" then persistCurrentSave ())

let scaleSelector = document.querySelectorAll "input[name='scale']"

for i in 0 .. int scaleSelector.length - 1 do
    let input = scaleSelector.[i] :?> HTMLInputElement

    input.addEventListener ("change", fun _ -> document.documentElement?style?setProperty ("--s", input.value))

let muteButton = getElement "mute-button"
let muteIconOn = getElement "mute-icon-on"
let muteIconOff = getElement "mute-icon-off"

muteButton.addEventListener (
    "click",
    fun _ ->
        let isMuted = toggleMute ()
        muteIconOn?classList?toggle ("hidden", isMuted)
        muteIconOff?classList?toggle ("hidden", not isMuted)
)

// Show/hide link mode UI
if linkModeParam then
    root?classList?add "link-mode"

    match document.getElementById "link-container" with
    | null -> ()
    | el -> el?classList?remove "hidden"

    match document.getElementById "link-shell" with
    | null -> ()
    | el -> el?classList?remove "hidden"

    match document.getElementById "p2-controls" with
    | null -> ()
    | el -> el?classList?remove "hidden"

// Link mode toggle
let linkModeToggle = document.getElementById "link-mode-toggle" :?> HTMLInputElement
linkModeToggle.``checked`` <- linkModeParam

linkModeToggle.addEventListener (
    "change",
    fun _ ->
        let urlParams = URLSearchParams.Create(window.location.search)

        if linkModeToggle.``checked`` then
            urlParams.set ("link", "1")
        else
            urlParams.delete "link"

        let newUrl = $"%s{window.location.pathname}?%s{urlParams.ToString()}"
        window.location.replace newUrl
)

// Pre-fetch the default ROM, then wait for user interaction to start
// User interaction on the page is needed to start Web Audio
let mutable private defaultRomBytes: byte array option = None
let mutable private defaultRomStarted = false

let private onFirstInteraction (_: Event) =
    if not defaultRomStarted then
        defaultRomStarted <- true

        match defaultRomBytes with
        | Some bytes -> startEmulator bytes
        | None -> ()

let private assetUrl fileName =
    let pathname = window.location.pathname
    let basePath =
        if pathname.EndsWith "/" then
            pathname
        else
            let lastSlash = pathname.LastIndexOf "/"
            let lastSegment = pathname.Substring(lastSlash + 1)

            if lastSegment.Contains "." then
                pathname.Substring(0, lastSlash + 1)
            else
                pathname + "/"

    basePath + fileName

let loadDefaultRom () =
    async {
        try
            let! response = fetch (assetUrl "tobudx.gb") |> Async.AwaitPromise
            let! arrayBuffer = response.arrayBuffer () |> Async.AwaitPromise
            let uint8Array = JS.Constructors.Uint8Array.Create(arrayBuffer)
            let bytes: byte array = Array.init (int uint8Array.length) (fun i -> uint8Array[i])

            defaultRomBytes <- Some bytes

            document.addEventListener ("click", onFirstInteraction)
            document.addEventListener ("keydown", onFirstInteraction)
        with _ ->
            showOverlayError "Error!<br>Couldn't load demo ROM"
    }
    |> Async.StartImmediate

loadDefaultRom ()
