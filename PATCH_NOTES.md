# Crew

0.10.0

Crew members are the seats inside the vehicle. A vehicle whose whole crew is dead is killed, with its hull intact and nothing exploding. An armored vehicle now dies when an ammo rack explodes or when its last crew member dies.

As with the rest of the mod, a build that uses any of its parts plays by all of it: a vehicle with ammo racks needs crew to load its rack-fed cannons.

Setting up
- Turn on "Crew" on a seat (Seat, Seat Ejection or Car Seat): that seat is one crew member. Only seats can be crew.
- A new crew member has no role. Pick his trade: Commander, Gunner, Loader, Driver or Radio operator. The trade brings its own job: a Driver drives, a Gunner aims, a Loader loads. Commander and Radio operator have none: they are spares.
- A copied, mirrored or pasted crew seat (or one in a subassembly) comes in with no role and no extra jobs.
- Under "Also does", give him more jobs (Driving, Aiming, Loading) to make him double up, for example a Commander who also loads. Any crew size works.
- A vehicle has at most one Driver, one Commander and one Radio operator, and only one crew member drives (any trade can be the one, through "Also does"). What another seat already has is marked * in the panel and can't be picked. Gunners and Loaders can be doubled.
- In the name: `[CRW t=commander j=load]` (`j` = the jobs he also does; no `t` = no role).

