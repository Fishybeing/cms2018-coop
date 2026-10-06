import bpy, sys, struct, math, os
from mathutils import Matrix, Vector, Quaternion

argv = sys.argv[sys.argv.index("--") + 1:]
mech_path, anim_path, out_path, preview_dir = argv[0], argv[1], argv[2], argv[3]
TARGET_HEIGHT = 1.8
FPS = 30.0
CLIPS = ["Idle", "Walk", "Run"]

bpy.ops.wm.read_factory_settings(use_empty=True)
scene = bpy.context.scene

bpy.ops.import_scene.fbx(filepath=mech_path)
T = next(o for o in bpy.data.objects if o.type == 'ARMATURE')
M = next(o for o in bpy.data.objects if o.type == 'MESH' and o.parent == T)
existing = set(bpy.data.objects)

bpy.ops.import_scene.gltf(filepath=anim_path)
S = next(o for o in bpy.data.objects if o.type == 'ARMATURE' and o not in existing)
for o in bpy.data.objects:
    if o not in existing and o.type == 'MESH':
        o.hide_render = True
        o.hide_viewport = True

def key(name):
    return name.split(":")[-1].replace("mixamorig", "")

if T.animation_data:
    T.animation_data.action = None
T.data.pose_position = 'REST'
bpy.context.view_layer.update()

deps = bpy.context.evaluated_depsgraph_get()
me_eval = M.evaluated_get(deps)
mesh = me_eval.to_mesh()
mw = M.matrix_world.copy()
verts_world = [mw @ v.co for v in mesh.vertices]
nrot = mw.to_3x3().normalized()
normals_world = [(nrot @ v.normal).normalized() for v in mesh.vertices]

minz = min(v.z for v in verts_world)
maxz = max(v.z for v in verts_world)
height = maxz - minz
s = TARGET_HEIGHT / height

bones = list(T.data.bones)
depth = {}
def bdepth(b):
    d = 0
    while b.parent:
        d += 1
        b = b.parent
    return d
bones.sort(key=bdepth)
index = {b.name: i for i, b in enumerate(bones)}

def rest_world(arm, b):
    m = arm.matrix_world @ b.matrix_local
    return m.to_translation(), m.to_quaternion().normalized()

Trest = {b.name: rest_world(T, b) for b in bones}
hips_name = next(b.name for b in bones if key(b.name) == "Hips")
hx, hy = Trest[hips_name][0].x, Trest[hips_name][0].y
origin = Vector((hx, hy, minz))
print("mesh height", height, "scale", s, "bones", len(bones))

src_by_key = {key(b.name): b for b in S.data.bones}
def sample_source(action, t):
    S.animation_data.action = action
    f = action.frame_range[0] + t * scene.render.fps / scene.render.fps_base
    scene.frame_set(int(math.floor(f)), subframe=f - math.floor(f))
    pose = {}
    for pb in S.pose.bones:
        m = S.matrix_world @ pb.matrix
        pose[pb.name] = (m.to_translation(), m.to_quaternion().normalized())
    if ALIGN is not None:
        pose = {k: (ALIGN @ p, (ALIGN @ r).normalized()) for k, (p, r) in pose.items()}
    return pose

ALIGN = None
if S.animation_data is None:
    S.animation_data_create()
tpose = bpy.data.actions.get("TPose")
Srest = sample_source(tpose, 0.0) if tpose else {b.name: rest_world(S, b) for b in S.data.bones}
print("source reference", "TPose action" if tpose else "rest pose")
tname = {key(b.name): b.name for b in bones}
lt = Trest[tname["LeftUpLeg"]][0] - Trest[tname["RightUpLeg"]][0]
ls = Srest[src_by_key["LeftUpLeg"].name][0] - Srest[src_by_key["RightUpLeg"].name][0]
ang = math.atan2(lt.y, lt.x) - math.atan2(ls.y, ls.x)
ALIGN = Quaternion((0.0, 0.0, 1.0), ang)
print("align degrees", round(math.degrees(ang), 1))
Srest = {k: (ALIGN @ p, (ALIGN @ r).normalized()) for k, (p, r) in Srest.items()}
src_hips = src_by_key["Hips"]
src_minz = min(p.z for p, r in Srest.values())
ratio = (Trest[hips_name][0].z - minz) / max(1e-6, (Srest[src_hips.name][0].z - src_minz))
print("hips ratio", ratio)

