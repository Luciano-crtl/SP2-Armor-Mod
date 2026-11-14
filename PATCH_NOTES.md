# Patch notes

## Unreleased: Ammo racks (by Jakniphe)

Builds on 0.7.0; the version number is the author's call. Tested in single player; not yet tested in multiplayer.

Tanks now carry their ammunition in racks. Racks set off by hits burn, then explode inside the hull. The vehicle is knocked out through its damaged modules instead of being blown apart, so the shell keeps its shape.

### Ammo racks
- **Any fuselage can be an ammo rack.** Turn on **Ammo rack** in the part's Armor Fragments section (the name gets an `[AMM]` tag).
- **Racks feed cannons.** A cannon with the **Cannon** toggle on draws its rounds from the racks in its group.
  - With no group set, every tagged cannon and rack share one default group.
  - For separate guns, add a group name in the part name: `[CAN k=100 g=Main]` on the gun, `[AMM g=Main]` on its racks.
- **Racks replace the Ammo Count slider.** A rack-fed cannon fires until its racks are empty, and the game's Ammo Count slider is hidden for it. Cannons without racks work as before.
- **Rounds are solid.** A rack holds only the whole rounds that fit its real shape, lying along the fuselage:
  - **Round size:** sized from the cannon. The case is 1.3 × the caliber wide and longer for faster guns; the shell in front is the game's own.
  - **Shape:** wedges, tapers and rounded corners lose the rounds that would poke out. A rack shorter than one round holds nothing.
  - **What can't be a rack:** hollow or cut fuselages, and armor.
- **Racks empty in order:** set it with the **Empty order** slider. Each rack empties from its top layer down.
- **Racks are heavy:** a rack weighs its part plus its rounds and gets lighter as you fire. A 50 mm KwK 39 round is about 3.5 kg.
- **Broken-off racks** keep their rounds, which can still cook off, but no longer feed the gun.

### Cook-offs
- **Only the rounds are the target.** A hit counts only if it passes through a round still in the rack, so the hitbox shrinks as you fire and a half-empty rack is safe at the top. Each piece of a shot is checked on its own: the shell, plate spall, and HE or APHE burst fragments, including HE spall through thin armor.
- **The chance to set it off:**
  - **Direct hits:** a shell punching through the cases is likely to set them off. That's about 64% for a 20 mm APHE, about 95% for a 37 mm, and certain for 75 mm and up. The shell counts with its full width, so it can't slip between packed rounds, even fired straight along them.
  - **Fragments:** count by the energy they leave in the rounds. They add up, but a few small ones rarely do it alone.
  - **Hits add up:** several hits count like one hit of their total.
  - **Destroyed racks:** the hit that destroys a rack always sets it off, if it goes through its rounds.
- **It burns, then explodes:** a rack on fire burns for 10 s at half the game's fire size and can't feed the gun meanwhile. Then it explodes with the rounds still inside.
- **The explosion follows real blast physics:**
  - **The charge:** the propellant left in the rack, at 25% of its TNT equivalent (a cook-off is an inefficient detonation), capped at 120 lb.
  - **The pressure:** it falls with distance ÷ the cube root of the charge. Parts are wrecked at 1000 kPa (145 psi), damaged down to 200 kPa (29 psi), and untouched below that.
  - **Reach:**

    | Rack | Wrecked within | Damaged out to |
    |---|---|---|
    | 42 × 100 mm (96 lb) | 2.7 m | 4.8 m |
    | 90 × 50 mm (55 lb) | 2.2 m | 4 m |
    | 10 × 100 mm (23 lb) | 1.7 m | 3 m |

  - **Inside your vehicle:** it hits only damage modules and other ammo racks, which roll their own chance, so a cook-off can chain.
  - **Outside, from where the blast leaves the hull:** it hits other vehicles nearby. Armor never takes damage; armored vehicles are hit in their modules and racks, and unarmored vehicles in every part.
- **Bang and fire jet:** the cook-off vents through the nearest part marked **Cook-off vent** (a fuselage at the turret ring, or a hatch), or just above the rack. There's one bomb-style bang, 75% of a game bomb of the same weight, then a jet of fire for 3 to 10 s. The bang is only for show; the blast physics does all the damage.
- **The wreck keeps burning:** the jet settles into a fire that never goes out (only water puts it out), and every other cook-off vent catches fire a moment later.
- **Knocked out, not blown apart:** once a rack catches fire, the game can no longer blow the vehicle up. Racks themselves are never destroyed or exploded by the game.
- **Multiplayer:** other players see the same fires and the same bang.
- **Funky Trees:**
  - `<group>_rounds`: rounds left.
  - `<group>_fire`: 1 while a rack burns.
  - `<group>_boom`: 1 once a rack has exploded. Wire a detacher to it to throw the turret.
  - The default group's variables are `Ammo_rounds`, `Ammo_fire` and `Ammo_boom`.

