# Ammo racks and cannon changes: context for Claude

Reference for the ammo-rack feature and what it changes in cannons, damage and vehicle death. Branch
`ammo-rack`, built on v0.7.0. Player summary: `PATCH_NOTES.md`.

## Goal
Armored vehicles are killed through damaged modules, not part explosions: the armor shell stays intact and the
vehicle is killed from the inside. Ammo racks are the first such module, and a rack exploding is always a kill.
Vehicles without armor die as in the base game.

## Files
New:
- `AmmoRacks.cs`: runtime. Registries (`Craft` → `Group` → `Rack`), feeding, weight, damage and cook-off, the
  explosion and its blast, the knocked-out state, and the rack Harmony patches (end of file).
- `AmmoRound.cs`: a round's size, mass, energy and explosive pounds, from the cannon's `CannonData`.
- `RackGrid.cs`: measures a rack's shape, packs the rounds, line-vs-rounds test, removal order, rounds mesh.
- `RackVisualizer.cs`: see-through racks with their rounds in the designer.
- `AmmoRackXRay.cs`: live rounds of your own racks in the X-ray.
- `VehicleRules.cs`: whether the game may explode a vehicle, which parts take the mod's damage, and the kill.
- `Net/RackFireSync.cs`: gives other players' copies of the cook-off fires the owner's look (message 202).

Changed: `FragmentSpawner.cs`, `ShellFlight.cs`, `Net/NetSync.cs`, `PartTags.cs`, `ArmorFragmentsPlugin.cs`,
`UI/DesignerSection.cs`, `XRayView.cs`, `ModuleOutputs.cs`, `ArmorFragments.csproj` (see the end).

## Tags
- `[AMM g=<group> o=<order>]` on a fuselage: an ammo rack. `g` = group name (empty = default group "Ammo"),
  `o` = empty order (lower first; none = after the numbered ones).
- `[CAN ... g=<group>]`: the cannon draws from that group's racks.
- `[VNT]` on a fuselage: a cook-off vent.
- In the designer, armor is exclusive with `[MOD]`, `[AMM]` and `[VNT]` (armor takes no damage). Hollow/cut
  fuselages and armor hold no rounds.

## Rounds (`AmmoRound`)
- Shell: mass = `ProjectileVolume x 11342` kg; length 1.5 calibers (Slug) or 1 (Sphere); energy = ½ m v².
- Case: diameter = `CaseDiameterRatio` (1.3) x caliber; volume = `CaseLitresPerMJ` (2) L per MJ of muzzle
  energy; length = max(caliber, volume / area).
- Round mass = shell + 1 kg per litre of case.
- Explosive pounds per round = energy (kJ) x 0.53 x 1.1 / 718 (5 cm KwK 39 ≈ 0.58 lb).
- A group's racks are sized for the biggest round among its cannons.

## Shape and packing (`RackGrid`)
- Frame: Z along the fuselage (`JFuselageData` offset), X/Y across; rounds lie along Z.
- Measured from the render mesh (`XRayView.GhostSources`; designer colliders are bounding boxes): sliced along Z
  (~1 cm, at most 1500 cuts), 5 scanlines per round circle, inside test by nonzero winding from both sides.
  Collider fallback.
- Across: a grid with pitch = diameter x (1 + `RoundSpacingPercent`/100). Each (column, layer) row holds as many
  whole rounds end to end as fit its own length.
- Removal order: top layer first, then left to right, then front to back.
- `HitsRounds(transform, point, direction, thickness)`: the line through the point (both ways) is clipped to the
  rounds' box and sampled; `RoundAt` finds a present round within radius x 1.0488 + thickness, searching
  neighboring columns and layers. Shells pass half their caliber (a tube); fragments pass 0.
- `DebugDumpShape` writes the measured shape and rounds to `BepInEx/ammo-rack-shape.obj`.

## Feeding and weight
- `AmmoRackVariablesPatch` (postfix of `VariableSystemScript.RefreshVariables`) registers the racks and creates
  the Funky Trees variables `<group>_rounds`, `<group>_fire`, `<group>_boom`.