Jobs
- Drive: the vehicle's engines (car engines and turboshafts) give their horsepower x the drive efficiency.
- Aim: rotators (hinge, small rotator, rotator) with "Aimed by crew" turned on (a turret ring, a gun's elevation) turn at their speed x the aim efficiency, whatever their input asks for. Other rotators aren't affected. In the name: `[AIM]`.
- Load: cannons fed by ammo racks reload in their Firing Delay / the load efficiency. A rack-fed cannon on a vehicle with no crew doesn't fire. Cannons on Ammo Count aren't affected.

Efficiency
- A crew member's efficiency at a job is his trade's skill at it times his workload: one job 100%, two jobs 90%, three jobs 80%.
- Skill (Drive / Aim / Load): Commander 70 / 85 / 85, Gunner 65 / 100 / 70, Loader 65 / 70 / 100, Driver 100 / 60 / 60, Radio operator 85 / 70 / 85, no role 60 / 60 / 60 (%). A crew member with no role does no job until one is left without crew.
- Crew on the same job stack, best first, each adding less: two aimers (the second adds 30% of his efficiency); any number of loaders. Aim and Load can go past 100%: rotators turn faster than their set speed and the reload is shorter than the Firing Delay.
- Loading depends on the round's weight (shell, case and propellant): it takes one loader per 20 kg, rounded, at least 1. With fewer, it's slower in proportion (one loader on a two-loader round: 50%).
- Past the loaders it takes, extra loaders keep helping with no ceiling, each adding less: the k-th extra adds his efficiency x 20% x the square root of the loaders it takes / k. Heavier rounds gain more from each extra loader, so heavier guns end up higher.
- With 100% loaders (1, 2, 3, 4, 6, 10, 20 loaders): 1-loader round: 100, 120, 130, 137, 146, 157, 171%. 2-loader round: 50, 100, 128, 142, 159, 177, 199%. 3-loader round: 33, 67, 100, 135, 164, 190, 219%.
- Example: a 5 s gun with a light round, loaded by a Commander who also aims: 85% x 90% = 77%, so it reloads in 6.5 s. With two Loaders: 120%, so it reloads in 4.2 s.
- A rack-fed cannon's panel shows its round's weight and how many loaders it takes.
- A job the vehicle needs that nobody has (no crew member of that trade, and nobody also doing it) goes, at the start, to the crew member who would do it best ("auto" in the panel).
- The crew panel lists this seat's jobs and the vehicle's, one row each with its efficiency. A cannon fed by racks shows its reload with this crew and who loads it; an aimed rotator shows its turn speed.

Bodies
- In flight, a character sits in every crew seat: the seat's Designer Character (Chad WW1 when it is set to None), posed by the seat. A seat your own character sits in keeps yours. Every player sees them.
- Hits are decided by the body, not the seat: shells and fragments pass through the seat itself and meet capsules around the sitting character's head, torso, arms and legs (on the skeleton the seat poses him with, so they follow his pose and the vehicle), which slow them like a human body would.
- The game's own shells fly through crew seats too. A shell whose path crosses a crew member (an open-topped vehicle, a gap in the armor) hits him there, even if nothing solid is in the way.
- Fragments don't ricochet off bodies.

Losses
- A crew member dies when the damage to his body has used up the seat's health, or when the seat breaks off the vehicle. The game never blows a crew seat up or off.
- A dead crew member's body stays in his seat and still stops fragments.
- His jobs go to the living crew member who would do each best, so the vehicle gets slower, weaker and slower to reload as its crew dies.
- Taking over a job takes time: the new holder needs `[Crew] SwitchSeconds` (5 s) to reach its station and does that job at 0% until then. If he was the only one on it, the job stops meanwhile: no driver, the engines are off until he starts them again; no aimer, the aimed rotators freeze; no loader, no shots. A job someone else still does (a second Loader or Gunner) carries on at their level. At the start of a flight, jobs nobody was given are filled at once.
- When the last crew member dies, the vehicle is killed: kill credit, kill cam and respawn, as when an ammo rack explodes, but with no fire or blast. Nothing on it works any more: its engines are off and every rotator stops.
- Crew seats take shell and fragment damage on armored vehicles, like damage modules and ammo racks. A crew seat can also be a damage module, to read that crew member's damage in Funky Trees.

Funky Trees
- `Crew_alive` = crew members alive; `Crew_drive`, `Crew_aim`, `Crew_load` = each job's efficiency (0 to 1).

Settings (host): `[Crew] Enabled`, `[Crew] SwitchSeconds` (5 s).

Bug fix
- An ammo rack could hold no rounds for a whole flight (its cannon never fired) when its fuselage's shape was built later than the rack waited for, which happened easily with the game sped up. The rack now waits in real time and keeps measuring until the shape is there, then fills.

Next
- A part finder in the designer, to find stray parts that bring the mod's rules onto a build (a forgotten armor block, rack or crew seat).

# Per-face armor

0.9.0

A fuselage used as armor is a hollow shell whose six faces are plates of their own thickness. Shells and fragments fly through open faces and the inside of the block, and meet an armored face from outside or from inside.

Setting up
- Turn on "Armor" on a fuselage: every face is open (0 mm).
- With "Per face" off, one "Thickness" sets every face at once. With "Per face" on, face buttons (Front, Back, Left, Right, Top, Bottom) appear and the slider sets the picked face on its own. Turning "Per face" off sets every face to the thickest one. "Per face" stays as you left it when you select another block (a duplicate opens the way its original was being edited); a block whose faces differ always opens with it on.
- Thickness is 0 to 500 mm, by slider or typed into the box beside it. The face being set is tinted orange on the block and the other armored faces blue; with "Per face" off, every armored face is orange.
- In the name: `[ARM h=100 b=50 fr=80 lt=20 rt=20]` (`fr bk lt rt tp bm`, millimeters; a face not listed is open).
- Symmetry: a block copied by mirror symmetry gets its armor mirrored, Left and Right swapped, so the outer face of one side is the outer face of the other. While symmetry is on, changing a block's armor (or a cutter's hole wall armor) changes its symmetry partners too, mirrored ones with Left and Right swapped, radial ones the same; one undo reverts them all. With symmetry off, only the selected part changes.
- Armor is always per face. A block with whole-block armor (`t=`) becomes every face at that thickness when it is selected in the designer; until then it keeps working as one solid plate.
- Every slider in the mod's panels has a box beside it where the value can be typed.

