using System;
using System.Text;

namespace ArmorFragments.Sim
{
    // The impact simulation's per-particle work on the graphics card (OpenCL). ImpactSim keeps the shell's logic (its energy, its path, the
    // ricochet) on the CPU, one copy of it for both; each step it hands this solver the shell's state and the solver does what the CPU
    // does with a thread pool: push the particles out of the shell's way, tear the links, relax them color by color, let the metal flow,
    // and move the particles. Only the number of particles the shell has newly swept through comes back, once a step.
    //
    // Every OpenCL call must come from ONE thread (the worker's); the solver is not thread safe. Any failure throws, and the caller falls
    // back to the CPU for good.
    internal sealed class ClSolver : IDisposable
    {
        // The slots of the parameter array that goes to the card every step (the same numbers are named in the kernel source below).
        public const int ParamCount = 64;
        // The shell's own numbers, for the kernel that runs the shell on the card (the first group does not change during a simulation, the
        // second is the shell's state, which that kernel updates every step).
        public const int PMass = 26, PSpeed0 = 27, PEnergy0 = 28, PCost = 29, PThickness = 30, PHz = 31, PDepthLimit = 32, PSkimLength = 33,
                         PRicochet = 34, PKept = 35, PDu = 36, PDv = 37, PEps = 38, PMinSettle = 39;
        public const int PEnergy = 40, PShellActive = 41, PStopped = 42, PExited = 43, PBounced = 44, PSkimming = 45, PSkimLeft = 46,
                         PDeepest = 47, PSteps = 48, PSettle = 49, PStoppedSteps = 50, PShellSteps = 51, PLastCount = 52, PDone = 53, PMoving = 54, PDeflect = 55,
                         PNoseLen = 56, PNoseP = 57;
        public const int PCx = 0, PCy = 1, PCz = 2, PAx = 3, PAy = 4, PAz = 5, PTipS = 6, PTx0 = 7, PTy0 = 8, PTz0 = 9, PAx0 = 10, PAy0 = 11,
                         PAz0 = 12, PRadius = 13, PBody = 14, PParticle = 15, PH = 16, PDt = 17, PDamp = 18, PHasShell = 19, PBackOpen = 20,
                         PBreak = 21, PYield = 22, PRate = 23, PDuctility = 24, PReach2 = 25;

