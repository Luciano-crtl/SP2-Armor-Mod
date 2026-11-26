# Cannon smoke

0.13.1

- Much faster smoke shadows: the shadow is worked out once per frame into a small map instead of in every pixel of the game's shadow map. It looks the same and costs a small fraction of what it did (it was most of the frame drop on many PCs).
- Vehicles, walls and the ground cost the smoke much less, especially when nothing moves.
- "Measure smoke cost" now also compares the new shadow with the old one and measures the drawing without the shadow.

0.13.0

- Cannons now make real smoke: a fluid simulation on the graphics card that flows around vehicles, terrain and buildings, drifts with the wind, rises while hot and casts shadows. Every shot also lights up its surroundings and lifts dust in the colour of the ground.
- The smoke follows the gun: its caliber, muzzle energy, barrel length and muzzle brake. Barrels heat up with every shot and keep smoking.
- Propellers, rotors, jets and moving vehicles push the smoke.
- New "Cannon smoke" section in the flight panel, with subsections. The host decides how the smoke looks for everyone with the mod; each player sets only the performance (quality and number of clouds). Players without the mod don't see it.
- Fixed: cannons fired with mouse aim had no muzzle flash, sound or smoke, and other players did not see their flash.
- Fixed: on a server, the host's crew on/off setting was also used for the ammo racks.

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
- Thickness is 0 to 500 mm, by slider or typed into the box beside it. The face being set is tinted purple on the block and the other armored faces blue; with "Per face" off, every armored face is purple.
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