def solve_pose(src_pose):
    world = {}
    for b in bones:
        Pt0, Rt0 = Trest[b.name]
        sb = src_by_key.get(key(b.name))
        if b.parent is None:
            if sb is not None:
                Ps, Rs = src_pose[sb.name]
                Ps0, Rs0 = Srest[sb.name]
                R = (Rs @ Rs0.inverted() @ Rt0).normalized()
                P = Pt0 + (Ps - Ps0) * ratio
            else:
                R, P = Rt0, Pt0
        else:
            Pp, Rp = world[b.parent.name]
            Pp0, Rp0 = Trest[b.parent.name]
            if sb is not None:
                Ps, Rs = src_pose[sb.name]
                Ps0, Rs0 = Srest[sb.name]
                R = (Rs @ Rs0.inverted() @ Rt0).normalized()
            else:
                R = (Rp @ Rp0.inverted() @ Rt0).normalized()
            offset = Rp0.inverted() @ (Pt0 - Pp0)
            P = Pp + Rp @ offset
        world[b.name] = (P, R)
    return world


C = Matrix(((-1, 0, 0), (0, 0, 1), (0, -1, 0)))
Ci = C.inverted()
def to_u_pos(p):
    q = C @ ((p - origin) * s)
    return (q.x, q.y, q.z)
def to_u_dir(d):
    q = C @ d
    return (q.x, q.y, q.z)
def to_u_rot(q):
    m = C @ q.to_matrix() @ Ci
    u = m.to_quaternion().normalized()
    return (u.x, u.y, u.z, u.w)

def local_of(world, b):
    P, R = world[b.name]
    if b.parent is None:
        return to_u_pos(P), to_u_rot(R)
    Pp, Rp = world[b.parent.name]
    lr = (Rp.inverted() @ R).normalized()
    lp = (Rp.inverted() @ (P - Pp)) * s
    lpu = C @ lp
    return (lpu.x, lpu.y, lpu.z), to_u_rot(lr)

rest_world_pose = {b.name: Trest[b.name] for b in bones}
rest_local = [local_of(rest_world_pose, b) for b in bones]

if S.animation_data is None:
    S.animation_data_create()
clips = []
for name in CLIPS:
    act = bpy.data.actions.get(name)
    if act is None:
        print("missing clip", name)
        continue
    length = (act.frame_range[1] - act.frame_range[0]) * scene.render.fps_base / scene.render.fps
    count = max(2, int(round(length * FPS)))
    frames = []
    for i in range(count):
        t = length * i / count
        world = solve_pose(sample_source(act, t))
        frames.append(world)
    hips_first = frames[0][hips_name][0]
    hips_last = solve_pose(sample_source(act, length))[hips_name][0]
    drift = hips_last - hips_first
    out_frames = []
    for i, world in enumerate(frames):
        P, R = world[hips_name]
        corr = drift * (i / count)
        P = Vector((P.x - corr.x, P.y - corr.y, P.z))
        world[hips_name] = (P, R)
        hp = to_u_pos(P)
        rots = [local_of(world, b)[1] for b in bones]
        out_frames.append((hp, rots))
    clips.append((name, count, out_frames, frames))
    print("clip", name, "frames", count, "drift", tuple(round(v, 3) for v in drift))

uv_layer = mesh.uv_layers.active.data if mesh.uv_layers.active else None
mesh.calc_loop_triangles()
group_names = {g.index: g.name for g in M.vertex_groups}
orig = M.data

def category(bname):
    k = key(bname)
    if k.startswith("Head") or k == "Neck" or "Hand" in k:
        return 0
    if "Foot" in k or "Toe" in k:
        return 2
    return 1
colors = [(0.86, 0.68, 0.55, 1.0), (0.18, 0.30, 0.55, 1.0), (0.12, 0.10, 0.09, 1.0)]