        private const string Source = @"
#define LINKS 18
__constant int OX[18] = {1,-1,0,0,0,0,1,1,-1,-1,1,1,-1,-1,0,0,0,0};
__constant int OY[18] = {0,0,1,-1,0,0,1,-1,1,-1,0,0,0,0,1,1,-1,-1};
__constant int OZ[18] = {0,0,0,0,1,-1,0,0,0,0,1,-1,1,-1,1,-1,1,-1};

#define P_CX 0
#define P_CY 1
#define P_CZ 2
#define P_AX 3
#define P_AY 4
#define P_AZ 5
#define P_TIPS 6
#define P_TX0 7
#define P_TY0 8
#define P_TZ0 9
#define P_AX0 10
#define P_AY0 11
#define P_AZ0 12
#define P_RADIUS 13
#define P_BODY 14
#define P_PARTICLE 15
#define P_H 16
#define P_DT 17
#define P_DAMP 18
#define P_HASSHELL 19
#define P_BACKOPEN 20
#define P_BREAK 21
#define P_YIELD 22
#define P_RATE 23
#define P_DUCT 24
#define P_REACH2 25
#define P_MASS 26
#define P_SPEED0 27
#define P_ENERGY0 28
#define P_COST 29
#define P_T 30
#define P_HZ 31
#define P_DEPTHLIMIT 32
#define P_SKIMLENGTH 33
#define P_RICOCHET 34
#define P_KEPT 35
#define P_DU 36
#define P_DV 37
#define P_EPS 38
#define P_MINSETTLE 39
#define P_ENERGY 40
#define P_SHELLACTIVE 41
#define P_STOPPED 42
#define P_EXITED 43
#define P_BOUNCED 44
#define P_SKIMMING 45
#define P_SKIMLEFT 46
#define P_DEEPEST 47
#define P_STEPS 48
#define P_SETTLE 49
#define P_STOPPEDSTEPS 50
#define P_SHELLSTEPS 51
#define P_LASTCOUNT 52
#define P_DONE 53
#define P_MOVING 54
#define P_DEFLECT 55
#define P_NOSELEN 56
#define P_NOSEP 57

#define MAX_ACTIVE 600.0f
#define SETTLE_STEPS 24.0f

// The index of the neighbor of particle (i, j, l) through link o, or -1 if there is none (outside the patch).
inline int neighbor(int i, int j, int l, int o, int n, int k)
{
    int ni = i + OX[o], nj = j + OY[o], nl = l + OZ[o];
    if (ni < 0 || nj < 0 || nl < 0 || ni >= n || nj >= n || nl >= k) return -1;
    return (nl * n + nj) * n + ni;
}

// The particle is inside the shell: it goes to the surface of the capsule.
inline float3 push_one(float3 q, __constant float* P)
{
    float ax = P[P_AX], ay = P[P_AY], az = P[P_AZ];
    float bodyLength = P[P_BODY], radius = P[P_RADIUS];
    float noseLen = P[P_NOSELEN];
    if (noseLen > 0.0f)
    {
        // A shell with a nose profile: a body of revolution (the same arithmetic as ImpactSim.PushShaped).
        float3 axis = (float3)(ax, ay, az);
        float3 tip = (float3)(P[P_CX] + ax * radius, P[P_CY] + ay * radius, P[P_CZ] + az * radius);
        float3 rel = q - tip;
        float s = dot(rel, axis);
        float a = -s;
        float total = radius + bodyLength;
        float pr = P[P_PARTICLE];
        if (a < -pr || a > total + pr) return q;
        float3 lat = rel - axis * s;
        float d = length(lat);
        float ac = fmin(fmax(a, 0.0f), total);
        float r, slope;
        if (ac >= noseLen) { r = radius; slope = 0.0f; }
        else
        {
            float p = P[P_NOSEP];
            float u = (noseLen - ac) / noseLen;
            float inner = fmax(1.0f - pow(u, p), 1e-6f);
            r = radius * pow(inner, 1.0f / p);
            slope = fmin(radius * pow(inner, 1.0f / p - 1.0f) * pow(u, p - 1.0f) / noseLen, 8.0f);
        }
        if (d >= r + pr) return q;
        float norm = sqrt(1.0f + slope * slope);
        float3 outward;
        if (d > 1e-6f) outward = lat / d;
        else { outward = (float3)(-ay, ax, 0.0f); if (outward.x * outward.x + outward.y * outward.y < 1e-6f) outward.x = 1.0f; }
        outward = (outward + axis * slope) / norm;
        float shift = fmin((r + pr - d) / norm, 1.2f * P[P_H]);
        return q + outward * shift;
    }
    float bx = P[P_CX] - ax * bodyLength, by = P[P_CY] - ay * bodyLength, bz = P[P_CZ] - az * bodyLength;
    float along = (q.x - bx) * ax + (q.y - by) * ay + (q.z - bz) * az;
    along = fmin(fmax(along, 0.0f), bodyLength);
    float3 nearest = (float3)(bx + ax * along, by + ay * along, bz + az * along);
    float3 d = q - nearest;
    float d2 = dot(d, d);
    float reach = radius + P[P_PARTICLE];
    if (d2 >= reach * reach) return q;

    float dl = sqrt(d2);
    float3 u;
    if (dl > 1e-6f) u = d / dl;
    else { u = (float3)(-ay, ax, 0.0f); if (u.x * u.x + u.y * u.y < 1e-6f) u.x = 1.0f; }
    float move = fmin(reach - dl, 1.2f * P[P_H]);
    return q + u * move;
}

// Has the shell's nose swept past this particle's ORIGINAL place (so it pays for it)?
inline int swept(float4 original, __constant float* P)
{
    float rx = original.x - P[P_TX0], ry = original.y - P[P_TY0], rz = original.z - P[P_TZ0];
    float s = rx * P[P_AX0] + ry * P[P_AY0] + rz * P[P_AZ0];
    float lx = rx - P[P_AX0] * s, ly = ry - P[P_AY0] * s, lz = rz - P[P_AZ0] * s;
    float lateral2 = lx * lx + ly * ly + lz * lz;
    float radius = P[P_RADIUS];
    if (lateral2 >= radius * radius) return 0;
    float reached = P[P_TIPS] - radius + sqrt(radius * radius - lateral2);
    if (s > reached) return 0;
    return 1;
}

// A link may only break near the shell, and the back layer only once the shell has come through to it.
inline int can_break(int layer, int oz, float3 a, float3 b, __constant float* P)
{
    float3 m = 0.5f * (a + b) - (float3)(P[P_CX], P[P_CY], P[P_CZ]);
    if (dot(m, m) > P[P_REACH2]) return 0;
    if (P[P_BACKOPEN] < 0.5f)
    {
        if (layer == 0 || layer + oz == 0) return 0;
    }
    return 1;
}

__kernel void init_links(__global const float4* p, __global float* rest, __global float* rest0, int n, int k, int count)
{
    int idx = get_global_id(0);
    if (idx >= count) return;
    int i = idx % n, j = (idx / n) % n, l = idx / (n * n);
    for (int o = 0; o < LINKS; o++)
    {
        int nb = neighbor(i, j, l, o, n, k);
        float r = 0.0f;
        if (nb >= 0) r = length(p[nb].xyz - p[idx].xyz);
        rest[o * count + idx] = r;
        rest0[o * count + idx] = r;
    }
}

__kernel void predict_push(__global const float4* p, __global const float4* v, __global float4* q, __global const float4* orig,
                           __global const uchar* fixedFlag, __global uchar* charged, __global int* counter, __constant float* P, int count)
{
    int idx = get_global_id(0);
    if (idx >= count) return;
    if (P[P_DONE] > 0.5f) return;
    if (fixedFlag[idx] != 0) return;
    float dt = P[P_DT];
    float3 pos = p[idx].xyz + v[idx].xyz * dt;
    if (P[P_HASSHELL] > 0.5f)
    {
        pos = push_one(pos, P);
        if (charged[idx] == 0 && swept(orig[idx], P) != 0)
        {
            charged[idx] = 1;
            atomic_add(counter, 1);
        }
    }
    q[idx] = (float4)(pos, 0.0f);
}

__kernel void push(__global float4* q, __global const uchar* fixedFlag, __constant float* P, int count)
{
    int idx = get_global_id(0);
    if (idx >= count) return;
    if (P[P_DONE] > 0.5f || P[P_HASSHELL] < 0.5f) return;
    if (fixedFlag[idx] != 0) return;
    q[idx] = (float4)(push_one(q[idx].xyz, P), 0.0f);
}

__kernel void tear(__global const float4* q, __global float* rest, __constant float* P, int n, int k, int count)
{
    int idx = get_global_id(0);
    if (idx >= count) return;
    if (P[P_DONE] > 0.5f) return;
    int i = idx % n, j = (idx / n) % n, l = idx / (n * n);
    float3 a = q[idx].xyz;
    for (int o = 0; o < LINKS; o++)
    {
        float r = rest[o * count + idx];
        if (r <= 0.0f) continue;
        int nb = neighbor(i, j, l, o, n, k);
        float3 b = q[nb].xyz;
        float len = length(b - a);
        if ((len - r) / r > P[P_BREAK] && can_break(l, OZ[o], a, b, P) != 0) rest[o * count + idx] = -1.0f;
    }
}

// One color of the links (the particles of a color are not linked to each other, so they can all move at once).
__kernel void relax(__global float4* q, __global const float* rest, __global const uchar* fixedFlag, int n, int k, int color, __constant float* P)
{
    if (P[P_DONE] > 0.5f) return;
    int cx = color & 1, cy = (color >> 1) & 1, cz = (color >> 2) & 1;
    int nx = (n - cx + 1) / 2, ny = (n - cy + 1) / 2, nz = (k - cz + 1) / 2;
    int gid = get_global_id(0);
    if (gid >= nx * ny * nz) return;
    int count = n * n * k;
    int a = gid % nx, b = (gid / nx) % ny, d = gid / (nx * ny);
    int i = 2 * a + cx, j = 2 * b + cy, l = 2 * d + cz;
    int idx = (l * n + j) * n + i;
    if (fixedFlag[idx] != 0) return;

    float3 me = q[idx].xyz;
    float3 sum = (float3)(0.0f, 0.0f, 0.0f);
    int used = 0;
    for (int o = 0; o < LINKS; o++)
    {
        float r = rest[o * count + idx];
        if (r <= 0.0f) continue;
        int nb = neighbor(i, j, l, o, n, k);
        float3 dv = q[nb].xyz - me;
        float len = length(dv);
        if (len < 1e-9f) continue;
        sum += dv * ((len - r) / len);
        used++;
    }
    if (used == 0) return;
    q[idx] = (float4)(me + sum * (1.0f / used), 0.0f);
}

__kernel void flow(__global const float4* q, __global float* rest, __global const float* rest0, __constant float* P, int n, int k, int count)
{
    int idx = get_global_id(0);
    if (idx >= count) return;
    if (P[P_DONE] > 0.5f) return;
    int i = idx % n, j = (idx / n) % n, l = idx / (n * n);
    float3 a = q[idx].xyz;
    for (int o = 0; o < LINKS; o++)
    {
        float r = rest[o * count + idx];
        if (r <= 0.0f) continue;
        int nb = neighbor(i, j, l, o, n, k);
        float3 b = q[nb].xyz;
        float len = length(b - a);
        float strain = (len - r) / r;
        if (strain > P[P_YIELD] || strain < -P[P_YIELD])
        {
            float flowed = r + P[P_RATE] * (len - r);
            if (flowed > rest0[o * count + idx] * (1.0f + P[P_DUCT]) && can_break(l, OZ[o], a, b, P) != 0) flowed = -1.0f;
            rest[o * count + idx] = flowed;
        }
    }
}

__kernel void finish(__global float4* p, __global float4* v, __global const float4* q, __global const uchar* fixedFlag, __constant float* P, int count, __global int* moving)
{
    int idx = get_global_id(0);
    if (idx >= count) return;
    if (P[P_DONE] > 0.5f) return;
    if (fixedFlag[idx] != 0) return;
    float inverse = 1.0f / P[P_DT];
    float damp = P[P_DAMP];
    float3 pos = p[idx].xyz, now = q[idx].xyz;
    float3 velocity = (now - pos) * inverse * damp;
    v[idx] = (float4)(velocity, 0.0f);
    p[idx] = (float4)(now, 0.0f);
    if (P[P_EPS] > 0.0f && dot(velocity, velocity) > P[P_EPS] * P[P_EPS]) atomic_inc(moving);
}

// The shell's logic, one step of it, by a single work-item (the same steps the CPU takes in ImpactSim.Execute, in the same order): the cost
// of the particles it swept through in the last step comes off its energy, it moves, ricochets, stops or leaves; or, with the shell gone,
// the plate is left to settle for a number of steps and then the whole simulation is done (every other kernel then does nothing). With
// this, the card can be given many steps at once and nobody has to wait for it in between.
__kernel void shell_step(__global float* S, __global const int* counter, __global int* moving)
{
    if (get_global_id(0) != 0) return;
    if (S[P_DONE] > 0.5f) return;

    // How many particles were still moving faster than the calm speed at the end of the last step (and start counting again).
    float movingBefore = (float)moving[0];
    moving[0] = 0;
    S[P_MOVING] = movingBefore;

    float total = (float)counter[0];
    if (S[P_HASSHELL] > 0.5f) S[P_ENERGY] -= (total - S[P_LASTCOUNT]) * S[P_COST];
    S[P_LASTCOUNT] = total;

    float steps = S[P_STEPS];
    if (steps >= MAX_ACTIVE + SETTLE_STEPS) { S[P_DONE] = 1.0f; S[P_HASSHELL] = 0.0f; return; }

    float energy = S[P_ENERGY];
    int active = S[P_SHELLACTIVE] > 0.5f;
    int stopped = S[P_STOPPED] > 0.5f;

    if (active)
    {
        float cx = S[P_CX], cy = S[P_CY], cz = S[P_CZ];
        float ax = S[P_AX], ay = S[P_AY], az = S[P_AZ];
        float radius = S[P_RADIUS], t = S[P_T], hz = S[P_HZ], dt = S[P_DT];
        int bounced = S[P_BOUNCED] > 0.5f, skimming = S[P_SKIMMING] > 0.5f, exited = S[P_EXITED] > 0.5f;
        float skimLeft = S[P_SKIMLEFT], deepest = S[P_DEEPEST], stoppedSteps = S[P_STOPPEDSTEPS];

        float speed = 0.0f;
        if (energy <= 0.0f || energy < S[P_ENERGY0] * 0.0004f) { stopped = 1; energy = 0.0f; }
        else
        {
            speed = sqrt(2.0f * energy / S[P_MASS]);
            speed = fmin(speed, S[P_SPEED0] * 1.0001f);
        }
        cx += ax * speed * dt;
        cy += ay * speed * dt;
        cz += az * speed * dt;

        float tipZ = cz + az * radius;
        S[P_TIPS] = (cx + ax * radius - S[P_TX0]) * S[P_AX0] + (cy + ay * radius - S[P_TY0]) * S[P_AY0] + (tipZ - S[P_TZ0]) * S[P_AZ0];

        // The plate turns the shell toward its own normal as it goes through (the energy model's deflection, spread over the thickness).
        if (S[P_DEFLECT] > 0.0f && !bounced && !skimming)
        {
            float theta0 = acos(clamp(-S[P_AZ0], -1.0f, 1.0f));
            float progress = clamp((t * 0.5f - tipZ) / t, 0.0f, 1.0f);
            float theta = fmax(theta0 - S[P_DEFLECT] * progress, 0.0f);
            float sn = sin(theta);
            ax = sn * S[P_DU]; ay = sn * S[P_DV]; az = -cos(theta);
        }

        if (S[P_RICOCHET] > 0.5f && !bounced && !skimming && (t * 0.5f - tipZ) >= S[P_DEPTHLIMIT])
        {
            skimming = 1;
            float lateral = sqrt(ax * ax + ay * ay);
            if (lateral < 1e-4f) { ax = S[P_DU]; ay = S[P_DV]; lateral = 1.0f; }
            ax /= lateral; ay /= lateral; az = 0.0f;
            skimLeft = S[P_SKIMLENGTH];
        }
        if (skimming)
        {
            skimLeft -= speed * dt;
            if (skimLeft <= 0.0f)
            {
                skimming = 0;
                bounced = 1;
                az = 0.5f;
                float norm = sqrt(ax * ax + ay * ay + az * az);
                ax /= norm; ay /= norm; az /= norm;
                energy *= S[P_KEPT];
            }
        }
        if (tipZ <= -t * 0.5f + hz) S[P_BACKOPEN] = 1.0f;
        if (tipZ < deepest) deepest = tipZ;
        if (az < 0.0f && tipZ < -t * 0.5f - radius * 1.2f) { exited = 1; active = 0; }
        if (az > 0.0f && bounced && tipZ > t * 0.5f + radius * 1.5f) active = 0;
        if (stopped)
        {
            stoppedSteps += 1.0f;
            active = stoppedSteps <= 8.0f;
        }
        if (steps >= MAX_ACTIVE) active = 0;

        S[P_CX] = cx; S[P_CY] = cy; S[P_CZ] = cz;
        S[P_AX] = ax; S[P_AY] = ay; S[P_AZ] = az;
        S[P_BOUNCED] = bounced ? 1.0f : 0.0f; S[P_SKIMMING] = skimming ? 1.0f : 0.0f; S[P_EXITED] = exited ? 1.0f : 0.0f;
        S[P_SKIMLEFT] = skimLeft; S[P_DEEPEST] = deepest; S[P_STOPPEDSTEPS] = stoppedSteps;
        S[P_ENERGY] = energy;
        S[P_STOPPED] = stopped ? 1.0f : 0.0f;
    }
    else
    {
        float settle = S[P_SETTLE] + 1.0f;
        S[P_SETTLE] = settle;
        int calm = S[P_EPS] > 0.0f && settle > S[P_MINSETTLE] && movingBefore < 0.5f;
        if (settle > SETTLE_STEPS || calm) { S[P_DONE] = 1.0f; S[P_HASSHELL] = 0.0f; return; }
    }

    S[P_SHELLACTIVE] = active ? 1.0f : 0.0f;
    S[P_HASSHELL] = active ? 1.0f : 0.0f;
    S[P_DAMP] = active ? 0.995f : 0.85f;
    if (active) S[P_SHELLSTEPS] += 1.0f;
    S[P_STEPS] = steps + 1.0f;
}

// The cracks on the two surfaces: for each column, how many of the 8 links across the plate (not through it) have torn, in the front layer
// (face 0, the top one) and in the back layer (face 1, the bottom one).
__kernel void crack_map(__global const float* rest, __global const float* rest0, __global int* cracks, __global float* stretch, int n, int k, int count)
{
    int gid = get_global_id(0);
    if (gid >= 2 * n * n) return;
    int face = gid / (n * n);
    int cell = gid % (n * n);
    int l = face == 0 ? k - 1 : 0;
    int idx = l * n * n + cell;
    int broken = 0;
    float worst = 0.0f;   // the most any of its links has been lengthened by flowing (rest / rest0 - 1): the damage it has taken
    for (int o = 0; o < LINKS; o++)
    {
        if (OZ[o] != 0) continue;
        float r = rest[o * count + idx];
        if (r < 0.0f) broken++;
        else if (r > 0.0f) worst = fmax(worst, r / rest0[o * count + idx] - 1.0f);
    }
    cracks[gid] = broken;
    stretch[gid] = worst;
}

__kernel void count_broken(__global const float* rest, __global int* counter, int total)
{
    int gid = get_global_id(0);
    if (gid >= total) return;
    if (rest[gid] < 0.0f) atomic_add(counter, 1);
}
";

