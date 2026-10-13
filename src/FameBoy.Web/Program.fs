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
open FameBoy.Web.Games
open FameBoy.Web.Joypad
open FameBoy.Web.JsBindings


let private getElement id =
    match document.getElementById id with
    | null -> failwith $"Element '{id}' not found"
    | el -> el

let getJoypadState = initJoypad ()
let getJoypadState2 = initJoypadP2 ()

let frameDrivenParam =
    match URLSearchParams.Create(window.location.search).get "frame-driven" with
     | Some "false" -> false
     | Some _ -> true
     | _ -> false

let linkModeParam =
    match URLSearchParams.Create(window.location.search).get "link" with
    | Some _ -> true
    | Option.None -> false

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
    let hash = bytes |> Array.fold (fun hash b -> ((hash <<< 5) + hash) ^^^ int b) 5381

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
        ValueNone
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

        if isValid then ValueSome bytes else ValueNone

let private loadSaveRam key (ram: byte array) =
    if ram.Length > 0 then
        try
            match window.localStorage.getItem key with
            | null -> ()
            | value ->
                match tryParseHex value with
                | ValueSome saveBytes -> Array.Copy(saveBytes, ram, min saveBytes.Length ram.Length)
                | ValueNone -> ()
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

let private showOverlay (message: string) =
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
            showOverlay "Error!<br>Invalid ROM"
            reraise ()

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
    // Player 2 waits until player 1 has run this long.
    let mutable cycles2 = float linkBootDelayCycles

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

                // The budget is in normal-speed cycles; a double-speed CPU runs two per cycle.
                accumulator <- accumulator - (if io1.DoubleSpeed then mCycles / 2.0 else mCycles)
 
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

let private assetUrl fileName =
    let pathname = window.location.pathname
    let basePath =
        if pathname.EndsWith "/" then
            pathname
        else
            let lastSlash = pathname.LastIndexOf "/"
            let lastSegment = pathname.Substring(lastSlash + 1)

            if lastSegment.Contains '.' then
                pathname.Substring(0, lastSlash + 1)
            else
                pathname + "/"

    basePath + fileName

let private toBytes (arrayBuffer: JS.ArrayBuffer) : byte array =
    let uint8Array = JS.Constructors.Uint8Array.Create(arrayBuffer)
    Array.init (int uint8Array.length) (fun i -> uint8Array[i])

let private fetchOk url =
    async {
        let! response = fetch url |> Async.AwaitPromise

        if not response.ok then
            failwith $"HTTP {response.status} for {url}"

        return response
    }

/// Reads the body chunk by chunk, reporting (received, total) bytes; total is 0
/// when the server sends no length.
let private readWithProgress (response: IResponse) (progress: int -> int -> unit) =
    async {
        match response?body with
        | null ->
            let! arrayBuffer = response.arrayBuffer () |> Async.AwaitPromise
            return toBytes arrayBuffer
        | body ->
            let total =
                match response?headers?get ("content-length") with
                | null -> 0
                | length -> int (string length)

            let reader = body?getReader ()
            let chunks = ResizeArray<JS.Uint8Array>()
            let mutable received = 0
            let mutable finished = false

            while not finished do
                let! chunk = (reader?read () : JS.Promise<obj>) |> Async.AwaitPromise

                if chunk?``done`` then
                    finished <- true
                else
                    let value: JS.Uint8Array = chunk?value
                    chunks.Add value
                    received <- received + int value.length
                    progress received total

            let bytes = Array.zeroCreate<byte> received
            let mutable offset = 0

            for chunk in chunks do
                bytes?set (chunk, offset)
                offset <- offset + int chunk.length

            return bytes
    }

let private fetchRom (game: Game) (progress: int -> int -> unit) =
    async {
        let url = if game.Rom.StartsWith "https://" then game.Rom else assetUrl game.Rom
        let! response = fetchOk url
        return! readWithProgress response progress
    }

/// "Loading… 45%" on the screen while a ROM downloads.
let private loadingText (received: int) (total: int) =
    if total > 0 then
        $"Loading…<br>%d{min 100 (received * 100 / total)}%%"
    else
        $"Loading…<br>%d{received / 1024} KB"

