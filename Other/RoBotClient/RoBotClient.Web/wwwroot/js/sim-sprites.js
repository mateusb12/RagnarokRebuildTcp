// Simulation tab sprites (visual only): monsters and the bot drawn and posed as the Unity client does it, from what the
// replay recorded. Nothing here changes what the replay says; sim.js asks it for layers to draw.
//  - Monsters: the imported sprite actions (/monster-anim, as the minimap). A swing starts the server's attack motion time
//    before its damage lands and is sped up so its attack frame connects then (ServerControllable.SetAttackAnimationSpeed);
//    a hit taken flinches; dead plays once and fades.
//  - The bot: imported body + head (hair colour), raw .spr/.act headgear, shield, weapon and swing trail (SimSprites.cs),
//    attached like RoSpriteAnimator children: same action and frame as the body, offset by the frames' "Pos" anchors, sorted
//    by SpriteOrder (behind the body on .imf foreground frames). Attack1/2/3 from the job + weapon class; skills pose
//    per the client's skill handlers; cast bars hold the cast pose.

// Client motion layout (RoAnimationHelper.GetMotionIdForSprite): action = motion + facing.
export const MON = { idle: 0, walk: 8, attack: 16, hit: 24, dead: 32 };
export const PLR = { idle: 0, walk: 8, sit: 16, standby: 32, throw: 40, hit: 48, dead: 64, attack2: 80, attack1: 88, casting: 96 };

/** RO Direction (South 0, SouthWest 1 ... SouthEast 7) from a world offset, Y up. */
export function facing(dx, dy) { if (!dx && !dy) return 0; return [6, 5, 4, 3, 2, 1, 0, 7][((Math.round(Math.atan2(dy, dx) / (Math.PI / 4)) % 8) + 8) % 8]; }

export function drawLayers(g, layers) {
    g.imageSmoothingEnabled = true;
    for (const { img, r, left, top, w, h, mirror, alpha, angle } of layers) {
        if (alpha <= 0 || !img) continue;
        g.save(); g.globalAlpha = Math.min(1, alpha);
        if (angle) { g.translate(left + w / 2, top + h / 2); g.rotate(angle * Math.PI / 180); g.translate(-(left + w / 2), -(top + h / 2)); }
        if (mirror) { g.translate(left * 2 + w, 0); g.scale(-1, 1); }
        g.drawImage(img, r[0], r[1], r[2], r[3], left, top, w, h);
        g.restore();
    }
}

const loadImg = src => new Promise(ok => { const i = new Image(); i.onload = () => ok(i); i.onerror = () => ok(null); i.src = src; });

// ---- monsters ----
const anims = new Map();
/** Imported monster actions + atlas for a species; null while loading or with no sprite (sim.js then draws a dot). */
export function animOf(name) {
    if (!anims.has(name)) {
        anims.set(name, null);
        fetch("monster-anim/" + encodeURIComponent(name)).then(r => r.ok ? r.json() : null)
            .then(async a => { if (a) { const img = await loadImg(a.atlas); if (img) anims.set(name, { ...a, img }); } }, () => {});
    }
    return anims.get(name);
}

/** Per monster: flinches (hits landed on it) and swings ({t0: swing start, speed}) from the replay. */
export function indexMonsters(rp) {
    const hits = new Map(), swings = new Map(), push = (m, id, v) => { let a = m.get(id); if (!a) m.set(id, a = []); a.push(v); };
    for (const e of rp.events) {
        if (e.kind === "hit" && e.mob) push(hits, e.mob, e.t);
        else if (e.kind === "hurt" && e.mob) push(swings, e.mob, { t0: e.t - (e.detail?.motion || 0), motion: e.detail?.motion || 0 });
    }
    for (const l of swings.values()) l.sort((a, b) => a.t0 - b.t0);
    return { hits, swings };
}