vweights = []
vcat = []
for v in orig.vertices:
    ws = []
    for g in v.groups:
        n = group_names.get(g.group)
        if n in index and g.weight > 0.0001:
            ws.append((index[n], g.weight))
    ws.sort(key=lambda x: -x[1])
    ws = ws[:4]
    tot = sum(w for _, w in ws) or 1.0
    ws = [(i, w / tot) for i, w in ws]
    while len(ws) < 4:
        ws.append((0, 0.0))
    vweights.append(ws)
    vcat.append(category(bones[ws[0][0]].name) if ws[0][1] > 0 else 1)

import numpy as np
TEX = 1024
Nv = np.array([tuple((v - origin) * s) for v in verts_world])
vc = np.array(vcat)
head_idx = np.where((vc == 0) & (Nv[:, 2] > 1.45))[0]
nose = Nv[head_idx[np.argmin(Nv[head_idx, 1])]]
top = Nv[:, 2].max()
EYE_Z = nose[2] + 0.034
MOUTH_Z = nose[2] - 0.031
BROW_Z = EYE_Z + 0.019
EYE_X = 0.031
hips_n = (Trest[hips_name][0] - origin) * s
BELT_LO = hips_n.z + 0.015
BELT_HI = hips_n.z + 0.06
hand_name = next(b.name for b in bones if key(b.name) == "LeftHand")
HAND_X = abs(((Trest[hand_name][0] - origin) * s).x)
print("face nose", np.round(nose, 3), "eye z", round(EYE_Z, 3), "belt", round(BELT_LO, 3), round(BELT_HI, 3))

def surf_y(x, z):
    sel = head_idx[(np.abs(Nv[head_idx, 0] - x) < 0.015) & (np.abs(Nv[head_idx, 2] - z) < 0.015)]
    return Nv[sel, 1].min() if len(sel) else nose[1] + 0.02

FRONT_Y = nose[1] + 0.05
SKIN = np.array([0.86, 0.67, 0.54])
HAIR = np.array([0.16, 0.11, 0.08])
SHIRT = np.array([0.36, 0.42, 0.48])
PANTS = np.array([0.13, 0.17, 0.26])
BELT = np.array([0.30, 0.18, 0.09])
BOOTS = np.array([0.11, 0.085, 0.07])
rng = np.random.default_rng(7)