/// A "More games" entry: the playable game plus the catalogue's metadata.
type private CatalogueGame =
    { Game: Game
      Platform: string
      Genres: string array
      HasLicense: bool
      /// Release date (yyyy-MM-dd or yyyy) when known, else "".
      Released: string
      /// Date the game was added to Homebrew Hub (yyyy-MM-dd).
      Added: string }

    /// Release date, or the date added to Homebrew Hub when that is unknown.
    member this.Date = if this.Released <> "" then this.Released else this.Added

// "More games": the Homebrew Hub catalogue generated by build-moregames.fsx,
// fetched the first time it is needed.
let mutable private moreGames: CatalogueGame list option = None

let private loadMoreGames () =
    async {
        match moreGames with
        | Some games -> return games
        | None ->
            let! response = fetchOk (assetUrl "moregames.json")
            let! text = response.text () |> Async.AwaitPromise
            let catalogue = JS.JSON.parse text
            let romBase: string = catalogue?romBase
            let infoBase: string = catalogue?infoBase

            let games =
                (catalogue?games: obj array)
                |> Array.map (fun g ->
                    let license: string = g?license
                    // Entries outside Homebrew Hub carry their own URLs.
                    let romUrl: string = g?romUrl
                    let info: string = g?info

                    { Game =
                        { Id = g?id
                          Title = g?title
                          Author = g?author
                          Genre = g?platform
                          LinkCable = (g?linkCable: obj) = box true
                          License = if license = "" then "no license stated" else license
                          Rom = if isNull romUrl then romBase + g?rom else romUrl
                          Screenshot = ""
                          Info = if isNull info then infoBase + g?id else info }
                      Platform = g?platform
                      Genres = g?genres
                      HasLicense = license <> ""
                      Released = g?released
                      Added = g?added })
                |> Array.toList

            moreGames <- Some games
            return games
    }

let private gameParam = URLSearchParams.Create(window.location.search).get "game"
let private gameList = getElement "game-list"
let private nowPlaying = getElement "now-playing"

// Bumped on every game start, so a slow download can't replace a newer pick.
let mutable private loadGeneration = 0
let mutable private started = false

/// Shows the game as current: shelf highlight, credit line and the ?game=
/// parameter, which survives the reload when link mode is toggled.
let private markCurrent (game: Game option) (updateUrl: bool) =
    let cards = gameList.querySelectorAll ".game-card"

    for i in 0 .. int cards.length - 1 do
        let card = cards.[i] :?> HTMLElement
        let isCurrent = game |> Option.exists (fun g -> g.Id = card?dataset?gameId)
        card.setAttribute ("aria-current", (if isCurrent then "true" else "false"))

    nowPlaying.textContent <- ""

    match game with
    | Some game ->
        let link = document.createElement "a" :?> HTMLAnchorElement
        link.href <- game.Info
        link.target <- "_blank"
        link.rel <- "noopener noreferrer"
        link.textContent <- game.Title
        nowPlaying.appendChild (document.createTextNode "Playing ") |> ignore
        nowPlaying.appendChild link |> ignore
        nowPlaying.appendChild (document.createTextNode $" by {game.Author} · {game.License}") |> ignore
    | None -> nowPlaying.textContent <- "Playing your own ROM"

    if updateUrl then
        let urlParams = URLSearchParams.Create(window.location.search)

        match game with
        | Some game -> urlParams.set ("game", game.Id)
        | None -> urlParams.delete "game"

        let query = urlParams.ToString()
        let url = if query = "" then window.location.pathname else $"%s{window.location.pathname}?%s{query}"
        history.replaceState (null, "", url)

let private playGame (game: Game) =
    started <- true
    loadGeneration <- loadGeneration + 1
    let generation = loadGeneration
    markCurrent (Some game) true
    showOverlay "Loading…"

    let progress received total =
        if generation = loadGeneration then
            showOverlay (loadingText received total)

    async {
        match! fetchRom game progress |> Async.Catch with
        | Choice1Of2 bytes when generation = loadGeneration ->
            try
                startEmulator bytes
            with _ ->
                ()
        | Choice2Of2 _ when generation = loadGeneration -> showOverlay "Error!<br>Couldn't download game"
        | _ -> ()
    }
    |> Async.StartImmediate

/// The "2P" badge of a link-cable game. Outside link mode it reloads straight
/// into a two-player game.
let private linkBadge (game: Game) =
    let badge = document.createElement (if linkModeParam then "span" else "a")
    badge.className <- "game-badge"
    badge.textContent <- "2P"

    if linkModeParam then
        badge.title <- "Two players over the link cable"
    else
        badge.setAttribute ("href", $"?link=1&game={game.Id}")
        badge.title <- $"Play {game.Title} with two players"

    badge