- `SyncCannons` writes the group's rounds into `CannonScript._currentAmmo`. `AmmoRackFirePatch`
  (`CannonScript.Fire`): no shot when the group has no round it can feed (`Group.Available`: burning or
  broken-off racks don't count); each shot takes a round from the first rack in order that can feed.
- `AmmoCountVisibilityPatch` (`PartModifierData.GetGenericDesignerPropertyVisibilityCallback`) hides Ammo Count
  on rack-fed cannons.
- Weight: the rack's `PartData.MassScale` is raised by its rounds' mass (`RecalculateLoadedMass`); the body
  applies mass changes over 100 kg. The center of mass doesn't follow.
- A rack no longer `ConnectedToMainCockpit` keeps its rounds but doesn't feed.

## Damage and cook-off chance
- `AmmoRackDamagePatch` (`PartScript.OnDamaged`, racks only): the prefix caps damage at 99.9% of `MaxHealth`
  (the game never destroys a rack) and records the level before and the hit's share; the postfix calls
  `AmmoRacks.OnDamaged`.
- Per piece: `FragmentSpawner.Accumulate` calls `AmmoRacks.NotePiece(part, damage, point, dir, shell,
  shellEnergyJ, shellRadius)` before summing a shot per part. `DepositCrossings` (the shell's own path) sets
  `depositingShell`, `shellEnergyIn` (`Crossed.EnergyIn`) and `shellRadius` (caliber passed by `ShellFlight`).
  Pieces whose path crosses a present round add their damage, and a shell its carried energy, to that frame's
  tally, which `OnDamaged` takes (`TakeNoted`).
- Chance = 1 − (1 − `CookOffDirectHit`)^(shells through the rounds) x e^(−kJ / `CookOffEnergyKJ`), with
  kJ = damage of the pieces through the rounds / `DamagePerKiloJoule` + the shells' carried energy. Defaults
  50% and 100 kJ. Independent of the rack's health.
- The hit that brings the rack to "destroyed" (≥ 0.998) through its rounds always cooks it off; a hit that
  wrecks it without crossing rounds doesn't.
- Single-line hits (the game's own damage, a remote player's summed hit): that line decides, kJ = damage / DPK,
  no direct-hit term.
- Racks are judged only in their owner's game.

## Burn and explosion
- `Ignite`: knocks the vehicle out, lights the game's fire at half size (`DressBurnOut`; `KeepBurning` sets
  `_duration` to MaxValue and `_requiresFuel` false), and explodes after `CookOffBurnSeconds` (10). A burning rack
  doesn't feed.
- `Explode`: pounds = rounds left x `ExplosiveLb` x `BlastStrength`, capped at `MaxBlastLb` (120); sets `_boom`;
  after the blast, `VehicleRules.Kill` kills the vehicle (unless the rack broke off first). A rack that burns out
  with no rounds left doesn't explode and doesn't kill.
- `Blast` (returns where the blast leaves the hull: the nearest `[VNT]`, or 0.5 m above the rack):
  - Bang: `CreateExplosion(BlastType, at, size, ...)`, size = lb x 0.8/250 x `BlastVisualPercent` (75%). Visual
    only: `AmmoRackBlastPatch` gives `HandleExplosiveBlast` an empty part list for blasts in `Recent` (3 m, 3 s).
  - Jet: the vent's fire scaled up (`JetSize` 1/3), stretched x2.5 upward with more flames, easing over
    clamp(2 + lb/15, 3, 10) s into the half-size endless fire. Other vents light the half-size fire 0.5 to 2 s
    later.
- `WreckInsides(rack, center, ventAt, lb)`: all the cook-off's damage.
  - W = lb x 0.4536 x `CookOffTNTEfficiency` (25%) kg of TNT; Z = R / W^(1/3); Mills (1987)
    P = 1772/Z³ − 114/Z² + 108/Z kPa; damage = MaxHealth x clamp(ln(P / `CookOffDamageKPa`) /
    ln(`CookOffWreckKPa` / `CookOffDamageKPa`), 0, 2) (200 and 1000 kPa).
  - Own vehicle from the rack center, other vehicles from the vent (`Physics.OverlapSphere`, nearest collider
    point, projectile colliders skipped). Who takes damage: `VehicleRules.TakesModDamage`.
  - Shielding: `ArmorBetween` sums [ARM] thickness / max(cos, 0.2) on the line (each part once);
    P x 0.5^(mm / `CookOffArmorHalvingMm` (10)). Outside targets ignore the burning vehicle's own armor.
  - Remote vehicles are damaged through `NetworkAircraft.DamagePart`. Racks hit roll their own chance.

## Vehicle death (`VehicleRules`)
- `GameMayExplode(aircraft)` = not knocked out and (no `[ARM]` part or `GameDamageExplosions`, default false):
  unarmored vehicles keep the base game's damage explosions. Used by the `QueueExplosion` patch and both
  `ExplodePart` patches (never `ExplodePart` on a rack). The game reaches these only from damage:
  `PartScript.OnDamaged` (damage ≥ `DamageLimit` → `QueueExplosion(MainCockpit, 100)`),
  `OnPartDamageLevelIncreased` (roll on `DamageDisconnect` parts and destroyed parts; a cockpit-group part →
  `QueueExplosion`), `FuelTankScript`, `PropellerAssemblyScript`, `ExplosionScript`. Parts only disconnect
  inside `ExplodePart`, so on an armored vehicle destroyed parts stay attached.
- The game's death: `AircraftStructureChanged` → `CheckForCriticalDamage` (parts left / initial non-weapon parts,
  or wing area left, below `CriticalDamageThreshold`) → `MarkAsCriticallyDamaged` (sets `CriticallyDamaged`, raises
  `AircraftKilled` in `OnLateUpdate`: kill credit, kill cam, respawn; `NetworkAircraftScript.OnAircraftKilled`
  tells the other players). An armored vehicle loses no parts, so `VehicleRules.Kill` calls
  `MarkAsCriticallyDamaged` (reflection) itself, in the owner's game, when a rack explodes. Planned: kills
  decided by modules in general; a rack exploding stays a kill.
