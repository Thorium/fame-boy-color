// Builds public/moregames.json, the "More games" catalogue, from the Homebrew
// Hub API (https://hh3.gbdev.io), whose entries live in
// https://github.com/gbdev/database. Only metadata is written: ROMs are
// fetched by the browser from that repository, pinned to its current commit,
// when the player picks a game.
//
//   dotnet fsi build-moregames.fsx [--licensed-only]
//
// --licensed-only keeps just the entries that state a license.

open System
open System.Globalization
open System.IO
open System.Net.Http
open System.Text.Json
open System.Text.Json.Nodes

let args = fsi.CommandLineArgs |> Array.skip 1
let licensedOnly = args |> Array.contains "--licensed-only"

let http = new HttpClient()
http.DefaultRequestHeaders.UserAgent.ParseAdd "fame-boy-color-build-moregames"

let getJson (url: string) =
    use doc = JsonDocument.Parse(http.GetStringAsync(url).Result)
    doc.RootElement.Clone()

/// The database commit the ROM URLs are pinned to.
let commit =
    (getJson "https://api.github.com/repos/gbdev/database/commits/master").GetProperty("sha").GetString()

/// All game entries of one platform; the API serves ten per page. Paging
/// needs a unique sort key: the default sort (date added) has ties, and
/// tied entries repeat or go missing across pages.
let searchPlatform (platform: string) =
    let page n = getJson $"https://hh3.gbdev.io/api/search?typetag=game&platform={platform}&sort=slug&order=asc&page={n}"
    let first = page 1

    [ yield first
      for n in 2 .. first.GetProperty("page_total").GetInt32() do
          yield page n ]
    |> List.collect (fun p -> p.GetProperty("entries").EnumerateArray() |> List.ofSeq)

// Fan games named after another company's franchise stay out of the list.
let franchiseWords =
    [ "mario"; "luigi"; "pokemon"; "pokémon"; "zelda"; "tetris"; "pacman"; "pac-man"; "bomberman"
      "sonic"; "kirby"; "metroid"; "donkey kong"; "street fighter"; "mortal kombat"; "final fantasy" ]

// Entries whose ROM here has a two-player link-cable mode that was played
// through in FameBoy's link mode. The database has no field for it.
// gb-corp is version 1.0; its link mode arrived in 2.0.
let linkCableGames = set [ "petris"; "gb-snake-reini1305"; "airaki"; "together-alone" ]

// Free-form tags folded into a small genre vocabulary for the filter.
let genreByTag =
    Map.ofList
        [ "action", "Action"; "adventure", "Adventure"; "arcade", "Arcade"; "high score", "Arcade"
          "platform", "Platformer"; "platformer", "Platformer"; "puzzle", "Puzzle"
          "rpg", "RPG"; "role playing", "RPG"; "shooter", "Shooter"; "shmup", "Shooter"
          "simulation", "Simulation"; "virtual pet", "Simulation"
          "visual novel", "Story"; "interactive fiction", "Story"
          "rhythm", "Music"; "music", "Music"; "racing", "Racing"; "sports", "Sports"
          "strategy", "Strategy"; "card game", "Strategy"; "horror", "Horror"
          "survival", "Survival"; "fighting", "Fighting" ]

let isFranchiseTitle (title: string) =
    let lower = title.ToLowerInvariant()
    franchiseWords |> List.exists lower.Contains

// JsonElement rather than JsonNode: a few entries repeat a key, which
// JsonDocument tolerates (the last definition wins).
let str (element: JsonElement) (name: string) =
    match element.TryGetProperty name with
    | true, value when value.ValueKind = JsonValueKind.String -> value.GetString().Trim()
    | true, value when value.ValueKind = JsonValueKind.Number -> value.ToString()
    | _ -> ""

let strictUtf8 = Text.UTF8Encoding(false, true)

/// Repairs UTF-8 text that was decoded as Latin-1 ("Ã©" for "é"): when every
/// character fits in a byte and those bytes are valid UTF-8 that decodes to
/// fewer characters, the decoded text is the intended one.
let fixMojibake (text: string) =
    if text |> Seq.forall (fun c -> int c < 256) && text |> Seq.exists (fun c -> int c >= 128) then
        try
            let decoded = strictUtf8.GetString(Text.Encoding.Latin1.GetBytes text)
            if decoded.Length < text.Length then decoded else text
        with :? Text.DecoderFallbackException ->
            text
    else
        text

/// "developer" is a name, a list of names, or {"name": ..., "link": ...}.
let rec developerName (value: JsonElement) =
    match value.ValueKind with
    | JsonValueKind.String -> value.GetString().Trim()
    | JsonValueKind.Array -> value.EnumerateArray() |> Seq.map developerName |> Seq.filter ((<>) "") |> String.concat ", "
    | JsonValueKind.Object -> str value "name"
    | _ -> ""

