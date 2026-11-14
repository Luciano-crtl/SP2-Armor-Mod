# Patch notes

## Unreleased: Ammo racks (by Jakniphe)

Not tested in multiplayer yet. Builds on 0.7.0; the version number is the author's call.

### New: ammo racks
- **Any fuselage can be an ammo rack.** Turn on **Ammo rack** in the part's Armor Fragments section (the name gets an `[AMM]` tag).
- **Racks feed cannons.** A cannon with the **Cannon** toggle on draws its rounds from the racks in its group. With no `g=`, every tagged cannon and rack share one default group. For separate guns, add a group name in the part name field: `[CAN k=100 g=Main]` on the gun, `[AMM g=Main]` on its racks.
- **Racks replace the Ammo Count slider.** A cannon fed by racks fires until its racks are empty. The game's Ammo Count slider is hidden for it as soon as a rack is linked. Cannons without racks work exactly as before.
- **Rounds are solid.** A rack holds only the whole rounds that fit its shape, lying along the fuselage's length:
  - Rounds are cylinders sized from the cannon. The case is 1.3 x the caliber wide. Its length grows with muzzle energy (about 2 litres per MJ), so faster guns have longer rounds, calibrated on the Pz III's 50 mm guns. The shell in front is the game's own (Slug 1.5 calibers long, Sphere 1).
  - The rack is measured from the fuselage's actual shape, so wedges, tapers and rounded corners lose the rounds that would poke out.
  - A rack shorter than one round holds nothing.
  - Hollow and cut fuselages can't be racks, and neither can armor (`[ARM]`).
