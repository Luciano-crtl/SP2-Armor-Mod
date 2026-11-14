# Ammo racks and cannon changes: context for Claude

The current, single source of truth for the ammo-rack feature and what it changed in cannons, damage and
vehicle death. Written by Jakniphe's Claude session; the decisions are Jakniphe's. Branch `ammo-rack`, built on
v0.7.0 (`7ff60c5`). Player-facing summary: `PATCH_NOTES.md`. (The short pointer in `claude.md` sends here.)

## Goal
Kill vehicles through damaged modules, not part explosions: the armor shell stays intact and the vehicle is
knocked out from the inside ("realistic volumetric kills"). Ammo racks are the first killable module; more are
planned. Every rule below serves that goal.

## Files
New:
- `AmmoRacks.cs`: runtime. Registries (`Craft` → `Group` → `Rack`), feeding, weight, damage and cook-off, the
  explosion and blast physics, knocked-out state, and every Harmony patch for racks (end of file).
- `AmmoRound.cs`: a round's size, mass, energy and explosive pounds, from a cannon's `CannonData`.
- `RackGrid.cs`: measures a rack's real shape, packs the rounds, says whether a line crosses a round still in it,
  removal order, and the rounds mesh.
- `RackVisualizer.cs`: designer see-through racks with their rounds (ghost copies, URP Lit materials).
- `AmmoRackXRay.cs`: live rounds of your own racks in the X-ray (F8 and the simulated shot).
- `VehicleRules.cs`: whether the game may explode a vehicle, and which parts take the mod's damage.
- `Net/RackFireSync.cs`: makes other players' copies of the cook-off fires look like the owner's (message 202).

Changed (yours): `FragmentSpawner.cs`, `ShellFlight.cs`, `Net/NetSync.cs`, `PartTags.cs`,
`ArmorFragmentsPlugin.cs`, `UI/DesignerSection.cs`, `XRayView.cs`, `ModuleOutputs.cs`, `ArmorFragments.csproj`
(details at the end).

## Tags
- `[AMM g=<group> o=<order>]` on a fuselage (`JFuselageData`): an ammo rack. `g` = text group (empty = default
  group "Ammo"), `o` = empty order (lower first; none = after the numbered ones).
- `[CAN ... g=<group>]`: the cannon draws from that group's racks. `g` sits next to `k z e q`.
- `[VNT]` on a fuselage: a cook-off vent (turret ring, hatch). Not armor.
- Armor is exclusive in the designer: turning `[ARM]` on removes `[MOD]`, `[AMM]`, `[VNT]`, and vice versa
  (armor takes no damage, so none of those could work on it). Hollow/cut fuselages and armor hold no rounds.

## Rounds (`AmmoRound`)
- Shell: the game's projectile. Mass = `ProjectileVolume x 11342` kg (the density `CannonScript.Fire` uses);
  length 1.5 calibers (Slug) or 1 (Sphere). Energy = ½ m v² at the muzzle.
- Case: diameter = `CaseDiameterRatio` (1.3) x caliber; volume = `CaseLitresPerMJ` (2) L per MJ of muzzle
  energy; length = max(caliber, volume / area). Calibrated on the Pz III's 5 cm KwK guns.
- Round mass = shell + 1 kg per litre of case (KwK 39 ≈ 3.5 kg).
- Explosive pounds per round = energy (kJ) x 0.53 x 1.1 / 718 (KwK 39 ≈ 0.58 lb; 90 rounds ≈ 55 lb).
- A group's racks are measured for the biggest round among its cannons.

## Shape and packing (`RackGrid`)
- Frame: Z along the fuselage (`JFuselageData` offset), X/Y across. Rounds lie along Z.
- Measured from the RENDER mesh (`XRayView.GhostSources`, skipping our own drawn objects), because designer
  colliders are bounding boxes. Sliced along Z (~1 cm, at most 1500 cuts), 5 scanlines per round circle,
  inside test by nonzero winding counted from both sides (robust to bad triangles). Collider fallback.