        // ONE launch does all of a step's relaxation: each work-group takes a TILE x TILE patch of columns (with a one-column halo around it)
        // and every layer of them into local memory, and does all the iterations and all 8 colors there, with a barrier between colors. The
        // old way was iterations x 8 launches a step, each a handful of thousands of threads, which cost more in launching than in working.
        // The result is the same as the old one for every particle whose neighbors are inside the patch; the halo's neighbors are not moved
        // during the launch (they are read as they were when it started, from a copy: 'qin', and the result goes to another: 'qout', so the
        // answer does not depend on which work-group runs first), so the particles at a patch's edge relax against slightly old neighbors.
        // The next step reads everything again. It is checked against the old way before it is used.
        private const string FusedSource = @"
#define LINKS 18
#define TILE 8
#define HALO 1
#define SIDE (TILE + 2 * HALO)
#define MAXK 14
#define P_DONE 53
__constant int OX[18] = {1,-1,0,0,0,0,1,1,-1,-1,1,1,-1,-1,0,0,0,0};
__constant int OY[18] = {0,0,1,-1,0,0,1,-1,1,-1,0,0,0,0,1,1,-1,-1};
__constant int OZ[18] = {0,0,0,0,1,-1,0,0,0,0,1,-1,1,-1,1,-1,1,-1};

__kernel void relax_fused(__global const float4* qin, __global float4* qout, __global const float* rest, int n, int k,
                          __constant float* P, int iterations, int groupsX, __local float4* tile)
{
    if (P[P_DONE] > 0.5f) return;   // the same for the whole group: nobody is left waiting at a barrier

    int count = n * n * k;
    int tid = get_local_id(0);
    int group = get_group_id(0);
    int i0 = (group % groupsX) * TILE, j0 = (group / groupsX) * TILE;

    // The patch and its halo, every layer. A local index is (layer * SIDE + row) * SIDE + column, the halo being the first and last ones.
    for (int e = tid; e < SIDE * SIDE * k; e += TILE * TILE)
    {
        int hx = e % SIDE, hy = (e / SIDE) % SIDE, l = e / (SIDE * SIDE);
        int i = i0 + hx - HALO, j = j0 + hy - HALO;
        float4 v = (float4)(0.0f, 0.0f, 0.0f, 0.0f);
        if (i >= 0 && j >= 0 && i < n && j < n) v = qin[(l * n + j) * n + i];
        tile[e] = v;
    }
    barrier(CLK_LOCAL_MEM_FENCE);

    for (int it = 0; it < iterations; it++)
    {
        for (int color = 0; color < 8; color++)
        {
            int cx = color & 1, cy = (color >> 1) & 1, cz = (color >> 2) & 1;
            int layers = (k - cz + 1) / 2;
            int total = (TILE / 2) * (TILE / 2) * layers;
            for (int a = tid; a < total; a += TILE * TILE)
            {
                int ax = a % (TILE / 2), ay = (a / (TILE / 2)) % (TILE / 2), al = a / ((TILE / 2) * (TILE / 2));
                int lx = 2 * ax + cx, ly = 2 * ay + cy, l = cz + 2 * al;
                int i = i0 + lx, j = j0 + ly;
                if (i >= n || j >= n) continue;
                if (i == 0 || j == 0 || i == n - 1 || j == n - 1) continue;   // the patch's border is held fixed

                int idx = (l * n + j) * n + i;
                int mine = (l * SIDE + ly + HALO) * SIDE + lx + HALO;
                float3 me = tile[mine].xyz;
                float3 sum = (float3)(0.0f, 0.0f, 0.0f);
                int used = 0;
                for (int o = 0; o < LINKS; o++)
                {
                    float r = rest[o * count + idx];
                    if (r <= 0.0f) continue;
                    int other = ((l + OZ[o]) * SIDE + ly + HALO + OY[o]) * SIDE + lx + HALO + OX[o];
                    float3 dv = tile[other].xyz - me;
                    float len = length(dv);
                    if (len < 1e-9f) continue;
                    sum += dv * ((len - r) / len);
                    used++;
                }
                if (used > 0) tile[mine] = (float4)(me + sum * (1.0f / used), 0.0f);
            }
            barrier(CLK_LOCAL_MEM_FENCE);
        }
    }

    for (int e = tid; e < TILE * TILE * k; e += TILE * TILE)
    {
        int lx = e % TILE, ly = (e / TILE) % TILE, l = e / (TILE * TILE);
        int i = i0 + lx, j = j0 + ly;
        if (i < n && j < n) qout[(l * n + j) * n + i] = tile[(l * SIDE + ly + HALO) * SIDE + lx + HALO];
    }
}
";