let developer (game: JsonElement) =
    match game.TryGetProperty "developer" with
    | true, value -> developerName value
    | _ -> ""

let releaseFormats =
    [| "yyyy-MM-dd"; "yyyy-M-d"; "yyyy/MM/dd"; "yyyyMMdd"; "dd/MM/yyyy HH:mm:ss"; "dd/MM/yyyy"; "dd-MM-yyyy" |]

/// Release date as yyyy-MM-dd, or yyyy when only the year is known.
/// The database mixes several formats and Unix timestamps.
let releaseDate (game: JsonElement) =
    let raw = str game "date"
    let plausible (d: DateTime) = d.Year >= 1989 && d <= DateTime.UtcNow.AddYears 1

    match DateTime.TryParseExact(raw, releaseFormats, CultureInfo.InvariantCulture, DateTimeStyles.None) with
    | true, d when plausible d -> d.ToString "yyyy-MM-dd"
    | _ ->
        match Int64.TryParse raw with
        | true, n when raw.Length = 4 && n >= 1989L -> raw
        | true, n when raw.Length = 10 ->
            let d = DateTimeOffset.FromUnixTimeSeconds(n).UtcDateTime
            if plausible d then d.ToString "yyyy-MM-dd" else ""
        | _ -> ""

let genres (game: JsonElement) =
    match game.TryGetProperty "tags" with
    | true, tags when tags.ValueKind = JsonValueKind.Array ->
        tags.EnumerateArray()
        |> Seq.choose (fun t ->
            if t.ValueKind = JsonValueKind.String then Map.tryFind (t.GetString().Trim().ToLowerInvariant()) genreByTag
            else None)
        |> Seq.distinct
        |> Seq.sort
        |> Seq.toArray
    | _ -> [||]

let license (game: JsonElement) =
    match str game "license" with
    | "" -> str game "gameLicense"
    | l -> l

let romFile (game: JsonElement) =
    match game.TryGetProperty "files" with
    | true, files when files.ValueKind = JsonValueKind.Array ->
        let candidates =
            files.EnumerateArray()
            |> Seq.filter (fun f -> f.ValueKind = JsonValueKind.Object)
            |> Seq.filter (fun f ->
                let name = (str f "filename").ToLowerInvariant()
                name.EndsWith ".gb" || name.EndsWith ".gbc")
            |> Seq.toList

        candidates
        |> List.tryFind (fun f ->
            match f.TryGetProperty "default" with
            | true, value -> value.ValueKind = JsonValueKind.True
            | _ -> false)
        |> Option.orElse (List.tryHead candidates)
        |> Option.map (fun f -> str f "filename")
    | _ -> None

/// "2023-07-25T11:22:36Z" -> "2023-07-25".
let addedDate (game: JsonElement) =
    match str game "firstadded_date" with
    | d when d.Length >= 10 -> d.Substring(0, 10)
    | _ -> ""

/// Games outside Homebrew Hub, played through in FameBoy, with their ROM
/// pinned to a commit of the author's repository.
let extraEntries =
    [ {| id = "onitama"
         title = "Onitama Pocket"
         author = "Boniato82"
         platform = "GBC"
         license = "GPL-3.0"
         released = "2026-04-12"
         genres = [ "Strategy" ]
         romUrl = "https://raw.githubusercontent.com/Boniato82/Onitama-pocket/03e0ea4738248b1a65006f860b09d439a6e441e5/BIN/Onitama%20Pocket.gbc"
         info = "https://github.com/Boniato82/Onitama-pocket"
         linkCable = true |}
      {| id = "crucible-exe"
         title = "CRUCIBLE.EXE"
         author = "Ravonus"
         platform = "GBC"
         license = "MIT"
         released = "2026-10-05"
         genres = [ "Puzzle"; "Adventure" ]
         romUrl = "https://raw.githubusercontent.com/Ravonus/crucible.exe/222c661515bc1009447d82d912876a3ad626cfde/release/crucible.exe.gbc"
         info = "https://github.com/Ravonus/crucible.exe"
         linkCable = true |}
      {| id = "heroes-tactics"
         title = "Heroes Tactics"
         author = "itIsBrando"
         platform = "GB"
         license = ""
         released = "2022-08-11"
         genres = [ "Strategy" ]
         romUrl = "https://raw.githubusercontent.com/itIsBrando/Heroes-Tactics-GB/4cca10e45010b2db832f5572f38f0743f0f73890/main.gb"
         info = "https://github.com/itIsBrando/Heroes-Tactics-GB"
         linkCable = true |} ]
    |> List.map (fun e ->
        let obj = JsonObject()
        obj["id"] <- JsonValue.Create e.id
        obj["title"] <- JsonValue.Create e.title
        obj["author"] <- JsonValue.Create e.author
        obj["platform"] <- JsonValue.Create e.platform
        obj["license"] <- JsonValue.Create e.license
        obj["released"] <- JsonValue.Create e.released
        obj["added"] <- JsonValue.Create ""
        obj["genres"] <- JsonArray(e.genres |> List.map (fun g -> JsonValue.Create g :> JsonNode) |> Array.ofList)
        obj["romUrl"] <- JsonValue.Create e.romUrl
        obj["info"] <- JsonValue.Create e.info

        if e.linkCable then
            obj["linkCable"] <- JsonValue.Create true

        obj)