def shade(P, W):
    x, y, z = P[:, 0], P[:, 1], P[:, 2]
    cat = W.argmax(1)
    out = np.zeros((len(P), 3))
    skin = cat == 0
    cloth = cat == 1
    boots = cat == 2
    out[skin] = SKIN
    out[boots] = BOOTS
    out[boots & (z < 0.03)] = BOOTS * 0.6

    shirt = cloth & (z >= BELT_HI)
    belt = cloth & (z >= BELT_LO) & (z < BELT_HI)
    pants = cloth & (z < BELT_LO)
    out[shirt] = SHIRT
    out[belt] = BELT
    out[pants] = PANTS
    front = y < -0.02
    buckle = belt & front & (np.abs(x) < 0.024)
    out[buckle] = np.array([0.62, 0.62, 0.6])
    out[buckle & (np.abs(x) < 0.014) & (np.abs(z - (BELT_LO + BELT_HI) / 2) < 0.012)] = BELT * 0.7
    collar = shirt & (z > 1.46) & (np.abs(x) < 0.12)
    out[collar] = SHIRT * 0.82
    placket = shirt & front & (np.abs(x) < 0.007) & (z < 1.46)
    out[placket] = SHIRT * 0.85
    button = placket & (np.abs(x) < 0.004) & (np.mod(z - BELT_HI, 0.075) < 0.009)
    out[button] = np.array([0.85, 0.85, 0.82])
    patch = shirt & front & (x > 0.05) & (x < 0.13) & (z > 1.27) & (z < 1.315)
    out[patch] = np.array([0.93, 0.93, 0.9])
    edge = patch & ((x < 0.055) | (x > 0.125) | (z < 1.275) | (z > 1.31))
    out[edge] = np.array([0.65, 0.12, 0.1])
    stitch = patch & (np.abs(z - 1.2925) < 0.0035) & (x > 0.065) & (x < 0.115)
    out[stitch] = np.array([0.2, 0.25, 0.45])
    pocket = shirt & front & (x < -0.05) & (x > -0.13) & (z > 1.23) & (z < 1.32)
    pocket_edge = pocket & ((x > -0.055) | (x < -0.125) | (z < 1.235) | (z > 1.315) | (np.abs(z - 1.30) < 0.003))
    out[pocket_edge] = SHIRT * 0.75
    cuff = shirt & (np.abs(x) > HAND_X - 0.07) & (np.abs(x) < HAND_X - 0.01)
    out[cuff] = SHIRT * 0.8
    knee = pants & (z > 0.43) & (z < 0.57) & (y < 0.02)
    out[knee] = PANTS * 0.78
    hem = pants & (z < 0.14)
    out[hem] = PANTS * 0.85

    head = skin & (z > 1.5)
    hair = head & ((z > top - 0.07) | ((y > 0.02) & (z > EYE_Z - 0.02)) | ((z > EYE_Z + 0.052) & (y > nose[1] + 0.025)) | ((z > EYE_Z + 0.06)))
    ears = (np.abs(x) > 0.068) & (z < EYE_Z + 0.03) & (z > EYE_Z - 0.045) & (y < 0.07)
    hair = hair & ~ears
    out[hair] = HAIR * (0.9 + 0.2 * rng.random((hair.sum(), 1)))
    stub = np.clip((MOUTH_Z + 0.006 - z) / 0.018, 0, 1)[:, None] * 0.16
    stubble = head & ~hair & (z > nose[2] - 0.09)
    out[stubble] = out[stubble] * (1 - stub[stubble]) + HAIR * stub[stubble]
    face = head & (y < FRONT_Y)
    for ex in (EYE_X, -EYE_X):
        dx = (x - ex) / 0.0135
        dz = (z - EYE_Z) / 0.0072
        r = dx * dx + dz * dz
        white = face & (r < 1.0)
        out[white] = np.array([0.92, 0.9, 0.86])
        iris = face & ((x - ex) ** 2 + (z - EYE_Z) ** 2 < 0.0058 ** 2)
        out[iris] = np.array([0.30, 0.20, 0.12])
        pupil = face & ((x - ex) ** 2 + (z - EYE_Z) ** 2 < 0.0026 ** 2)
        out[pupil] = np.array([0.04, 0.03, 0.03])
        lid = face & (r >= 0.75) & (r < 1.35) & (z > EYE_Z)
        out[lid] = SKIN * 0.55
        bx = x - ex
        arch = BROW_Z + 0.004 * (1 - (bx / 0.02) ** 2)
        brow = face & (np.abs(bx) < 0.02) & (np.abs(z - arch) < 0.0035 + 0.001 * (1 - np.abs(bx) / 0.02))
        out[brow] = HAIR * 1.1
    lip_h = 0.0045 * np.clip(1 - (x / 0.023) ** 2, 0, 1)
    lips = face & (np.abs(x) < 0.023) & (np.abs(z - MOUTH_Z) < lip_h)
    out[lips] = np.array([0.62, 0.36, 0.32])
    line = face & (np.abs(x) < 0.02) & (np.abs(z - MOUTH_Z) < 0.0011)
    out[line] = np.array([0.30, 0.15, 0.13])
    nostril = face & (np.abs(np.abs(x) - 0.009) < 0.004) & (np.abs(z - (nose[2] - 0.006)) < 0.0028)
    out[nostril] = SKIN * 0.7

    grain = 1.0 + rng.normal(0, 0.025, (len(P), 1))
    out = out * np.where((cloth | boots)[:, None], grain, 1.0 + rng.normal(0, 0.01, (len(P), 1)))
    return np.clip(out, 0, 1)