        private IntPtr context, queue, program, device;
        private IntPtr programFused, kFused, bQ2;

        // The fused relaxation is EXPERIMENTAL and off: checked against the 8-colors way on the RX 7600 it was 1.3-1.9 x faster on thin plates
        // but slower on thick ones (more layers = more local memory a group, fewer groups at once), and it changes the result (more links
        // tear at the patches' edges: 0.1-0.6 mm of difference in the dents, a few hole cells the other way), where the 8-colors way matches
        // the CPU to 0.002 mm. A test switches it on with AllowFused before the solver is made.
        public static bool AllowFused = false;
        public bool Fused = true;
        public bool FusedReady;
        public string FusedNote = "";
        public int LocalMemBytes;
        private const int FusedTile = 8, FusedMaxLayers = 14;
        private bool fusedNow;          // used by the simulation in progress
        private int groupsX, groupsY;
        private IntPtr kInit, kPredict, kPush, kTear, kRelax, kFlow, kFinish, kBroken, kShell, kCrack;

        // True = the shell's logic runs on the card too (the whole simulation is queued in chunks, with a wait only after each chunk).
        // False = the old way: the CPU runs the shell and the card is waited for twice a step (kept to compare the two).
        public bool DeviceShell = true;
        private IntPtr bP, bV, bQ, bO, bRest, bRest0, bFixed, bCharged, bCounter, bParams, bMoving, bCracks, bStretch;
        private int capacity;
        private int n, k, count;
        private float[] zeros4;
        private readonly int[] counterValue = new int[1];