const lastLE = (arr, t, key = x => x) => { let lo = 0, hi = arr.length - 1, r = -1; while (lo <= hi) { const m = (lo + hi) >> 1; if (key(arr[m]) <= t) { r = m; lo = m + 1; } else hi = m - 1; } return r; };
/** Frame i of an action played once from age (ms) at speed (delay multiplier); null once it's over unless hold. */
function once(act, age, speed, hold) {
    if (!act?.frames.length || !(age >= 0) || !isFinite(age)) return null;
    const i = Math.floor(age / Math.max(1, act.delay * speed));
    return i < act.frames.length ? i : hold ? act.frames.length - 1 : null;
}
const loopFrame = (act, ms) => act?.frames.length ? Math.floor(ms / Math.max(1, act.delay)) % act.frames.length : 0;

/** Layers for a monster at t. The simulator has no monster facing: it faces the bot while fighting it (a swing or flinch
 *  within 3 s), else south. */
export function monsterLayers(ix, a, id, m, t, me, s, spriteH) {
    const flinchAt = (() => { const l = ix.hits.get(id); const i = l ? lastLE(l, t) : -1; return i >= 0 ? l[i] : -Infinity; })();
    const sw = (() => { const l = ix.swings.get(id); const i = l ? lastLE(l, t, x => x.t0) : -1; return i >= 0 ? l[i] : null; })();
    const dir = (sw && t - sw.t0 < 3) || t - flinchAt < 3 || !m.alive ? facing(me.x - m.x, me.y - m.y) : 0;
    const act = motion => a.actions[motion + dir] || a.actions[motion];
    let frame = null, alpha = 1;
    if (!m.alive) { const age = (t - m.diedAt) * 1000, i = once(act(MON.dead), age, 1, true); frame = i != null ? act(MON.dead).frames[i] : null; alpha = Math.max(0, Math.min(1, (1900 - age) / 700)); }
    else {
        // ServerControllable.SetAttackAnimationSpeed: AnimSpeed = motionTime / (AttackFrameTime / 1000), so the attack frame lands with the damage.
        const speed = sw && sw.motion > 0 && a.attackFrameTime > 0 ? sw.motion * 1000 / a.attackFrameTime : 1;
        const ia = sw ? once(act(MON.attack), (t - sw.t0) * 1000, speed) : null, ih = once(act(MON.hit), (t - flinchAt) * 1000, 1);
        const atkNewer = sw && sw.t0 >= flinchAt;
        frame = atkNewer ? (ia != null ? act(MON.attack).frames[ia] : ih != null ? act(MON.hit).frames[ih] : null)
                         : (ih != null ? act(MON.hit).frames[ih] : ia != null ? act(MON.attack).frames[ia] : null);
    }
    if (!frame) { const ac = act(MON.idle); if (!ac?.frames.length) return null; frame = ac.frames[loopFrame(ac, t * 1000)]; }
    const r0 = a.rects[0], k = spriteH / Math.max(r0[2], r0[3], 1);
    const layers = place(a, frame, 0, 0, s.x, s.y + 2, k, alpha);
    return layers.length ? { layers, box: box(layers) } : null;
}

function place(spr, frame, ox, oy, x, feet, k, alpha) {
    return frame.filter(l => l[0] >= 0 && spr.rects[l[0]]).map(([i, lx, ly, mirror, scx, scy, al, angle]) => {
        const r = spr.rects[i], w = r[2] * Math.abs(scx) * k, h = r[3] * Math.abs(scy) * k;
        return { img: spr.img, r, left: x + (lx + ox) * k - w / 2, top: feet + (ly + oy) * k - h / 2, w, h, mirror: !!mirror !== scx < 0, alpha: al * alpha, angle: angle || 0 };
    });
}
const box = ls => [Math.min(...ls.map(l => l.left)), Math.min(...ls.map(l => l.top)), Math.max(...ls.map(l => l.left + l.w)), Math.max(...ls.map(l => l.top + l.h))];