def paint_texture():
    img = np.zeros((TEX, TEX, 4), np.float32)
    onehot = np.eye(3)[vc]
    for tri in mesh.loop_triangles:
        loops = list(tri.loops)
        vis = [mesh.loops[l].vertex_index for l in loops]
        uvs = np.array([[uv_layer[l].uv.x * TEX, (uv_layer[l].uv.y + 1.0) * TEX] for l in loops])
        x0, y0 = np.floor(uvs.min(0)).astype(int) - 1
        x1, y1 = np.ceil(uvs.max(0)).astype(int) + 1
        x0, y0 = max(0, x0), max(0, y0)
        x1, y1 = min(TEX - 1, x1), min(TEX - 1, y1)
        if x1 < x0 or y1 < y0:
            continue
        xs, ys = np.meshgrid(np.arange(x0, x1 + 1), np.arange(y0, y1 + 1))
        px = xs.ravel() + 0.5
        py = ys.ravel() + 0.5
        a, b, c = uvs
        d = (b[1] - c[1]) * (a[0] - c[0]) + (c[0] - b[0]) * (a[1] - c[1])
        if abs(d) < 1e-9:
            continue
        w0 = ((b[1] - c[1]) * (px - c[0]) + (c[0] - b[0]) * (py - c[1])) / d
        w1 = ((c[1] - a[1]) * (px - c[0]) + (a[0] - c[0]) * (py - c[1])) / d
        w2 = 1 - w0 - w1
        eps = -0.02
        inside = (w0 >= eps) & (w1 >= eps) & (w2 >= eps)
        if not inside.any():
            continue
        bw = np.stack([w0[inside], w1[inside], w2[inside]], 1)
        bw = np.clip(bw, 0, None)
        bw /= bw.sum(1, keepdims=True)
        P = bw @ Nv[vis]
        W = bw @ onehot[vis]
        col = shade(P, W)
        yy = ys.ravel()[inside]
        xx = xs.ravel()[inside]
        img[yy, xx, :3] = col
        img[yy, xx, 3] = 1.0
    for _ in range(6):
        empty = img[:, :, 3] == 0
        if not empty.any():
            break
        acc = np.zeros_like(img)
        cnt = np.zeros((TEX, TEX, 1), np.float32)
        for dy, dx in ((1, 0), (-1, 0), (0, 1), (0, -1)):
            sh = np.roll(np.roll(img, dy, 0), dx, 1)
            m = sh[:, :, 3:4]
            acc += sh * m
            cnt += m
        fill = empty & (cnt[:, :, 0] > 0)
        img[fill] = acc[fill] / cnt[fill]
        img[fill, 3] = 1.0
    img[:, :, 3] = 1.0
    image = bpy.data.images.new("MechanicTex", TEX, TEX, alpha=False)
    image.pixels.foreach_set(img.ravel())
    tex_path = os.path.splitext(out_path)[0] + "_tex.png"
    image.filepath_raw = tex_path
    image.file_format = 'PNG'
    image.save()
    return image, tex_path

tex_image, tex_path = paint_texture()
print("texture", tex_path, os.path.getsize(tex_path))

out_verts = []
remap = {}
subs = {0: [], 1: [], 2: []}
for tri in mesh.loop_triangles:
    idx = []
    for li in tri.loops:
        vi = mesh.loops[li].vertex_index
        uv = tuple(round(c, 5) for c in uv_layer[li].uv) if uv_layer else (0.0, 0.0)
        k2 = (vi, uv)
        if k2 not in remap:
            remap[k2] = len(out_verts)
            out_verts.append((vi, uv))
        idx.append(remap[k2])
    cats = [vcat[mesh.loops[li].vertex_index] for li in tri.loops]
    c = max(set(cats), key=cats.count)
    subs[1].extend([idx[0], idx[2], idx[1]])

def w7(f, n):
    while True:
        b = n & 0x7F
        n >>= 7
        if n:
            f.write(bytes([b | 0x80]))
        else:
            f.write(bytes([b]))
            break
def wstr(f, txt):
    data = txt.encode("utf-8")
    w7(f, len(data))
    f.write(data)

