// Simulation tab: replays a recorded offline hunt (HuntReplay JSON from "huntsim --replay"). Everything drawn is read from
// the recording: the player's steps, the server's hits and regen ticks, spawns, knockbacks, deaths and the decision
// events with what each saw. Nothing is re-simulated or re-decided here; between two recorded steps the player is
// drawn gliding across the cell it is stepping into (a step takes 0.15 s, 0.21 s diagonal).
// World Y increases UPWARD (as map.js): screenY = (cy - y) * zoom + height / 2.

const Sprites = await import("./sim-sprites.js" + new URL(import.meta.url).search); // visual-only sprite code
const { animOf, drawLayers, dpr, font, lastLE } = Sprites;
const Effects = await import("./sim-effects.js" + new URL(import.meta.url).search); // the client's STR skill effects

const COLORS = {
    bg: "#10131a", walkable: "#3a4254", blocked: "#191c24", self: "#3b9dff", monster: "#e5484d", target: "#ffe066",
    route: "#ffe066", view: "rgba(203,210,220,0.18)", text: "#cbd2dc", dim: "#7c8493", area: "#c77dff",
};
const ACTIVITY = {
    wander: { label: "wandering", color: "#8e7cc3" }, approach: { label: "walking to target", color: "#ffe066" },
    fight: { label: "fighting", color: "#e5484d" }, rest: { label: "resting", color: "#3b9dff" },
    idle: { label: "wedged / idle", color: "#6c757d" }, flee: { label: "fleeing", color: "#ff8c42" }, loot: { label: "looting", color: "#f6c344" }, none: { label: "deciding", color: "#2b303b" },
};
const CATEGORY = {
    decision: ["pick", "nopick", "wander", "idle", "cast-decision", "attack-decision", "sit", "stand", "flee", "flee-move", "flee-end", "threat"],
    combat: ["engage", "kill", "death", "fight-end", "hit", "hurt", "executed", "rejected", "queued", "no-effect", "castbar", "reach", "abandon", "knockback", "aggro", "ai", "monster-cast"],
    recovery: ["regen", "sit-refused", "stand-refused"],
    world: ["spawn", "area-start", "area-end", "area-status", "world", "sighting", "route", "start", "end", "note"],
    problem: ["failed"],
    items: ["drop", "loot", "loot-skip", "use", "use-refused", "use-skip", "teleport"],
};
const KNOWN = new Set(Object.values(CATEGORY).flat());
const MINOR = new Set(["hit", "hurt", "regen", "spawn", "attack-decision", "route", "area-status", "world", "castbar", "reach"]);
const MAJOR = new Set(["pick", "nopick", "wander", "idle", "engage", "kill", "death", "sit", "stand", "rejected", "no-effect", "failed", "fight-end", "sit-refused", "abandon", "aggro", "flee", "flee-end", "threat", "loot", "use", "teleport"]);
const BADGE = {
    pick: "text-bg-success", nopick: "text-bg-warning", wander: "text-bg-secondary", idle: "text-bg-secondary", kill: "text-bg-success",
    death: "text-bg-danger", failed: "text-bg-danger", rejected: "text-bg-warning", "no-effect": "text-bg-warning", sit: "text-bg-info", stand: "text-bg-info",
    "cast-decision": "text-bg-primary", executed: "text-bg-primary", engage: "text-bg-light", hurt: "text-bg-danger", hit: "text-bg-dark",
    loot: "text-bg-warning", drop: "text-bg-light", "loot-skip": "text-bg-secondary", use: "text-bg-info", "use-refused": "text-bg-secondary", "use-skip": "text-bg-secondary", teleport: "text-bg-primary", aggro: "text-bg-danger", "monster-cast": "text-bg-danger", flee: "text-bg-warning", "flee-move": "text-bg-warning", threat: "text-bg-warning", "flee-end": "text-bg-secondary",
};

const esc = s => String(s ?? "").replace(/[&<>"]/g, c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;" })[c]);
const fmtT = t => { const m = Math.floor(t / 60), s = t - 60 * m; return m + ":" + (s < 10 ? "0" : "") + s.toFixed(2); };
const pct = v => (100 * v).toFixed(0) + "%";
const n1 = v => v == null ? "—" : (+v).toFixed(1);

let current = null;

const itemIcon = code => "icons/" + encodeURIComponent(code || "") + ".png"; // as WorldMap / BotDetail ItemIcon
const images = new Map();
/** map.js's loader: undefined while loading, null when missing. */
function image(url) {
    if (!images.has(url)) { images.set(url, undefined); const img = new Image(); img.onload = () => images.set(url, img); img.onerror = () => images.set(url, null); img.src = url; }
    return images.get(url);
}

/** player: the bot's body and head sprite actions (MonsterSprites.PlayerAnim, camelCase), or null for a dot. */
export async function mount(root, url, player, skillNames) {
    if (current) current.stop();
    current = null;
    root.innerHTML = '<div class="text-secondary small py-3">Loading replay…</div>';
    let rp;
    try { const r = await fetch(url); if (!r.ok) throw new Error(r.status + " " + r.statusText); rp = await r.json(); }
    catch (e) { root.innerHTML = '<div class="alert alert-danger small">Could not load the replay: ' + esc(e.message) + "</div>"; return; }
    rp.bow = player?.weaponClass === 12; // PacketAttack: WeaponClass 12 shoots an arrow per auto-attack
    const P = await Sprites.loadPlayer(player);
    const create = (replay, clock, onEnd) => {
        replay.bow = player?.weaponClass === 12;
        const v = new Viewer(root, replay);
        v.skillNames = skillNames; v.routeClock = clock; v.onRouteEnd = onEnd;
        if (P) { v.player = P; v.playerMoves = Sprites.indexPlayer(replay, P); }
        current = v;
        return v;
    };
    if (rp.routeSegments?.length) {
        const route = await import("./sim-route.js" + new URL(import.meta.url).search);
        current = route.play(root, rp, create);
    } else current = create(rp, null, null);
}

class Viewer {
    constructor(root, rp) {
        this.root = root; this.rp = rp; this.t = 0; this.playing = false; this.speed = 5; this.follow = true; this.zoom = 0;
        this.selected = null; this.selectedMob = null; this.alive = true;
        this.index(); this.dom(); this.terrain();
        this.fit(); this.zoom = Math.max(this.zoom, 10); this.listDirty = true;
        window.simReplay = this; // for inspecting the replay from the browser console
        this.renderList(); this.inspect();
        this.lastFrame = performance.now();
        const loop = now => { if (!this.alive) return; if (!this.root.isConnected) { this.stop(); return; } this.frame(now); requestAnimationFrame(loop); };
        requestAnimationFrame(loop);
    }
    stop() { this.alive = false; window.removeEventListener("keydown", this.onKey); }

