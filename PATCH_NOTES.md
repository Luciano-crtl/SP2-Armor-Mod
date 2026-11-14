# Ammo racks (by Jakniphe), on top of v0.7.0

Not tested in multiplayer yet. The version number is the author's call. Full design notes for Claude: `AMMO_RACKS_CONTEXT.md`.

Ammo racks (tag: `[AMM g= o=]`, toggle "Ammo rack" on fuselages)
- A rack holds whole rounds sized from the cannon it feeds (case 1.3 x caliber wide, longer for faster guns), packed into the fuselage's real shape (measured from its mesh); tapers and corners lose the rounds that would poke out.
- Racks feed the cannons of their group (`[CAN g=]`, default group if empty) and replace the game's Ammo Count, which is hidden on rack-fed cannons. They empty in `o=` order, top layer first.
- A rack weighs its rounds and gets lighter as you fire. A broken-off rack keeps its rounds but stops feeding.

Cook-offs
- Only hits through a round still in the rack count; the hitbox shrinks as it empties. Every piece of a shot is checked on its own: the shell, plate spall, and HE/APHE burst fragments.
- Chance: 1 - (1 - 50%)^(shells through the rounds) x e^(-kJ into the rounds / 100 kJ). The shell is checked at its full caliber and counts with the energy it carries in, so a 20 mm APHE is about 64%, a 37 mm about 95%, and 75 mm or more certain; fragments count by the energy they leave. A hit that destroys the rack through its rounds always sets it off.
- The rack burns for 10 s (half-size fire, can't feed the gun), then explodes.

Cook-off blast
- Real blast physics: the propellant left, as TNT, at 25% efficiency (capped at 120 lb). Overpressure by Mills' formula over the scaled distance R / W^(1/3), wrecking parts at 1000 kPa, nothing below 200 kPa. A full 100 mm rack (96 lb) wrecks within 2.7 m and damages out to 4.8 m.
- Inside the vehicle it hits only `[MOD]` and `[AMM]` parts (racks roll their own chance). Outside, from the vent, it hits other vehicles, damaging remote players' over the network.
- Armor in the way shields: every 10 mm of effective armor halves the pressure.
- The bang (75% of a game bomb of the same weight) is only for show. The fire comes out of the nearest `[VNT]` part as a jet, and every vent keeps burning.

Vehicles keep their shape
- The game no longer blows parts or vehicles up because of damage (damage-stage roll, damage limit, fuel tanks, propellers, explosion chains), on any vehicle; destroyed parts stay attached. Setting: `GameDamageExplosions` (off).
- On a vehicle with armor, only `[MOD]` and `[AMM]` parts take shell and fragment damage; the structure just slows them. Setting: `ArmoredStructureDamage` (off).

Designer
- See-through racks with their rounds drawn (clickable; normal in screenshots), rack and cannon read-outs.
- Every cannon shows shell energy, mass, muzzle velocity and penetration at 0 / 30 / 60 deg; HE shows "none (HE bursts)".
- "Cook-off vent" toggle on fuselages (`[VNT]`). Armor is exclusive with module, rack and vent.

X-ray
- Your own racks show their live rounds (F8 and the simulated shot).

Multiplayer
- Other players see the cook-off fires and jet as the owner does (part message type 202). Host settings apply.

Funky Trees
- `<group>_rounds`, `<group>_fire`, `<group>_boom` (wire a detacher to `_boom` to throw the turret).

Settings (`[AmmoRacks]`, host-synced)
- `CookOffDirectHit` 50, `CookOffEnergyKJ` 100, `CookOffBurnSeconds` 10, `CookOffTNTEfficiency` 25, `CookOffWreckKPa` 1000, `CookOffDamageKPa` 200, `CookOffArmorHalvingMm` 10, `BlastVisualPercent` 75, `MaxBlastLb` 120, `BlastStrength`, `BlastType`, `CaseDiameterRatio`, `CaseLitresPerMJ`, `RoundSpacingPercent`, `Enabled`; local: `ShowInDesigner`, `DebugDumpShape`.

Fixes
- Fix: a shell coming out of a plate restarted 1 cm past its back face, inside any part touching it, which its ray then never saw (a rack right behind the armor was skipped and the shell lost almost no energy). It now restarts on the plate's front surface.
- Fix: spall in a hull's inner fuselages blew whole vehicles up (the cockpit-group part explosion). See "Vehicles keep their shape".
- The project file finds `SimplePlanes 2_Data` or `SimplePlanes 2 (1.1)_Data` (override with `-p:ManagedDir=`).

Known limits
- Another player's shot reaches a rack as one summed hit, judged by its biggest piece and by energy only (piece-by-piece over the network is planned).
- HE/APHE filler doesn't add to the cook-off. The center of mass doesn't follow the rounds' weight.