// ---- raw .spr / .act (port of the client's importer: Editor/RagnarokSpriteLoader.cs, RagnarokAnimLoader.cs) ----
function decodeSpr(buf) {
    const d = new DataView(buf), u8 = new Uint8Array(buf);
    const ver = d.getUint8(3) * 10 + d.getUint8(2);
    const indexCount = d.getUint16(4, true), rgbaCount = ver > 11 ? d.getUint16(6, true) : 0;
    let p = ver > 11 ? 8 : 6;
    const frames = [];
    for (let i = 0; i < indexCount; i++) {
        const w = d.getUint16(p, true), h = d.getUint16(p + 2, true); p += 4;
        const data = new Uint8Array(w * h);
        if (ver < 21) { data.set(u8.subarray(p, p + w * h)); p += w * h; }
        else {
            const end = d.getUint16(p, true) + p + 2; p += 2;
            let o = 0;
            while (p < end) {
                const c = u8[p++]; data[o++] = c;
                if (c === 0) { const n = u8[p++]; if (n === 0) data[o++] = n; else for (let j = 1; j < n; j++) data[o++] = c; }
            }
        }
        frames.push({ w, h, data, indexed: true });
    }
    for (let i = 0; i < rgbaCount; i++) {
        const w = d.getInt16(p, true), h = d.getInt16(p + 2, true); p += 4;
        frames.push({ w, h, data: u8.subarray(p, p + w * h * 4), indexed: false }); p += w * h * 4;
    }
    const pal = ver > 10 ? u8.subarray(buf.byteLength - 1024) : null;
    return { frames, indexCount, pal };
}

function decodeAct(buf, indexCount) {
    const d = new DataView(buf); let p = 2;
    const ver = d.getUint8(3) * 10 + d.getUint8(2); p = 4;
    const i32 = () => { const v = d.getInt32(p, true); p += 4; return v; }, f32 = () => { const v = d.getFloat32(p, true); p += 4; return v; };
    const count = d.getUint16(p, true); p += 2 + 10;
    const actions = [];
    for (let a = 0; a < count; a++) {
        const act = { delay: 150, frames: [], anchors: [], sounds: [] };
        const fc = d.getUint32(p, true); p += 4;
        for (let f = 0; f < fc; f++) {
            p += 32;
            const lc = d.getUint32(p, true); p += 4;
            const layers = [];
            for (let l = 0; l < lc; l++) {
                const x = i32(), y = i32(); let index = i32(); const mirror = i32() !== 0 ? 1 : 0;
                let alpha = 1, sx = 1, sy = 1, angle = 0;
                if (ver >= 20) {
                    alpha = d.getUint8(p + 3) / 255; p += 4;
                    sx = f32(); sy = ver > 23 ? f32() : sx; angle = i32();
                    if (i32() === 1) index += indexCount;
                    if (ver >= 25) p += 8;
                }
                layers.push([index, x, y, mirror, sx, sy, alpha, angle]);
            }
            act.sounds.push(ver >= 20 ? i32() : -1);
            let anchor = [0, 0];
            if (ver >= 23) { const pc = i32(); for (let k = 0; k < pc; k++) { i32(); const ax = i32(), ay = i32(); i32(); if (k === 0) anchor = [ax, ay]; } }
            act.frames.push(layers); act.anchors.push(anchor);
        }
        actions.push(act);
    }
    if (ver >= 21) { const n = i32(); p += 40 * n; }
    if (ver >= 22) for (const act of actions) act.delay = Math.trunc(f32() * 24);
    return actions;
}