    // ---- indexes over the recording ----
    index() {
        const rp = this.rp;
        rp.events.forEach((e, i) => e.i = i);
        this.mobs = new Map(rp.mobs.map(m => [m.id, m]));
        this.mobLife = new Map();  // id -> [{t, alive, x, y, dt?, dir?}]: dt > 0 a step of a wander that took dt to arrive at t
        this.mobHp = new Map();    // id -> [{t, hp}]
        this.spriteIx = Sprites.indexMonsters(rp);
        this.effects = Effects.index(rp);
        this.drops = new Map(); // mob id (0: the bot) -> HP drops [{t, before, after}], for the HP bars' white trail
        for (const e of rp.events) if ((e.kind === "hit" || e.kind === "hurt") && e.amount > 0 && e.hpLeft >= 0) {
            const k = e.kind === "hurt" ? 0 : e.mob; let l = this.drops.get(k); if (!l) this.drops.set(k, l = []);
            l.push({ t: e.t, before: e.hpLeft + e.amount, after: e.hpLeft });
        }
        const push = (map, id, v) => { let a = map.get(id); if (!a) map.set(id, a = []); a.push(v); };
        // A wander's steps are pushed ahead of time; anything that happens to the monster first (fight, death, knockback) cuts them.
        // Steps the server AI walked (real) are recorded as they happened and are never cut.
        const cut = (id, t) => { const l = this.mobLife.get(id); while (l?.length && l[l.length - 1].t > t && !l[l.length - 1].real) l.pop(); return l?.[l.length - 1]; };
        const die = (id, t) => { const p = cut(id, t); if (p?.alive) push(this.mobLife, id, { t, alive: false, x: p.x, y: p.y, dir: p.dir }); };
        this.areas = [];           // {t0, t1, x, y, skill, text}
        // Drops on the ground: from the kill ("drop") until picked up ("loot", same dropId) or 60 s (GroundItem.Expiration).
        this.ground = new Map();
        for (const e of rp.events) {
            if (e.kind === "drop") this.ground.set(e.detail.dropId, { x: e.x, y: e.y, name: e.detail.name, code: e.detail.code, count: e.detail.count, t0: e.t, t1: Math.min(rp.duration, e.t + 60) });
            else if (e.kind === "loot" && this.ground.has(e.detail?.dropId)) this.ground.get(e.detail.dropId).t1 = e.t;
        }
        const openAreas = new Map();
        // Wanders (rp.mobMoves, compact) merge into the timeline; on a tie the move comes first so an engage at that instant cuts it.
        const D = [[0, 1], [1, 1], [1, 0], [1, -1], [0, -1], [-1, -1], [-1, 0], [-1, 1]]; // HuntSimulator.StepDirs
        const moves = (rp.mobMoves || []).map(([mob, t, speed, x, y, steps, real]) => ({ kind: "mob-move", mob, t, speed, x, y, steps, real: !!real }));
        for (const e of [...moves, ...rp.events].sort((a, b) => a.t - b.t)) {
            if (e.kind === "spawn") { cut(e.mob, e.t); push(this.mobLife, e.mob, { t: e.t, alive: true, x: e.x, y: e.y }); push(this.mobHp, e.mob, { t: e.t, hp: this.mobs.get(e.mob)?.maxHp ?? 1 }); }
            else if (e.kind === "knockback" && e.mob) { if (cut(e.mob, e.t)) push(this.mobLife, e.mob, { t: e.t, alive: true, x: e.x, y: e.y }); }
            else if (e.kind === "hit" && e.mob) { push(this.mobHp, e.mob, { t: e.t, hp: e.hpLeft }); if (e.hpLeft === 0) die(e.mob, e.t); }
            else if (e.kind === "kill" && e.mob) die(e.mob, e.t);
            else if (e.kind === "engage" && e.mob) cut(e.mob, e.t);
            else if (e.kind === "mob-move") {
                if (!cut(e.mob, e.t)?.alive) continue;
                let { t, x, y } = e;
                for (const c of e.steps) {
                    const [dx, dy] = D[c], dt = e.speed * (dx && dy ? 1.4142 : 1);
                    push(this.mobLife, e.mob, { t: t += dt, alive: true, x: x += dx, y: y += dy, dt, spd: e.speed, dir: Sprites.facing(dx, dy), real: e.real });
                }
            }
            else if (e.kind === "area-start") { const a = { t0: e.t, t1: rp.duration, x: e.x, y: e.y, skill: e.skill, text: e.text }; this.areas.push(a); openAreas.set(e.detail?.entity, a); }
            else if (e.kind === "area-end") { const a = openAreas.get(e.detail?.entity); if (a) { a.t1 = e.t; openAreas.delete(e.detail?.entity); } }
        }
        // Activity: the latest activity-starting event decides it; a fight ends at its kill / end / the player's death.
        this.segments = [];
        let cur = "none", from = 0, fightMob = 0;
        const to = (act, t) => { if (act === cur) return; if (t > from) this.segments.push({ t0: from, t1: t, act: cur }); cur = act; from = t; };
        for (const e of rp.events) {
            if (e.kind === "route") to(e.detail?.approach ? "approach" : e.detail?.flee ? "flee" : e.detail?.loot ? "loot" : "wander", e.t);
            else if (e.kind === "flee") to("flee", e.t);
            else if (e.kind === "flee-end") to("none", e.t);
            else if (e.kind === "engage") { to("fight", e.t); fightMob = e.mob; }
            else if (cur === "fight" && ((e.kind === "kill" && e.mob === fightMob) || e.kind === "fight-end" || e.kind === "death")) to("none", e.t);
            else if (e.kind === "sit") to("rest", e.t);
            else if (e.kind === "stand") to("none", e.t);
            else if (e.kind === "idle") to("idle", e.t);
            else if (cur !== "fight" && cur !== "rest" && (e.kind === "pick" || e.kind === "nopick")) to("none", e.t);
        }
        to("end", rp.duration);
        this.majors = rp.events.filter(e => MAJOR.has(e.kind));
        this.lastHurtBefore = t => rp.events.filter(e => e.kind === "hurt" && e.t <= t && e.t > t - 30).slice(-8);
    }

    activityAt(t) { const i = lastLE(this.segments, t, s => s.t0); return i >= 0 && t < this.segments[i].t1 ? this.segments[i].act : "none"; }

    posAt(t) {
        const tr = this.rp.track, i = lastLE(tr, t, s => s.t);
        const a = tr[Math.max(0, i)];
        const b = tr[i + 1];
        if (b && b.dt > 0 && t > b.t - b.dt) { const f = (t - (b.t - b.dt)) / b.dt; return { x: a.x + (b.x - a.x) * f, y: a.y + (b.y - a.y) * f, cx: a.x, cy: a.y }; }
        return { x: a.x, y: a.y, cx: a.x, cy: a.y };
    }
    vitalsAt(t) { const v = this.rp.vitals, i = lastLE(v, t, s => s.t); return v[Math.max(0, i)]; }
    mobAt(id, t) {
        const l = this.mobLife.get(id); if (!l) return null;
        const i = lastLE(l, t, s => s.t); if (i < 0) return null;
        let s = l[i]; const n = l[i + 1];
        if (s.alive && n?.dt > 0 && t > n.t - n.dt) { const f = (t - (n.t - n.dt)) / n.dt; s = { ...s, x: s.x + (n.x - s.x) * f, y: s.y + (n.y - s.y) * f, dir: n.dir, walk: n.spd, real: n.real }; }
        const h = this.mobHp.get(id); const j = h ? lastLE(h, t, s => s.t) : -1;
        return { ...s, hp: j >= 0 ? h[j].hp : this.mobs.get(id).maxHp, diedAt: s.alive ? null : s.t };
    }
    lastOf(kinds, t) { const ev = this.rp.events; for (let i = lastLE(ev, t, e => e.t); i >= 0; i--) if (kinds.includes(ev[i].kind)) return ev[i]; return null; }
    targetAt(t) {
        const p = this.lastOf(["pick", "kill", "fight-end", "death", "nopick", "sit"], t);
        if (!p || p.kind !== "pick") return null;
        const m = this.mobAt(p.mob, t); return m && m.alive ? p.mob : null;
    }