- `TakesModDamage(part, tags)`: armor never; `[MOD]`/`[AMM]` always; other parts only when the vehicle has no
  `[ARM]` part (`IsArmored`, cached) or `ArmoredStructureDamage` is on. Used by `FragmentSpawner.Deposit` and the
  blast.

## Multiplayer
- All `[AmmoRacks]` settings and the two `[Fragments]` ones are host globals.
- `RackFireSync`, type 202 on the `NetSync` part-message channel, sent on the burning vehicle:
  `R` rack fire, `X` rack fire out, `J,dur,x,y,z,boost` jet, `V` vent fire, `B,x,y,z` blast spot (part-local,
  sent before `CreateExplosion` so receivers add it to `Recent`). Receivers pair it with the plain fire the game
  syncs (`CreateEffect` → `CreateDamageEffect`, observers RPC with `ExcludeOwner`) within 2 s in either order
  (`RackFireSeenPatch`), or light their own after 1.5 s, then apply `DressBurnOut`/`DressJet`.
- A remote shooter's damage reaches the rack's owner as one `DamagePart` per part, so it gets no per-piece
  check or direct-hit term. Planned: share each rack's rounds-left count (the removal order fixes which rounds
  remain) and let the shooter run the per-piece check, sending "through rounds" and "missed" as two hits.

## Designer and X-ray
- `DesignerSection`: rack rows (Status, Rounds held, Layout, Lost to shape, Space, Round, Shell, Rounds weight,
  variables, See-through toggle); cannon rows (Ammo group, Ammo from, Rounds, Round, Shell); on every cannon
  Shell energy, Shell mass, Muzzle velocity, Penetration at 0/30/60 deg = energy / (`ArmorResistance` x caliber
  area) x cos (HE: "none (HE bursts)"). Live rows refresh every 0.5 s on a signature change (`LiveBlock`).
  The vent toggle is on fuselages.
- `RackVisualizer`: ghost copies of rack meshes (still clickable), rounds as cylinders, up to 20,000 drawn,
  hidden for the screenshot dialog.