0.12.1
- Armor view: a shield button at the bottom right of the designer, beside the XML overload button, turns it on and off (grey off, blue on; off each time the designer opens). While it is on, every armored face of the craft is colored by its thickness: the thicknesses on the craft are ranked, the thinnest light green, through yellow and orange, to the thickest dark red, whatever and however many they are. Open faces and parts without armor get no color; armored hole walls get their thickness's color; a part armored as a whole is colored all over. The colors are solid (paint, decals and text under them do not show), also in the game's X-ray view. At each point only the armored surface nearest the camera is colored: faces hidden behind other parts are not. An armored face is colored on both sides, so it shows from inside the block too (on a hollow block, on its inner wall); so is an armored hole wall: to see armor inside the craft, move the camera inside. The face tint while editing works the same way (in the X-ray view without the armor view, outer sides only).
- The armor view lists the craft's thicknesses at the top left with their colors, thinnest first (with more than 16, an even pick of them including the thinnest and the thickest). A menu open on the left side of the designer pushes the list to its right.
- The face being set is tinted dark purple, a color not on the armor view's thickness scale. With the armor view off, the other armored faces of the block being edited are blue. With it on, that block is colored by thickness like the rest, and with "Per face" on the face being set is drawn over it in purple.
- The face being set is shown by its button turning blue (the game's own selected style); so is a crew seat's trade.
- The face tint shows only while its block is selected: clicking empty space clears it.
- A thickness typed in the box or set with the slider is kept without pressing Enter: clicking a face button, another part or anywhere else takes it, with its undo step and the craft's new weight. A face button works on the first click right after typing or dragging.
- The face tint follows the game's own hidden flag for the block, so it stays on when the game draws the block as part of a combined mesh, and it rebuilds its colors if they were lost. When it is not drawn for a selected per-face block, the log says why (`[TINT]`).
- Text and decals laid on a part are not taken for its shape: ammo rack capacity, the flight X-ray views, the rack view and the armor view read only the part's own meshes.

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

0.11.0
- Armor plates now show what hit them: dents, holes, grooves, fragment pits and HE burst dishes, drawn as normal/height maps (with mip levels) and, near the damage, as real geometry within a per-vehicle vertex budget. Thick blocks get marks on their sides too.
- Each projectile hit is simulated in 3D (lattice of particles, on the graphics card through OpenCL when available, otherwise the CPU) and drawn when the shell reaches the plate. It is visual only: the energy model still decides the outcome. Quality, threads, queue and time limit are in the "Damage maps" panel; the grid never gets finer than the map can show.
- The shell's nose shape (AP ogive, APHE capped, HE blunt) shapes the hole. Thin or ductile plates petal outward on the back face; thick or brittle ones shear a clean plug. Brittle plates crack radially; the hole rim is smooth and irregular.
- Paint: impacts scorch the paint, flake it off to bare steel, and leave a bright steel rim at the hole ("Scorched and flaked paint"; plates with patterned or textured paint lose the pattern).
- Marks are shared with other players who have the mod, and sent again to players who join later.
- Fix: the frame rate no longer collapses after visiting the designer and returning to flight.
- Armor plates stay out of the game's mesh merge so they can carry marks.
- Added an impact camera and a vehicle X-ray.

0.11.0 (impact look)
- Sparks, a flash, paint dust and smoke where a shell hits armor: a fountain of sparks for a hit, a stream of long sparks along a ricochet, a hot spray and dark smoke out of the back of a penetration, a fireball for an HE shell. "Impact effects" and "Impact effects amount" in the panel.
- A ricochet now skids a band of paint off the plate: wide and bright where it touched, thinning out with fine lines along the way, with a scorched spot where it hit.
- The steel that shows through the paint is metallic and shiny (brightest where a shell skidded or cut through, duller where it was torn), and scorched paint is dull ("Shiny steel, dull soot"; one more 21 MB atlas per vehicle).

0.11.0 (finer marks, bending)
- The detail of the marks no longer depends on how many armor plates the vehicle has: each plate gets its own piece of the vehicle's texture the first time it is hit (up to 768 pixels per meter, less if the texture is filling up). Before, a vehicle with many plates shared one texture and every mark came out at about 120 pixels per meter.
- A heavy blow bends the whole plate it hits, and a little the armor plates joined to it ("Heavy hits bend plates").
- Marks no longer push a plate's metal through the armor plates that overlap it ("Marks stay inside overlapping plates").

0.11.0 (marks on every part)
- Shells that go through or burst on parts that are not armor (wings, plain fuselage, engines...) now leave marks: a hole where they went in and, if they came out, a bigger torn hole, with scorch and bare metal round it. They are decals laid over the surface, only where seen from outside ("Marks on parts that are not armor", "Decals kept per vehicle").

# Turret mouse aim

0.12.0
- Mark a rotator as **Turret** (the designer's "Turret" toggle: a turret ring, a gun elevation) and a cannon as a turret ("Create turret group" on the cannon). Then switch the aim on with Backspace (`AimKey` in the config; or the panel's Tools > "Turret mouse aim") in flight.
- The mouse turns the camera (an orbit view is selected for you) and the turret turns by itself until the pointer cannon points at what is under the circle in the middle of the screen. A small marker shows where the cannon really points: green when it is on target, yellow while it turns, red at the turret's limit.
- The aim is stabilized: when the hull pitches or rolls, the turret keeps the gun on the circle. A rotator that does not move the pointer cannon is left out (the log says so).
- Hold **Alt**: the cursor comes back for the game's interface. Hold **C**: the camera is freed from the turret, which keeps pointing where it was while you look around; let go and the camera comes back gradually. **Right button**: gradual zoom (held). **Left button**: fire.
- The turret turns at the rotator's own speed (and the crew's aim efficiency, if you use the crew). Settings: `[TurretAim]` in the config (toggle key, look key, zoom, response).
- In mouse aim the camera turns around a point centered on the vehicle and 0.5 meters above its highest point (`PivotHeight` in `[TurretAim]`, also in the panel), so at full zoom it looks over the turret from above.
- The panel has a **Mouse aim** section: status, on/off, the two keys (press "Change ... key", then the new key), zoom, turret response and the camera pivot height.
- The turret no longer swings around the circle: it aims from where the gun will be a moment from now ("Turret damping" in the Mouse aim section), and if the aim still swings it lowers its own gain until it settles (the log says so).
- The turrets of a vehicle with many of them (a ship) are calibrated together instead of one by one, so the aim starts in seconds, not half a minute. The turret's own delay is measured when the aim starts and the control allows for it: the barrel no longer swings back and forth around the target. Each turret lowers its own gain if it still swings.
- The turrets now brake in proportion to how fast the gun is moving (a derivative term: "Turret braking" in the Mouse aim section, `BrakingSeconds` in the config). On a ship with many turrets they used to push the hull into a growing swing, and overshoot big moves; with the braking they settle without swinging.
- Mouse aim firing: a turret fires only when its gun points at the cursor (the others hold fire while the rest shoot; tolerance in the panel), a salvo goes one cannon at a time with a short gap between them ("Salvo ripple"), and a cannon does not fire while its barrel points at a part of its own vehicle within 20 meters. The turrets of a group also start following the cursor one after another, a few hundredths of a second apart, so they don't all push the hull at the same instant.
- Many turrets on one hull body (a ship): each turret's gain is divided by the number of turrets of the group that share the hull ("Slow turrets sharing a hull", on by default), because turrets turning together rocked the hull. A turret that still swings lowers its own gain by itself, and the camera pivot now follows the centre of mass of the whole vehicle, so a rocking part no longer shakes the camera.
- A turret that still swings now teaches itself: it first assumes its barrel follows its rotators later (the measured delay is for a slow move, a swing is faster), and only if that does not stop it does it lower its gain. The "turrets sharing a hull" count is now every turret of the active group, since they are all on one vehicle.
- Turrets sharing a hull are slowed by the square root of their number instead of the number itself ("Shared-hull slowing" in the panel: 1 = by the number, 0 = not at all), and a turret now keeps nudging toward the cursor on errors of a few hundredths of a degree instead of sitting still.
- The swing detector no longer mistakes you sweeping the cursor for a swinging turret (which made the turrets slow and gave them a slow wobble): it only counts when the cursor is still and the turret is already close to it.
- Mouse aim test mode: "Control only one turret (test)" in the Mouse aim section makes only one turret of the active group follow the cursor and fire (the others hold where they are); the "<" and ">" buttons pick which one.
- A turret with a springy joint (it rang at 2-3 Hz after every move, the barrel turning three times as far as its rotator) no longer swings: the turret control always assumes the barrel needs at least 130 ms to follow its rotators, whatever the calibration measured.
- Turrets that rock their own hull (many on one ship): the control watches the hull's own spin and, when it starts rocking, lowers the gain of all the turrets on it until it calms, whether you are moving the cursor or not. A turret that swung no longer assumes more and more lag (that made a lone turret swing by itself).
- Many turrets on one hull: each turret's gain is now 1.9 divided by the number of turrets ("Turrets at full gain per hull"), the gap each turret closes is smoothed over 0.4 s ("Gap smoothing") so the turrets stop chasing the 2 Hz rocking of their hull, and the tiny constant-speed nudge for very small errors is only used when two or fewer turrets share the hull (with many it kept the hull rocking).
- Turret groups on the flight screen: with the mouse aim on and more than one turret group, a bar for each group appears at the bottom right, above the camera buttons. A lit bar is a group that follows the mouse aim, a dark one holds where it is; click a bar (hold Alt to free the cursor) to switch that group, and several groups can follow the aim together. The group key (Tab) now picks just the next group.
- Turret groups have names now: a new group gets the next name of the military phonetic alphabet (Alpha, Bravo, Charlie...), and the designer has a "Group name" row to rename a group (all its turrets). On the flight screen each group's bar shows its name and has three small buttons under it, Ground / Air / Both: they are dimmed while you control the group by hand, and a click on one takes the group off the manual control and sets its automatic mode (the automatic aiming itself is not in yet, so for now such a group holds where it is).
- **Optical sight:** on the pointer cannon the designer has **Pick sight camera** (click a Camera part, like picking an ammo rack; best a First Person camera part sitting on the gun and looking along the barrel). In mouse aim, **Shift** switches to that camera and back. Through the sight the mouse turns the aim (slower when zoomed) and the turret follows; the right button zooms the sight (gradually); Alt still gives the cursor back. Settings: the sight key, zoom and sensitivity in the Mouse aim section.
- Through the sight: hold **C** to free the view from the turret (look round the inside of the tank while the turret keeps its aim; let go and the view comes back gradually); the **mouse wheel** sets the normal zoom, which stays, and the **right button** zooms further while held.
- The aim circle is now much thinner, the gun marker disappears through the sight camera, and both are drawn like the crosshair of Minecraft: they invert whatever is behind them, so they always stand out on any scene (they no longer change color with the aim state; the panel's status line says it).
- **Turret groups:** on a cannon the designer has **Create turret group**. In a group you can **Add turret to group** / **Remove turret from group** (click a cannon, like picking an ammo rack): all the turrets of a group are equals, and in mouse aim all of them are aimed at the same point at the same time (mark the rotators that turn each one as Turret). Each turret is turned by the rotators that move it and no earlier turret of its group, so a second turret on the same turret ring only needs its own elevation rotator marked. A vehicle can have several groups: only one is aimed (and fires) at a time, and the **next group key** (Tab by default, in the Mouse aim section) switches. Each turret has its own sight camera and its own marker on the screen; the sight key uses the first turret of the active group that has one. Kept right when the game renumbers or deletes parts.
- **Fixed cannons:** a turret can also have **fixed cannons** (Add fixed cannon / Remove fixed cannon): cannons that sit on the turret and move with it, but are not aimed by themselves.
- **Fire inputs in mouse aim:** the game's own weapon selection is ignored while the aim is on: only the cannons of the active turret group (its turrets and their fixed cannons) fire, and no other cannon of the vehicle does. Every turret and every fixed cannon has its own **Fire by key** toggle in the turret's designer section: off = the mouse button (left by default), on = the fire key (Space by default). A held input fires every cannon of its set at once, each at its own Firing Delay (still stretched by the crew's loading, and still using the ammo racks). The mouse button (Left or Middle), the fire key and the next group key are changeable in the Mouse aim section of the panel. The right button stays the zoom.
- **Ammo racks on the flight screen:** a bar for each ammo rack above the game's fire buttons (bottom left), made like the throttle and fuel bars: on the left the rack's round (caliber and kind, like "50mm AP"; "AP/HE" if the cannons that draw from it differ), on the right the rounds left, and the bar fills with what is left of what the rack started with. The bar turns red below 20% and while the rack burns, has exploded or is off the vehicle. A click on any bar switches all of them between rounds left and percent (like the speed bar does between IAS, GS and TAS). Both are in the new "Ammo display" section of the panel (`AmmoDisplay/Enabled`, `AmmoDisplay/ShowPercent`).

- **Automatic aim for turret groups:** each group's bar now has two buttons under it, **Ground** and **Air** (the old third button is gone: light both for both). A click on one takes a group that you control by hand off the manual control and sets just that; after that each click switches that option on or off (both off = the group holds again). A group on automatic aims and fires by itself at the nearest target of that kind that its shell can reach: the bar says what it is shooting at ("ground 850 m", "air 1.2 km", or "searching"). The targets come from the game's own list of targets: its NPC aircraft are air, its ground vehicles and ships are ground, and another player's vehicle (the game does not say what it is) counts as air when it is higher than 12 m above the ground or fast (70 m/s) and clear of it, otherwise as ground. The aim leads a moving target and aims above it by the shell's fall (gravity and drag, flown like the game's physics does it, with your vehicle's speed added to the shell's); it fires one cannon at a time, only when the gun is on target and clear of your own vehicle. Targets behind the ground are left out. Settings in the Mouse aim section of the panel: fire by itself, range (2000 m), lead moving targets, allow for shell drop, only targets in clear view, skip friendly targets, and the height and speed that make a player's vehicle count as air. The group that follows the mouse is never automatic, and one group always stays on the mouse.
- Automatic aim follows a moving target much better: the turrets used to trail a plane by several degrees (a recording of a ship's turrets showed them about two seconds behind it) and now turn at the rate the target moves instead of only closing the gap, close a big gap faster (the gentle gain that keeps many turrets from rocking the hull now only applies once they are near the target), and the turrets that cannot reach the target (stopped at their limit, on the wrong side of the ship) no longer slow down the ones that can.
- A turret rotator that is blocked by something (a barrel on the deck, a turret on another turret) now stops pushing. Before, the angle the rotator asks for kept running ahead of the part and the joint pushed harder and harder, which tore parts of the ship apart. The mod follows where each part really is (the same "Current Angle" the game shows for a rotator); when the asked-for angle gets ahead of it and the part does not follow, the rotator puts the asked-for angle back where the part is, may only turn away from the obstacle, and tries again after a few seconds (each time it is blocked at the same place it waits twice as long, up to 30 s). The first touch can't be avoided (it only stops once it has met the obstacle), and a rotator whose joint can't be read is not watched (the log says so). Settings in the Mouse aim section: stop rotators at obstacles (on), degrees ahead (2.5), wait before trying again (4 s); the panel shows how many rotators are watched and how many are blocked.

- Turret groups: you can now leave NO group under the mouse aim. Click the bar of the group that is controlled by hand to let it go (or click one of its Ground / Air buttons while it is the only one): the groups on automatic keep aiming and firing, the rest hold, and the camera still orbits (the circle and the sight are off, the status says "No group is controlled by hand"). The group key (Tab) picks the first group when none is controlled. Before, one group always had to stay controlled.

- The mod's keys (aim on/off, group, sight, free look, fire) are ignored while you type in the multiplayer chat or any other text box, so Backspace no longer switches the turret aim on or off when you erase a letter.
- The ammo rack bars and the turret group bars are now drawn under the game's other menus (they used to cover them).

- Turrets are now built from cannons. Select a cannon in the designer and press "Create turret": it gets a name (a number, 1, 2, 3..., the next free one; you can rename it). A turret has its own rotators (the ring and the elevation hinge: "Add rotator" / "Remove rotator", with the game's part picker), an optional optical sight camera, its auxiliary cannons ("Add auxiliary cannon", they move with the turret) and its fire-by-key switch. When a turret has rotators linked, the mod moves only those to aim it; with none linked it still works out by itself which of the old Turret-marked rotators move the barrel. The Turret toggle on rotators is gone (a rotator that still has the old mark shows "Turret rotator (old mark)" so you can clear it).
- Only turrets can be in a turret group. A new turret is in no group: use "Create group" or "Join <group name>", and inside a group "Add turret to group", "Remove turret from group" (they only list turrets) or "Leave group". A turret that is in no group is not aimed or fired by the mouse aim or the flight-screen bars. Turrets and groups made before this change keep working (a turret from before is in the group it had and has no name; its label is its part id).

- Mouse aim: a turret ring that turns all the way round in the game now does so under the mouse aim too. The mod used to stop every rotator at its Range setting (a ring set to 90 could only reach +-90, half a circle) even when the craft's own controls ask for more. Now the limits come from the rotator's input controller, the way the game works it out: a plain game axis (Roll, Pitch...) stays within -1..1 times the controller's min and max; `clamp(expression, low, high)` is limited to low..high; any other Funky Trees expression with no clamp (for example `x/90` on a ring with Range 90, which is simply the angle x) is not limited at all. A gun elevation with `clamp(y/25, -1, 0.45)` now stops at the real limits (about -11 to +25 degrees with Range 25, inverted) and not at +-25. The log line of each rotator now says where its limits came from.

- Mouse aim and automatic aim: the arcs that a craft's own rotator controls allow are now found by running the craft's expressions through the game's own evaluator (so a ring built with `?:` and `ang` that only allows +-60 degrees stays within +-60, a gun elevation `clamp(-A2, -10, 20)/90` stays between -10 and +20, and a plain `x/90` ring still turns all the way round). The previous version treated every expression that was not a simple `clamp(...)` as "no limit", which sent the rings of a ship with arc-limited turrets into the deck and into each other.

- Mouse aim and automatic aim, experimental: "Direct control" (off by default; Mouse aim section of the panel, or `TurretAim/DirectControl`). Instead of closing a fraction of the gap every physics step, each rotator is moved along a speed profile: it speeds up over the "ramp" time, runs at its own top speed (read from the rotator) and slows down to arrive exactly on the aim, working from where the barrel will settle. The hull stabilizer, the shared-hull gain division and the gap smoothing are not used in this mode. Settings: "Direct control: ramp" (0.3 s) and "Direct control: dead band" (0.05 deg). Turn it off to get the previous control back.

- Direct control (experimental): fixed a swing of the gun's elevation. Near the aim the rotators now turn at a gain times the angle still to go ("Direct control: gain near the aim", 3 per second) instead of the full speed profile, so a springy joint whose barrel rings at 3 to 5 Hz is no longer chased back and forth at the rotator's top speed.

- Direct control (experimental) with several turrets on one hull: it now uses the same protections the gain-based control had, which it was missing, so a ship's turrets no longer swing the hull at 2 Hz. From two turrets on, the gap each rotator closes is smoothed (Gap smoothing), reduced by the gun's own speed times "Direct control: braking (several turrets)" (0.15 s), and the gain near the aim is divided by the number of turrets (Turrets at full gain per hull) and cut further if the swing detectors trip. A lone turret uses none of them, as before.
- Automatic aim: a group now prefers the target that the most of its turrets can actually point at, using each rotator's real arc, instead of simply the nearest one (setting "Automatic aim: prefer targets the turrets can reach", on by default). The group bar shows how many turrets can reach the target ("air 850 m  7/12"), and the log says it when the target is chosen. Turrets that cannot reach still turn as far as they can.

- Mouse aim: the operating system's cursor no longer shows up when you let go of C (free look) while the camera comes back to the aim; it stays hidden and locked, as with the mouse look on (Alt still frees it).

- Mouse aim, experimental "Free cursor" (off by default; Mouse aim section of the panel, or `TurretAim/FreeCursor`): in the orbit view the ring is moved by the mouse over the screen instead of being fixed at the middle, and the turrets aim where it is. The camera does not follow at once and there is no hard box: while the ring is near the middle the camera stays still, it starts to turn a little before the ring reaches the edge of the "zone" (30% of half the view's height) and follows more and more as the ring goes further, and the ring drifts back toward the middle as the camera catches up. It never leaves the screen (the mouse is slowed toward the edge). Settings: zone, camera follow time, sensitivity. C (free look) and Alt work as before, and the sight view is unchanged.

- Mouse aim: the free cursor and the direct control are now on by default (both can be turned off in the Mouse aim section of the panel). With the free cursor, when the mouse has been still for about half a second the camera slowly turns until the ring is back at the middle of the screen (the aim does not change, only the view); "Free cursor: recenter when the mouse is still" is the time that takes (3 s; 0 = the ring stays where it is).

- Mouse aim: fixed the mouse wheel not zooming the orbit camera when the free cursor is on (the game only reads the wheel while its own mouse look is on; the mod now zooms the camera itself in that case, the way the game does). Fixed the ring jumping aside for a single frame in the sight view when a shell leaves the muzzle: the aim ray now ignores shells in flight, and the sight's aim distance is the median of the last three readings.

- Mouse aim: five control profiles. In the Mouse aim section of the panel, "Profile 1" to "Profile 5" load a saved set of the turret control's gains (direct control with its ramp, gain, braking and dead band; response, braking and damping of the gain-based control; shared-hull slowing; gap smoothing; start stagger). Change the sliders as you like and press "Save gains to the picked profile" to keep them. The line above the buttons says which profile is picked and whether the settings still match it. The first time, all five start as the numbers in use. They are stored in `BepInEx/config/com.yourname.armorfragments.turret-profiles.txt`, so "Reset to defaults" does not erase them.

- Mouse aim: the picked control profile can be renamed. The "Profile name" box under the profile buttons renames the profile that is picked at that moment (up to 16 characters; the name is shown on its button and in the status line; leave it empty to get "Profile N" back). Names are kept in the same profiles file.

0.12.2 (mouse aim: easier setup, scope sight; armor readout)

Setting up
- On a cannon, turn on **Mouse aim**. Then **Pick traverse** (the turret ring) and **Pick elevation** (the gun's hinge). A slot left empty is left to your own controls; a gun with neither fires whenever you click.
- **Fires with**: Left click or Key. With Key, type the gun's own key in the box (a letter, a digit or a name such as Space or LeftCtrl), so every gun can have its own.
- **Group** is off by default: every Mouse aim gun follows the reticle and fires together. Turn it on only for separate batteries (ships); the group bars and Tab then work as before.
- A gun's limits are only its rotators' own sliders: Range, and the Min and Max of their input. Input expressions are for driving without mouse aim.
- Rotators have **Limited / Unlimited** (the game's shortest-angle mode: turns all the way round, the short way). Hinges don't.
- Rotators and hinges have **Speed in deg/s**: a slider, and a box for any number.
- Turrets, auxiliary cannons and rotator links made before still work.

In flight
- Mouse aim switches on by itself for a vehicle with Mouse aim guns. The mouse button at the bottom right switches it off and on (hold Alt to click it). If it can't start, the reason shows on the screen.
- The flight menu has a **Mouse aim** button under Armor Fragments: everyday settings on top, the turret control's tuning under Advanced.
- The reticle: a ring where you aim and a dot where each gun points (the free cursor is on by default; off, the ring stays in the middle). The right button zooms more while held, C looks around.
- The wheel steps the orbit camera through fixed distances (3, 5, 8, 13, 20, 32 m), easing between them. The camera rises the further out it is (the point it turns around rises up to 10 m at the furthest step), so far targets can be aimed at over the vehicle. Scrolling in from the closest step goes into the gun sight, and scrolling out below the sight's 2x comes back to the orbit view.
- **Gun Sight part** (Weapons, after the Tank Cannon): the game's Camera part with none of its settings. Place it where a gun's sight is, facing the way the gun points. A craft with one needs the mod to open.
- On a Mouse aim cannon, **Gun sight**: **Pick camera sight** (Ghost view turns on while picking; only Gun Sights no other gun uses, except guns of the same turret group) or **Gun as gun sight** (the sight sits at the muzzle, looking down the barrel). One sight per gun; with neither, the gun has no sight view. Sights picked as plain Camera parts before keep working.
- **Sight view** (Shift): a scope, the screen black round a circle in the middle. The wheel zooms it 2x, 4x, 8x, 16x, 32x, straight to each step, with the magnification shown under the right side of the crosshair (the zoom is kept for the next visit); out below 2x goes back to the orbit view. The mouse moves the scope itself; the guns aim at what its middle meets and catch up at their own speed, with a ring for each gun where its barrel points, so a gun still turning, or one that can't reach, stays off the middle while the others settle on it. **V** (NextSightKey) goes to the next sight when the guns being aimed have several.
- The scope has a crosshair with range marks below its middle (every 100 m to 2 km, then every 500 m to 6 km; labelled in hundreds of meters every 400 m). Put the target's range mark on the target and fire. The marks are worked out for each gun's own shell (muzzle velocity, gravity and drag) every time the aim starts. A Gun Sight shared by the guns of a group uses the gun marked **Ranging gun** (designer, under Gun sight), else the biggest.
- Zoomed in, the turret settles finer: it stops correcting at a share of the view's height instead of a fixed 0.05 degree, so the gun stays on the middle of a 32x sight.
- Ammo bars (bottom left): a cannon that draws from no rack gets a bar too, named the same way (caliber and round, "120mm AP"), with the game's Ammo Count; it fills against the rounds it had at the start. Cannons with unlimited ammo get none.
- While mouse aim is on, the game's mouse-as-joystick is switched off (it fought the aim). Mouse aim that went off because the camera view was changed comes back by itself when the orbit view does (the log says which view it was changed to). A gun turned by a single rotator counts as on target from that rotator's direction alone, so it fires and its ring settles.
- Armor view: hover over armor in the designer for a readout: **effective armor** (the armor over the cosine of the angle), the **angle** the line from the camera through the mouse meets the surface at, and the **armor** there. It hides over menus, while a mouse button is held and after a click until the mouse moves on.