    // ---- DOM ----
    dom() {
        const opt = (v, label, sel) => '<option value="' + v + '"' + (sel ? " selected" : "") + ">" + label + "</option>";
        this.root.innerHTML =
'<div class="row g-2">' +
'  <div class="col-xl-8">' +
'    <div class="card"><div class="card-body p-2">' +
'      <div style="position:relative">' +
'        <canvas class="sim-canvas" style="width:100%;height:560px;display:block;background:' + COLORS.bg + ';border-radius:6px;cursor:grab"></canvas>' +
'        <div class="sim-tip small" style="position:absolute;pointer-events:none;display:none;background:#000c;color:#fff;padding:2px 6px;border-radius:4px"></div>' +
'      </div>' +
'      <div class="d-flex flex-wrap align-items-center gap-2 mt-2">' +
'        <button class="btn btn-sm btn-outline-secondary" data-a="prev" title="Previous significant event (left arrow)">⏮</button>' +
'        <button class="btn btn-sm btn-primary" data-a="play" style="min-width:4.5rem" title="Play / pause (space)">▶ Play</button>' +
'        <button class="btn btn-sm btn-outline-secondary" data-a="next" title="Next significant event (right arrow)">⏭</button>' +
'        <select class="form-select form-select-sm" data-a="speed" style="width:auto" title="Virtual seconds per real second">' +
           [0.25, 0.5, 1, 2, 5, 10, 30, 60].map(s => opt(s, s + "×", s === 5)).join("") + '</select>' +
'        <span class="font-monospace small sim-time" style="min-width:11rem"></span>' +
'        <div class="form-check form-check-inline small mb-0"><input class="form-check-input" type="checkbox" data-a="follow" checked id="sim-follow"><label class="form-check-label" for="sim-follow">follow bot</label></div>' +
'        <button class="btn btn-sm btn-outline-secondary" data-a="fit">Fit map</button>' +
'        <select class="form-select form-select-sm" data-a="jump" style="width:auto">' + opt("", "Jump to next…") +
           ["pick", "nopick", "kill", "death", "sit", "stand", "rejected", "no-effect", "failed", "idle", "wander", "cast-decision", "loot"].map(k => opt(k, k)).join("") + '</select>' +
'      </div>' +
'      <canvas class="sim-strip mt-2" style="width:100%;height:46px;display:block;cursor:pointer;border-radius:4px" title="Timeline: click or drag to seek"></canvas>' +
'      <div class="d-flex flex-wrap gap-3 small text-secondary mt-1">' +
         Object.entries(ACTIVITY).filter(([k]) => k !== "none").map(([k, a]) => '<span><span style="display:inline-block;width:10px;height:10px;border-radius:2px;background:' + a.color + '"></span> ' + a.label + "</span>").join("") +
'        <span>marks: <span style="color:#fff">│</span> kill <span style="color:#ff4d4f">┃</span> death <span style="color:#ffa940">▪</span> refused skill <span style="color:#ff4d4f">✖</span> failure; green line: HP</span>' +
'      </div>' +
'    </div></div>' +
'  </div>' +
'  <div class="col-xl-4">' +
'    <div class="card" style="height:100%">' +
'      <ul class="nav nav-tabs small px-2 pt-2"><li class="nav-item"><a href="#" class="nav-link py-1 active" data-tab="world">World</a></li><li class="nav-item"><a href="#" class="nav-link py-1" data-tab="inventory">Inventory</a></li></ul>' +
'      <div class="card-body p-2 sim-inspector small" style="max-height:650px;overflow:auto"></div></div>' +
'  </div>' +
'  <div class="col-12">' +
'    <div class="card"><div class="card-body p-2">' +
'      <div class="d-flex flex-wrap align-items-center gap-2 mb-2 small">' +
'        <span class="fw-semibold">Events</span>' +
         Object.keys(CATEGORY).map(c => '<div class="form-check form-check-inline mb-0"><input class="form-check-input" type="checkbox" data-cat="' + c + '" id="cat-' + c + '" checked><label class="form-check-label" for="cat-' + c + '">' + c + "</label></div>").join("") +
'        <div class="form-check form-check-inline mb-0"><input class="form-check-input" type="checkbox" data-a="minor" id="sim-minor"><label class="form-check-label" for="sim-minor" title="hits, regen ticks, spawns, auto-attack decisions, routes">every tick-level event</label></div>' +
'        <input class="form-control form-control-sm" data-a="search" placeholder="filter text…" style="max-width:14rem">' +
'        <span class="text-secondary sim-count"></span>' +
'      </div>' +
'      <div class="sim-list font-monospace" style="max-height:300px;overflow:auto;font-size:.78rem"></div>' +
'    </div></div>' +
'  </div>' +
'</div>';

        const q = s => this.root.querySelector(s);
        this.canvas = q(".sim-canvas"); this.strip = q(".sim-strip"); this.tip = q(".sim-tip"); this.inspector = q(".sim-inspector");
        this.list = q(".sim-list"); this.timeLabel = q(".sim-time"); this.playBtn = q('[data-a="play"]'); this.count = q(".sim-count");
        const follow = q('[data-a="follow"]');
        const unfollow = () => { this.follow = false; follow.checked = false; };
        q('[data-a="play"]').onclick = () => this.toggle();
        q('[data-a="prev"]').onclick = () => this.jump(-1);
        q('[data-a="next"]').onclick = () => this.jump(1);
        q('[data-a="speed"]').onchange = e => this.speed = +e.target.value;
        follow.onchange = e => this.follow = e.target.checked;
        q('[data-a="fit"]').onclick = () => { unfollow(); this.fit(); };
        q('[data-a="jump"]').onchange = e => {
            const k = e.target.value; e.target.value = ""; if (!k) return;
            const ev = this.rp.events.find(x => x.kind === k && x.t > this.t + 1e-6) ?? this.rp.events.find(x => x.kind === k);
            if (ev) this.select(ev);
        };
        this.root.querySelectorAll("[data-cat]").forEach(c => c.onchange = () => { this.listDirty = true; this.renderList(); });
        q('[data-a="minor"]').onchange = () => { this.listDirty = true; this.renderList(); };
        q('[data-a="search"]').oninput = () => { this.listDirty = true; this.renderList(); };
        this.list.onclick = e => { const row = e.target.closest("[data-i]"); if (row) this.select(this.rp.events[+row.dataset.i]); };
        this.tab = "world";
        this.root.querySelectorAll("[data-tab]").forEach(a => a.onclick = e => {
            e.preventDefault(); this.tab = a.dataset.tab;
            this.root.querySelectorAll("[data-tab]").forEach(b => b.classList.toggle("active", b === a));
            this.inspect();
        });
        this.inspector.onclick = e => {
            const a = e.target.closest("[data-ev]"); if (a) { e.preventDefault(); this.select(this.rp.events[+a.dataset.ev]); return; }
            const m = e.target.closest("[data-mob]"); if (m) { e.preventDefault(); this.selectedMob = +m.dataset.mob; this.selected = null; this.inspect(); return; }
            if (e.target.closest("[data-now]")) { e.preventDefault(); this.selected = null; this.selectedMob = null; this.inspect(); }
        };
        let seeking = false;
        const seek = e => {
            const r = this.strip.getBoundingClientRect(), ratio = (e.clientX - r.left) / r.width;
            if (this.routeClock) {
                const viewer = this.routeClock.seek(ratio * this.routeClock.total);
                viewer.selected = null; viewer.inspect();
                return;
            }
            else this.setT(ratio * this.rp.duration);
            this.selected = null; this.inspect();
        };
        this.strip.onpointerdown = e => { seeking = true; this.strip.setPointerCapture(e.pointerId); this.pause(); seek(e); };
        this.strip.onpointermove = e => { if (seeking) seek(e); };
        this.strip.onpointerup = () => seeking = false;
        let drag = null;
        this.canvas.onpointerdown = e => { drag = { x: e.clientX, y: e.clientY, cx: this.cx, cy: this.cy, moved: false }; this.canvas.setPointerCapture(e.pointerId); };
        this.canvas.onpointermove = e => {
            if (drag) {
                const dx = e.clientX - drag.x, dy = e.clientY - drag.y;
                if (Math.abs(dx) + Math.abs(dy) > 3) { drag.moved = true; unfollow(); }
                if (drag.moved) { this.cx = drag.cx - dx / this.zoom; this.cy = drag.cy + dy / this.zoom; }
            }
            this.hover(e);
        };
        this.canvas.onpointerup = e => { if (drag && !drag.moved) this.click(e); drag = null; };
        this.canvas.onpointerleave = () => this.tip.style.display = "none";
        this.canvas.onwheel = e => {
            e.preventDefault();
            const r = this.canvas.getBoundingClientRect(), px = e.clientX - r.left, py = e.clientY - r.top;
            const before = this.toWorld(px, py);
            this.zoom = Math.max(0.5, Math.min(40, this.zoom * (e.deltaY < 0 ? 1.2 : 1 / 1.2)));
            const after = this.toWorld(px, py);
            if (!this.follow) { this.cx += before.x - after.x; this.cy += before.y - after.y; }
        };
        this.onKey = e => {
            if (!this.root.isConnected || e.target.closest?.("input,select,textarea")) return;
            if (e.key === " ") { e.preventDefault(); this.toggle(); }
            else if (e.key === "ArrowLeft") { e.preventDefault(); if (e.shiftKey) this.setT(this.t - 1); else this.jump(-1); }
            else if (e.key === "ArrowRight") { e.preventDefault(); if (e.shiftKey) this.setT(this.t + 1); else this.jump(1); }
        };
        window.addEventListener("keydown", this.onKey);
    }

    terrain() {
        const W = this.rp.width, H = this.rp.height, walk = this.rp.walk;
        const c = document.createElement("canvas"); c.width = W; c.height = H;
        const g = c.getContext("2d"), img = g.createImageData(W, H);
        const hex = h => [parseInt(h.slice(1, 3), 16), parseInt(h.slice(3, 5), 16), parseInt(h.slice(5, 7), 16)];
        const on = hex(COLORS.walkable), off = hex(COLORS.blocked);
        for (let y = 0; y < H; y++) for (let x = 0; x < W; x++) {
            const col = walk.charCodeAt(y * W + x) === 49 ? on : off, o = ((H - 1 - y) * W + x) * 4;
            img.data[o] = col[0]; img.data[o + 1] = col[1]; img.data[o + 2] = col[2]; img.data[o + 3] = 255;
        }
        g.putImageData(img, 0, 0);
        this.terrainImg = c;
    }

    fit() {
        const r = this.canvas.getBoundingClientRect();
        this.zoom = Math.min(r.width / this.rp.width, r.height / this.rp.height) || 2;
        this.cx = (this.rp.width - 1) / 2; this.cy = (this.rp.height - 1) / 2;
    }
    toScreen(x, y) { return { x: (x - this.cx) * this.zoom + this.cw / 2, y: (this.cy - y) * this.zoom + this.ch / 2 }; }
    toWorld(px, py) { return { x: (px - this.cw / 2) / this.zoom + this.cx, y: this.cy - (py - this.ch / 2) / this.zoom }; }

    // ---- playback ----
    toggle() { if (this.playing) this.pause(); else this.play(); }
    play() { if (this.t >= this.rp.duration) this.t = 0; this.playing = true; this.selected = null; this.selectedMob = null; this.playBtn.textContent = "⏸ Pause"; }
    pause() { const was = this.playing; this.playing = false; this.playBtn.textContent = "▶ Play"; if (was && !this.selected && !this.selectedMob) this.inspect(); }
    setT(t) { this.t = Math.max(0, Math.min(this.rp.duration, t)); }
    jump(dir) {
        this.pause();
        const m = this.majors, eps = 1e-6;
        const ev = dir > 0 ? m.find(e => e.t > this.t + eps) : [...m].reverse().find(e => e.t < this.t - eps);
        if (ev) this.select(ev);
    }
    select(ev) { this.pause(); this.selected = ev; this.selectedMob = null; this.setT(ev.t); this.inspect(); this.highlight(true); }

    frame(now) {
        const dt = Math.min(0.1, (now - this.lastFrame) / 1000); this.lastFrame = now;
        if (this.playing) {
            this.setT(this.t + dt * this.speed);
            if (this.t >= this.rp.duration) {
                if (this.onRouteEnd && !this.routeEndFired) { this.routeEndFired = true; this.onRouteEnd(this); }
                else if (!this.onRouteEnd) this.pause();
            }
        }
        this.draw(); this.drawStrip();
        const elapsed = this.routeClock ? this.routeClock.offset + this.t : this.t;
        const total = this.routeClock ? this.routeClock.total : this.rp.duration;
        this.timeLabel.textContent = fmtT(elapsed) + " / " + fmtT(total) + "  (" + elapsed.toFixed(2) + " s)";
        this.highlight(false);
        if (this.playing && (!this.lastInspect || now - this.lastInspect > 250)) { this.inspect(); this.lastInspect = now; }
    }