- Grid across: pitch = diameter x (1 + `RoundSpacingPercent`/100). Packing is per row: each (column, layer) row
  holds as many whole rounds end to end as fit its own length (shared slots left blocks of stepped racks empty).
- Removal order: top layer first, then left to right, then front to back.
- `HitsRounds(transform, point, direction, thickness)`: the line through the point (both ways) is clipped to
  the rounds' box and sampled; `RoundAt` checks rounds still present within radius x 1.0488 + thickness,
  looking at neighboring columns/layers when thickness > 0. Shells pass their radius (a tube of their
  caliber: a thin line threaded between packed rounds fired end-on); fragments are thin lines.
- `DebugDumpShape` writes the measured shape and rounds to `BepInEx/ammo-rack-shape.obj`.

## Feeding, weight
- `AmmoRackVariablesPatch` (postfix of `VariableSystemScript.RefreshVariables`): registers the vehicle's racks
  and creates the Funky Trees variables `<group>_rounds`, `<group>_fire`, `<group>_boom` (default group: `Ammo_`).
- `SyncCannons`: writes the group's rounds into `CannonScript._currentAmmo` (from `CannonData._ammoCount`).
  `AmmoRackFirePatch` (`CannonScript.Fire` prefix/postfix): blocks firing when the group has no round it can
  feed (`Group.Available`: burning or broken-off racks don't count), else takes a round from the first rack in
  order that can feed. Runs alongside your `CannonScalarsPatch`.
- `AmmoCountVisibilityPatch` (`PartModifierData.GetGenericDesignerPropertyVisibilityCallback`): hides Ammo
  Count on rack-fed cannons (`RefreshGenericPanels` re-asks after tag changes).
- Weight: the rack's `PartData.MassScale` grows by the rounds' mass, `RecalculateLoadedMass`. The body only
  applies mass changes over 1 game unit (100 kg). Center of mass doesn't follow.
- Broken off (`!ConnectedToMainCockpit`): keeps its rounds (can still cook off), stops feeding.

## Damage and cook-off chance
- `AmmoRackDamagePatch` (`PartScript.OnDamaged` prefix/postfix, racks only): the prefix caps the damage so the
  rack stops at 99.9% of `MaxHealth` (a rack is never destroyed or disabled by the game) and records the level
  before and the hit's share; the postfix calls `AmmoRacks.OnDamaged`.
- Per piece (your 0.7.0 batching sums a shot per part): `FragmentSpawner.Accumulate` calls
  `AmmoRacks.NotePiece(part, damage, point, dir, shell, shellEnergyJ, shellRadius)` before summing. While
  `DepositCrossings` runs (the shell's own path) it sets `depositingShell`, `shellEnergyIn` (`Crossed.EnergyIn`)
  and `shellRadius` (caliber from `ShellFlight`). Pieces whose path crosses a round still in the rack add their
  damage (and, for the shell, its carried energy) to that frame's tally; `OnDamaged` takes it (`TakeNoted`).
- Chance = 1 - (1 - `CookOffDirectHit`)^(shells through the rounds) x e^(-kJ / `CookOffEnergyKJ`), with
  kJ = (damage of pieces through the rounds) / `DamagePerKiloJoule` + (energy the shells carried in).
  Defaults 50% and 100 kJ: 20 mm APHE ≈ 64%, 37 mm ≈ 95%, ≥ 75 mm certain; fragments add up.
  Independent of the rack's health (a fuselage's health is large against kJ x DPK; health comes from the
  part's XML `health` / `PartType.Health`, not its mass).
- The hit that takes the rack to "destroyed" (≥ 0.998 of health) THROUGH its rounds always cooks it off. A hit
  that wrecks it without crossing rounds leaves them; later hits through them roll normally.
- Single-line hits (the game's own damage, a remote player's summed hit): the one line decides; kJ = damage /
  DPK, no direct-hit term.
- Remote racks are judged only by their owner's game (`RemoteAircraft` → skip).

## Burn and explosion
- `Ignite`: `KnockOut` the vehicle, the game's fire at half size (`DressBurnOut`: scale 0.5, particle scaling
  Hierarchy, `KeepBurning` = `_duration` MaxValue and `_requiresFuel` false, so only water puts it out), explode
  after `CookOffBurnSeconds` (10). A burning rack can't feed.
- `Explode`: pounds = rounds left x `ExplosiveLb` x `BlastStrength`, capped at `MaxBlastLb` (120). `_boom` = 1.
- `Blast` (returns where the blast leaves the hull: the nearest `[VNT]`, or 0.5 m above the rack):
  - The bang: `FlightSceneScript.CreateExplosion(BlastType, at, size, ...)`, size = lb x 0.8/250 x
    `BlastVisualPercent` (75%) ("Boom 25" = 250 lb = 0.8). Network-synced by the game. ONLY FOR SHOW:
    `AmmoRackBlastPatch` gives `HandleExplosiveBlast` an empty part list for blasts in `Recent` (3 m, 3 s).
  - The jet: the vent's game fire, scaled (`JetSize` 1/3 of the first version), stretched x2.5 up, more flames,
    easing out over clamp(2 + lb/15, 3, 10) s into the half-size endless fire. Other vents catch the half-size
    fire 0.5 to 2 s later.
- `WreckInsides(rack, center, ventAt, lb)`: the cook-off's only damage.
  - W = lb x 0.4536 x `CookOffTNTEfficiency` (25%) kg TNT. Z = R / W^(1/3). Mills (1987):
    P = 1772/Z³ − 114/Z² + 108/Z kPa. Damage = MaxHealth x clamp(ln(P/`CookOffDamageKPa`) /
    ln(`CookOffWreckKPa`/`CookOffDamageKPa`), 0, 2) (200 and 1000 kPa). 96 lb: wrecks ≤ 2.7 m, damages ≤ 4.8 m.
  - Own vehicle from the rack center, every other vehicle from the vent (`Physics.OverlapSphere`, nearest
    collider point; projectile colliders skipped). Who takes damage: `VehicleRules.TakesModDamage`.
  - Armor shielding: `ArmorBetween` sums [ARM] thickness / max(cos, 0.2) along the line (each part once);
    P x 0.5^(mm / `CookOffArmorHalvingMm` (10)). Outside targets ignore the burning vehicle's own armor.
  - Remote vehicles: `NetworkAircraft.DamagePart`. Racks hit this way roll their own chance (chains).

## Vehicle death (`VehicleRules`)
- `GameMayExplode(aircraft)` = not knocked out AND `GameDamageExplosions` (default false). Used by
  `KnockedOutVehicleExplosionPatch` (`AircraftScript.QueueExplosion`) and both `ExplodePart` overloads (racks:
  never). IL scan of Game.dll: the only callers of `QueueExplosion`/`ExplodePart` are `PartScript.OnDamaged`
  (damage ≥ `AircraftData.DamageLimit` → `QueueExplosion(MainCockpit, 100)`), `OnPartDamageLevelIncreased`
  (roll on `DamageDisconnect` parts at each damage level and on any destroyed part; `ExplodePart` on a
  cockpit-group part → `QueueExplosion`), `FuelTankScript` delayed explosion, `PropellerAssemblyScript`, and
  `ExplosionScript` (the vehicle's own chain). Weapons use other scripts. Destroyed parts only disconnect
  inside `ExplodePart`, so with it off they stay attached (wings don't fall off); the game never counts the
  vehicle as destroyed.
- `TakesModDamage(part, tags)`: armor never; `[MOD]`/`[AMM]` always; anything else only if the vehicle has no
  `[ARM]` part (`IsArmored`, cached) or `ArmoredStructureDamage` is on. Used by `FragmentSpawner.Deposit` and
  the blast.

## Multiplayer
- All rack settings and the two `[Fragments]` ones are host globals (`NetSync.RegisterGlobal`).
- `RackFireSync` (type 202 on the `NetSync` part-message channel, sent on the burning vehicle's own
  `NetworkAircraft`): `R` rack fire, `X` rack fire out, `J,dur,x,y,z,boost` jet, `V` vent fire, `B,x,y,z` blast
  spot (part-local, sent before `CreateExplosion` so receivers add it to `Recent`). Receivers match the plain
  fire the game synced (`PartDamageEffects.CreateEffect` sends `CreateDamageEffect`; the observers RPC has
  `ExcludeOwner=True`) within 2 s in either order (`RackFireSeenPatch` postfix), or light their own after
  1.5 s, and dress it with `DressBurnOut`/`DressJet`. `PartMessagePatch` passes the part id for 202.
- Limit: a remote shooter's damage reaches the rack owner as one `DamagePart` per part (biggest piece's line),
  so no per-piece check or direct-hit term. Planned separately: share each rack's rounds-left count (removal
  order is deterministic, so the count fixes which rounds remain) and let the shooter run the per-piece check,
  sending "through rounds" and "missed" as two hits. Alternative: a message with the pieces' paths.
- Not yet tested with two players.

## Designer and X-ray
- `DesignerSection`: rack rows (Status, Rounds held, Layout, Lost to shape, Space, Round, Shell, Rounds weight,
  variables, See-through toggle); cannon rows (Ammo group, Ammo from, Rounds, Round, Shell); on every cannon
  Shell energy, Shell mass, Muzzle velocity and Penetration at 0/30/60 deg = energy / (`ArmorResistance` x
  caliber area) x cos (HE: "none (HE bursts)"). Live rows refresh every 0.5 s on a signature change
  (`LiveBlock`). `Tick` runs `RefreshLiveRows` then your `PollProjectileType`. The section hides on parts with
  nothing to show; the vent toggle is on fuselages only (a part already marked keeps it).
- `RackVisualizer`: ghost copies of rack meshes (renderers hidden, still clickable), rounds as cylinders (brass
  case, steel shell), up to 20,000 drawn; hidden for the screenshot dialog (`HideScriptsManager` patches).
- `AmmoRackXRay`: hooked from `XRayView` show/clear; rounds vanish from the top, burning = orange, exploded =
  empty. Own vehicle only (the contents exist only in the owner's game).

## Changes to existing files
- `FragmentSpawner.cs`: `Accumulate` → `AmmoRacks.NotePiece`; `DepositCrossings(..., shellCaliberMm = 0)` sets
  `depositingShell`/`shellEnergyIn`/`shellRadius`; `Deposit` uses `VehicleRules.TakesModDamage`;
  `ContinueAfterPlate` restarts the shell on the plate's FRONT surface (plate ignored), fuse counted from there,
  start time `timeAtPlate` (it used to start 1 cm past the back face, inside any touching part, which a ray
  starting inside a collider never sees; `exitPoint` removed).
- `ShellFlight.cs`: passes `shell.CaliberMm` to `DepositCrossings`.
- `Net/NetSync.cs`: `PartMessagePatch` accepts 202 and reads the part id.
- `PartTags.cs`: `AmmoRackTag`, `VentTag`, `CannonTag.Group`; regex `(ARM|CAN|MOD|AMM|VNT)`.
- `ArmorFragmentsPlugin.cs`: `[AmmoRacks]` settings, `GameDamageExplosions`, `ArmoredStructureDamage`, globals,
  ticks (`AmmoRacks.Tick`, `RackFireSync.Tick`, `RackVisualizer.Tick`, `AmmoRackXRay.Tick`).
- `XRayView.cs`: `GhostSources` skips "AmmoRackRounds"/"AmmoRackGhost"; `AmmoRackXRay.OnShown`/`Clear`.
- `ModuleOutputs.cs`: `Sanitize`, `CreateVariable` internal.
- `ArmorFragments.csproj`: `ManagedDir` auto-detect (`SimplePlanes 2_Data` / `SimplePlanes 2 (1.1)_Data`),
  `UnityEngine.ParticleSystemModule` reference.

## Settings
`[AmmoRacks]` (host): `Enabled` true, `CaseDiameterRatio` 1.3, `CaseLitresPerMJ` 2, `RoundSpacingPercent` 10,
`CookOffEnergyKJ` 100, `CookOffDirectHit` 50, `CookOffBurnSeconds` 10, `CookOffTNTEfficiency` 25,
`CookOffWreckKPa` 1000, `CookOffDamageKPa` 200, `CookOffArmorHalvingMm` 10, `BlastStrength` 1, `MaxBlastLb` 120,
`BlastVisualPercent` 75, `BlastType` BombExplosion. Local: `ShowInDesigner` true, `DebugDumpShape` false.
`[Fragments]` (host): `GameDamageExplosions` false, `ArmoredStructureDamage` false.
Retired keys (orphaned in old .cfg files, harmless): `CookOffSensitivity`, `CookOffDelayMin`, `CookOffDelayMax`.

## Game facts used (Game.dll)
- `CannonData._ammoCount` → `CannonScript._currentAmmo`; `Fire()` decrements; `CannonWeaponSystem.CanFire`
  sums ammo.
- `PartScript.OnDamaged` returns at once on a remote aircraft; adds `PartDamage`; `AircraftScript.OnDamaged`
  adds to `Damage`; DamageLimit check; modifiers' `OnDamaged`; level thresholds → `OnDamageLevelIncreased`.
- `MaxHealth` = `PartData.Health` (XML `health`, default `PartType.Health`), set once in `Initialize`.
- `PartDamageEffects.CreateEffect(type, part, pos, dir)` instantiates the prefab parented to the part and, on
  the owner, sends `CreateDamageEffect`. `PartDamageEffect` ends by `_duration` and fuel (`_requiresFuel`).
- `BodyScript.HandleExplosiveBlast(parts, force, radius, criticalRadius, origin, owner)` knocks parts off by a
  random force threshold (armor included, outside `OnDamaged`, so `ArmorDamagePatch` can't stop it), pushes
  the bodies, and damages through `OnDamaged`.
- `PartData.LoadedMass = (PartType.Mass + modifier mass) x MassScale`; `RecalculateLoadedMass` is lazy.
- `AircraftScript.GetPartById(int, bool includeDisconnected)`; `NetworkAircraftScript.SendPartNetworkMessage
  (byte, PartData, Action<PooledWriter>, Channel)`; message layout: type (1), part id (2), payload.

## Verified in game (single player, by Jakniphe)
Ammo limit, packing on shaped fuselages, fly-through over emptied layers, per-piece check (HE/APHE spall), 20 mm
direct hits (cook-off on the 3rd shot at 71%), shell through a 64 mm plate into the rack behind it (now seen,
slows realistically, bursts inside), cook-off burn/bang/jet/vents, armor shielding a module behind 20 mm,
hulls no longer blown apart, X-ray, designer read-outs. NOT yet seen: the blast hitting another vehicle, and
anything multiplayer.

## Open items / careful
- Explosive filler (HE/APHE) does not add to the cook-off (Jakniphe's choice: propellant only).
- If shell shapes are reworked, `AmmoRound` must follow; racks, the hit check and the drawing read from it.
- A ray that starts inside a collider never sees that collider: start rays on a surface you know is outside
  anything that matters (see `ContinueAfterPlate`).
- Access game members whose types may change through reflection (see v0.6.3's `RendererMaterialMap` lesson).
- The commit history keeps superseded steps (a linear blast radius, a health-based chance, a random 5–20 s
  burn): this file describes what the code does now.