        public string DeviceName = "";
        public int ComputeUnits, ClockMhz;
        public long MemoryMb;
        public bool Discrete;
        public double BuildMs;

        public static ClSolver Create(out string error)
        {
            ClSolver s = new ClSolver();
            try
            {
                s.Open();
                error = null;
                return s;
            }
            catch (Exception e)
            {
                s.Dispose();
                error = e is DllNotFoundException ? "OpenCL.dll is not installed" : e.GetType().Name + ": " + e.Message;
                return null;
            }
        }

        private static void Check(int status, string what)
        {
            if (status != 0) throw new InvalidOperationException(what + " failed (OpenCL error " + status + ")");
        }

        private void Open()
        {
            uint platformCount;
            Check(ClNative.clGetPlatformIDs(0, null, out platformCount), "clGetPlatformIDs");
            if (platformCount == 0) throw new InvalidOperationException("OpenCL has no platform");
            IntPtr[] platforms = new IntPtr[platformCount];
            ClNative.clGetPlatformIDs(platformCount, platforms, out platformCount);

            double best = -1.0;
            foreach (IntPtr platform in platforms)
            {
                uint deviceCount;
                if (ClNative.clGetDeviceIDs(platform, 0xFFFFFFFF, 0, null, out deviceCount) != 0 || deviceCount == 0) continue;
                IntPtr[] devices = new IntPtr[deviceCount];
                ClNative.clGetDeviceIDs(platform, 0xFFFFFFFF, deviceCount, devices, out deviceCount);
                foreach (IntPtr candidate in devices)
                {
                    if (ClNative.DeviceNumber(candidate, 0x1000) != 4) continue;   // a graphics card only
                    bool discrete = ClNative.DeviceNumber(candidate, 0x1035) == 0;
                    double score = (discrete ? 1e9 : 0.0) + ClNative.DeviceNumber(candidate, 0x1002) * (double)ClNative.DeviceNumber(candidate, 0x100C);
                    if (score <= best) continue;
                    best = score;
                    device = candidate;
                    DeviceName = ClNative.DeviceString(candidate, 0x102B);
                    ComputeUnits = (int)ClNative.DeviceNumber(candidate, 0x1002);
                    ClockMhz = (int)ClNative.DeviceNumber(candidate, 0x100C);
                    MemoryMb = ClNative.DeviceNumber(candidate, 0x101F) / (1024 * 1024);
                    Discrete = discrete;
                }
            }
            if (device == IntPtr.Zero) throw new InvalidOperationException("no graphics card among the OpenCL devices");

            int err;
            context = ClNative.clCreateContext(IntPtr.Zero, 1, new[] { device }, IntPtr.Zero, IntPtr.Zero, out err);
            Check(err, "clCreateContext");
            queue = ClNative.clCreateCommandQueue(context, device, 0, out err);
            Check(err, "clCreateCommandQueue");

            System.Diagnostics.Stopwatch build = System.Diagnostics.Stopwatch.StartNew();
            program = ClNative.clCreateProgramWithSource(context, 1, new[] { Source }, null, out err);
            Check(err, "clCreateProgramWithSource");
            int status = ClNative.clBuildProgram(program, 1, new[] { device }, "", IntPtr.Zero, IntPtr.Zero);
            if (status != 0)
            {
                byte[] log = new byte[8192];
                UIntPtr size;
                ClNative.clGetProgramBuildInfo(program, device, 0x1183, (UIntPtr)log.Length, log, out size);
                throw new InvalidOperationException("the kernels did not compile (" + status + "): " + ClNative.Text(log, size).Trim());
            }
            BuildMs = build.Elapsed.TotalMilliseconds;

            kInit = Kernel("init_links"); kPredict = Kernel("predict_push"); kPush = Kernel("push"); kTear = Kernel("tear");
            kRelax = Kernel("relax"); kFlow = Kernel("flow"); kFinish = Kernel("finish"); kBroken = Kernel("count_broken"); kShell = Kernel("shell_step"); kCrack = Kernel("crack_map");
            bParams = Buffer(ParamCount * 4);
            bCounter = Buffer(4);
            bMoving = Buffer(4);

            // The fused relaxation is an extra: if the card cannot build it, the simulation goes on with the 8-colors-a-launch way.
            if (!AllowFused)
            {
                FusedNote = "the fused relaxation is off";
            }
            else try
            {
                LocalMemBytes = (int)ClNative.DeviceNumber(device, 0x1023);   // CL_DEVICE_LOCAL_MEM_SIZE
                int needed = (FusedTile + 2) * (FusedTile + 2) * FusedMaxLayers * 16;
                if (LocalMemBytes < needed) throw new InvalidOperationException("local memory is " + LocalMemBytes + " bytes, " + needed + " are needed");
                System.Diagnostics.Stopwatch fusedBuild = System.Diagnostics.Stopwatch.StartNew();
                int fusedError;
                programFused = ClNative.clCreateProgramWithSource(context, 1, new[] { FusedSource }, null, out fusedError);
                Check(fusedError, "clCreateProgramWithSource (fused)");
                int fusedStatus = ClNative.clBuildProgram(programFused, 1, new[] { device }, "", IntPtr.Zero, IntPtr.Zero);
                if (fusedStatus != 0)
                {
                    byte[] log = new byte[8192];
                    UIntPtr size;
                    ClNative.clGetProgramBuildInfo(programFused, device, 0x1183, (UIntPtr)log.Length, log, out size);
                    throw new InvalidOperationException("did not compile (" + fusedStatus + "): " + ClNative.Text(log, size).Trim());
                }
                kFused = ClNative.clCreateKernel(programFused, "relax_fused", out fusedError);
                Check(fusedError, "clCreateKernel relax_fused");
                BuildMs += fusedBuild.Elapsed.TotalMilliseconds;
                FusedReady = true;
            }
            catch (Exception e)
            {
                FusedReady = false;
                FusedNote = "the fused relaxation is not used: " + e.Message;
            }
        }