    // ---- map ----
    draw() {
        const cv = this.canvas, r = cv.getBoundingClientRect(), k = dpr();
        if (cv.width !== Math.round(r.width * k) || cv.height !== Math.round(r.height * k)) { cv.width = Math.round(r.width * k); cv.height = Math.round(r.height * k); }
        this.cw = r.width; this.ch = r.height;
        const g = cv.getContext("2d"); g.setTransform(k, 0, 0, k, 0, 0);
        g.fillStyle = COLORS.bg; g.fillRect(0, 0, r.width, r.height);
        const t = this.t, z = this.zoom, me = this.posAt(t);
        if (this.follow) { this.cx = me.x; this.cy = me.y; }
        const tl = this.toScreen(-0.5, this.rp.height - 0.5);
        g.imageSmoothingEnabled = false;
        g.drawImage(this.terrainImg, tl.x, tl.y, this.rp.width * z, this.rp.height * z);
        const v0 = this.toScreen(me.cx - 21.5, me.cy + 21.5);
        g.strokeStyle = COLORS.view; g.lineWidth = 1; g.setLineDash([4, 4]); g.strokeRect(v0.x, v0.y, 43 * z, 43 * z); g.setLineDash([]);
        const act = this.activityAt(t);
        if (act === "wander" || act === "approach" || act === "loot") {
            const route = this.lastOf(["route"], t);
            if (route && route.detail && route.detail.path) {
                const p = route.detail.path; let start = 0;
                for (let i = 0; i < p.length; i += 2) if (p[i] === me.cx && p[i + 1] === me.cy) { start = i; break; }
                g.strokeStyle = COLORS.route; g.globalAlpha = 0.7; g.lineWidth = Math.max(1, z * 0.15); g.setLineDash([z * 0.4 + 2, z * 0.3 + 2]);
                g.beginPath(); const s0 = this.toScreen(me.x, me.y); g.moveTo(s0.x, s0.y);
                for (let i = start + 2; i < p.length; i += 2) { const s = this.toScreen(p[i], p[i + 1]); g.lineTo(s.x, s.y); }
                g.stroke(); g.setLineDash([]); g.globalAlpha = 1;
                const end = this.toScreen(p[p.length - 2], p[p.length - 1]);
                g.beginPath(); g.arc(end.x, end.y, Math.max(3, z * 0.5), 0, 7); g.stroke();
            }
        }
        for (const a of this.areas) if (a.t0 <= t && t < a.t1 && a.x >= 0) {
            const s = this.toScreen(a.x, a.y); g.fillStyle = "rgba(199,125,255,0.25)"; g.strokeStyle = COLORS.area;
            g.fillRect(s.x - z * 1.5, s.y - z * 1.5, z * 3, z * 3); g.strokeRect(s.x - z * 1.5, s.y - z * 1.5, z * 3, z * 3);
            if (z >= 4) { g.fillStyle = COLORS.area; g.font = font(10); g.fillText(a.skill || "event", s.x - z * 1.5, s.y - z * 1.5 - 2); }
        }
        const target = this.targetAt(t), rad = Math.max(2.5, z * 0.42);
        const sel = this.selected;
        const cand = sel && (sel.kind === "pick" || sel.kind === "nopick") && Math.abs(sel.t - t) < 1e-6 ? new Map(sel.detail.candidates.map(c => [c.mob, c])) : null;
        this.hoverTargets = [];
        const now = t * 1000, list = [];
        for (const [id, mob] of this.mobs) {
            const m = this.mobAt(id, t); if (!m) continue;
            if (!m.alive && t - m.diedAt >= 2) continue;
            const s = this.toScreen(m.x, m.y);
            if (s.x < -60 || s.y < -60 || s.x > this.cw + 60 || s.y > this.ch + 60) continue;
            list.push({ id, mob, m, s });
        }
        list.sort((a, b) => b.m.y - a.m.y); // nearer the bottom drawn on top, as the minimap
        const spriteH = Math.max(22, Math.min(72, z * 2.6)), px = Math.max(z, 8) * 1.5 / 50; // ponytail: floor at 8 px/tile so zoomed-out sprites stay visible
        for (const { id, mob, m, s } of list) {
            const a = animOf(mob.name), lay = a && Sprites.monsterLayers(this.spriteIx, a, id, m, t, me, s, px);
            if (!m.alive) {
                if (lay) drawLayers(g, lay.layers);
                else if (t - m.diedAt < 1.5) { g.globalAlpha = 1 - (t - m.diedAt) / 1.5; g.strokeStyle = COLORS.monster; g.lineWidth = 2; g.beginPath(); g.moveTo(s.x - rad, s.y - rad); g.lineTo(s.x + rad, s.y + rad); g.moveTo(s.x + rad, s.y - rad); g.lineTo(s.x - rad, s.y + rad); g.stroke(); g.globalAlpha = 1; }
                continue;
            }
            const win = mob.forecast.startsWith("win");
            const box = lay ? lay.box : [s.x - rad, s.y - rad, s.x + rad, s.y + rad];
            const ring = (col, r) => { g.strokeStyle = col; g.lineWidth = 2; g.beginPath(); if (lay) g.ellipse(s.x, s.y, r + spriteH * 0.3, (r + spriteH * 0.3) * 0.45, 0, 0, 7); else g.arc(s.x, s.y, rad + r, 0, 7); g.stroke(); };
            if (lay) { const sr = (a.shadow > 0 ? a.shadow : 0.35) * (a.size || 1) * px * 50 * 0.6; g.fillStyle = win ? "#0007" : "#e5484d99"; g.beginPath(); g.ellipse(s.x, s.y, sr, sr * 0.45, 0, 0, 7); g.fill(); drawLayers(g, lay.layers); }
            else { g.fillStyle = win ? COLORS.monster : "#7a2a2a"; g.beginPath(); g.arc(s.x, s.y, rad, 0, 7); g.fill(); }
            if (id === target) ring(COLORS.target, 3);
            if (id === this.selectedMob) ring("#fff", 6);
            const c = cand && cand.get(id);
            if (c) {
                ring(c.verdict.startsWith("chosen") ? "#52c41a" : c.verdict.startsWith("too strong") ? "#ff4d4f" : c.verdict.startsWith("unreachable") ? "#ffa940" : "#bfbfbf", 4);
                g.fillStyle = "#fff"; g.font = font(11, true); g.fillText(c.rank ? "#" + c.rank : "✕", box[2] + 2, box[1] + 10);
            }
            if (m.hp < mob.maxHp) { const w = Math.max(24, Math.min(48, z * 1.2)); Sprites.drawHpBar(g, s.x, box[1] - 3, w, Math.max(3, w * 0.13), m.hp / mob.maxHp, this.trailAt(id, t, m.hp, mob.maxHp)); }
            if (z >= 9 || id === target) { g.fillStyle = COLORS.text; g.font = font(10); const w = g.measureText(mob.name).width; g.fillText(mob.name, s.x - w / 2, Math.max(box[3], s.y + rad) + 11); }
            const hr = Math.max(rad + 3, (box[3] - box[1]) / 2, (box[2] - box[0]) / 2);
            this.hoverTargets.push({ x: (box[0] + box[2]) / 2, y: (box[1] + box[3]) / 2, r: hr, id, text: mob.name + " #" + id + " (" + m.x + "," + m.y + ") HP " + m.hp + "/" + mob.maxHp + " · " + mob.forecast });
        }
        const b = this.toScreen(me.x, me.y), vit = this.vitalsAt(t), sitting = act === "rest", br = Math.max(3.5, z * 0.5);
        if (target) { const m = this.mobAt(target, t), s = this.toScreen(m.x, m.y); g.strokeStyle = COLORS.target; g.globalAlpha = 0.6; g.lineWidth = 1.5; g.beginPath(); g.moveTo(b.x, b.y); g.lineTo(s.x, s.y); g.stroke(); g.globalAlpha = 1; }
        // drops: the item's icon on its cell (map.js drawItem), a gold diamond until it loads
        for (const it of this.ground.values()) if (it.t0 <= t && t < it.t1) {
            const s = this.toScreen(it.x, it.y), img = image(itemIcon(it.code)), k = Math.max(10, Math.min(36, z * 1.3));
            if (s.x < -k || s.y < -k || s.x > this.cw + k || s.y > this.ch + k) continue;
            // Unity GroundItem.Update: an animated drop starts 0.2 up at 20 units/s, gravity 120/s² (≈0.35 s in the air, ≈1.9 tiles high)
            const a = t - it.t0, h = Math.max(0, 0.2 + 20 * a - 60 * a * a), y = s.y - h * z;
            g.fillStyle = "#0006"; g.beginPath(); g.ellipse(s.x, s.y + k * 0.35, k * 0.35, k * 0.14, 0, 0, 7); g.fill(); // SpriteUtil.AttachShadowToGameObject
            if (img) { g.imageSmoothingEnabled = true; g.drawImage(img, s.x - k / 2, y - k / 2, k, k); g.imageSmoothingEnabled = false; }
            else { const d = k * 0.3; g.fillStyle = "#ffd54a"; g.beginPath(); g.moveTo(s.x, y - d); g.lineTo(s.x + d, y); g.lineTo(s.x, y + d); g.lineTo(s.x - d, y); g.closePath(); g.fill(); }        }
        const pl = this.player && Sprites.playerLayers(this.player, this.playerMoves, this.rp, t, me, b, act, target && this.mobAt(target, t), Math.max(z, 8) * 1.5 / 50);
        if (pl) { g.fillStyle = "#0008"; g.beginPath(); g.ellipse(b.x, b.y + 2, br * 1.6, br * 0.7, 0, 0, 7); g.fill(); drawLayers(g, pl); }
        else { g.fillStyle = COLORS.self; g.beginPath(); g.arc(b.x, b.y, br, 0, 7); g.fill(); g.strokeStyle = "#fff"; g.lineWidth = 1.5; g.stroke(); }
        if (sitting) { g.fillStyle = "#9cd1ff"; g.font = font(12, true); g.fillText("z z", b.x + 6, b.y - 8); }
        Sprites.drawHpBar(g, b.x, b.y + br + 7, 28, 4, vit.hp / this.rp.maxHp, this.trailAt(0, t, vit.hp, this.rp.maxHp));
        this.bar(g, b.x - 14, b.y + br + 8, 28, 3, vit.sp / Math.max(1, this.rp.maxSp), "#3b9dff");
        this.hoverTargets.push({ x: b.x, y: b.y, r: br + 3, id: 0, text: "bot (" + me.cx + "," + me.cy + ") HP " + vit.hp + "/" + this.rp.maxHp + " SP " + vit.sp + "/" + this.rp.maxSp + " · " + ACTIVITY[act].label });
        Effects.draw(g, this, this.effects, t);
        const ev = this.rp.events;
        // Damage numbers and the skill shout (sim-sprites.js, ported from the client; the minimap draws them the same way).
        const sh = Math.max(22, Math.min(72, z * 2.6)), NV = { ctx: g, spriteHeight: sh * dpr() }, combos = new Set();
        const num = (path, value, x, y, born, extra) => { const life = (t - born) / path.tween; if (life < 0 || life > 1) return; g.save(); g.setTransform(1, 0, 0, 1, 0, 0); g.textAlign = "center"; g.textBaseline = "alphabetic"; Sprites.drawNumber(NV, { path, value, x: x * dpr(), y: y * dpr(), ...extra }, life); g.restore(); }; // device pixels, exactly as the minimap draws
        let shouted = false;
        for (let i = lastLE(ev, t, e => e.t); i >= 0 && ev[i].t > t - 4; i--) {
            const e = ev[i], age = t - e.t; let x, y, txt, col;
            if (e.kind === "hit" || e.kind === "hurt") {
                const m = this.mobAt(e.mob, e.kind === "hit" ? e.t : t), p = this.posAt(e.t); if (!m) continue;
                const ms = this.toScreen(m.x, m.y), my = ms.y - sh * 0.6;
                if (e.kind === "hurt") { // on the player: red, one number per landing (as the minimap)
                    if (e.amount === 0) num(Sprites.MISS_PATH, "Miss", b.x, b.y - sh * 0.6, e.t, { color: Sprites.RED });
                    else num(Sprites.DAMAGE_PATH, e.amount, b.x, b.y - sh * 0.6, e.t, { dir: Math.sign(p.x - m.x) || 1, color: Sprites.RED });
                    continue;
                }
                if (e.amount === 0) { num(Sprites.MISS_PATH, "Miss", ms.x, my, e.t); continue; }
                // Double Attack: each hit 0.2 s apart, plus the yellow running total riding the monster; the newest total ends older ones.
                const hits = Math.max(1, e.detail?.hits || 1), per = Math.round(e.amount / hits), dir = Math.sign(m.x - p.x) || 1;
                for (let h = hits - 1; h >= 0; h--) {
                    const born = e.t + Sprites.HIT_INTERVAL_MS / 1000 * h;
                    num(Sprites.DAMAGE_PATH, per, ms.x, my, born, { dir });
                    if (hits > 1 && born <= t && !combos.has(e.mob)) {
                        combos.add(e.mob);
                        const now = (e.hpLeft !== 0 && this.mobAt(e.mob, t)) || m, ns = this.toScreen(now.x, now.y);
                        num(Sprites.COMBO_PATH, per * (h + 1), ns.x, ns.y - sh * 0.6, born);
                    }
                }
                continue;
            }
            else if (e.kind === "executed") { // "Bash!!" for 3 s, like WorldMap.Shout / Unity's ShowSkillCastMessage(skill, 3)
                if (!shouted && age <= 3 && !["Hiding", "Cloaking", "NoCast"].includes(e.skill)) { shouted = true; g.save(); Sprites.drawShout({ ctx: g, fs: 15 }, (this.skillNames?.[e.skill] || e.skill) + "!!", b.x, b.y - sh); g.restore(); }
                continue;
            }
            else if (age > 1) continue;
            else if (e.kind === "rejected" || e.kind === "no-effect") { x = b.x; y = b.y - 14; txt = e.skill + (e.kind === "rejected" ? " refused" : " no effect"); col = "#ffa940"; }
            else if (e.kind === "regen" && sitting) { x = b.x + 14; y = b.y; txt = "+" + e.amount; col = "#73d13d"; }
            else if (e.kind === "death") { x = b.x; y = b.y; txt = "DIED"; col = "#ff4d4f"; }
            else continue;
            g.globalAlpha = Math.max(0, 1 - age); g.fillStyle = col; g.font = font(12, true); g.fillText(txt, x - g.measureText(txt).width / 2, y - 10 - age * 18); g.globalAlpha = 1;
        }
        g.fillStyle = "#000a"; g.fillRect(8, 8, 236, 56);
        g.fillStyle = COLORS.text; g.font = font(12, true); g.fillText((this.rp.map || "") + "  " + fmtT((this.routeClock?.offset || 0) + t) + "  " + ACTIVITY[act].label, 16, 24);
        this.bar(g, 16, 32, 150, 8, vit.hp / this.rp.maxHp, "#198754"); g.font = font(10); g.fillStyle = COLORS.text; g.fillText("HP " + vit.hp + "/" + this.rp.maxHp, 172, 40);
        this.bar(g, 16, 46, 150, 6, vit.sp / Math.max(1, this.rp.maxSp), "#3b9dff"); g.fillStyle = COLORS.text; g.fillText("SP " + vit.sp + "/" + this.rp.maxSp, 172, 53);
        // "Jellopy - 1 obtained." for 3 s at the top middle, as map.js drawToast (Unity's ItemObtainedToast)
        for (let i = lastLE(ev, t, e => e.t); i >= 0 && ev[i].t > t - 3; i--) {
            if (ev[i].kind !== "loot") continue;
            const d = ev[i].detail || {}, txt = d.name + " - " + d.count + " obtained.", img = image(itemIcon(d.code)), k = img ? 20 : 0;
            g.font = font(12, true); g.textBaseline = "middle";
            const w = g.measureText(txt).width + (k ? k + 6 : 0), left = g.canvas.width / dpr() / 2 - w / 2, mid = 8 + 14;
            g.fillStyle = "#000000aa"; g.fillRect(left - 6, 8, w + 12, 28);
            if (img) g.drawImage(img, left, mid - k / 2, k, k);
            g.fillStyle = COLORS.text; g.fillText(txt, left + k + (k ? 6 : 0), mid); g.textBaseline = "alphabetic";
            break;
        }
    }
    /** Where the HP bar's white trail ends at t, replayed from the drops in the last 2 s (it drains in under 0.4 s), so
     *  seeking and playing backwards show the same thing. */
    trailAt(id, t, hp, maxHp) {
        const l = this.drops.get(id), tr = {};
        if (l) for (let i = lastLE(l, t - 2, d => d.t) + 1; i < l.length && l[i].t <= t; i++) {
            Sprites.hpTrail(tr, l[i].before / maxHp, l[i].t); Sprites.hpTrail(tr, l[i].after / maxHp, l[i].t);
        }
        return Sprites.hpTrail(tr, hp / maxHp, t);
    }
    bar(g, x, y, w, h, f, col) { g.fillStyle = "#000c"; g.fillRect(x, y, w, h); g.fillStyle = col; g.fillRect(x, y, w * Math.max(0, Math.min(1, f)), h); }