let hubEntries =
    // The slug is the entry's directory in gbdev/database.
    [ "GB"; "GBC" ]
    |> List.collect searchPlatform
    |> List.distinctBy (fun game -> str game "slug")
    |> List.filter (fun game ->
        str game "slug" <> ""
        && str game "title" <> ""
        && not (isFranchiseTitle (str game "title"))
        && (not licensedOnly || license game <> ""))
    |> List.toArray
    |> Array.choose (fun game ->
        romFile game
        |> Option.map (fun rom ->
            let slug = str game "slug"
            // The API files entries without a platform under GB; the ROM's extension tells.
            let platform =
                match str game "platform" with
                | "" when rom.EndsWith(".gbc", StringComparison.OrdinalIgnoreCase) -> "GBC"
                | "" -> "GB"
                | p -> p
            let path = (slug + "/" + rom).Split '/' |> Array.map Uri.EscapeDataString |> String.concat "/"
            let obj = JsonObject()
            obj["id"] <- JsonValue.Create slug
            obj["title"] <- JsonValue.Create(fixMojibake (str game "title"))
            obj["author"] <- JsonValue.Create(fixMojibake (developer game))
            obj["platform"] <- JsonValue.Create platform
            obj["license"] <- JsonValue.Create(license game)
            obj["released"] <- JsonValue.Create(releaseDate game)
            obj["added"] <- JsonValue.Create(addedDate game)
            obj["genres"] <- JsonArray(genres game |> Array.map (fun g -> JsonValue.Create g :> JsonNode))
            obj["rom"] <- JsonValue.Create path

            if linkCableGames.Contains slug then
                obj["linkCable"] <- JsonValue.Create true

            obj))

let hubIds = hubEntries |> Array.map (fun o -> o["id"].ToString()) |> Set.ofArray

let romBase = $"https://raw.githubusercontent.com/gbdev/database/{commit}/entries/"

let romUrl (entry: JsonObject) =
    match entry["romUrl"] with
    | null -> romBase + entry["rom"].ToString()
    | url -> url.ToString()

/// Whether the ROM URL answers 200. The API can list entries whose files
/// are no longer in the repository.
let romExists (entry: JsonObject) =
    async {
        use request = new HttpRequestMessage(HttpMethod.Head, romUrl entry)
        use! response = http.SendAsync request |> Async.AwaitTask
        return response.IsSuccessStatusCode
    }

// An extra entry gives way once Homebrew Hub lists the game itself.
let candidates =
    extraEntries
    |> List.filter (fun o -> not (hubIds.Contains(o["id"].ToString())))
    |> Array.ofList
    |> Array.append hubEntries

let reachable =
    Async.Parallel(candidates |> Array.map romExists, maxDegreeOfParallelism = 16)
    |> Async.RunSynchronously

let entries =
    Array.zip candidates reachable
    |> Array.choose (fun (entry, ok) ->
        if not ok then
            let pid = entry["id"]
            eprintfn $"  skipped {pid}: no ROM at {romUrl entry}"

        if ok then Some entry else None)
    |> Array.sortBy (fun o -> o["title"].ToString().ToLowerInvariant())

let catalogue = JsonObject()
catalogue["source"] <- JsonValue.Create "https://github.com/gbdev/database"
catalogue["commit"] <- JsonValue.Create commit
catalogue["romBase"] <- JsonValue.Create romBase
catalogue["infoBase"] <- JsonValue.Create "https://hh.gbdev.io/game/"
catalogue["games"] <- JsonArray(entries |> Array.map (fun e -> e :> JsonNode))

let output = Path.Combine(__SOURCE_DIRECTORY__, "public", "moregames.json")
let jsonOptions =
    JsonSerializerOptions(WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping)

File.WriteAllText(output, catalogue.ToJsonString jsonOptions + "\n")

let count predicate = entries |> Array.filter predicate |> Array.length
let released = count (fun o -> string o["released"] <> "")
let added = count (fun o -> string o["added"] <> "")
let withGenres = count (fun o -> o["genres"].AsArray().Count > 0)
printfn $"Wrote {entries.Length} games from gbdev/database@{commit.Substring(0, 7)} to {output}"
printfn $"  released: {released}, added: {added}, with genres: {withGenres}"
