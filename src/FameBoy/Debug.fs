module FameBoy.Debug

open System

module CgbTrace =
    let mutable Enabled = false

    let private lastSeen = Collections.Generic.Dictionary<string, int64>()

    let private tick64 = Environment.TickCount64

    let logRare (key: string) (minIntervalMs: int64) (message: unit -> string) =
        if Enabled then
            let now = tick64
            let mutable last = 0L

            if lastSeen.TryGetValue(key, &last) then
                if now - last >= minIntervalMs then
                    lastSeen[key] <- now
                    printfn "%s" (message ())
            else
                lastSeen[key] <- now
                printfn "%s" (message ())

    let logOnce key message = logRare key Int64.MaxValue message