    drawStrip() {
        const cv = this.strip, r = cv.getBoundingClientRect(), k = dpr();
        if (cv.width !== Math.round(r.width * k) || cv.height !== Math.round(r.height * k)) { cv.width = Math.round(r.width * k); cv.height = Math.round(r.height * k); this.stripCache = null; }
        const g = cv.getContext("2d");
        const W = r.width, H = r.height, D = this.routeClock?.total || this.rp.duration;
        const offset = this.routeClock?.offset || 0, X = t => (offset + t) / D * W;
        if (!this.stripCache) {
            const c = document.createElement("canvas"); c.width = cv.width; c.height = cv.height;
            const h = c.getContext("2d"); h.setTransform(k, 0, 0, k, 0, 0);
            h.fillStyle = "#1b1f27"; h.fillRect(0, 0, W, H);
            const parts = [...(this.routeClock?.timeline || []), {
                offset, segments: this.segments, events: this.rp.events, vitals: this.rp.vitals, maxHp: this.rp.maxHp,
            }];
            for (const part of parts) {
                const at = t => X(part.offset + t);
                for (const s of part.segments) if (ACTIVITY[s.act]) { const x0 = Math.floor(at(s.t0)); h.fillStyle = ACTIVITY[s.act].color; h.fillRect(x0, 14, Math.max(1, Math.ceil(at(s.t1)) - x0), 20); }
                h.strokeStyle = "#73d13d"; h.lineWidth = 1; h.beginPath();
                const vit = part.vitals, hy = v => 45 - 10 * v.hp / part.maxHp;
                vit.forEach((v, i) => { if (i) h.lineTo(at(v.t), hy(vit[i - 1])); i ? h.lineTo(at(v.t), hy(v)) : h.moveTo(at(v.t), hy(v)); });
                h.stroke();
                for (const e of part.events) {
                    if (e.kind === "kill") { h.fillStyle = "#fff"; h.fillRect(at(e.t), 2, 1, 10); }
                    else if (e.kind === "death") { h.fillStyle = "#ff4d4f"; h.fillRect(at(e.t) - 1, 0, 3, 14); }
                    else if (e.kind === "rejected" || e.kind === "no-effect") { h.fillStyle = "#ffa940"; h.fillRect(at(e.t), 6, 2, 2); }
                    else if (e.kind === "failed") { h.fillStyle = "#ff4d4f"; h.font = font(10, true); h.fillText("✖", at(e.t) - 3, 11); }
                }
            }
            this.stripCache = c;
        }
        g.setTransform(1, 0, 0, 1, 0, 0); g.drawImage(this.stripCache, 0, 0); g.setTransform(k, 0, 0, k, 0, 0);
        g.fillStyle = "#fff"; g.fillRect(X(this.t) - 1, 0, 2, H);
    }