let private gameCard (game: Game) =
    let item = document.createElement "li"
    item.className <- "game-item"

    let card = document.createElement "button" :?> HTMLButtonElement
    card.setAttribute ("type", "button")
    card.className <- "game-card"
    card?dataset?gameId <- game.Id
    card.title <- $"{game.Title} by {game.Author} ({game.License})"

    let image = document.createElement "img" :?> HTMLImageElement
    image.src <- game.Screenshot
    image.alt <- ""
    image.setAttribute ("loading", "lazy")

    let title = document.createElement "span"
    title.className <- "game-card-title"
    title.textContent <- game.Title

    let genre = document.createElement "span"
    genre.className <- "game-card-genre"
    genre.textContent <- game.Genre

    card.appendChild image |> ignore
    card.appendChild title |> ignore
    card.appendChild genre |> ignore
    card.addEventListener ("click", fun _ -> playGame game)
    item.appendChild card |> ignore

    if game.LinkCable then
        item.appendChild (linkBadge game) |> ignore

    item

// Cards go before the "More games" tile, which stays last.
let private moreGamesTile = gameList.querySelector ".game-item-more"

for game in featured do
    gameList.insertBefore (gameCard game, moreGamesTile) |> ignore

let private moreGamesDialog = getElement "more-games"
let private moreGamesList = getElement "more-games-list"
let private moreGamesSearch = getElement "more-games-search" :?> HTMLInputElement
let private moreGamesStatus = getElement "more-games-status"
let private moreGamesSort = getElement "more-games-sort" :?> HTMLSelectElement
let private moreGamesPlatform = getElement "more-games-platform" :?> HTMLSelectElement
let private moreGamesGenre = getElement "more-games-genre" :?> HTMLSelectElement
let private moreGamesLicensed = getElement "more-games-licensed" :?> HTMLInputElement

/// Genre filter value that selects the link-cable games instead of a genre.
[<Literal>]
let private twoPlayers = "@2p"

/// Fills the genre filter, "2 players" then the most common genre first,
/// once the catalogue is in.
let private fillGenres (games: CatalogueGame list) =
    if moreGamesGenre.options.length <= 1 then
        let linkGames = games |> List.filter (fun g -> g.Game.LinkCable) |> List.length

        (twoPlayers, $"2 players ({linkGames})")
        :: (games
            |> List.collect (fun g -> List.ofArray g.Genres)
            |> List.countBy id
            |> List.sortBy (fun (genre, count) -> -count, genre)
            |> List.map (fun (genre, count) -> genre, $"{genre} ({count})"))
        |> List.iter (fun (value, label) ->
            let option = document.createElement "option" :?> HTMLOptionElement
            option.value <- value
            option.textContent <- label
            moreGamesGenre.appendChild option |> ignore)

/// "2021", or "added 2021" when the release date is unknown.
let private yearLabel (entry: CatalogueGame) =
    if entry.Released <> "" then entry.Released.Substring(0, 4)
    elif entry.Added <> "" then $"added {entry.Added.Substring(0, 4)}"
    else ""