/** A raw sprite as {img (an atlas canvas), rects, actions} - the imported format. */
async function loadRaw(url) {
    const get = u => fetch(u).then(r => r.ok ? r.arrayBuffer() : null, () => null);
    const [sb, ab] = await Promise.all([get(url + ".spr"), get(url + ".act")]);
    if (!sb || !ab) return null;
    const spr = decodeSpr(sb), actions = decodeAct(ab, spr.indexCount);
    const W = 1024, rects = []; let x = 0, y = 0, rowH = 0;
    for (const f of spr.frames) { if (x + f.w > W) { x = 0; y += rowH; rowH = 0; } rects.push([x, y, f.w, f.h]); x += f.w; rowH = Math.max(rowH, f.h); }
    const c = document.createElement("canvas"); c.width = W; c.height = Math.max(1, y + rowH);
    const g = c.getContext("2d");
    spr.frames.forEach((f, i) => {
        if (!f.w || !f.h) return;
        const im = g.createImageData(f.w, f.h), o = im.data;
        for (let yy = 0; yy < f.h; yy++) for (let xx = 0; xx < f.w; xx++) {
            const q = (xx + yy * f.w) * 4;
            if (f.indexed) { const ci = f.data[xx + yy * f.w]; if (ci && spr.pal) { o[q] = spr.pal[ci * 4]; o[q + 1] = spr.pal[ci * 4 + 1]; o[q + 2] = spr.pal[ci * 4 + 2]; o[q + 3] = 255; } }
            else { const s = (xx + (f.h - yy - 1) * f.w) * 4; o[q] = f.data[s + 3]; o[q + 1] = f.data[s + 2]; o[q + 2] = f.data[s + 1]; o[q + 3] = f.data[s]; } // RGBA frames are stored bottom-up, ABGR
        }
        g.putImageData(im, rects[i][0], rects[i][1]);
    });
    return { img: c, rects, actions };
}

// ---- the bot ----
/** spec: SimSprites.Player (body, head, parts, attackMotion, skills, look). Resolves to the drawable player or null. */
export async function loadPlayer(spec) {
    if (!spec?.body || !spec?.head) return null;
    const [bi, hi] = await Promise.all([loadImg(spec.body.atlas), loadImg(spec.head.atlas)]);
    if (!bi || !hi) return null;
    const parts = (await Promise.all(spec.parts.map(async p => { const s = await loadRaw(p.url); return s && { ...s, order: p.order, what: p.what }; }))).filter(Boolean);
    return {
        body: { ...spec.body, img: bi }, children: [{ ...spec.head, img: hi, order: 1, what: "head" }, ...parts],
        attack: spec.attackMotion === 2 ? PLR.attack2 : PLR.attack1, skills: spec.skills || {}, look: spec.look,
        parts: spec.parts.map(p => p.what + (parts.some(q => q.what === p.what) ? "" : " (failed to load)")),
    };
}

// Player attack speed (PacketAttack + ServerControllable.SetAttackAnimationSpeed): motionTime padded by 10% (at most +0.1 s),
// capped at 1.5 s, against the 0.6 s a player sprite's attack is drawn for.
const playerSpeed = m => m > 0 ? Math.min(1.5, Math.min(m * 1.1, m + 0.1)) / 0.6 : 1;

/** The bot's one-shot motions from the replay, by start time: swings (auto-attack hits started motion seconds before
 *  they landed; skills on execution, per the client's handler), cast bars (held), flinches, deaths. */
export function indexPlayer(rp, P) {
    const out = [], ev = rp.events, how = s => P.skills[s] || P.skills["*"] || { execute: "attack", cast: "standby" };
    for (let i = 0; i < ev.length; i++) {
        const e = ev[i];
        if (e.kind === "hit" && e.mob && !e.skill) out.push({ t0: e.t - (e.detail?.motion || 0), motion: P.attack, speed: playerSpeed(e.detail?.motion) });
        else if (e.kind === "executed" && e.skill) {
            const h = how(e.skill).execute;
            if (h === "attack") { const hit = ev.slice(i + 1, i + 40).find(x => x.kind === "hit" && x.skill === e.skill && x.t - e.t < 3); out.push({ t0: e.t, motion: P.attack, speed: playerSpeed(hit?.detail?.motion || 0.6) }); }
            else if (h === "skill") out.push({ t0: e.t, motion: PLR.casting, speed: 1 });
            else if (h === "throw") out.push({ t0: e.t, motion: PLR.throw, speed: 1 });
        }
        else if (e.kind === "castbar" && e.skill) {
            const c = how(e.skill).cast, end = ev.slice(i + 1).find(x => x.t > e.t && ["executed", "failed", "rejected", "death"].includes(x.kind));
            out.push({ t0: e.t, until: Math.min(e.t + (e.amount || 0) / 1000, end ? end.t : Infinity), motion: c === "attack" ? P.attack : c === "standby" ? PLR.standby : PLR.casting, hold: c !== "standby" });
        }
        else if (e.kind === "hurt" && e.amount > 0) out.push({ t0: e.t, motion: PLR.hit, speed: 1 });
        else if (e.kind === "death") out.push({ t0: e.t, until: e.t + 2, motion: PLR.dead, speed: 1, hold: true, dead: true });
    }
    return out.sort((a, b) => a.t0 - b.t0);
}