    hover(e) {
        const r = this.canvas.getBoundingClientRect(), px = e.clientX - r.left, py = e.clientY - r.top;
        const h = (this.hoverTargets || []).find(o => (o.x - px) ** 2 + (o.y - py) ** 2 <= o.r * o.r);
        const w = this.toWorld(px, py), cx = Math.round(w.x), cy = Math.round(w.y);
        const inMap = cx >= 0 && cy >= 0 && cx < this.rp.width && cy < this.rp.height;
        const text = h ? h.text : inMap ? "(" + cx + "," + cy + ") " + (this.rp.walk[cy * this.rp.width + cx] === "1" ? "walkable" : "blocked") : "";
        if (!text) { this.tip.style.display = "none"; return; }
        this.tip.textContent = text; this.tip.style.display = "block"; this.tip.style.left = (px + 12) + "px"; this.tip.style.top = (py + 12) + "px";
    }
    click(e) {
        const r = this.canvas.getBoundingClientRect(), px = e.clientX - r.left, py = e.clientY - r.top;
        const h = (this.hoverTargets || []).find(o => (o.x - px) ** 2 + (o.y - py) ** 2 <= o.r * o.r);
        if (h && h.id) { this.selectedMob = h.id; this.selected = null; this.pause(); this.inspect(); }
    }

    // ---- event list ----
    visibleEvents() {
        const cats = new Set([...this.root.querySelectorAll("[data-cat]")].filter(c => c.checked).map(c => c.dataset.cat));
        const kinds = new Set(Object.entries(CATEGORY).filter(([c]) => cats.has(c)).flatMap(([, k]) => k));
        const minor = this.root.querySelector('[data-a="minor"]').checked;
        const q = this.root.querySelector('[data-a="search"]').value.trim().toLowerCase();
        return this.rp.events.filter(e => (kinds.has(e.kind) || !KNOWN.has(e.kind)) && (minor || !MINOR.has(e.kind)) && (!q || e.text.toLowerCase().includes(q)));
    }
    renderList() {
        if (!this.listDirty) return;
        this.listDirty = false;
        this.shown = this.visibleEvents();
        this.count.textContent = this.shown.length + " of " + this.rp.events.length + " events";
        this.list.innerHTML = this.shown.map(e => '<div data-i="' + e.i + '" class="d-flex gap-2 px-1" style="cursor:pointer;border-bottom:1px solid #ffffff0d"><span class="text-secondary" style="min-width:5.5rem">'
            + fmtT(e.t) + '</span><span class="badge ' + (BADGE[e.kind] || "text-bg-dark border") + '" style="min-width:7.5rem">' + esc(e.kind) + "</span><span>" + esc(e.text) + "</span></div>").join("");
        this.curRow = null;
    }
    highlight(scroll) {
        if (!this.shown) return;
        const i = lastLE(this.shown, this.t, e => e.t);
        const row = i >= 0 ? this.list.querySelector('[data-i="' + this.shown[i].i + '"]') : null;
        if (row === this.curRow && !scroll) return;
        if (this.curRow) this.curRow.style.background = "";
        this.curRow = row;
        if (row) {
            row.style.background = "rgba(59,157,255,0.25)";
            const top = row.offsetTop - this.list.offsetTop;
            if ((this.playing || scroll) && (top < this.list.scrollTop || top > this.list.scrollTop + this.list.clientHeight - 20)) this.list.scrollTop = top - 100;
        }
    }

    // ---- inspector: why ----
    link(e, label) { return e ? '<a href="#" data-ev="' + e.i + '">' + esc(label || (fmtT(e.t) + " " + e.text)) + "</a>" : "—"; }
    mobLink(id) { const m = this.mobs.get(id); return m ? '<a href="#" data-mob="' + id + '">' + esc(m.name) + " #" + id + "</a>" : "#" + id; }

    inspect() {
        if (this.tab === "inventory") { this.inspector.innerHTML = this.inventory(); return; }
        this.inspector.innerHTML = this.selectedMob ? this.explainMob(this.selectedMob) : this.selected ? this.explain(this.selected) : this.now();
    }

    /** The bag at t: the build's inventory (startInventory), plus every "loot" event so far, minus every "use", as the Character page's inventory card shows it. */
    inventory() {
        const t = this.t, got = this.rp.events.filter(e => (e.kind === "loot" || e.kind === "use") && e.t <= t), bag = new Map();
        for (const s of this.rp.startInventory || []) bag.set(s.itemId, { name: s.name, code: s.code, count: s.count });
        for (const e of got) { const d = e.detail || {}, it = bag.get(d.itemId) || { name: d.name, code: d.code, count: 0 }; it.count += e.kind === "use" ? -d.count : d.count; if (it.count > 0) bag.set(d.itemId, it); else bag.delete(d.itemId); }
        let h = '<div class="fw-semibold mb-1">Inventory at ' + fmtT(t) + ' <span class="text-secondary">(' + bag.size + ")</span></div>";
        if (!bag.size) return h + '<div class="text-secondary">empty' + (this.rp.events.some(e => e.kind === "loot") ? "" : " (nothing dropped this run, or AutoLoot is off)") + "</div>";
        h += '<div class="mb-2" style="display:grid;grid-template-columns:repeat(2,minmax(0,1fr));gap:.25rem">';
        for (const it of [...bag.values()].sort((a, b) => a.name.localeCompare(b.name)))
            h += '<div class="d-flex align-items-center gap-1 border rounded px-1 py-1" title="' + esc(it.name) + '"><img src="' + itemIcon(it.code)
                + '" alt="" width="24" height="24" style="object-fit:contain;flex:none" onerror="this.style.visibility=\'hidden\'"><span class="text-truncate flex-grow-1">'
                + esc(it.name) + '</span><span class="fw-semibold">' + it.count + "</span></div>";
        h += '</div><div class="fw-semibold">Pickups and uses</div>';
        return h + [...got].reverse().map(e => '<div>' + this.link(e, fmtT(e.t)) + " " + esc(e.detail?.name) + (e.kind === "use" ? " -" : " +") + e.detail?.count + "</div>").join("");
    }

    now() {
        const t = this.t, me = this.posAt(t), v = this.vitalsAt(t), act = this.activityAt(t), target = this.targetAt(t);
        let alive = 0, inView = 0;
        for (const id of this.mobs.keys()) { const m = this.mobAt(id, t); if (m && m.alive) { alive++; if (Math.max(Math.abs(m.x - me.cx), Math.abs(m.y - me.cy)) <= 21) inView++; } }
        return '<div class="d-flex justify-content-between"><span class="fw-semibold">World at ' + fmtT(t) + '</span><span class="text-secondary">click an event or a monster for why</span></div>'
            + '<table class="table table-sm small mb-2"><tbody>'
            + "<tr><td>Bot</td><td>(" + me.cx + "," + me.cy + "), " + ACTIVITY[act].label + "</td></tr>"
            + "<tr><td>HP / SP</td><td>" + v.hp + "/" + this.rp.maxHp + " · " + v.sp + "/" + this.rp.maxSp + "</td></tr>"
            + "<tr><td>Target</td><td>" + (target ? this.mobLink(target) : "none") + "</td></tr>"
            + "<tr><td>Monsters</td><td>" + alive + " alive on the map, " + inView + " within view (21 cells)</td></tr>"
            + "</tbody></table>"
            + '<div class="fw-semibold">Latest decisions</div><ul class="ps-3 mb-0">'
            + "<li>Target: " + this.link(this.lastOf(["pick", "nopick"], t)) + "</li><li>Skill: " + this.link(this.lastOf(["cast-decision", "attack-decision"], t))
            + "</li><li>Rest: " + this.link(this.lastOf(["sit", "stand"], t)) + "</li><li>Wander: " + this.link(this.lastOf(["wander", "idle"], t)) + "</li></ul>";
    }