let private renderMoreGames (games: CatalogueGame list) =
    fillGenres games
    let query = moreGamesSearch.value.Trim().ToLowerInvariant()
    let platform = moreGamesPlatform.value
    let genre = moreGamesGenre.value
    let licensedOnly = moreGamesLicensed.``checked``
    let byTitle (e: CatalogueGame) = e.Game.Title.ToLowerInvariant()

    let matches =
        games
        |> List.filter (fun e ->
            (query = ""
             || e.Game.Title.ToLowerInvariant().Contains query
             || e.Game.Author.ToLowerInvariant().Contains query)
            && (platform = "" || e.Platform = platform)
            && (genre = ""
                || (genre = twoPlayers && e.Game.LinkCable)
                || Array.contains genre e.Genres)
            && (not licensedOnly || e.HasLicense))
        |> match moreGamesSort.value with
           | "title-desc" -> List.sortByDescending byTitle
           | "newest" -> List.sortBy byTitle >> List.sortByDescending (fun e -> e.Date)
           | "oldest" -> List.sortBy byTitle >> List.sortBy (fun e -> e.Date)
           | _ -> List.sortBy byTitle

    moreGamesList.textContent <- ""

    for entry in matches do
        let game = entry.Game
        let item = document.createElement "li"

        let button = document.createElement "button" :?> HTMLButtonElement
        button.setAttribute ("type", "button")
        button.className <- "more-games-item"

        let title = document.createElement "span"
        title.className <- "more-games-title"
        title.textContent <- game.Title

        let details = document.createElement "span"
        details.className <- "more-games-details"
        details.textContent <-
            [ game.Author; entry.Platform; yearLabel entry; String.Join(", ", entry.Genres); game.License ]
            |> List.filter (fun part -> part <> "")
            |> String.concat " · "

        button.appendChild title |> ignore
        button.appendChild details |> ignore

        button.addEventListener (
            "click",
            fun _ ->
                moreGamesDialog?close ()
                playGame game
        )

        let info = document.createElement "a" :?> HTMLAnchorElement
        info.className <- "more-games-info"
        info.href <- game.Info
        info.target <- "_blank"
        info.rel <- "noopener noreferrer"
        info.textContent <- "Info"
        info.setAttribute ("aria-label", $"About {game.Title}")

        item.appendChild button |> ignore

        if game.LinkCable then
            item.appendChild (linkBadge game) |> ignore

        item.appendChild info |> ignore
        moreGamesList.appendChild item |> ignore

    moreGamesStatus.textContent <- $"{matches.Length} of {games.Length} games"

(getElement "more-games-button").addEventListener (
    "click",
    fun _ ->
        moreGamesDialog?showModal ()

        async {
            match! loadMoreGames () |> Async.Catch with
            | Choice1Of2 games -> renderMoreGames games
            | Choice2Of2 _ -> moreGamesStatus.textContent <- "Couldn't load the game list."
        }
        |> Async.StartImmediate
)

moreGamesSearch.addEventListener ("input", fun _ -> moreGames |> Option.iter renderMoreGames)

for control in [ moreGamesSort :> HTMLElement; moreGamesPlatform; moreGamesGenre; moreGamesLicensed ] do
    control.addEventListener ("change", fun _ -> moreGames |> Option.iter renderMoreGames)

let private onFileLoaded (ev: Event) =
    let input = ev.target :?> HTMLInputElement
    let files = input.files

    if not (isNull files) && files.length > 0 then
        let file = files.[0]
        let reader = FileReader.Create()
        started <- true
        loadGeneration <- loadGeneration + 1
        let generation = loadGeneration

        reader.onload <-
            fun _ ->
                if generation = loadGeneration then
                    markCurrent None true

                    try
                        startEmulator (toBytes (reader.result :?> JS.ArrayBuffer))
                    with _ ->
                        ()

        reader.readAsArrayBuffer file

fileUploadButton.addEventListener ("change", onFileLoaded)

// Pre-fetch the startup game (?game=<id>, or the demo), then wait for user
// interaction to start: Web Audio needs it. Picking a game starts directly.
let mutable private startupRom: byte array option = None

let private onFirstInteraction (ev: Event) =
    let isGameChoice = not (isNull (ev.target?closest (".game-shelf, #more-games, .rom-upload")))

    if not (started || isGameChoice) then
        started <- true
        startupRom |> Option.iter startEmulator

let private startupGame () =
    async {
        match gameParam with
        | Some id ->
            match featured |> List.tryFind (fun g -> g.Id = id) with
            | Some game -> return game
            | None ->
                match! loadMoreGames () |> Async.Catch with
                | Choice1Of2 games ->
                    match games |> List.tryFind (fun e -> e.Game.Id = id) with
                    | Some entry -> return entry.Game
                    | None -> return featured.Head
                | Choice2Of2 _ -> return featured.Head
        | None -> return featured.Head
    }

let loadStartupGame () =
    async {
        let! game = startupGame ()

        let progress received total =
            if not started then
                showOverlay (loadingText received total)

        match! fetchRom game progress |> Async.Catch with
        | Choice1Of2 bytes when not started ->
            startupRom <- Some bytes
            showOverlay "Press any<br>button to start"
            // An unknown ?game= falls back to the demo, and the URL follows.
            markCurrent (Some game) gameParam.IsSome
            document.addEventListener ("click", onFirstInteraction)
            document.addEventListener ("keydown", onFirstInteraction)
        | Choice2Of2 _ when not started -> showOverlay "Error!<br>Couldn't load game"
        | _ -> ()
    }
    |> Async.StartImmediate

loadStartupGame ()
