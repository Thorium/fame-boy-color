module FameBoy.Serial

open FameBoy.Interrupts
open FameBoy.Hardware
open FameBoy.IoController

let private getCyclesPerByte (io: IoController) (sc: uint8) =
    // stepSerial is already clocked in normal-speed M-cycles by Emulator.stepper,
    // including while the CPU is in CGB double-speed mode, so serial timing here
    // should not apply an extra double-speed adjustment.
    let fastClock = io.CgbMode && (sc &&& 0x02uy <> 0uy)

    if fastClock then 4 * 8 else 128 * 8

/// Minimal one-shot diagnostics for the in-process link cable. Announces
/// when the link cable is connected. Per-byte and per-override tracing has
/// been removed; reintroduce ad-hoc when debugging.
module private LinkLog =
    let mutable private announced = false

    let announce () =
        if not announced then
            announced <- true
            printfn "[LINK] Connected."

/// Shared link-cable arbitration state for the currently active transfer.
/// Unlike the previous implementation, ownership is not locked for the whole
/// session: the master can change on later transfers.
type LinkArbiter() =
    let mutable activeMasterIsP1: bool option = None

    member _.RoleFor(isP1: bool) : bool option =
        match activeMasterIsP1 with
        | None -> None
        | Some m -> Some (m = isP1)

    member _.Clear() = activeMasterIsP1 <- None

    member _.ChooseMaster(preferP1: bool) =
        if activeMasterIsP1.IsNone then
            activeMasterIsP1 <- Some preferP1

type SerialState =
    { mutable Counter: int
      mutable IsTransferring: bool
      /// Outgoing byte to send to the link partner, latched from SB at
      /// transfer start (master) or refreshed from SB each tick (slave).
      mutable OutgoingByte: uint8
      /// Shared arbiter that decides which side is master in link mode.
      /// None for standalone (no link). Both paired SerialStates share the
      /// same arbiter instance.
      mutable LinkArbiter: LinkArbiter option
      /// Identifies this device's position in the link pair so the arbiter
      /// can resolve roles. true = P1 (left), false = P2 (right).
      mutable IsLinkP1: bool
      /// Reference to the peer's SerialState in link mode. Used by the
      /// master's stepSerial to deliver bytes to the slave at the exact
      /// emulated moment the master's clock completes — keeping master
      /// and slave receive timing symmetric. None for standalone.
      mutable LinkPeer: SerialState option
      /// Reference to the peer's IoController in link mode. The master
      /// writes to peer SB / SC at clock-completion time.
      mutable LinkPeerIo: IoController option }

let createSerial () =
    { Counter = 0
      IsTransferring = false
      OutgoingByte = 0xFFuy
      LinkArbiter = None
      IsLinkP1 = true
      LinkPeer = None
      LinkPeerIo = None }

/// Resolve the device's role for the currently pending transfer. If exactly
/// one side requests internal clock, it becomes the active master. If both
/// request internal clock simultaneously, break the tie deterministically in
/// favor of P1 for that one transfer.
let private applyArbitration (state: SerialState) (io: IoController) : uint8 =
    let scInit = io.Registers[Io.Sc]

    match state.LinkArbiter with
    | None -> scInit
    | Some arbiter ->
        match state.LinkPeerIo with
        | Some peerIo when scInit &&& 0x80uy <> 0uy ->
            let selfRequestsMaster = scInit &&& 0x81uy = 0x81uy
            let peerSc = peerIo.Registers[Io.Sc]
            let peerRequestsMaster = peerSc &&& 0x81uy = 0x81uy

            if selfRequestsMaster && not peerRequestsMaster then
                arbiter.ChooseMaster(state.IsLinkP1)
            elif peerRequestsMaster && not selfRequestsMaster then
                arbiter.ChooseMaster(not state.IsLinkP1)
            elif selfRequestsMaster && peerRequestsMaster then
                arbiter.ChooseMaster(true)

            match arbiter.RoleFor(state.IsLinkP1) with
            | Some isMasterRole ->
                let newSc =
                    if isMasterRole then scInit ||| 0x01uy
                    else scInit &&& 0b1111_1110uy

                if newSc <> scInit then
                    io.Registers[Io.Sc] <- newSc
                    newSc
                else
                    scInit
            | None -> scInit
        | _ ->
            arbiter.Clear()
            scInit