/** The bot's layers at t (b: its screen point). Facing: toward the target while fighting, else its last step. */
export function playerLayers(P, moves, rp, t, me, b, act, tm, spriteH) {
    const tr = rp.track; let dir = 0;
    const moving = me.x !== me.cx || me.y !== me.cy;
    if (act === "fight" && tm) dir = facing(tm.x - me.x, tm.y - me.y);
    else if (moving) dir = facing(me.x - me.cx, me.y - me.cy);
    else for (let i = lastLE(tr, t, s => s.t); i > 0; i--) if (tr[i].x !== tr[i - 1].x || tr[i].y !== tr[i - 1].y) { dir = facing(tr[i].x - tr[i - 1].x, tr[i].y - tr[i - 1].y); break; }
    const A = P.body.actions, action = m => A[m + dir] || A[m];
    let motion = null, fi = 0;
    // The newest one-shot motion still playing wins (a cast pose is frozen on its first frame, a death holds its last).
    for (let i = lastLE(moves, t, m => m.t0), n = 0; i >= 0 && n < 12; i--, n++) {
        const m = moves[i], age = (t - m.t0) * 1000;
        if (m.until != null && t >= m.until) continue;
        if (m.until != null && m.hold && !m.dead) { motion = m.motion; fi = 0; break; }
        const f = once(action(m.motion), age, m.speed || 1, m.hold);
        if (f != null) { motion = m.motion; fi = f; break; }
    }
    if (motion == null) {
        // Idle and sit hold the head-facing frame (0: straight ahead); walk and battle stance loop.
        motion = moving ? PLR.walk : act === "rest" ? PLR.sit : act === "fight" ? PLR.standby : PLR.idle;
        fi = motion === PLR.walk || motion === PLR.standby ? loopFrame(action(motion), t * 1000) : 0;
    }
    const ba = action(motion); if (!ba?.frames.length) return null;
    fi = Math.min(fi, ba.frames.length - 1);
    const r0 = P.body.rects[0] || [0, 0, 1, 1], k = spriteH * 1.1 / Math.max(r0[3], 1), feet = b.y + 2;
    const fg = ba.foreground?.[fi], anchor = ba.anchors?.[fi] || [0, 0];
    const layers = [{ order: 0, ls: place(P.body, ba.frames[fi], 0, 0, b.x, feet, k, 1) }];
    for (const c of P.children) {
        // RoSpriteAnimator.ChildSetFrameData / ChildUpdate: the body's action (wrapped) and frame; offset body anchor - own anchor.
        let ai = motion + dir; if (ai > c.actions.length - 1) ai %= c.actions.length;
        const ca = c.actions[ai]; if (!ca?.frames.length) continue;
        const cf = Math.min(fi, ca.frames.length - 1), own = ca.anchors?.[cf] || [0, 0];
        const [ox, oy] = own[0] || own[1] ? [anchor[0] - own[0], anchor[1] - own[1]] : [0, 0];
        layers.push({ order: fg ? c.order - 10 : c.order, ls: place(c, ca.frames[cf], ox, oy, b.x, feet, k, 1) });
    }
    return layers.sort((x, y) => x.order - y.order).flatMap(x => x.ls);
}