- **Racks empty in order.** Set it with the **Empty order** slider (`o=`); racks without a number go last. Each rack empties from its top layer down.
- **Racks are heavy.** In flight, a rack weighs its part plus its rounds (shell plus about 1 kg of case and propellant per litre, about 3.5 kg for a 50 mm KwK 39 round), and gets lighter as you fire. The game applies mass changes in steps of about 100 kg. The designer shows the **Rounds weight**.
- **Broken-off racks:** a rack knocked off the vehicle keeps its rounds, which can still cook off, but no longer feeds the gun.
- **Cook-offs.**
  - **What counts:** a hit only matters if it passes through the part of a rack that still holds rounds. A half-empty rack is safe at the top.
  - **Every piece counts:** a shot can reach a rack as the shell itself, as spall from a plate, or as the fragments of an HE or APHE burst, including HE spall through thin armor. Each piece's path is checked against the rounds, and only the damage of the pieces that went through rounds counts toward a cook-off.
  - **Chance:** 1 - e^(-3 x the rise in the rack's damage level), using the same 0 to 1 damage scale as damage modules. Losing a third of its health gives 63%.
  - **Destroyed racks:** the hit that destroys a rack always cooks it off, if it goes through its rounds. A hit that wrecks the rack but misses the rounds (say, through the empty top of a half-empty rack) leaves them lying there, and later hits through them roll the normal chance.
  - **The game never destroys or explodes a rack.** It stops at 99.9% health, which counts as destroyed, and the game's random "part explodes" roll is off for it. Every fuselage rolls that at each new damage stage, and for a rack welded to the cockpit's structure it blew the whole vehicle apart at once, skipping the burn. From the moment a rack catches fire, the vehicle is knocked out (below).
  - **Fire, then explosion:** a cooking rack burns for 5 to 20 s, with the game's fire effect at half size, and can't feed the gun meanwhile. Then it explodes with the propellant of the rounds still inside.
  - **A believable blast:** the explosion has a bomb's look but not a bomb's power.
    - Each round counts as its propellant's explosive equivalent: about 0.58 lb for a 50 mm round at 835 m/s, so a Pz III's 90 rounds make about 55 lb.
    - The total is capped at 120 lb and sized like the game's own bombs: "Boom 25" (250 lb) is 0.8, so 55 lb is 0.18.
  - **One bang, then a jet of fire:** the cook-off vents through the nearest part marked **Cook-off vent** (`[VNT]`: a fuselage at the turret ring, or a hatch; not armor), or just above the rack with no vent. There is a single bomb-style bang sized by its pounds, then the fire flares up and rises in a jet for 3 to 10 s depending on the pounds. At the 120 lb cap, the flare-up is about 3x wider and 8x taller than the fire it settles into.
  - **The tank holds its shape:**
    - the cook-off only cooks your damage modules (`[MOD]`) and other ammo racks near the rack (destroyed close in, damaged farther out; radius 1 m plus 1 m per 25 lb, so 3 m for 50 lb);
    - armor and structure aren't touched: the blast doesn't damage, push or knock off any part of your vehicle, and doesn't knock off armor plates on any vehicle;
    - other racks it reaches roll their own chance.
  - **The wreck keeps burning:** the jet then shrinks smoothly into a half-size fire that never goes out, even when the vehicle runs out of fuel; only water puts it out. Every other cook-off vent on the vehicle catches the same small fire a moment later, as the fire spreads through the hull. Only the nearest vent gets the bang and the jet.
  - **Other players see the same cook-off:** the game already sends them its fires and the bang. Their mod gives those fires the same look (the half-size rack fire, the jet flaring up and settling, vent fires that never go out) and keeps the bang off the burning vehicle and armor on their screen too. Players without the mod see the game's plain fires.
  - **Knocked out, not blown apart:** once a rack catches fire, the vehicle is knocked out. The game's own vehicle and part explosions are off for it, so the cooked modules, or a fuel tank going up later, can't tear the shell apart. The game no longer counts it as destroyed: use Funky Trees on `<group>_boom` or the modules' damage to cut its controls.
- **Funky Trees:**
  - `<group>_rounds`: rounds the group can still fire.
  - `<group>_fire`: 1 while one of its racks is burning.
  - `<group>_boom`: 1 from the moment one of its racks explodes. Wire a detacher to it to throw the turret.

  The default group's variables are `Ammo_rounds`, `Ammo_fire` and `Ammo_boom`.

### Vehicles keep their shape
- **No more damage explosions, on any vehicle:** spall reaching a hull's inner fuselages used to set off the game's "part explodes" roll, and a part welded to the cockpit blew the whole vehicle up and stripped its parts. The game no longer blows parts or vehicles up because of damage. That covers the damage-stage roll, the damage limit, damaged fuel tanks and propellers, and explosion chains.
  - Vehicles are knocked out through their modules and ammo racks instead.
  - Destroyed parts stay attached, so wings no longer fall off planes.
  - The game doesn't count such vehicles as destroyed.
  - Weapons' own explosions (bombs, missiles, cook-offs) are unchanged.
  - Setting: `GameDamageExplosions` (default off).
- **Armored vehicles take damage only in their modules and racks:** on a vehicle with any `[ARM]` part, the mod's shells and fragments damage only damage modules (`[MOD]`) and ammo racks (`[AMM]`). The rest of the structure only slows them down. Vehicles without armor, like planes, still take it on every part. Setting: `ArmoredStructureDamage` (default off).

### X-ray view
- **Your own racks show their rounds in the X-ray view:** your vehicle with F8, or the parts around a simulated shot. It follows the rack live: rounds vanish from the top as you fire, a burning rack's rounds glow orange, and an exploded rack shows empty. Other players' racks aren't shown, because their contents only exist in their game.

### Designer
- **See-through racks:** racks are drawn see-through, with their rounds inside as cylinders (brass case, steel shell toward the front).
  - You can still click a rack to select it, and the selected rack turns orange.
  - Switch this off with **See-through racks**.
  - The screenshot dialog shows racks normally.
  - Up to 20,000 rounds per rack are drawn; past that, all of them still count, and the log says so.
- **Rack read-out:** Status, Rounds held, Layout, Lost to shape, Space (L x W x H), Round size, Shell, Rounds weight and its Funky Trees variables. It updates as you reshape the fuselage or change the cannon.
- **Cannon read-out:** Ammo group, Ammo from, Rounds, Round size and Shell.
- **Estimated penetration, on every cannon:** shell energy, mass and muzzle velocity, plus the thickest plate it gets through at 0, 30 and 60 degrees, one row each. It's worked out from the mod's armor rule at the muzzle, with the current armor resistance; ricochets and plate hardness aren't counted. Example: a 100 mm Slug at 1000 m/s gets through 142 / 123 / 71 mm. APHE gets through like AP; HE shows "none: bursts on contact", since only its fragments go on.
- **Cook-off vent:** a toggle on fuselages (a part of another kind already marked as a vent keeps its toggle, to switch it off).
- **Armor is exclusive:** armor takes no damage, so a part can't be armor and also a damage module, ammo rack or cook-off vent. Turning Armor on switches those off, and turning one of them on switches Armor off. Put a vent on a non-armor part, such as a hatch or a ring under the armor.

### Settings (config file only, section `[AmmoRacks]`; the host's values apply in multiplayer)
- `Enabled`
- `CaseDiameterRatio`
- `CaseLitresPerMJ`
- `RoundSpacingPercent`
- `CookOffSensitivity`
- `CookOffDelayMin` / `CookOffDelayMax`
- `BlastStrength`: multiplies each round's pounds
- `MaxBlastLb`: cap on one rack's cook-off (default 120 lb)
- `BlastType`: `BombExplosion` (default; the sizes are worked out in its scale), `GeneralExplosion`, `MissileExplosion` or `RocketExplosion`
- `ShowInDesigner` (only changes what you see)
- `DebugDumpShape` (off by default): writes the last measured rack's shape and round positions to `BepInEx/ammo-rack-shape.obj`, for reporting measuring problems

### Other
- **Project file:** finds the game's data folder whether it is named `SimplePlanes 2_Data` or `SimplePlanes 2 (1.1)_Data`. Override it with `-p:ManagedDir=...`.

### Known limits
- **Damage per kJ:** a cannon with the Cannon tag does no game damage, so every hit on a rack from it goes through this setting. At very low values (for example 0.001), racks will almost never cook off.
- **Multiplayer:** fragments still spall into an opponent's vehicle and damage its racks, but another player's shot reaches your rack as one summed hit, judged by its biggest piece's path, not piece by piece. Piece-by-piece hits over the network are planned as a separate update.
- **Explosive filler** (HE, APHE) doesn't add to a rack's blast; only propellant does.
- **Center of mass** doesn't move with the rounds' weight.
- **Shell shapes:** if they are reworked later, the round size (`AmmoRound.cs`) has to follow. Racks, the hit check and the designer drawing all read the round from there.
