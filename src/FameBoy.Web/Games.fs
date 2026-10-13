module FameBoy.Web.Games

/// A free homebrew game the player can start with one click. ROMs are not
/// part of this repository (except the bundled demo): the browser downloads
/// them from the author's or Homebrew Hub's repository, pinned to a commit.
type Game =
    { Id: string
      Title: string
      Author: string
      Genre: string
      /// Supports two players over the link cable.
      LinkCable: bool
      License: string
      /// Absolute URL, or a file name served next to index.html.
      Rom: string
      Screenshot: string
      Info: string }

[<Literal>]
let private homebrewHub = "https://raw.githubusercontent.com/gbdev/database/50293559a496a3e20382fbf6a2e84b70ec622f88/entries/"
[<Literal>]
let private onitama = "https://raw.githubusercontent.com/Boniato82/Onitama-pocket/03e0ea4738248b1a65006f860b09d439a6e441e5/"

/// The shelf, in display order; the first game is the demo that starts by default.
let featured =
    [ { Id = "tobudx"
        Title = "Tobu Tobu Girl Deluxe"
        Author = "Tangram Games"
        Genre = "Arcade platformer"
        LinkCable = false
        License = "MIT / CC-BY 4.0"
        Rom = "tobudx.gb"
        Screenshot = homebrewHub + "tobutobugirldeluxe/1.png"
        Info = "https://tangramgames.itch.io/tobu-tobu-girl-deluxe" }
      { Id = "petris"
        Title = "Petris"
        Author = "bbbbbr"
        Genre = "Falling-block puzzle"
        LinkCable = true
        License = "CC-BY-NC-SA 4.0"
        Rom = homebrewHub + "petris/Petris_web.gbc"
        Screenshot = homebrewHub + "petris/petris_001.png"
        Info = "https://github.com/bbbbbr/Petris" }
      { Id = "airaki"
        Title = "Airaki"
        Author = "furrtek"
        Genre = "Puzzle battle"
        LinkCable = true
        License = "GPL-3.0"
        Rom = homebrewHub + "airaki/airaki.gb"
        Screenshot = homebrewHub + "airaki/screenshot.png"
        Info = "https://github.com/furrtek/Airaki" }
      { Id = "onitama"
        Title = "Onitama Pocket"
        Author = "Boniato82"
        Genre = "Strategy board game"
        LinkCable = true
        License = "GPL-3.0"
        Rom = onitama + "BIN/Onitama%20Pocket.gbc"
        Screenshot = onitama + "MEDIA/title.jpg"
        Info = "https://github.com/Boniato82/Onitama-pocket" }
      { Id = "snake"
        Title = "Snake"
        Author = "Christian Reinbacher"
        Genre = "Classic snake"
        LinkCable = true
        License = "MIT"
        Rom = homebrewHub + "gb-snake-reini1305/snake.gb"
        Screenshot = homebrewHub + "gb-snake-reini1305/title.png"
        Info = "https://github.com/reini1305/gb_snake" }
      { Id = "ucity"
        Title = "µCity"
        Author = "AntonioND"
        Genre = "City builder"
        LinkCable = false
        License = "GPL-3.0"
        Rom = homebrewHub + "ucity/ucity.gbc"
        Screenshot = homebrewHub + "ucity/screenshot1.png"
        Info = "https://github.com/AntonioND/ucity" }
      { Id = "shock-lobster"
        Title = "Shock Lobster"
        Author = "tbsp"
        Genre = "Arcade action"
        LinkCable = false
        License = "Zlib"
        Rom = homebrewHub + "shock-lobster/shocklobster.gb"
        Screenshot = homebrewHub + "shock-lobster/sh.png"
        Info = "https://github.com/tbsp/shock-lobster" }
      { Id = "tuff"
        Title = "Tuff"
        Author = "BonsaiDen"
        Genre = "Exploration platformer"
        LinkCable = false
        License = "MIT"
        Rom = homebrewHub + "tuff/game.gb"
        Screenshot = homebrewHub + "tuff/1.png"
        Info = "https://github.com/BonsaiDen/Tuff.gb" }
      { Id = "libbet"
        Title = "Libbet and the Magic Floor"
        Author = "Damian Yerrick"
        Genre = "Puzzle"
        LinkCable = false
        License = "Zlib"
        Rom = homebrewHub + "libbet/libbet.gb"
        Screenshot = homebrewHub + "libbet/libbet6x4.png"
        Info = "https://github.com/pinobatch/libbet" }
      { Id = "2048"
        Title = "2048"
        Author = "Sanqui"
        Genre = "Number puzzle"
        LinkCable = false
        License = "Zlib"
        Rom = homebrewHub + "2048gb/2048.gb"
        Screenshot = homebrewHub + "2048gb/1.png"
        Info = "https://github.com/Sanqui/2048-gb" }
      { Id = "renegade-rush"
        Title = "Renegade Rush"
        Author = "quinnp"
        Genre = "Car combat"
        LinkCable = false
        License = "MIT"
        Rom = homebrewHub + "renegade-rush/RenegadeRush.gb"
        Screenshot = homebrewHub + "renegade-rush/1.png"
        Info = "https://github.com/QuinnPainter/Renegade-Rush" } ]
