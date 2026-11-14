# Ammo racks

Ammo racks are fuselages that hold a cannon's ammunition. When hit through their rounds they can catch fire and explode inside the vehicle, knocking it out without blowing the hull apart.

Setting up
- Turn on "Ammo rack" on a fuselage (`[AMM]`) and "Cannon" on the gun (`[CAN]`). Racks feed the cannons of their group (`g=`; one default group if empty) and replace the cannon's Ammo Count.
- A rack holds whole rounds sized from its cannon, packed into the fuselage's real shape. It weighs its rounds and empties from the top layer down, in the "Empty order" set on each rack (`o=`).
- Mark a fuselage at the turret ring or a hatch as a "Cook-off vent" (`[VNT]`).

Cook-off
- Only hits that pass through a round still in the rack count: the shell itself, plate spall, or burst fragments.
- A shell through the rounds is likely to set them off (about 64% for 20 mm, 95% for 37 mm, certain from 75 mm); fragments add up by the energy they leave.
- The rack burns for 10 s, then explodes. Fire jets out of the nearest vent, and every vent keeps burning.

Explosion
- Sized by the propellant left in the rack, as a real blast: pressure falls with distance. A full 100 mm rack wrecks modules within 2.7 m and damages them out to 4.8 m.
- It damages your vehicle's modules and racks, and other vehicles nearby. Armor in the way shields what's behind it. Armor itself is never damaged.

Vehicles keep their shape
- The game no longer blows up parts or vehicles from damage. Vehicles are knocked out through their modules instead.
- On armored vehicles, only damage modules and ammo racks take damage from shells and fragments.

Also
- Every cannon shows its shell energy and estimated penetration at 0, 30 and 60 degrees.
- Racks are drawn see-through in the designer with their rounds inside, and show their rounds in the X-ray.
- Funky Trees: `<group>_rounds`, `<group>_fire`, `<group>_boom` (wire `_boom` to a detacher to throw the turret).
- Settings are in the `[AmmoRacks]` section of the config file.