        private IntPtr Kernel(string name)
        {
            int err;
            IntPtr kernel = ClNative.clCreateKernel(program, name, out err);
            Check(err, "clCreateKernel " + name);
            return kernel;
        }

        private IntPtr Buffer(long bytes)
        {
            int err;
            IntPtr buffer = ClNative.clCreateBuffer(context, 1, (UIntPtr)bytes, IntPtr.Zero, out err);   // read-write
            Check(err, "clCreateBuffer (" + bytes + " bytes)");
            return buffer;
        }

        private void Release(ref IntPtr buffer)
        {
            if (buffer != IntPtr.Zero) ClNative.clReleaseMemObject(buffer);
            buffer = IntPtr.Zero;
        }

        // (The links' rest lengths are stored link by link: rest[link * count + particle], so that neighbouring threads read neighbouring memory.)
        // Buffers big enough for this many particles (kept and reused: a bigger simulation makes them again).
        private void Reserve(int particles)
        {
            if (particles <= capacity) return;
            Release(ref bP); Release(ref bV); Release(ref bQ); Release(ref bQ2); Release(ref bO); Release(ref bRest); Release(ref bRest0); Release(ref bFixed); Release(ref bCharged);
            // The biggest simulation the grids allow is 128 x 128 x 14 = 229,376 particles (~52 MB on the card): the first run (the check at
            // the start) makes room for that, so that a bigger impact later never has to make the card allocate new buffers in the middle of
            // a shot (it took ~200 ms).
            capacity = Math.Max(particles, 229376);
            bP = Buffer(16L * capacity); bV = Buffer(16L * capacity); bQ = Buffer(16L * capacity); bQ2 = Buffer(16L * capacity); bO = Buffer(16L * capacity);
            bRest = Buffer(4L * 18 * capacity); bRest0 = Buffer(4L * 18 * capacity);
            bFixed = Buffer(capacity); bCharged = Buffer(capacity);
            zeros4 = new float[4 * capacity];
        }

        private static void Arg(IntPtr kernel, uint index, IntPtr buffer)
        {
            Check(ClNative.clSetKernelArg(kernel, index, (UIntPtr)IntPtr.Size, ref buffer), "clSetKernelArg");
        }

        private static void Arg(IntPtr kernel, uint index, int value)
        {
            Check(ClNative.clSetKernelArg(kernel, index, (UIntPtr)4, ref value), "clSetKernelArg");
        }

        private static UIntPtr Rounded(int items)
        {
            return (UIntPtr)(((Math.Max(items, 1) + 63) / 64) * 64);
        }

        private static readonly UIntPtr[] Local = { (UIntPtr)64 };

        private void Run(IntPtr kernel, int items)
        {
            Check(ClNative.clEnqueueNDRangeKernel(queue, kernel, 1, null, new[] { Rounded(items) }, Local, 0, null, IntPtr.Zero), "a dispatch");
        }

        // A new simulation: the particles go to the card (their places, where they started, and which are held fixed), and the links' rest
        // lengths are worked out there from those places.
        // Where the time of the last simulation went (Stopwatch ticks), for the [PERF] breakdown: Begin = everything the start does; ParamWrite =
        // the blocking upload of each step's numbers (an in-order queue makes it wait for the step before); Dispatch = queueing the kernels;
        // CounterRead = the wait for the shell's swept-particle count; Finish = the wait for the card to end; Readback = the particles back.
        public long TicksBegin, TicksParamWrite, TicksDispatch, TicksCounterRead, TicksFinish, TicksReadback;
        public int Steps, SyncedSteps;

