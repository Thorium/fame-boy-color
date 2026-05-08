module FameBoy.Serial

open FameBoy.Interrupts
open FameBoy.Hardware
open FameBoy.IoController

let private getCyclesPerByte (io: IoController) (sc: uint8) =
    // stepSerial runs in normal-speed M-cycles.
    // DMG / CGB normal speed, slow clock: 128 M-cycles per bit = 1024 per byte.
    // CGB normal speed, fast clock: 4 M-cycles per bit = 32 per byte.
    // CGB double speed halves those normal-speed M-cycle counts.
    let fastClock = io.CgbMode && (sc &&& 0x02uy <> 0uy)

    match fastClock, io.DoubleSpeed with
    | false, false -> 128 * 8
    | false, true -> 64 * 8
    | true, false -> 4 * 8
    | true, true -> 2 * 8

/// Minimal one-shot diagnostics for the in-process link cable. Announces
/// when the master/slave roles are locked. Per-byte and per-override
/// tracing has been removed; reintroduce ad-hoc when debugging.
module private LinkLog =
    let mutable private announced = false

    let announce (masterLabel: string) (slaveLabel: string) =
        if not announced then
            announced <- true
            printfn $"[LINK] Connected. Master=%s{masterLabel}, Slave=%s{slaveLabel}."

/// Shared link-cable arbitration state, owned by the frontend and shared
/// between the two paired SerialStates. Tracks which device has claimed
/// the master role. None until the first device asserts SC=0x81 (transfer
/// requested with internal clock), after which the role is locked for the
/// remainder of the session.
///
/// There is intentionally no fallback timer. On real hardware, two paired
/// Game Boys at their title screens both sit at SC=0x80 (slave-listening)
/// indefinitely — neither escalates to SC=0x81 until a user navigates to
/// a multiplayer menu option (e.g. selecting "2-PLAYER" in Tetris). We
/// mirror that: if neither side ever asserts SC=0x81, no role is locked
/// and no exchange occurs.
type LinkArbiter() =
    let mutable masterIsP1: bool option = None

    /// Returns Some true if the given device is the locked master,
    /// Some false if locked slave, None if not yet locked.
    member _.RoleFor(isP1: bool) : bool option =
        match masterIsP1 with
        | None -> None
        | Some m -> Some (m = isP1)

    member _.IsLocked = masterIsP1.IsSome

    /// Lock the role: the given device becomes master.
    member _.LockMaster(isP1: bool) =
        if masterIsP1.IsNone then
            masterIsP1 <- Some isP1
            let masterLabel = if isP1 then "P1" else "P2"
            let slaveLabel = if isP1 then "P2" else "P1"
            LinkLog.announce masterLabel slaveLabel

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

/// Resolve the device's wired role and force its ROM-visible SC bit 0 to
/// match. Pre-lock, observe whether this device is voluntarily asserting
/// SC=0x81 and lock the arbiter to it. Returns the (possibly-modified) SC
/// value for the rest of stepSerial to use.
let private applyArbitration (state: SerialState) (io: IoController) : uint8 =
    let scInit = io.Registers[Io.Sc]

    match state.LinkArbiter with
    | None -> scInit
    | Some arbiter ->
        // Pre-lock: if this device is asserting itself as master
        // (SC=0x81), claim the role.
        if not arbiter.IsLocked && scInit &&& 0x81uy = 0x81uy then
            arbiter.LockMaster(state.IsLinkP1)

        // Override SC bit 0 to match the locked role, but only while a
        // transfer is pending (bit 7 set) so we don't disturb idle SC
        // bookkeeping the ROM may rely on between transfers.
        match arbiter.RoleFor(state.IsLinkP1) with
        | Some isMasterRole when scInit &&& 0x80uy <> 0uy ->
            let newSc =
                if isMasterRole then scInit ||| 0x01uy
                else scInit &&& 0b1111_1110uy
            if newSc <> scInit then
                io.Registers[Io.Sc] <- newSc
                newSc
            else
                scInit
        | _ -> scInit

/// Master's clock has completed. Deliver the byte to the master, and
/// simultaneously to the slave if it is listening (SC bit 7 set). This
/// keeps master and slave receive timing symmetric: both observe the byte
/// at the same emulated m-cycle. Without symmetry, slaves would receive
/// bytes ~1000x faster than masters (per-frame swap vs per-1024-m-cycle
/// clock), which causes identical ROMs to take divergent code paths
/// purely from perceived link speed.
let private completeMasterTransfer (state: SerialState) (io: IoController) (sc: uint8) =
    let peerByte =
        match state.LinkPeer with
        | Some peer -> peer.OutgoingByte
        | None -> 0xFFuy // unlinked: open-bus reads as 0xFF

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
    serial1.LinkArbiter <- Some arbiter
    serial1.IsLinkP1 <- true
    serial1.LinkPeer <- Some serial2
    serial1.LinkPeerIo <- Some io2

    serial2.LinkArbiter <- Some arbiter
    serial2.IsLinkP1 <- false
    serial2.LinkPeer <- Some serial1
    serial2.LinkPeerIo <- Some io1