### Vehicles keep their shape (every vehicle)
- **No more damage explosions.** Spall in a hull's inner fuselages used to trigger the game's "part explodes" roll, and a part welded to the cockpit then blew up the whole vehicle and stripped its parts. The game no longer blows parts or vehicles up because of damage. That covers the damage-stage roll, the damage limit, damaged fuel tanks and propellers, and explosion chains.
  - Vehicles are knocked out through their modules and racks instead.
  - Destroyed parts stay attached, so wings no longer fall off planes.
  - The game doesn't count vehicles as destroyed.
  - Weapons' own explosions are unchanged.
- **Armored vehicles take damage only in their modules and racks.** On a vehicle with any armor, shells and fragments damage only damage modules and ammo racks; the rest of the structure just slows them down. Vehicles without armor, like planes, take damage on every part.

### Designer
- **See-through racks:** racks are drawn see-through with their rounds inside. You can still click them, the selected one turns orange, and screenshots show them normally. Switch this off with **See-through racks**.
- **Rack read-out:** status, rounds held, layout, rounds lost to the shape, inside space, round size, shell, rounds weight and Funky Trees variables. It updates as you reshape the fuselage or change the cannon.
- **Cannon read-out:** ammo group, where its rounds come from, rounds, round size and shell.
- **Estimated penetration on every cannon:** shell energy, mass and muzzle velocity, plus the thickest plate it gets through at 0, 30 and 60 degrees. It's worked out at the muzzle, without ricochets. Example: a 100 mm Slug at 1000 m/s gets through 142 / 123 / 71 mm. HE shows "none (HE bursts)".
- **Cook-off vent:** a toggle on fuselages.
- **Armor is exclusive:** a part can't be armor and also a damage module, ammo rack or cook-off vent. Turning one on turns the other off.

### X-ray view
- **Your racks show their rounds live** (F8, or around a simulated shot): rounds vanish from the top as you fire, a burning rack glows orange, and an exploded one is empty.

### Settings (config file; the host's values apply in multiplayer)
- **`[AmmoRacks]`:**

  | Setting | Default | What it does |
  |---|---|---|
  | `Enabled` | | Switches ammo racks on or off |
  | `CaseDiameterRatio`, `CaseLitresPerMJ`, `RoundSpacingPercent` | | Round size and packing |
  | `CookOffDirectHit` | 50% | Chance a shell through the rounds sets them off, before its energy |
  | `CookOffEnergyKJ` | 100 | kJ into the rounds for a 63% chance; lower is more sensitive |
  | `CookOffBurnSeconds` | 10 | How long a rack burns before it explodes |
  | `CookOffTNTEfficiency` | 25% | Share of the propellant that counts as a real explosion |
  | `CookOffWreckKPa` | 1000 | Blast pressure that wrecks a part |
  | `CookOffDamageKPa` | 200 | Blast pressure below which nothing is damaged |
  | `BlastStrength` | | Multiplies each round's pounds |
  | `MaxBlastLb` | 120 | Cap on one rack's pounds |
  | `BlastVisualPercent` | 75% | Size of the bang |
  | `BlastType` | | Which game explosion is shown |
  | `ShowInDesigner` | | Only changes what you see |
  | `DebugDumpShape` | off | Writes a rack's measured shape to a file, for bug reports |

- **`[Fragments]`:**

  | Setting | Default | What it does |
  |---|---|---|
  | `GameDamageExplosions` | off | On restores the game's damage explosions |
  | `ArmoredStructureDamage` | off | On lets an armored vehicle's structure take damage again |

### Known limits
- **Multiplayer:** another player's shot reaches your rack as one summed hit, judged by its biggest piece and by energy only, so their direct hits aren't recognized as such. Piece-by-piece hits over the network are planned for a later update.
- **Explosive filler** in HE and APHE shells doesn't add to a rack's explosion; only propellant does.
- **Center of mass** doesn't move with the rounds' weight.
- **Shell shapes:** if they're reworked, the round size has to follow (`AmmoRound.cs`).

### For modders
- **Project file:** finds the game's data folder whether it's named `SimplePlanes 2_Data` or `SimplePlanes 2 (1.1)_Data`. Override it with `-p:ManagedDir=...`.