    header(e) {
        const v = e.kind === "death" ? { hp: 0, sp: this.vitalsAt(e.t - 1e-6).sp } : this.vitalsAt(e.t), me = this.posAt(e.t);
        return '<div class="d-flex justify-content-between align-items-start mb-1"><div><span class="badge ' + (BADGE[e.kind] || "text-bg-dark border") + '">' + esc(e.kind) + '</span> <span class="text-secondary">'
            + fmtT(e.t) + " · bot (" + me.cx + "," + me.cy + ") · HP " + v.hp + " SP " + v.sp + '</span></div><a href="#" data-now="1">world now</a></div><div class="fw-semibold mb-2">' + esc(e.text) + "</div>";
    }

    explain(e) {
        const d = e.detail || {}, ev = this.rp.events;
        let h = this.header(e);
        const why = s => '<div class="mb-2"><span class="text-secondary">Why: </span>' + s + "</div>";
        switch (e.kind) {
            case "pick": case "nopick": {
                h += why(e.kind === "pick"
                    ? "the live target rules (HuntPolicy.PickTarget) ranked the winnable candidates in view by EXP per second = exp / (forecast kill time + straight distance × 0.15 s + rest time for the forecast damage), best first, and took the best one A* reaches without a long detour."
                    : "no monster in view passed the live target rules" + (d.restToWin ? "; one is winnable at full HP, so the bot sits to rest first (rest to win)." : ", so the bot keeps wandering."));
                h += '<div class="text-secondary mb-1">Win rule at this HP (' + d.hp + "/" + d.maxHp + "): wins some forecast duels, needs at most MaxRoundsToKill " + d.maxRoundsToKill + " attacks, expected damage at most WinMargin " + d.winMargin + " × HP, loses at most 5%.</div>";
                h += '<table class="table table-sm small mb-1"><thead><tr><th>#</th><th>Monster</th><th class="text-end">dist</th><th class="text-end">exp</th><th class="text-end">win</th><th class="text-end">kill s</th><th class="text-end">dmg</th><th class="text-end">rest s</th><th class="text-end">exp/s</th></tr></thead><tbody>';
                for (const c of d.candidates) {
                    const col = c.verdict.startsWith("chosen") ? "#52c41a" : c.verdict.startsWith("too strong") ? "#ff7875" : c.verdict.startsWith("unreachable") ? "#ffa940" : "";
                    h += "<tr><td>" + (c.rank || "") + "</td><td>" + this.mobLink(c.mob) + '</td><td class="text-end">' + c.distance + '</td><td class="text-end">' + c.exp + '</td><td class="text-end">'
                        + (c.forecastKnown ? pct(c.winRate) : "?") + '</td><td class="text-end">' + n1(c.killSeconds) + '</td><td class="text-end">' + (c.damageTaken || 0).toFixed(0) + '</td><td class="text-end">'
                        + n1(c.restSeconds) + '</td><td class="text-end">' + (c.expPerSecond != null ? c.expPerSecond.toFixed(2) : "—") + "</td></tr>"
                        + '<tr><td></td><td colspan="8" style="color:' + (col || "var(--bs-secondary-color)") + ';border-top:0;padding-top:0">' + esc(c.verdict) + "</td></tr>";
                }
                h += "</tbody></table>";
                const me = this.posAt(e.t); let out = 0;
                for (const id of this.mobs.keys()) { const m = this.mobAt(id, e.t); if (m && m.alive && Math.max(Math.abs(m.x - me.cx), Math.abs(m.y - me.cy)) > 21) out++; }
                h += '<div class="text-secondary">Only monsters within view (21 cells) are candidates; ' + out + " more were alive out of view. On the map: green ring chosen, red too strong, orange unreachable, grey ranked lower.</div>";
                const next = ev.slice(e.i + 1).find(x => ["route", "sit", "wander", "engage"].includes(x.kind));
                if (next) h += '<div class="mt-1">Then: ' + this.link(next) + "</div>";
                break;
            }
            case "wander": case "idle":
                h += why(esc(d.why || "no reachable wander destination") + ". " + esc(d.rule || "WanderAsync: 6 random draws, then a 0.8 s retry."));
                h += '<div class="fw-semibold">Random draws (seeded)</div><ol class="ps-3 mb-0">' + (d.attempts || []).map(a => "<li>" + esc(a) + "</li>").join("") + "</ol>";
                break;
            case "cast-decision": {
                h += why("rule " + d.ruleIndex + " of the skill script fired: <code>" + esc(d.rule) + "</code>. The live rule engine (BotBehavior.ChooseSkillRule) takes the first rule that is off its cooldown, known, whose conditions hold and that is worth the SP.");
                if (d.skippedBefore && d.skippedBefore.length) h += '<div class="mb-2">Earlier rules skipped: <ul class="ps-3 mb-0">' + d.skippedBefore.map(s => "<li>" + esc(s) + "</li>").join("") + "</ul></div>";
                h += "<div>Own HP " + d.ownHp + " SP " + d.ownSp + " · target HP " + d.targetHp + "/" + d.targetMaxHp + "</div>";
                const skill = e.text.split(" ")[2];
                const nextCast = ev.findIndex((x, j) => j > e.i && x.kind === "cast-decision");
                const out = ev.slice(e.i + 1, nextCast < 0 ? ev.length : nextCast).filter(x => x.skill === skill && ["executed", "rejected", "queued", "no-effect", "castbar", "failed"].includes(x.kind)).slice(0, 3);
                h += '<div class="mt-2 fw-semibold">What the server did</div>' + (out.length ? '<ul class="ps-3 mb-0">' + out.map(x => "<li>" + this.link(x) + "</li>").join("") + "</ul>" : '<div class="text-secondary">No server outcome recorded right after this cast (no SP spent, no status, no damage booked to it).</div>');
                break;
            }
            case "attack-decision":
                h += why(esc(d.reason) + (d.skipped && d.skipped.length ? ":" : "."));
                if (d.skipped && d.skipped.length) h += '<ul class="ps-3">' + d.skipped.map(s => "<li>" + esc(s) + "</li>").join("") + "</ul>";
                break;
            case "rejected": {
                const dec = ev.slice(0, e.i).reverse().find(x => x.kind === "cast-decision" && x.t > e.t - 10);
                const reason = (e.text.match(/\(([^)]*)\)\s*$/) || [])[1] || "?";
                h += why("the game server's skill validation refused it and answered <code>" + esc(reason) + "</code>. The rule itself fired, so the live bot would send it again at its next chance.");
                h += "<div>Decision that sent it: " + this.link(dec) + "</div>";
                if (/zeny|item|ammo|arrow|catalyst|gem|weight/i.test(reason)) h += '<div class="alert alert-secondary small mt-2 mb-0">This world has no zeny, inventory or ammunition (see "Items" under what the simulation models), so a cost the live bot could pay is refused here.</div>';
                break;
            }
            case "executed": case "castbar": case "queued": {
                const dec = ev.slice(0, e.i + 1).reverse().find(x => x.kind === "cast-decision" && x.text.split(" ")[2] === e.skill);
                h += "<div class=\"mb-2\">Sent by: " + this.link(dec) + "</div>";
                if (e.kind === "executed") {
                    const nextCast = ev.findIndex((x, j) => j > e.i && x.kind === "cast-decision");
                    const after = ev.slice(e.i + 1, nextCast < 0 ? ev.length : nextCast);
                    const hits = after.filter(x => x.kind === "hit" && x.skill === e.skill), kb = after.filter(x => x.kind === "knockback" && x.skill === e.skill);
                    const dmg = hits.reduce((s, x) => s + x.amount, 0), mobs = [...new Set(hits.map(x => x.mob))];
                    h += '<div class="fw-semibold">What the server did with it</div><ul class="ps-3 mb-0"><li>' + esc(e.text.replace(/^\w+: executed,? ?/, "") || "executed") + "</li>"
                        + (hits.length ? "<li>" + hits.length + " hit(s) for " + dmg + " damage on " + mobs.map(id => this.mobLink(id)).join(", ") + "; kills: " + hits.filter(x => x.hpLeft === 0).length + "</li>" : "<li>no damage booked to it</li>")
                        + (kb.length ? "<li>" + kb.length + " knockback(s): " + kb.map(x => this.link(x, this.mobs.get(x.mob)?.name + " to (" + x.x + "," + x.y + ")")).join(", ") + "</li>" : "") + "</ul>";
                } else if (e.kind === "queued") h += why("the after-cast delay of the previous skill was still running; the server keeps the cast and runs it when the delay ends (Player.Update).");
                break;
            }
            case "no-effect": {
                const dec = ev.slice(0, e.i).reverse().find(x => x.kind === "cast-decision" && x.t > e.t - 10);
                const rt = dec && dec.detail ? dec.detail.ruleTarget : null, st = d.serverTarget;
                const enemyAsSelf = rt === "Enemy" && st === "Self", mismatch = rt && st && rt !== st && st !== "Any" && !(rt === "SelfCast" && st === "Self");
                h += why(mismatch
                    ? "the rule sends it as " + (/^[AEIOU]/.test(rt) ? "an" : "a") + " <b>" + esc(rt) + "</b>-targeted cast, but the server's handler declares the skill <b>" + esc(st) + "</b>-targeted. The server's skill packet handler (PacketSkill) drops a packet of the wrong target type without sending an error, so nothing happens."
                      + (enemyAsSelf ? " Writing the rule with <code>self</code> would send the self-cast packet." : "")
                    : "the packet reached the server's skill handler, which sent no error back and changed nothing the simulator observes (SP, cast bar, statuses, attack delay, damage). The server declares the skill " + esc(st || "?") + "-targeted.");
                h += "<div>Decision that sent it: " + this.link(dec) + "</div>";
                break;
            }
            case "sit": {
                h += why(d.lowOnHpSp ? "HP" + (d.spCounts ? " or SP" : "") + " is under RestBelowPercent " + pct(d.restBelow) + " with RestWhenNoPotions on (HuntPolicy.ShouldSit)." : "rest to win: a monster in view is too strong at the current HP but winnable at full HP (HuntPolicy.ShouldSit).");
                h += '<table class="table table-sm small mb-1"><tbody><tr><td>HP</td><td>' + pct(d.hpFraction) + "</td></tr><tr><td>SP</td><td>" + pct(d.spFraction) + (d.spCounts ? "" : " (not counted: this bot doesn't kill with spells)")
                    + "</td></tr><tr><td>Stands at</td><td>HP and SP at or above " + pct(d.standAt) + ", when hit, or when threatened</td></tr><tr><td>Threatened</td><td>" + (d.threatened ? "yes" : "no: no aggressive monster (or one that hit it) within 3 cells, no hazard in view") + " (HuntPolicy.Threatened)</td></tr></tbody></table>";
                const st = ev.slice(e.i + 1).find(x => x.kind === "stand"); if (st) h += "<div>Stood up: " + this.link(st) + "</div>";
                break;
            }
            case "stand":
                h += why(esc(d.reason) + " (HuntPolicy.ShouldStand).");
                h += "<div>Sat " + n1(d.seconds) + " s; the server's regen gave +" + d.regenHp + " HP, +" + d.regenSp + " SP.</div>";
                break;
            case "death":
                h += why("the player's HP reached 0 under the hits below" + (d.forecastWinRate != null ? "; the target's fight forecast gave a " + pct(d.forecastWinRate) + " win rate" : "") + "."
                    + (d.killer ? " Blamed on " + this.mobLink(d.killer) + " (the last attacker, as live): its class is avoided as a target for a while (live rule)." : ""));
                h += '<div class="fw-semibold">Last hits taken</div><ul class="ps-3">' + this.lastHurtBefore(e.t).map(x => "<li>" + this.link(x) + "</li>").join("") + "</ul>";
                h += '<div class="text-secondary">A death ends the run: the rates are over the time lived.</div>';
                break;
            case "engage":
                h += why(d.why === "cornered" ? "cornered by a hazard with no way out: the live flee fights back (HuntPolicy.ChooseFlee)." : "the bot reached attack range of its target.");
                if (d.targetingPlayer?.length) h += "<div>Already targeting the bot (their own server AI): " + d.targetingPlayer.map(id => this.mobLink(id)).join(", ") + ". Each swings on its own cooldown.</div>";
                h += "<div>Forecast (server-code duels from full HP): win " + pct(d.forecastWinRate) + ", " + n1(d.forecastSeconds) + " s, −" + (d.forecastDamageTaken || 0).toFixed(0) + " HP, " + n1(d.forecastAttacks) + " attacks.</div>"
                    + "<div>" + (d.rules ? d.rules + " skill rule(s); " : "No skill script; ") + (d.noAutoAttack ? "NoAutoAttack: skills only." : "auto-attacks when no rule fires.") + (d.statuses ? " Statuses up: " + esc(d.statuses) : "") + "</div>";
                break;
            case "kill":
                h += "<div>+" + d.exp + " base EXP" + (d.byArea ? ", killed by area damage" : ", " + n1(d.fightSeconds) + " s fight, " + d.actions + " actions (" + d.skillActions + " skills), " + d.damageTaken + " damage taken") + ". Respawns at " + n1(d.respawnAt) + " s.</div>";
                break;
            case "hurt":
                h += why(e.mob ? this.mobLink(e.mob) + (d.target ? ", the bot's target," : ", not the bot's target,") + " attacked it" + (e.skill ? " with " + esc(e.skill) : "")
                    + ": its own AI on the game server (Monster.Update, MonsterAI.csv) chose to. Every monster targeting the bot swings on its own cooldown." : "a ground effect.");
                break;
            case "aggro":
                h += why("the monster's own AI on the game server picked the bot as its target: " + esc((e.text.match(/\(([^()]*)\)$/) || [])[1] || "") + ".");
                h += "<div>AI " + esc(d.ai || "?") + (d.aggressive ? ", aggressive: it looks for players within its sight (ScanDist " + d.scanDist + ") on its own" : ", not aggressive: it only answers attacks (or helps its kind)") + ". Nothing in the simulator decides this; the server's Monster.Update runs on it while it is near the bot.</div>";
                break;
            case "ai": case "monster-cast":
                h += why("the monster's own AI on the game server (Monster.Update" + (e.kind === "monster-cast" ? " and its skill handler" : "") + ").");
                break;
            case "flee": case "flee-move": {
                h += why(e.kind === "flee" ? "an aggressive monster the forecast at the current HP loses to has the bot inside its danger zone: the live AvoidHazardsAsync drops everything and flees." : "the flee's move this tick (HuntPolicy.ChooseFlee, the live AvoidHazardsAsync's move).");
                h += '<table class="table table-sm small mb-1"><tbody>'
                    + "<tr><td>Hazard</td><td>" + (d.hazard ? this.mobLink(d.hazard) : esc(d.name)) + " (" + esc(d.ai || "") + ")</td></tr>"
                    + "<tr><td>Distance</td><td>" + d.distance + " cells; danger radius " + d.radius + " (ScanDist " + d.scanDist + " + 2), +" + d.buffer + " while fleeing</td></tr>"
                    + "<tr><td>Forecast</td><td>" + esc(d.engine) + " says lose at HP " + d.hp + "/" + d.maxHp + (d.veto ? ": " + esc(d.veto) : "") + "</td></tr>"
                    + "<tr><td>Move</td><td>" + esc(d.action) + (d.action === "Hold" ? "" : " to (" + d.x + "," + d.y + ")") + "</td></tr></tbody></table>";
                const end = ev.slice(e.i + 1).find(x => x.kind === "flee-end"); if (end) h += "<div>Flee ended: " + this.link(end) + "</div>";
                break;
            }
            case "threat":
                h += why("the live tick runs AvoidHazardsAsync first: " + (e.text.includes("hit by") ? "a hit stops a walk with no target, so the next tick can pick what's hitting it." : "a hazard covering the bot interrupts any walk."));
                break;
            case "sit-refused":
                if (d.why) h += why("it wants to rest but HuntPolicy.ShouldSit refuses while threatened: " + esc(d.why) + ".");
                break;
            case "failed":
                h += '<div class="alert alert-danger small mb-0">A runtime error: game server code threw here. What it did to the player was undone and the skill withheld for the rest of the run, so this result is incomplete.</div>';
                break;
            case "sighting":
                h += why("a walk stops when a monster enters view (21 cells) so the bot can look for a target.");
                h += '<ul class="ps-3 mb-0">' + (d.entered || []).map(id => "<li>" + this.mobLink(id) + "</li>").join("") + "</ul>";
                break;
            default:
                if (e.detail) h += '<pre class="small mb-0" style="white-space:pre-wrap">' + esc(JSON.stringify(e.detail, null, 1)).slice(0, 3000) + "</pre>";
        }
        if (e.mob && e.kind !== "pick" && e.kind !== "nopick") h += '<div class="mt-2">Monster: ' + this.mobLink(e.mob) + "</div>";
        return h;
    }

    explainMob(id) {
        const m = this.mobs.get(id), s = this.mobAt(id, this.t);
        const evs = this.rp.events.filter(e => e.t <= this.t + 1e-6 && ((e.mob === id && e.kind !== "hit") || ((e.kind === "pick" || e.kind === "nopick") && e.detail.candidates.some(c => c.mob === id)))).slice(-14);
        let h = '<div class="d-flex justify-content-between"><span class="fw-semibold">' + esc(m.name) + " #" + id + '</span><a href="#" data-now="1">world now</a></div>';
        h += '<table class="table table-sm small mb-2"><tbody>'
            + "<tr><td>At " + fmtT(this.t) + "</td><td>" + (s ? (s.alive ? "alive at (" + Math.round(s.x) + "," + Math.round(s.y) + ")" + (s.walk ? (s.real ? ", walking (its server AI)" : ", wandering") : "") + ", HP " + s.hp + "/" + m.maxHp : "dead") : "not spawned yet") + "</td></tr>"
            + "<tr><td>Level / EXP</td><td>" + m.level + " / " + m.exp + "</td></tr>"
            + "<tr><td>Forecast</td><td>" + esc(m.forecast) + "</td></tr>"
            + "<tr><td>Spawn</td><td>" + (m.pinned ? "pinned area (" + m.area.join(",") + ")" : "anywhere on the map") + ", respawn " + esc(m.respawn) + "</td></tr></tbody></table>";
        h += '<div class="fw-semibold">Its events so far</div><ul class="ps-3 mb-0">' + evs.map(e => {
            const c = e.detail && e.detail.candidates && e.detail.candidates.find(c => c.mob === id);
            return "<li>" + this.link(e) + (c ? ' <span class="text-secondary">→ ' + esc(c.verdict) + "</span>" : "") + "</li>";
        }).join("") + "</ul>";
        return h;
    }
}