// Client damage numbers: DamageIndicator.cs with the curves of Data/FlyingText/{Damage,Miss}Path.asset as [time, value, inSlope, outSlope].
// ponytail: Unity's weighted tangents are evaluated as plain Hermite; error is under a few percent of the size curve.
const DMG_ALPHA = [[0, 1, 0, 0], [0.11453, 0.99997, 0, -0.07507], [0.5606, 0.70586, -1.30471, -1.45443], [1.00429, 0.00602, -0.20067, -0.20067]];
const PATHS = {
    damage: { traj: [[0.00199, -0.00142, 4.0522, 4.0522], [0.99421, 0.01113, -3.90422, -3.90422]], size: [[-0.00092, 5.01931, -14.13749, -14.13749], [0.39646, 0.99757, -0.60128, -0.32725], [1, 0.80007, -0.32725, -0.32725]], alpha: DMG_ALPHA, fly: true, height: 4, dist: 4, tween: 1, scale: 1, start: 0 },
    miss: { traj: [[-0.00126, -0.00134, 1.00567, 1.00567], [1.00094, 1.00654, 1.00567, 1.00567]], size: [[0, 1, 0, 0], [1, 1, 0, 0]], alpha: [[0, 1, 0.00945, 0.00945], [0.49994, 1.00472, 0.00945, -0.42355], [1.00241, -0.00126, -4.67463, -4.67463]], fly: false, height: 5, dist: 4, tween: 1.5, scale: 2, start: 0.6 },
};
export const INDICATOR_SECONDS = 1.5;
const DAMAGE_NUMBER_SIZE = 0.85, DIGIT = 0.5, SPACING = 0.7; // GameConfigData defaults; shader digit quad 0.5 units, advance = 0.5 * spacing
function curve(k, t) {
    if (t <= k[0][0]) return k[0][1];
    for (let i = 1; i < k.length; i++) if (t <= k[i][0]) {
        const [t0, v0, , m0] = k[i - 1], [t1, v1, m1] = k[i], d = t1 - t0, s = (t - t0) / d, s2 = s * s, s3 = s2 * s;
        return (2 * s3 - 3 * s2 + 1) * v0 + (s3 - 2 * s2 + s) * d * m0 + (3 * s2 - 2 * s3) * v1 + (s3 - s2) * d * m1;
    }
    return k[k.length - 1][1];
}
// Map-space unit vector the number flies along: away from the attacker, snapped like Direction.GetIntercardinalDirection (cardinals turn 45° clockwise).
export function flyAway(victim, attacker) {
    if (!attacker) return [0, 0];
    const a = Math.round(Math.atan2(attacker.y - victim.y, attacker.x - victim.x) / (Math.PI / 4));
    const r = (a % 2 === 0 ? a - 1 : a) * Math.PI / 4; // facing toward the attacker, made diagonal
    return [-Math.sign(Math.round(Math.cos(r) * 1e6)), -Math.sign(Math.round(Math.sin(r) * 1e6))];
}
// kind "damage" | "miss"; (x, y) victim feet on screen; (dx, dy) screen-space flight vector per world unit; u = px per world unit.
export function drawIndicator(g, kind, txt, color, age, x, y, dx, dy, u) {
    const P = PATHS[kind], lt = age / P.tween;
    if (lt < 0 || lt > 1) return;
    const size = curve(P.size, lt) * DAMAGE_NUMBER_SIZE * P.scale, fly = P.fly ? lt * P.dist : 0;
    const px = x + dx * fly * u, py = y + dy * fly * u - (P.start + 1.25 + curve(P.traj, lt) * P.height) * u;
    const h = Math.max(9, DIGIT * size * u), adv = kind === "miss" ? null : h * SPACING;
    g.save(); g.globalAlpha = Math.max(0, Math.min(1, curve(P.alpha, lt)));
    g.font = "900 " + h.toFixed(1) + "px Arial Black, Impact, sans-serif"; g.textBaseline = "middle"; g.lineJoin = "round";
    g.lineWidth = Math.max(2, h / 7); g.strokeStyle = "#000"; g.fillStyle = color;
    const chars = adv ? [...txt] : [txt], w = adv ? adv * chars.length : g.measureText(txt).width;
    chars.forEach((c, i) => { const cx = px - w / 2 + (adv ? i * adv + (adv - g.measureText(c).width) / 2 : 0); g.strokeText(c, cx, py); g.fillText(c, cx, py); });
    g.restore();
}