with open(out_path, "wb") as f:
    f.write(b"CMSM")
    f.write(struct.pack("<i", 2))
    f.write(struct.pack("<i", len(bones)))
    for i, b in enumerate(bones):
        wstr(f, key(b.name))
        f.write(struct.pack("<i", index[b.parent.name] if b.parent else -1))
        lp, lr = rest_local[i]
        f.write(struct.pack("<3f", *lp))
        f.write(struct.pack("<4f", *lr))
    f.write(struct.pack("<i", index[hips_name]))
    f.write(struct.pack("<i", len(out_verts)))
    for vi, uv in out_verts:
        f.write(struct.pack("<3f", *to_u_pos(verts_world[vi])))
    for vi, uv in out_verts:
        f.write(struct.pack("<3f", *to_u_dir(normals_world[vi])))
    for vi, uv in out_verts:
        f.write(struct.pack("<2f", *uv))
    for vi, uv in out_verts:
        ws = vweights[vi]
        f.write(struct.pack("<4i", *[i for i, _ in ws]))
        f.write(struct.pack("<4f", *[w for _, w in ws]))
    used = [c for c in (0, 1, 2) if subs[c]]
    f.write(struct.pack("<i", len(used)))
    for c in used:
        f.write(struct.pack("<4f", 1.0, 1.0, 1.0, 1.0))
        f.write(struct.pack("<i", len(subs[c])))
        f.write(struct.pack("<%di" % len(subs[c]), *subs[c]))
    f.write(struct.pack("<i", len(clips)))
    for name, count, out_frames, _ in clips:
        wstr(f, name)
        f.write(struct.pack("<f", FPS))
        f.write(struct.pack("<i", count))
        for hp, rots in out_frames:
            f.write(struct.pack("<3f", *hp))
            for r in rots:
                f.write(struct.pack("<4f", *r))
    png = open(tex_path, "rb").read()
    f.write(struct.pack("<i", len(png)))
    f.write(png)
print("wrote", out_path, os.path.getsize(out_path), "bytes,", len(out_verts), "verts,", {c: len(subs[c]) // 3 for c in subs}, "tris")

if preview_dir:
    os.makedirs(preview_dir, exist_ok=True)
    mat = bpy.data.materials.new("tex")
    mat.use_nodes = True
    nt = mat.node_tree
    tn = nt.nodes.new("ShaderNodeTexImage")
    tn.image = tex_image
    bsdf = nt.nodes.get("Principled BSDF")
    nt.links.new(tn.outputs["Color"], bsdf.inputs["Base Color"])
    nt.nodes.active = tn
    M.data.materials.clear()
    M.data.materials.append(mat)
    T.data.pose_position = 'POSE'
    for o in bpy.data.objects:
        if o.type == 'ARMATURE' and o != T:
            o.hide_render = True
    scene.render.engine = 'BLENDER_WORKBENCH'
    scene.display.shading.color_type = 'TEXTURE'
    scene.render.resolution_x = 360
    scene.render.resolution_y = 480
    cam_data = bpy.data.cameras.new("cam")
    cam_data.clip_start = height * 0.01
    cam_data.clip_end = height * 100.0
    cam = bpy.data.objects.new("cam", cam_data)
    scene.collection.objects.link(cam)
    scene.camera = cam
    center = Vector((hx, hy, minz + height * 0.5))
    T.animation_data_clear()

    def apply_world(world):
        for b in bones:
            pb = T.pose.bones[b.name]
            P, R = world[b.name]
            sc = (T.matrix_world @ b.matrix_local).to_scale()
            pb.matrix = T.matrix_world.inverted() @ Matrix.LocRotScale(P, R, sc)
            bpy.context.view_layer.update()

    def shoot(name, world, side):
        apply_world(world)
        d = Vector((-1.0, -2.2, 0.15)) if side else Vector((0.0, -2.6, 0.1))
        cam.location = center + d * height
        cam.rotation_euler = (center - cam.location).to_track_quat('-Z', 'Y').to_euler()
        scene.render.filepath = os.path.join(preview_dir, name + ".png")
        bpy.ops.render.render(write_still=True)

    shoot("rest", rest_world_pose, False)
    face_c = origin + Vector((nose[0], nose[1], EYE_Z - 0.02)) / s
    def shoot_face(name, world, yaw):
        apply_world(world)
        d = Vector((math.sin(yaw) * 0.42, -math.cos(yaw) * 0.42, 0.02)) / s
        cam.location = face_c + d
        cam.rotation_euler = (face_c - cam.location).to_track_quat('-Z', 'Y').to_euler()
        scene.render.filepath = os.path.join(preview_dir, name + ".png")
        bpy.ops.render.render(write_still=True)
    shoot_face("face_front", rest_world_pose, 0.0)
    shoot_face("face_side", rest_world_pose, 0.7)
    for name, count, out_frames, frames in clips:
        if name == "Walk":
            shoot("walk_a", frames[0], True)
            shoot("walk_b", frames[count // 4], True)
            shoot("walk_c", frames[count // 2], True)
        if name == "Idle":
            shoot("idle", frames[count // 2], False)
    print("previews done")