- `AmmoRackXRay`: rounds of your own racks; burning = orange, exploded = empty.

## Changes to existing files
- `FragmentSpawner.cs`: `Accumulate` → `AmmoRacks.NotePiece`; `DepositCrossings(..., shellCaliberMm = 0)` sets
  the shell flags; `Deposit` uses `VehicleRules.TakesModDamage`; `ContinueAfterPlate` restarts the shell's ray on
  the plate's front surface with the plate ignored, the fuse counted from there and the start time at the plate
  (a ray that starts inside a collider doesn't see it, so starting behind the plate would miss a part touching
  its back face).
- `ShellFlight.cs`: passes `shell.CaliberMm` to `DepositCrossings`.
- `Net/NetSync.cs`: `PartMessagePatch` accepts 202 and reads the part id.
- `PartTags.cs`: `AmmoRackTag`, `VentTag`, `CannonTag.Group`; regex `(ARM|CAN|MOD|AMM|VNT)`.
- `ArmorFragmentsPlugin.cs`: settings, globals, ticks (`AmmoRacks`, `RackFireSync`, `RackVisualizer`,
  `AmmoRackXRay`).
- `UI/DesignerSection.cs`: the rows and toggles above; `Tick` runs `RefreshLiveRows` and `PollProjectileType`.
- `XRayView.cs`: `GhostSources` skips our drawn objects; calls `AmmoRackXRay.OnShown`/`Clear`.
- `ModuleOutputs.cs`: `Sanitize`, `CreateVariable` internal.
- `ArmorFragments.csproj`: `ManagedDir` auto-detect (`SimplePlanes 2_Data` / `SimplePlanes 2 (1.1)_Data`),
  `UnityEngine.ParticleSystemModule` reference.

## Settings
`[AmmoRacks]` (host): `Enabled` true, `CaseDiameterRatio` 1.3, `CaseLitresPerMJ` 2, `RoundSpacingPercent` 10,
`CookOffEnergyKJ` 100, `CookOffDirectHit` 50, `CookOffBurnSeconds` 10, `CookOffTNTEfficiency` 25,
`CookOffWreckKPa` 1000, `CookOffDamageKPa` 200, `CookOffArmorHalvingMm` 10, `BlastStrength` 1, `MaxBlastLb` 120,
`BlastVisualPercent` 75, `BlastType` BombExplosion. Local: `ShowInDesigner` true, `DebugDumpShape` false.
`[Fragments]` (host): `GameDamageExplosions` false, `ArmoredStructureDamage` false.

## Game facts used (Game.dll)
- `CannonData._ammoCount` → `CannonScript._currentAmmo`; `Fire()` decrements.
- `PartScript.OnDamaged` returns at once on a remote aircraft; adds `PartDamage` and the aircraft's `Damage`;
  checks `DamageLimit`; level thresholds → `OnDamageLevelIncreased`.
- `MaxHealth` = `PartData.Health` (XML `health`, default `PartType.Health`), set once in `Initialize`.
- `PartDamageEffects.CreateEffect(type, part, pos, dir)` parents the effect to the part and, on the owner, sends
  `CreateDamageEffect`. `PartDamageEffect` ends by `_duration` and fuel (`_requiresFuel`).
- `BodyScript.HandleExplosiveBlast` knocks parts off by a random force threshold (outside `OnDamaged`), pushes,
  and damages through `OnDamaged`.
- `PartData.LoadedMass = (PartType.Mass + modifier mass) x MassScale`.
- `AircraftScript.GetPartById(int, bool includeDisconnected)`; `SendPartNetworkMessage(byte, PartData,
  Action<PooledWriter>, Channel)`; part messages: type (1 byte), part id (2), payload.

## Open items
- Kills decided by modules in general (planned); a rack exploding stays a kill.
- HE/APHE filler doesn't add to the cook-off (propellant only, by choice).
- Per-piece hits for other players' shots (see Multiplayer).
- If shell shapes change, `AmmoRound` must follow: racks, the hit check and the drawing read from it.