Faces
- Front and Back are the block's ends (the game's front and rear sections); Left, Right, Top and Bottom are its sides, looking toward the front with the block's top up.
- Faces follow the block's real shape: tapered, offset, curved, cut and sliced blocks keep the face the player sees. Each part of the surface belongs to the face it points closest to, so a slice or cut surface is one face across its whole width, and a cut end stays the end up to 45 degrees of slope.
- On a rounded section the four sides meet where the surface turns past 45 degrees. Rounded or bulged corners split there between the sides.
- A triangle section (trapezium at 100%): the slanted walls are Left and Right, the base is Top or Bottom, and the edge that shrank to a point has no area.
- A cone (the fuselage Cone style): its surface is the four sides all the way to the tip, the base is Back, and Front exists only where the tip is cut off.
- Fixed parts with no fuselage shape (the parts-list nose cones and blocks) take the face whose direction is closest to the surface.
- A hollow fuselage's plate is its outer surface; its inner walls are part of no face.
- Holes cut with the carving parts are open: a face has no plate where a hole goes through it, and the hole's own walls are part of no face (they are not tinted and weigh nothing).
- Hole wall armor: select a cutter that cuts into an armor block and "Hole wall armor" appears (a cutter that cuts no armor shows nothing). Switched on, the walls of the holes it carves into armor are plates of the "Wall thickness" set there: shells and fragments flying through the hole meet them, they weigh like any plate (an "Hole walls" row under the block's armor weight), and they are tinted with the block's other armored faces. Tag on the cutter: `[HOL t=20]`.

How it plays
- An armored face is a plate like any other: ricochet, block or penetration, with the face's thickness and the armor's hardness and brittleness.
- An open face is empty space: no ricochet, no stop, no HE burst, no fuse start. So is a carved hole: shells and fragments fly through it into the block (or out of it), although the game's own physics still treats the block as solid there.
- A shell through the front plate flies on inside the block, through any parts in there, and meets the back plate from inside. Spall does the same.
- Cook-off blasts count the armored faces between the rack and each part.

Weight
- Armor weighs what its steel would: each armored face's area on the block times its thickness, at 7850 kg/m3. A 1 x 1 m face at 100 mm is 785 kg.
- The weight is added to the block's own, in the designer (part mass, craft mass, center of mass) and in flight. Nothing extra is saved in the craft file.
- The armor panel shows "Armor weight" and, below it, one row per face with its weight ("open" for an open face). In flight the log lists each armor block's weight by face.
- Whole-block armor (`t=`) weighs as every face at that thickness.
- The weight sits where the plates are: each face's weight at the middle of that face, half its thickness in. The block's center of mass moves toward its armor, so a thick front plate makes the vehicle nose-heavy and the designer's center-of-mass marker moves with it. The craft file keeps the block's own center of mass.

0.9.0.1
- The armor panel's weight by face has a row for each face, so it can be read in full.

0.9.1.1
- Faces: each part of a block's surface belongs to the face it points closest to, so sliced and cut blocks get one face across a whole cut surface.
- The face tint and the armor weight read only the block's own surface: text and decals on it no longer catch the tint or count as armor.
- The blue tint is stronger, follows a block while it is dragged (a fresh duplicate), and "Per face" stays as you left it from block to block.
- Carved holes are open, and a cutter that cuts into armor can armor its holes' walls ("Hole wall armor", `[HOL t=]`).
- Symmetry: mirrored copies get their armor mirrored (Left and Right swapped), and with symmetry on, armor edits reach the symmetry partners.
- The face tint is drawn only over the block in the designer, never during a flight.
- When the game itself kills a vehicle (fewer than 70% of its parts or wing area still connected to the cockpit), the log says so, with the numbers and the parts no longer connected.
- Fix: an explosive (HE or APHE) shell the mod has resolved is removed without the game's own explosion. That explosion went off where the shell stopped, often inside the hit vehicle, and its blast could cut the vehicle's parts loose and get it killed.

# Ammo racks

Ammo racks are fuselages that hold a cannon's ammunition. When hit through their rounds they can catch fire and explode inside the vehicle, killing it without blowing the hull apart.

0.10.0
- Cut fuselages can be ammo racks: the rounds are packed to the cut shape. Hollow fuselages still hold none.

0.9.2
- Racks pack their rounds in whichever layout holds the most: lying along the fuselage, across it, upright, or on a 45-degree diagonal; in square or staggered rows (each row nested into the gaps of the one below); centered or shifted. The rounds still all point the same way. A rack never holds fewer rounds than before. The rack's panel shows the layout it picked.
- New "Fullness" slider on racks (0 to 100%): how full the rack starts. The top rounds are left out, as if already fired, so a part-full rack has safe space at the top, weighs less and makes a smaller cook-off.
- Symmetric guns share their settings: with symmetry on, changing a cannon (Cannon on/off, shell hardness, fuse, filler, brittleness, Add rack / Remove rack) changes its symmetry partners too. Each partner draws from the matching rack: when the racks are symmetric too, the left gun keeps the left rack and the right gun the right one; a single rack stays shared. Making a gun symmetric also gives each copy its nearest matching rack. A duplicated gun keeps the original's settings and racks.
- Draw order: a gun empties its numbered racks first, lowest number up, then the racks with no order, nearest first (racks with the same number also go nearest first). New "No order" toggle on racks, on by default; turn it off to give the rack an Empty order (1 to 20). Racks from before count as No order.
- Racks follow the gun: when you drop a gun or a rack onto the craft (drag, clone, paste) or link it with the part connections tool, each gun swaps any of its racks that has symmetry copies for the copy nearest to it. A duplicated gun on the other side takes that side's rack as soon as it's connected. A gun that lists both racks of a mirrored pair keeps both and drains its own side first. Loading the craft, undo and other edits never change rack picks.

0.8.1
- Cannons pick their own ammo racks with "Add rack" and "Remove rack". Cannons of different sizes each draw from their own racks, instead of every cannon sharing every rack.
- With no rack picked, a cannon uses its Ammo Count slider.
- Vehicles built on 0.8.0: pick the racks once on each cannon.
- The cook-off blast and its bang come from the rack, for every vehicle around; the vent only carries the fire. Armor in the way, the burning vehicle's own hull included, shields what's behind it.
- Racks exploding at the same moment go up as one blast, with one fire jet per vent.
- The bang's size grows with the cube root of the explosive, like the blast's reach, so low-velocity rounds (little propellant) still make a visible cloud. A full 120 lb rack looks the same as before.

Setting up
- Turn on "Ammo rack" on a fuselage (`[AMM]`) and "Cannon" on the gun (`[CAN]`). On the cannon, "Add rack" opens the game's part picker: click the racks it draws from (as a wheel picks its engine). A cannon can draw from several racks, and cannons that fire the same round can share one. With no rack picked, the cannon keeps its Ammo Count slider.
- A rack holds whole rounds sized from the cannons that draw from it, packed into the fuselage's real shape. It weighs its rounds and empties from the top down. A cannon empties its racks in the "Empty order" set on each rack (`o=`).
- Mark a fuselage at the turret ring or a hatch as a "Cook-off vent" (`[VNT]`).

Cook-off
- Only hits that pass through a round still in the rack count: the shell itself, plate spall, or burst fragments.
- A shell through the rounds is likely to set them off (about 64% for 20 mm, 95% for 37 mm, certain from 75 mm); fragments add up by the energy they leave.
- The rack burns for 10 s, then explodes. Fire jets out of the nearest vent, and every vent keeps burning.
- The explosion kills the vehicle (a normal kill: credit, kill cam, respawn), while the hull stays intact.

Explosion
- Sized by the propellant left in the rack, as a real blast from the rack: pressure falls with distance. A full 100 mm rack wrecks modules within 2.7 m and damages them out to 4.8 m.
- It damages your vehicle's modules and racks, and other vehicles nearby. Armor in the way, your own hull included, shields what's behind it. Armor itself is never damaged.
- Racks that explode at the same moment make one blast: their explosive adds up, up to the `MaxBlastLb` cap.

Armored vehicles keep their shape
- On vehicles with armor, the game no longer blows up parts or the vehicle from damage, and only damage modules and ammo racks take damage from shells and fragments. They are killed by an ammo rack exploding.
- Vehicles without armor, like planes, take damage and die as before.

Also
- Every cannon shows its shell energy and estimated penetration at 0, 30 and 60 degrees.
- Racks are drawn see-through in the designer with their rounds inside, and show their rounds in the X-ray.
- Funky Trees: `Ammo_rounds`, `Ammo_fire`, `Ammo_boom` (wire `_boom` to a detacher to throw the turret). Racks with a group name (`g=`) get their own `<group>_` variables.
- Settings are in the `[AmmoRacks]` section of the config file; `GameDamageExplosions` and `ArmoredStructureDamage` (both off) are under `[Fragments]`.

# Damage marks and impact simulation

0.10.0
- Armor plates now show what hit them: dents, holes, grooves, fragment pits and HE burst dishes, drawn as normal/height maps (with mip levels) and, near the damage, as real geometry within a per-vehicle vertex budget. Thick blocks get marks on their sides too.
- Each projectile hit is simulated in 3D (lattice of particles, on the graphics card through OpenCL when available, otherwise the CPU) and drawn when the shell reaches the plate. It is visual only: the energy model still decides the outcome. Quality, threads, queue and time limit are in the "Damage maps" panel; the grid never gets finer than the map can show.
- The shell's nose shape (AP ogive, APHE capped, HE blunt) shapes the hole. Thin or ductile plates petal outward on the back face; thick or brittle ones shear a clean plug. Brittle plates crack radially; the hole rim is smooth and irregular.
- Paint: impacts scorch the paint, flake it off to bare steel, and leave a bright steel rim at the hole ("Scorched and flaked paint"; plates with patterned or textured paint lose the pattern).
- Marks are shared with other players who have the mod, and sent again to players who join later.
- Fix: the frame rate no longer collapses after visiting the designer and returning to flight.
- Armor plates stay out of the game's mesh merge so they can carry marks.
- Added an impact camera and a vehicle X-ray.

0.10.0 (impact look)
- Sparks, a flash, paint dust and smoke where a shell hits armor: a fountain of sparks for a hit, a stream of long sparks along a ricochet, a hot spray and dark smoke out of the back of a penetration, a fireball for an HE shell. "Impact effects" and "Impact effects amount" in the panel.
- A ricochet now skids a band of paint off the plate: wide and bright where it touched, thinning out with fine lines along the way, with a scorched spot where it hit.
- The steel that shows through the paint is metallic and shiny (brightest where a shell skidded or cut through, duller where it was torn), and scorched paint is dull ("Shiny steel, dull soot"; one more 21 MB atlas per vehicle).

0.10.0 (finer marks, bending)
- The detail of the marks no longer depends on how many armor plates the vehicle has: each plate gets its own piece of the vehicle's texture the first time it is hit (up to 768 pixels per meter, less if the texture is filling up). Before, a vehicle with many plates shared one texture and every mark came out at about 120 pixels per meter.
- A heavy blow bends the whole plate it hits, and a little the armor plates joined to it ("Heavy hits bend plates").
- Marks no longer push a plate's metal through the armor plates that overlap it ("Marks stay inside overlapping plates").

0.10.0 (marks on every part)
- Shells that go through or burst on parts that are not armor (wings, plain fuselage, engines...) now leave marks: a hole where they went in and, if they came out, a bigger torn hole, with scorch and bare metal round it. They are decals laid over the surface, only where seen from outside ("Marks on parts that are not armor", "Decals kept per vehicle").