        public void Begin(float[] px, float[] py, float[] pz, float[] ox, float[] oy, float[] oz, byte[] fixedFlag, int columns, int layers)
        {
            long beganAt = System.Diagnostics.Stopwatch.GetTimestamp();
            TicksBegin = TicksParamWrite = TicksDispatch = TicksCounterRead = TicksFinish = TicksReadback = 0;
            Steps = SyncedSteps = 0;
            n = columns; k = layers; count = n * n * k;
            Reserve(count);

            float[] position = new float[4 * count], original = new float[4 * count];
            for (int i = 0; i < count; i++)
            {
                position[4 * i] = px[i]; position[4 * i + 1] = py[i]; position[4 * i + 2] = pz[i];
                original[4 * i] = ox[i]; original[4 * i + 1] = oy[i]; original[4 * i + 2] = oz[i];
            }
            UIntPtr bytes4 = (UIntPtr)(16L * count);
            Check(ClNative.clEnqueueWriteBuffer(queue, bP, 1, UIntPtr.Zero, bytes4, position, 0, null, IntPtr.Zero), "upload positions");
            Check(ClNative.clEnqueueWriteBuffer(queue, bQ, 1, UIntPtr.Zero, bytes4, position, 0, null, IntPtr.Zero), "upload predicted positions");
            if (FusedReady && Fused && DeviceShell)
                Check(ClNative.clEnqueueWriteBuffer(queue, bQ2, 1, UIntPtr.Zero, bytes4, position, 0, null, IntPtr.Zero), "upload the second predicted positions");
            Check(ClNative.clEnqueueWriteBuffer(queue, bO, 1, UIntPtr.Zero, bytes4, original, 0, null, IntPtr.Zero), "upload original positions");
            Check(ClNative.clEnqueueWriteBuffer(queue, bV, 1, UIntPtr.Zero, bytes4, zeros4, 0, null, IntPtr.Zero), "clear velocities");
            Check(ClNative.clEnqueueWriteBuffer(queue, bFixed, 1, UIntPtr.Zero, (UIntPtr)count, fixedFlag, 0, null, IntPtr.Zero), "upload fixed flags");
            Check(ClNative.clEnqueueWriteBuffer(queue, bCharged, 1, UIntPtr.Zero, (UIntPtr)count, new byte[count], 0, null, IntPtr.Zero), "clear charged flags");
            counterValue[0] = 0;
            Check(ClNative.clEnqueueWriteBuffer(queue, bCounter, 1, UIntPtr.Zero, (UIntPtr)4, counterValue, 0, null, IntPtr.Zero), "clear the counter");
            Check(ClNative.clEnqueueWriteBuffer(queue, bMoving, 1, UIntPtr.Zero, (UIntPtr)4, counterValue, 0, null, IntPtr.Zero), "clear the moving counter");

            // Kernel arguments (the buffers and the sizes stay the same for the whole simulation; only the relax color changes).
            Arg(kInit, 0, bP); Arg(kInit, 1, bRest); Arg(kInit, 2, bRest0); Arg(kInit, 3, n); Arg(kInit, 4, k); Arg(kInit, 5, count);
            Arg(kPredict, 0, bP); Arg(kPredict, 1, bV); Arg(kPredict, 2, bQ); Arg(kPredict, 3, bO); Arg(kPredict, 4, bFixed);
            Arg(kPredict, 5, bCharged); Arg(kPredict, 6, bCounter); Arg(kPredict, 7, bParams); Arg(kPredict, 8, count);
            // With the fused relaxation the particles come out of it in a second buffer, and what follows (push, flow, finish) works on that.
            fusedNow = Fused && FusedReady && DeviceShell && k <= FusedMaxLayers;
            IntPtr after = fusedNow ? bQ2 : bQ;
            if (fusedNow)
            {
                groupsX = (n + FusedTile - 1) / FusedTile;
                groupsY = groupsX;
                Arg(kFused, 0, bQ); Arg(kFused, 1, bQ2); Arg(kFused, 2, bRest); Arg(kFused, 3, n); Arg(kFused, 4, k); Arg(kFused, 5, bParams);
                Arg(kFused, 7, groupsX);
                // The tile's local memory is only as big as this simulation's layers need, so that more work-groups fit on a compute unit.
                Check(ClNative.clSetKernelArg(kFused, 8, (UIntPtr)((FusedTile + 2) * (FusedTile + 2) * k * 16), IntPtr.Zero), "the fused relaxation's local memory");
            }
            Arg(kPush, 0, after); Arg(kPush, 1, bFixed); Arg(kPush, 2, bParams); Arg(kPush, 3, count);
            Arg(kTear, 0, bQ); Arg(kTear, 1, bRest); Arg(kTear, 2, bParams); Arg(kTear, 3, n); Arg(kTear, 4, k); Arg(kTear, 5, count);
            Arg(kRelax, 0, bQ); Arg(kRelax, 1, bRest); Arg(kRelax, 2, bFixed); Arg(kRelax, 3, n); Arg(kRelax, 4, k); Arg(kRelax, 6, bParams);
            Arg(kShell, 0, bParams); Arg(kShell, 1, bCounter); Arg(kShell, 2, bMoving);
            Arg(kFlow, 0, after); Arg(kFlow, 1, bRest); Arg(kFlow, 2, bRest0); Arg(kFlow, 3, bParams); Arg(kFlow, 4, n); Arg(kFlow, 5, k); Arg(kFlow, 6, count);
            Arg(kFinish, 0, bP); Arg(kFinish, 1, bV); Arg(kFinish, 2, after); Arg(kFinish, 3, bFixed); Arg(kFinish, 4, bParams); Arg(kFinish, 5, count); Arg(kFinish, 6, bMoving);

            Run(kInit, count);
            TicksBegin = System.Diagnostics.Stopwatch.GetTimestamp() - beganAt;
        }

        // One step of the particles. Returns how many particles the shell has swept through so far (in all); that number is only read back
        // while the shell is in play (afterwards nobody waits for the card between steps).
        public int Step(float[] parameters, bool hasShell, int iterations)
        {
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            Check(ClNative.clEnqueueWriteBuffer(queue, bParams, 1, UIntPtr.Zero, (UIntPtr)(ParamCount * 4), parameters, 0, null, IntPtr.Zero), "upload the step's numbers");
            long t1 = System.Diagnostics.Stopwatch.GetTimestamp();
            TicksParamWrite += t1 - t0;
            Steps++;

            Run(kPredict, count);
            Run(kTear, count);
            for (int iteration = 0; iteration < iterations; iteration++)
            {
                for (int color = 0; color < 8; color++)
                {
                    Arg(kRelax, 5, color);
                    int cx = color & 1, cy = (color >> 1) & 1, cz = (color >> 2) & 1;
                    Run(kRelax, ((n - cx + 1) / 2) * ((n - cy + 1) / 2) * ((k - cz + 1) / 2));
                }
            }
            if (hasShell) Run(kPush, count);
            Run(kFlow, count);
            Run(kFinish, count);
            long t2 = System.Diagnostics.Stopwatch.GetTimestamp();
            TicksDispatch += t2 - t1;

            if (!hasShell) return counterValue[0];
            Check(ClNative.clEnqueueReadBuffer(queue, bCounter, 1, UIntPtr.Zero, (UIntPtr)4, counterValue, 0, null, IntPtr.Zero), "read the counter");
            TicksCounterRead += System.Diagnostics.Stopwatch.GetTimestamp() - t2;
            SyncedSteps++;
            return counterValue[0];
        }