/// Master's clock has completed. Deliver the byte to the master, and
/// simultaneously to the slave if it is listening (SC bit 7 set). This
/// keeps master and slave receive timing symmetric: both observe the byte
/// at the same emulated m-cycle. Without symmetry, slaves would receive
/// bytes ~1000x faster than masters (per-frame swap vs per-1024-m-cycle
/// clock), which causes identical ROMs to take divergent code paths
/// purely from perceived link speed.
let private completeMasterTransfer (state: SerialState) (io: IoController) (sc: uint8) =
    let peerByte =
        match state.LinkPeer, state.LinkPeerIo with
        | Some peer, Some peerIo when peerIo.Registers[Io.Sc] &&& 0x80uy <> 0uy -> peer.OutgoingByte
        | _ -> 0xFFuy // unlinked or unarmed peer: input is pulled high

    // Master delivery.
    io.Registers[Io.Sb] <- peerByte
    io.Registers[Io.Sc] <- sc &&& 0b0111_1111uy
    io.TriggerInterrupt InterruptType.Serial

    // Slave delivery (only if it's listening).
    match state.LinkPeer, state.LinkPeerIo with
    | Some peer, Some peerIo when peerIo.Registers[Io.Sc] &&& 0x80uy <> 0uy ->
        peerIo.Registers[Io.Sb] <- state.OutgoingByte
        peerIo.Registers[Io.Sc] <- peerIo.Registers[Io.Sc] &&& 0b0111_1111uy
        peer.IsTransferring <- false
        peerIo.TriggerInterrupt InterruptType.Serial
    | _ -> ()

let stepSerial (state: SerialState) (io: IoController) =
    let sc = applyArbitration state io
    let isMaster = sc &&& 0x01uy <> 0uy
    let transferRequested = sc &&& 0x80uy <> 0uy

    if state.IsTransferring then
        if isMaster then
            // Master drives the clock. Track the slave's live SB so when
            // the byte completes we deliver the freshest staged value
            // (mirrors a real shift register: slave can update SB right
            // up until the master clocks the next bit out).
            match state.LinkPeer, state.LinkPeerIo with
            | Some peer, Some peerIo -> peer.OutgoingByte <- peerIo.Registers[Io.Sb]
            | _ -> ()

            state.Counter <- state.Counter + 1

            if state.Counter >= getCyclesPerByte io sc then
                state.Counter <- 0
                state.IsTransferring <- false
                completeMasterTransfer state io sc

                match state.LinkArbiter with
                | Some arbiter -> arbiter.Clear ()
                | None -> ()
        else
            // Slave: track the live SB so master sees the freshest
            // outgoing byte. Receipt and IRQ are driven by the master's
            // completeMasterTransfer at clock completion.
            state.OutgoingByte <- io.Registers[Io.Sb]
    elif transferRequested then
        state.IsTransferring <- true
        state.Counter <- 0
        state.OutgoingByte <- io.Registers[Io.Sb]

/// Frontend hook called after stepping both emulators each frame. Reserved
/// for future arbiter observation / diagnostics; currently a no-op.
/// Byte exchange is performed inside `stepSerial` on the master device,
/// not here, to keep master/slave receive timing symmetric.
let exchangeSerial (_serial1: SerialState) (_io1: IoController) (_serial2: SerialState) (_io2: IoController) =
    ()

/// Wire two serial states together as link-cable peers, sharing one
/// arbiter. Roles are decided dynamically at runtime by whichever side
/// first writes SC=0x81. Call once during frontend setup.
let pairLink
    (serial1: SerialState) (io1: IoController)
    (serial2: SerialState) (io2: IoController) =
    let arbiter = LinkArbiter()
    LinkLog.announce ()
    serial1.LinkArbiter <- Some arbiter
    serial1.IsLinkP1 <- true
    serial1.LinkPeer <- Some serial2
    serial1.LinkPeerIo <- Some io2

    serial2.LinkArbiter <- Some arbiter
    serial2.IsLinkP1 <- false
    serial2.LinkPeer <- Some serial1
    serial2.LinkPeerIo <- Some io1