        // The shell's numbers and its state at the start (all the slots), for the shell kernel; they stay on the card.
        public void WriteParams(float[] parameters)
        {
            Check(ClNative.clEnqueueWriteBuffer(queue, bParams, 1, UIntPtr.Zero, (UIntPtr)(ParamCount * 4), parameters, 0, null, IntPtr.Zero), "upload the shell's numbers");
        }

        // Queues 'steps' whole steps (the shell's logic and the particles' work) without waiting, then reads the shell's state back, which is
        // the one wait. True = the simulation is over (the steps queued after it ended did nothing).
        public bool RunSteps(int steps, int iterations, float[] state)
        {
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            if (fusedNow) Arg(kFused, 6, iterations);
            UIntPtr[] fusedItems = { (UIntPtr)(groupsX * groupsY * FusedTile * FusedTile) };
            UIntPtr[] fusedLocal = { (UIntPtr)(FusedTile * FusedTile) };
            for (int step = 0; step < steps; step++)
            {
                Run(kShell, 1);
                Run(kPredict, count);
                Run(kTear, count);
                if (fusedNow)
                {
                    Check(ClNative.clEnqueueNDRangeKernel(queue, kFused, 1, null, fusedItems, fusedLocal, 0, null, IntPtr.Zero), "the fused relaxation");
                }
                else
                {
                    for (int iteration = 0; iteration < iterations; iteration++)
                    {
                        for (int color = 0; color < 8; color++)
                        {
                            Arg(kRelax, 5, color);
                            int cx = color & 1, cy = (color >> 1) & 1, cz = (color >> 2) & 1;
                            Run(kRelax, ((n - cx + 1) / 2) * ((n - cy + 1) / 2) * ((k - cz + 1) / 2));
                        }
                    }
                }
                Run(kPush, count);
                Run(kFlow, count);
                Run(kFinish, count);
            }
            Steps += steps;
            long t1 = System.Diagnostics.Stopwatch.GetTimestamp();
            TicksDispatch += t1 - t0;
            Check(ClNative.clEnqueueReadBuffer(queue, bParams, 1, UIntPtr.Zero, (UIntPtr)(ParamCount * 4), state, 0, null, IntPtr.Zero), "read the shell's state");
            TicksCounterRead += System.Diagnostics.Stopwatch.GetTimestamp() - t1;
            SyncedSteps++;
            return state[PDone] > 0.5f;
        }

        private int crackCapacity;

        // How many of the 8 across-the-plate links have torn at each column's front layer (dest[0 .. n*n)) and back layer (dest[n*n .. 2*n*n)).
        // Call after the simulation, with the same columns and layers it had.
        // 'stretch' gets, for the same columns and faces, the most any surface link has been lengthened by plastic flow.
        public void ReadCracks(int columns, int layers, int[] dest, float[] stretch)
        {
            int cells = columns * columns;
            if (2 * cells > crackCapacity)
            {
                Release(ref bCracks); Release(ref bStretch);
                crackCapacity = 2 * cells;
                bCracks = Buffer(4L * crackCapacity);
                bStretch = Buffer(4L * crackCapacity);
            }
            Arg(kCrack, 0, bRest); Arg(kCrack, 1, bRest0); Arg(kCrack, 2, bCracks); Arg(kCrack, 3, bStretch);
            Arg(kCrack, 4, columns); Arg(kCrack, 5, layers); Arg(kCrack, 6, columns * columns * layers);
            Run(kCrack, 2 * cells);
            Check(ClNative.clEnqueueReadBuffer(queue, bCracks, 1, UIntPtr.Zero, (UIntPtr)(4L * 2 * cells), dest, 0, null, IntPtr.Zero), "read the cracks");
            Check(ClNative.clEnqueueReadBuffer(queue, bStretch, 1, UIntPtr.Zero, (UIntPtr)(4L * 2 * cells), stretch, 0, null, IntPtr.Zero), "read the stretch");
        }

        // The simulation is over: the particles' places come back, and the number of links that tore.
        public void End(float[] px, float[] py, float[] pz, out int brokenLinks)
        {
            long f0 = System.Diagnostics.Stopwatch.GetTimestamp();
            ClNative.clFinish(queue);
            long f1 = System.Diagnostics.Stopwatch.GetTimestamp();
            TicksFinish = f1 - f0;
            float[] position = new float[4 * count];
            Check(ClNative.clEnqueueReadBuffer(queue, bP, 1, UIntPtr.Zero, (UIntPtr)(16L * count), position, 0, null, IntPtr.Zero), "read the particles back");
            for (int i = 0; i < count; i++)
            {
                px[i] = position[4 * i]; py[i] = position[4 * i + 1]; pz[i] = position[4 * i + 2];
            }

            counterValue[0] = 0;
            Check(ClNative.clEnqueueWriteBuffer(queue, bCounter, 1, UIntPtr.Zero, (UIntPtr)4, counterValue, 0, null, IntPtr.Zero), "clear the counter");
            Arg(kBroken, 0, bRest); Arg(kBroken, 1, bCounter); Arg(kBroken, 2, count * 18);
            Run(kBroken, count * 18);
            Check(ClNative.clEnqueueReadBuffer(queue, bCounter, 1, UIntPtr.Zero, (UIntPtr)4, counterValue, 0, null, IntPtr.Zero), "read the count of torn links");
            brokenLinks = counterValue[0] / 2;
            TicksReadback = System.Diagnostics.Stopwatch.GetTimestamp() - f1;
        }

        public void Dispose()
        {
            try
            {
                Release(ref bP); Release(ref bV); Release(ref bQ); Release(ref bQ2); Release(ref bO); Release(ref bRest); Release(ref bRest0);
                Release(ref bFixed); Release(ref bCharged); Release(ref bCounter); Release(ref bParams); Release(ref bMoving); Release(ref bCracks); Release(ref bStretch);
                foreach (IntPtr kernel in new[] { kInit, kPredict, kPush, kTear, kRelax, kFlow, kFinish, kBroken, kShell, kFused, kCrack })
                    if (kernel != IntPtr.Zero) ClNative.clReleaseKernel(kernel);
                kInit = kPredict = kPush = kTear = kRelax = kFlow = kFinish = kBroken = kShell = kFused = kCrack = IntPtr.Zero;
                if (program != IntPtr.Zero) ClNative.clReleaseProgram(program);
                if (queue != IntPtr.Zero) ClNative.clReleaseCommandQueue(queue);
                if (context != IntPtr.Zero) ClNative.clReleaseContext(context);
                program = queue = context = IntPtr.Zero;
            }
            catch (Exception)
            {
                // releasing is best effort
            }
        }
    }
}
